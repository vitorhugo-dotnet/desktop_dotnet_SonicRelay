using Concentus;
using Concentus.Structs;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SonicRelay.Windows.Core.Audio;

namespace SonicRelay.Windows.WebRtc;

/// <summary>
/// Publisher-side peer connection backed by SIPSorcery: one Opus 48 kHz audio
/// track per viewer, encoded per the selected <see cref="AudioQualityProfile"/>
/// (channels/bitrate/frame duration). Trickle ICE — local candidates are surfaced
/// through <see cref="LocalIceCandidateReady"/> as they gather and remote ones can
/// be applied at any time after the offer.
///
/// The track is send-only by default. A <see cref="WebRtcAudioDirection.SendRecv"/>
/// connection (a `duplex` session) additionally decodes the peer's inbound Opus and
/// raises it on <see cref="RemoteAudioFrameReceived"/> for playback.
/// </summary>
public sealed class SipSorceryPeerConnection : IWebRtcPeerConnection
{
    private const int SampleRate = 48000;

    // Encoded packets may queue this much audio behind the pacing schedule before
    // the oldest are discarded; the upper end of the 100–200 ms budget (issue #31).
    private static readonly TimeSpan PacingLatencyBudget = TimeSpan.FromMilliseconds(200);

    private readonly RTCPeerConnection connection;
    private readonly WebRtcAudioDirection direction;
    private readonly OpusEncoder opusEncoder;

    // Created lazily on the first inbound frame: a send-only connection never needs one, and
    // even a duplex one only needs it once the peer actually starts transmitting.
    private IOpusDecoder? opusDecoder;
    private int decoderChannels;
    private readonly object decoderLock = new();
    private long inboundAudioFrames;
    private volatile bool outgoingMuted;
    private readonly OpusFrameAccumulator accumulator;
    private readonly RtpPacketPacer pacer;
    private readonly AudioQualityProfile profile;
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private readonly AudioFormat opusFormat;
    private readonly byte[] encodeBuffer = new byte[4000];
    // Samples per channel in one frame at 48 kHz; the accumulator emits exactly this.
    private readonly int samplesPerChannel;
    private volatile bool formatNegotiated;
    private PeerConnectionState state = PeerConnectionState.New;
    // Latest receiver-side quality from the viewer's RTCP RR about our stream. Reference
    // assignment of an immutable record is atomic, so no lock is needed to read it.
    private AudioReceptionDiagnostics? reception;
    // The last sender report we emitted (compact NTP + when), used to correlate the RR that
    // echoes it for RTT. Immutable record, assigned atomically.
    private SentSenderReport? lastSentReport;
    // Estimated RTT in ticks, or -1 for none; read/written with Volatile for cross-thread safety.
    private long roundTripTicks = -1;
    private bool disposed;

    private sealed record SentSenderReport(uint CompactNtp, DateTime SentAtUtc);

    public SipSorceryPeerConnection(
        string viewerId,
        RTCPeerConnection connection,
        AudioQualityProfile? profile = null,
        WebRtcAudioDirection direction = WebRtcAudioDirection.SendOnly)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(viewerId);
        this.direction = direction;
        ViewerId = viewerId;
        this.connection = connection ?? throw new ArgumentNullException(nameof(connection));

        var quality = profile ?? AudioQualityProfile.Default;
        quality.Validate();
        this.profile = quality;
        var channels = quality.Channels;
        var bitrate = quality.OpusBitrateKbps * 1000;
        var stereo = channels == 2 ? 1 : 0;
        samplesPerChannel = SampleRate * quality.FrameDurationMs / 1000;
        accumulator = new OpusFrameAccumulator(SampleRate, channels, quality.FrameDurationMs);

