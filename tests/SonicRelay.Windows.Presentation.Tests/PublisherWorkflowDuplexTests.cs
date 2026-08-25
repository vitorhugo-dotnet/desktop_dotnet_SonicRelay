using SonicRelay.Windows.ApiClient.Pairing;
using SonicRelay.Windows.ApiClient.Sessions;
using SonicRelay.Windows.Audio;
using SonicRelay.Windows.Core.Authentication;
using SonicRelay.Windows.Core.Storage.DeviceIdentity;
using SonicRelay.Windows.Presentation;
using SonicRelay.Windows.Signaling;
using SonicRelay.Windows.WebRtc;

namespace SonicRelay.Windows.Presentation.Tests;

/// <summary>
/// The workflow half of two-way audio (dotnet_SonicRelay#22): creating a duplex session,
/// publishing from the microphone rather than the system output, mute, and the publisher-only
/// audio-permission control.
/// </summary>
public sealed class PublisherWorkflowDuplexTests
{
    [Fact]
    public async Task AOneWaySessionIsCreatedWithoutAMode()
    {
        await using var context = new WorkflowContext();
        await context.AuthenticateAsync();

        await context.Workflow.CreateSessionAsync();

        // Byte-identical to what pre-duplex builds sent, which is what guarantees this change
        // cannot alter an existing flow.
        Assert.Null(context.Sessions.LastCreateRequest.Mode);
        Assert.False(context.Workflow.State.IsDuplexSession);
        Assert.Equal(SessionModes.Broadcast, context.PublishedMode);
    }

    [Fact]
    public async Task ADuplexSessionIsCreatedWithTheModeAndPublishesIt()
    {
        await using var context = new WorkflowContext();
        await context.AuthenticateAsync();

        await context.Workflow.CreateSessionAsync(duplex: true);

        Assert.Equal(SessionModes.Duplex, context.Sessions.LastCreateRequest.Mode);
        Assert.True(context.Workflow.State.IsDuplexSession);
        // The peer-connection factory reads this before the first peer exists: a connection's
        // audio direction is fixed at construction and cannot be corrected afterwards.
        Assert.Equal(SessionModes.Duplex, context.PublishedMode);
    }

    [Fact]
    public async Task ABackendThatIgnoresTheModeLeavesTheSessionOneWay()
    {
        await using var context = new WorkflowContext();
        context.Sessions.RespondWithMode = SessionModes.Broadcast;
        await context.AuthenticateAsync();

        await context.Workflow.CreateSessionAsync(duplex: true);

        // Trust the backend's echo over the request: an older backend that drops `mode` must
        // not leave this device believing it can talk.
        Assert.False(context.Workflow.State.IsDuplexSession);
        Assert.Equal(SessionModes.Broadcast, context.PublishedMode);
    }

