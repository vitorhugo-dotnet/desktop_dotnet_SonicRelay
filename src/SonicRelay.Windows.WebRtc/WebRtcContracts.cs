using SonicRelay.Windows.Signaling;

namespace SonicRelay.Windows.WebRtc;

public enum PeerConnectionState
{
    New,
    Connecting,
    Connected,
    Disconnected,
    Failed,
    Closed
}

public sealed record WebRtcSessionDescription(string Type, string Sdp);

/// <summary>
/// Which way audio flows on a peer connection. Decided by the session mode at creation time
/// and fixed for that connection's life, because the m-line it produces is what a peer can
/// (or cannot) attach its own track to later.
/// </summary>
public enum WebRtcAudioDirection
{
    /// <summary>One-way: the publisher transmits and the viewer only receives.</summary>
    SendOnly,

    /// <summary>
    /// Two-way (`duplex`). The audio m-line is offered <c>sendrecv</c> from the very first
    /// offer even when neither side is transmitting yet: this side is the only offerer in
    /// the protocol, so a peer that later starts sending its own audio has no way to add an
    /// m-line of its own — it can only answer into one that already exists.
    /// </summary>
    SendRecv
}

/// <summary>
/// Decoded PCM16 audio received from a peer, ready for playback. Interleaved when stereo.
/// </summary>
public sealed record RemoteAudioFrame(short[] Samples, int SampleRate, int ChannelCount);

public sealed record WebRtcIceCandidate(string Candidate, string? SdpMid = null, int? SdpMLineIndex = null);

public sealed class WebRtcAudioFrame
{
    private readonly byte[] data;

    public WebRtcAudioFrame(ReadOnlySpan<byte> data, int sampleRate, int channelCount, TimeSpan timestamp)
    {
        if (data.IsEmpty) throw new ArgumentException("Audio frame data cannot be empty.", nameof(data));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        if (channelCount is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(channelCount));
        ArgumentOutOfRangeException.ThrowIfLessThan(timestamp, TimeSpan.Zero);
        this.data = data.ToArray();
        SampleRate = sampleRate;
        ChannelCount = channelCount;
        Timestamp = timestamp;
    }

    public ReadOnlyMemory<byte> Data => data;
    public int SampleRate { get; }
    public int ChannelCount { get; }
    public TimeSpan Timestamp { get; }
}

public sealed record WebRtcIceServer
{
    public WebRtcIceServer(IEnumerable<string> urls, string? username = null, string? credential = null)
    {
        Urls = (urls ?? throw new ArgumentNullException(nameof(urls))).Select(url =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(url);
            return url;
        }).ToArray();
        if (Urls.Count == 0) throw new ArgumentException("An ICE server requires at least one URL.", nameof(urls));
        Username = username;
        Credential = credential;
    }

    public IReadOnlyList<string> Urls { get; }
    public string? Username { get; }
    public string? Credential { get; }
}

/// <summary>
/// Resolves the ICE servers (STUN/TURN plus short-lived TURN credentials) to
/// apply to a new peer connection. Implementations live outside this project
/// (e.g. backed by the SonicRelay API) so the WebRTC layer stays free of HTTP
/// concerns; they must not throw and should fall back to STUN-only defaults.
/// </summary>
public interface IIceServersProvider
{
    Task<IReadOnlyList<WebRtcIceServer>> GetIceServersAsync(CancellationToken cancellationToken = default);
}

public sealed class WebRtcPublisherOptions
{
    public WebRtcPublisherOptions(IEnumerable<WebRtcIceServer>? iceServers = null)
    {
        IceServers = (iceServers ?? []).Select(server =>
            server ?? throw new ArgumentException("ICE servers cannot be null.", nameof(iceServers))).ToArray();
    }

    public IReadOnlyList<WebRtcIceServer> IceServers { get; }
}

