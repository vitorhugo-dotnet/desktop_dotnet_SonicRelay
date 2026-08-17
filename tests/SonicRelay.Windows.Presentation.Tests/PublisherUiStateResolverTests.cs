using SonicRelay.Windows.Audio;
using SonicRelay.Windows.Signaling;
using SonicRelay.Windows.WebRtc;

namespace SonicRelay.Windows.Presentation.Tests;

public sealed class PublisherUiStateResolverTests
{
    private static PublisherSnapshot SignedIn => new()
    {
        IsAuthenticated = true,
        SignalingState = SignalingConnectionState.Disconnected,
        AudioState = AudioCaptureState.Stopped,
    };

    private static PublisherSnapshot InSession => SignedIn with
    {
        SessionId = Guid.NewGuid(),
        SessionCode = "ABC123",
        SignalingState = SignalingConnectionState.Connected,
    };

    private static WebRtcPublisherDiagnostics Viewers(params PeerConnectionDiagnostics[] viewers) =>
        new(viewers.Length, viewers);

    private static PeerConnectionDiagnostics Viewer(PeerConnectionState state, string? pair = null) =>
        new("viewer-1", state, pair);

    [Fact]
    public void No_snapshot_is_logged_out()
    {
        Assert.Equal(PublisherUiState.LoggedOut, PublisherUiStateResolver.Resolve(null));
    }

    [Fact]
    public void Unauthenticated_is_logged_out_and_busy_means_authenticating()
    {
        var snapshot = new PublisherSnapshot();
        Assert.Equal(PublisherUiState.LoggedOut, PublisherUiStateResolver.Resolve(snapshot));
        Assert.Equal(
            PublisherUiState.Authenticating,
            PublisherUiStateResolver.Resolve(snapshot with { IsBusy = true }));
    }

    [Fact]
    public void Signed_in_without_session_is_idle()
    {
        Assert.Equal(PublisherUiState.Idle, PublisherUiStateResolver.Resolve(SignedIn));
    }

    [Fact]
    public void Busy_without_session_is_creating_session()
    {
        Assert.Equal(
            PublisherUiState.CreatingSession,
            PublisherUiStateResolver.Resolve(SignedIn with { IsBusy = true }, previous: PublisherUiState.Idle));
    }

    [Fact]
    public void Session_with_signaling_not_connected_is_connecting_signaling()
    {
        var snapshot = InSession with { SignalingState = SignalingConnectionState.Connecting };
        Assert.Equal(PublisherUiState.ConnectingSignaling, PublisherUiStateResolver.Resolve(snapshot));
    }

    [Fact]
    public void Session_connected_without_viewers_is_waiting_viewer()
    {
        Assert.Equal(PublisherUiState.WaitingViewer, PublisherUiStateResolver.Resolve(InSession));
    }

    [Fact]
    public void Negotiating_viewer_is_connecting_webrtc()
    {
        var state = PublisherUiStateResolver.Resolve(InSession, Viewers(Viewer(PeerConnectionState.Connecting)));
        Assert.Equal(PublisherUiState.ConnectingWebRtc, state);
    }

    [Fact]
    public void Capturing_with_connected_viewer_streams_direct_by_default()
    {
        var snapshot = InSession with { AudioState = AudioCaptureState.Capturing };
        var state = PublisherUiStateResolver.Resolve(
            snapshot, Viewers(Viewer(PeerConnectionState.Connected, "host <-> srflx")));
        Assert.Equal(PublisherUiState.StreamingDirect, state);
    }

    [Fact]
    public void Relay_candidate_pair_streams_relay()
    {
        var snapshot = InSession with { AudioState = AudioCaptureState.Capturing };
        var state = PublisherUiStateResolver.Resolve(
            snapshot, Viewers(Viewer(PeerConnectionState.Connected, "relay <-> host")));
        Assert.Equal(PublisherUiState.StreamingRelay, state);
    }

    [Fact]
    public void Forced_relay_without_reported_pair_streams_relay()
    {
        var snapshot = InSession with { AudioState = AudioCaptureState.Capturing };
        var state = PublisherUiStateResolver.Resolve(
            snapshot, Viewers(Viewer(PeerConnectionState.Connected)), forceRelay: true);
        Assert.Equal(PublisherUiState.StreamingRelay, state);
    }

