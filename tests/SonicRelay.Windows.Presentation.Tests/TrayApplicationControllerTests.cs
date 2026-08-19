using SonicRelay.Windows.Audio;
using SonicRelay.Windows.Presentation;
using SonicRelay.Windows.Signaling;

namespace SonicRelay.Windows.Presentation.Tests;

public sealed class TrayApplicationControllerTests
{
    private static TrayApplicationController Controller(bool keepInTray) => new(() => keepInTray);

    private static PublisherSnapshot DeviceReady => new()
    {
        IsAuthenticated = true,
        DeviceId = Guid.Parse("00000000-0000-0000-0000-000000000601"),
        SignalingState = SignalingConnectionState.Connected,
        AudioState = AudioCaptureState.Stopped,
    };

    private static PublisherSnapshot Streaming => DeviceReady with
    {
        SessionId = Guid.NewGuid(),
        SessionCode = "ABC123",
        AudioState = AudioCaptureState.Capturing,
        ViewerCount = 1,
    };

    [Fact]
    public void Close_hides_when_keep_in_tray_is_on()
    {
        Assert.Equal(TrayCloseDecision.Hide, Controller(true).DecideOnClose(null));
    }

    [Fact]
    public void Close_quits_when_off_and_no_stream()
    {
        Assert.Equal(TrayCloseDecision.Quit, Controller(false).DecideOnClose(DeviceReady));
    }

    [Fact]
    public void Close_hides_during_an_active_stream_even_when_off()
    {
        Assert.Equal(TrayCloseDecision.Hide, Controller(false).DecideOnClose(Streaming));
    }

    [Fact]
    public void Minimize_hides_or_minimizes_by_setting()
    {
        Assert.Equal(TrayCloseDecision.Hide, Controller(true).DecideOnMinimize());
        Assert.Equal(TrayCloseDecision.MinimizeNormally, Controller(false).DecideOnMinimize());
    }

    [Fact]
    public void Menu_for_idle_state_offers_open_status_disabled_start_and_quit()
    {
        var menu = Controller(true).BuildMenu(null);

        Assert.Equal(TrayCommand.Open, menu[0].Command);
        Assert.False(menu[1].Enabled); // status header
        Assert.Contains(menu, item => item.Command == TrayCommand.StartStream && !item.Enabled);
        Assert.Equal(TrayCommand.Quit, menu[^1].Command);
        Assert.DoesNotContain(menu, item => item.Command == TrayCommand.CopySessionCode);
    }

    [Fact]
    public void Menu_while_streaming_offers_stop_and_copy_code()
    {
        var menu = Controller(true).BuildMenu(Streaming);

        Assert.Contains(menu, item => item.Command == TrayCommand.StopStream && item.Enabled);
        Assert.Contains(menu, item => item.Command == TrayCommand.CopySessionCode);
        Assert.DoesNotContain(menu, item => item.Command == TrayCommand.StartStream);
    }

    [Fact]
    public void Menu_offers_reconnect_when_disconnected_with_a_session()
    {
        var state = Streaming with { SignalingState = SignalingConnectionState.Disconnected };

        var menu = Controller(true).BuildMenu(state);

        Assert.Contains(menu, item => item.Command == TrayCommand.ReconnectSignaling);
    }

    [Fact]
    public void Menu_hides_reconnect_when_signaling_is_connected()
    {
        Assert.DoesNotContain(Controller(true).BuildMenu(Streaming), item => item.Command == TrayCommand.ReconnectSignaling);
    }

    [Fact]
    public void Menu_offers_create_session_when_idle_with_device_identity()
    {
        // Regression coverage: recovering from a session that ended unexpectedly while the
        // window was hidden to tray used to require reopening the window and clicking through
        // — with no cue at all from the tray itself that anything needed doing. One click here
        // is the difference between "click Create session" and "the app looks frozen, force-quit
        // and reopen it".
        var menu = Controller(true).BuildMenu(DeviceReady);

        Assert.Contains(menu, item => item.Command == TrayCommand.CreateSession && item.Enabled);
    }

    [Fact]
    public void Menu_hides_create_session_once_a_session_exists()
    {
        Assert.DoesNotContain(Controller(true).BuildMenu(Streaming), item => item.Command == TrayCommand.CreateSession);
    }

    [Fact]
    public void Menu_hides_create_session_without_device_identity()
    {
        Assert.DoesNotContain(Controller(true).BuildMenu(null), item => item.Command == TrayCommand.CreateSession);
    }

    [Fact]
    public void Start_stream_is_enabled_only_when_startable()
    {
        var ready = DeviceReady with { SessionId = Guid.NewGuid() };

        var menu = Controller(true).BuildMenu(ready);

        Assert.Contains(menu, item => item.Command == TrayCommand.StartStream && item.Enabled);
    }

    [Fact]
    public void Tooltip_reflects_the_publisher_state()
    {
        Assert.Contains("backend not configured", Controller(true).TooltipFor(null));
        Assert.Contains("device identity unavailable", Controller(true).TooltipFor(new PublisherSnapshot()));
        Assert.Contains("device identity ready", Controller(true).TooltipFor(DeviceReady));
        Assert.DoesNotContain("sign", Controller(true).TooltipFor(DeviceReady), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("streaming", Controller(true).TooltipFor(Streaming));
    }

    [Fact]
    public void DiffNotice_reports_stream_start_and_stop()
    {
        var controller = Controller(true);
        Assert.Equal("Streaming started.", controller.DiffNotice(DeviceReady, Streaming)?.Message);
        Assert.Equal("Streaming stopped.", controller.DiffNotice(Streaming, Streaming with { AudioState = AudioCaptureState.Stopped })?.Message);
    }

    [Fact]
    public void DiffNotice_reports_viewer_changes()
    {
        var controller = Controller(true);
        var two = Streaming with { ViewerCount = 2 };

        Assert.Equal("Viewer connected", controller.DiffNotice(Streaming, two)?.Title);
        Assert.Equal("Viewer disconnected", controller.DiffNotice(two, Streaming)?.Title);
    }

    [Fact]
    public void DiffNotice_is_silent_when_nothing_noteworthy_changed()
    {
        Assert.Null(Controller(true).DiffNotice(Streaming, Streaming));
    }
}
