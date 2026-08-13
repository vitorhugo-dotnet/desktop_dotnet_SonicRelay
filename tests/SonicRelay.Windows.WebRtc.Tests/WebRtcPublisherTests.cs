using System.Text.Json;
using SonicRelay.Windows.Signaling;

namespace SonicRelay.Windows.WebRtc.Tests;

public sealed class WebRtcPublisherTests
{
    [Fact]
    public async Task ViewerReadyRegistersPeerAndSendsOffer()
    {
        var context = CreateContext();
        await using var publisher = context.Publisher;

        await ReadyAsync(publisher, "viewer-1");

        var peer = Assert.Single(context.Factory.Peers);
        Assert.Equal("viewer-1", peer.ViewerId);
        Assert.Equal(1, peer.OfferCount);
        var offer = Assert.Single(context.Signaling.Messages);
        Assert.Equal(SignalingMessageTypes.WebRtcOffer, offer.Type);
        Assert.Equal("session-1", offer.SessionId);
        Assert.Equal("viewer-1", offer.To);
        Assert.Equal("offer-viewer-1", offer.Payload!.Value.GetProperty("sdp").GetString());
        Assert.Equal(1, publisher.Diagnostics.ViewerConnectionCount);
    }

    // A viewer whose ICE died re-announces `viewer.ready` to ask for a fresh offer while
    // its signaling socket is perfectly healthy. Returning silently because a peer already
    // exists leaves that viewer waiting forever for an offer that never arrives.
    [Fact]
    public async Task ViewerReadyForAnAlreadyRegisteredViewerRestartsIceAndReoffers()
    {
        var context = CreateContext();
        await using var publisher = context.Publisher;
        await ReadyAsync(publisher, "viewer-1");

        await ReadyAsync(publisher, "viewer-1");

        var peer = Assert.Single(context.Factory.Peers); // recovered in place, not recreated
        Assert.Equal(1, peer.OfferCount); // the original offer only
        Assert.Equal(1, peer.IceRestartCount);
        Assert.Equal(2, context.Signaling.Messages.Count);
        var reoffer = context.Signaling.Messages[1];
        Assert.Equal(SignalingMessageTypes.WebRtcOffer, reoffer.Type);
        Assert.Equal("viewer-1", reoffer.To);
        Assert.Equal("restart-viewer-1-1", reoffer.Payload!.Value.GetProperty("sdp").GetString());
    }

    [Fact]
    public async Task ViewerSessionJoinedRegistersPeerAndSendsOfferOnlyOnce()
    {
        var context = CreateContext();
        await using var publisher = context.Publisher;

        var joined = new SignalingMessageEnvelope(
            SignalingMessageTypes.SessionJoined,
            "session-1",
            Payload: JsonSerializer.SerializeToElement(new { participantId = "viewer-1", role = "viewer" }),
            From: "viewer-1");
        await publisher.HandleAsync(joined);
        await publisher.HandleAsync(joined);

        var peer = Assert.Single(context.Factory.Peers);
        Assert.Equal("viewer-1", peer.ViewerId);
        Assert.Equal(1, peer.OfferCount);
        var offer = Assert.Single(context.Signaling.Messages);
        Assert.Equal(SignalingMessageTypes.WebRtcOffer, offer.Type);
        Assert.Equal("viewer-1", offer.To);
    }

    [Fact]
    public async Task NonViewerSessionJoinedDoesNotOffer()
    {
        var context = CreateContext();
        await using var publisher = context.Publisher;

        // The publisher's own join carries no `from`; another publisher would be role=publisher.
        await publisher.HandleAsync(new(SignalingMessageTypes.SessionJoined, "session-1",
            Payload: JsonSerializer.SerializeToElement(new { participantId = "pub", role = "publisher" })));
        await publisher.HandleAsync(new(SignalingMessageTypes.SessionJoined, "session-1",
            Payload: JsonSerializer.SerializeToElement(new { participantId = "pub2", role = "publisher" }), From: "pub2"));

        Assert.Empty(context.Factory.Peers);
        Assert.Empty(context.Signaling.Messages);
    }

