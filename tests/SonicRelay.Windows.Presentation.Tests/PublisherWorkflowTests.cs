using SonicRelay.Windows.ApiClient.Authentication;
using SonicRelay.Windows.ApiClient.Devices;
using SonicRelay.Windows.ApiClient.Errors;
using SonicRelay.Windows.ApiClient.Sessions;
using SonicRelay.Windows.Audio;
using SonicRelay.Windows.Core.Authentication;
using SonicRelay.Windows.Core.Storage;
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

    [Theory]
    [InlineData("", "password", "Email is required.")]
    [InlineData("user@example.com", "", "Password is required.")]
    public async Task LoginRejectsRequiredFields(string email, string password, string expected)
    {
        await using var fixture = new Fixture();
        await fixture.Workflow.LoginAsync(email, password);
        Assert.Equal(expected, fixture.Workflow.State.ErrorMessage);
        Assert.False(fixture.Auth.LoginCalled);
    }

    [Theory]
    [InlineData("", "password", "password", "Email is required.")]
    [InlineData("user@example.com", "", "password", "Password is required.")]
    [InlineData("user@example.com", "password", "different", "Passwords do not match.")]
    public async Task RegisterRejectsInvalidInput(string email, string password, string confirm, string expected)
    {
        await using var fixture = new Fixture();
        await fixture.Workflow.RegisterAsync(email, password, confirm);
        Assert.Equal(expected, fixture.Workflow.State.ErrorMessage);
        Assert.False(fixture.Auth.RegisterCalled);
        Assert.False(fixture.Auth.LoginCalled);
    }

    [Fact]
    public async Task SuccessfulRegisterSignsInAfterwards()
    {
        await using var fixture = new Fixture();

        await fixture.Workflow.RegisterAsync("user@example.com", "password", "password");

        Assert.Equal(new[] { "register", "login" }, fixture.Auth.Calls);
        Assert.True(fixture.Workflow.State.IsAuthenticated);
    }

    [Fact]
    public async Task SuccessfulRegisterPreparesPublisherDevice()
    {
        await using var fixture = new Fixture();

        await fixture.Workflow.RegisterAsync("user@example.com", "password", "password");

        Assert.True(fixture.Devices.RegisterCalled);
        Assert.Equal(fixture.Devices.LastRegisteredId, fixture.Workflow.State.DeviceId);
    }

    [Fact]
    public async Task RegisterFailureIsFriendlyAndSkipsLogin()
    {
        await using var fixture = new Fixture();
        fixture.Auth.RegisterException = new ApiClientException(ApiErrorKind.Validation, "Email 'x' is already taken.");

        await fixture.Workflow.RegisterAsync("user@example.com", "password", "password");

        Assert.False(fixture.Auth.LoginCalled);
        Assert.False(fixture.Workflow.State.IsAuthenticated);
        Assert.Equal("That email is already registered. Try signing in instead.", fixture.Workflow.State.ErrorMessage);
    }

    [Fact]
    public async Task RegisterWithFailedAutoLoginAsksUserToSignInManually()
    {
        await using var fixture = new Fixture();
        fixture.Auth.Exception = new ApiClientException(ApiErrorKind.Unauthorized, "nope");

        await fixture.Workflow.RegisterAsync("user@example.com", "password", "password");

        Assert.True(fixture.Auth.RegisterCalled);
        Assert.True(fixture.Auth.LoginCalled);
        Assert.False(fixture.Workflow.State.IsAuthenticated);
        Assert.Equal("Account created. Please sign in with your new email and password.", fixture.Workflow.State.ErrorMessage);
    }

    [Fact]
    public async Task LoginReusesThisMachinesPublisherDevice()
    {
        await using var fixture = new Fixture();
        // A device already registered for this machine (same hostname as the fixture).
        var device = Device("Test PC", revoked: false);
        fixture.Devices.Items.Add(device);

        await fixture.Workflow.LoginAsync("user@example.com", "password");

        Assert.True(fixture.Workflow.State.IsAuthenticated);
        Assert.Equal(device.Id, fixture.Workflow.State.DeviceId);
        Assert.False(fixture.Devices.RegisterCalled);
    }

    [Fact]
    public async Task LoginRegistersASeparateDeviceWhenOnlyAnotherMachinesExists()
    {
        await using var fixture = new Fixture();
        // The account is already used on a different machine; that device must not be
        // adopted here — this machine registers its own so the name is correct.
        fixture.Devices.Items.Add(Device("Other PC", revoked: false));

        await fixture.Workflow.LoginAsync("user@example.com", "password");

        Assert.True(fixture.Devices.RegisterCalled);
        Assert.Equal("Test PC", fixture.Workflow.State.DeviceName);
        Assert.Equal(fixture.Devices.LastRegisteredId, fixture.Workflow.State.DeviceId);
    }

    [Fact]
    public async Task LogoutTearsDownSessionClearsAuthAndResetsState()
    {
        await using var fixture = new Fixture();
        await fixture.Workflow.LoginAsync("user@example.com", "password");
        await fixture.Workflow.CreateSessionAsync();
        await fixture.Workflow.StartAudioAsync();

        await fixture.Workflow.LogoutAsync();

        Assert.True(fixture.Auth.LogoutCalled);
        Assert.True(fixture.Audio.StopCalled);
        Assert.True(fixture.Signaling.CloseCalled);
        Assert.Equal(fixture.Sessions.Created.Id, fixture.Sessions.EndedId);
        Assert.False(fixture.Workflow.State.IsAuthenticated);
        Assert.Null(fixture.Workflow.State.SessionId);
        Assert.Null(fixture.Workflow.State.DeviceId);
    }

    [Fact]
    public async Task DeleteAccountTearsDownSessionCallsServerAndResetsState()
    {
        await using var fixture = new Fixture();
        await fixture.Workflow.LoginAsync("user@example.com", "password");
        await fixture.Workflow.CreateSessionAsync();
        await fixture.Workflow.StartAudioAsync();

        await fixture.Workflow.DeleteAccountAsync();

        Assert.True(fixture.Auth.DeleteAccountCalled);
        Assert.True(fixture.Audio.StopCalled);
        Assert.True(fixture.Signaling.CloseCalled);
        Assert.Equal(fixture.Sessions.Created.Id, fixture.Sessions.EndedId);
        Assert.False(fixture.Workflow.State.IsAuthenticated);
        Assert.Null(fixture.Workflow.State.SessionId);
        Assert.Null(fixture.Workflow.State.DeviceId);
    }

    [Fact]
    public async Task CreateSessionConnectsSignalingAndExposesCode()
    {
        await using var fixture = new Fixture();
        await fixture.Workflow.LoginAsync("user@example.com", "password");

        await fixture.Workflow.CreateSessionAsync();

        Assert.Equal("ABC123", fixture.Workflow.State.SessionCode);
        Assert.Equal(SignalingConnectionState.Connected, fixture.Workflow.State.SignalingState);
        Assert.Equal(fixture.Sessions.Created.Id.ToString("D"), fixture.Signaling.SessionId);
        Assert.True(fixture.Workflow.State.CanStartAudio);
    }

    [Fact]
    public async Task CommandsAreGatedByPrerequisites()
    {
        await using var fixture = new Fixture();
        await fixture.Workflow.CreateSessionAsync();
        Assert.Equal("Initialize this publisher device before creating a session.", fixture.Workflow.State.ErrorMessage);

        await fixture.Workflow.StartAudioAsync();
        Assert.Equal("Create a session and connect signaling before starting audio.", fixture.Workflow.State.ErrorMessage);
        Assert.False(fixture.Audio.StartCalled);
    }

    [Fact]
    public async Task EndSessionStopsAudioClosesSignalingAndCallsBackend()
    {
        await using var fixture = new Fixture();
        await fixture.Workflow.LoginAsync("user@example.com", "password");
        await fixture.Workflow.CreateSessionAsync();
        await fixture.Workflow.StartAudioAsync();

        await fixture.Workflow.EndSessionAsync();

        Assert.True(fixture.Audio.StopCalled);
        Assert.True(fixture.Signaling.CloseCalled);
        Assert.Equal(fixture.Sessions.Created.Id, fixture.Sessions.EndedId);
        Assert.Null(fixture.Workflow.State.SessionId);
    }

    [Fact]
    public async Task FailureIsVisibleAndNotReportedAsSuccess()
    {
        await using var fixture = new Fixture();
        fixture.Auth.Exception = new HttpRequestException("network detail");

        await fixture.Workflow.LoginAsync("user@example.com", "password");

        Assert.False(fixture.Workflow.State.IsAuthenticated);
        Assert.Contains("network detail", fixture.Workflow.State.ErrorMessage);
    }

    [Fact]
    public async Task RejectedCredentialsBlameTheCredentials()
    {
        await using var fixture = new Fixture();
        fixture.Auth.Exception = new ApiClientException(ApiErrorKind.Unauthorized, "Unauthorized.");

        await fixture.Workflow.LoginAsync("user@example.com", "wrong");

        Assert.False(fixture.Workflow.State.IsAuthenticated);
        Assert.Equal("Login failed. Check your email and password.", fixture.Workflow.State.ErrorMessage);
    }

    [Fact]
    public async Task ExpiredSessionAfterSignInDropsAuthInsteadOfClaimingLoginFailure()
    {
        await using var fixture = new Fixture();
        await fixture.Workflow.LoginAsync("user@example.com", "password");
        Assert.True(fixture.Workflow.State.IsAuthenticated);

        // The refresh token died between sign-in and the next call: the 401 that
        // survives the HTTP layer's refresh retry must not read as a credential
        // failure while the UI still shows the signed-in email.
        fixture.Sessions.CreateException = new ApiClientException(ApiErrorKind.Unauthorized, "Unauthorized.");
        await fixture.Workflow.CreateSessionAsync();

        Assert.Equal("Your session expired. Sign in again.", fixture.Workflow.State.ErrorMessage);
        Assert.False(fixture.Workflow.State.IsAuthenticated);
        Assert.Null(fixture.Workflow.State.UserEmail);
        Assert.Null(fixture.Workflow.State.DeviceId);
    }

    [Fact]
    public async Task RestoreSessionAuthenticatesAndReusesExistingDevice()
    {
        await using var fixture = new Fixture();
        // A persisted session exists for this machine's already-registered device.
        fixture.Devices.Items.Add(Device("Test PC", revoked: false));

        await fixture.Workflow.RestoreSessionAsync();

        Assert.True(fixture.Workflow.State.IsAuthenticated);
        Assert.True(fixture.Auth.GetCurrentUserCalled);
        Assert.False(fixture.Devices.RegisterCalled); // reused, not re-registered
        Assert.Equal(fixture.Devices.Items[0].Id, fixture.Workflow.State.DeviceId);
        Assert.Null(fixture.Workflow.State.ErrorMessage);
    }

    [Fact]
    public async Task RestoreSessionWithoutValidSessionReturnsToLogin()
    {
        await using var fixture = new Fixture();
        fixture.Auth.GetCurrentUserException =
            new ApiClientException(ApiErrorKind.Unauthorized, "Unauthorized.");

        await fixture.Workflow.RestoreSessionAsync();

        Assert.False(fixture.Workflow.State.IsAuthenticated);
        Assert.True(fixture.Auth.LogoutCalled); // stored tokens cleared
        Assert.Null(fixture.Workflow.State.ErrorMessage); // no alarming banner
    }

    [Fact]
    public async Task RestoreSessionStaysSignedOutWithoutBannerOnNetworkError()
    {
        await using var fixture = new Fixture();
        fixture.Auth.GetCurrentUserException =
            new ApiClientException(ApiErrorKind.NetworkUnavailable, "The backend network is unavailable.");

        await fixture.Workflow.RestoreSessionAsync();

        Assert.False(fixture.Workflow.State.IsAuthenticated);
        Assert.Null(fixture.Workflow.State.ErrorMessage);
    }

    [Fact]
    public async Task ReconnectSignalingRejectsWithoutAnActiveSession()
    {
        await using var fixture = new Fixture();

        await fixture.Workflow.ReconnectSignalingAsync();

        Assert.Equal("There is no active session to reconnect.", fixture.Workflow.State.ErrorMessage);
        Assert.False(fixture.Signaling.CloseCalled);
    }

    [Fact]
    public async Task ReconnectSignalingReconnectsTheActiveSession()
    {
        await using var fixture = new Fixture();
        await fixture.Workflow.RegisterAsync("user@example.com", "password", "password");
        await fixture.Workflow.CreateSessionAsync();

        await fixture.Workflow.ReconnectSignalingAsync();

        Assert.True(fixture.Signaling.CloseCalled);
        Assert.Equal(SignalingConnectionState.Connected, fixture.Workflow.State.SignalingState);
        Assert.Equal(fixture.Sessions.Created.Id.ToString("D"), fixture.Signaling.SessionId);
    }

    private static DeviceResponse Device(string name, bool revoked) =>
        new(Guid.NewGuid(), name, "windows_publisher", "windows", null, true, revoked, null, DateTimeOffset.UtcNow);

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
        public PublisherWorkflow Workflow { get; }

        public DeviceIdentityFixture()
        {
            Workflow = new PublisherWorkflow(
                Identity,
                new FakeDeviceCredentialStore(Credential),
                Sessions,
                Signaling,
                Audio);
        }

        public ValueTask DisposeAsync() => Workflow.DisposeAsync();
    }

    private sealed class FakeDeviceIdentity : IDeviceAccessTokenProvider
    {
        public int TokenRequests { get; private set; }

        public Task<string> GetAccessTokenAsync(
            bool forceRefresh = false,
            CancellationToken cancellationToken = default)
        {
            TokenRequests++;
            return Task.FromResult("device-token");
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

    private sealed class Fixture : IAsyncDisposable
    {
        public FakeAuth Auth { get; } = new();
        public FakeDevices Devices { get; } = new();
        public FakeSessions Sessions { get; } = new();
        public FakeSignaling Signaling { get; } = new();
        public FakeAudio Audio { get; } = new();
        public PublisherWorkflow Workflow { get; }

        public Fixture()
        {
            Workflow = new PublisherWorkflow(Auth, Devices, Sessions, Signaling, Audio, "Test PC");
        }

        public ValueTask DisposeAsync() => Workflow.DisposeAsync();
    }

    private sealed class FakeAuth : IAuthApiClient
    {
        public List<string> Calls { get; } = [];
        public bool LoginCalled => Calls.Contains("login");
        public bool RegisterCalled => Calls.Contains("register");
        public Exception? Exception { get; set; }
        public Exception? RegisterException { get; set; }
        public Task<TokenSet> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add("login");
            return Exception is null
                ? Task.FromResult(new TokenSet("access", "refresh", DateTimeOffset.UtcNow.AddHours(1)))
                : Task.FromException<TokenSet>(Exception);
        }
        public Task RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add("register");
            return RegisterException is null ? Task.CompletedTask : Task.FromException(RegisterException);
        }
        public Task<TokenSet> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Exception? GetCurrentUserException { get; set; }
        public bool GetCurrentUserCalled { get; private set; }
        public Task<CurrentUserResponse> GetCurrentUserAsync(CancellationToken cancellationToken = default)
        {
            GetCurrentUserCalled = true;
            return GetCurrentUserException is null
                ? Task.FromResult(new CurrentUserResponse(Guid.NewGuid(), "user@example.com", "User", true, DateTimeOffset.UtcNow, null))
                : Task.FromException<CurrentUserResponse>(GetCurrentUserException);
        }
        public bool LogoutCalled => Calls.Contains("logout");
        public Task LogoutAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("logout");
            return Task.CompletedTask;
        }
        public bool DeleteAccountCalled => Calls.Contains("delete-account");
        public Task DeleteAccountAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("delete-account");
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDevices : IDeviceApiClient
    {
        public List<DeviceResponse> Items { get; } = [];
        public bool RegisterCalled { get; private set; }
        public Guid? LastRegisteredId { get; private set; }
        public Task<IReadOnlyList<DeviceResponse>> GetDevicesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DeviceResponse>>(Items);
        public Task<DeviceResponse> RegisterWindowsPublisherAsync(RegisterDeviceRequest request, CancellationToken cancellationToken = default)
        {
            RegisterCalled = true;
            var device = Device(request.Name, revoked: false);
            LastRegisteredId = device.Id;
            return Task.FromResult(device);
        }
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
