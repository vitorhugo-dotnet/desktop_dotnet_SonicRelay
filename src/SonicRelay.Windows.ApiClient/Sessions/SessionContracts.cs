using System.Text.Json.Serialization;

namespace SonicRelay.Windows.ApiClient.Sessions;

/// <summary>
/// The audio direction a session is created with. Chosen once at creation and immutable
/// afterwards (backend ADR 0007); anything the backend does not recognize is rejected with
/// <c>400 invalid_session_mode</c>, and omitting it means <see cref="Broadcast"/>.
/// </summary>
public static class SessionModes
{
    /// <summary>The publisher transmits and every other participant only receives.</summary>
    public const string Broadcast = "broadcast";

    /// <summary>Every authorized participant may send and receive audio on the same peer connection.</summary>
    public const string Duplex = "duplex";

    /// <summary>The publisher shares a screen and its system audio; others only receive.</summary>
    public const string ScreenShare = "screen_share";

    public static bool IsDuplex(string? mode) =>
        string.Equals(mode, Duplex, StringComparison.OrdinalIgnoreCase);
}

public interface ISessionApiClient
{
    Task<StreamSessionResponse> CreateSessionAsync(
        CreateSessionRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ActiveSessionResponse>> GetActiveSessionsAsync(CancellationToken cancellationToken = default);

    Task<StreamSessionResponse> EndSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the session mode plus every participant's presence and audio capabilities.
    /// This is the authoritative view of who may publish audio — the WebSocket broadcasts
    /// carry the same state, and neither can be raised by a client.
    /// </summary>
    Task<SessionParticipantsResponse> GetParticipantsAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Grants or revokes one participant's permission to publish audio. Publisher-only, live
    /// sessions only, and duplex sessions only (<c>409 session_not_duplex</c> otherwise).
    /// The backend broadcasts the result to the whole session, the affected participant
    /// included, so nothing else has to deliver the news.
    /// </summary>
    Task<SessionParticipant> SetAudioPermissionAsync(
        Guid sessionId,
        Guid participantId,
        bool canSendAudio,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Body of <c>POST /api/sessions/</c>. <paramref name="Mode"/> is null for the default
/// one-way session, keeping the request byte-identical to what pre-duplex builds sent.
/// </summary>
public sealed record CreateSessionRequest(
    int? MaxViewers = null,
    // Omitted entirely rather than sent as null: the backend reads an absent mode as
    // `broadcast`, and keeping the one-way request byte-identical to what pre-duplex builds
    // sent is what guarantees this change cannot alter an existing flow.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Mode = null);

public sealed record StreamSessionResponse(
    Guid Id,
    Guid SourceDeviceId,
    string Status,
    int MaxViewers,
    DateTimeOffset CodeExpiresAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    DateTimeOffset CreatedAt,
    string? Code,
    string? Mode = null);

public sealed record ActiveSessionResponse(
    Guid Id,
    Guid SourceDeviceId,
    string Status,
    int MaxViewers,
    DateTimeOffset CodeExpiresAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    DateTimeOffset CreatedAt,
    int ViewerCount,
    string? Mode = null);

/// <summary>Response of <c>GET /api/sessions/{id}/participants</c>.</summary>
public sealed record SessionParticipantsResponse(
    Guid SessionId,
    string Mode,
    IReadOnlyList<SessionParticipant> Participants);

/// <summary>
/// One participant's presence and audio state.
///
/// <paramref name="AudioSendAllowed"/> is the backend's authorization and can never be
/// raised by a client; <paramref name="CanSendAudio"/> is the participant's own declared
/// intent, which the backend clamps to zero while it is not authorized. Device ids are
/// deliberately absent from this projection — signaling addresses participants.
/// </summary>
public sealed record SessionParticipant(
    Guid ParticipantId,
    string Role,
    string Status,
    bool AudioSendAllowed,
    bool CanSendAudio,
    bool CanReceiveAudio,
    bool AudioMuted,
    DateTimeOffset JoinedAt,
    DateTimeOffset? LeftAt,
    bool IsSelf);

/// <summary>Body of the per-participant audio-permission endpoint.</summary>
public sealed record SetAudioPermissionRequest(bool CanSendAudio);