    [Fact]
    public async Task AnswerAndRemoteCandidateRouteToAddressedViewer()
    {
        var context = CreateContext();
        await using var publisher = context.Publisher;
        await ReadyAsync(publisher, "viewer-1");
        await ReadyAsync(publisher, "viewer-2");
        var answer = JsonSerializer.SerializeToElement(new WebRtcSessionDescription("answer", "answer-sdp"));
        var candidate = JsonSerializer.SerializeToElement(new WebRtcIceCandidate("candidate-2", "audio", 0));

        await publisher.HandleAsync(new(SignalingMessageTypes.WebRtcAnswer, "session-1", Payload: answer, From: "viewer-2"));
        await publisher.HandleAsync(new(SignalingMessageTypes.WebRtcIceCandidate, "session-1", Payload: candidate, From: "viewer-2"));

        var first = context.Factory.Peers.Single(peer => peer.ViewerId == "viewer-1");
        var second = context.Factory.Peers.Single(peer => peer.ViewerId == "viewer-2");
        Assert.Null(first.Answer);
        Assert.Empty(first.RemoteCandidates);
        Assert.Equal("answer-sdp", second.Answer?.Sdp);
        Assert.Equal("candidate-2", Assert.Single(second.RemoteCandidates).Candidate);
    }

    [Fact]
    public async Task LocalCandidateIsEmittedThroughSignalingForOwningViewer()
    {
        var context = CreateContext();
        await using var publisher = context.Publisher;
        await ReadyAsync(publisher, "viewer-1");
        context.Signaling.Messages.Clear();

        await context.Factory.Peers[0].EmitCandidateAsync(new("local-candidate", "audio", 0));

        var message = Assert.Single(context.Signaling.Messages);
        Assert.Equal(SignalingMessageTypes.WebRtcIceCandidate, message.Type);
        Assert.Equal("viewer-1", message.To);
        Assert.Equal("local-candidate", message.Payload!.Value.GetProperty("candidate").GetString());
    }

    [Fact]
    public async Task AudioIsFannedOutAndDiagnosticsTrackPeerState()
    {
        var context = CreateContext();
        await using var publisher = context.Publisher;
        await ReadyAsync(publisher, "viewer-1");
        await ReadyAsync(publisher, "viewer-2");
        var frame = new WebRtcAudioFrame([1, 2, 3, 4], 48_000, 2, TimeSpan.Zero);

        await publisher.PushAudioFrameAsync(frame);
        context.Factory.Peers[1].SetDiagnostics(PeerConnectionState.Connected, "relay/udp", TimeSpan.FromMilliseconds(24));

        Assert.All(context.Factory.Peers, peer => Assert.Same(frame, Assert.Single(peer.Frames)));
        Assert.Equal(2, publisher.Diagnostics.ViewerConnectionCount);
        var viewer = publisher.Diagnostics.Viewers.Single(item => item.ViewerId == "viewer-2");
        Assert.Equal(PeerConnectionState.Connected, viewer.State);
        Assert.Equal("relay/udp", viewer.SelectedCandidatePair);
        Assert.Equal(TimeSpan.FromMilliseconds(24), viewer.EstimatedRoundTripTime);
    }

    [Fact]
    public async Task ViewerAndSessionLifecycleDisposeTheCorrectPeers()
    {
        var context = CreateContext();
        await using var publisher = context.Publisher;
        await ReadyAsync(publisher, "viewer-1");
        await ReadyAsync(publisher, "viewer-2");

        await publisher.HandleAsync(new(SignalingMessageTypes.SessionLeft, "session-1", From: "viewer-1"));

        Assert.True(context.Factory.Peers[0].Disposed);
        Assert.False(context.Factory.Peers[1].Disposed);
        Assert.Equal(1, publisher.Diagnostics.ViewerConnectionCount);

        await publisher.HandleAsync(new(SignalingMessageTypes.SessionEnded, "session-1"));

        Assert.True(context.Factory.Peers[1].Disposed);
        Assert.Equal(0, publisher.Diagnostics.ViewerConnectionCount);
    }

