using SonicRelay.Windows.ApiClient.DeviceIdentity;
using SonicRelay.Windows.ApiClient.Pairing;
using SonicRelay.Windows.ApiClient.Sessions;
using SonicRelay.Windows.ApiClient.Settings;
using SonicRelay.Windows.ApiClient.WebRtc;
using SonicRelay.Windows.Audio;
using SonicRelay.Windows.Core.Audio;
using SonicRelay.Windows.Core.Configuration;
using SonicRelay.Windows.Core.Diagnostics;
using SonicRelay.Windows.Core.Storage.DeviceIdentity;
using SonicRelay.Windows.Presentation.Pairing;
using SonicRelay.Windows.Signaling;
using SonicRelay.Windows.WebRtc;

namespace SonicRelay.Windows.Presentation;

public sealed class PublisherRuntime : IAsyncDisposable
{
    // Google's public STUN server is a development-only fallback for when the
    // backend ICE endpoint is unreachable; it must never be relied on in a
    // release build.
#if DEBUG
    private const bool AllowGoogleStunDevFallback = true;
#else
    private const bool AllowGoogleStunDevFallback = false;
#endif

    private readonly HttpClient httpClient;
    private readonly IPeerConnectionManager peers;
    private readonly IWebRtcPublisher webRtcPublisher;
    private readonly WebRtcAudioBridge audioBridge;
    private readonly AudioPlaybackService? playback;
    private readonly DeviceIdentitySession deviceIdentitySession;
    private readonly SystemNetworkAvailability networkAvailability;
    private readonly ResourceUsageSampler resourceUsageSampler;
    private string? lastLoggedState;
    private bool hadActiveSession;

    private PublisherRuntime(
        HttpClient httpClient,
        PublisherWorkflow workflow,
        Uri backendBaseUrl,
        IPeerConnectionManager peers,
        IWebRtcPublisher webRtcPublisher,
        WebRtcAudioBridge audioBridge,
        AudioPlaybackService? playback,
        RelayPreferenceStore relayPreference,
        AudioQualityStore audioQuality,
        IAudioCaptureService audioCapture,
        AudioOutputPreferenceStore audioOutput,
        DeviceIdentitySession deviceIdentitySession,
        DiagnosticLog diagnosticLog,
        SystemNetworkAvailability networkAvailability,
        ResourceUsageSampler resourceUsageSampler)
    {
        this.networkAvailability = networkAvailability;
        this.resourceUsageSampler = resourceUsageSampler;
        this.httpClient = httpClient;
        this.peers = peers;
        this.webRtcPublisher = webRtcPublisher;
        this.audioBridge = audioBridge;
        this.playback = playback;
        this.deviceIdentitySession = deviceIdentitySession;
        Workflow = workflow;
        BackendBaseUrl = backendBaseUrl;
        RelayPreference = relayPreference;
        AudioQuality = audioQuality;
        AudioCapture = audioCapture;
        AudioOutput = audioOutput;
        DiagnosticLog = diagnosticLog;
        ReportExporter = new DiagnosticReportExporter();
        Workflow.StateChanged += OnWorkflowStateChanged;
        Workflow.LogAppended += OnWorkflowLogAppended;
        _ = WriteDiagnosticAsync("runtime", "Publisher runtime configured.", new Dictionary<string, string>
        {
            ["backend"] = DiagnosticRedactor.BackendHost(backendBaseUrl)
        });
    }

    public PublisherWorkflow Workflow { get; }
    public Uri BackendBaseUrl { get; }
    public RelayPreferenceStore RelayPreference { get; }
    public AudioQualityStore AudioQuality { get; }
    public IAudioCaptureService AudioCapture { get; }
    public AudioOutputPreferenceStore AudioOutput { get; }
    public DiagnosticLog DiagnosticLog { get; }
    public DiagnosticReportExporter ReportExporter { get; }
    public IWebRtcPublisher WebRtcPublisher => webRtcPublisher;
    public PairingViewModel? Pairing { get; private set; }

    /// <summary>Whether this platform composition can play back what other participants send.</summary>
    public bool SupportsTwoWayAudio => playback is not null;

