using System.Globalization;
using System.Net.WebSockets;
using SonicRelay.Windows.Core.Authentication;
using SonicRelay.Windows.Core.Configuration;
using SonicRelay.Windows.Core.Diagnostics;
using SonicRelay.Windows.Signaling.WebSockets;

namespace SonicRelay.Windows.Signaling;

internal interface IReconnectDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

internal sealed class ReconnectDelay : IReconnectDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);
}

/// <summary>Supplies the random component of the reconnect backoff's jitter.</summary>
internal interface IReconnectJitter
{
    /// <summary>Returns a value in [-1, 1] scaling the policy's <see cref="SignalingReconnectPolicy.JitterRatio"/>.</summary>
    double NextRatio();
}

internal sealed class ReconnectJitter : IReconnectJitter
{
    public double NextRatio() => (Random.Shared.NextDouble() * 2) - 1;
}

/// <summary>
/// Controls how the signaling client reconnects after a transient drop. Uses
/// capped exponential backoff and, by default, retries indefinitely so a long
/// outage (API restart, network blip) recovers on its own rather than parking
/// the connection in a terminal <see cref="SignalingConnectionState.Faulted"/>.
/// </summary>
public sealed record SignalingReconnectPolicy
{
    /// <summary>Maximum reconnect attempts before faulting; <c>null</c> means unlimited.</summary>
    public int? MaxAttempts { get; init; }
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Fraction of the computed backoff delay randomized in both directions (e.g. 0.2 means
    /// ±20%), so publishers dropped by the same outage don't all retry the API in lockstep.
    /// Zero disables jitter.
    /// </summary>
    public double JitterRatio { get; init; } = 0.2;
}

/// <summary>Why a signaling connection ended, for diagnostics — never derived from free text.</summary>
public enum SignalingCloseReason { NormalClosure, SessionEnded, ReconnectExhausted, SessionGone }

public sealed class SignalingClient : ISignalingClient
{
    private readonly PublisherConfiguration configuration;
    private readonly IDeviceAccessTokenProvider accessTokenProvider;
    private readonly IReadOnlyList<ISignalingMessageHandler> handlers;
    private readonly IWebSocketConnectionFactory connectionFactory;
    private readonly IReconnectDelay reconnectDelay;
    private readonly IReconnectJitter reconnectJitter;
    private readonly SignalingReconnectPolicy reconnectPolicy;
    private readonly INetworkAvailability network;
    private readonly IRecoveryJournal journal;
    private readonly SemaphoreSlim lifecycleLock = new(1, 1);
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private CancellationTokenSource? lifecycleCancellation;
    private IWebSocketConnection? connection;
    private Task? receiveTask;
    private string? activeSessionId;

    /// <summary>
    /// Monotonic id of the current recovery cycle, so journal lines from an attempt that
    /// finished late are distinguishable from the live one's.
    /// </summary>
    private int connectionGeneration;

    /// <summary>
    /// How long to let a freshly-restored interface settle before retrying. An interface reports
    /// itself usable the moment it has an address, which is routinely before it can complete a
    /// TLS handshake; retrying into that window just burns an attempt and produces a confusing
    /// failure in the log.
    /// </summary>
    internal static readonly TimeSpan DefaultNetworkStabilizationDelay = TimeSpan.FromMilliseconds(750);

    public SignalingClient(
        PublisherConfiguration configuration,
        IDeviceAccessTokenProvider accessTokenProvider,
        IEnumerable<ISignalingMessageHandler> handlers,
        INetworkAvailability? network = null,
        IRecoveryJournal? journal = null)
        : this(configuration, accessTokenProvider, handlers, new ClientWebSocketConnectionFactory(),
            new ReconnectDelay(), reconnectPolicy: null, reconnectJitter: null, network, journal)
    {
    }

