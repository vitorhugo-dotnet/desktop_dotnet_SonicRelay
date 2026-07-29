using SonicRelay.Windows.Core.Authentication;

namespace SonicRelay.Windows.ApiClient.WebRtc;

public interface IWebRtcApiClient
{
    Task<IceServersResponse> GetIceServersAsync(CancellationToken cancellationToken = default);
}

public sealed record IceServersResponse(
    IReadOnlyList<IceServerResponse> IceServers,
    string IceTransportPolicy,
    DateTimeOffset ExpiresAt);

public sealed record IceServerResponse(IReadOnlyList<string> Urls, string? Username = null, string? Credential = null);

public sealed class WebRtcApiClient(
    HttpClient httpClient,
    IDeviceAccessTokenProvider accessTokenProvider) : IWebRtcApiClient
{
    private readonly ApiHttpClient _api = new(httpClient, accessTokenProvider);

    public Task<IceServersResponse> GetIceServersAsync(CancellationToken cancellationToken = default) =>
        _api.SendAsync<IceServersResponse>(
            HttpMethod.Get,
            "/api/webrtc/ice-servers",
            null,
            true,
            cancellationToken,
            replaySafe: true);
}