    private IRelaySettingsApiClient? relaySettingsApi;

    /// <summary>Backend relay-preference sync (shared across this device's pairings).</summary>
    public IRelaySettingsApiClient RelaySettingsApi =>
        relaySettingsApi ??= new RelaySettingsApiClient(httpClient, deviceIdentitySession);

    /// <summary>
    /// Composes the shared publisher runtime for one backend. The platform shell
    /// supplies its capture implementation (WASAPI loopback on Windows, PipeWire on
    /// Linux — issue #32) and, optionally, its own device-credential store and
    /// audio-output preference store (Linux would use Secret Service instead of
    /// DPAPI); omitting either keeps the existing Windows-default behavior.
    /// <paramref name="relayPreferenceOverride"/> exists for tests — the default on-disk
    /// preferences file is otherwise always used. <paramref name="deviceIdentityApiClientOverride"/>
    /// also exists only for tests: it is the narrowest seam that lets a test drive a
    /// device-identity bootstrap through to a genuine success without a real backend,
    /// since <see cref="DeviceIdentitySession"/> otherwise always talks over
    /// <paramref name="backendBaseUrl"/>.
    /// </summary>
    public static PublisherRuntime Create(
        Uri backendBaseUrl,
        IAudioCaptureService audioCapture,
        IDeviceCredentialStore? credentialStoreOverride = null,
        AudioOutputPreferenceStore? audioOutputPreferenceOverride = null,
        RelayPreferenceStore? relayPreferenceOverride = null,
        IDeviceIdentityApiClient? deviceIdentityApiClientOverride = null,
        IAudioPlaybackBackend? playbackBackend = null)
    {
        ArgumentNullException.ThrowIfNull(backendBaseUrl);
        ArgumentNullException.ThrowIfNull(audioCapture);
        if (!backendBaseUrl.IsAbsoluteUri || backendBaseUrl.Scheme is not ("http" or "https"))
            throw new ConfigurationValidationException("Backend URL must be an absolute HTTP or HTTPS URL.");

        var normalized = backendBaseUrl.AbsoluteUri.EndsWith('/') ? backendBaseUrl : new Uri(backendBaseUrl.AbsoluteUri + "/");
        // The backend hosts the signaling WebSocket at /ws/signaling; deriving it from the backend base
        // keeps a single configured address while matching the server route (a bare /signaling returns 404).
        var signalingUrl = new Uri(normalized, "ws/signaling");
        var configuration = new PublisherConfiguration(normalized, signalingUrl, 4);
        configuration.Validate();
        var http = new HttpClient { BaseAddress = normalized, Timeout = TimeSpan.FromSeconds(30) };
        var credentialStore = credentialStoreOverride ?? new UserScopedDeviceCredentialStore();
        var deviceIdentitySession = new DeviceIdentitySession(
            deviceIdentityApiClientOverride ?? new DeviceIdentityApiClient(http),
            credentialStore,
            Environment.MachineName);

        var diagnosticLog = new DiagnosticLog();
        // Samples CPU/memory/network throughout the run (not just while streaming) so a
        // post-mortem has a baseline to compare a suspicious reading against — "was this normal
        // for this machine, or a spike?" needs both.
        var resourceUsageSampler = new ResourceUsageSampler(new ProcessRawResourceCounterSource(), diagnosticLog);
        // The WebRTC publisher needs the signaling client to send offers/candidates,
        // but the client takes its handlers up front — register the publisher through
        // a composite handler after both exist.
        var signalingHandlers = new CompositeSignalingMessageHandler();
        // The network gate keeps a machine with no route at all from spending its reconnect
        // budget on attempts that cannot succeed, and the journal records each recovery step
        // with the generation that produced it — the two things a post-mortem of a failed
        // reconnect needs and the log could not previously answer.
        var networkAvailability = new SystemNetworkAvailability();
        var signaling = new SignalingClient(configuration, deviceIdentitySession, [signalingHandlers],
            networkAvailability, new DiagnosticRecoveryJournal(diagnosticLog));
        var relayPreference = relayPreferenceOverride ?? new RelayPreferenceStore();
        // ICE servers (including short-lived TURN credentials) come from the
        // backend, which serves the SonicRelay coturn deployment. The public
        // Google STUN fallback is a debug-build-only convenience for when the
        // backend request fails; release builds get an empty ICE server list
        // instead of silently depending on Google's STUN server. The relay-mode/coturn
        // preferences are per-device local settings (issue #26 follow-up), read live so a
        // Settings change applies to the next ICE fetch without recreating the runtime.
        var iceServersProvider = new BackendIceServersProvider(
            new WebRtcApiClient(http, deviceIdentitySession),
            () => new RelayPreferenceSnapshot(relayPreference.RelayMode, relayPreference.CoturnUrlOverride),
            allowGoogleStunDevFallback: AllowGoogleStunDevFallback);
        var audioQuality = new AudioQualityStore();
        // The session mode is chosen when the session is created and read here when each peer
        // connection is built, because a connection's audio direction is fixed at construction:
        // a `sendonly` m-line cannot later accept a peer's own audio track.
        var sessionMode = new SessionModeState();
        var peers = new PeerConnectionManager(
            new SipSorceryPeerConnectionFactory(
                iceServersProvider,
                () => relayPreference.ForceRelay,
                () => audioQuality.CurrentProfile,
                () => sessionMode.IsDuplex ? WebRtcAudioDirection.SendRecv : WebRtcAudioDirection.SendOnly),
            new WebRtcPublisherOptions());
        var webRtcPublisher = new WebRtcPublisher(signaling, peers);
        signalingHandlers.Register(webRtcPublisher);

        signaling.ReconnectAttempting += attempt => LogReconnectAttempt(diagnosticLog, attempt);
        signaling.Closed += reason => LogSignalingClosed(diagnosticLog, reason);
        webRtcPublisher.IceRestartRequested += viewerId => LogIceRestart(diagnosticLog, viewerId);
        webRtcPublisher.PeerRebuildRequested += viewerId => LogPeerRebuild(diagnosticLog, viewerId);

        var audio = audioCapture;
        var audioOutput = audioOutputPreferenceOverride ?? new AudioOutputPreferenceStore();
        // Restore the previously selected output device (null = system default).
        audio.SelectOutputDevice(audioOutput.SelectedDeviceId);
        var audioBridge = new WebRtcAudioBridge(audio, webRtcPublisher);

        // Capture is the same system-output mix in both modes, so playback is the only thing
        // two-way audio adds — and the only thing a platform can be missing.
        var playback = playbackBackend is null ? null : new AudioPlaybackService(playbackBackend);
        if (playback is not null)
        {
            webRtcPublisher.RemoteAudioFrameReceived += (_, frame) =>
                playback.Play(frame.Samples, frame.SampleRate, frame.ChannelCount);
        }

        var workflow = new PublisherWorkflow(
            deviceIdentitySession,
            credentialStore,
            new SessionApiClient(http, deviceIdentitySession),
            signaling,
            audio,
            new PairingApiClient(http, deviceIdentitySession),
            webRtcPublisher,
            playback,
            mode => sessionMode.Mode = mode);
        // Surface WebRTC recovery events in the technical console too — the on-disk
        // diagnostic log is invisible in the UI and these are exactly the lines a user
        // debugging a dropped viewer needs to see.
        webRtcPublisher.IceRestartRequested += _ =>
            workflow.LogActivity("WebRTC: ICE restart requested for a reconnected viewer.");
        webRtcPublisher.PeerRebuildRequested += _ =>
            workflow.LogActivity("WebRTC: rebuilding peer connection after repeated ICE restart failures.");
        return new PublisherRuntime(
            http,
            workflow,
            normalized,
            peers,
            webRtcPublisher,
            audioBridge,
            playback,
            relayPreference,
            audioQuality,
            audio,
            audioOutput,
            deviceIdentitySession,
            diagnosticLog,
            networkAvailability,
            resourceUsageSampler);
    }