    [Fact]
    public void Connected_viewer_without_capture_is_waiting_viewer()
    {
        var state = PublisherUiStateResolver.Resolve(InSession, Viewers(Viewer(PeerConnectionState.Connected)));
        Assert.Equal(PublisherUiState.WaitingViewer, state);
    }

    [Theory]
    [InlineData(SignalingConnectionState.Reconnecting, AudioCaptureState.Capturing)]
    [InlineData(SignalingConnectionState.Connected, AudioCaptureState.Recovering)]
    public void Signaling_or_capture_recovery_is_reconnecting(SignalingConnectionState signaling, AudioCaptureState audio)
    {
        var snapshot = InSession with { SignalingState = signaling, AudioState = audio };
        Assert.Equal(PublisherUiState.Reconnecting, PublisherUiStateResolver.Resolve(snapshot));
    }

    [Fact]
    public void Disconnected_viewer_without_any_live_viewer_is_reconnecting()
    {
        var state = PublisherUiStateResolver.Resolve(InSession, Viewers(Viewer(PeerConnectionState.Disconnected)));
        Assert.Equal(PublisherUiState.Reconnecting, state);
    }

    [Theory]
    [InlineData(SignalingConnectionState.Faulted, AudioCaptureState.Capturing)]
    [InlineData(SignalingConnectionState.Connected, AudioCaptureState.Faulted)]
    public void Signaling_or_capture_fault_is_faulted(SignalingConnectionState signaling, AudioCaptureState audio)
    {
        var snapshot = InSession with { SignalingState = signaling, AudioState = audio };
        Assert.Equal(PublisherUiState.Faulted, PublisherUiStateResolver.Resolve(snapshot));
    }

    [Fact]
    public void Failed_viewer_without_any_live_viewer_is_faulted()
    {
        var state = PublisherUiStateResolver.Resolve(InSession, Viewers(Viewer(PeerConnectionState.Failed)));
        Assert.Equal(PublisherUiState.Faulted, state);
    }

    [Fact]
    public void Failed_viewer_does_not_fault_while_another_viewer_is_still_negotiating()
    {
        var diagnostics = new WebRtcPublisherDiagnostics(2, [
            Viewer(PeerConnectionState.Failed),
            new PeerConnectionDiagnostics("viewer-2", PeerConnectionState.Connecting),
        ]);
        Assert.Equal(PublisherUiState.ConnectingWebRtc, PublisherUiStateResolver.Resolve(InSession, diagnostics));
    }

    [Fact]
    public void Failed_viewer_with_another_viewer_recovering_is_reconnecting()
    {
        var diagnostics = new WebRtcPublisherDiagnostics(2, [
            Viewer(PeerConnectionState.Failed),
            new PeerConnectionDiagnostics("viewer-2", PeerConnectionState.Disconnected),
        ]);
        Assert.Equal(PublisherUiState.Reconnecting, PublisherUiStateResolver.Resolve(InSession, diagnostics));
    }

    [Fact]
    public void Failed_viewer_faults_when_the_only_other_viewer_is_closed()
    {
        var diagnostics = new WebRtcPublisherDiagnostics(2, [
            Viewer(PeerConnectionState.Failed),
            new PeerConnectionDiagnostics("viewer-2", PeerConnectionState.Closed),
        ]);
        Assert.Equal(PublisherUiState.Faulted, PublisherUiStateResolver.Resolve(InSession, diagnostics));
    }

    [Fact]
    public void One_live_viewer_outweighs_another_failed_viewer()
    {
        var snapshot = InSession with { AudioState = AudioCaptureState.Capturing };
        var diagnostics = new WebRtcPublisherDiagnostics(2, [
            Viewer(PeerConnectionState.Failed),
            new PeerConnectionDiagnostics("viewer-2", PeerConnectionState.Connected, "host <-> host"),
        ]);
        Assert.Equal(PublisherUiState.StreamingDirect, PublisherUiStateResolver.Resolve(snapshot, diagnostics));
    }

