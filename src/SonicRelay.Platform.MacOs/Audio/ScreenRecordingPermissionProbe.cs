using SonicRelay.Windows.Core.Processes;

namespace SonicRelay.Platform.MacOs.Audio;

public enum ScreenRecordingPermission { Granted, Denied, Unknown }

/// <summary>
/// Asks the bundled helper whether SonicRelay currently holds Screen Recording
/// consent, without starting a capture and without triggering the system
/// prompt. Only <c>check-permission</c> is non-prompting; the <c>capture</c>
/// command deliberately prompts, because that is the point at which the user
/// has asked to stream.
/// </summary>
public sealed class ScreenRecordingPermissionProbe(IChildProcessRunner processRunner, string helperPath)
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);

    public async Task<ScreenRecordingPermission> CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await processRunner
                .RunAsync(helperPath, ["check-permission"], CommandTimeout, cancellationToken)
                .ConfigureAwait(false);
            return result.ExitCode switch
            {
                AudioTapExitCode.Success => ScreenRecordingPermission.Granted,
                AudioTapExitCode.PermissionDenied => ScreenRecordingPermission.Denied,
                _ => ScreenRecordingPermission.Unknown,
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A helper that cannot even be launched tells us nothing about
            // consent; the caller must not report "denied" and send the user to
            // System Settings for what is really a broken install.
            return ScreenRecordingPermission.Unknown;
        }
    }
}