    internal SignalingClient(
        PublisherConfiguration configuration,
        IDeviceAccessTokenProvider accessTokenProvider,
        IEnumerable<ISignalingMessageHandler> handlers,
        IWebSocketConnectionFactory connectionFactory,
        IReconnectDelay reconnectDelay,
        SignalingReconnectPolicy? reconnectPolicy = null,
        IReconnectJitter? reconnectJitter = null,
        INetworkAvailability? network = null,
        IRecoveryJournal? journal = null)
    {
        this.network = network ?? AlwaysAvailableNetwork.Instance;
        this.journal = journal ?? NullRecoveryJournal.Instance;
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        this.accessTokenProvider = accessTokenProvider ?? throw new ArgumentNullException(nameof(accessTokenProvider));
        this.handlers = handlers?.ToArray() ?? throw new ArgumentNullException(nameof(handlers));
        this.connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        this.reconnectDelay = reconnectDelay ?? throw new ArgumentNullException(nameof(reconnectDelay));
        this.reconnectPolicy = reconnectPolicy ?? new SignalingReconnectPolicy();
        this.reconnectJitter = reconnectJitter ?? new ReconnectJitter();
    }

    public SignalingConnectionState State { get; private set; } = SignalingConnectionState.Disconnected;
    public event Action<SignalingConnectionState>? StateChanged;
    public event Action<int>? ReconnectAttempting;
    public event Action<SignalingCloseReason>? Closed;

    /// <summary>
    /// Raised when a registered message handler throws. The receive loop keeps
    /// running so one faulting handler cannot silently kill signaling.
    /// </summary>
    public event Action<Exception>? HandlerFaulted;

    public async Task ConnectAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        await lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            if (IsActive())
            {
                if (string.Equals(activeSessionId, sessionId, StringComparison.Ordinal))
                {
                    return;
                }
                throw new InvalidOperationException("A signaling connection is already active for another session.");
            }

