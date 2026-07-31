using SonicRelay.Windows.Audio;
using SonicRelay.Windows.Desktop.ViewModels;
using SonicRelay.Windows.Presentation;
using SonicRelay.Windows.Signaling;
using SonicRelay.Windows.WebRtc;

namespace SonicRelay.Windows.Desktop.Tests;

/// <summary>
/// The shell view model must be a faithful, UI-free projection of the shared presentation
/// layer — no fabricated metrics (issue #32's "real metrics, no permanent mocks" rule) and
/// the canonical UI state resolved from the snapshot.
/// </summary>
public sealed class DashboardShellViewModelTests
{
    private static PublisherSnapshot StreamingSnapshot() => new()
    {
        IsAuthenticated = true,
        UserEmail = "vitor.hugo@sonicrelay.app",
        DeviceName = "STUDIO-PC",
        SessionId = Guid.NewGuid(),
        SessionCode = "K7DRRP",
        ViewerCount = 1,
        SignalingState = SignalingConnectionState.Connected,
        AudioState = AudioCaptureState.Capturing,
    };

    private static WebRtcPublisherDiagnostics ConnectedViewer() => new(
        1,
        [new PeerConnectionDiagnostics("v1", PeerConnectionState.Connected, "host:host", TimeSpan.FromMilliseconds(42))]);

    [Fact]
    public void Update_projects_streaming_state_from_snapshot()
    {
        var vm = new DashboardShellViewModel();

        vm.Update(StreamingSnapshot(), ConnectedViewer(), forceRelay: false);

        Assert.Equal(PublisherUiState.StreamingDirect, vm.UiState);
        Assert.True(vm.IsStreaming);
        Assert.Equal("K7DRRP", vm.SessionCodeText);
        Assert.True(vm.HasSessionCode);
        Assert.Equal("1", vm.ViewerCountText);
        Assert.Equal("Direct", vm.ConnectionModeText);
        Assert.Equal("42 ms", vm.LatencyText);
        Assert.True(vm.Capabilities.ShowsLiveMetrics);
    }

    [Fact]
    public void Unplumbed_metrics_render_as_unknown_never_mocked()
    {
        var vm = new DashboardShellViewModel();

        vm.Update(StreamingSnapshot(), ConnectedViewer(), forceRelay: false);

        // Jitter / packet loss / bitrate are not yet wired from WebRTC getStats; they must
        // surface as the unknown marker, not invented numbers.
        Assert.Equal(DashboardViewModel.Unknown, vm.JitterText);
        Assert.Equal(DashboardViewModel.Unknown, vm.PacketLossText);
        Assert.Equal(DashboardViewModel.Unknown, vm.BitrateText);
    }

    [Fact]
    public void Account_fields_are_derived_from_the_snapshot()
    {
        var vm = new DashboardShellViewModel();

        vm.Update(StreamingSnapshot(), ConnectedViewer(), forceRelay: false);

        Assert.Equal("vitor.hugo@sonicrelay.app", vm.AccountLabel);
        Assert.Equal("VH", vm.AccountInitials);
    }

    [Fact]
    public void No_snapshot_is_logged_out_with_actions_disabled()
    {
        var vm = new DashboardShellViewModel();

        vm.Update(null, null, forceRelay: false);

        Assert.Equal(PublisherUiState.LoggedOut, vm.UiState);
        Assert.False(vm.HasSessionCode);
        Assert.False(vm.Capabilities.CanEndSession);
    }

    [Fact]
    public void Relay_candidate_pair_resolves_streaming_relay()
    {
        var vm = new DashboardShellViewModel();
        var relay = new WebRtcPublisherDiagnostics(
            1,
            [new PeerConnectionDiagnostics("v1", PeerConnectionState.Connected, "relay:host")]);

        vm.Update(StreamingSnapshot(), relay, forceRelay: false);

        Assert.Equal(PublisherUiState.StreamingRelay, vm.UiState);
        Assert.Equal("Relay", vm.ConnectionModeText);
    }
}
