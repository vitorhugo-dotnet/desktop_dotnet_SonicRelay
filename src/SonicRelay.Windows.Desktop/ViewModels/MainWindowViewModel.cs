using Avalonia.Threading;
using SonicRelay.Windows.Presentation;
using SonicRelay.Windows.WebRtc;

namespace SonicRelay.Windows.Desktop.ViewModels;

/// <summary>
/// Root view model for the desktop shell. It composes the sidebar, the account header and
/// the <see cref="DashboardShellViewModel"/>, and translates the shell's contextual actions
/// into <see cref="PublisherWorkflow"/> calls once a runtime is attached. With no runtime
/// (the standalone preview launch) the actions are disabled and the shell renders the
/// representative snapshot, so the layout and design system stay verifiable without a
/// backend. Attaching a live runtime — tray, reconnection and sign-in flow — is the next
/// phase-2 slice (issue #32); this type already exposes the seam for it.
/// </summary>
public sealed class MainWindowViewModel : ViewModelBase
{
    private PublisherRuntime? runtime;
    private PublisherWorkflow? workflow;
    private IWebRtcPublisher? webRtc;
    private PublisherSnapshot? snapshot;
    private bool showLogin = true;
    private NavigationItem selectedNavigation;
    private bool clearLogsArmed;
    private string? diagnosticsActionMessage;

    public MainWindowViewModel()
    {
        Auth = new AuthViewModel(
            (email, password) => Run(w => w.LoginAsync(email, password)),
            (email, password, confirm) => Run(w => w.RegisterAsync(email, password, confirm)));

        Navigation =
        [
            new NavigationItem(PageKey.Dashboard, "◧", "Dashboard"),
            new NavigationItem(PageKey.Audio, "♪", "Audio"),
            new NavigationItem(PageKey.Session, "⧉", "Session"),
            new NavigationItem(PageKey.Diagnostics, "⚙", "Diagnostics"),
            new NavigationItem(PageKey.Settings, "⚑", "Settings"),
        ];
        selectedNavigation = Navigation[0];

        CreateSessionCommand = new RelayCommand(() => Run(w => w.CreateSessionAsync()), () => ShellCommandAvailability.CreateSession(snapshot, HasWorkflow));
        StartAudioCommand = new RelayCommand(() => Run(w => w.StartAudioAsync()), () => ShellCommandAvailability.StartAudio(snapshot, HasWorkflow));
        StopAudioCommand = new RelayCommand(() => Run(w => w.StopAudioAsync()), () => ShellCommandAvailability.StopAudio(snapshot, HasWorkflow));
        EndSessionCommand = new RelayCommand(() => Run(w => w.EndSessionAsync()), () => ShellCommandAvailability.EndSession(snapshot, HasWorkflow));
        RetryCommand = new RelayCommand(() => Run(w => w.ReconnectSignalingAsync()), () => ShellCommandAvailability.Retry(snapshot, Shell.Capabilities, HasWorkflow));
        LogoutCommand = new RelayCommand(() => Run(w => w.LogoutAsync()), () => ShellCommandAvailability.Logout(snapshot, Shell.Capabilities, HasWorkflow));
        ExportDiagnosticsCommand = new RelayCommand(ExportDiagnosticsAsync, () => runtime is not null);
        ClearDiagnosticsCommand = new RelayCommand(ClearDiagnosticsAsync, () => runtime is not null);
    }

    public IReadOnlyList<NavigationItem> Navigation { get; }
    public DashboardShellViewModel Shell { get; } = new();
    public AuthViewModel Auth { get; }

    /// <summary>Settings and Audio surfaces; rebuilt from the runtime's stores on <see cref="Attach"/>,
    /// disconnected placeholders otherwise.</summary>
    public SettingsViewModel Settings { get; private set; } = new();
    public AudioPageViewModel Audio { get; private set; } = new();

    /// <summary>The selected sidebar destination; bound two-way to the navigation rail.</summary>
    public NavigationItem SelectedNavigation
    {
        get => selectedNavigation;
        set
        {
            // The rail can push a null selection transiently; keep the last valid page.
            if (value is null || !SetProperty(ref selectedNavigation, value)) return;
            RaisePropertyChanged(nameof(CurrentPage));
            RaisePropertyChanged(nameof(IsDashboard));
            RaisePropertyChanged(nameof(IsSession));
            RaisePropertyChanged(nameof(IsDiagnostics));
            RaisePropertyChanged(nameof(IsAudio));
            RaisePropertyChanged(nameof(IsSettings));
            RaisePropertyChanged(nameof(PageTitle));
            RaisePropertyChanged(nameof(PageSubtitle));
        }
    }

    public PageKey CurrentPage => selectedNavigation.Key;
    public bool IsDashboard => CurrentPage == PageKey.Dashboard;
    public bool IsSession => CurrentPage == PageKey.Session;
    public bool IsDiagnostics => CurrentPage == PageKey.Diagnostics;
    public bool IsAudio => CurrentPage == PageKey.Audio;
    public bool IsSettings => CurrentPage == PageKey.Settings;

