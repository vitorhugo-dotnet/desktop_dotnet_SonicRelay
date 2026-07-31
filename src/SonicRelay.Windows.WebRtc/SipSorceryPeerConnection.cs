using Concentus.Structs;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SonicRelay.Windows.Core.Audio;

namespace SonicRelay.Windows.WebRtc;

/// <summary>
/// Publisher-side peer connection backed by SIPSorcery: one send-only Opus
/// 48 kHz audio track per viewer, encoded per the selected
/// <see cref="AudioQualityProfile"/> (channels/bitrate/frame duration). Trickle
/// ICE — local candidates are surfaced through <see cref="LocalIceCandidateReady"/>
/// as they gather and remote ones can be applied at any time after the offer.
/// </summary>
public sealed class SipSorceryPeerConnection : IWebRtcPeerConnection
{
    private const int SampleRate = 48000;

    // Encoded packets may queue this much audio behind the pacing schedule before
    // the oldest are discarded; the upper end of the 100–200 ms budget (issue #31).
    private static readonly TimeSpan PacingLatencyBudget = TimeSpan.FromMilliseconds(200);

    private readonly RTCPeerConnection connection;
    private readonly OpusEncoder opusEncoder;
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
        AudioQualityProfile? profile = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(viewerId);
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
        this.connection.addTrack(new MediaStreamTrack(opusFormat, MediaStreamStatusEnum.SendOnly));

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
        reception);

    public event Func<WebRtcIceCandidate, CancellationToken, Task>? LocalIceCandidateReady;
    public event Action? DiagnosticsChanged;

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
            if (disposed) return;
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
        if (candidate is null || string.IsNullOrWhiteSpace(candidate.candidate)) return;
        var handlers = LocalIceCandidateReady;
        if (handlers is null) return;
        // Browsers and flutter_webrtc expect the standard "candidate:" prefix
        // that SIPSorcery omits from RTCIceCandidate.candidate.
        var value = candidate.candidate.StartsWith("candidate:", StringComparison.OrdinalIgnoreCase)
            ? candidate.candidate
            : $"candidate:{candidate.candidate}";
        var sdpMid = string.IsNullOrEmpty(candidate.sdpMid) ? null : candidate.sdpMid;
        var payload = new WebRtcIceCandidate(value, sdpMid, candidate.sdpMLineIndex);
        _ = DispatchCandidateAsync(handlers, payload);
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
