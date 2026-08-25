namespace SonicRelay.Windows.Signaling;

public static class SignalingMessageTypes
{
    public const string PublisherReady = "publisher.ready";
    public const string ViewerReady = "viewer.ready";
    public const string WebRtcOffer = "webrtc.offer";
    public const string WebRtcAnswer = "webrtc.answer";
    public const string WebRtcIceCandidate = "webrtc.ice_candidate";

    /// <summary>
    /// A peer asking for a fresh offer on the *existing* peer connection so an audio track
    /// can be added or dropped without recreating the session (duplex sessions). The
    /// publisher is the only offerer in this protocol, so it receives these and answers with
    /// a new <see cref="WebRtcOffer"/>.
    /// </summary>
    public const string WebRtcRenegotiate = "webrtc.renegotiate";
    public const string SessionJoined = "session.joined";
    public const string SessionLeft = "session.left";
    public const string SessionEnded = "session.ended";

    /// <summary>
    /// A participant's socket dropped but the backend's reconnect grace period has not
    /// elapsed yet (transient — do not tear down the peer connection for it).
    /// </summary>
    public const string ParticipantDisconnected = "participant.disconnected";

    /// <summary>
    /// A participant reconnected within the backend's grace period, reusing the same
    /// participant id. Renegotiate (ICE restart) any existing peer connection for it
    /// instead of waiting indefinitely for its ICE to recover on its own.
    /// </summary>
    public const string ParticipantReconnected = "participant.reconnected";

    /// <summary>
    /// A participant's authoritative audio capabilities. A client sends it about itself
    /// (with no <c>to</c>) and the backend re-broadcasts its own version to the whole
    /// session, sender included. The broadcast — never a peer's own claim — is the only
    /// source of truth for whether a participant may publish audio.
    /// </summary>
    public const string ParticipantCapabilities = "participant.capabilities";

    /// <summary>
    /// A participant's mute state, following the same no-<c>to</c>, server-broadcast pattern
    /// as <see cref="ParticipantCapabilities"/>.
    /// </summary>
    public const string ParticipantAudioStateChanged = "participant.audio_state_changed";

    public const string Ping = "ping";
    public const string Pong = "pong";
    public const string Error = "error";

    private static readonly HashSet<string> Supported = new(StringComparer.Ordinal)
    {
        PublisherReady,
        ViewerReady,
        WebRtcOffer,
        WebRtcAnswer,
        WebRtcIceCandidate,
        WebRtcRenegotiate,
        ParticipantCapabilities,
        ParticipantAudioStateChanged,
        SessionJoined,
        SessionLeft,
        SessionEnded,
        ParticipantDisconnected,
        ParticipantReconnected,
        Ping,
        Pong,
        Error
    };

    public static bool IsSupported(string? type) => type is not null && Supported.Contains(type);

    /// <summary>
    /// Whether a message's payload must be redacted from diagnostics. Only SDP and ICE
    /// bodies qualify: they describe network paths and media parameters. Capability and
    /// mute payloads are booleans and participant ids, which the routing logs already
    /// carry, so redacting them would cost the duplex flow its only readable trace.
    /// </summary>
    public static bool HasSensitivePayload(string? type) =>
        type is WebRtcOffer or WebRtcAnswer or WebRtcIceCandidate;
}