    public string PageTitle => CurrentPage switch
    {
        PageKey.Audio => "Audio",
        PageKey.Session => "Session",
        PageKey.Diagnostics => "Diagnostics",
        PageKey.Settings => "Settings",
        _ => "Dashboard",
    };

    public string PageSubtitle => CurrentPage switch
    {
        PageKey.Audio => "Choose the system output to capture",
        PageKey.Session => "Broadcast session details and controls",
        PageKey.Diagnostics => "Publisher event log",
        PageKey.Settings => "Backend, relay and audio quality",
        _ => "Live status of the publisher transmission",
    };

    /// <summary>
    /// Whether the sign-in surface (rather than the dashboard) should be shown. Derived from the
    /// snapshot so a successful sign-in flips the shell to the dashboard automatically.
    /// </summary>
    public bool ShowLogin
    {
        get => showLogin;
        private set => SetProperty(ref showLogin, value);
    }

    /// <summary>Pure rule: show login whenever there is no authenticated snapshot.</summary>
    public static bool ShouldShowLogin(PublisherSnapshot? snapshot) =>
        snapshot is null || !snapshot.IsAuthenticated;

    public bool ClearLogsArmed
    {
        get => clearLogsArmed;
        private set => SetProperty(ref clearLogsArmed, value);
    }

    public string? DiagnosticsActionMessage
    {
        get => diagnosticsActionMessage;
        private set
        {
            if (SetProperty(ref diagnosticsActionMessage, value))
                RaisePropertyChanged(nameof(HasDiagnosticsActionMessage));
        }
    }

    /// <summary>Bindable presence check, following the same pattern as <see cref="HasSessionCode"/>
    /// rather than an Avalonia converter (there is no existing null/bool-to-visibility converter
    /// wired up anywhere in this codebase to reuse).</summary>
    public bool HasDiagnosticsActionMessage => diagnosticsActionMessage is not null;

    public void ArmClearLogs() => ClearLogsArmed = true;
    public void DisarmClearLogs() => ClearLogsArmed = false;

    public RelayCommand CreateSessionCommand { get; }
    public RelayCommand StartAudioCommand { get; }
    public RelayCommand StopAudioCommand { get; }
    public RelayCommand EndSessionCommand { get; }
    public RelayCommand RetryCommand { get; }
    public RelayCommand LogoutCommand { get; }
    public RelayCommand ExportDiagnosticsCommand { get; }
    public RelayCommand ClearDiagnosticsCommand { get; }

    /// <summary>
    /// Attaches a live publisher runtime: subscribes to workflow and WebRTC diagnostics and
    /// rebuilds the shell on every change (marshalled to the UI thread). Passing <c>null</c>
    /// detaches. Idempotent for the same runtime.
    /// </summary>
    public void Attach(PublisherRuntime? next)
    {
        if (ReferenceEquals(runtime, next)) return;

        if (workflow is not null) workflow.StateChanged -= OnStateChanged;
        if (webRtc is not null) webRtc.DiagnosticsChanged -= OnDiagnosticsChanged;

        runtime = next;
        workflow = next?.Workflow;
        webRtc = next?.WebRtcPublisher;

        if (workflow is not null) workflow.StateChanged += OnStateChanged;
        if (webRtc is not null) webRtc.DiagnosticsChanged += OnDiagnosticsChanged;

        Settings = next is null
            ? new SettingsViewModel()
            : new SettingsViewModel(next.BackendBaseUrl.ToString(), next.RelayPreference, next.AudioQuality);
        Audio = next is null
            ? new AudioPageViewModel()
            : new AudioPageViewModel(next.AudioCapture, next.AudioOutput);
        RaisePropertyChanged(nameof(Settings));
        RaisePropertyChanged(nameof(Audio));

        snapshot = workflow?.State;
        Rebuild();
    }

    private void OnStateChanged(PublisherSnapshot state) => Dispatch(() => { snapshot = state; Rebuild(); });
    private void OnDiagnosticsChanged(WebRtcPublisherDiagnostics _) => Dispatch(Rebuild);

    private void Rebuild() =>
        Apply(snapshot, webRtc?.Diagnostics, runtime?.RelayPreference.ForceRelay ?? false);

    private void Apply(PublisherSnapshot? state, WebRtcPublisherDiagnostics? diagnostics, bool forceRelay)
    {
        Shell.Update(state, diagnostics, forceRelay);
        ShowLogin = ShouldShowLogin(state);
        // The sign-in surface reads its busy/error state from the workflow snapshot; the
        // workflow owns credential validation and the friendly error messages.
        Auth.IsBusy = state?.IsBusy ?? false;
        Auth.ErrorMessage = ShowLogin ? state?.ErrorMessage : null;
        RaiseCommandStates();
        RaisePropertyChanged(nameof(KeepRunningInTray));
        Changed?.Invoke();
    }

    /// <summary>Raised after every state rebuild so the tray/background controller can refresh.</summary>
    public event Action? Changed;