        // Advertise Opus with explicit channel/bitrate hints. Without the stereo and
        // maxaveragebitrate fmtp params the remote negotiates a low-bitrate mono
        // profile that sounds muffled; the encoder below is configured to match.
        opusFormat = new AudioFormat(
            AudioCodecsEnum.OPUS,
            111,
            SampleRate,
            channels,
            $"useinbandfec=1;stereo={stereo};sprop-stereo={stereo};maxaveragebitrate={bitrate};maxplaybackrate=48000");
        // `sendrecv` from the first offer in a duplex session, even though nothing is coming
        // back yet: this side is the only offerer, so a peer that later starts sending its own
        // audio can only answer into an m-line that already accepts audio.
        this.connection.addTrack(new MediaStreamTrack(
            opusFormat,
            direction == WebRtcAudioDirection.SendRecv
                ? MediaStreamStatusEnum.SendRecv
                : MediaStreamStatusEnum.SendOnly));

        opusEncoder = OpusEncoderFactory.Create(quality);
        // Encoded packets go through a monotonic pacer instead of straight to
        // SendAudio: the accumulator can yield several frames per capture callback
        // and SIPSorcery does not pace transmission by RTP timestamp, so without
        // this stage those frames leave as a burst.
        pacer = new RtpPacketPacer(
            TimeSpan.FromMilliseconds(quality.FrameDurationMs),
            PacingLatencyBudget,
            packet => this.connection.SendAudio((uint)samplesPerChannel, packet));

