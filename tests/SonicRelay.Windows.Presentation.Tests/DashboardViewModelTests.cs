using SonicRelay.Windows.Audio;
using SonicRelay.Windows.Presentation;
using SonicRelay.Windows.Signaling;
using SonicRelay.Windows.WebRtc;

namespace SonicRelay.Windows.Presentation.Tests;

public sealed class DashboardViewModelTests
{
    private static PublisherSnapshot Streaming => new()
    {
        IsAuthenticated = true,
        SessionId = Guid.NewGuid(),
        SessionCode = "ABC123",
        ViewerCount = 2,
        SignalingState = SignalingConnectionState.Connected,
        AudioState = AudioCaptureState.Capturing,
        AudioDiagnostics = new AudioCaptureDiagnostics(
            AudioCaptureState.Capturing, null, null, new AudioLevelSnapshot(0.8f, 0.5f), 0, 0),
    };

    private static WebRtcPublisherDiagnostics Viewers(params PeerConnectionDiagnostics[] viewers) =>
        new(viewers.Length, viewers);

    [Fact]
    public void Null_snapshot_yields_safe_unknown_defaults()
    {
        var vm = DashboardViewModel.From(null, null, forceRelay: false);

        Assert.Equal("Idle", vm.SessionStatusText);
        Assert.Equal(DashboardViewModel.Unknown, vm.SessionCodeText);
        Assert.Equal(DashboardViewModel.Unknown, vm.LatencyText);
        Assert.Equal(DashboardViewModel.Unknown, vm.ConnectionModeText);
    }

    [Fact]
    public void Streaming_snapshot_maps_status_and_audio_level()
    {
        var vm = DashboardViewModel.From(Streaming, null, forceRelay: false);

        Assert.Equal("Streaming", vm.SessionStatusText);
        Assert.Equal(DashboardBadge.Success, vm.SessionStatusBadge);
        Assert.Equal("Connected", vm.SignalingStatusText);
        Assert.Equal("ABC123", vm.SessionCodeText);
        Assert.Equal(2, vm.ViewerCount);
        Assert.True(vm.IsCapturing);
        Assert.Equal(0.8, vm.AudioPeak, 3);
        Assert.Equal(0.5, vm.AudioRms, 3);
    }

    [Fact]
    public void Waiting_when_session_exists_but_not_capturing()
    {
        var waiting = Streaming with { AudioState = AudioCaptureState.Stopped, AudioDiagnostics = null };

        var vm = DashboardViewModel.From(waiting, null, forceRelay: false);

        Assert.Equal("Waiting", vm.SessionStatusText);
        Assert.Equal(DashboardBadge.Warning, vm.SessionStatusBadge);
    }

    [Fact]
    public void Error_message_takes_priority_in_session_status()
    {
        var errored = Streaming with { ErrorMessage = "boom" };

        var vm = DashboardViewModel.From(errored, null, forceRelay: false);

        Assert.Equal("Error", vm.SessionStatusText);
        Assert.Equal(DashboardBadge.Danger, vm.SessionStatusBadge);
    }

    [Theory]
    [InlineData(SignalingConnectionState.Connected, "Connected", DashboardBadge.Success)]
    [InlineData(SignalingConnectionState.Connecting, "Connecting", DashboardBadge.Warning)]
    [InlineData(SignalingConnectionState.Reconnecting, "Reconnecting", DashboardBadge.Warning)]
    [InlineData(SignalingConnectionState.Faulted, "Failed", DashboardBadge.Danger)]
    [InlineData(SignalingConnectionState.Disconnected, "Disconnected", DashboardBadge.Neutral)]
    public void Signaling_status_maps_each_state(SignalingConnectionState state, string text, DashboardBadge badge)
    {
        var vm = DashboardViewModel.From(Streaming with { SignalingState = state }, null, forceRelay: false);

        Assert.Equal(text, vm.SignalingStatusText);
        Assert.Equal(badge, vm.SignalingStatusBadge);
    }

    [Fact]
    public void WebRtc_status_prefers_a_connected_viewer()
    {
        var diag = Viewers(
            new PeerConnectionDiagnostics("v1", PeerConnectionState.Failed),
            new PeerConnectionDiagnostics("v2", PeerConnectionState.Connected));

        var vm = DashboardViewModel.From(Streaming, diag, forceRelay: false);

        Assert.Equal("Connected", vm.WebRtcStatusText);
        Assert.Equal(DashboardBadge.Success, vm.WebRtcStatusBadge);
    }

