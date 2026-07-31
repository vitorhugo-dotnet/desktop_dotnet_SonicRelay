using System.Globalization;
using SonicRelay.Windows.Audio;
using SonicRelay.Windows.Signaling;
using SonicRelay.Windows.WebRtc;

namespace SonicRelay.Windows.Presentation;

/// <summary>Semantic status colour for a dashboard badge.</summary>
public enum DashboardBadge { Success, Warning, Danger, Neutral }

/// <summary>
/// Pure projection of the publisher/signaling/WebRTC/audio state into UI-friendly,
/// always-non-null display values for the dashboard (issue #25). Holds no UI types
/// so it is unit-testable and keeps business logic out of XAML. Unknown values are
/// surfaced as <see cref="Unknown"/> ("—"), never null or empty.
/// </summary>
public sealed record DashboardViewModel
{
    public const string Unknown = "—";

    public string SessionStatusText { get; init; } = "Idle";
    public DashboardBadge SessionStatusBadge { get; init; } = DashboardBadge.Neutral;

    public string SignalingStatusText { get; init; } = "Disconnected";
    public DashboardBadge SignalingStatusBadge { get; init; } = DashboardBadge.Neutral;

    public string WebRtcStatusText { get; init; } = "Idle";
    public DashboardBadge WebRtcStatusBadge { get; init; } = DashboardBadge.Neutral;

    public string ConnectionModeText { get; init; } = Unknown;
    public string SessionCodeText { get; init; } = Unknown;
    public int ViewerCount { get; init; }

    public bool IsCapturing { get; init; }
    public double AudioPeak { get; init; }
    public double AudioRms { get; init; }

    public string LatencyText { get; init; } = Unknown;
    public string JitterText { get; init; } = Unknown;
    public string PacketLossText { get; init; } = Unknown;
    public string BitrateText { get; init; } = Unknown;

    public static DashboardViewModel From(
        PublisherSnapshot? snapshot,
        WebRtcPublisherDiagnostics? webrtc,
        bool forceRelay)
    {
        if (snapshot is null) return new DashboardViewModel();

        var (sessionText, sessionBadge) = SessionStatus(snapshot);
        var (signalingText, signalingBadge) = SignalingStatus(snapshot.SignalingState);
        var (webRtcText, webRtcBadge) = WebRtcStatus(webrtc);
        var level = snapshot.AudioDiagnostics?.Level ?? AudioLevelSnapshot.Silence;
        var connected = FirstConnected(webrtc);
        var selected = connected?.SelectedCandidatePair;
        var rtt = FirstRoundTripTime(webrtc);
        var reception = connected?.AudioReceive;
        var send = connected?.AudioSend;

        return new DashboardViewModel
        {
            SessionStatusText = sessionText,
            SessionStatusBadge = sessionBadge,
            SignalingStatusText = signalingText,
            SignalingStatusBadge = signalingBadge,
            WebRtcStatusText = webRtcText,
            WebRtcStatusBadge = webRtcBadge,
            ConnectionModeText = ConnectionMode(selected, forceRelay),
            SessionCodeText = string.IsNullOrWhiteSpace(snapshot.SessionCode) ? Unknown : snapshot.SessionCode!,
            ViewerCount = snapshot.ViewerCount,
            IsCapturing = snapshot.AudioState is AudioCaptureState.Capturing,
            AudioPeak = level.Peak,
            AudioRms = level.Rms,
            LatencyText = rtt is { } value ? $"{value.TotalMilliseconds:F0} ms" : Unknown,
            // Latency (RTT), jitter and packet loss all come from the connected viewer's RTCP
            // reports (issue #32); each reads as unknown until the first report correlates.
            // Bitrate is the negotiated Opus send bitrate.
            JitterText = reception is { } r ? $"{r.Jitter.TotalMilliseconds:F0} ms" : Unknown,
            PacketLossText = reception is { } r2 ? $"{r2.PacketLossPercent:F1} %" : Unknown,
            BitrateText = send is { } s ? $"{s.OpusBitrateKbps} kbps" : Unknown,
        };
    }

