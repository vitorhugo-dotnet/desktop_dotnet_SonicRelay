using SonicRelay.Windows.ApiClient.Authentication;
using SonicRelay.Windows.ApiClient.Devices;
using SonicRelay.Windows.ApiClient.Errors;
using SonicRelay.Windows.ApiClient.Sessions;
using SonicRelay.Windows.Audio;
using SonicRelay.Windows.Core.Authentication;
using SonicRelay.Windows.Core.Storage.DeviceIdentity;
using SonicRelay.Windows.Signaling;

namespace SonicRelay.Windows.Presentation;

public sealed class PublisherWorkflow : IAsyncDisposable
{
    private readonly IAuthApiClient auth;
    private readonly IDeviceApiClient devices;
    private readonly ISessionApiClient sessions;
    private readonly ISignalingClient signaling;
    private readonly IAudioCaptureService audio;
    private readonly string deviceName;
    private readonly IDeviceAccessTokenProvider deviceIdentity;
    private readonly IDeviceCredentialStore deviceCredentials;
    private readonly SemaphoreSlim operationLock = new(1, 1);
    private readonly object stateLock = new();
    private bool disposed;

    public PublisherWorkflow(
        IAuthApiClient auth,
        IDeviceApiClient devices,
        ISessionApiClient sessions,
        ISignalingClient signaling,
        IAudioCaptureService audio,
        string deviceName)
    {
        this.auth = auth ?? throw new ArgumentNullException(nameof(auth));
        this.devices = devices ?? throw new ArgumentNullException(nameof(devices));
        this.sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        this.signaling = signaling ?? throw new ArgumentNullException(nameof(signaling));
        this.audio = audio ?? throw new ArgumentNullException(nameof(audio));
        this.deviceName = string.IsNullOrWhiteSpace(deviceName) ? "Windows Publisher" : deviceName.Trim();
        deviceIdentity = null!;
        deviceCredentials = null!;
        signaling.StateChanged += OnSignalingStateChanged;
        audio.StateChanged += OnAudioStateChanged;
        audio.LevelChanged += OnAudioLevelChanged;
        State = new PublisherSnapshot { AudioDiagnostics = audio.Diagnostics };
    }

    public PublisherWorkflow(
        IDeviceAccessTokenProvider deviceIdentity,
        IDeviceCredentialStore deviceCredentials,
        ISessionApiClient sessions,
        ISignalingClient signaling,
        IAudioCaptureService audio)
    {
        this.deviceIdentity = deviceIdentity ?? throw new ArgumentNullException(nameof(deviceIdentity));
        this.deviceCredentials = deviceCredentials ?? throw new ArgumentNullException(nameof(deviceCredentials));
        this.sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        this.signaling = signaling ?? throw new ArgumentNullException(nameof(signaling));
        this.audio = audio ?? throw new ArgumentNullException(nameof(audio));
        auth = null!;
        devices = null!;
        deviceName = "Windows Publisher";
        signaling.StateChanged += OnSignalingStateChanged;
        audio.StateChanged += OnAudioStateChanged;
        audio.LevelChanged += OnAudioLevelChanged;
        State = new PublisherSnapshot { AudioDiagnostics = audio.Diagnostics };
    }

    public PublisherSnapshot State { get; private set; }
    public event Action<PublisherSnapshot>? StateChanged;

    public Task InitializeDeviceIdentityAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(async token =>
        {
            await deviceIdentity.GetAccessTokenAsync(cancellationToken: token);
            var stored = await deviceCredentials.LoadAsync(token);
            if (!stored.Succeeded || stored.Credential is null)
            {
                throw new InvalidOperationException("The device credential is unavailable after bootstrap.");
            }

            SetState(state => state with
            {
                IsAuthenticated = true,
                DeviceId = stored.Credential.DeviceId,
                DeviceName = Environment.MachineName
            }, "Publisher device identity is ready.");
        }, cancellationToken);

