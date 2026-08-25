using Concentus.Enums;
using Concentus.Structs;
using SIPSorcery.Net;
using SonicRelay.Windows.Core.Audio;
using SonicRelay.Windows.WebRtc;

namespace SonicRelay.Windows.WebRtc.Tests;

public sealed class SipSorceryPeerConnectionTests
{
    [Fact]
    public void OpusMusicEncoderProducesFullbandStereoPackets()
    {
        // Mirrors the production encoder config; proves the Concentus 20 ms stereo
        // encode path yields a non-trivial Opus packet at the music bitrate.
        var encoder = new OpusEncoder(48000, 2, OpusApplication.OPUS_APPLICATION_AUDIO)
        {
            Bitrate = 128000,
            Complexity = 10,
            SignalType = OpusSignal.OPUS_SIGNAL_MUSIC,
        };
        var pcm = new short[960 * 2];
        for (var i = 0; i < 960; i++)
        {
            var sample = (short)(short.MaxValue * 0.5 * Math.Sin(2 * Math.PI * 1000 * i / 48000.0));
            pcm[i * 2] = sample;
            pcm[i * 2 + 1] = sample;
        }
        var buffer = new byte[4000];

        var length = encoder.Encode(pcm, 960, buffer, buffer.Length);

        Assert.True(length > 0, "Opus encode should produce a packet.");
    }

    [Fact]
    public async Task CreateOffer_produces_sendonly_opus_sdp()
    {
        await using var peer = new SipSorceryPeerConnection("viewer-1", new RTCPeerConnection(new RTCConfiguration()));

        var offer = await peer.CreateOfferAsync();

        Assert.Equal("offer", offer.Type);
        Assert.Contains("OPUS", offer.Sdp, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("a=sendonly", offer.Sdp, StringComparison.Ordinal);
        // Fullband stereo music profile — without these the remote negotiates a
        // muffled low-bitrate mono Opus stream.
        Assert.Contains("stereo=1", offer.Sdp, StringComparison.Ordinal);
        Assert.Contains("maxaveragebitrate=128000", offer.Sdp, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Voice_profile_offers_mono_opus_at_its_bitrate()
    {
        await using var peer = new SipSorceryPeerConnection(
            "viewer-1", new RTCPeerConnection(new RTCConfiguration()), AudioQualityProfile.Voice);

        var offer = await peer.CreateOfferAsync();

        Assert.Contains("OPUS", offer.Sdp, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("stereo=0", offer.Sdp, StringComparison.Ordinal);
        // Voice preset is 32 kbps.
        Assert.Contains("maxaveragebitrate=32000", offer.Sdp, StringComparison.Ordinal);
    }

    [Fact]
    public void A_candidate_without_the_prefix_gets_one()
    {
        // Browsers and flutter_webrtc expect the standard prefix that SIPSorcery omits.
        // Getting this wrong breaks every viewer while looking healthy on this side.
        var projected = SipSorceryPeerConnection.ToSignalingCandidate("1 1 udp 2130706431 10.0.0.1 5000 typ host", "0", 0);

        Assert.NotNull(projected);
        Assert.Equal("candidate:1 1 udp 2130706431 10.0.0.1 5000 typ host", projected.Candidate);
        Assert.Equal("0", projected.SdpMid);
        Assert.Equal(0, projected.SdpMLineIndex);
    }

    [Fact]
    public void A_candidate_that_already_has_the_prefix_keeps_exactly_one()
    {
        var projected = SipSorceryPeerConnection.ToSignalingCandidate("candidate:1 1 udp 2130706431 10.0.0.1 5000 typ host", "0", 0);

        Assert.NotNull(projected);
        Assert.StartsWith("candidate:1", projected.Candidate, StringComparison.Ordinal);
        Assert.DoesNotContain("candidate:candidate:", projected.Candidate, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_candidate_is_not_forwarded(string? candidate)
    {
        // SIPSorcery emits a blank candidate to mark end-of-gathering. The protocol leaves
        // that to each client and this one does not forward it.
        Assert.Null(SipSorceryPeerConnection.ToSignalingCandidate(candidate, "0", 0));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void An_empty_mid_becomes_null_so_peers_route_by_line_index(string? sdpMid)
    {
        var projected = SipSorceryPeerConnection.ToSignalingCandidate("1 1 udp 1 10.0.0.1 5000 typ host", sdpMid, 3);

        Assert.NotNull(projected);
        Assert.Null(projected.SdpMid);
        Assert.Equal(3, projected.SdpMLineIndex);
    }

    [Fact]
    public void SipSorcery_still_strips_the_prefix_this_projection_restores()
    {
        // The reason ToSignalingCandidate exists at all, pinned against the dependency rather
        // than assumed: hand SIPSorcery a candidate that *has* the standard prefix and it
        // gives it back without one. If a future bump starts preserving it, this fails and
        // says so, instead of the projection silently double-prefixing every candidate.
        const string withPrefix = "candidate:1 1 udp 2130706431 10.0.0.1 5000 typ host";
        var native = new RTCIceCandidate(new RTCIceCandidateInit
        {
            candidate = withPrefix,
            sdpMid = "0",
            sdpMLineIndex = 0,
        });

        Assert.DoesNotContain("candidate:", native.candidate, StringComparison.Ordinal);

        var projected = SipSorceryPeerConnection.ToSignalingCandidate(native.candidate, native.sdpMid, native.sdpMLineIndex);

        Assert.NotNull(projected);
        Assert.StartsWith("candidate:", projected.Candidate, StringComparison.Ordinal);
        Assert.DoesNotContain("candidate:candidate:", projected.Candidate, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Frames_sent_before_connection_are_discarded_not_buffered()
    {
        // Issue #31: disconnected/non-negotiated peers must not accumulate audio
        // for later playback — neither in the accumulator nor the pacer backlog.
        await using var peer = new SipSorceryPeerConnection("viewer-1", new RTCPeerConnection(new RTCConfiguration()));
        var frame = new WebRtcAudioFrame(new byte[960 * 2 * 2], 48000, 2, TimeSpan.FromMilliseconds(1));

        await peer.SendAudioFrameAsync(frame);

        var audioSend = peer.Diagnostics.AudioSend;
        Assert.NotNull(audioSend);
        Assert.Equal(0, audioSend!.EncodedPacketsSent);
        Assert.Equal(0, audioSend.PacingBacklogPackets);
    }

    [Fact]
    public async Task Diagnostics_expose_the_encoder_and_pacing_configuration()
    {
        await using var peer = new SipSorceryPeerConnection(
            "viewer-1", new RTCPeerConnection(new RTCConfiguration()), AudioQualityProfile.Voice);

        var audioSend = peer.Diagnostics.AudioSend;

        Assert.NotNull(audioSend);
        Assert.Equal(20, audioSend!.FrameDurationMs);
        Assert.Equal(32, audioSend.OpusBitrateKbps);
        Assert.Equal(1, audioSend.Channels);
        Assert.Equal("voice", audioSend.ProfileId);
        Assert.True(audioSend.InbandFecEnabled);
        Assert.Equal(AudioQualityProfile.DefaultExpectedPacketLossPercent, audioSend.ExpectedPacketLossPercent);
    }

    [Fact]
    public async Task Applying_a_malformed_answer_throws_publisher_exception()
    {
        await using var peer = new SipSorceryPeerConnection("viewer-1", new RTCPeerConnection(new RTCConfiguration()));
        await peer.CreateOfferAsync();

        await Assert.ThrowsAsync<WebRtcPublisherException>(
            () => peer.ApplyAnswerAsync(new WebRtcSessionDescription("answer", "not-valid-sdp")));
    }
}
