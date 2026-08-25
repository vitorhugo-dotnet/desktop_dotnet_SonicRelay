using System.Runtime.Versioning;
using SonicRelay.Platform.Linux.Audio;
using SonicRelay.Platform.MacOs.Audio;
using SonicRelay.Windows.Audio;
using SonicRelay.Windows.Core.Configuration;
using SonicRelay.Windows.Core.Processes;
using SonicRelay.Windows.Presentation;

namespace SonicRelay.Windows.Desktop;

/// <summary>
/// Platform composition root for the publisher runtime (issues #32, #62):
/// Windows composes WASAPI capture, Linux the PipeWire adapter, and macOS the
/// ScreenCaptureKit tap helper. All three then share the same runtime, WebRTC
/// pipeline, signaling and session flow.
///
/// All three also share the default DPAPI-backed device-credential store, which
/// only actually protects anything on Windows: DPAPI is unavailable elsewhere,
/// so device identity bootstrap on Linux and macOS reports secure storage as
/// unavailable rather than persisting silently insecurely. Pairing still works;
/// the device just re-bootstraps its credential after a restart. Keychain
/// (macOS) and Secret Service (Linux) backed stores remain the follow-up to
/// issue #26. Any platform beyond these three is an explicit unsupported state,
/// not a silent preview.
/// </summary>
public static class DesktopRuntimeFactory
{
    public static PublisherRuntime? Create(Uri backendBaseUrl)
    {
        if (OperatingSystem.IsWindows()) return CreateWindows(backendBaseUrl);
        if (OperatingSystem.IsLinux()) return CreateLinux(backendBaseUrl);
        if (OperatingSystem.IsMacOS()) return CreateMacOs(backendBaseUrl);
        return null;
    }

    [SupportedOSPlatform("windows")]
    private static PublisherRuntime CreateWindows(Uri backendBaseUrl) =>
        PublisherRuntime.Create(
            backendBaseUrl,
            new AudioCaptureService(),
            // Two-way audio uses a separate capture service on the microphone endpoint: the
            // loopback service is pinned to a render endpoint and its device picker means
            // "which output do I publish", which is a different question from "which
            // microphone do I talk into".
            microphoneCapture: AudioCaptureService.Create(new WasapiMicrophoneBackend(), new NullOutputDeviceProbe()),
            playbackBackend: new WasapiRenderBackend());

    private static PublisherRuntime CreateLinux(Uri backendBaseUrl)
    {
        var commandPaths = new PipeWireCommandLocator().Locate();
        var processRunner = new ChildProcessRunner();
        var resolver = new PipeWireSinkResolver(processRunner, commandPaths);
        var probe = new PipeWireOutputDeviceProbe(processRunner, commandPaths);
        // On Windows, IAudioCaptureService.SelectOutputDevice() is the live routing
        // switch: it updates AudioCaptureService's own internal selection, which
        // WasapiLoopbackBackend reads on its next start. AudioCaptureService.Create's
        // internal selection has no seam this composition root can reach, so the
        // Linux backend instead reads the shared, persisted AudioOutputPreferenceStore
        // directly. AudioPageViewModel's device picker updates both the enumerator and
        // this store together, so the two stay in sync in practice today — but a
        // future caller that only calls SelectOutputDevice() (bypassing the store)
        // would silently have no effect on Linux capture routing. Keep this in mind
        // before adding a new output-device selection path on either platform.
        var audioOutputPreference = new AudioOutputPreferenceStore();
        var backend = new PipeWireProcessBackend(processRunner, commandPaths, resolver, () => audioOutputPreference.SelectedDeviceId);
        var audioCapture = AudioCaptureService.Create(backend, probe);

        // pw-play is located optionally (see PipeWireCommandLocator), so an install without the
        // full PipeWire user tools keeps publishing and simply offers no two-way audio, rather
        // than failing to launch over a feature the user may never use.
        var microphone = commandPaths.PwPlay is null
            ? null
            : AudioCaptureService.Create(new PipeWireMicrophoneBackend(processRunner, commandPaths), new NullOutputDeviceProbe());
        var playbackBackend = commandPaths.PwPlay is null
            ? null
            : new PipeWirePlaybackBackend(processRunner, commandPaths);

        return PublisherRuntime.Create(
            backendBaseUrl,
            audioCapture,
            audioOutputPreferenceOverride: audioOutputPreference,
            microphoneCapture: microphone,
            playbackBackend: playbackBackend);
    }

    /// <summary>
    /// Composes macOS capture. Unlike Windows and Linux there is no
    /// output-device selection to thread through: ScreenCaptureKit taps the
    /// system output mix, so <see cref="MacOsOutputDeviceProbe"/> offers no
    /// endpoints and the runtime keeps its default audio-output preference
    /// store (which the picker still writes, harmlessly, for the "System
    /// default" entry).
    ///
    /// Two-way audio is not composed here. The bundled helper is a ScreenCaptureKit
    /// system-audio tap, which is the wrong capture path for a microphone, and macOS has no
    /// playback backend in this repository yet — so the runtime reports two-way audio
    /// unsupported and the controls stay off, rather than offering a conversation this build
    /// could only half hold up. Adding an AVAudioEngine input/output helper is its own change.
    /// </summary>
    private static PublisherRuntime CreateMacOs(Uri backendBaseUrl)
    {
        // Throws an actionable AudioCaptureException when the bundled helper is
        // missing, exactly like PipeWireCommandLocator does for a missing
        // PipeWire install. App.axaml.cs catches it and leaves the shell on the
        // sign-in surface rather than failing to launch.
        var helperPath = new AudioTapLocator().Locate();
        var processRunner = new ChildProcessRunner();
        var backend = new MacOsAudioTapBackend(processRunner, helperPath);
        var audioCapture = AudioCaptureService.Create(backend, new MacOsOutputDeviceProbe());

        return PublisherRuntime.Create(backendBaseUrl, audioCapture);
    }
}