    private static (string, DashboardBadge) SessionStatus(PublisherSnapshot s)
    {
        if (s.ErrorMessage is not null) return ("Error", DashboardBadge.Danger);
        if (s.SessionId is null) return ("Idle", DashboardBadge.Neutral);
        return s.AudioState is AudioCaptureState.Capturing
            ? ("Streaming", DashboardBadge.Success)
            : ("Waiting", DashboardBadge.Warning);
    }

    private static (string, DashboardBadge) SignalingStatus(SignalingConnectionState state) => state switch
    {
        SignalingConnectionState.Connected => ("Connected", DashboardBadge.Success),
        SignalingConnectionState.Connecting => ("Connecting", DashboardBadge.Warning),
        SignalingConnectionState.Reconnecting => ("Reconnecting", DashboardBadge.Warning),
        SignalingConnectionState.Faulted => ("Failed", DashboardBadge.Danger),
        _ => ("Disconnected", DashboardBadge.Neutral),
    };

    private static (string, DashboardBadge) WebRtcStatus(WebRtcPublisherDiagnostics? webrtc)
    {
        var viewers = webrtc?.Viewers;
        if (viewers is null || viewers.Count == 0) return ("Idle", DashboardBadge.Neutral);

        // Aggregate the per-viewer connection states into a single headline: a live
        // viewer (Connected) wins, then surface the worst problem, else in-progress.
        if (viewers.Any(v => v.State == PeerConnectionState.Connected)) return ("Connected", DashboardBadge.Success);
        if (viewers.Any(v => v.State == PeerConnectionState.Failed)) return ("Failed", DashboardBadge.Danger);
        if (viewers.Any(v => v.State is PeerConnectionState.New or PeerConnectionState.Connecting))
            return ("Checking", DashboardBadge.Warning);
        if (viewers.Any(v => v.State == PeerConnectionState.Disconnected)) return ("Disconnected", DashboardBadge.Neutral);
        return ("Closed", DashboardBadge.Neutral);
    }

    private static string ConnectionMode(string? selectedCandidatePair, bool forceRelay)
    {
        if (!string.IsNullOrWhiteSpace(selectedCandidatePair))
        {
            return selectedCandidatePair!.Contains("relay", StringComparison.OrdinalIgnoreCase)
                ? "Relay"
                : "Direct";
        }

        // No negotiated pair yet: forcing relay guarantees relay once connected.
        return forceRelay ? "Relay" : Unknown;
    }

    private static PeerConnectionDiagnostics? FirstConnected(WebRtcPublisherDiagnostics? webrtc) =>
        webrtc?.Viewers?.FirstOrDefault(v => v.State == PeerConnectionState.Connected);

    private static TimeSpan? FirstRoundTripTime(WebRtcPublisherDiagnostics? webrtc) =>
        webrtc?.Viewers?.FirstOrDefault(v => v.EstimatedRoundTripTime is not null)?.EstimatedRoundTripTime;

    /// <summary>Mock data so the dashboard renders in the XAML designer without a session.</summary>
    public static DashboardViewModel DesignTime { get; } = new()
    {
        SessionStatusText = "Streaming",
        SessionStatusBadge = DashboardBadge.Success,
        SignalingStatusText = "Connected",
        SignalingStatusBadge = DashboardBadge.Success,
        WebRtcStatusText = "Connected",
        WebRtcStatusBadge = DashboardBadge.Success,
        ConnectionModeText = "Direct",
        SessionCodeText = "ABC123",
        ViewerCount = 2,
        IsCapturing = true,
        AudioPeak = 0.72,
        AudioRms = 0.41,
        LatencyText = "38 ms",
        JitterText = "4 ms",
        PacketLossText = "0.1 %",
        BitrateText = "128 kbps",
    };

    // Kept for callers that render viewer counts with the current culture.
    public string ViewerCountText => ViewerCount.ToString(CultureInfo.CurrentCulture);
}
