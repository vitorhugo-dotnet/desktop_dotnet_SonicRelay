using SonicRelay.Windows.Core.Authentication;

namespace SonicRelay.Windows.ApiClient.Pairing;

public sealed class PairingApiClient(
    HttpClient httpClient,
    IDeviceAccessTokenProvider accessTokenProvider) : IPairingApiClient
{
    private readonly ApiHttpClient api = new(httpClient, accessTokenProvider);

    public Task<CreatePairingChallengeResponse> CreatePairingChallengeAsync(
        CancellationToken cancellationToken = default) =>
        api.SendAsync<CreatePairingChallengeResponse>(
            HttpMethod.Post,
            "/api/pairings/challenges",
            null,
            authenticated: true,
            cancellationToken,
            replaySafe: true);

    public async Task<IReadOnlyList<PairingResponse>> ListPairingsAsync(
        Guid deviceId,
        CancellationToken cancellationToken = default) =>
        await api.SendAsync<List<PairingResponse>>(
            HttpMethod.Get,
            $"/api/devices/{deviceId:D}/pairings",
            null,
            authenticated: true,
            cancellationToken,
            replaySafe: true);

    public Task RevokePairingAsync(Guid pairingId, CancellationToken cancellationToken = default) =>
        api.SendAsync(
            HttpMethod.Delete,
            $"/api/pairings/{pairingId:D}",
            null,
            authenticated: true,
            cancellationToken);
}
