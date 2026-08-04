namespace SonicRelay.Windows.Core.Authentication;

public interface IDeviceAccessTokenProvider
{
    Task<string> GetAccessTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default);

    bool IsTransientFailure(Exception exception) => false;

    /// <summary>
    /// Forgets the current device identity so the next <see cref="GetAccessTokenAsync"/>
    /// call bootstraps a fresh one, without requiring the process to restart.
    /// </summary>
    Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
