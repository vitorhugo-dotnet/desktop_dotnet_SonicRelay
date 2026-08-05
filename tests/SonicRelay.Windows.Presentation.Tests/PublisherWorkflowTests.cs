using SonicRelay.Windows.ApiClient.Errors;
using SonicRelay.Windows.ApiClient.Pairing;
using SonicRelay.Windows.ApiClient.Sessions;
using SonicRelay.Windows.Audio;
using SonicRelay.Windows.Core.Authentication;
using SonicRelay.Windows.Core.Storage.DeviceIdentity;
using SonicRelay.Windows.Signaling;

namespace SonicRelay.Windows.Presentation.Tests;

public sealed class PublisherWorkflowTests
{
    [Fact]
    public async Task Device_identity_startup_requests_token_and_exposes_persisted_device()
    {
        await using var fixture = new DeviceIdentityFixture();

        await fixture.Workflow.InitializeDeviceIdentityAsync();

        Assert.Equal(1, fixture.Identity.TokenRequests);
        Assert.True(fixture.Workflow.State.IsAuthenticated);
        Assert.Equal(fixture.Credential.DeviceId, fixture.Workflow.State.DeviceId);
        Assert.True(fixture.Workflow.State.CanCreateSession);
    }

    [Fact]
    public async Task Device_identity_workflow_creates_and_reconnects_session_with_session_id_only()
    {
        await using var fixture = new DeviceIdentityFixture();
        await fixture.Workflow.InitializeDeviceIdentityAsync();

        await fixture.Workflow.CreateSessionAsync();
        await fixture.Workflow.ReconnectSignalingAsync();

        Assert.Null(fixture.Sessions.LastCreateRequest.MaxViewers);
        Assert.Equal(fixture.Sessions.Created.Id.ToString("D"), fixture.Signaling.SessionId);
        Assert.True(fixture.Signaling.CloseCalled);
    }

    [Fact]
    public async Task CreateSessionConnectsSignalingAndExposesCode()
    {
        await using var fixture = new DeviceIdentityFixture();
        await fixture.Workflow.InitializeDeviceIdentityAsync();

        await fixture.Workflow.CreateSessionAsync();

        Assert.Equal("ABC123", fixture.Workflow.State.SessionCode);
        Assert.Equal(SignalingConnectionState.Connected, fixture.Workflow.State.SignalingState);
        Assert.Equal(fixture.Sessions.Created.Id.ToString("D"), fixture.Signaling.SessionId);
        Assert.True(fixture.Workflow.State.CanStartAudio);
    }

    [Fact]
    public async Task CommandsAreGatedByPrerequisites()
    {
        await using var fixture = new DeviceIdentityFixture();

        // Device identity was never initialized, so there is no session yet.
        await fixture.Workflow.CreateSessionAsync();
        Assert.Equal("Initialize this publisher device before creating a session.", fixture.Workflow.State.ErrorMessage);

        await fixture.Workflow.StartAudioAsync();
        Assert.Equal("Create a session and connect signaling before starting audio.", fixture.Workflow.State.ErrorMessage);
        Assert.False(fixture.Audio.StartCalled);
    }

    [Fact]
    public async Task EndSessionStopsAudioClosesSignalingAndCallsBackend()
    {
        await using var fixture = new DeviceIdentityFixture();
        await fixture.Workflow.InitializeDeviceIdentityAsync();
        await fixture.Workflow.CreateSessionAsync();
        await fixture.Workflow.StartAudioAsync();

        await fixture.Workflow.EndSessionAsync();

        Assert.True(fixture.Audio.StopCalled);
        Assert.True(fixture.Signaling.CloseCalled);
        Assert.Equal(fixture.Sessions.Created.Id, fixture.Sessions.EndedId);
        Assert.Null(fixture.Workflow.State.SessionId);
    }

    [Fact]
    public async Task LogoutEndsTheActiveSessionAndForgetsTheDeviceIdentity()
    {
        await using var fixture = new DeviceIdentityFixture();
        await fixture.Workflow.InitializeDeviceIdentityAsync();
        await fixture.Workflow.CreateSessionAsync();
        await fixture.Workflow.StartAudioAsync();

        await fixture.Workflow.UnpairAsync();

        Assert.True(fixture.Audio.StopCalled);
        Assert.True(fixture.Signaling.CloseCalled);
        Assert.Equal(fixture.Sessions.Created.Id, fixture.Sessions.EndedId);
        Assert.Equal(1, fixture.Identity.ResetCalls);
        Assert.False(fixture.Workflow.State.IsAuthenticated);
        Assert.Null(fixture.Workflow.State.DeviceId);
        Assert.Null(fixture.Workflow.State.SessionId);
        Assert.Null(fixture.Workflow.State.SessionCode);
    }