    public async Task InitializeDeviceIdentityAsync(CancellationToken cancellationToken = default)
    {
        await Workflow.InitializeDeviceIdentityAsync(cancellationToken);
        if (Workflow.State.DeviceId is not { } deviceId)
        {
            return;
        }

        Pairing = new PairingViewModel(
            new PairingApiClient(httpClient, deviceIdentitySession),
            new PairingQrCodeService(),
            deviceId);
    }

    private void OnWorkflowStateChanged(PublisherSnapshot state)
    {
        // The publisher closes signaling locally on session end, so it never
        // receives its own session.ended; tear down peer connections here when
        // the active session clears.
        var hasSession = state.SessionId is not null;
        if (hadActiveSession && !hasSession)
        {
            _ = peers.RemoveAllAsync();
        }
        hadActiveSession = hasSession;

        var signature = $"{state.IsAuthenticated}|{state.SignalingState}|{state.AudioState}|{state.ViewerCount}|{state.ErrorMessage}";
        if (signature == lastLoggedState) return;
        lastLoggedState = signature;
        _ = WriteDiagnosticAsync("publisher-state", state.ErrorMessage ?? "Publisher status changed.", new Dictionary<string, string>
        {
            ["authenticated"] = state.IsAuthenticated.ToString(),
            ["signaling"] = state.SignalingState.ToString(),
            ["audio"] = state.AudioState.ToString(),
            ["viewerCount"] = state.ViewerCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
        });
    }

