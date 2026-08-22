using SonicRelay.Platform.MacOs.Audio;
using SonicRelay.Windows.Audio;

namespace SonicRelay.Platform.MacOs.Tests;

public sealed class AudioTapExitCodeTests
{
    [Theory]
    [InlineData(AudioTapExitCode.PermissionDenied, AudioCaptureError.AccessDenied)]
    [InlineData(AudioTapExitCode.Unavailable, AudioCaptureError.NoDevice)]
    [InlineData(AudioTapExitCode.UnsupportedOs, AudioCaptureError.PlatformFailure)]
    [InlineData(AudioTapExitCode.Usage, AudioCaptureError.PlatformFailure)]
    [InlineData(AudioTapExitCode.InternalFailure, AudioCaptureError.PlatformFailure)]
    [InlineData(AudioTapExitCode.Success, AudioCaptureError.DeviceLost)]
    [InlineData(139, AudioCaptureError.PlatformFailure)]
    public void MapsHelperExitCodesOntoTheSharedErrorTaxonomy(int exitCode, AudioCaptureError expected)
    {
        Assert.Equal(expected, AudioTapExitCode.Map(exitCode).Error);
    }

    /// <summary>
    /// Only NoDevice and DeviceLost are retried by AudioCaptureService. A
    /// revoked privacy grant must not be one of them, or capture would spin on
    /// something only the user can restore; a helper that merely died must be,
    /// or a transient crash would end the stream permanently.
    /// </summary>
    [Fact]
    public void PermissionDenialIsTerminalWhileAnUnexpectedExitIsRetryable()
    {
        Assert.Equal(AudioCaptureError.AccessDenied, AudioTapExitCode.Map(AudioTapExitCode.PermissionDenied).Error);
        Assert.Equal(AudioCaptureError.DeviceLost, AudioTapExitCode.Map(AudioTapExitCode.Success).Error);
    }
}