    [Fact]
    public void Leaving_a_session_state_without_a_session_is_ended_then_stays_ended()
    {
        // Teardown in flight: session already cleared while the operation is busy.
        var tearingDown = SignedIn with { IsBusy = true };
        var ended = PublisherUiStateResolver.Resolve(tearingDown, previous: PublisherUiState.StreamingDirect);
        Assert.Equal(PublisherUiState.Ended, ended);

        // Settled: still Ended (not Idle) so the shell can present the summary state.
        Assert.Equal(
            PublisherUiState.Ended,
            PublisherUiStateResolver.Resolve(SignedIn, previous: PublisherUiState.Ended));
    }

    [Fact]
    public void Creating_a_new_session_from_ended_is_creating_session()
    {
        var creating = PublisherUiStateResolver.Resolve(
            SignedIn with { IsBusy = true }, previous: PublisherUiState.Ended);
        Assert.Equal(PublisherUiState.CreatingSession, creating);
    }

    [Fact]
    public void Capabilities_are_defined_for_every_state()
    {
        foreach (var state in Enum.GetValues<PublisherUiState>())
        {
            Assert.NotNull(PublisherUiCapabilities.For(state));
        }
    }

    [Fact]
    public void Streaming_states_allow_stop_and_show_live_metrics()
    {
        foreach (var state in new[] { PublisherUiState.StreamingDirect, PublisherUiState.StreamingRelay })
        {
            var capabilities = PublisherUiCapabilities.For(state);
            Assert.True(capabilities.CanStopAudio);
            Assert.True(capabilities.CanEndSession);
            Assert.True(capabilities.ShowsLiveMetrics);
            Assert.True(capabilities.KeepsRunningInTray);
            Assert.False(capabilities.CanCreateSession);
        }
    }

    [Fact]
    public void Faulted_and_reconnecting_offer_retry()
    {
        Assert.True(PublisherUiCapabilities.For(PublisherUiState.Faulted).CanRetry);
        Assert.True(PublisherUiCapabilities.For(PublisherUiState.Reconnecting).CanRetry);
        Assert.False(PublisherUiCapabilities.For(PublisherUiState.StreamingDirect).CanRetry);
    }

    [Fact]
    public void Logged_out_only_allows_authentication()
    {
        var capabilities = PublisherUiCapabilities.For(PublisherUiState.LoggedOut);
        Assert.True(capabilities.CanAuthenticate);
        Assert.False(capabilities.CanCreateSession);
        Assert.False(capabilities.CanEndSession);
        Assert.False(capabilities.KeepsRunningInTray);
    }

    [Fact]
    public void Idle_and_ended_allow_creating_a_session()
    {
        Assert.True(PublisherUiCapabilities.For(PublisherUiState.Idle).CanCreateSession);
        Assert.True(PublisherUiCapabilities.For(PublisherUiState.Ended).CanCreateSession);
    }

    [Fact]
    public void An_offline_machine_reads_as_waiting_for_network_not_reconnecting()
    {
        var snapshot = InSession with { SignalingState = SignalingConnectionState.WaitingForNetwork };

        // "Reconnecting" tells the user something is being retried against the backend, and
        // sends them looking there for a problem that is on their own desk. Nothing is being
        // retried while the machine has no route at all.
        Assert.Equal(
            PublisherUiState.WaitingForNetwork,
            PublisherUiStateResolver.Resolve(snapshot, Viewers(Viewer(PeerConnectionState.Connected))));
    }

    [Fact]
    public void Waiting_for_network_hides_live_metrics_but_keeps_retry_and_the_session()
    {
        var capabilities = PublisherUiCapabilities.For(PublisherUiState.WaitingForNetwork);

        // The last RTT/bitrate readings were taken over a route that no longer exists, so
        // showing them as live is the same lie as showing "Streaming" with no media.
        Assert.False(capabilities.ShowsLiveMetrics);
        Assert.True(capabilities.CanRetry);
        Assert.True(capabilities.CanEndSession);
        Assert.True(capabilities.KeepsRunningInTray);
    }

    [Fact]
    public void Recovering_from_an_outage_does_not_read_as_a_session_that_ended()
    {
        var offline = InSession with { SignalingState = SignalingConnectionState.WaitingForNetwork };
        var previous = PublisherUiStateResolver.Resolve(offline);

        // The session id survives the outage, but if WaitingForNetwork were not a session
        // state, the moment it did drop the shell would report "Idle" instead of "Ended".
        Assert.Equal(
            PublisherUiState.Ended,
            PublisherUiStateResolver.Resolve(SignedIn, previous: previous));
    }
}
