using SonicRelay.Windows.Core.Authentication;

namespace SonicRelay.Windows.ApiClient.Settings;

public interface IRelaySettingsApiClient
{
    Task<RelaySettingsResponse> GetAsync(CancellationToken cancellationToken = default);
    Task<RelaySettingsResponse> UpdateAsync(UpdateRelaySettingsRequest request, CancellationToken cancellationToken = default);
}

public sealed record RelaySettingsResponse(string RelayMode, IReadOnlyList<string> TurnUris, bool HasCustomTurnSecret);

public sealed record UpdateRelaySettingsRequest(string? RelayMode, IReadOnlyList<string>? TurnUris, string? TurnStaticAuthSecret);

public sealed class RelaySettingsApiClient(
    HttpClient httpClient,
    IDeviceAccessTokenProvider accessTokenProvider) : IRelaySettingsApiClient
{
    private readonly ApiHttpClient _api = new(httpClient, accessTokenProvider);

    public Task<RelaySettingsResponse> GetAsync(CancellationToken cancellationToken = default) =>
        _api.SendAsync<RelaySettingsResponse>(HttpMethod.Get, "/api/settings/relay", null, true, cancellationToken, replaySafe: true);

    public Task<RelaySettingsResponse> UpdateAsync(UpdateRelaySettingsRequest request, CancellationToken cancellationToken = default) =>
        _api.SendAsync<RelaySettingsResponse>(HttpMethod.Put, "/api/settings/relay", request, true, cancellationToken);
}