    [Fact]
    public async Task LogoutWithNoActiveSessionStillForgetsTheDeviceIdentity()
    {
        await using var fixture = new DeviceIdentityFixture();
        await fixture.Workflow.InitializeDeviceIdentityAsync();

        await fixture.Workflow.UnpairAsync();

        Assert.False(fixture.Signaling.CloseCalled);
        Assert.Equal(1, fixture.Identity.ResetCalls);
        Assert.False(fixture.Workflow.State.IsAuthenticated);
        Assert.Null(fixture.Workflow.State.DeviceId);
    }

    [Fact]
    public async Task Unpair_revokes_active_pairings_before_clearing_the_local_identity()
    {
        var pairings = new FakePairingApiClient
        {
            Pairings = [new PairingResponse(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "active", DateTimeOffset.UtcNow, null)]
        };
        var workflow = CreateWorkflow(pairings);
        await workflow.InitializeDeviceIdentityAsync();

        await workflow.UnpairAsync();

        Assert.Single(pairings.RevokedIds);
        Assert.False(workflow.State.IsAuthenticated);
        Assert.Null(workflow.State.DeviceId);
    }

    [Fact]
    public async Task Unpair_still_clears_the_local_identity_when_revocation_fails()
    {
        var pairings = new FakePairingApiClient { ThrowOnList = true };
        var workflow = CreateWorkflow(pairings);
        await workflow.InitializeDeviceIdentityAsync();

        await workflow.UnpairAsync();

        Assert.False(workflow.State.IsAuthenticated);
        Assert.Contains(workflow.State.ActivityLog, line => line.Contains("could not be revoked", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unpair_attempts_every_active_pairing_even_if_one_in_the_middle_fails()
    {
        var first = new PairingResponse(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "active", DateTimeOffset.UtcNow, null);
        var second = new PairingResponse(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "active", DateTimeOffset.UtcNow, null);
        var third = new PairingResponse(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "active", DateTimeOffset.UtcNow, null);
        var pairings = new FakePairingApiClient { Pairings = [first, second, third] };
        pairings.ThrowOnRevokeIds.Add(second.PairingId);
        var workflow = CreateWorkflow(pairings);
        await workflow.InitializeDeviceIdentityAsync();

        await workflow.UnpairAsync();

        // The middle revocation throwing must not skip the third pairing: both first and
        // third are still attempted (and succeed), only the middle one fails.
        Assert.Contains(first.PairingId, pairings.RevokedIds);
        Assert.Contains(third.PairingId, pairings.RevokedIds);
        Assert.DoesNotContain(second.PairingId, pairings.RevokedIds);
        Assert.Equal(2, pairings.RevokedIds.Count);

        // The local identity is still cleared regardless of the partial failure.
        Assert.False(workflow.State.IsAuthenticated);
        Assert.Null(workflow.State.DeviceId);

        // The log names the partial outcome rather than implying total success or total failure.
        Assert.Contains(workflow.State.ActivityLog, line =>
            line.Contains("could not be revoked", StringComparison.Ordinal) &&
            line.Contains('2', StringComparison.Ordinal) &&
            line.Contains('1', StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unpair_revokes_every_active_pairing_not_just_the_first()
    {
        var first = new PairingResponse(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "active", DateTimeOffset.UtcNow, null);
        var second = new PairingResponse(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "active", DateTimeOffset.UtcNow, null);
        var third = new PairingResponse(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "active", DateTimeOffset.UtcNow, null);
        var pairings = new FakePairingApiClient { Pairings = [first, second, third] };
        var workflow = CreateWorkflow(pairings);
        await workflow.InitializeDeviceIdentityAsync();

        await workflow.UnpairAsync();

        Assert.Equal(3, pairings.RevokedIds.Count);
        Assert.Contains(first.PairingId, pairings.RevokedIds);
        Assert.Contains(second.PairingId, pairings.RevokedIds);
        Assert.Contains(third.PairingId, pairings.RevokedIds);
    }

    [Fact]
    public async Task RejectedDeviceCredentialClearsDeviceIdentityInsteadOfClaimingSuccess()
    {
        await using var fixture = new DeviceIdentityFixture();
        await fixture.Workflow.InitializeDeviceIdentityAsync();
        Assert.True(fixture.Workflow.State.IsAuthenticated);

        // The device credential was revoked/rejected between bootstrap and the next call: a
        // surviving 401 must drop the local device identity so the publisher bootstraps again,
        // not silently keep showing the device as authorized.
        fixture.Sessions.CreateException = new ApiClientException(ApiErrorKind.Unauthorized, "Unauthorized.");
        await fixture.Workflow.CreateSessionAsync();

        Assert.Equal(
            "The publisher device is no longer authorized. Restart to bootstrap it again.",
            fixture.Workflow.State.ErrorMessage);
        Assert.False(fixture.Workflow.State.IsAuthenticated);
        Assert.Null(fixture.Workflow.State.DeviceId);
    }

    [Fact]
    public async Task ReconnectSignalingRejectsWithoutAnActiveSession()
    {
        await using var fixture = new DeviceIdentityFixture();

        await fixture.Workflow.ReconnectSignalingAsync();

        Assert.Equal("There is no active session to reconnect.", fixture.Workflow.State.ErrorMessage);
        Assert.False(fixture.Signaling.CloseCalled);
    }

    [Fact]
    public async Task ReconnectSignalingReconnectsTheActiveSession()
    {
        await using var fixture = new DeviceIdentityFixture();
        await fixture.Workflow.InitializeDeviceIdentityAsync();
        await fixture.Workflow.CreateSessionAsync();

        await fixture.Workflow.ReconnectSignalingAsync();

        Assert.True(fixture.Signaling.CloseCalled);
        Assert.Equal(SignalingConnectionState.Connected, fixture.Workflow.State.SignalingState);
        Assert.Equal(fixture.Sessions.Created.Id.ToString("D"), fixture.Signaling.SessionId);
    }

    private static PublisherWorkflow CreateWorkflow(IPairingApiClient pairings)
    {
        var credential = new DeviceCredential(
            Guid.Parse("00000000-0000-0000-0000-000000000501"),
            "device-secret",
            1,
            "windows_publisher",
            "windows");
        return new PublisherWorkflow(
            new FakeDeviceIdentity(),
            new FakeDeviceCredentialStore(credential),
            new FakeSessions(),
            new FakeSignaling(),
            new FakeAudio(),
            pairings);
    }

    private sealed class DeviceIdentityFixture : IAsyncDisposable
    {
        public DeviceCredential Credential { get; } = new(
            Guid.Parse("00000000-0000-0000-0000-000000000501"),
            "device-secret",
            1,
            "windows_publisher",
            "windows");
        public FakeDeviceIdentity Identity { get; } = new();
        public FakeSessions Sessions { get; } = new();
        public FakeSignaling Signaling { get; } = new();
        public FakeAudio Audio { get; } = new();
        public FakePairingApiClient Pairings { get; } = new();
        public PublisherWorkflow Workflow { get; }

        public DeviceIdentityFixture()
        {
            Workflow = new PublisherWorkflow(
                Identity,
                new FakeDeviceCredentialStore(Credential),
                Sessions,
                Signaling,
                Audio,
                Pairings);
        }

        public ValueTask DisposeAsync() => Workflow.DisposeAsync();
    }

    private sealed class FakeDeviceIdentity : IDeviceAccessTokenProvider
    {
        public int TokenRequests { get; private set; }
        public int ResetCalls { get; private set; }

        public Task<string> GetAccessTokenAsync(
            bool forceRefresh = false,
            CancellationToken cancellationToken = default)
        {
            TokenRequests++;
            return Task.FromResult("device-token");
        }

        public Task ResetAsync(CancellationToken cancellationToken = default)
        {
            ResetCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDeviceCredentialStore(DeviceCredential credential) : IDeviceCredentialStore
    {
        public Task<DeviceCredentialStorageResult> SaveAsync(
            DeviceCredential value,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(DeviceCredentialStorageResult.Success(value));

        public Task<DeviceCredentialStorageResult> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(DeviceCredentialStorageResult.Success(credential));

        public Task<DeviceCredentialStorageResult> DeleteAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(DeviceCredentialStorageResult.Success());
    }

    private sealed class FakeSessions : ISessionApiClient
    {
        public StreamSessionResponse Created { get; } = new(Guid.NewGuid(), Guid.NewGuid(), "active", 4, DateTimeOffset.UtcNow.AddMinutes(5), DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow, "ABC123");
        public Guid? EndedId { get; private set; }
        public Exception? CreateException { get; set; }
        public CreateSessionRequest LastCreateRequest { get; private set; } = new();
        public Task<StreamSessionResponse> CreateSessionAsync(CreateSessionRequest request, CancellationToken cancellationToken = default)
        {
            LastCreateRequest = request;
            return CreateException is null
                ? Task.FromResult(Created)
                : Task.FromException<StreamSessionResponse>(CreateException);
        }
        public Task<IReadOnlyList<ActiveSessionResponse>> GetActiveSessionsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ActiveSessionResponse>>([]);
        public Task<StreamSessionResponse> EndSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            EndedId = sessionId;
            return Task.FromResult(Created with { Id = sessionId, Status = "ended", EndedAt = DateTimeOffset.UtcNow });
        }
    }

    private sealed class FakeSignaling : ISignalingClient
    {
        public SignalingConnectionState State { get; private set; } = SignalingConnectionState.Disconnected;
        public string? SessionId { get; private set; }
        public bool CloseCalled { get; private set; }
        public event Action<SignalingConnectionState>? StateChanged;
        public event Action<int>? ReconnectAttempting;
        public event Action<SignalingCloseReason>? Closed;
        public Task ConnectAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            SessionId = sessionId;
            State = SignalingConnectionState.Connected;
            StateChanged?.Invoke(State);
            return Task.CompletedTask;
        }
        public Task SendAsync(SignalingMessageEnvelope message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CloseAsync(CancellationToken cancellationToken = default)
        {
            CloseCalled = true;
            State = SignalingConnectionState.Closed;
            StateChanged?.Invoke(State);
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakePairingApiClient : IPairingApiClient
    {
        public IReadOnlyList<PairingResponse> Pairings { get; set; } = [];
        public List<Guid> RevokedIds { get; } = [];
        public bool ThrowOnList { get; set; }
        public HashSet<Guid> ThrowOnRevokeIds { get; } = [];

        public Task<CreatePairingChallengeResponse> CreatePairingChallengeAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by PublisherWorkflow.");

        public Task<IReadOnlyList<PairingResponse>> ListPairingsAsync(Guid deviceId, CancellationToken cancellationToken = default) =>
            ThrowOnList
                ? Task.FromException<IReadOnlyList<PairingResponse>>(new InvalidOperationException("Pairing backend unreachable."))
                : Task.FromResult(Pairings);

        public Task RevokePairingAsync(Guid pairingId, CancellationToken cancellationToken = default)
        {
            if (ThrowOnRevokeIds.Contains(pairingId))
            {
                return Task.FromException(new InvalidOperationException("Revocation failed."));
            }

            RevokedIds.Add(pairingId);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAudio : IAudioCaptureService
    {
        public AudioCaptureState State { get; private set; } = AudioCaptureState.Stopped;
        public AudioCaptureDiagnostics Diagnostics => new(State, null, null, AudioLevelSnapshot.Silence, 0, 0);
        public string? PreferredDeviceId { get; private set; }
        public bool StartCalled { get; private set; }
        public bool StopCalled { get; private set; }
        public event Action<AudioCaptureState>? StateChanged;
        public event Action<AudioFrame>? FrameCaptured { add { } remove { } }
        public event Action<AudioLevelSnapshot>? LevelChanged { add { } remove { } }
        public Task StartAsync(CancellationToken cancellationToken = default) { StartCalled = true; State = AudioCaptureState.Capturing; StateChanged?.Invoke(State); return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken = default) { StopCalled = true; State = AudioCaptureState.Stopped; StateChanged?.Invoke(State); return Task.CompletedTask; }
        public Task PauseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResumeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public IReadOnlyList<AudioOutputDevice> GetOutputDevices() => [];
        public void SelectOutputDevice(string? deviceId) => PreferredDeviceId = deviceId;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
