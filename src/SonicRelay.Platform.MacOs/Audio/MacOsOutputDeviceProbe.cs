using SonicRelay.Windows.Audio;

namespace SonicRelay.Platform.MacOs.Audio;

/// <summary>
/// Reports the selectable capture endpoints on macOS — deliberately none.
///
/// Windows enumerates WASAPI render endpoints and Linux enumerates PipeWire
/// sinks because on both platforms capture can be pointed at a chosen endpoint.
/// ScreenCaptureKit has no such control: it taps the system output mix, and
/// whichever device the user sends output to is what gets captured. Listing
/// CoreAudio output devices here would render a picker whose entries could not
/// change what is captured, so the probe returns nothing and
/// <c>AudioPageViewModel</c> shows only its built-in "System default" entry —
/// which is exactly what macOS capture does.
/// </summary>
public sealed class MacOsOutputDeviceProbe : IAudioOutputDeviceProbe
{
    public IReadOnlyList<AudioOutputDevice> GetOutputDevices() => [];
}
