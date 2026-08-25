using System.Text.Json;

namespace SonicRelay.Windows.Signaling.Tests;

public sealed class ParticipantAudioStateTests
{
    [Fact]
    public void ParsesAFullDuplexPayload()
    {
        var state = ParticipantAudioState.TryParse(JsonSerializer.SerializeToElement(new
        {
            participantId = "p-1",
            role = "publisher",
            sessionMode = "duplex",
            audioSendAllowed = true,
            canSendAudio = true,
            canReceiveAudio = true,
            audioMuted = true,
        }));

        Assert.NotNull(state);
        Assert.Equal("p-1", state.ParticipantId);
        Assert.True(state.IsPublisher);
        Assert.True(state.IsDuplexSession);
        Assert.True(state.IsAudioTrusted);
        Assert.True(state.AudioMuted);
    }

    [Fact]
    public void ReadsAPreDuplexPayloadAsAReceiveOnlyBroadcastViewer()
    {
        var state = ParticipantAudioState.TryParse(JsonSerializer.SerializeToElement(new
        {
            participantId = "p-2",
            role = "viewer",
        }));

        Assert.NotNull(state);
        Assert.Equal(ParticipantAudioState.BroadcastMode, state.SessionMode);
        Assert.False(state.AudioSendAllowed);
        Assert.False(state.CanSendAudio);
        // Absent means a backend that predates duplex, whose participants all receive.
        Assert.True(state.CanReceiveAudio);
        Assert.False(state.IsAudioTrusted);
    }

    [Fact]
    public void AnAuthorizedPeerThatStoppedSendingIsNotTrustedAudio()
    {
        var state = ParticipantAudioState.TryParse(JsonSerializer.SerializeToElement(new
        {
            participantId = "p-3",
            audioSendAllowed = true,
            canSendAudio = false,
        }));

        Assert.NotNull(state);
        Assert.False(state.IsAudioTrusted);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"participantId":""}""")]
    [InlineData("""{"code":"invalid_message"}""")]
    [InlineData("[]")]
    public void ReturnsNullForAPayloadThatNamesNoParticipant(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Null(ParticipantAudioState.TryParse(document.RootElement.Clone()));
    }

    [Fact]
    public void ReturnsNullForAMissingPayload() => Assert.Null(ParticipantAudioState.TryParse(null));
}

public sealed class DuplexSignalingMessageTypeTests
{
    [Theory]
    [InlineData(SignalingMessageTypes.WebRtcRenegotiate)]
    [InlineData(SignalingMessageTypes.ParticipantCapabilities)]
    [InlineData(SignalingMessageTypes.ParticipantAudioStateChanged)]
    public void TheDuplexTypesAreAcceptedOnTheWire(string type)
    {
        Assert.True(SignalingMessageTypes.IsSupported(type));
        var envelope = new SignalingMessageEnvelope(type, "session-1");
        Assert.Equal(type, SignalingMessageEnvelope.Deserialize(envelope.Serialize()).Type);
    }

    [Fact]
    public void CapabilityPayloadsStayReadableInDiagnostics()
    {
        // Only SDP and ICE bodies describe network paths. Redacting a boolean and a
        // participant id would cost the duplex flow its only readable trace.
        Assert.False(SignalingMessageTypes.HasSensitivePayload(SignalingMessageTypes.ParticipantCapabilities));
        Assert.False(SignalingMessageTypes.HasSensitivePayload(SignalingMessageTypes.ParticipantAudioStateChanged));
        Assert.True(SignalingMessageTypes.HasSensitivePayload(SignalingMessageTypes.WebRtcOffer));
    }

    [Fact]
    public void RenegotiationCarriesNoSdpSoItsPayloadIsNotRedacted()
    {
        // `webrtc.renegotiate` carries only a reason string; the offer it triggers is what
        // carries SDP, and that stays redacted.
        Assert.False(SignalingMessageTypes.HasSensitivePayload(SignalingMessageTypes.WebRtcRenegotiate));
        var envelope = new SignalingMessageEnvelope(
            SignalingMessageTypes.WebRtcRenegotiate,
            "session-1",
            "viewer-1",
            JsonSerializer.SerializeToElement(new { reason = "adding-microphone-track" }));
        Assert.Contains("adding-microphone-track", envelope.ToSafeDiagnosticString(), StringComparison.Ordinal);
    }
}
