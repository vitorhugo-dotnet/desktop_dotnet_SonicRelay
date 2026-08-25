using SonicRelay.Windows.ApiClient.Errors;
using SonicRelay.Windows.ApiClient.Pairing;
using SonicRelay.Windows.ApiClient.Sessions;
using SonicRelay.Windows.Audio;
using SonicRelay.Windows.Core.Authentication;
using SonicRelay.Windows.Core.Storage.DeviceIdentity;
using SonicRelay.Windows.Signaling;
using SonicRelay.Windows.WebRtc;

namespace SonicRelay.Windows.Presentation;

public sealed class PublisherWorkflow : IAsyncDisposable
{
    private readonly ISessionApiClient sessions;
    private readonly ISignalingClient signaling;
    private readonly IAudioCaptureService audio;
    private readonly IDeviceAccessTokenProvider deviceIdentity;
    private readonly IDeviceCredentialStore deviceCredentials;
    private readonly IPairingApiClient pairings;

    /// <summary>
    /// Capture for two-way sessions. Null on a platform with no microphone backend, which is
    /// exactly what keeps the two-way controls off there instead of failing at the device.
    /// </summary>
    private readonly IAudioCaptureService? microphone;

    /// <summary>Playback for audio arriving from participants. Null when unsupported.</summary>
    private readonly AudioPlaybackService? playback;

    private readonly IWebRtcPublisher? webRtc;

    /// <summary>
    /// Publishes the created session's mode to whatever needs it before the first peer exists —
    /// the peer-connection factory decides `sendonly` vs `sendrecv` from it, and a connection's
    /// direction cannot be changed after it is built.
    /// </summary>
    private readonly Action<string>? onSessionModeChanged;

    private readonly SemaphoreSlim operationLock = new(1, 1);
    private readonly object stateLock = new();
    private bool disposed;

    public PublisherWorkflow(
        IDeviceAccessTokenProvider deviceIdentity,
        IDeviceCredentialStore deviceCredentials,
        ISessionApiClient sessions,
        ISignalingClient signaling,
        IAudioCaptureService audio,
        IPairingApiClient pairings,
        IWebRtcPublisher? webRtc = null,
        IAudioCaptureService? microphone = null,
        AudioPlaybackService? playback = null,
        Action<string>? onSessionModeChanged = null)
    {
        this.deviceIdentity = deviceIdentity ?? throw new ArgumentNullException(nameof(deviceIdentity));
        this.deviceCredentials = deviceCredentials ?? throw new ArgumentNullException(nameof(deviceCredentials));
        this.sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        this.signaling = signaling ?? throw new ArgumentNullException(nameof(signaling));
        this.audio = audio ?? throw new ArgumentNullException(nameof(audio));
        this.pairings = pairings ?? throw new ArgumentNullException(nameof(pairings));
        this.webRtc = webRtc;
        this.microphone = microphone;
        this.playback = playback;
        this.onSessionModeChanged = onSessionModeChanged;
        if (microphone is not null)
        {
            microphone.StateChanged += OnAudioStateChanged;
            microphone.LevelChanged += OnAudioLevelChanged;
        }
        if (playback is not null) playback.StateChanged += OnPlaybackStateChanged;
        if (webRtc is not null) webRtc.ParticipantAudioStateChanged += OnParticipantAudioStateChanged;
        signaling.StateChanged += OnSignalingStateChanged;
        signaling.Closed += OnSignalingClosed;
        signaling.ReconnectAttempting += OnSignalingReconnectAttempting;
        audio.StateChanged += OnAudioStateChanged;
        audio.LevelChanged += OnAudioLevelChanged;
        State = new PublisherSnapshot { AudioDiagnostics = audio.Diagnostics };
    }

    public PublisherSnapshot State { get; private set; }
    public event Action<PublisherSnapshot>? StateChanged;

    /// <summary>Whether this build can capture a microphone at all (two-way audio needs one).</summary>
    public bool SupportsTwoWayAudio => microphone is not null;

    /// <summary>
    /// The capture device the active session publishes from: the microphone in a two-way
    /// session, the system output mix otherwise. A session's mode never changes, so this never
    /// changes underneath a running capture.
    /// </summary>
    private IAudioCaptureService ActiveCapture =>
        State.IsDuplexSession && microphone is not null ? microphone : audio;

