namespace SonicRelay.Windows.Core.Authentication;

public interface IDeviceAccessTokenProvider
{
    Task<string> GetAccessTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default);
}
