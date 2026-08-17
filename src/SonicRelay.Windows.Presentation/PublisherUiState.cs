using SonicRelay.Windows.Audio;
using SonicRelay.Windows.Signaling;
using SonicRelay.Windows.WebRtc;

namespace SonicRelay.Windows.Presentation;

/// <summary>
/// The canonical interface states of the publisher shell (issue #32). Every desktop
/// shell (WinUI today, the shared Avalonia shell later) renders from this state
/// instead of re-deriving conditions from raw snapshots, so Windows and Linux
/// present identical behaviour. The state is resolved by
/// <see cref="PublisherUiStateResolver"/> and each state's allowed actions come from
/// <see cref="PublisherUiCapabilities.For"/>.
/// </summary>
public enum PublisherUiState
{
    LoggedOut,
    Authenticating,
    /// <summary>Signed in with no publisher session.</summary>
    Idle,
    CreatingSession,
    /// <summary>Session live and signaling connected; no viewer streaming yet.</summary>
    WaitingViewer,
    ConnectingSignaling,
    ConnectingWebRtc,
    StreamingDirect,
    StreamingRelay,
    /// <summary>Signaling, capture, or a viewer connection is re-establishing itself.</summary>
    Reconnecting,
    /// <summary>
    /// The machine has no usable network interface, so recovery is parked rather than
    /// retrying. Deliberately distinct from <see cref="Reconnecting"/>: no attempt budget is
    /// being spent, and the cause is local rather than anything about the backend.
    /// </summary>
    WaitingForNetwork,
    /// <summary>Signaling, capture, or every viewer connection failed; retry is offered.</summary>
    Faulted,
    /// <summary>The session was ended; a new one can be created.</summary>
    Ended
}

/// <summary>
/// Pure projection of the publisher snapshot (plus WebRTC diagnostics) into a
/// <see cref="PublisherUiState"/>. Holds no UI or platform types so it is fully
/// unit-testable and shared across shells.
/// </summary>
public static class PublisherUiStateResolver
{
    /// <summary>
    /// Resolves the interface state. <paramref name="previous"/> is the last state
    /// this resolver returned for the same surface; it only disambiguates the
    /// transitions a single snapshot cannot express (session ended vs never started,
    /// and creating a new session from <see cref="PublisherUiState.Ended"/>).
    /// </summary>
    public static PublisherUiState Resolve(
        PublisherSnapshot? snapshot,
        WebRtcPublisherDiagnostics? webrtc = null,
        bool forceRelay = false,
        PublisherUiState? previous = null)
    {
        if (snapshot is null) return PublisherUiState.LoggedOut;
        if (!snapshot.IsAuthenticated)
            return snapshot.IsBusy ? PublisherUiState.Authenticating : PublisherUiState.LoggedOut;
        if (snapshot.SessionId is null) return ResolveWithoutSession(snapshot, previous);

        var viewers = webrtc?.Viewers ?? [];
        var anyViewerConnected = viewers.Any(viewer => viewer.State == PeerConnectionState.Connected);
        // A failed peer only faults the viewer side once no other peer can still
        // become (or come back to) a live connection — a viewer that failed ICE must
        // not surface fault/retry affordances while another is still negotiating.
        var anyViewerRecoverable = viewers.Any(viewer => viewer.State is
            PeerConnectionState.Connected
            or PeerConnectionState.New
            or PeerConnectionState.Connecting
            or PeerConnectionState.Disconnected);

        if (snapshot.SignalingState == SignalingConnectionState.Faulted
            || snapshot.AudioState == AudioCaptureState.Faulted
            || (!anyViewerRecoverable && viewers.Any(viewer => viewer.State == PeerConnectionState.Failed)))
        {
            return PublisherUiState.Faulted;
        }

        // Checked before Reconnecting: an offline machine is a more specific — and more
        // actionable — explanation than "something is being retried".
        if (snapshot.SignalingState == SignalingConnectionState.WaitingForNetwork)
            return PublisherUiState.WaitingForNetwork;

        if (snapshot.SignalingState == SignalingConnectionState.Reconnecting
            || snapshot.AudioState == AudioCaptureState.Recovering
            || (!anyViewerConnected && viewers.Any(viewer => viewer.State == PeerConnectionState.Disconnected)))
        {
            return PublisherUiState.Reconnecting;
        }

        if (snapshot.SignalingState != SignalingConnectionState.Connected)
            return PublisherUiState.ConnectingSignaling;

        if (anyViewerConnected && snapshot.AudioState == AudioCaptureState.Capturing)
        {
            return IsRelay(viewers, forceRelay)
                ? PublisherUiState.StreamingRelay
                : PublisherUiState.StreamingDirect;
        }

        if (viewers.Any(viewer => viewer.State is PeerConnectionState.New or PeerConnectionState.Connecting))
            return PublisherUiState.ConnectingWebRtc;

        // Session up and signaling connected: either no viewer yet, or a viewer is
        // attached but audio has not started — both read as "waiting" to the user.
        return PublisherUiState.WaitingViewer;
    }

    private static PublisherUiState ResolveWithoutSession(PublisherSnapshot snapshot, PublisherUiState? previous)
    {
        // Leaving a live session state with no session left means it just ended,
        // whether the teardown operation is still in flight or already finished.
        if (previous is { } last && IsSessionState(last)) return PublisherUiState.Ended;
        if (snapshot.IsBusy) return PublisherUiState.CreatingSession;
        return previous == PublisherUiState.Ended ? PublisherUiState.Ended : PublisherUiState.Idle;
    }

