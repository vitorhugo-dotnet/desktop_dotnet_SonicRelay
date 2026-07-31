using System.Text.Json;
using SonicRelay.Windows.Signaling;

namespace SonicRelay.Windows.WebRtc;

public sealed class WebRtcPublisher : IWebRtcPublisher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ISignalingClient signaling;
    private readonly IPeerConnectionManager peers;
    private string? activeSessionId;
    private string? lastError;
    private bool disposed;

    public WebRtcPublisher(ISignalingClient signaling, IPeerConnectionManager peers)
    {
        this.signaling = signaling ?? throw new ArgumentNullException(nameof(signaling));
        this.peers = peers ?? throw new ArgumentNullException(nameof(peers));
        peers.LocalIceCandidateReady += SendLocalIceCandidateAsync;
        peers.DiagnosticsChanged += PublishDiagnostics;
    }

    public WebRtcPublisherDiagnostics Diagnostics =>
        new(peers.ViewerCount, peers.GetDiagnostics(), lastError);

    public event Action<WebRtcPublisherDiagnostics>? DiagnosticsChanged;
    public event Action<string>? IceRestartRequested;

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
                case SignalingMessageTypes.SessionLeft when message.From is not null:
                    ValidateSession(message);
                    await peers.RemoveViewerAsync(message.From, cancellationToken);
                    break;
                case SignalingMessageTypes.ParticipantReconnected when message.From is not null:
                    var reconnectSessionId = RequireSessionId(message);
                    activeSessionId ??= reconnectSessionId;
                    ValidateSession(message);
                    await ReofferToViewerAsync(reconnectSessionId, message.From, cancellationToken);
                    break;
                case SignalingMessageTypes.SessionEnded:
                    ValidateSession(message);
                    await peers.RemoveAllAsync(cancellationToken);
                    activeSessionId = null;
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
            return;
        }
        activeSessionId ??= sessionId;
        ValidateSession(message);
        await OfferToViewerAsync(sessionId, message.From!, cancellationToken);
    }

    // Retained for viewers that still announce readiness explicitly; idempotent with
    // the `session.joined`-driven offer above because RegisterViewerAsync dedupes.
    private async Task HandleViewerReadyAsync(SignalingMessageEnvelope message, CancellationToken cancellationToken)
    {
        var sessionId = RequireSessionId(message);
        activeSessionId ??= sessionId;
        ValidateSession(message);
        await OfferToViewerAsync(sessionId, RequireViewerId(message), cancellationToken);
    }

    private async Task OfferToViewerAsync(string sessionId, string viewerId, CancellationToken cancellationToken)
    {
        var registration = await peers.RegisterViewerAsync(viewerId, cancellationToken);
        if (!registration.WasCreated) return;
        try
        {
            var offer = await registration.Peer.Connection.CreateOfferAsync(cancellationToken);
            await SendOfferAsync(sessionId, viewerId, offer, cancellationToken);
        }
        catch
        {
            await peers.RemoveViewerAsync(viewerId, CancellationToken.None);
            throw;
        }
    }

    // A `participant.reconnected` announcement means the same participant re-opened its
    // signaling socket within the backend's grace period. Whatever dropped the socket
    // likely took ICE down with it too, so renegotiate the existing peer with an ICE
    // restart instead of tearing it down and losing playback state. If no peer exists yet
    // (e.g. the publisher itself only just adopted the session), fall back to a normal
    // fresh offer.
    private async Task ReofferToViewerAsync(string sessionId, string viewerId, CancellationToken cancellationToken)
    {
        try
        {
            var restartOffer = await peers.RequestIceRestartAsync(viewerId, cancellationToken);
            if (restartOffer is null)
            {
                await OfferToViewerAsync(sessionId, viewerId, cancellationToken);
                return;
            }
            IceRestartRequested?.Invoke(viewerId);
            await SendOfferAsync(sessionId, viewerId, restartOffer, cancellationToken);
        }
        catch
        {
            await peers.RemoveViewerAsync(viewerId, CancellationToken.None);
            throw;
        }
    }

    private Task SendOfferAsync(string sessionId, string viewerId, WebRtcSessionDescription offer, CancellationToken cancellationToken) =>
        signaling.SendAsync(
            new SignalingMessageEnvelope(
                SignalingMessageTypes.WebRtcOffer,
                sessionId,
                viewerId,
                JsonSerializer.SerializeToElement(offer, JsonOptions)),
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
        await peers.DisposeAsync();
    }
}
