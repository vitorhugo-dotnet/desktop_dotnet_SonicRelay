using System.Globalization;
using SonicRelay.Windows.Presentation;
using SonicRelay.Windows.WebRtc;

namespace SonicRelay.Windows.Desktop.ViewModels;

/// <summary>
/// Bindable projection that drives every dashboard component. It owns no display logic
/// of its own: <see cref="Update"/> runs the shared, unit-tested presentation projection
/// (<see cref="DashboardViewModel.From"/> + <see cref="PublisherUiStateResolver.Resolve"/>)
/// and republishes the results as change-notifying properties. Fields the presentation
/// layer does not yet supply (jitter, packet loss, bitrate — pending the WebRTC getStats
/// wiring) surface as <see cref="DashboardViewModel.Unknown"/>, never as fabricated data.
/// </summary>
public sealed class DashboardShellViewModel : ViewModelBase
{
    private DashboardViewModel model = new();
    private PublisherUiState uiState = PublisherUiState.LoggedOut;
    private PublisherUiCapabilities capabilities = PublisherUiCapabilities.For(PublisherUiState.LoggedOut);
    private string? accountEmail;
    private string? deviceName;
    private IReadOnlyList<string> activityLog = [];

    /// <summary>The canonical interface state (issue #32), resolved from the snapshot.</summary>
    public PublisherUiState UiState
    {
        get => uiState;
        private set
        {
            if (SetProperty(ref uiState, value))
            {
                Capabilities = PublisherUiCapabilities.For(value);
                RaisePropertyChanged(nameof(UiStateText));
                RaisePropertyChanged(nameof(GlobalStatusText));
                RaisePropertyChanged(nameof(GlobalStatusBadge));
                RaisePropertyChanged(nameof(IsStreaming));
            }
        }
    }

    public PublisherUiCapabilities Capabilities
    {
        get => capabilities;
        private set
        {
            if (SetProperty(ref capabilities, value))
                RaisePropertyChanged(nameof(ShowsLiveMetrics));
        }
    }

    public bool ShowsLiveMetrics => Capabilities.ShowsLiveMetrics;
    public bool IsStreaming => uiState is PublisherUiState.StreamingDirect or PublisherUiState.StreamingRelay;

    public string UiStateText => Humanize(uiState);
    public string GlobalStatusText => GlobalStatus(uiState).Text;
    public DashboardBadge GlobalStatusBadge => GlobalStatus(uiState).Badge;

    // ---- Infrastructure ----
    public string SessionStatusText => model.SessionStatusText;
    public DashboardBadge SessionStatusBadge => model.SessionStatusBadge;
    public string SignalingStatusText => model.SignalingStatusText;
    public DashboardBadge SignalingStatusBadge => model.SignalingStatusBadge;
    public string WebRtcStatusText => model.WebRtcStatusText;
    public DashboardBadge WebRtcStatusBadge => model.WebRtcStatusBadge;
    public string ConnectionModeText => model.ConnectionModeText;
    public string ViewerCountText => model.ViewerCount.ToString(CultureInfo.CurrentCulture);

    // ---- Session code ----
    public string SessionCodeText => model.SessionCodeText;
    public bool HasSessionCode => !string.Equals(model.SessionCodeText, DashboardViewModel.Unknown, StringComparison.Ordinal);

    // ---- Audio monitor ----
    public bool IsCapturing => model.IsCapturing;
    /// <summary>Peak level clamped to 0..1 for the meter fill.</summary>
    public double AudioLevelFraction => Math.Clamp(model.AudioPeak, 0d, 1d);
    public string AudioPeakDbText => PeakToDb(model.AudioPeak);

    // ---- Quality metrics ----
    public string LatencyText => model.LatencyText;
    public string JitterText => model.JitterText;
    public string PacketLossText => model.PacketLossText;

    // ---- Bandwidth ----
    public string BitrateText => model.BitrateText;
    // The publisher only ever encodes Opus; codec is a real, stable fact even before the
    // getStats bitrate wiring lands, so it is shown rather than left blank.
    public string CodecText => model.IsCapturing ? "Opus" : DashboardViewModel.Unknown;
    public string BandwidthProfileText { get; } = "Opus / WebRTC";

    // ---- Account ----
    public string? AccountEmail
    {
        get => accountEmail;
        private set
        {
            if (SetProperty(ref accountEmail, value))
            {
                RaisePropertyChanged(nameof(AccountLabel));
                RaisePropertyChanged(nameof(AccountInitials));
            }
        }
    }

    public string? DeviceName
    {
        get => deviceName;
        private set
        {
            if (SetProperty(ref deviceName, value))
                RaisePropertyChanged(nameof(AccountLabel));
        }
    }

    public string AccountLabel => accountEmail ?? "Not signed in";
    public string AccountInitials => Initials(accountEmail);