    [Fact]
    public async Task NewSessionSupersedesStalePeersAndBecomesActive()
    {
        var context = CreateContext();
        await using var publisher = context.Publisher;

        // A viewer connects on session-1, then that session dies without a clean
        // `session.ended` (e.g. the viewer crashed and the server reaped it).
        await ReadyAsync(publisher, "viewer-1");
        Assert.Equal(1, publisher.Diagnostics.ViewerConnectionCount);

        // The publisher (re)joins a brand-new session; its own join carries no `from`.
        await publisher.HandleAsync(new(SignalingMessageTypes.SessionJoined, "session-2",
            Payload: JsonSerializer.SerializeToElement(new { participantId = "pub", role = "publisher" })));

        // Stale peers from session-1 are torn down and the new session is adopted.
        Assert.True(context.Factory.Peers[0].Disposed);
        Assert.Equal(0, publisher.Diagnostics.ViewerConnectionCount);

        // A viewer on session-2 is accepted (no "does not match" rejection) and offered to.
        context.Signaling.Messages.Clear();
        await publisher.HandleAsync(new(SignalingMessageTypes.ViewerReady, "session-2", From: "viewer-2"));

        var offer = Assert.Single(context.Signaling.Messages);
        Assert.Equal(SignalingMessageTypes.WebRtcOffer, offer.Type);
        Assert.Equal("session-2", offer.SessionId);
        Assert.Equal("viewer-2", offer.To);
    }

    // A viewer whose socket dropped triggers two independent recovery requests that race:
    // the backend announces `participant.reconnected` to the publisher, and the viewer
    // itself re-announces `viewer.ready`. Restarting ICE twice back to back risks glare —
    // the answer to the first offer arriving after the second was already created.
    [Fact]
    public async Task RacingRecoveryRequestsRestartIceOnlyOnce()
    {
        var context = CreateContext();
        await using var publisher = context.Publisher;
        await ReadyAsync(publisher, "viewer-1");
        context.Signaling.Messages.Clear();

        await publisher.HandleAsync(new(SignalingMessageTypes.ParticipantReconnected, "session-1", From: "viewer-1"));
        await ReadyAsync(publisher, "viewer-1");

        Assert.Equal(1, context.Factory.Peers[0].IceRestartCount);
        Assert.Single(context.Signaling.Messages);
    }

    // The debounce collapses a burst, it does not spend the viewer's one recovery: a stream
    // that loses ICE repeatedly over a long session must be recoverable every time.
    [Fact]
    public async Task ARecoveryRequestAfterTheDebounceWindowRestartsIceAgain()
    {
        var time = new MutableTimeProvider();
        var context = CreateContext(time);
        await using var publisher = context.Publisher;
        await ReadyAsync(publisher, "viewer-1");
        await ReadyAsync(publisher, "viewer-1");

        time.Advance(TimeSpan.FromSeconds(5));
        await ReadyAsync(publisher, "viewer-1");

        Assert.Equal(2, context.Factory.Peers[0].IceRestartCount);
    }

    [Fact]
    public async Task ParticipantReconnectedRestartsIceOnTheExistingPeerInsteadOfRecreatingIt()
    {
        var context = CreateContext();
        await using var publisher = context.Publisher;
        await ReadyAsync(publisher, "viewer-1");
        context.Signaling.Messages.Clear();

        await publisher.HandleAsync(new(SignalingMessageTypes.ParticipantReconnected, "session-1", From: "viewer-1"));

        Assert.Single(context.Factory.Peers); // no new peer created
        var peer = Assert.Single(context.Factory.Peers);
        Assert.Equal(1, peer.IceRestartCount);
        Assert.Equal(1, peer.OfferCount); // the original offer only, restart doesn't call CreateOfferAsync
        var offer = Assert.Single(context.Signaling.Messages);
        Assert.Equal(SignalingMessageTypes.WebRtcOffer, offer.Type);
        Assert.Equal("viewer-1", offer.To);
        Assert.Equal("restart-viewer-1-1", offer.Payload!.Value.GetProperty("sdp").GetString());
    }

    [Fact]
    public async Task ParticipantReconnectedRaisesIceRestartRequestedForTheViewer()
    {
        var context = CreateContext();
        await using var publisher = context.Publisher;
        await ReadyAsync(publisher, "viewer-1");
        var requested = new List<string>();
        publisher.IceRestartRequested += requested.Add;

        await publisher.HandleAsync(new(SignalingMessageTypes.ParticipantReconnected, "session-1", From: "viewer-1"));

        Assert.Equal(["viewer-1"], requested);
    }