            activeSessionId = sessionId;
            lifecycleCancellation?.Dispose();
            lifecycleCancellation = new CancellationTokenSource();
            SetState(SignalingConnectionState.Connecting);

            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifecycleCancellation.Token);
                await OpenConnectionAsync(linked.Token);
                receiveTask = RunReceiveLoopAsync(lifecycleCancellation.Token);
            }
            catch
            {
                SetState(SignalingConnectionState.Faulted);
                await DisposeConnectionAsync();
                throw;
            }
        }
        finally
        {
            lifecycleLock.Release();
        }
    }

    public async Task SendAsync(SignalingMessageEnvelope message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var current = connection;
        if (State != SignalingConnectionState.Connected || current is null)
        {
            throw new InvalidOperationException("The signaling connection is not connected.");
        }

        await SendCoreAsync(current, message, cancellationToken);
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        Task? pendingReceive;
        await lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            if (State is SignalingConnectionState.Disconnected or SignalingConnectionState.Closed)
            {
                return;
            }

            SetState(SignalingConnectionState.Closing);
            lifecycleCancellation?.Cancel();
            var current = connection;
            if (current is not null)
            {
                await current.CloseAsync(WebSocketCloseStatus.NormalClosure, "Publisher closed signaling.", cancellationToken);
            }
            pendingReceive = receiveTask;
        }
        finally
        {
            lifecycleLock.Release();
        }

        if (pendingReceive is not null)
        {
            await ObserveReceiveCompletionAsync(pendingReceive);
        }
        await DisposeConnectionAsync();
        ClearActiveIdentity();
        Closed?.Invoke(SignalingCloseReason.NormalClosure);
        SetState(SignalingConnectionState.Closed);
    }

    private async Task OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var accessToken = await accessTokenProvider.GetAccessTokenAsync(cancellationToken: cancellationToken);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException("A current access token is required for signaling.");
        }

        var next = connectionFactory.Create();
        try
        {
            await next.ConnectAsync(BuildConnectionUri(), accessToken, cancellationToken);
            await SendCoreAsync(next, new SignalingMessageEnvelope(SignalingMessageTypes.PublisherReady, activeSessionId), cancellationToken);
        }
        catch
        {
            await next.DisposeAsync();
            throw;
        }

        var previous = connection;
        connection = next;
        if (previous is not null)
        {
            await previous.DisposeAsync();
        }
        SetState(SignalingConnectionState.Connected);
    }

    private async Task RunReceiveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var current = connection ?? throw new WebSocketException("The signaling socket is unavailable.");
                var inbound = await current.ReceiveAsync(cancellationToken);
                if (inbound.MessageType == WebSocketMessageType.Close)
                {
                    if (inbound.CloseStatus == WebSocketCloseStatus.NormalClosure)
                    {
                        await CloseFromReceiveLoopAsync();
                        return;
                    }
                    throw new WebSocketException("The signaling socket closed unexpectedly.");
                }
                if (inbound.MessageType != WebSocketMessageType.Text || inbound.Text is null)
                {
                    continue;
                }

                SignalingMessageEnvelope message;
                try
                {
                    message = SignalingMessageEnvelope.Deserialize(inbound.Text);
                }
                catch (SignalingProtocolException)
                {
                    continue;
                }

                if (message.Type == SignalingMessageTypes.Ping)
                {
                    // Reply on the current socket directly; the public SendAsync
                    // throws a non-transient InvalidOperationException if the state
                    // is briefly not Connected (e.g. mid-reconnect), which would
                    // otherwise escape and silently kill the receive loop. A failed
                    // pong is never fatal — the next receive surfaces real errors.
                    try
                    {
                        await SendCoreAsync(current, new SignalingMessageEnvelope(SignalingMessageTypes.Pong, activeSessionId, message.From), cancellationToken);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                    }
                }

                // Isolate handler dispatch: a handler throwing (e.g. the WebRTC
                // publisher raising a non-transient WebRtcPublisherException) must
                // not tear down signaling or skip the remaining handlers' turn on
                // future messages.
                foreach (var handler in handlers)
                {
                    try
                    {
                        await handler.HandleAsync(message, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception exception)
                    {
                        HandlerFaulted?.Invoke(exception);
                    }
                }

                if (message.Type == SignalingMessageTypes.SessionEnded)
                {
                    await CloseFromReceiveLoopAsync();
                    return;
                }
            }
            // A deliberate close (CloseAsync, CloseFromReceiveLoopAsync, or disposal) cancels the
            // lifecycle token and tears the socket down underneath this pending receive. Depending
            // on timing that surfaces either as an OperationCanceledException or as a perfectly
            // transient-looking WebSocketException — and the latter used to fall through to the
            // reconnect branch below with an already-cancelled token. The first backoff delay then
            // threw immediately, so a loop configured for unlimited retries reported
            // ReconnectExhausted and parked the client in Faulted milliseconds after a close the
            // caller had asked for. Faulted also meant the next connect reused a session the
            // backend had already discarded, which came back as 410 Gone. A cancelled lifecycle
            // means the close was intentional: there is nothing here to reconnect.
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (IsTransient(exception))
            {
                ReconnectOutcome outcome;
                try
                {
                    outcome = await TryReconnectAsync(cancellationToken);
                }
                catch
                {
                    SetState(SignalingConnectionState.Faulted);
                    throw;
                }

                switch (outcome)
                {
                    case ReconnectOutcome.Reconnected:
                        break;
                    case ReconnectOutcome.SessionGone:
                        await HandleSessionGoneAsync();
                        return;
                    // A cancelled lifecycle means the caller asked for the close — including a
                    // close that landed while recovery was parked waiting for the network.
                    // Reporting exhaustion there would tell the UI the session died on its own.
                    case ReconnectOutcome.Exhausted when cancellationToken.IsCancellationRequested:
                        return;
                    default:
                        Closed?.Invoke(SignalingCloseReason.ReconnectExhausted);
                        SetState(SignalingConnectionState.Faulted);
                        return;
                }
            }
        }
    }

    private enum ReconnectOutcome
    {
        Reconnected,
        Exhausted,
        SessionGone,
    }

    private async Task<ReconnectOutcome> TryReconnectAsync(CancellationToken cancellationToken)
    {
        var generation = ++connectionGeneration;
        SetState(SignalingConnectionState.Reconnecting);
        await RecordAsync(RecoveryEvents.RecoveryStarted, generation, 0);

        var attempt = 0;
        while (reconnectPolicy.MaxAttempts is null || attempt < reconnectPolicy.MaxAttempts)
        {
            if (!network.IsAvailable)
            {
                // The machine has no route at all, so every attempt would fail for a reason that
                // says nothing about the backend. Park instead of spending budget: burning the
                // retries here is exactly what used to leave a publisher terminally Faulted
                // after an outage that outlasted the backoff, with the network already back.
                if (!await WaitForNetworkAsync(generation, cancellationToken))
                {
                    return ReconnectOutcome.Exhausted;
                }
                // The machine that just came back is a new situation, not the continuation of an
                // escalating failure against a live network, so the backoff starts over.
                attempt = 0;
                SetState(SignalingConnectionState.Reconnecting);
                continue;
            }

            ReconnectAttempting?.Invoke(attempt);
            await RecordAsync(RecoveryEvents.SignalingReconnectStarted, generation, attempt);
            try
            {
                await reconnectDelay.DelayAsync(ReconnectDelayFor(attempt), cancellationToken);
                await OpenConnectionAsync(cancellationToken);
                await RecordAsync(RecoveryEvents.SignalingReconnectSucceeded, generation, attempt);
                return ReconnectOutcome.Reconnected;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await RecordAsync(RecoveryEvents.RecoveryCancelled, generation, attempt);
                return ReconnectOutcome.Exhausted;
            }
            catch (SignalingSessionGoneException)
            {
                // The session is gone (410/404). Stop retrying immediately — looping on a
                // dead session wedges the client and blocks starting a new one.
                await RecordAsync(RecoveryEvents.RecoveryFailed, generation, attempt,
                    new Dictionary<string, string> { ["reason"] = "session gone" });
                return ReconnectOutcome.SessionGone;
            }
            catch (Exception exception) when (IsTransient(exception))
            {
                attempt++;
            }
        }
        await RecordAsync(RecoveryEvents.RecoveryFailed, generation, attempt,
            new Dictionary<string, string> { ["reason"] = "reconnect attempts exhausted" });
        return ReconnectOutcome.Exhausted;
    }

    /// <summary>
    /// Parks recovery until the machine has a usable interface again, then lets it settle.
    /// Returns false if the lifecycle was cancelled while waiting (a deliberate close), in which
    /// case there is nothing left to recover.
    /// </summary>
    private async Task<bool> WaitForNetworkAsync(int generation, CancellationToken cancellationToken)
    {
        SetState(SignalingConnectionState.WaitingForNetwork);
        await RecordAsync(RecoveryEvents.NetworkLost, generation, 0);

        var restored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnAvailabilityChanged(bool available)
        {
            if (available) restored.TrySetResult();
        }

        network.AvailabilityChanged += OnAvailabilityChanged;
        try
        {
            // Re-check after subscribing: the interface can come back in the window between the
            // caller's check and this subscription, and nothing would raise the event again.
            if (network.IsAvailable) restored.TrySetResult();
            await using var registration = cancellationToken.Register(
                static state => ((TaskCompletionSource)state!).TrySetCanceled(), restored);
            await restored.Task;
        }
        catch (OperationCanceledException)
        {
            await RecordAsync(RecoveryEvents.RecoveryCancelled, generation, 0);
            return false;
        }
        finally
        {
            network.AvailabilityChanged -= OnAvailabilityChanged;
        }

        await RecordAsync(RecoveryEvents.NetworkRestored, generation, 0);
        try
        {
            await reconnectDelay.DelayAsync(DefaultNetworkStabilizationDelay, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await RecordAsync(RecoveryEvents.RecoveryCancelled, generation, 0);
            return false;
        }
        return true;
    }

    private Task RecordAsync(string @event, int generation, int attempt,
        IReadOnlyDictionary<string, string>? properties = null)
    {
        var payload = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["stage"] = "signaling",
            ["state"] = State.ToString(),
            ["sessionCorrelationId"] = DiagnosticRedactor.MaskIdentifier(activeSessionId),
        };
        foreach (var pair in properties ?? new Dictionary<string, string>())
        {
            payload[pair.Key] = pair.Value;
        }
        // Journalling must never be able to break recovery: it writes to disk, and a full or
        // read-only log directory is not a reason to abandon a session that is coming back.
        return SafeRecordAsync(@event, generation, attempt, payload);
    }

    private async Task SafeRecordAsync(string @event, int generation, int attempt,
        IReadOnlyDictionary<string, string> properties)
    {
        try
        {
            await journal.RecordAsync(@event, generation, attempt, properties, CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    // Terminally releases a session the server has discarded: tears down the socket
    // and clears the identity so the UI can start a fresh session immediately.
    private async Task HandleSessionGoneAsync()
    {
        await DisposeConnectionAsync();
        ClearActiveIdentity();
        Closed?.Invoke(SignalingCloseReason.SessionGone);
        SetState(SignalingConnectionState.Closed);
    }

    private TimeSpan ReconnectDelayFor(int attempt)
    {
        // Capped exponential backoff: BaseDelay * 2^attempt, clamped to MaxDelay.
        // The shift is bounded so it cannot overflow on a long-lived reconnect loop.
        var multiplier = 1L << Math.Min(attempt, 30);
        var ticks = reconnectPolicy.BaseDelay.Ticks * multiplier;
        var capped = ticks < 0 || ticks > reconnectPolicy.MaxDelay.Ticks
            ? reconnectPolicy.MaxDelay.Ticks
            : ticks;

        var jitterRatio = Math.Clamp(reconnectPolicy.JitterRatio, 0, 1);
        if (jitterRatio <= 0) return TimeSpan.FromTicks(capped);

        // Randomize within ±jitterRatio of the capped delay so publishers dropped by the
        // same outage don't all hammer the API in lockstep.
        var jitterFraction = jitterRatio * Math.Clamp(reconnectJitter.NextRatio(), -1, 1);
        var jittered = Math.Clamp(capped * (1 + jitterFraction), 0d, (double)reconnectPolicy.MaxDelay.Ticks);
        return TimeSpan.FromTicks((long)jittered);
    }

    private async Task CloseFromReceiveLoopAsync()
    {
        SetState(SignalingConnectionState.Closing);
        lifecycleCancellation?.Cancel();
        var current = connection;
        if (current is not null)
        {
            await current.CloseAsync(WebSocketCloseStatus.NormalClosure, "Session ended.", CancellationToken.None);
        }
        await DisposeConnectionAsync();
        ClearActiveIdentity();
        Closed?.Invoke(SignalingCloseReason.SessionEnded);
        SetState(SignalingConnectionState.Closed);
    }

    private async Task SendCoreAsync(
        IWebSocketConnection target,
        SignalingMessageEnvelope message,
        CancellationToken cancellationToken)
    {
        await sendLock.WaitAsync(cancellationToken);
        try
        {
            await target.SendTextAsync(message.Serialize(), cancellationToken);
        }
        finally
        {
            sendLock.Release();
        }
    }

    private Uri BuildConnectionUri()
    {
        var builder = new UriBuilder(configuration.SignalingBaseUrl)
        {
            Scheme = configuration.SignalingBaseUrl.Scheme.ToLowerInvariant() switch
            {
                "http" => "ws",
                "https" => "wss",
                "ws" => "ws",
                "wss" => "wss",
                _ => throw new InvalidOperationException("The signaling URL must use HTTP(S) or WS(S).")
            }
        };
        var existingQuery = builder.Query.TrimStart('?');
        var identityQuery = $"sessionId={Uri.EscapeDataString(activeSessionId!)}";
        builder.Query = string.IsNullOrEmpty(existingQuery) ? identityQuery : $"{existingQuery}&{identityQuery}";
        return builder.Uri;
    }

    private bool IsActive() => State is SignalingConnectionState.Connecting
        or SignalingConnectionState.Connected
        or SignalingConnectionState.Reconnecting
        or SignalingConnectionState.WaitingForNetwork
        or SignalingConnectionState.Closing;

    private bool IsTransient(Exception exception) =>
        exception is WebSocketException or IOException
        || accessTokenProvider.IsTransientFailure(exception);

    private void SetState(SignalingConnectionState state)
    {
        if (State == state)
        {
            return;
        }
        State = state;
        StateChanged?.Invoke(state);
    }

    private async Task DisposeConnectionAsync()
    {
        var current = Interlocked.Exchange(ref connection, null);
        if (current is not null)
        {
            await current.DisposeAsync();
        }
    }

    private void ClearActiveIdentity()
    {
        activeSessionId = null;
    }

    private static async Task ObserveReceiveCompletionAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
        lifecycleCancellation?.Dispose();
        lifecycleLock.Dispose();
        sendLock.Dispose();
    }
}