    // ---- Technical console ----
    public IReadOnlyList<string> ActivityLog
    {
        get => activityLog;
        private set => SetProperty(ref activityLog, value);
    }

    /// <summary>
    /// The real binding path shared with every shell: rebuilds all display values from a
    /// publisher snapshot and the current WebRTC diagnostics. <paramref name="previous"/>
    /// carries the prior UI state so <see cref="PublisherUiStateResolver"/> can distinguish
    /// "session ended" from "never started".
    /// </summary>
    public void Update(PublisherSnapshot? snapshot, WebRtcPublisherDiagnostics? diagnostics, bool forceRelay)
    {
        var previous = uiState;
        model = DashboardViewModel.From(snapshot, diagnostics, forceRelay);
        UiState = PublisherUiStateResolver.Resolve(snapshot, diagnostics, forceRelay, previous);
        AccountEmail = snapshot?.UserEmail;
        DeviceName = snapshot?.DeviceName;
        ActivityLog = snapshot?.ActivityLog ?? [];
        RaiseModelProperties();
    }

    private void RaiseModelProperties()
    {
        foreach (var name in ModelDrivenProperties)
            RaisePropertyChanged(name);
    }

    private static readonly string[] ModelDrivenProperties =
    [
        nameof(SessionStatusText), nameof(SessionStatusBadge),
        nameof(SignalingStatusText), nameof(SignalingStatusBadge),
        nameof(WebRtcStatusText), nameof(WebRtcStatusBadge),
        nameof(ConnectionModeText), nameof(ViewerCountText),
        nameof(SessionCodeText), nameof(HasSessionCode),
        nameof(IsCapturing), nameof(AudioLevelFraction), nameof(AudioPeakDbText),
        nameof(LatencyText), nameof(JitterText), nameof(PacketLossText),
        nameof(BitrateText), nameof(CodecText), nameof(BandwidthProfileText),
    ];

    private static (string Text, DashboardBadge Badge) GlobalStatus(PublisherUiState state) => state switch
    {
        PublisherUiState.StreamingDirect => ("STREAMING · DIRECT", DashboardBadge.Success),
        PublisherUiState.StreamingRelay => ("STREAMING · RELAY", DashboardBadge.Success),
        PublisherUiState.WaitingViewer => ("WAITING FOR VIEWER", DashboardBadge.Warning),
        PublisherUiState.ConnectingSignaling => ("CONNECTING SIGNALING", DashboardBadge.Warning),
        PublisherUiState.ConnectingWebRtc => ("NEGOTIATING WEBRTC", DashboardBadge.Warning),
        PublisherUiState.Reconnecting => ("RECONNECTING", DashboardBadge.Warning),
        PublisherUiState.CreatingSession => ("CREATING SESSION", DashboardBadge.Warning),
        PublisherUiState.Authenticating => ("SIGNING IN", DashboardBadge.Warning),
        PublisherUiState.Faulted => ("FAULTED", DashboardBadge.Danger),
        PublisherUiState.Idle => ("IDLE", DashboardBadge.Neutral),
        PublisherUiState.Ended => ("SESSION ENDED", DashboardBadge.Neutral),
        _ => ("OFFLINE", DashboardBadge.Neutral),
    };

    private static string Humanize(PublisherUiState state) => state switch
    {
        PublisherUiState.LoggedOut => "Logged out",
        PublisherUiState.Authenticating => "Authenticating",
        PublisherUiState.Idle => "Idle",
        PublisherUiState.CreatingSession => "Creating session",
        PublisherUiState.WaitingViewer => "Waiting for viewer",
        PublisherUiState.ConnectingSignaling => "Connecting signaling",
        PublisherUiState.ConnectingWebRtc => "Connecting WebRTC",
        PublisherUiState.StreamingDirect => "Streaming (direct)",
        PublisherUiState.StreamingRelay => "Streaming (relay)",
        PublisherUiState.Reconnecting => "Reconnecting",
        PublisherUiState.Faulted => "Faulted",
        PublisherUiState.Ended => "Session ended",
        _ => state.ToString(),
    };

    private static string PeakToDb(double peak)
    {
        if (peak <= 0.0001d) return DashboardViewModel.Unknown;
        var db = 20d * Math.Log10(Math.Min(peak, 1d));
        return string.Create(CultureInfo.CurrentCulture, $"{db:F1} dB");
    }

    private static string Initials(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return "–";
        var name = email.Split('@')[0];
        var parts = name.Split(['.', '_', '-'], StringSplitOptions.RemoveEmptyEntries);
        var initials = parts.Length >= 2
            ? $"{parts[0][0]}{parts[1][0]}"
            : name[..Math.Min(2, name.Length)];
        return initials.ToUpper(CultureInfo.CurrentCulture);
    }
}
