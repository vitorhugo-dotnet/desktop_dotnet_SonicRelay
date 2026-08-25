using System.Text.Json;
using SonicRelay.Windows.Signaling;

namespace SonicRelay.Windows.WebRtc.Tests;

/// <summary>
/// The publisher half of two-way audio (dotnet_SonicRelay#22): renegotiation on request,
/// backend-owned publish permission, and mute.
/// </summary>
public sealed class DuplexAudioTests
{
    private const string SessionId = "session-1";
    private const string SelfId = "publisher-1";
    private const string ViewerId = "viewer-1";

    [Fact]
    public async Task RenegotiateFromAViewerReoffersOnTheExistingPeerConnection()
    {
        var context = CreateContext();
        await using var publisher = context.Publisher;
        await ReadyAsync(publisher, ViewerId);

        await publisher.HandleAsync(new(SignalingMessageTypes.WebRtcRenegotiate, SessionId, From: ViewerId));

        var peer = Assert.Single(context.Factory.Peers);
        // The connection is reused, and its ICE credentials are left alone: only the media
        // description changed, and a restart would drop audio for the length of a new
        // connectivity check.
        Assert.Equal(1, peer.RenegotiationOffers);
        Assert.Equal(0, peer.IceRestartCount);
        Assert.Equal(1, peer.OfferCount);

        var offer = context.Signaling.Messages[^1];
        Assert.Equal(SignalingMessageTypes.WebRtcOffer, offer.Type);
        Assert.Equal(ViewerId, offer.To);
        Assert.Equal("renegotiated-1", offer.Payload!.Value.GetProperty("sdp").GetString());
        // The flag is what tells the viewer to answer in place rather than rebuild.
        Assert.True(offer.Payload!.Value.GetProperty("renegotiation").GetBoolean());
    }

    [Fact]
    public async Task AnOrdinaryOfferCarriesNoRenegotiationFlag()
    {
        var context = CreateContext();
        await using var publisher = context.Publisher;

        await ReadyAsync(publisher, ViewerId);

        var offer = Assert.Single(context.Signaling.Messages);
        Assert.False(offer.Payload!.Value.TryGetProperty("renegotiation", out _));
    }

    [Fact]
    public async Task RenegotiateFromAnUnknownViewerFallsBackToAFreshOffer()
    {
        var context = CreateContext();
        await using var publisher = context.Publisher;
        await JoinSelfAsync(publisher, duplex: true);
        context.Signaling.Messages.Clear();

        await publisher.HandleAsync(new(SignalingMessageTypes.WebRtcRenegotiate, SessionId, From: ViewerId));

        // Asking to negotiate is not a reason to be left waiting just because there was
        // nothing to renegotiate yet.
        var peer = Assert.Single(context.Factory.Peers);
        Assert.Equal(1, peer.OfferCount);
        Assert.Equal(SignalingMessageTypes.WebRtcOffer, Assert.Single(context.Signaling.Messages).Type);
    }

    [Fact]
    public async Task JoiningADuplexSessionAnnouncesThisDeviceCapabilities()
    {
        var context = CreateContext();
        await using var publisher = context.Publisher;

        await JoinSelfAsync(publisher, duplex: true);

        var declared = Assert.Single(context.Signaling.Messages);
        Assert.Equal(SignalingMessageTypes.ParticipantCapabilities, declared.Type);
        // No recipient: the backend broadcasts these to the whole session.
        Assert.Null(declared.To);
        Assert.True(declared.Payload!.Value.GetProperty("canSendAudio").GetBoolean());
        Assert.True(declared.Payload!.Value.GetProperty("canReceiveAudio").GetBoolean());
    }

    [Fact]
    public async Task JoiningAOneWaySessionAnnouncesNothing()
    {
        var context = CreateContext();
        await using var publisher = context.Publisher;

        await JoinSelfAsync(publisher, duplex: false);

        Assert.Empty(context.Signaling.Messages);
    }

    [Fact]
    public async Task ASupersedingSessionAnnouncesItsOwnCapabilities()
    {
        // A session that ended without a clean `session.ended` is superseded by the next one's
        // join. That is a new participant in a new session, and it needs its own
        // announcement — the backend stored the previous one against a row that is gone.
        var context = CreateContext();
        await using var publisher = context.Publisher;
        await JoinSelfAsync(publisher, duplex: true);
        context.Signaling.Messages.Clear();

        await publisher.HandleAsync(new(
            SignalingMessageTypes.SessionJoined,
            "session-2",
            Payload: Participant("publisher-2", "publisher", duplex: true, audioSendAllowed: true, canSendAudio: true)));

        var declared = Assert.Single(context.Signaling.Messages);
        Assert.Equal(SignalingMessageTypes.ParticipantCapabilities, declared.Type);
        Assert.Equal("session-2", declared.SessionId);
    }

