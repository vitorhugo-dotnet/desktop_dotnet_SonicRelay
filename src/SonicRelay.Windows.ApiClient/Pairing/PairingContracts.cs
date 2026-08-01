namespace SonicRelay.Windows.ApiClient.Pairing;

public interface IPairingApiClient
{
    Task<CreatePairingChallengeResponse> CreatePairingChallengeAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PairingResponse>> ListPairingsAsync(
        Guid deviceId,
        CancellationToken cancellationToken = default);

    Task RevokePairingAsync(Guid pairingId, CancellationToken cancellationToken = default);
}

public sealed record CreatePairingChallengeResponse(
    Guid ChallengeId,
    string Code,
    string QrPayload,
    DateTimeOffset ExpiresAt);

public sealed record PairingResponse(
    Guid PairingId,
    Guid PublisherDeviceId,
    Guid ViewerDeviceId,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt);
