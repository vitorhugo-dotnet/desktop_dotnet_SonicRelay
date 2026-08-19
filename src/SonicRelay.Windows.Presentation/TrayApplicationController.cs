using SonicRelay.Windows.Audio;
using SonicRelay.Windows.Signaling;

namespace SonicRelay.Windows.Presentation;

/// <summary>What to do when the user minimizes or closes the main window.</summary>
public enum TrayCloseDecision { Hide, Quit, MinimizeNormally }

/// <summary>A command the tray menu can raise.</summary>
public enum TrayCommand { Open, Status, CreateSession, StartStream, StopStream, CopySessionCode, ReconnectSignaling, Quit }

/// <summary>One entry of the tray context menu.</summary>
public sealed record TrayMenuItem(TrayCommand Command, string Label, bool Enabled = true);

/// <summary>A user-visible background notification (title + body).</summary>
public sealed record TrayNotice(string Title, string Message);

/// <summary>
/// Pure decision core for the tray/background experience: it decides window
/// close/minimize behaviour, builds the tray context menu from the publisher
/// snapshot, and derives tooltip/notification text. Holds no UI or Win32 state so
/// it is fully unit-testable and keeps this logic out of XAML/code-behind.
/// </summary>
public sealed class TrayApplicationController(Func<bool> keepRunningInTray)
{
    private readonly Func<bool> keepRunningInTray = keepRunningInTray;

    /// <summary>A stream is "active" while a publisher session exists.</summary>
    public static bool IsStreamActive(PublisherSnapshot? state) => state?.SessionId is not null;

    public TrayCloseDecision DecideOnClose(PublisherSnapshot? state) =>
        IsStreamActive(state) || keepRunningInTray() ? TrayCloseDecision.Hide : TrayCloseDecision.Quit;

    public TrayCloseDecision DecideOnMinimize() =>
        keepRunningInTray() ? TrayCloseDecision.Hide : TrayCloseDecision.MinimizeNormally;

    public string TooltipFor(PublisherSnapshot? state)
    {
        if (state is null) return "SonicRelay — backend not configured";
        if (!state.HasDeviceIdentity) return "SonicRelay — device identity unavailable";
        if (state.SessionId is null) return "SonicRelay — device identity ready";
        return state.AudioState is AudioCaptureState.Capturing
            ? $"SonicRelay — streaming · {state.ViewerCount} viewer(s)"
            : $"SonicRelay — session {state.SessionCode ?? "ready"}";
    }

    public IReadOnlyList<TrayMenuItem> BuildMenu(PublisherSnapshot? state)
    {
        var items = new List<TrayMenuItem>
        {
            new(TrayCommand.Open, "Open SonicRelay"),
            new(TrayCommand.Status, TooltipFor(state), Enabled: false),
        };

        // Offer a one-click way back to a working session from the tray itself: without this,
        // a session that ends while the window is hidden (minimized to tray) has no recovery
        // path short of reopening the window and clicking through — which, if the user does not
        // notice the session ended, looks indistinguishable from the app being stuck.
        if (state?.CanCreateSession == true)
        {
            items.Add(new TrayMenuItem(TrayCommand.CreateSession, "Create session"));
        }

        if (state?.CanStopAudio == true)
        {
            items.Add(new TrayMenuItem(TrayCommand.StopStream, "Stop stream"));
        }
        else
        {
            items.Add(new TrayMenuItem(TrayCommand.StartStream, "Start stream", Enabled: state?.CanStartAudio == true));
        }

        if (!string.IsNullOrWhiteSpace(state?.SessionCode))
        {
            items.Add(new TrayMenuItem(TrayCommand.CopySessionCode, $"Copy session code ({state!.SessionCode})"));
        }

        if (state?.SessionId is not null && state.SignalingState is not SignalingConnectionState.Connected)
        {
            items.Add(new TrayMenuItem(TrayCommand.ReconnectSignaling, "Reconnect signaling"));
        }

        items.Add(new TrayMenuItem(TrayCommand.Quit, "Quit SonicRelay"));
        return items;
    }

    /// <summary>
    /// A notification to raise for a state transition, or null when nothing
    /// noteworthy changed. Viewer connect/disconnect and stream start/stop are
    /// surfaced; normal reconnect churn is not, so notifications don't spam.
    /// </summary>
    public TrayNotice? DiffNotice(PublisherSnapshot? previous, PublisherSnapshot? next)
    {
        if (next is null) return null;

        var previousStreaming = previous?.AudioState == AudioCaptureState.Capturing;
        var nextStreaming = next.AudioState == AudioCaptureState.Capturing;
        if (!previousStreaming && nextStreaming) return new TrayNotice("SonicRelay", "Streaming started.");
        if (previousStreaming && !nextStreaming) return new TrayNotice("SonicRelay", "Streaming stopped.");

        var previousViewers = previous?.ViewerCount ?? 0;
        if (next.ViewerCount > previousViewers)
            return new TrayNotice("Viewer connected", $"{next.ViewerCount} viewer(s) connected.");
        if (next.ViewerCount < previousViewers && next.SessionId is not null)
            return new TrayNotice("Viewer disconnected", $"{next.ViewerCount} viewer(s) connected.");

        return null;
    }
}