    [Fact]
    public async Task ParticipantReconnectedForAnUnregisteredViewerDoesNotRaiseIceRestartRequested()
    {
        var context = CreateContext();
        await using var publisher = context.Publisher;
        var requested = new List<string>();
        publisher.IceRestartRequested += requested.Add;

        await publisher.HandleAsync(new(SignalingMessageTypes.ParticipantReconnected, "session-1", From: "viewer-1"));

        Assert.Empty(requested);
    }

    [Fact]
    public async Task ParticipantReconnectedForAnUnregisteredViewerFallsBackToAFreshOffer()
    {
        var context = CreateContext();
        await using var publisher = context.Publisher;

        await publisher.HandleAsync(new(SignalingMessageTypes.ParticipantReconnected, "session-1", From: "viewer-1"));

        var peer = Assert.Single(context.Factory.Peers);
        Assert.Equal(0, peer.IceRestartCount);
        Assert.Equal(1, peer.OfferCount);
        var offer = Assert.Single(context.Signaling.Messages);
        Assert.Equal(SignalingMessageTypes.WebRtcOffer, offer.Type);
        Assert.Equal("viewer-1", offer.To);
    }

    [Fact]
    public async Task ParticipantDisconnectedDoesNotTearDownTheExistingPeer()
    {
        var context = CreateContext();
        await using var publisher = context.Publisher;
        await ReadyAsync(publisher, "viewer-1");
        context.Signaling.Messages.Clear();

        await publisher.HandleAsync(new(SignalingMessageTypes.ParticipantDisconnected, "session-1", From: "viewer-1"));

        Assert.Single(context.Factory.Peers);
        Assert.False(context.Factory.Peers[0].Disposed);
        Assert.Empty(context.Signaling.Messages);
    }

    [Fact]
    public async Task IceRestartFailureRemovesTheViewerLikeAnOfferFailure()
    {
        var context = CreateContext();
        await using var publisher = context.Publisher;
        await ReadyAsync(publisher, "viewer-1");
        context.Factory.Peers[0].CreateIceRestartOfferException = new InvalidOperationException("restart failed");

        await Assert.ThrowsAsync<WebRtcPublisherException>(() => publisher.HandleAsync(
            new(SignalingMessageTypes.ParticipantReconnected, "session-1", From: "viewer-1")));

        Assert.True(context.Factory.Peers[0].Disposed);
        Assert.Equal(0, publisher.Diagnostics.ViewerConnectionCount);
    }

    [Fact]
    public async Task InvalidInboundPayloadUpdatesLastError()
    {
        var context = CreateContext();
        await using var publisher = context.Publisher;
        await ReadyAsync(publisher, "viewer-1");

        await Assert.ThrowsAsync<WebRtcPublisherException>(() => publisher.HandleAsync(
            new(SignalingMessageTypes.WebRtcAnswer, "session-1", Payload: JsonSerializer.SerializeToElement(new { wrong = true }), From: "viewer-1")));

        Assert.NotNull(publisher.Diagnostics.LastError);
    }

    [Fact]
    public async Task OfferFailureRemovesPartiallyRegisteredViewer()
    {
        var context = CreateContext();
        context.Factory.CreateOfferException = new InvalidOperationException("offer failed");
        await using var publisher = context.Publisher;

        await Assert.ThrowsAsync<WebRtcPublisherException>(() => ReadyAsync(publisher, "viewer-1"));

        Assert.True(Assert.Single(context.Factory.Peers).Disposed);
        Assert.Equal(0, publisher.Diagnostics.ViewerConnectionCount);
        Assert.Contains("offer failed", publisher.Diagnostics.LastError, StringComparison.Ordinal);
    }

    private static async Task ReadyAsync(WebRtcPublisher publisher, string viewerId) =>
        await publisher.HandleAsync(new(SignalingMessageTypes.ViewerReady, "session-1", From: viewerId));

    private static TestContext CreateContext(TimeProvider? timeProvider = null)
    {
        var signaling = new RecordingSignalingClient();
        var factory = new FakePeerConnectionFactory();
        var manager = new PeerConnectionManager(factory, new WebRtcPublisherOptions());
        return new(signaling, factory, new WebRtcPublisher(signaling, manager, timeProvider));
    }