/// <summary>
/// Publisher-side audio send counters for one peer (issue #31): what was encoded
/// and sent, what was dropped locally, how far pacing is behind, and the encoder
/// configuration needed to correlate receiver-side loss reports. Contains no SDP,
/// addresses, or other sensitive connection data.
/// </summary>
public sealed record AudioSendDiagnostics(
    long EncodedPacketsSent,
    long PacedPacketsDropped,
    long SendFailures,
    int PacingBacklogPackets,
    TimeSpan PacingBacklogDuration,
    int FrameDurationMs,
    int OpusBitrateKbps,
    int Channels,
    string ProfileId,
    bool InbandFecEnabled,
    int ExpectedPacketLossPercent);

/// <summary>
/// Receiver-side quality for one peer, taken from the viewer's RTCP receiver reports about
/// our outgoing audio stream (issue #32's "real metrics"). Jitter is the interarrival jitter
/// the viewer measured; <see cref="PacketLossPercent"/> is the loss fraction of the most
/// recent report interval; <see cref="CumulativePacketsLost"/> is the running total. Contains
/// no addresses or SDP — only quality counters.
/// </summary>
public sealed record AudioReceptionDiagnostics(
    TimeSpan Jitter,
    double PacketLossPercent,
    long CumulativePacketsLost)
{
    /// <summary>
    /// Projects a raw RTCP reception report sample into UI-friendly units: RTP jitter units
    /// on the given clock become a duration, and the 8-bit fraction-lost becomes a percentage.
    /// </summary>
    public static AudioReceptionDiagnostics FromReport(uint jitterRtpUnits, byte fractionLost, int cumulativePacketsLost, int clockRateHz)
    {
        var clock = clockRateHz > 0 ? clockRateHz : 48000;
        return new AudioReceptionDiagnostics(
            Jitter: TimeSpan.FromSeconds((double)jitterRtpUnits / clock),
            // RTCP fraction lost is the loss count scaled to an 8-bit fixed point (0..255).
            PacketLossPercent: fractionLost / 256d * 100d,
            CumulativePacketsLost: cumulativePacketsLost);
    }
}

public sealed record PeerConnectionDiagnostics(
    string ViewerId,
    PeerConnectionState State,
    string? SelectedCandidatePair = null,
    TimeSpan? EstimatedRoundTripTime = null,
    AudioSendDiagnostics? AudioSend = null,
    AudioReceptionDiagnostics? AudioReceive = null,
    // How many audio frames this peer has sent us; only ever non-zero in a duplex session.
    long InboundAudioFrames = 0);

public sealed record WebRtcPublisherDiagnostics(
    int ViewerConnectionCount,
    IReadOnlyList<PeerConnectionDiagnostics> Viewers,
    string? LastError = null);

public sealed record ViewerPeer(string ViewerId, IWebRtcPeerConnection Connection);

public sealed record ViewerPeerRegistration(ViewerPeer Peer, bool WasCreated);

public interface IWebRtcPeerConnection : IAsyncDisposable
{
    string ViewerId { get; }
    PeerConnectionDiagnostics Diagnostics { get; }
    event Func<WebRtcIceCandidate, CancellationToken, Task>? LocalIceCandidateReady;
    event Action? DiagnosticsChanged;
    Task<WebRtcSessionDescription> CreateOfferAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Restarts ICE on this peer connection and produces a fresh offer carrying the new ICE
    /// credentials, without discarding the connection or its negotiated audio track. Used to
    /// recover a peer whose viewer reconnected (or whose ICE degraded) rather than tearing
    /// down and rebuilding the whole connection.
    /// </summary>
    Task<WebRtcSessionDescription> CreateIceRestartOfferAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Produces a fresh offer on the existing connection <em>without</em> restarting ICE, so a
    /// peer can add or drop an audio track mid-session. Unlike
    /// <see cref="CreateIceRestartOfferAsync"/> this keeps the ICE credentials — the network
    /// path is fine, only the media description changed.
    /// </summary>
    Task<WebRtcSessionDescription> CreateRenegotiationOfferAsync(CancellationToken cancellationToken = default);

    Task ApplyAnswerAsync(WebRtcSessionDescription answer, CancellationToken cancellationToken = default);
    Task AddRemoteIceCandidateAsync(WebRtcIceCandidate candidate, CancellationToken cancellationToken = default);
    Task SendAudioFrameAsync(WebRtcAudioFrame frame, CancellationToken cancellationToken = default);