    [Fact]
    public void WebRtc_status_is_failed_when_no_viewer_connected_but_one_failed()
    {
        var diag = Viewers(new PeerConnectionDiagnostics("v1", PeerConnectionState.Failed));

        var vm = DashboardViewModel.From(Streaming, diag, forceRelay: false);

        Assert.Equal("Failed", vm.WebRtcStatusText);
        Assert.Equal(DashboardBadge.Danger, vm.WebRtcStatusBadge);
    }

    [Fact]
    public void WebRtc_status_is_idle_without_viewers()
    {
        var vm = DashboardViewModel.From(Streaming, Viewers(), forceRelay: false);

        Assert.Equal("Idle", vm.WebRtcStatusText);
    }

    [Fact]
    public void Latency_comes_from_the_estimated_round_trip_time()
    {
        var diag = Viewers(new PeerConnectionDiagnostics(
            "v1", PeerConnectionState.Connected, "host->host", TimeSpan.FromMilliseconds(42)));

        var vm = DashboardViewModel.From(Streaming, diag, forceRelay: false);

        Assert.Equal("42 ms", vm.LatencyText);
    }

    [Fact]
    public void Connection_mode_is_relay_when_selected_pair_is_relay()
    {
        var diag = Viewers(new PeerConnectionDiagnostics(
            "v1", PeerConnectionState.Connected, "candidate: typ relay", TimeSpan.FromMilliseconds(10)));

        var vm = DashboardViewModel.From(Streaming, diag, forceRelay: false);

        Assert.Equal("Relay", vm.ConnectionModeText);
    }

    [Fact]
    public void Connection_mode_is_direct_when_a_non_relay_pair_is_selected()
    {
        var diag = Viewers(new PeerConnectionDiagnostics(
            "v1", PeerConnectionState.Connected, "candidate: typ srflx", TimeSpan.FromMilliseconds(10)));

        var vm = DashboardViewModel.From(Streaming, diag, forceRelay: true);

        Assert.Equal("Direct", vm.ConnectionModeText);
    }

    [Fact]
    public void Connection_mode_falls_back_to_relay_when_forced_without_a_pair()
    {
        var vm = DashboardViewModel.From(Streaming, Viewers(), forceRelay: true);

        Assert.Equal("Relay", vm.ConnectionModeText);
    }

    [Fact]
    public void Unmeasured_metrics_are_shown_as_unknown()
    {
        var vm = DashboardViewModel.From(Streaming, null, forceRelay: false);

        Assert.Equal(DashboardViewModel.Unknown, vm.JitterText);
        Assert.Equal(DashboardViewModel.Unknown, vm.PacketLossText);
        Assert.Equal(DashboardViewModel.Unknown, vm.BitrateText);
    }

    [Fact]
    public void Jitter_and_loss_come_from_the_connected_viewer_reception_report()
    {
        var diag = Viewers(new PeerConnectionDiagnostics(
            "v1", PeerConnectionState.Connected, "host->host", TimeSpan.FromMilliseconds(20),
            AudioReceive: new AudioReceptionDiagnostics(TimeSpan.FromMilliseconds(6), 1.5, 12)));

        var vm = DashboardViewModel.From(Streaming, diag, forceRelay: false);

        Assert.Equal("6 ms", vm.JitterText);
        Assert.Equal("1.5 %", vm.PacketLossText);
    }

    [Fact]
    public void Bitrate_comes_from_the_negotiated_opus_send_bitrate()
    {
        var diag = Viewers(new PeerConnectionDiagnostics(
            "v1", PeerConnectionState.Connected, "host->host", TimeSpan.FromMilliseconds(20),
            AudioSend: new AudioSendDiagnostics(0, 0, 0, 0, TimeSpan.Zero, 20, 96, 2, "music-96", true, 1)));

        var vm = DashboardViewModel.From(Streaming, diag, forceRelay: false);

        Assert.Equal("96 kbps", vm.BitrateText);
    }

    [Fact]
    public void Reception_metrics_stay_unknown_until_a_report_arrives()
    {
        // Viewer connected but no RTCP receiver report yet: jitter/loss unknown, but the
        // negotiated send bitrate is already a real value.
        var diag = Viewers(new PeerConnectionDiagnostics(
            "v1", PeerConnectionState.Connected, "host->host", null,
            AudioSend: new AudioSendDiagnostics(0, 0, 0, 0, TimeSpan.Zero, 20, 64, 1, "voice-64", true, 1)));

        var vm = DashboardViewModel.From(Streaming, diag, forceRelay: false);

        Assert.Equal(DashboardViewModel.Unknown, vm.JitterText);
        Assert.Equal(DashboardViewModel.Unknown, vm.PacketLossText);
        Assert.Equal("64 kbps", vm.BitrateText);
    }
}