    /// <summary>A clock that only moves when a test moves it.</summary>
    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan delta) => now += delta;
    }

    private sealed record TestContext(
        RecordingSignalingClient Signaling,
        FakePeerConnectionFactory Factory,
        WebRtcPublisher Publisher);

    private sealed class RecordingSignalingClient : ISignalingClient
    {
        public List<SignalingMessageEnvelope> Messages { get; } = [];
        public SignalingConnectionState State => SignalingConnectionState.Connected;
        public event Action<SignalingConnectionState>? StateChanged { add { } remove { } }
        public event Action<int>? ReconnectAttempting { add { } remove { } }
        public event Action<SignalingCloseReason>? Closed { add { } remove { } }
        public Task ConnectAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendAsync(SignalingMessageEnvelope message, CancellationToken cancellationToken = default)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakePeerConnectionFactory : IWebRtcPeerConnectionFactory
    {
        public List<FakePeerConnection> Peers { get; } = [];
        public Exception? CreateOfferException { get; set; }
        public Task<IWebRtcPeerConnection> CreateAsync(string viewerId, WebRtcPublisherOptions options, CancellationToken cancellationToken = default)
        {
            var peer = new FakePeerConnection(viewerId) { CreateOfferException = CreateOfferException };
            Peers.Add(peer);
            return Task.FromResult<IWebRtcPeerConnection>(peer);
        }
    }

    private sealed class FakePeerConnection(string viewerId) : IWebRtcPeerConnection
    {
        public string ViewerId { get; } = viewerId;
        public int OfferCount { get; private set; }
        public int IceRestartCount { get; private set; }
        public WebRtcSessionDescription? Answer { get; private set; }
        public List<WebRtcIceCandidate> RemoteCandidates { get; } = [];
        public List<WebRtcAudioFrame> Frames { get; } = [];
        public PeerConnectionDiagnostics Diagnostics { get; private set; } = new(viewerId, PeerConnectionState.New);
        public bool Disposed { get; private set; }
        public Exception? CreateOfferException { get; init; }
        public Exception? CreateIceRestartOfferException { get; set; }
        public event Func<WebRtcIceCandidate, CancellationToken, Task>? LocalIceCandidateReady;
        public event Action? DiagnosticsChanged;

        public Task<WebRtcSessionDescription> CreateOfferAsync(CancellationToken cancellationToken = default)
        {
            if (CreateOfferException is not null) return Task.FromException<WebRtcSessionDescription>(CreateOfferException);
            OfferCount++;
            return Task.FromResult(new WebRtcSessionDescription("offer", $"offer-{ViewerId}"));
        }
        public Task<WebRtcSessionDescription> CreateIceRestartOfferAsync(CancellationToken cancellationToken = default)
        {
            if (CreateIceRestartOfferException is not null) return Task.FromException<WebRtcSessionDescription>(CreateIceRestartOfferException);
            IceRestartCount++;
            return Task.FromResult(new WebRtcSessionDescription("offer", $"restart-{ViewerId}-{IceRestartCount}"));
        }
        public Task ApplyAnswerAsync(WebRtcSessionDescription answer, CancellationToken cancellationToken = default)
        {
            Answer = answer;
            return Task.CompletedTask;
        }
        public Task AddRemoteIceCandidateAsync(WebRtcIceCandidate candidate, CancellationToken cancellationToken = default)
        {
            RemoteCandidates.Add(candidate);
            return Task.CompletedTask;
        }
        public Task SendAudioFrameAsync(WebRtcAudioFrame frame, CancellationToken cancellationToken = default)
        {
            Frames.Add(frame);
            return Task.CompletedTask;
        }
        public async Task EmitCandidateAsync(WebRtcIceCandidate candidate)
        {
            if (LocalIceCandidateReady is not null) await LocalIceCandidateReady(candidate, CancellationToken.None);
        }
        public void SetDiagnostics(PeerConnectionState state, string pair, TimeSpan rtt)
        {
            Diagnostics = new(ViewerId, state, pair, rtt);
            DiagnosticsChanged?.Invoke();
        }
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