    /// <summary>
    /// Raised for every line appended to <see cref="PublisherSnapshot.ActivityLog"/>, independent
    /// of whether the tracked state fields (signaling/audio/session/error) changed. A persisted
    /// diagnostic writer that only reacts to state-signature changes silently drops messages like
    /// "Session ended." when the session ends without an accompanying signaling/audio state change
    /// — exactly the gap that made a real production incident undiagnosable after the fact, since
    /// the in-memory ActivityLog had the line but the exported log did not.
    /// </summary>
    public event Action<string>? LogAppended;

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

    /// <summary>
    /// Creates a session. <paramref name="duplex"/> opens it for two-way audio, which the
    /// backend fixes at creation and never changes; a build with no microphone refuses rather
    /// than creating a session it could only ever listen on.
    /// </summary>
    public Task CreateSessionAsync(bool duplex = false, CancellationToken cancellationToken = default)
    {
        if (!State.IsAuthenticated || State.DeviceId is null)
            return SetValidationErrorAsync("Initialize this publisher device before creating a session.");
        if (State.SessionId is not null) return SetValidationErrorAsync("A publisher session is already active.");
        if (duplex && microphone is null)
            return SetValidationErrorAsync("This device has no microphone support, so it cannot start a two-way session.");
        return ExecuteAsync(async token =>
        {
            var requestedMode = duplex ? SessionModes.Duplex : null;
            var session = await sessions.CreateSessionAsync(new CreateSessionRequest(Mode: requestedMode), token);
            // Trust the backend's echo over the request: it normalizes the value, and a build
            // talking to an older backend that ignores `mode` must not think it got duplex.
            var mode = session.Mode ?? requestedMode ?? SessionModes.Broadcast;
            onSessionModeChanged?.Invoke(mode);
            SetState(
                state => state with
                {
                    SessionId = session.Id,
                    SessionCode = session.Code,
                    ViewerCount = 0,
                    SessionMode = mode,
                    OutgoingAudioMuted = false,
                    Participants = [],
                },
                SessionModes.IsDuplex(mode) ? "Two-way session created." : "Session created.");
            try
            {
                await signaling.ConnectAsync(session.Id.ToString("D"), token);
                await RefreshViewerCountCoreAsync(token);
            }
            catch
            {
                try { await sessions.EndSessionAsync(session.Id, CancellationToken.None); } catch { }
                onSessionModeChanged?.Invoke(SessionModes.Broadcast);
                SetState(state => state with
                {
                    SessionId = null,
                    SessionCode = null,
                    ViewerCount = 0,
                    SessionMode = SessionModes.Broadcast,
                });
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
        return ExecuteAsync(async token =>
        {
            var source = ActiveCapture;
            await source.StartAsync(token);
            AddLog(ReferenceEquals(source, microphone)
                ? "Microphone capture started."
                : "Audio capture started.");
        }, cancellationToken);
    }

    public Task StopAudioAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(async token =>
        {
            // Both, unconditionally: whichever is idle no-ops, and stopping only the currently
            // active one would leave a device open if the mode changed between start and stop.
            await audio.StopAsync(token);
            if (microphone is not null) await microphone.StopAsync(token);
            AddLog("Audio capture stopped.");
        }, cancellationToken);

    /// <summary>
    /// Mutes or unmutes this device's outgoing audio. Nothing is renegotiated: the encoder
    /// stops being fed and the backend broadcasts the new state to the session.
    /// </summary>
    public Task SetOutgoingAudioMutedAsync(bool muted, CancellationToken cancellationToken = default)
    {
        if (webRtc is null || State.SessionId is null) return Task.CompletedTask;
        return ExecuteAsync(async token =>
        {
            await webRtc.SetOutgoingAudioMutedAsync(muted, token);
            SetState(state => state with { OutgoingAudioMuted = muted }, muted ? "Microphone muted." : "Microphone unmuted.");
        }, cancellationToken);
    }

    /// <summary>
    /// Grants or revokes one participant's permission to publish audio. Backend-only decision:
    /// the result arrives as a broadcast to the whole session, the affected participant
    /// included, so nothing local has to be told about it separately.
    /// </summary>
    public Task SetParticipantAudioPermissionAsync(
        Guid participantId,
        bool canSendAudio,
        CancellationToken cancellationToken = default)
    {
        if (State.SessionId is not { } sessionId)
            return SetValidationErrorAsync("There is no active session to change audio permissions in.");
        if (!State.IsDuplexSession)
            return SetValidationErrorAsync("Audio permissions only apply to two-way sessions.");
        return ExecuteAsync(async token =>
        {
            await sessions.SetAudioPermissionAsync(sessionId, participantId, canSendAudio, token);
            AddLog(canSendAudio
                ? "Granted a participant permission to talk."
                : "Revoked a participant's permission to talk.");
        }, cancellationToken);
    }

    /// <summary>
    /// Re-reads the session's participants over HTTP. The WebSocket broadcasts already keep
    /// this current; this exists for the moments there were none to hear — a signaling socket
    /// that reconnected, or a UI opened mid-session.
    /// </summary>
    public Task RefreshParticipantsAsync(CancellationToken cancellationToken = default)
    {
        if (State.SessionId is not { } sessionId || !State.IsDuplexSession) return Task.CompletedTask;
        return ExecuteAsync(async token =>
        {
            var response = await sessions.GetParticipantsAsync(sessionId, token);
            var participants = response.Participants
                .Select(participant => new ParticipantAudioState(
                    participant.ParticipantId.ToString("D"),
                    participant.Role,
                    response.Mode,
                    participant.AudioSendAllowed,
                    participant.CanSendAudio,
                    participant.CanReceiveAudio,
                    participant.AudioMuted))
                .ToArray();
            SetState(state => state with { Participants = participants });
        }, cancellationToken);
    }

    public Task EndSessionAsync(CancellationToken cancellationToken = default)
    {
        if (State.SessionId is null) return SetValidationErrorAsync("There is no active session to end.");
        return ExecuteAsync(async token =>
        {
            var sessionId = State.SessionId.Value;
            await StopAllCaptureAsync(token);
            if (playback is not null) await playback.StopAsync(token);
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
            onSessionModeChanged?.Invoke(SessionModes.Broadcast);
            SetState(
                state => state with
                {
                    SessionId = null,
                    SessionCode = null,
                    ViewerCount = 0,
                    SessionMode = SessionModes.Broadcast,
                    OutgoingAudioMuted = false,
                    Participants = [],
                },
                "Session ended.");
        }, cancellationToken);
    }

    private async Task StopAllCaptureAsync(CancellationToken cancellationToken)
    {
        if (audio.State is not AudioCaptureState.Stopped) await audio.StopAsync(cancellationToken);
        if (microphone is not null && microphone.State is not AudioCaptureState.Stopped)
        {
            await microphone.StopAsync(cancellationToken);
        }
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
                await StopAllCaptureAsync(token);
                if (playback is not null) await playback.StopAsync(token);
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
                ViewerCount = 0,
                SessionMode = SessionModes.Broadcast,
                Participants = [],
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
        current => current with { AudioState = state, AudioDiagnostics = ActiveCapture.Diagnostics },
        $"Audio capture: {state}.");
    private void OnAudioLevelChanged(AudioLevelSnapshot _) =>
        SetState(current => current with { AudioDiagnostics = ActiveCapture.Diagnostics });

    private void OnPlaybackStateChanged(AudioPlaybackState state) =>
        SetState(current => current with { PlaybackState = state }, $"Audio playback: {state}.");

    /// <summary>
    /// Folds one backend-published participant state into the snapshot. Replaces by id rather
    /// than appending, so a permission change updates the row instead of duplicating it.
    /// </summary>
    private void OnParticipantAudioStateChanged(ParticipantAudioState participant) => SetState(current =>
    {
        var updated = current.Participants
            .Where(existing => !string.Equals(existing.ParticipantId, participant.ParticipantId, StringComparison.Ordinal))
            .Append(participant)
            .OrderBy(existing => existing.ParticipantId, StringComparer.Ordinal)
            .ToArray();
        return current with { Participants = updated };
    });

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
        if (logMessage is not null)
        {
            LogAppended?.Invoke(logMessage);
        }
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
        if (microphone is not null)
        {
            microphone.StateChanged -= OnAudioStateChanged;
            microphone.LevelChanged -= OnAudioLevelChanged;
        }
        if (playback is not null) playback.StateChanged -= OnPlaybackStateChanged;
        if (webRtc is not null) webRtc.ParticipantAudioStateChanged -= OnParticipantAudioStateChanged;
        try { await StopAllCaptureAsync(CancellationToken.None); } catch { }
        try { await signaling.CloseAsync(); } catch { }
        await audio.DisposeAsync();
        if (microphone is not null) await microphone.DisposeAsync();
        await signaling.DisposeAsync();
        operationLock.Dispose();
    }
}