    private static bool IsSessionState(PublisherUiState state) => state is
        PublisherUiState.WaitingViewer
        or PublisherUiState.ConnectingSignaling
        or PublisherUiState.ConnectingWebRtc
        or PublisherUiState.StreamingDirect
        or PublisherUiState.StreamingRelay
        or PublisherUiState.Reconnecting
        or PublisherUiState.WaitingForNetwork
        or PublisherUiState.Faulted;

    private static bool IsRelay(IReadOnlyList<PeerConnectionDiagnostics> viewers, bool forceRelay)
    {
        var selected = viewers.FirstOrDefault(viewer =>
            viewer.State == PeerConnectionState.Connected && viewer.SelectedCandidatePair is not null)
            ?.SelectedCandidatePair;
        if (!string.IsNullOrWhiteSpace(selected))
            return selected.Contains("relay", StringComparison.OrdinalIgnoreCase);
        // No negotiated pair reported yet: forcing relay guarantees relay once connected.
        return forceRelay;
    }
}

/// <summary>
/// What each <see cref="PublisherUiState"/> allows, as required by issue #32: the
/// actions a shell may offer, whether retry applies, whether live session metrics
/// are meaningful, and whether closing the window should keep the app in the tray.
/// These are design-level rules; shells still combine them with transient snapshot
/// facts (e.g. <see cref="PublisherSnapshot.IsBusy"/>) before enabling a control.
/// </summary>
public sealed record PublisherUiCapabilities(
    bool CanAuthenticate,
    bool CanLogout,
    bool CanCreateSession,
    bool CanStartAudio,
    bool CanStopAudio,
    bool CanEndSession,
    bool CanRetry,
    bool ShowsLiveMetrics,
    bool KeepsRunningInTray)
{
    public static PublisherUiCapabilities For(PublisherUiState state) => state switch
    {
        PublisherUiState.LoggedOut => new(
            CanAuthenticate: true, CanLogout: false, CanCreateSession: false, CanStartAudio: false,
            CanStopAudio: false, CanEndSession: false, CanRetry: false, ShowsLiveMetrics: false,
            KeepsRunningInTray: false),
        PublisherUiState.Authenticating => new(
            CanAuthenticate: false, CanLogout: false, CanCreateSession: false, CanStartAudio: false,
            CanStopAudio: false, CanEndSession: false, CanRetry: false, ShowsLiveMetrics: false,
            KeepsRunningInTray: false),
        PublisherUiState.Idle => new(
            CanAuthenticate: false, CanLogout: true, CanCreateSession: true, CanStartAudio: false,
            CanStopAudio: false, CanEndSession: false, CanRetry: false, ShowsLiveMetrics: false,
            KeepsRunningInTray: false),
        PublisherUiState.CreatingSession => new(
            CanAuthenticate: false, CanLogout: false, CanCreateSession: false, CanStartAudio: false,
            CanStopAudio: false, CanEndSession: false, CanRetry: false, ShowsLiveMetrics: false,
            KeepsRunningInTray: true),
        PublisherUiState.WaitingViewer => new(
            CanAuthenticate: false, CanLogout: true, CanCreateSession: false, CanStartAudio: true,
            CanStopAudio: false, CanEndSession: true, CanRetry: false, ShowsLiveMetrics: false,
            KeepsRunningInTray: true),
        PublisherUiState.ConnectingSignaling => new(
            CanAuthenticate: false, CanLogout: false, CanCreateSession: false, CanStartAudio: false,
            CanStopAudio: false, CanEndSession: true, CanRetry: true, ShowsLiveMetrics: false,
            KeepsRunningInTray: true),
        PublisherUiState.ConnectingWebRtc => new(
            CanAuthenticate: false, CanLogout: false, CanCreateSession: false, CanStartAudio: true,
            CanStopAudio: false, CanEndSession: true, CanRetry: false, ShowsLiveMetrics: false,
            KeepsRunningInTray: true),
        PublisherUiState.StreamingDirect or PublisherUiState.StreamingRelay => new(
            CanAuthenticate: false, CanLogout: false, CanCreateSession: false, CanStartAudio: false,
            CanStopAudio: true, CanEndSession: true, CanRetry: false, ShowsLiveMetrics: true,
            KeepsRunningInTray: true),
        PublisherUiState.Reconnecting => new(
            CanAuthenticate: false, CanLogout: false, CanCreateSession: false, CanStartAudio: false,
            CanStopAudio: true, CanEndSession: true, CanRetry: true, ShowsLiveMetrics: true,
            KeepsRunningInTray: true),
        // Live metrics are off here on purpose: the last readings were taken over a route
        // that no longer exists, and presenting them as current is the same lie as showing
        // "Streaming" with no media flowing. Retry stays available — a manual retry runs the
        // same recovery the automatic one does, just from a newer generation.
        PublisherUiState.WaitingForNetwork => new(
            CanAuthenticate: false, CanLogout: false, CanCreateSession: false, CanStartAudio: false,
            CanStopAudio: true, CanEndSession: true, CanRetry: true, ShowsLiveMetrics: false,
            KeepsRunningInTray: true),
        PublisherUiState.Faulted => new(
            CanAuthenticate: false, CanLogout: false, CanCreateSession: false, CanStartAudio: false,
            CanStopAudio: true, CanEndSession: true, CanRetry: true, ShowsLiveMetrics: false,
            KeepsRunningInTray: true),
        PublisherUiState.Ended => new(
            CanAuthenticate: false, CanLogout: true, CanCreateSession: true, CanStartAudio: false,
            CanStopAudio: false, CanEndSession: false, CanRetry: false, ShowsLiveMetrics: false,
            KeepsRunningInTray: false),
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown publisher UI state.")
    };
}
