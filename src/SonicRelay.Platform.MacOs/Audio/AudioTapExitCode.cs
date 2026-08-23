using SonicRelay.Windows.Audio;

namespace SonicRelay.Platform.MacOs.Audio;

/// <summary>
/// The exit codes <c>sonicrelay-audio-tap</c> uses to report *why* it stopped
/// (src/SonicRelay.Platform.MacOs/native/SonicRelayAudioTap.swift). They are the
/// only structured channel between the helper and the supervisor, so the two
/// definitions must be changed together.
/// </summary>
public static class AudioTapExitCode
{
    public const int Success = 0;
    public const int Usage = 64;
    public const int Unavailable = 69;
    public const int InternalFailure = 70;
    public const int PermissionDenied = 77;
    public const int UnsupportedOs = 78;

    /// <summary>
    /// Maps a helper exit code onto the shared capture error taxonomy. The
    /// distinction matters beyond the message: <see cref="AudioCaptureService"/>
    /// automatically retries <see cref="AudioCaptureError.DeviceLost"/> and
    /// <see cref="AudioCaptureError.NoDevice"/>, while
    /// <see cref="AudioCaptureError.AccessDenied"/> is terminal — retrying a
    /// revoked Screen Recording grant in a loop would only spin, since the user
    /// has to act in System Settings before capture can work again.
    /// </summary>
    public static AudioCaptureException Map(int exitCode) => exitCode switch
    {
        PermissionDenied => new AudioCaptureException(
            AudioCaptureError.AccessDenied,
            "Screen Recording permission is required to capture system audio on macOS. Grant SonicRelay access in System Settings > Privacy & Security > Screen & System Audio Recording, then start capture again."),
        Unavailable => new AudioCaptureException(
            AudioCaptureError.NoDevice,
            "macOS reported no capturable display, so system audio cannot be tapped."),
        UnsupportedOs => new AudioCaptureException(
            AudioCaptureError.PlatformFailure,
            "System audio capture requires macOS 13 (Ventura) or later."),
        Usage => new AudioCaptureException(
            AudioCaptureError.PlatformFailure,
            "The bundled system audio helper rejected its arguments; the installed SonicRelay build looks inconsistent."),
        // A clean exit is still a fault here: the helper only stops on its own
        // when the capture it was supervising ended, which the service should
        // recover from by restarting it.
        Success => new AudioCaptureException(
            AudioCaptureError.DeviceLost,
            "The macOS system audio helper exited unexpectedly."),
        _ => new AudioCaptureException(
            AudioCaptureError.PlatformFailure,
            $"The macOS system audio helper exited with code {exitCode}."),
    };
}
