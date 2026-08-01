namespace SonicRelay.Windows.ApiClient.DeviceIdentity;

public interface IDeviceIdentityApiClient
{
    Task<BootstrapDeviceResponse> BootstrapAsync(
        BootstrapDeviceRequest request,
        CancellationToken cancellationToken = default);

    Task<DeviceTokenResponse> TokenAsync(
        DeviceTokenRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record BootstrapDeviceRequest(string Name, string DeviceType, string Platform);

public sealed record BootstrapDeviceResponse(Guid DeviceId, string CredentialSecret, int CredentialVersion);

public sealed record DeviceTokenRequest(Guid DeviceId, string CredentialSecret);

public sealed record DeviceTokenResponse(
    string AccessToken,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<string> Scopes);