    public Task LoginAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email)) return SetValidationErrorAsync("Email is required.");
        if (string.IsNullOrWhiteSpace(password)) return SetValidationErrorAsync("Password is required.");
        // Only an explicit sign-in may blame the credentials; every other
        // operation's 401 means the stored session expired (see ExecuteAsync).
        return ExecuteAsync(
            token => SignInAndPrepareDeviceAsync(email.Trim(), password, token),
            cancellationToken,
            unauthorizedMessage: "Login failed. Check your email and password.");
    }

    public Task RegisterAsync(string email, string password, string confirmPassword, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email)) return SetValidationErrorAsync("Email is required.");
        if (string.IsNullOrWhiteSpace(password)) return SetValidationErrorAsync("Password is required.");
        if (string.IsNullOrWhiteSpace(confirmPassword)) return SetValidationErrorAsync("Confirm your password.");
        if (!string.Equals(password, confirmPassword, StringComparison.Ordinal))
            return SetValidationErrorAsync("Passwords do not match.");

        return ExecuteAsync(async token =>
        {
            try
            {
                await auth.RegisterAsync(new RegisterRequest(email.Trim(), password), token);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var message = ToRegisterFriendlyMessage(exception);
                SetState(state => state with { ErrorMessage = message }, $"Registration failed: {message}");
                return;
            }

            // ASP.NET Core Identity registration does not return tokens, so sign in with the
            // same credentials to reuse the existing device-preparation flow.
            try
            {
                await SignInAndPrepareDeviceAsync(email.Trim(), password, token);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                SetState(
                    state => state with { ErrorMessage = "Account created. Please sign in with your new email and password." },
                    "Account created, but automatic sign-in failed.");
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Restores a persisted session on startup. <see cref="IAuthApiClient.GetCurrentUserAsync"/>
    /// is authenticated, so the HTTP layer transparently refreshes an expired access
    /// token with the stored refresh token. A missing session or an invalid refresh
    /// token surfaces as <see cref="ApiErrorKind.Unauthorized"/>, which clears local
    /// auth and returns the user to sign-in; transient network/backend errors leave
    /// the app unauthenticated silently so the user can retry.
    /// </summary>
    public Task RestoreSessionAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(async token =>
        {
            try
            {
                await PrepareAuthenticatedStateAsync(token);
            }
            catch (ApiClientException exception) when (exception.Kind == ApiErrorKind.Unauthorized)
            {
                // No stored session, or the refresh token is no longer valid.
                try { await auth.LogoutAsync(token); } catch { }
                SetState(_ => new PublisherSnapshot { AudioDiagnostics = audio.Diagnostics });
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Backend/network unreachable at startup: stay signed out without an
                // alarming banner; the user can retry once connectivity returns.
                SetState(_ => new PublisherSnapshot { AudioDiagnostics = audio.Diagnostics });
            }
        }, cancellationToken);

    private async Task SignInAndPrepareDeviceAsync(string email, string password, CancellationToken cancellationToken)
    {
        await auth.LoginAsync(new LoginRequest(email, password), cancellationToken);
        await PrepareAuthenticatedStateAsync(cancellationToken);
    }

    // Confirms the signed-in user via /auth/me and ensures this machine's publisher
    // device exists (reusing it when present), then publishes the authenticated
    // snapshot. Shared by fresh sign-in and startup session restore.
    private async Task PrepareAuthenticatedStateAsync(CancellationToken cancellationToken)
    {
        var user = await auth.GetCurrentUserAsync(cancellationToken);
        var available = await devices.GetDevicesAsync(cancellationToken);
        // Match the publisher device for *this* machine by its hostname. The same account
        // can be signed in on several machines, so reusing any windows_publisher device
        // would surface another machine's name; register a device for this machine instead.
        var device = available.FirstOrDefault(item =>
            item.Type == "windows_publisher"
            && item.Platform == "windows"
            && !item.Revoked
            && string.Equals(item.Name, deviceName, StringComparison.Ordinal))
            ?? await devices.RegisterWindowsPublisherAsync(new RegisterDeviceRequest(deviceName, null), cancellationToken);
        SetState(state => state with
        {
            IsAuthenticated = true,
            UserDisplayName = user.DisplayName ?? user.Email,
            UserEmail = user.Email,
            DeviceId = device.Id,
            DeviceName = device.Name
        }, "Signed in and publisher device is ready.");
    }

    public Task CreateSessionAsync(CancellationToken cancellationToken = default)
    {
        if (!State.IsAuthenticated || State.DeviceId is null)
            return SetValidationErrorAsync("Initialize this publisher device before creating a session.");
        if (State.SessionId is not null) return SetValidationErrorAsync("A publisher session is already active.");
        return ExecuteAsync(async token =>
        {
            var session = await sessions.CreateSessionAsync(new CreateSessionRequest(), token);
            SetState(state => state with { SessionId = session.Id, SessionCode = session.Code, ViewerCount = 0 }, "Session created.");
            try
            {
                await signaling.ConnectAsync(session.Id.ToString("D"), token);
                await RefreshViewerCountCoreAsync(token);
            }
            catch
            {
                try { await sessions.EndSessionAsync(session.Id, CancellationToken.None); } catch { }
                SetState(state => state with { SessionId = null, SessionCode = null, ViewerCount = 0 });
                throw;
            }
        }, cancellationToken);
    }

    public Task RefreshViewerCountAsync(CancellationToken cancellationToken = default) =>
        State.SessionId is null ? Task.CompletedTask : ExecuteAsync(RefreshViewerCountCoreAsync, cancellationToken);

    /// <summary>
    /// Re-establishes signaling for the current session without recreating the
    /// session or device, so the tray "Reconnect signaling" action never spawns a
    /// duplicate session. No-op guard when there is no active session.
    /// </summary>
    public Task ReconnectSignalingAsync(CancellationToken cancellationToken = default)
    {
        if (State.SessionId is null)
            return SetValidationErrorAsync("There is no active session to reconnect.");
        return ExecuteAsync(async token =>
        {
            var sessionId = State.SessionId.Value;
            await signaling.CloseAsync(token);
            await signaling.ConnectAsync(sessionId.ToString("D"), token);
            await RefreshViewerCountCoreAsync(token);
        }, cancellationToken);
    }

    public Task StartAudioAsync(CancellationToken cancellationToken = default)
    {
        if (State.SessionId is null || State.SignalingState != SignalingConnectionState.Connected)
            return SetValidationErrorAsync("Create a session and connect signaling before starting audio.");
        return ExecuteAsync(async token => { await audio.StartAsync(token); AddLog("Audio capture started."); }, cancellationToken);
    }

    public Task StopAudioAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(async token => { await audio.StopAsync(token); AddLog("Audio capture stopped."); }, cancellationToken);

    public Task EndSessionAsync(CancellationToken cancellationToken = default)
    {
        if (State.SessionId is null) return SetValidationErrorAsync("There is no active session to end.");
        return ExecuteAsync(async token =>
        {
            var sessionId = State.SessionId.Value;
            if (audio.State is not AudioCaptureState.Stopped) await audio.StopAsync(token);
            await signaling.CloseAsync(token);
            await sessions.EndSessionAsync(sessionId, token);
            SetState(state => state with { SessionId = null, SessionCode = null, ViewerCount = 0 }, "Session ended.");
        }, cancellationToken);
    }

    public Task LogoutAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(async token =>
        {
            // Tear down any live session locally before dropping credentials, then reset
            // to a fresh unauthenticated snapshot so the UI returns to the sign-in state.
            if (State.SessionId is { } sessionId)
            {
                if (audio.State is not AudioCaptureState.Stopped) await audio.StopAsync(token);
                await signaling.CloseAsync(token);
                try { await sessions.EndSessionAsync(sessionId, token); } catch { }
            }
            await auth.LogoutAsync(token);
            SetState(_ => new PublisherSnapshot { AudioDiagnostics = audio.Diagnostics }, "Signed out.");
        }, cancellationToken);

    /// <summary>
    /// Permanently deletes the signed-in account. Tears down any live session, calls the
    /// server's self-service deletion endpoint, then resets to a fresh unauthenticated
    /// snapshot so the UI returns to the sign-in state.
    /// </summary>
    public Task DeleteAccountAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(async token =>
        {
            if (State.SessionId is { } sessionId)
            {
                if (audio.State is not AudioCaptureState.Stopped) await audio.StopAsync(token);
                await signaling.CloseAsync(token);
                try { await sessions.EndSessionAsync(sessionId, token); } catch { }
            }
            await auth.DeleteAccountAsync(token);
            SetState(_ => new PublisherSnapshot { AudioDiagnostics = audio.Diagnostics }, "Account deleted.");
        }, cancellationToken);

    private async Task RefreshViewerCountCoreAsync(CancellationToken cancellationToken)
    {
        if (State.SessionId is not { } id) return;
        var active = await sessions.GetActiveSessionsAsync(cancellationToken);
        var current = active.FirstOrDefault(item => item.Id == id);
        SetState(state => state with { ViewerCount = current?.ViewerCount ?? 0 });
    }

    /// <summary>
    /// Serializes an operation, publishing busy/error state around it.
    /// <paramref name="unauthorizedMessage"/> is the message shown when the
    /// operation fails with <see cref="ApiErrorKind.Unauthorized"/>. In the active
    /// device-identity flow, a surviving 401 means the credential was rejected and
    /// the publisher must bootstrap again rather than fall back to account login.
    /// </summary>
    private async Task ExecuteAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken,
        string? unauthorizedMessage = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            SetState(state => state with { IsBusy = true, ErrorMessage = null });
            await operation(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetState(state => state with { ErrorMessage = "The operation was cancelled." });
        }
        catch (Exception exception)
        {
            var message = ToFriendlyMessage(exception, unauthorizedMessage);
            if (unauthorizedMessage is null && exception is ApiClientException { Kind: ApiErrorKind.Unauthorized })
            {
                SetState(state => state with
                {
                    IsAuthenticated = false,
                    UserDisplayName = null,
                    UserEmail = null,
                    DeviceId = null,
                    DeviceName = null,
                    ErrorMessage = message
                }, $"Error: {message}");
            }
            else
            {
                SetState(state => state with { ErrorMessage = message }, $"Error: {message}");
            }
        }
        finally
        {
            SetState(state => state with { IsBusy = false });
            operationLock.Release();
        }
    }

    private Task SetValidationErrorAsync(string message)
    {
        SetState(state => state with { ErrorMessage = message }, $"Validation: {message}");
        return Task.CompletedTask;
    }

    private string ToFriendlyMessage(Exception exception, string? unauthorizedMessage) => exception switch
    {
        ApiClientException api => api.Kind switch
        {
            ApiErrorKind.Unauthorized => unauthorizedMessage ?? (deviceIdentity is null
                ? "Your session expired. Sign in again."
                : "The publisher device is no longer authorized. Restart to bootstrap it again."),
            ApiErrorKind.NetworkUnavailable => "The backend network is unavailable. Check the URL and connection.",
            ApiErrorKind.BackendUnavailable => "The backend is unavailable. Try again shortly.",
            _ => api.Message
        },
        AudioCaptureException audioException => audioException.Message,
        _ => exception.Message
    };

    private static string ToRegisterFriendlyMessage(Exception exception) => exception switch
    {
        ApiClientException { Kind: ApiErrorKind.NetworkUnavailable } => "The backend network is unavailable. Check the URL and connection.",
        ApiClientException { Kind: ApiErrorKind.BackendUnavailable } => "The backend is unavailable. Try again shortly.",
        ApiClientException { Kind: ApiErrorKind.Conflict } => "That email is already registered. Try signing in instead.",
        ApiClientException api => ClassifyRegisterValidation(api.Message),
        _ => exception.Message
    };

    private static string ClassifyRegisterValidation(string detail)
    {
        if (Mentions(detail, "already taken") || Mentions(detail, "DuplicateEmail") || Mentions(detail, "DuplicateUserName"))
            return "That email is already registered. Try signing in instead.";
        if (Mentions(detail, "InvalidEmail") || Mentions(detail, "is invalid"))
            return "Enter a valid email address.";
        if (Mentions(detail, "Password"))
            return string.IsNullOrWhiteSpace(detail) ? "Choose a stronger password." : detail;
        return string.IsNullOrWhiteSpace(detail) ? "Registration failed. Check your details and try again." : detail;
    }

    private static bool Mentions(string value, string term) => value.Contains(term, StringComparison.OrdinalIgnoreCase);

    private void OnSignalingStateChanged(SignalingConnectionState state) => SetState(current => current with { SignalingState = state }, $"Signaling: {state}.");
    private void OnAudioStateChanged(AudioCaptureState state) => SetState(current => current with { AudioState = state, AudioDiagnostics = audio.Diagnostics });
    private void OnAudioLevelChanged(AudioLevelSnapshot _) => SetState(current => current with { AudioDiagnostics = audio.Diagnostics });

    private void AddLog(string message) => SetState(state => state, message);

    /// <summary>
    /// Applies <paramref name="update"/> to the current snapshot atomically.
    /// Signaling and audio events fire from background threads while workflow
    /// operations mutate state, so the read-modify-write must happen under a
    /// lock — otherwise a concurrent event can publish a snapshot captured
    /// before an operation's write and silently revert it (the classic
    /// symptom was IsAuthenticated flipping back to false right after a
    /// successful sign-in).
    /// </summary>
    private void SetState(Func<PublisherSnapshot, PublisherSnapshot> update, string? logMessage = null)
    {
        PublisherSnapshot next;
        lock (stateLock)
        {
            next = update(State);
            if (logMessage is not null)
            {
                var logs = next.ActivityLog.Append($"{DateTimeOffset.Now:HH:mm:ss} {logMessage}").TakeLast(100).ToArray();
                next = next with { ActivityLog = logs };
            }
            State = next;
        }
        StateChanged?.Invoke(next);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        signaling.StateChanged -= OnSignalingStateChanged;
        audio.StateChanged -= OnAudioStateChanged;
        audio.LevelChanged -= OnAudioLevelChanged;
        if (audio.State is not AudioCaptureState.Stopped)
        {
            try { await audio.StopAsync(); } catch { }
        }
        try { await signaling.CloseAsync(); } catch { }
        await audio.DisposeAsync();
        await signaling.DisposeAsync();
        operationLock.Dispose();
    }
}
