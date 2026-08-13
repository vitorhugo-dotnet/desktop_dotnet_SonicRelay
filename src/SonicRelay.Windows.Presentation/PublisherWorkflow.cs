using SonicRelay.Windows.ApiClient.Errors;
using SonicRelay.Windows.ApiClient.Pairing;
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
    private readonly IPairingApiClient pairings;
    private readonly SemaphoreSlim operationLock = new(1, 1);
    private readonly object stateLock = new();
    private bool disposed;

    public PublisherWorkflow(
        IDeviceAccessTokenProvider deviceIdentity,
        IDeviceCredentialStore deviceCredentials,
        ISessionApiClient sessions,
        ISignalingClient signaling,
        IAudioCaptureService audio,
        IPairingApiClient pairings)
    {
        this.deviceIdentity = deviceIdentity ?? throw new ArgumentNullException(nameof(deviceIdentity));
        this.deviceCredentials = deviceCredentials ?? throw new ArgumentNullException(nameof(deviceCredentials));
        this.sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        this.signaling = signaling ?? throw new ArgumentNullException(nameof(signaling));
        this.audio = audio ?? throw new ArgumentNullException(nameof(audio));
        this.pairings = pairings ?? throw new ArgumentNullException(nameof(pairings));
        signaling.StateChanged += OnSignalingStateChanged;
        signaling.Closed += OnSignalingClosed;
        signaling.ReconnectAttempting += OnSignalingReconnectAttempting;
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
            try
            {
                await sessions.EndSessionAsync(sessionId, token);
            }
            catch (ApiClientException exception)
            {
                // The backend may have discarded the session already (e.g. it expired). Ending
                // must always release the local session, otherwise the only way to create a new
                // one is restarting the app.
                AddLog($"The backend could not end the session ({exception.Message}); releasing it locally.");
            }
            SetState(state => state with { SessionId = null, SessionCode = null, ViewerCount = 0 }, "Session ended.");
        }, cancellationToken);
    }

    /// <summary>
    /// Unpairs this device: tears down any active session, revokes this device's active
    /// pairings on the backend, then forgets the local identity so a fresh one — and a fresh
    /// pairing challenge — can be bootstrapped without restarting.
    ///
    /// Revocation comes first because clearing the identity re-bootstraps into a *new* DeviceId,
    /// which would leave every existing pairing row pointing at a publisher that no longer
    /// exists — the viewer would keep reporting "invalid code" for a perfectly good code.
    ///
    /// A failed revocation does not block the reset: the whole point of this action is
    /// recovering from a rejected or unreachable credential, so an unreachable backend must not
    /// trap the user. The failure is logged instead of swallowed.
    /// </summary>
    public Task UnpairAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(async token =>
        {
            if (State.SessionId is { } sessionId)
            {
                if (audio.State is not AudioCaptureState.Stopped) await audio.StopAsync(token);
                await signaling.CloseAsync(token);
                try { await sessions.EndSessionAsync(sessionId, token); } catch { }
            }

            if (State.DeviceId is { } deviceId)
            {
                try
                {
                    var active = await pairings.ListPairingsAsync(deviceId, token);
                    var revoked = 0;
                    var failed = 0;
                    foreach (var pairing in active.Where(x => x.Status == "active"))
                    {
                        try
                        {
                            await pairings.RevokePairingAsync(pairing.PairingId, token);
                            revoked++;
                        }
                        catch (Exception exception) when (exception is not OperationCanceledException)
                        {
                            failed++;
                        }
                    }

                    if (failed > 0)
                    {
                        AddLog($"Pairings could not be fully revoked: {revoked} revoked, {failed} could not be revoked.");
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    AddLog($"Pairings could not be revoked: {exception.Message}");
                }
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
            }, "Device unpaired.");
        }, cancellationToken);

    private async Task RefreshViewerCountCoreAsync(CancellationToken cancellationToken)
    {
        if (State.SessionId is not { } id) return;
        var active = await sessions.GetActiveSessionsAsync(cancellationToken);
        var current = active.FirstOrDefault(item => item.Id == id);
        var viewerCount = current?.ViewerCount ?? 0;
        var changed = viewerCount != State.ViewerCount;
        SetState(state => state with { ViewerCount = viewerCount },
            changed ? $"Viewers connected: {viewerCount}." : null);
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
            if (exception is SignalingSessionGoneException)
            {
                // The backend no longer knows this session, so holding on to its id would keep
                // "Create session" disabled forever. Release it so the user can start over
                // without restarting the app.
                SetState(state => state with
                {
                    SessionId = null,
                    SessionCode = null,
                    ViewerCount = 0,
                    ErrorMessage = message
                }, $"Error: {message}");
            }
            else if (exception is ApiClientException { Kind: ApiErrorKind.Unauthorized })
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
        SignalingSessionGoneException =>
            "The session no longer exists on the backend. It was released — create a new session to continue.",
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

    /// <summary>
    /// A session the backend reports gone (or ended) is released immediately so the UI leaves
    /// the faulted state and "Create session" becomes available again without a restart.
    /// </summary>
    private void OnSignalingClosed(SignalingCloseReason reason)
    {
        switch (reason)
        {
            case SignalingCloseReason.SessionGone:
                SetState(state => state with { SessionId = null, SessionCode = null, ViewerCount = 0 },
                    "The session no longer exists on the backend. Create a new session to continue.");
                break;
            case SignalingCloseReason.SessionEnded:
                SetState(state => state with { SessionId = null, SessionCode = null, ViewerCount = 0 },
                    "The session was ended by the backend.");
                break;
            case SignalingCloseReason.ReconnectExhausted:
                AddLog("Signaling gave up reconnecting. Use Retry to reconnect.");
                break;
        }
    }

    private void OnSignalingReconnectAttempting(int attempt) =>
        AddLog($"Signaling: reconnect attempt {attempt}.");
    private void OnAudioStateChanged(AudioCaptureState state) => SetState(
        current => current with { AudioState = state, AudioDiagnostics = audio.Diagnostics },
        $"Audio capture: {state}.");
    private void OnAudioLevelChanged(AudioLevelSnapshot _) => SetState(current => current with { AudioDiagnostics = audio.Diagnostics });

    /// <summary>Appends a line to the technical console's event log without changing state.</summary>
    public void LogActivity(string message) => AddLog(message);

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
        signaling.Closed -= OnSignalingClosed;
        signaling.ReconnectAttempting -= OnSignalingReconnectAttempting;
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
