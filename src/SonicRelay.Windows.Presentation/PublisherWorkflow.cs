using SonicRelay.Windows.ApiClient.Errors;
using SonicRelay.Windows.ApiClient.Sessions;
using SonicRelay.Windows.Audio;
using SonicRelay.Windows.Core.Authentication;
using SonicRelay.Windows.Core.Storage.DeviceIdentity;
using SonicRelay.Windows.Signaling;

namespace SonicRelay.Windows.Presentation;

public sealed class PublisherWorkflow : IAsyncDisposable
{
    private readonly ISessionApiClient sessions;
    private readonly ISignalingClient signaling;
    private readonly IAudioCaptureService audio;
    private readonly IDeviceAccessTokenProvider deviceIdentity;
    private readonly IDeviceCredentialStore deviceCredentials;
    private readonly SemaphoreSlim operationLock = new(1, 1);
    private readonly object stateLock = new();
    private bool disposed;

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

    /// <summary>
    /// Signs out of this publisher device: tears down any active session, then forgets
    /// the local device identity so the shell falls back to the pairing surface and a
    /// fresh device identity — and pairing challenge — can be bootstrapped without
    /// restarting the app. Needed because a stale or rejected device credential
    /// otherwise has no recovery path short of a restart (issue #26 follow-up).
    /// </summary>
    public Task LogoutAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(async token =>
        {
            if (State.SessionId is { } sessionId)
            {
                if (audio.State is not AudioCaptureState.Stopped) await audio.StopAsync(token);
                await signaling.CloseAsync(token);
                try { await sessions.EndSessionAsync(sessionId, token); } catch { }
            }

            await deviceIdentity.ResetAsync(token);

            SetState(state => state with
            {
                IsAuthenticated = false,
                DeviceId = null,
                DeviceName = null,
                SessionId = null,
                SessionCode = null,
                ViewerCount = 0
            }, "Signed out.");
        }, cancellationToken);

    private async Task RefreshViewerCountCoreAsync(CancellationToken cancellationToken)
    {
        if (State.SessionId is not { } id) return;
        var active = await sessions.GetActiveSessionsAsync(cancellationToken);
        var current = active.FirstOrDefault(item => item.Id == id);
        SetState(state => state with { ViewerCount = current?.ViewerCount ?? 0 });
    }

    /// <summary>
    /// Serializes an operation, publishing busy/error state around it. A surviving 401
    /// (the HTTP layer already retried with a fresh device-access token where safe) means
    /// the device credential was rejected — the local device identity is cleared so the
    /// publisher bootstraps again rather than silently retrying with a dead credential.
    /// </summary>
    private async Task ExecuteAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
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
            var message = ToFriendlyMessage(exception);
            if (exception is ApiClientException { Kind: ApiErrorKind.Unauthorized })
            {
                SetState(state => state with
                {
                    IsAuthenticated = false,
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

    private static string ToFriendlyMessage(Exception exception) => exception switch
    {
        ApiClientException api => api.Kind switch
        {
            ApiErrorKind.Unauthorized => "The publisher device is no longer authorized. Restart to bootstrap it again.",
            ApiErrorKind.NetworkUnavailable => "The backend network is unavailable. Check the URL and connection.",
            ApiErrorKind.BackendUnavailable => "The backend is unavailable. Try again shortly.",
            _ => api.Message
        },
        AudioCaptureException audioException => audioException.Message,
        _ => exception.Message
    };

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
