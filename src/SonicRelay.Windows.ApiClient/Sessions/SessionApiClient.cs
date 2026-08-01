using SonicRelay.Windows.Core.Authentication;

namespace SonicRelay.Windows.ApiClient.Sessions;

public sealed class SessionApiClient(
    HttpClient httpClient,
    IDeviceAccessTokenProvider accessTokenProvider) : ISessionApiClient
{
    private readonly ApiHttpClient _api = new(httpClient, accessTokenProvider);

    public Task<StreamSessionResponse> CreateSessionAsync(
        CreateSessionRequest request,
        CancellationToken cancellationToken = default) =>
        _api.SendAsync<StreamSessionResponse>(HttpMethod.Post, "/api/sessions/", request, true, cancellationToken);

    public async Task<IReadOnlyList<ActiveSessionResponse>> GetActiveSessionsAsync(
        CancellationToken cancellationToken = default) =>
        await _api.SendAsync<List<ActiveSessionResponse>>(
            HttpMethod.Get,
            "/api/sessions/active",
            null,
            true,
            cancellationToken,
            replaySafe: true);

    public Task<StreamSessionResponse> EndSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default) =>
        _api.SendAsync<StreamSessionResponse>(
            HttpMethod.Post,
            $"/api/sessions/{sessionId:D}/end",
            null,
            true,
            cancellationToken);
}