    /// <summary>Decoded audio arriving from the peer. Never raised on a send-only connection.</summary>
    event Action<RemoteAudioFrame>? RemoteAudioFrameReceived;

    /// <summary>
    /// Stops (or resumes) transmitting on this connection. Muting drops frames at the encoder
    /// instead of renegotiating, so the negotiated m-line survives and unmuting is instant.
    /// </summary>
    void SetOutgoingAudioMuted(bool muted);
}

public interface IWebRtcPeerConnectionFactory
{
    Task<IWebRtcPeerConnection> CreateAsync(
        string viewerId,
        WebRtcPublisherOptions options,
        CancellationToken cancellationToken = default);
}

public interface IPeerConnectionManager : IAsyncDisposable
{
    int ViewerCount { get; }
    event Func<string, WebRtcIceCandidate, CancellationToken, Task>? LocalIceCandidateReady;
    event Action? DiagnosticsChanged;
    Task<ViewerPeerRegistration> RegisterViewerAsync(string viewerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests an ICE-restart offer from the existing peer connection for <paramref name="viewerId"/>,
    /// or returns <c>null</c> if no peer is registered for it yet.
    /// </summary>
    Task<WebRtcSessionDescription?> RequestIceRestartAsync(string viewerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests a renegotiation offer from the existing peer connection for
    /// <paramref name="viewerId"/>, or returns <c>null</c> if no peer is registered for it.
    /// </summary>
    Task<WebRtcSessionDescription?> RequestRenegotiationAsync(string viewerId, CancellationToken cancellationToken = default);

    /// <summary>Decoded audio arriving from a peer, tagged with the peer that sent it.</summary>
    event Action<string, RemoteAudioFrame>? RemoteAudioFrameReceived;

    /// <summary>Mutes or unmutes outgoing audio on every registered peer.</summary>
    void SetOutgoingAudioMuted(bool muted);

    Task ApplyAnswerAsync(string viewerId, WebRtcSessionDescription answer, CancellationToken cancellationToken = default);
    Task AddRemoteIceCandidateAsync(string viewerId, WebRtcIceCandidate candidate, CancellationToken cancellationToken = default);
    Task PushAudioFrameAsync(WebRtcAudioFrame frame, CancellationToken cancellationToken = default);
    Task<bool> RemoveViewerAsync(string viewerId, CancellationToken cancellationToken = default);
    Task RemoveAllAsync(CancellationToken cancellationToken = default);
    IReadOnlyList<PeerConnectionDiagnostics> GetDiagnostics();
}

public interface IWebRtcPublisher : ISignalingMessageHandler, IAsyncDisposable
{
    WebRtcPublisherDiagnostics Diagnostics { get; }
    event Action<WebRtcPublisherDiagnostics>? DiagnosticsChanged;
    event Action<string>? IceRestartRequested;
    event Action<string>? PeerRebuildRequested;

    /// <summary>
    /// Raised with the backend's authoritative state for a participant whenever it changes,
    /// this device's own included. The UI reads publish permission from here, never from what
    /// a peer claims about itself.
    /// </summary>
    event Action<ParticipantAudioState>? ParticipantAudioStateChanged;

    /// <summary>
    /// Decoded audio from a participant the backend has authorized to publish. Audio from an
    /// unauthorized peer is dropped before this and never reaches playback.
    /// </summary>
    event Action<string, RemoteAudioFrame>? RemoteAudioFrameReceived;

    /// <summary>The latest server-published state of every participant seen in this session.</summary>
    IReadOnlyCollection<ParticipantAudioState> Participants { get; }

    Task PushAudioFrameAsync(WebRtcAudioFrame frame, CancellationToken cancellationToken = default);

    /// <summary>Mutes or unmutes this device's outgoing audio and announces it to the session.</summary>
    Task SetOutgoingAudioMutedAsync(bool muted, CancellationToken cancellationToken = default);
}

public sealed class WebRtcPublisherException(string message, Exception? innerException = null)
    : Exception(message, innerException);
