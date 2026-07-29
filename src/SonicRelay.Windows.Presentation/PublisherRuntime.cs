using SonicRelay.Windows.ApiClient.DeviceIdentity;
using SonicRelay.Windows.ApiClient.Pairing;
using SonicRelay.Windows.ApiClient.Sessions;
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
    private readonly DeviceIdentitySession deviceIdentitySession;
    private string? lastLoggedState;
    private bool hadActiveSession;

    private PublisherRuntime(
        HttpClient httpClient,
        PublisherWorkflow workflow,
        Uri backendBaseUrl,
        IPeerConnectionManager peers,
        IWebRtcPublisher webRtcPublisher,
        WebRtcAudioBridge audioBridge,
        RelayPreferenceStore relayPreference,
        AudioQualityStore audioQuality,
        IAudioCaptureService audioCapture,
        AudioOutputPreferenceStore audioOutput,
        DeviceIdentitySession deviceIdentitySession)
    {
        this.httpClient = httpClient;
        this.peers = peers;
        this.webRtcPublisher = webRtcPublisher;
        this.audioBridge = audioBridge;
        this.deviceIdentitySession = deviceIdentitySession;
        Workflow = workflow;
        BackendBaseUrl = backendBaseUrl;
        RelayPreference = relayPreference;
        AudioQuality = audioQuality;
        AudioCapture = audioCapture;
        AudioOutput = audioOutput;
        DiagnosticLog = new DiagnosticLog();
        ReportExporter = new DiagnosticReportExporter();
        Workflow.StateChanged += OnWorkflowStateChanged;
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

    /// <summary>
    /// Composes the shared publisher runtime for one backend. The platform shell
    /// supplies its capture implementation (WASAPI loopback on Windows, PipeWire on
    /// Linux later — issue #32); this shared composition never references a concrete
    /// capture backend.
    /// </summary>
    public static PublisherRuntime Create(Uri backendBaseUrl, IAudioCaptureService audioCapture)
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
        var credentialStore = new UserScopedDeviceCredentialStore();
        var deviceIdentitySession = new DeviceIdentitySession(
            new DeviceIdentityApiClient(http),
            credentialStore,
            Environment.MachineName);

        // The WebRTC publisher needs the signaling client to send offers/candidates,
        // but the client takes its handlers up front — register the publisher through
        // a composite handler after both exist.
        var signalingHandlers = new CompositeSignalingMessageHandler();
        var signaling = new SignalingClient(configuration, deviceIdentitySession, [signalingHandlers]);
        // ICE servers (including short-lived TURN credentials) come from the
        // backend, which serves the SonicRelay coturn deployment. The public
        // Google STUN fallback is a debug-build-only convenience for when the
        // backend request fails; release builds get an empty ICE server list
        // instead of silently depending on Google's STUN server.
        var iceServersProvider = new BackendIceServersProvider(
            new WebRtcApiClient(http, deviceIdentitySession),
            allowGoogleStunDevFallback: AllowGoogleStunDevFallback);
        var relayPreference = new RelayPreferenceStore();
        var audioQuality = new AudioQualityStore();
        var peers = new PeerConnectionManager(
            new SipSorceryPeerConnectionFactory(
                iceServersProvider,
                () => relayPreference.ForceRelay,
                () => audioQuality.CurrentProfile),
            new WebRtcPublisherOptions());
        var webRtcPublisher = new WebRtcPublisher(signaling, peers);
        signalingHandlers.Register(webRtcPublisher);

        var audio = audioCapture;
        var audioOutput = new AudioOutputPreferenceStore();
        // Restore the previously selected output device (null = system default).
        audio.SelectOutputDevice(audioOutput.SelectedDeviceId);
        var audioBridge = new WebRtcAudioBridge(audio, webRtcPublisher);
        var workflow = new PublisherWorkflow(
            deviceIdentitySession,
            credentialStore,
            new SessionApiClient(http, deviceIdentitySession),
            signaling,
            audio);
        return new PublisherRuntime(
            http,
            workflow,
            normalized,
            peers,
            webRtcPublisher,
            audioBridge,
            relayPreference,
            audioQuality,
            audio,
            audioOutput,
            deviceIdentitySession);
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

    public async ValueTask DisposeAsync()
    {
        Workflow.StateChanged -= OnWorkflowStateChanged;
        // Stop the audio pump before the workflow disposes the capture service,
        // then tear down the WebRTC publisher (which disposes the peer manager).
        await audioBridge.DisposeAsync();
        await Workflow.DisposeAsync();
        await webRtcPublisher.DisposeAsync();
        deviceIdentitySession.Dispose();
        httpClient.Dispose();
        DiagnosticLog.Dispose();
    }
}