    [Fact]
    public async Task AReconnectedSocketDoesNotReAnnounce()
    {
        // Same session, same participant id: the backend already holds this state, and
        // re-announcing on every reconnect would be noise on a path that is recovering.
        var context = CreateContext();
        await using var publisher = context.Publisher;
        await JoinSelfAsync(publisher, duplex: true);
        context.Signaling.Messages.Clear();

        await JoinSelfAsync(publisher, duplex: true);

        Assert.Empty(context.Signaling.Messages);
    }

    [Fact]
    public async Task ParticipantCapabilitiesAreRecordedAndSurfaced()
    {
        var context = CreateContext();
        await using var publisher = context.Publisher;
        var observed = new List<ParticipantAudioState>();
        publisher.ParticipantAudioStateChanged += observed.Add;
        await JoinSelfAsync(publisher, duplex: true);

        await publisher.HandleAsync(new(
            SignalingMessageTypes.ParticipantCapabilities,
            SessionId,
            Payload: Participant(ViewerId, "viewer", duplex: true, audioSendAllowed: true, canSendAudio: true),
            From: ViewerId));

        var viewer = Assert.Single(publisher.Participants, participant => participant.ParticipantId == ViewerId);
        Assert.True(viewer.AudioSendAllowed);
        Assert.True(viewer.IsAudioTrusted);
        Assert.Contains(observed, participant => participant.ParticipantId == ViewerId);
    }

    [Fact]
    public async Task AudioFromAnUnauthorizedPeerNeverReachesPlayback()
    {
        var context = CreateContext();
        await using var publisher = context.Publisher;
        var played = new List<RemoteAudioFrame>();
        publisher.RemoteAudioFrameReceived += (_, frame) => played.Add(frame);
        await JoinSelfAsync(publisher, duplex: true);
        await ReadyAsync(publisher, ViewerId);

        await publisher.HandleAsync(new(
            SignalingMessageTypes.ParticipantCapabilities,
            SessionId,
            Payload: Participant(ViewerId, "viewer", duplex: true, audioSendAllowed: false, canSendAudio: true),
            From: ViewerId));
        // The API cannot stop the track from arriving — it never parses SDP — so refusing it
        // here is the only enforcement there is.
        Assert.Single(context.Factory.Peers).RaiseRemoteAudio(new RemoteAudioFrame([1, 2, 3], 48000, 1));

        Assert.Empty(played);
    }

    [Fact]
    public async Task AudioFromAnAuthorizedPeerIsPlayed()
    {
        var context = CreateContext();
        await using var publisher = context.Publisher;
        var played = new List<RemoteAudioFrame>();
        publisher.RemoteAudioFrameReceived += (_, frame) => played.Add(frame);
        await JoinSelfAsync(publisher, duplex: true);
        await ReadyAsync(publisher, ViewerId);

        await publisher.HandleAsync(new(
            SignalingMessageTypes.ParticipantCapabilities,
            SessionId,
            Payload: Participant(ViewerId, "viewer", duplex: true, audioSendAllowed: true, canSendAudio: true),
            From: ViewerId));
        Assert.Single(context.Factory.Peers).RaiseRemoteAudio(new RemoteAudioFrame([1, 2, 3], 48000, 1));

        Assert.Single(played);
    }

    [Fact]
    public async Task AudioFromAPeerWithNoPublishedStateIsPlayed()
    {
        // A viewer on a client that predates duplex sends no capability frames at all. Gating
        // it off would silence a session that worked before this feature existed.
        var context = CreateContext();
        await using var publisher = context.Publisher;
        var played = new List<RemoteAudioFrame>();
        publisher.RemoteAudioFrameReceived += (_, frame) => played.Add(frame);
        await JoinSelfAsync(publisher, duplex: true);
        await ReadyAsync(publisher, ViewerId);

        Assert.Single(context.Factory.Peers).RaiseRemoteAudio(new RemoteAudioFrame([1, 2, 3], 48000, 1));

        Assert.Single(played);
    }

    [Fact]
    public async Task MutingStopsEveryPeerAndAnnouncesTheState()
    {
        var context = CreateContext();
        await using var publisher = context.Publisher;
        await JoinSelfAsync(publisher, duplex: true);
        await ReadyAsync(publisher, ViewerId);
        context.Signaling.Messages.Clear();

        await publisher.SetOutgoingAudioMutedAsync(true);

        Assert.True(publisher.OutgoingAudioMuted);
        Assert.True(Assert.Single(context.Factory.Peers).OutgoingAudioMuted);
        var announced = Assert.Single(context.Signaling.Messages);
        Assert.Equal(SignalingMessageTypes.ParticipantAudioStateChanged, announced.Type);
        Assert.Null(announced.To);
        Assert.True(announced.Payload!.Value.GetProperty("muted").GetBoolean());
    }

