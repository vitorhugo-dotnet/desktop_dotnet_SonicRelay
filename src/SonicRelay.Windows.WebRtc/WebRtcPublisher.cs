using System.Text.Json;
using SonicRelay.Windows.Signaling;

namespace SonicRelay.Windows.WebRtc;

public sealed class WebRtcPublisher : IWebRtcPublisher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// How long after an ICE restart a further recovery request for the same viewer is
    /// treated as a duplicate of it. A dropped viewer socket produces two independent
    /// requests at once — the backend's `participant.reconnected` and the viewer's own
    /// `viewer.ready` — and honouring both would create a second offer while the answer to
    /// the first is still in flight. Genuine recovery requests are seconds to minutes apart,
    /// so this window never suppresses one.
    /// </summary>
    private static readonly TimeSpan IceRestartDebounce = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How many consecutive ICE restarts a viewer's existing peer connection gets before its
    /// ICE agent is presumed stuck rather than merely slow, and the peer is torn down and
    /// rebuilt from scratch instead of restarted again in place. Sized around a real incident
    /// where a viewer's ICE kept failing and being restarted on the same peer connection every
    /// ~15 seconds for over a minute without ever converging, and only recovered once the
    /// viewer app was manually disconnected and reconnected — which rebuilt the peer instead
    /// of restarting it. A restart that actually works resets the count (see
    /// <see cref="ResetRecoveredViewers"/>), so a viewer with a healthy-but-flaky connection
    /// never gets rebuilt just for reconnecting often over a long session.
    /// </summary>
    private const int MaxConsecutiveIceRestarts = 3;

    private readonly ISignalingClient signaling;
    private readonly IPeerConnectionManager peers;
    private readonly TimeProvider timeProvider;
    private readonly Dictionary<string, DateTimeOffset> lastIceRestartAt = [];
    private readonly Dictionary<string, int> consecutiveIceRestarts = [];

    /// <summary>
    /// The last state the <em>backend</em> published for each participant, keyed by
    /// participant id. This is the only place publish permission is read from: the API never
    /// parses SDP, so a peer can attach an audio track it was never authorized to send, and
    /// dropping that audio here is the only enforcement there is (backend ADR 0007).
    /// </summary>
    private readonly Dictionary<string, ParticipantAudioState> participants = new(StringComparer.Ordinal);

    private string? selfParticipantId;
    private bool duplexSession;
    private bool outgoingMuted;
    private string? activeSessionId;
    private string? lastError;
    private bool disposed;

    public WebRtcPublisher(ISignalingClient signaling, IPeerConnectionManager peers, TimeProvider? timeProvider = null)
    {
        this.signaling = signaling ?? throw new ArgumentNullException(nameof(signaling));
        this.peers = peers ?? throw new ArgumentNullException(nameof(peers));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        peers.LocalIceCandidateReady += SendLocalIceCandidateAsync;
        peers.DiagnosticsChanged += PublishDiagnostics;
        peers.DiagnosticsChanged += ResetRecoveredViewers;
        peers.RemoteAudioFrameReceived += OnRemoteAudioFrameReceived;
    }

    public WebRtcPublisherDiagnostics Diagnostics =>
        new(peers.ViewerCount, peers.GetDiagnostics(), lastError);

    public event Action<WebRtcPublisherDiagnostics>? DiagnosticsChanged;
    public event Action<string>? IceRestartRequested;

    /// <summary>
    /// Raised instead of <see cref="IceRestartRequested"/> when a viewer's peer connection is
    /// torn down and rebuilt from scratch after too many consecutive ICE restarts failed to
    /// ever reach <see cref="PeerConnectionState.Connected"/>.
    /// </summary>
    public event Action<string>? PeerRebuildRequested;
    public event Action<ParticipantAudioState>? ParticipantAudioStateChanged;
    public event Action<string, RemoteAudioFrame>? RemoteAudioFrameReceived;

    public IReadOnlyCollection<ParticipantAudioState> Participants
    {
        get { lock (participants) return participants.Values.ToArray(); }
    }

    /// <summary>Whether this device is currently withholding its outgoing audio.</summary>
    public bool OutgoingAudioMuted => outgoingMuted;

    public async Task HandleAsync(SignalingMessageEnvelope message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ObjectDisposedException.ThrowIf(disposed, this);
        try
        {
            switch (message.Type)
            {
                case SignalingMessageTypes.SessionJoined:
                    await HandleSessionJoinedAsync(message, cancellationToken);
                    break;
                case SignalingMessageTypes.ViewerReady:
                    await HandleViewerReadyAsync(message, cancellationToken);
                    break;
                case SignalingMessageTypes.WebRtcAnswer:
                    ValidateSession(message);
                    await peers.ApplyAnswerAsync(RequireViewerId(message), DeserializePayload<WebRtcSessionDescription>(message), cancellationToken);
                    break;
                case SignalingMessageTypes.WebRtcIceCandidate:
                    ValidateSession(message);
                    await peers.AddRemoteIceCandidateAsync(RequireViewerId(message), DeserializePayload<WebRtcIceCandidate>(message), cancellationToken);
                    break;
                case SignalingMessageTypes.WebRtcRenegotiate:
                    ValidateSession(message);
                    await HandleRenegotiateAsync(RequireSessionId(message), RequireViewerId(message), cancellationToken);
                    break;
                case SignalingMessageTypes.ParticipantCapabilities:
                case SignalingMessageTypes.ParticipantAudioStateChanged:
                    // The backend broadcasts its own authoritative version of these to the
                    // whole session, this device included, so they are absorbed rather than
                    // acted on: they never address a peer and never carry a session to route to.
                    await AbsorbParticipantStateAsync(message, cancellationToken);
                    break;
                case SignalingMessageTypes.SessionLeft when message.From is not null:
                    ValidateSession(message);
                    await peers.RemoveViewerAsync(message.From, cancellationToken);
                    ForgetRecoveryState(message.From);
                    lock (participants) participants.Remove(message.From);
                    break;
                case SignalingMessageTypes.ParticipantReconnected when message.From is not null:
                    var reconnectSessionId = RequireSessionId(message);
                    activeSessionId ??= reconnectSessionId;
                    ValidateSession(message);
                    await AbsorbParticipantStateAsync(message, cancellationToken);
                    await ReofferToViewerAsync(reconnectSessionId, message.From, cancellationToken);
                    break;
                case SignalingMessageTypes.SessionEnded:
                    ValidateSession(message);
                    await peers.RemoveAllAsync(cancellationToken);
                    activeSessionId = null;
                    lock (consecutiveIceRestarts) consecutiveIceRestarts.Clear();
                    lock (participants) participants.Clear();
                    selfParticipantId = null;
                    duplexSession = false;
                    break;
                // ParticipantDisconnected is intentionally a no-op: it just means the
                // viewer's socket dropped within the backend's reconnect grace period.
                // The peer connection is left alone; ParticipantReconnected (above) or the
                // peer's own ICE recovery drives any renegotiation.
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            lastError = exception.Message;
            PublishDiagnostics();
            if (exception is WebRtcPublisherException) throw;
            throw new WebRtcPublisherException("WebRTC signaling processing failed.", exception);
        }
    }

    public async Task PushAudioFrameAsync(WebRtcAudioFrame frame, CancellationToken cancellationToken = default)
    {
        try
        {
            await peers.PushAudioFrameAsync(frame, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            lastError = exception.Message;
            PublishDiagnostics();
            throw;
        }
    }

    // A viewer joining the session is broadcast to the publisher as `session.joined`
    // with the viewer's participant id in `from`. The publisher is the offerer, so it
    // registers the viewer and sends the offer directly (the viewer answers it).
    private async Task HandleSessionJoinedAsync(SignalingMessageEnvelope message, CancellationToken cancellationToken)
    {
        var sessionId = RequireSessionId(message);
        if (!IsViewerJoin(message))
        {
            // The publisher's own join (from == null) establishes — or supersedes — the
            // active session. A new session id means the previous session ended without a
            // clean `session.ended` (e.g. the viewer crashed and the server reaped the
            // session); adopt the new one instead of rejecting all its traffic forever.
            await AdoptSessionAsync(sessionId, cancellationToken);
            // After adopting, never before: this device's own join is what establishes the
            // session, and announcing capabilities needs a session to announce them in.
            await AbsorbParticipantStateAsync(message, cancellationToken);
            return;
        }
        activeSessionId ??= sessionId;
        ValidateSession(message);
        await AbsorbParticipantStateAsync(message, cancellationToken);
        // A repeated `session.joined` for a viewer we already hold is backend noise about a
        // presence we already acted on, not a request for anything, so it stays deduped.
        await OfferToViewerAsync(sessionId, message.From!, recoverKnownViewer: false, cancellationToken);
    }

    // Unlike `session.joined`, `viewer.ready` is the viewer explicitly asking to be offered.
    // A viewer that already has a peer only asks again when its own media path has died, so
    // a repeat announcement is a recovery request rather than a duplicate.
    private async Task HandleViewerReadyAsync(SignalingMessageEnvelope message, CancellationToken cancellationToken)
    {
        var sessionId = RequireSessionId(message);
        activeSessionId ??= sessionId;
        ValidateSession(message);
        await OfferToViewerAsync(sessionId, RequireViewerId(message), recoverKnownViewer: true, cancellationToken);
    }

    private async Task OfferToViewerAsync(string sessionId, string viewerId, bool recoverKnownViewer,
        CancellationToken cancellationToken)
    {
        var registration = await peers.RegisterViewerAsync(viewerId, cancellationToken);
        if (!registration.WasCreated)
        {
            // The viewer's ICE died while signaling stayed up (a network handover or NAT
            // rebinding). Recover the existing peer with an ICE restart. Returning silently
            // here — the previous behaviour for every caller — left such a viewer waiting
            // forever for an offer that would never come, because no other path reaches it:
            // `participant.reconnected` only fires when the *signaling socket* dropped.
            if (recoverKnownViewer) await ReofferToViewerAsync(sessionId, viewerId, cancellationToken);
            return;
        }
        try
        {
            var offer = await registration.Peer.Connection.CreateOfferAsync(cancellationToken);
            await SendOfferAsync(sessionId, viewerId, offer, cancellationToken);
        }
        catch
        {
            await peers.RemoveViewerAsync(viewerId, CancellationToken.None);
            ForgetRecoveryState(viewerId);
            throw;
        }
    }

    // A `participant.reconnected` announcement means the same participant re-opened its
    // signaling socket within the backend's grace period. Whatever dropped the socket
    // likely took ICE down with it too, so renegotiate the existing peer with an ICE
    // restart instead of tearing it down and losing playback state. If no peer exists yet
    // (e.g. the publisher itself only just adopted the session), fall back to a normal
    // fresh offer. Once a viewer has burned through too many consecutive restarts without
    // ever reconnecting, give up on the existing peer and rebuild it instead (see
    // `ShouldRebuildInsteadOfRestart`).
    private async Task ReofferToViewerAsync(string sessionId, string viewerId, CancellationToken cancellationToken)
    {
        if (!TryBeginIceRestart(viewerId)) return;
        if (ShouldRebuildInsteadOfRestart(viewerId))
        {
            await peers.RemoveViewerAsync(viewerId, CancellationToken.None);
            PeerRebuildRequested?.Invoke(viewerId);
            await OfferToViewerAsync(sessionId, viewerId, recoverKnownViewer: false, cancellationToken);
            return;
        }
        try
        {
            var restartOffer = await peers.RequestIceRestartAsync(viewerId, cancellationToken);
            if (restartOffer is null)
            {
                // No peer to restart, so this registers one and offers fresh; recovery is
                // already what we are doing, and false keeps it from recursing back here.
                await OfferToViewerAsync(sessionId, viewerId, recoverKnownViewer: false, cancellationToken);
                return;
            }
            IceRestartRequested?.Invoke(viewerId);
            await SendOfferAsync(sessionId, viewerId, restartOffer, cancellationToken);
        }
        catch
        {
            await peers.RemoveViewerAsync(viewerId, CancellationToken.None);
            ForgetRecoveryState(viewerId);
            throw;
        }
    }

    /// <summary>
    /// Counts this restart attempt against <paramref name="viewerId"/>'s consecutive-failure
    /// streak and reports whether it has now exceeded <see cref="MaxConsecutiveIceRestarts"/>.
    /// The streak resets whenever the viewer's peer actually reaches
    /// <see cref="PeerConnectionState.Connected"/> (see <see cref="ResetRecoveredViewers"/>),
    /// so a viewer that reconnects often but successfully is never rebuilt for it.
    /// </summary>
    private bool ShouldRebuildInsteadOfRestart(string viewerId)
    {
        lock (consecutiveIceRestarts)
        {
            var count = consecutiveIceRestarts.GetValueOrDefault(viewerId) + 1;
            if (count > MaxConsecutiveIceRestarts)
            {
                consecutiveIceRestarts.Remove(viewerId);
                return true;
            }
            consecutiveIceRestarts[viewerId] = count;
            return false;
        }
    }

    // Fires on every peer diagnostics update (connects, disconnects, restarts...) for any
    // viewer; a viewer whose peer just reached Connected proved its ICE agent still works, so
    // its consecutive-restart streak no longer reflects a stuck connection.
    private void ResetRecoveredViewers()
    {
        foreach (var diagnostics in peers.GetDiagnostics())
        {
            if (diagnostics.State != PeerConnectionState.Connected) continue;
            lock (consecutiveIceRestarts) consecutiveIceRestarts.Remove(diagnostics.ViewerId);
        }
    }

    private void ForgetRecoveryState(string viewerId)
    {
        lock (consecutiveIceRestarts) consecutiveIceRestarts.Remove(viewerId);
    }

    /// <summary>
    /// Claims the right to restart ICE for <paramref name="viewerId"/>, or reports that a
    /// restart within <see cref="IceRestartDebounce"/> already covers this request. A removed
    /// viewer leaves no claim behind: its next request registers a brand-new peer and is
    /// offered to directly, never reaching here.
    /// </summary>
    private bool TryBeginIceRestart(string viewerId)
    {
        var now = timeProvider.GetUtcNow();
        lock (lastIceRestartAt)
        {
            if (lastIceRestartAt.TryGetValue(viewerId, out var previous)
                && now - previous < IceRestartDebounce)
            {
                return false;
            }
            lastIceRestartAt[viewerId] = now;
            return true;
        }
    }

    /// <summary>
    /// Answers a peer's <c>webrtc.renegotiate</c> with a fresh offer on its existing peer
    /// connection, so it can add or drop an audio track without the session being recreated.
    /// A peer we hold no connection for gets a normal fresh offer instead — it is asking to
    /// negotiate, and having nothing to renegotiate is not a reason to leave it waiting.
    /// </summary>
    private async Task HandleRenegotiateAsync(string sessionId, string viewerId, CancellationToken cancellationToken)
    {
        var offer = await peers.RequestRenegotiationAsync(viewerId, cancellationToken);
        if (offer is null)
        {
            await OfferToViewerAsync(sessionId, viewerId, recoverKnownViewer: false, cancellationToken);
            return;
        }
        await SendOfferAsync(sessionId, viewerId, offer, cancellationToken, renegotiation: true);
    }

    /// <summary>
    /// Folds a server-published participant state into what this device believes, and
    /// announces this device's own capabilities the first time it learns it is in a duplex
    /// session. Payloads without a participant (a pre-duplex backend) are ignored.
    /// </summary>
    private async Task AbsorbParticipantStateAsync(SignalingMessageEnvelope message, CancellationToken cancellationToken)
    {
        var state = ParticipantAudioState.TryParse(message.Payload);
        if (state is null) return;

        // `from == null` is the backend describing this device to itself.
        var isSelf = message.From is null || string.Equals(state.ParticipantId, selfParticipantId, StringComparison.Ordinal);
        var firstSelfState = false;
        if (isSelf)
        {
            // A different participant id is a different session (or a fresh join), which needs
            // its own announcement — not just the very first one this process ever sees.
            firstSelfState = !string.Equals(selfParticipantId, state.ParticipantId, StringComparison.Ordinal);
            selfParticipantId = state.ParticipantId;
            duplexSession = state.IsDuplexSession;
        }

        lock (participants) participants[state.ParticipantId] = state;
        ParticipantAudioStateChanged?.Invoke(state);

        // Announce once, right after joining, as the protocol expects. Only in duplex: in a
        // one-way session the defaults the backend already assigned are exactly right, and
        // sending anything would be noise on a path that worked before duplex existed.
        if (firstSelfState && duplexSession && activeSessionId is not null)
        {
            await DeclareCapabilitiesAsync(cancellationToken);
        }
    }

    private Task DeclareCapabilitiesAsync(CancellationToken cancellationToken)
    {
        var sessionId = activeSessionId;
        if (sessionId is null) return Task.CompletedTask;
        return SendSelfStateAsync(
            SignalingMessageTypes.ParticipantCapabilities,
            new { canSendAudio = true, canReceiveAudio = true },
            sessionId,
            cancellationToken);
    }

    public Task SetOutgoingAudioMutedAsync(bool muted, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        outgoingMuted = muted;
        peers.SetOutgoingAudioMuted(muted);
        var sessionId = activeSessionId;
        // Mute is announced only in a duplex session. A one-way viewer has no UI for the
        // publisher's mute state and the backend would broadcast a frame nobody reads.
        if (sessionId is null || !duplexSession) return Task.CompletedTask;
        return SendSelfStateAsync(
            SignalingMessageTypes.ParticipantAudioStateChanged,
            new { muted },
            sessionId,
            cancellationToken);
    }

    /// <summary>
    /// Sends a message that describes this device rather than addressing a peer. These carry
    /// no <c>to</c>: the backend validates them, persists the result and broadcasts its own
    /// version to the whole session.
    /// </summary>
    private async Task SendSelfStateAsync(string type, object payload, string sessionId, CancellationToken cancellationToken)
    {
        try
        {
            await signaling.SendAsync(
                new SignalingMessageEnvelope(type, sessionId, To: null, JsonSerializer.SerializeToElement(payload, JsonOptions)),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Failing to announce state must never take the session down: the backend's own
            // broadcast is what the peers act on, and the next change re-announces.
            lastError = exception.Message;
            PublishDiagnostics();
        }
    }

    /// <summary>
    /// Drops audio from a peer the backend has not authorized to publish before it can reach
    /// playback. A peer with no published state at all is a pre-duplex peer, whose contract
    /// was simply "the publisher publishes" — gating that off would silence a working session.
    /// </summary>
    private void OnRemoteAudioFrameReceived(string viewerId, RemoteAudioFrame frame)
    {
        ParticipantAudioState? state;
        lock (participants) participants.TryGetValue(viewerId, out state);
        if (state is not null && !state.AudioSendAllowed) return;
        RemoteAudioFrameReceived?.Invoke(viewerId, frame);
    }

    private Task SendOfferAsync(
        string sessionId,
        string viewerId,
        WebRtcSessionDescription offer,
        CancellationToken cancellationToken,
        bool renegotiation = false) =>
        signaling.SendAsync(
            new SignalingMessageEnvelope(
                SignalingMessageTypes.WebRtcOffer,
                sessionId,
                viewerId,
                // The extra flag marks an offer the peer must apply to its existing connection
                // rather than rebuild from. An ordinary offer stays byte-identical to what
                // pre-duplex builds sent, so a viewer that ignores the flag is unaffected.
                renegotiation
                    ? JsonSerializer.SerializeToElement(
                        new { type = offer.Type, sdp = offer.Sdp, renegotiation = true }, JsonOptions)
                    : JsonSerializer.SerializeToElement(offer, JsonOptions)),
            cancellationToken);

    private static bool IsViewerJoin(SignalingMessageEnvelope message)
    {
        if (string.IsNullOrWhiteSpace(message.From)) return false;
        if (message.Payload is not { } payload || payload.ValueKind != JsonValueKind.Object) return false;
        return payload.TryGetProperty("role", out var role)
            && role.ValueKind == JsonValueKind.String
            && string.Equals(role.GetString(), "viewer", StringComparison.OrdinalIgnoreCase);
    }

    private async Task SendLocalIceCandidateAsync(
        string viewerId,
        WebRtcIceCandidate candidate,
        CancellationToken cancellationToken)
    {
        var sessionId = activeSessionId
            ?? throw new WebRtcPublisherException("Cannot send a local ICE candidate without an active session.");
        await signaling.SendAsync(
            new SignalingMessageEnvelope(
                SignalingMessageTypes.WebRtcIceCandidate,
                sessionId,
                viewerId,
                JsonSerializer.SerializeToElement(candidate, JsonOptions)),
            cancellationToken);
    }

    // Switches to a superseding session: tears down peers left over from the previous
    // session and clears the stale error so the publisher can serve the new session.
    private async Task AdoptSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (string.Equals(activeSessionId, sessionId, StringComparison.Ordinal)) return;
        if (activeSessionId is not null)
        {
            await peers.RemoveAllAsync(cancellationToken);
            lastError = null;
            PublishDiagnostics();
        }
        activeSessionId = sessionId;
        // The previous session's participants — and this device's identity in it — say nothing
        // about the new one. Keeping them would also suppress the capability announcement the
        // new session still needs, since that only fires on this device's first state in it.
        lock (participants) participants.Clear();
        selfParticipantId = null;
        duplexSession = false;
    }

    private void ValidateSession(SignalingMessageEnvelope message)
    {
        var sessionId = RequireSessionId(message);
        if (!string.Equals(activeSessionId, sessionId, StringComparison.Ordinal))
        {
            throw new WebRtcPublisherException($"Message session '{sessionId}' does not match the active WebRTC session.");
        }
    }

    private static string RequireSessionId(SignalingMessageEnvelope message) =>
        !string.IsNullOrWhiteSpace(message.SessionId)
            ? message.SessionId
            : throw new WebRtcPublisherException("A signaling session ID is required.");

    private static string RequireViewerId(SignalingMessageEnvelope message) =>
        !string.IsNullOrWhiteSpace(message.From)
            ? message.From
            : throw new WebRtcPublisherException("A signaling viewer ID is required.");

    private static T DeserializePayload<T>(SignalingMessageEnvelope message)
    {
        if (message.Payload is null)
        {
            throw new WebRtcPublisherException($"A {typeof(T).Name} payload is required.");
        }
        try
        {
            var payload = message.Payload.Value.Deserialize<T>(JsonOptions)
                ?? throw new WebRtcPublisherException($"The {typeof(T).Name} payload is empty.");
            ValidatePayload(payload);
            return payload;
        }
        catch (JsonException exception)
        {
            throw new WebRtcPublisherException($"The {typeof(T).Name} payload is invalid.", exception);
        }
    }

    private static void ValidatePayload<T>(T payload)
    {
        if (payload is WebRtcSessionDescription description
            && (string.IsNullOrWhiteSpace(description.Type) || string.IsNullOrWhiteSpace(description.Sdp)))
        {
            throw new WebRtcPublisherException("A WebRTC session description requires type and SDP values.");
        }
        if (payload is WebRtcIceCandidate candidate && string.IsNullOrWhiteSpace(candidate.Candidate))
        {
            throw new WebRtcPublisherException("A WebRTC ICE candidate value is required.");
        }
    }

    private void PublishDiagnostics() => DiagnosticsChanged?.Invoke(Diagnostics);

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        peers.LocalIceCandidateReady -= SendLocalIceCandidateAsync;
        peers.DiagnosticsChanged -= PublishDiagnostics;
        peers.DiagnosticsChanged -= ResetRecoveredViewers;
        peers.RemoteAudioFrameReceived -= OnRemoteAudioFrameReceived;
        await peers.DisposeAsync();
    }
}