    /// <summary>
    /// Persists every workflow activity line, independent of <see cref="OnWorkflowStateChanged"/>'s
    /// state-signature dedup. That dedup exists to keep "publisher-state" from spamming a line per
    /// no-op update, but it also means a message like "Session ended." — which changes only
    /// SessionId, a field the signature does not track — never reached the on-disk log even though
    /// it was right there in the UI's ActivityLog. This is the only place that gap is closed, so a
    /// post-mortem of a dropped connection always has the actual reason, not just a state trail
    /// that quietly stops.
    /// </summary>
    private void OnWorkflowLogAppended(string message) =>
        _ = WriteDiagnosticAsync("workflow", message, NoProperties);

    private async Task WriteDiagnosticAsync(string category, string message, IReadOnlyDictionary<string, string> properties)
    {
        try
        {
            await DiagnosticLog.WriteAsync(category, message, properties);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            // Diagnostics must never interrupt publisher operation.
        }
    }

    private static readonly IReadOnlyDictionary<string, string> NoProperties = new Dictionary<string, string>();

    private static async void LogReconnectAttempt(DiagnosticLog log, int attempt)
    {
        try { await log.WriteAsync("reconnect-attempt", $"Signaling reconnect attempt {attempt + 1}.", NoProperties); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException) { }
    }

    private static async void LogSignalingClosed(DiagnosticLog log, SignalingCloseReason reason)
    {
        try
        {
            await log.WriteAsync("signaling-closed", "Signaling connection closed.", new Dictionary<string, string>
            {
                ["reason"] = reason.ToString()
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException) { }
    }

    private static async void LogIceRestart(DiagnosticLog log, string viewerId)
    {
        try
        {
            await log.WriteAsync("ice-restart", "ICE restart requested for a reconnected viewer.", new Dictionary<string, string>
            {
                ["viewerId"] = viewerId
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException) { }
    }

    private static async void LogPeerRebuild(DiagnosticLog log, string viewerId)
    {
        try
        {
            await log.WriteAsync("peer-rebuild", "Peer connection rebuilt after repeated ICE restart failures.", new Dictionary<string, string>
            {
                ["viewerId"] = viewerId
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        Workflow.StateChanged -= OnWorkflowStateChanged;
        Workflow.LogAppended -= OnWorkflowLogAppended;
        // Stop the audio pump before the workflow disposes the capture service,
        // then tear down the WebRTC publisher (which disposes the peer manager).
        await audioBridge.DisposeAsync();
        await Workflow.DisposeAsync();
        // After the workflow, which stops playback while the peer connections it belongs to
        // are still up; disposing it first would leave the receive path writing to a
        // disposed device.
        if (playback is not null) await playback.DisposeAsync();
        await webRtcPublisher.DisposeAsync();
        await resourceUsageSampler.DisposeAsync();
        deviceIdentitySession.Dispose();
        networkAvailability.Dispose();
        httpClient.Dispose();
        DiagnosticLog.Dispose();
    }
}