    [Fact]
    public async Task ADeviceWithoutAMicrophoneRefusesToStartATwoWaySession()
    {
        await using var context = new WorkflowContext(withMicrophone: false);
        await context.AuthenticateAsync();

        await context.Workflow.CreateSessionAsync(duplex: true);

        Assert.Null(context.Workflow.State.SessionId);
        Assert.Contains("two-way", context.Workflow.State.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(context.Workflow.SupportsTwoWayAudio);
    }

    [Fact]
    public async Task AOneWaySessionPublishesTheSystemOutput()
    {
        await using var context = new WorkflowContext();
        await context.AuthenticateAsync();
        await context.Workflow.CreateSessionAsync();

        await context.Workflow.StartAudioAsync();

        Assert.True(context.Audio.StartCalled);
        Assert.False(context.Microphone.StartCalled);
    }

    [Fact]
    public async Task ATwoWaySessionPublishesTheMicrophone()
    {
        await using var context = new WorkflowContext();
        await context.AuthenticateAsync();
        await context.Workflow.CreateSessionAsync(duplex: true);

        await context.Workflow.StartAudioAsync();

        // Publishing the system output mix into a call would send the other side its own
        // voice back through the speakers.
        Assert.True(context.Microphone.StartCalled);
        Assert.False(context.Audio.StartCalled);
    }

    [Fact]
    public async Task StoppingAudioReleasesBothCaptureDevices()
    {
        await using var context = new WorkflowContext();
        await context.AuthenticateAsync();
        await context.Workflow.CreateSessionAsync(duplex: true);
        await context.Workflow.StartAudioAsync();

        await context.Workflow.StopAudioAsync();

        Assert.True(context.Microphone.StopCalled);
        Assert.True(context.Audio.StopCalled);
    }

    [Fact]
    public async Task MutingIsForwardedToWebRtcAndReflectedInTheSnapshot()
    {
        await using var context = new WorkflowContext();
        await context.AuthenticateAsync();
        await context.Workflow.CreateSessionAsync(duplex: true);

        await context.Workflow.SetOutgoingAudioMutedAsync(true);

        Assert.True(context.WebRtc.Muted);
        Assert.True(context.Workflow.State.OutgoingAudioMuted);
    }

    [Fact]
    public async Task RevokingAParticipantPermissionCallsTheBackend()
    {
        await using var context = new WorkflowContext();
        await context.AuthenticateAsync();
        await context.Workflow.CreateSessionAsync(duplex: true);
        var participantId = Guid.NewGuid();

        await context.Workflow.SetParticipantAudioPermissionAsync(participantId, canSendAudio: false);

        var call = Assert.Single(context.Sessions.AudioPermissionCalls);
        Assert.Equal(participantId, call.ParticipantId);
        Assert.False(call.CanSendAudio);
    }

    [Fact]
    public async Task AudioPermissionsAreRefusedOnAOneWaySession()
    {
        await using var context = new WorkflowContext();
        await context.AuthenticateAsync();
        await context.Workflow.CreateSessionAsync();

        await context.Workflow.SetParticipantAudioPermissionAsync(Guid.NewGuid(), canSendAudio: false);

        Assert.Empty(context.Sessions.AudioPermissionCalls);
        Assert.Contains("two-way", context.Workflow.State.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ParticipantBroadcastsAreFoldedInByIdRatherThanAppended()
    {
        await using var context = new WorkflowContext();
        await context.AuthenticateAsync();
        await context.Workflow.CreateSessionAsync(duplex: true);

        context.WebRtc.RaiseParticipant(new ParticipantAudioState("p-1", "viewer", "duplex", true, true, true, false));
        context.WebRtc.RaiseParticipant(new ParticipantAudioState("p-1", "viewer", "duplex", false, false, true, false));

        var participant = Assert.Single(context.Workflow.State.Participants);
        // A permission change updates the row rather than duplicating the participant.
        Assert.False(participant.AudioSendAllowed);
    }

    [Fact]
    public async Task EndingASessionReturnsToOneWayDefaults()
    {
        await using var context = new WorkflowContext();
        await context.AuthenticateAsync();
        await context.Workflow.CreateSessionAsync(duplex: true);
        context.WebRtc.RaiseParticipant(new ParticipantAudioState("p-1", "viewer", "duplex", true, true, true, false));

        await context.Workflow.EndSessionAsync();

        Assert.False(context.Workflow.State.IsDuplexSession);
        Assert.Empty(context.Workflow.State.Participants);
        // The next session builds its peer connections from this, so a stale duplex value
        // would make a one-way session offer `sendrecv`.
        Assert.Equal(SessionModes.Broadcast, context.PublishedMode);
    }

    [Fact]
    public async Task RefreshingParticipantsReadsTheAuthoritativeRoster()
    {
        await using var context = new WorkflowContext();
        await context.AuthenticateAsync();
        await context.Workflow.CreateSessionAsync(duplex: true);
        var participantId = Guid.NewGuid();
        context.Sessions.Participants = new SessionParticipantsResponse(
            context.Sessions.Created.Id,
            SessionModes.Duplex,
            [new SessionParticipant(participantId, "viewer", "connected", true, true, true, false, DateTimeOffset.UtcNow, null, false)]);

        await context.Workflow.RefreshParticipantsAsync();

        var participant = Assert.Single(context.Workflow.State.Participants);
        Assert.Equal(participantId.ToString("D"), participant.ParticipantId);
        Assert.True(participant.AudioSendAllowed);
    }

    private sealed class WorkflowContext : IAsyncDisposable
    {
        public WorkflowContext(bool withMicrophone = true)
        {
            Microphone = new FakeCapture();
            Workflow = new PublisherWorkflow(
                new FakeIdentity(),
                new FakeCredentialStore(),
                Sessions,
                new FakeSignaling(),
                Audio,
                new FakePairings(),
                WebRtc,
                withMicrophone ? Microphone : null,
                playback: null,
                onSessionModeChanged: mode => PublishedMode = mode);
        }

        public PublisherWorkflow Workflow { get; }
        public FakeSessionApi Sessions { get; } = new();
        public FakeCapture Audio { get; } = new();
        public FakeCapture Microphone { get; }
        public FakePublisher WebRtc { get; } = new();
        public string PublishedMode { get; private set; } = SessionModes.Broadcast;

        public Task AuthenticateAsync() => Workflow.InitializeDeviceIdentityAsync();

        public ValueTask DisposeAsync() => Workflow.DisposeAsync();
    }

    private sealed class FakeSessionApi : ISessionApiClient
    {
        public StreamSessionResponse Created { get; } = new(
            Guid.NewGuid(), Guid.NewGuid(), "waiting", 4,
            DateTimeOffset.UtcNow.AddMinutes(5), null, null, DateTimeOffset.UtcNow, "ABC123");

        public CreateSessionRequest LastCreateRequest { get; private set; } = new();

        /// <summary>Overrides the mode the backend echoes back, defaulting to the requested one.</summary>
        public string? RespondWithMode { get; set; }

        public SessionParticipantsResponse Participants { get; set; } =
            new(Guid.NewGuid(), SessionModes.Duplex, []);

        public List<(Guid SessionId, Guid ParticipantId, bool CanSendAudio)> AudioPermissionCalls { get; } = [];

        public Task<StreamSessionResponse> CreateSessionAsync(CreateSessionRequest request, CancellationToken cancellationToken = default)
        {
            LastCreateRequest = request;
            return Task.FromResult(Created with { Mode = RespondWithMode ?? request.Mode ?? SessionModes.Broadcast });
        }

        public Task<IReadOnlyList<ActiveSessionResponse>> GetActiveSessionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ActiveSessionResponse>>([]);

        public Task<StreamSessionResponse> EndSessionAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Created with { Id = sessionId, Status = "ended" });

        public Task<SessionParticipantsResponse> GetParticipantsAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Participants);

        public Task<SessionParticipant> SetAudioPermissionAsync(Guid sessionId, Guid participantId, bool canSendAudio, CancellationToken cancellationToken = default)
        {
            AudioPermissionCalls.Add((sessionId, participantId, canSendAudio));
            return Task.FromResult(new SessionParticipant(
                participantId, "viewer", "connected", canSendAudio, canSendAudio, true, false,
                DateTimeOffset.UtcNow, null, false));
        }
    }

    private sealed class FakePublisher : IWebRtcPublisher
    {
        public WebRtcPublisherDiagnostics Diagnostics { get; } = new(0, []);
        public bool Muted { get; private set; }
        public IReadOnlyCollection<ParticipantAudioState> Participants => [];

        public event Action<WebRtcPublisherDiagnostics>? DiagnosticsChanged { add { } remove { } }
        public event Action<string>? IceRestartRequested { add { } remove { } }
        public event Action<string>? PeerRebuildRequested { add { } remove { } }
        public event Action<ParticipantAudioState>? ParticipantAudioStateChanged;
        public event Action<string, RemoteAudioFrame>? RemoteAudioFrameReceived { add { } remove { } }

        public void RaiseParticipant(ParticipantAudioState state) => ParticipantAudioStateChanged?.Invoke(state);

        public Task HandleAsync(SignalingMessageEnvelope message, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task PushAudioFrameAsync(WebRtcAudioFrame frame, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SetOutgoingAudioMutedAsync(bool muted, CancellationToken cancellationToken = default)
        {
            Muted = muted;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeCapture : IAudioCaptureService
    {
        public AudioCaptureState State { get; private set; } = AudioCaptureState.Stopped;
        public AudioCaptureDiagnostics Diagnostics => new(State, null, null, AudioLevelSnapshot.Silence, 0, 0);
        public string? PreferredDeviceId { get; private set; }
        public bool StartCalled { get; private set; }
        public bool StopCalled { get; private set; }

        public event Action<AudioCaptureState>? StateChanged;
        public event Action<AudioFrame>? FrameCaptured { add { } remove { } }
        public event Action<AudioLevelSnapshot>? LevelChanged { add { } remove { } }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartCalled = true;
            State = AudioCaptureState.Capturing;
            StateChanged?.Invoke(State);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCalled = true;
            State = AudioCaptureState.Stopped;
            StateChanged?.Invoke(State);
            return Task.CompletedTask;
        }

        public Task PauseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResumeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public IReadOnlyList<AudioOutputDevice> GetOutputDevices() => [];
        public void SelectOutputDevice(string? deviceId) => PreferredDeviceId = deviceId;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeIdentity : IDeviceAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default) =>
            Task.FromResult("token");

        public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeCredentialStore : IDeviceCredentialStore
    {
        private readonly DeviceCredential credential = new(
            Guid.Parse("00000000-0000-0000-0000-000000000501"), "secret", 1, "windows_publisher", "windows");

        public Task<DeviceCredentialStorageResult> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(DeviceCredentialStorageResult.Success(credential));

        public Task<DeviceCredentialStorageResult> SaveAsync(DeviceCredential credential, CancellationToken cancellationToken = default) =>
            Task.FromResult(DeviceCredentialStorageResult.Success());

        public Task<DeviceCredentialStorageResult> DeleteAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(DeviceCredentialStorageResult.Success());
    }

    private sealed class FakeSignaling : ISignalingClient
    {
        public SignalingConnectionState State => SignalingConnectionState.Connected;
        public event Action<SignalingConnectionState>? StateChanged;
        public event Action<int>? ReconnectAttempting { add { } remove { } }
        public event Action<SignalingCloseReason>? Closed { add { } remove { } }

        public Task ConnectAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            StateChanged?.Invoke(SignalingConnectionState.Connected);
            return Task.CompletedTask;
        }

        public Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendAsync(SignalingMessageEnvelope message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakePairings : IPairingApiClient
    {
        public Task<CreatePairingChallengeResponse> CreatePairingChallengeAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PairingResponse>> ListPairingsAsync(Guid deviceId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PairingResponse>>([]);

        public Task RevokePairingAsync(Guid pairingId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