    /// <summary>The latest publisher snapshot the shell is rendering (null before a runtime attaches).</summary>
    public PublisherSnapshot? CurrentSnapshot => snapshot;

    /// <summary>Whether closing the window should keep the app alive in the tray for the current state.</summary>
    public bool KeepRunningInTray => Shell.Capabilities.KeepsRunningInTray;

    private bool HasWorkflow => workflow is not null;

    private Task Run(Func<PublisherWorkflow, Task> action) =>
        workflow is null ? Task.CompletedTask : action(workflow);

    private async Task ExportDiagnosticsAsync()
    {
        DisarmClearLogs();
        if (runtime is null) return;
        DiagnosticsActionMessage = await DiagnosticsActions.ExportAsync(runtime.DiagnosticLog);
    }

    private async Task ClearDiagnosticsAsync()
    {
        if (runtime is null) return;
        if (!ClearLogsArmed)
        {
            ArmClearLogs();
            return;
        }
        DisarmClearLogs();
        DiagnosticsActionMessage = await DiagnosticsActions.ClearAsync(runtime.DiagnosticLog);
    }

    /// <summary>
    /// Forwards a diagnostic event to the attached runtime's log, or does nothing if no
    /// runtime is attached (the standalone preview launch). Diagnostics must never throw
    /// into the caller, matching PublisherRuntime.WriteDiagnosticAsync's own guarantee.
    /// </summary>
    public void LogDiagnostic(string category, string message)
    {
        if (runtime is null) return;
        _ = LogAsync(runtime.DiagnosticLog, category, message);

        static async Task LogAsync(SonicRelay.Windows.Core.Diagnostics.DiagnosticLog log, string category, string message)
        {
            try { await log.WriteAsync(category, message); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException) { }
        }
    }

    private void RaiseCommandStates()
    {
        CreateSessionCommand.RaiseCanExecuteChanged();
        StartAudioCommand.RaiseCanExecuteChanged();
        StopAudioCommand.RaiseCanExecuteChanged();
        EndSessionCommand.RaiseCanExecuteChanged();
        RetryCommand.RaiseCanExecuteChanged();
        LogoutCommand.RaiseCanExecuteChanged();
        ExportDiagnosticsCommand.RaiseCanExecuteChanged();
        ClearDiagnosticsCommand.RaiseCanExecuteChanged();
    }

    private static void Dispatch(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    /// <summary>Standalone preview instance (no runtime) used at launch and by the designer.</summary>
    public static MainWindowViewModel CreatePreview()
    {
        var vm = new MainWindowViewModel();
        vm.Apply(PreviewSnapshot(), PreviewDiagnostics(), forceRelay: false);
        return vm;
    }

    private static WebRtcPublisherDiagnostics PreviewDiagnostics() => new(
        ViewerConnectionCount: 1,
        Viewers:
        [
            new PeerConnectionDiagnostics(
                "viewer-1",
                PeerConnectionState.Connected,
                SelectedCandidatePair: "host:host",
                EstimatedRoundTripTime: TimeSpan.FromMilliseconds(38),
                AudioSend: new AudioSendDiagnostics(
                    EncodedPacketsSent: 12_000,
                    PacedPacketsDropped: 0,
                    SendFailures: 0,
                    PacingBacklogPackets: 0,
                    PacingBacklogDuration: TimeSpan.Zero,
                    FrameDurationMs: 20,
                    OpusBitrateKbps: 96,
                    Channels: 2,
                    ProfileId: "music-96",
                    InbandFecEnabled: true,
                    ExpectedPacketLossPercent: 1),
                AudioReceive: new AudioReceptionDiagnostics(
                    Jitter: TimeSpan.FromMilliseconds(4),
                    PacketLossPercent: 0.2,
                    CumulativePacketsLost: 0)),
        ]);

    private static PublisherSnapshot PreviewSnapshot() => new()
    {
        IsAuthenticated = true,
        UserEmail = "publisher@sonicrelay.app",
        DeviceName = Environment.MachineName,
        SessionId = Guid.NewGuid(),
        SessionCode = "K7DRRP",
        ViewerCount = 2,
        SignalingState = SonicRelay.Windows.Signaling.SignalingConnectionState.Connected,
        AudioState = SonicRelay.Windows.Audio.AudioCaptureState.Capturing,
        AudioDiagnostics = new SonicRelay.Windows.Audio.AudioCaptureDiagnostics(
            SonicRelay.Windows.Audio.AudioCaptureState.Capturing,
            Device: null,
            LastError: null,
            Level: new SonicRelay.Windows.Audio.AudioLevelSnapshot(0.72f, 0.41f),
            BytesCaptured: 0,
            FramesCaptured: 0),
        ActivityLog =
        [
            $"{DateTimeOffset.Now:HH:mm:ss} Signed in and publisher device is ready.",
            $"{DateTimeOffset.Now:HH:mm:ss} Session created.",
            $"{DateTimeOffset.Now:HH:mm:ss} Signaling: Connected.",
            $"{DateTimeOffset.Now:HH:mm:ss} Audio capture started.",
        ],
    };
}