    [Fact]
    public async Task MutingInAOneWaySessionAnnouncesNothing()
    {
        var context = CreateContext();
        await using var publisher = context.Publisher;
        await JoinSelfAsync(publisher, duplex: false);
        await ReadyAsync(publisher, ViewerId);
        context.Signaling.Messages.Clear();

        await publisher.SetOutgoingAudioMutedAsync(true);

        // The peer still stops transmitting; only the broadcast — which no one-way viewer
        // reads — is skipped.
        Assert.True(Assert.Single(context.Factory.Peers).OutgoingAudioMuted);
        Assert.Empty(context.Signaling.Messages);
    }

    [Fact]
    public async Task AViewerRegisteredWhileMutedDoesNotStartHearingUs()
    {
        var context = CreateContext();
        await using var publisher = context.Publisher;
        await JoinSelfAsync(publisher, duplex: true);
        await publisher.SetOutgoingAudioMutedAsync(true);

        await ReadyAsync(publisher, ViewerId);

        Assert.True(Assert.Single(context.Factory.Peers).OutgoingAudioMuted);
    }

    private static JsonElement Participant(
        string participantId,
        string role,
        bool duplex,
        bool audioSendAllowed,
        bool canSendAudio) =>
        JsonSerializer.SerializeToElement(new
        {
            participantId,
            role,
            sessionMode = duplex ? "duplex" : "broadcast",
            audioSendAllowed,
            canSendAudio,
            canReceiveAudio = true,
            audioMuted = false,
        });

    private static Task JoinSelfAsync(WebRtcPublisher publisher, bool duplex) =>
        publisher.HandleAsync(new(
            SignalingMessageTypes.SessionJoined,
            SessionId,
            Payload: Participant(SelfId, "publisher", duplex, audioSendAllowed: true, canSendAudio: true)));

    private static Task ReadyAsync(WebRtcPublisher publisher, string viewerId) =>
        publisher.HandleAsync(new(SignalingMessageTypes.ViewerReady, SessionId, From: viewerId));

    private static TestContext CreateContext()
    {
        var signaling = new RecordingSignalingClient();
        var factory = new FakePeerConnectionFactory();
        var manager = new PeerConnectionManager(factory, new WebRtcPublisherOptions());
        return new(signaling, factory, new WebRtcPublisher(signaling, manager));
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

        public Task<IWebRtcPeerConnection> CreateAsync(string viewerId, WebRtcPublisherOptions options, CancellationToken cancellationToken = default)
        {
            var peer = new FakePeerConnection(viewerId);
            Peers.Add(peer);
            return Task.FromResult<IWebRtcPeerConnection>(peer);
        }
    }

    private sealed class FakePeerConnection(string viewerId) : IWebRtcPeerConnection
    {
        public string ViewerId { get; } = viewerId;
        public int OfferCount { get; private set; }
        public int IceRestartCount { get; private set; }
        public int RenegotiationOffers { get; private set; }
        public bool OutgoingAudioMuted { get; private set; }

        public PeerConnectionDiagnostics Diagnostics => new(ViewerId, PeerConnectionState.Connected);

        public event Func<WebRtcIceCandidate, CancellationToken, Task>? LocalIceCandidateReady;
        public event Action? DiagnosticsChanged;
        public event Action<RemoteAudioFrame>? RemoteAudioFrameReceived;

        public void RaiseRemoteAudio(RemoteAudioFrame frame) => RemoteAudioFrameReceived?.Invoke(frame);

        public Task<WebRtcSessionDescription> CreateOfferAsync(CancellationToken cancellationToken = default)
        {
            OfferCount++;
            _ = LocalIceCandidateReady;
            _ = DiagnosticsChanged;
            return Task.FromResult(new WebRtcSessionDescription("offer", $"offer-{ViewerId}"));
        }

        public Task<WebRtcSessionDescription> CreateIceRestartOfferAsync(CancellationToken cancellationToken = default)
        {
            IceRestartCount++;
            return Task.FromResult(new WebRtcSessionDescription("offer", $"restart-{ViewerId}-{IceRestartCount}"));
        }

        public Task<WebRtcSessionDescription> CreateRenegotiationOfferAsync(CancellationToken cancellationToken = default)
        {
            RenegotiationOffers++;
            return Task.FromResult(new WebRtcSessionDescription("offer", $"renegotiated-{RenegotiationOffers}"));
        }

        public void SetOutgoingAudioMuted(bool muted) => OutgoingAudioMuted = muted;

        public Task ApplyAnswerAsync(WebRtcSessionDescription answer, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task AddRemoteIceCandidateAsync(WebRtcIceCandidate candidate, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SendAudioFrameAsync(WebRtcAudioFrame frame, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
