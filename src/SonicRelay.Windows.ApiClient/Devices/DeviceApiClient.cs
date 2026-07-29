using SonicRelay.Windows.Core.Storage;

namespace SonicRelay.Windows.ApiClient.Devices;

public sealed class DeviceApiClient(HttpClient httpClient, ITokenStore tokenStore) : IDeviceApiClient
{
    private readonly ApiHttpClient _api = new(httpClient, tokenStore);

    public Task<DeviceResponse> RegisterWindowsPublisherAsync(
        RegisterDeviceRequest request,
        CancellationToken cancellationToken = default) =>
        _api.SendAsync<DeviceResponse>(
            HttpMethod.Post,
            "/api/devices/",
            new CreateDeviceRequest(request.Name, "windows_publisher", "windows", request.PublicKey),
            true,
            cancellationToken);

    public async Task<IReadOnlyList<DeviceResponse>> GetDevicesAsync(CancellationToken cancellationToken = default) =>
        await _api.SendAsync<List<DeviceResponse>>(
            HttpMethod.Get,
            "/api/devices/",
            null,
            true,
            cancellationToken,
            replaySafe: true);
}
