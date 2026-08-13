using SonicRelay.Windows.Core.Authentication;

namespace SonicRelay.Windows.ApiClient.Settings;

/// <summary>
/// Client for the backend's per-device relay preferences. The backend resolves the effective
/// settings across the device's active pairings (latest write wins), which is what keeps a
/// coturn override made on the phone in sync with the desktop and vice versa. The response
/// never contains the provider's own TURN configuration, and the custom credential is
/// write-only (<see cref="RelaySettingsResponse.HasTurnCredential"/> only reports presence).
/// </summary>
public interface IRelaySettingsApiClient
{
    Task<RelaySettingsResponse> GetRelaySettingsAsync(CancellationToken cancellationToken = default);

    Task<RelaySettingsResponse> UpdateRelaySettingsAsync(
        UpdateRelaySettingsRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record RelaySettingsResponse(
    string RelayMode,
    IReadOnlyList<string> TurnUris,
    string? TurnUsername,
    bool HasTurnCredential,
    DateTimeOffset? UpdatedAt);

public sealed record UpdateRelaySettingsRequest(
    string? RelayMode = null,
    IReadOnlyList<string>? TurnUris = null,
    string? TurnUsername = null,
    string? TurnCredential = null);

public sealed class RelaySettingsApiClient(
    HttpClient httpClient,
    IDeviceAccessTokenProvider accessTokenProvider) : IRelaySettingsApiClient
{
    private readonly ApiHttpClient _api = new(httpClient, accessTokenProvider);

    public Task<RelaySettingsResponse> GetRelaySettingsAsync(CancellationToken cancellationToken = default) =>
        _api.SendAsync<RelaySettingsResponse>(
            HttpMethod.Get,
            "/api/settings/relay",
            null,
            true,
            cancellationToken,
            replaySafe: true);

    public Task<RelaySettingsResponse> UpdateRelaySettingsAsync(
        UpdateRelaySettingsRequest request,
        CancellationToken cancellationToken = default) =>
        _api.SendAsync<RelaySettingsResponse>(
            HttpMethod.Put,
            "/api/settings/relay",
            request,
            true,
            cancellationToken,
            replaySafe: true);
}
