namespace SonicRelay.Windows.ApiClient.Sessions;

public interface ISessionApiClient
{
    Task<StreamSessionResponse> CreateSessionAsync(
        CreateSessionRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ActiveSessionResponse>> GetActiveSessionsAsync(CancellationToken cancellationToken = default);

    Task<StreamSessionResponse> EndSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);
}

public sealed record CreateSessionRequest(int? MaxViewers = null);

public sealed record StreamSessionResponse(
    Guid Id,
    Guid SourceDeviceId,
    string Status,
    int MaxViewers,
    DateTimeOffset CodeExpiresAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    DateTimeOffset CreatedAt,
    string? Code);

public sealed record ActiveSessionResponse(
    Guid Id,
    Guid SourceDeviceId,
    string Status,
    int MaxViewers,
    DateTimeOffset CodeExpiresAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    DateTimeOffset CreatedAt,
    int ViewerCount);
