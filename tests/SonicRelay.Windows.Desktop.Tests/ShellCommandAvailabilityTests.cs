using SonicRelay.Windows.Audio;
using SonicRelay.Windows.Desktop.ViewModels;
using SonicRelay.Windows.Presentation;
using SonicRelay.Windows.Signaling;

namespace SonicRelay.Windows.Desktop.Tests;

/// <summary>
/// The shell's action gating must follow the snapshot's real guards, not the coarser
/// state-derived capabilities (PR #35 review). The canonical failure: audio capturing while the
/// UI state is still WaitingViewer (no viewer yet) — Stop must be enabled and Start disabled.
/// </summary>
public sealed class ShellCommandAvailabilityTests
{
    private static PublisherSnapshot CapturingWithoutViewer => new()
    {
        IsAuthenticated = true,
        DeviceId = Guid.NewGuid(),
        SessionId = Guid.NewGuid(),
        SessionCode = "K7DRRP",
        SignalingState = SignalingConnectionState.Connected,
        AudioState = AudioCaptureState.Capturing,
    };

    [Fact]
    public void Stop_is_enabled_and_start_disabled_while_capturing_before_a_viewer()
    {
        var snapshot = CapturingWithoutViewer;

        // Sanity: the UI-state capability set — what the shell used to trust — is wrong here.
        var state = PublisherUiStateResolver.Resolve(snapshot, webrtc: null);
        var capabilities = PublisherUiCapabilities.For(state);
        Assert.Equal(PublisherUiState.WaitingViewer, state);
        Assert.False(capabilities.CanStopAudio);

        // The snapshot-driven gating the shell now uses is correct.
        Assert.True(ShellCommandAvailability.StopAudio(snapshot, hasWorkflow: true));
        Assert.False(ShellCommandAvailability.StartAudio(snapshot, hasWorkflow: true));
    }

    [Fact]
    public void Start_is_enabled_only_when_stopped_and_signaling_connected()
    {
        var idleAudio = CapturingWithoutViewer with { AudioState = AudioCaptureState.Stopped };

        Assert.True(ShellCommandAvailability.StartAudio(idleAudio, hasWorkflow: true));
        Assert.False(ShellCommandAvailability.StopAudio(idleAudio, hasWorkflow: true));
    }

    [Fact]
    public void Every_action_is_disabled_without_an_attached_workflow()
    {
        var snapshot = CapturingWithoutViewer;
        var capabilities = PublisherUiCapabilities.For(PublisherUiState.WaitingViewer);

        Assert.False(ShellCommandAvailability.CreateSession(snapshot, hasWorkflow: false));
        Assert.False(ShellCommandAvailability.StartAudio(snapshot, hasWorkflow: false));
        Assert.False(ShellCommandAvailability.StopAudio(snapshot, hasWorkflow: false));
        Assert.False(ShellCommandAvailability.EndSession(snapshot, hasWorkflow: false));
        Assert.False(ShellCommandAvailability.Retry(snapshot, capabilities, hasWorkflow: false));
        Assert.False(ShellCommandAvailability.Logout(snapshot, capabilities, hasWorkflow: false));
    }

    [Fact]
    public void Busy_snapshot_blocks_retry_and_logout()
    {
        var busy = CapturingWithoutViewer with { IsBusy = true };
        var capabilities = PublisherUiCapabilities.For(PublisherUiState.Reconnecting);

        Assert.False(ShellCommandAvailability.Retry(busy, capabilities, hasWorkflow: true));
        Assert.False(ShellCommandAvailability.Logout(busy, capabilities, hasWorkflow: true));
    }
}
