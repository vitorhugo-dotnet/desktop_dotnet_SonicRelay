using System.Text.Json;

namespace SonicRelay.Windows.Signaling;

/// <summary>
/// One participant's audio state exactly as the backend published it, carried on
/// <c>session.joined</c>, <c>participant.reconnected</c>, <c>participant.capabilities</c> and
/// <c>participant.audio_state_changed</c>.
///
/// <see cref="AudioSendAllowed"/> is the only authorization signal in the protocol and no
/// client can raise it. The API never parses SDP, so a peer *can* attach a track it was not
/// authorized to send; refusing that audio is the receiving client's job (backend ADR 0007).
/// Never treat a peer's own claim as authorization — only these server-sent frames.
/// </summary>
public sealed record ParticipantAudioState(
    string ParticipantId,
    string Role,
    string SessionMode,
    bool AudioSendAllowed,
    bool CanSendAudio,
    bool CanReceiveAudio,
    bool AudioMuted)
{
    public const string BroadcastMode = "broadcast";
    public const string DuplexMode = "duplex";

    public bool IsPublisher => string.Equals(Role, "publisher", StringComparison.OrdinalIgnoreCase);

    public bool IsDuplexSession => string.Equals(SessionMode, DuplexMode, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether audio arriving from this participant may be played: it must be authorized to
    /// publish <em>and</em> currently say that it intends to.
    /// </summary>
    public bool IsAudioTrusted => AudioSendAllowed && CanSendAudio;

    /// <summary>
    /// Reads a signaling payload, or returns null when it carries no participant — which is
    /// what a payload from a backend that predates duplex, or an unrelated message, looks like.
    /// </summary>
    public static ParticipantAudioState? TryParse(JsonElement? payload)
    {
        if (payload is not { } element || element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty("participantId", out var id)
            || id.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(id.GetString()))
        {
            return null;
        }

        return new ParticipantAudioState(
            id.GetString()!,
            ReadString(element, "role") ?? "viewer",
            ReadString(element, "sessionMode") ?? BroadcastMode,
            ReadBool(element, "audioSendAllowed") ?? false,
            ReadBool(element, "canSendAudio") ?? false,
            // Absent means a backend that predates duplex, whose participants all receive;
            // only an explicit false turns it off.
            ReadBool(element, "canReceiveAudio") ?? true,
            ReadBool(element, "audioMuted") ?? false);
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool? ReadBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;
}