        this.connection.OnAudioFormatsNegotiated += OnAudioFormatsNegotiated;
        this.connection.onicecandidate += OnIceCandidate;
        this.connection.onconnectionstatechange += OnConnectionStateChanged;
        this.connection.OnReceiveReport += OnReceiveReport;
        this.connection.OnSendReport += OnSendReport;
        if (direction == WebRtcAudioDirection.SendRecv)
        {
            this.connection.OnAudioFrameReceived += OnAudioFrameReceived;
        }
    }

    public string ViewerId { get; }

    public PeerConnectionDiagnostics Diagnostics => new(
        ViewerId,
        state,
        SelectedCandidatePairTypes(),
        RoundTripTime(),
        new AudioSendDiagnostics(
            pacer.PacketsSent,
            pacer.PacketsDropped,
            pacer.SendFailures,
            pacer.Backlog,
            pacer.BacklogDuration,
            profile.FrameDurationMs,
            profile.OpusBitrateKbps,
            profile.Channels,
            profile.Id,
            opusEncoder.UseInbandFEC,
            profile.ExpectedPacketLossPercent),
        reception,
        Interlocked.Read(ref inboundAudioFrames));

    public event Func<WebRtcIceCandidate, CancellationToken, Task>? LocalIceCandidateReady;
    public event Action? DiagnosticsChanged;
    public event Action<RemoteAudioFrame>? RemoteAudioFrameReceived;

    public async Task<WebRtcSessionDescription> CreateOfferAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var offer = connection.createOffer(null)
            ?? throw new WebRtcPublisherException("SIPSorcery could not create an SDP offer.");
        await connection.setLocalDescription(offer).ConfigureAwait(false);
        return new WebRtcSessionDescription("offer", offer.sdp);
    }

    public async Task<WebRtcSessionDescription> CreateIceRestartOfferAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        // Generates new ICE ufrag/pwd on the existing connection; the offer that follows
        // carries them so the remote renegotiates ICE without losing the negotiated audio
        // track or resetting sender/receiver report correlation.
        connection.restartIce();
        return await CreateOfferAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<WebRtcSessionDescription> CreateRenegotiationOfferAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        // Deliberately no restartIce(): the network path is fine here, only the media
        // description changed. Regenerating ICE credentials would make every start/stop of a
        // peer's audio re-run connectivity checks and drop audio for the duration.
        return await CreateOfferAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Stops feeding the encoder without touching the negotiated session. The track and its
    /// m-line stay exactly as they were, so unmuting resumes immediately and the peer never
    /// sees a renegotiation for something as ordinary as a mute button.
    /// </summary>
    public void SetOutgoingAudioMuted(bool muted)
    {
        if (outgoingMuted == muted) return;
        outgoingMuted = muted;
        if (muted)
        {
            // Anything already queued describes audio from before the mute; sending it after
            // the fact would leak exactly the moment the user meant to cut.
            accumulator.Clear();
            pacer.Clear();
        }
        DiagnosticsChanged?.Invoke();
    }

    public Task ApplyAnswerAsync(WebRtcSessionDescription answer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(answer);
        ThrowIfDisposed();
        var result = connection.setRemoteDescription(new RTCSessionDescriptionInit
        {
            type = RTCSdpType.answer,
            sdp = answer.Sdp
        });
        return result == SetDescriptionResultEnum.OK
            ? Task.CompletedTask
            : throw new WebRtcPublisherException($"The WebRTC answer was rejected: {result}.");
    }

    public Task AddRemoteIceCandidateAsync(WebRtcIceCandidate candidate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ThrowIfDisposed();
        try
        {
            connection.addIceCandidate(new RTCIceCandidateInit
            {
                candidate = candidate.Candidate,
                sdpMid = candidate.SdpMid,
                sdpMLineIndex = (ushort)(candidate.SdpMLineIndex ?? 0)
            });
        }
        catch (Exception exception)
        {
            throw new WebRtcPublisherException("The remote ICE candidate could not be applied.", exception);
        }
        return Task.CompletedTask;
    }

    public async Task SendAudioFrameAsync(WebRtcAudioFrame frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (disposed) return;
        await sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (disposed || outgoingMuted) return;
            if (state != PeerConnectionState.Connected || !formatNegotiated)
            {
                // No point queueing audio the transport cannot carry yet; stale
                // buffered samples would only add latency once it connects.
                accumulator.Clear();
                pacer.Clear();
                return;
            }

            var samples = PcmAudioConverter.ToS16(frame.Data.Span, WebRtcSourceSampleFormat.Pcm16);
            accumulator.Append(samples, frame.SampleRate, frame.ChannelCount);
            while (accumulator.TryTakeFrame(out var pcm))
            {
                var length = opusEncoder.Encode(pcm, samplesPerChannel, encodeBuffer, encodeBuffer.Length);
                if (length <= 0) continue;
                // Opus RTP timestamps advance on the 48 kHz clock: samplesPerChannel
                // units per frame (480/960/1920 for 10/20/40 ms) regardless of
                // channels. The pacer sends one packet per frame deadline instead
                // of bursting everything the accumulator produced.
                pacer.Enqueue(encodeBuffer[..length]);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new WebRtcPublisherException("Sending audio over the peer connection failed.", exception);
        }
        finally
        {
            sendLock.Release();
        }
    }

    /// <summary>
    /// Decodes one inbound Opus frame to PCM16 and hands it to playback. Only wired up on a
    /// duplex connection. Decoding failures are dropped rather than propagated: a corrupt
    /// packet is a normal event on a lossy path and must not disturb the send side.
    /// </summary>
    private void OnAudioFrameReceived(EncodedAudioFrame frame)
    {
        if (disposed) return;
        var handler = RemoteAudioFrameReceived;
        if (handler is null) return;
        var encoded = frame.EncodedAudio;
        if (encoded is null || encoded.Length == 0) return;
        if (!string.Equals(frame.AudioFormat.FormatName, "OPUS", StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            var channels = frame.AudioFormat.ChannelCount is 1 or 2 ? frame.AudioFormat.ChannelCount : 1;
            var decoder = ResolveDecoder(channels);
            // 120 ms at 48 kHz is the largest frame Opus can carry, so this never truncates.
            var pcm = new short[SampleRate / 1000 * 120 * channels];
            int decodedPerChannel;
            lock (decoderLock)
            {
                decodedPerChannel = decoder.Decode(encoded, pcm, pcm.Length / channels, false);
            }
            if (decodedPerChannel <= 0) return;
            var sampleCount = decodedPerChannel * channels;
            Interlocked.Increment(ref inboundAudioFrames);
            handler(new RemoteAudioFrame(pcm[..sampleCount], SampleRate, channels));
        }
        catch
        {
            // A packet that will not decode is lost audio, not a broken connection.
        }
    }

    private IOpusDecoder ResolveDecoder(int channels)
    {
        lock (decoderLock)
        {
            if (opusDecoder is not null && decoderChannels == channels) return opusDecoder;
            opusDecoder?.Dispose();
            opusDecoder = OpusCodecFactory.CreateDecoder(SampleRate, channels);
            decoderChannels = channels;
            return opusDecoder;
        }
    }

    private void OnAudioFormatsNegotiated(List<AudioFormat> formats)
    {
        // Gate sending until the remote has accepted Opus; we always encode Opus
        // ourselves, so only the fact of negotiation matters, not the returned format.
        if (formats.Any(format => string.Equals(format.FormatName, "OPUS", StringComparison.OrdinalIgnoreCase)))
        {
            formatNegotiated = true;
        }
    }

    private void OnIceCandidate(RTCIceCandidate? candidate)
    {
        if (candidate is null) return;
        var handlers = LocalIceCandidateReady;
        if (handlers is null) return;
        var payload = ToSignalingCandidate(candidate.candidate, candidate.sdpMid, candidate.sdpMLineIndex);
        if (payload is null) return;
        _ = DispatchCandidateAsync(handlers, payload);
    }

    /// <summary>
    /// Projects a SIPSorcery candidate onto the signaling shape, or returns null for a blank
    /// one (SIPSorcery emits an empty candidate to mark end-of-gathering, which the protocol
    /// leaves to each client and this one does not forward).
    /// </summary>
    /// <remarks>
    /// Split out from <see cref="OnIceCandidate"/> so the wire format can be pinned down
    /// without a live ICE agent. The prefix is the whole reason this exists: browsers and
    /// flutter_webrtc expect the standard <c>candidate:</c> prefix that SIPSorcery omits from
    /// <c>RTCIceCandidate.candidate</c>, and getting it wrong breaks every viewer while
    /// looking perfectly healthy on this side.
    /// </remarks>
    internal static WebRtcIceCandidate? ToSignalingCandidate(string? candidate, string? sdpMid, ushort sdpMLineIndex)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return null;
        var value = candidate.StartsWith("candidate:", StringComparison.OrdinalIgnoreCase)
            ? candidate
            : $"candidate:{candidate}";
        // An empty mid means "route by line index"; passing it through as an empty string
        // would have peers match on a mid that does not exist.
        return new WebRtcIceCandidate(value, string.IsNullOrEmpty(sdpMid) ? null : sdpMid, sdpMLineIndex);
    }

    private static async Task DispatchCandidateAsync(
        Func<WebRtcIceCandidate, CancellationToken, Task> handlers,
        WebRtcIceCandidate candidate)
    {
        foreach (Func<WebRtcIceCandidate, CancellationToken, Task> handler in handlers.GetInvocationList())
        {
            try
            {
                await handler(candidate, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Candidate delivery is best-effort; a failed send must not tear
                // down ICE gathering. Connectivity failures surface via the
                // connection state instead.
            }
        }
    }

    /// <summary>
    /// Candidate types of the nominated ICE pair ("host->relay" etc.), so
    /// diagnostics can tell direct from relayed transport. Types only — addresses
    /// and ports are sensitive connection data and stay out of diagnostics.
    /// </summary>
    private string? SelectedCandidatePairTypes()
    {
        if (state != PeerConnectionState.Connected) return null;
        try
        {
            var entry = connection.GetRtpChannel()?.NominatedEntry;
            if (entry?.LocalCandidate is null || entry.RemoteCandidate is null) return null;
            return $"{entry.LocalCandidate.type}->{entry.RemoteCandidate.type}";
        }
        catch
        {
            // Diagnostics must never take down the stream; the pair just reads
            // as unknown.
            return null;
        }
    }

    /// <summary>
    /// Captures the viewer's RTCP receiver report about our audio stream — jitter and packet
    /// loss the viewer observed. Best-effort: any parsing issue leaves the previous reading in
    /// place and never disturbs the send path.
    /// </summary>
    private void OnReceiveReport(System.Net.IPEndPoint endpoint, SDPMediaTypesEnum media, RTCPCompoundPacket report)
    {
        if (media != SDPMediaTypesEnum.audio) return;
        try
        {
            var samples = report.ReceiverReport?.ReceptionReports;
            if (samples is null || samples.Count == 0) return;

            // The sample whose SSRC matches our outgoing audio source is the report about us;
            // fall back to the first when the SSRC is not yet known.
            var ourSsrc = connection.AudioRtcpSession?.Ssrc;
            var sample = samples.FirstOrDefault(s => ourSsrc is null || s.SSRC == ourSsrc) ?? samples[0];

            reception = AudioReceptionDiagnostics.FromReport(
                sample.Jitter, sample.FractionLost, sample.PacketsLost, SampleRate);

            // Correlate the RR with the sender report it acknowledges (LSR) for RTT.
            var sent = lastSentReport;
            if (sent is not null && sample.LastSenderReportTimestamp == sent.CompactNtp)
            {
                var rtt = RtcpRoundTripEstimator.EstimateRoundTripTime(
                    sent.SentAtUtc, DateTime.UtcNow, sample.DelaySinceLastSenderReport);
                if (rtt is { } value) Volatile.Write(ref roundTripTicks, value.Ticks);
            }

            DiagnosticsChanged?.Invoke();
        }
        catch
        {
            // Diagnostics must never take down the stream; keep the last known reading.
        }
    }

    /// <summary>Records the compact NTP timestamp and send time of each outgoing sender report,
    /// so the matching receiver report can be turned into an RTT estimate.</summary>
    private void OnSendReport(SDPMediaTypesEnum media, RTCPCompoundPacket report)
    {
        if (media != SDPMediaTypesEnum.audio || report.SenderReport is null) return;
        lastSentReport = new SentSenderReport(
            RtcpRoundTripEstimator.CompactNtp(report.SenderReport.NtpTimestamp), DateTime.UtcNow);
    }

    private TimeSpan? RoundTripTime()
    {
        var ticks = Volatile.Read(ref roundTripTicks);
        return ticks >= 0 ? TimeSpan.FromTicks(ticks) : null;
    }

    private void OnConnectionStateChanged(RTCPeerConnectionState next)
    {
        state = next switch
        {
            RTCPeerConnectionState.@new => PeerConnectionState.New,
            RTCPeerConnectionState.connecting => PeerConnectionState.Connecting,
            RTCPeerConnectionState.connected => PeerConnectionState.Connected,
            RTCPeerConnectionState.disconnected => PeerConnectionState.Disconnected,
            RTCPeerConnectionState.failed => PeerConnectionState.Failed,
            RTCPeerConnectionState.closed => PeerConnectionState.Closed,
            _ => state
        };
        DiagnosticsChanged?.Invoke();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        await sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return;
            disposed = true;
        }
        finally
        {
            sendLock.Release();
        }

        connection.OnAudioFormatsNegotiated -= OnAudioFormatsNegotiated;
        connection.onicecandidate -= OnIceCandidate;
        connection.onconnectionstatechange -= OnConnectionStateChanged;
        connection.OnReceiveReport -= OnReceiveReport;
        connection.OnSendReport -= OnSendReport;
        if (direction == WebRtcAudioDirection.SendRecv)
        {
            connection.OnAudioFrameReceived -= OnAudioFrameReceived;
        }
        lock (decoderLock)
        {
            opusDecoder?.Dispose();
            opusDecoder = null;
        }
        // Stop paced sends before closing the transport they write to.
        await pacer.DisposeAsync().ConfigureAwait(false);
        try
        {
            connection.close();
        }
        catch
        {
            // Closing an already-failed transport must not throw out of dispose.
        }
        sendLock.Dispose();
    }
}
