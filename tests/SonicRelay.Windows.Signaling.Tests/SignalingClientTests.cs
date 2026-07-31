using SonicRelay.Windows.Core.Configuration;
using SonicRelay.Windows.Core.Authentication;
using SonicRelay.Windows.Signaling.WebSockets;
using System.Net;
using System.Net.WebSockets;

namespace SonicRelay.Windows.Signaling.Tests;

public sealed class SignalingClientTests
{
    [Fact]
    public async Task ConnectUsesSessionIdentityAndDeviceBearerAndSendsPublisherReady()
    {
        var socket = new FakeWebSocketConnection();
        var factory = new FakeWebSocketFactory(socket);
        await using var client = CreateClient(factory);

        await client.ConnectAsync("session one");

        Assert.Equal("wss://signal.example/ws?tenant=blue&sessionId=session%20one", socket.ConnectedUri?.AbsoluteUri);
        Assert.Equal("access-secret", socket.AccessToken);
        Assert.Equal(SignalingConnectionState.Connected, client.State);
        Assert.Equal(SignalingMessageTypes.PublisherReady, SignalingMessageEnvelope.Deserialize(Assert.Single(socket.Sent)).Type);
    }

    [Fact]
    public async Task ReceiveDispatchesViewerReadyAndAnswersPing()
    {
        var socket = new FakeWebSocketConnection();
        var handler = new RecordingHandler();
        await using var client = CreateClient(new FakeWebSocketFactory(socket), handler);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ConnectAsync("session-1");

        socket.QueueText(new SignalingMessageEnvelope(SignalingMessageTypes.ViewerReady, "session-1", From: "viewer-7"));
        var dispatched = await handler.NextAsync(timeout.Token);
        socket.QueueText(new SignalingMessageEnvelope(SignalingMessageTypes.Ping, "session-1"));
        await WaitUntilAsync(() => socket.Sent.Count == 2, timeout.Token);

        Assert.Equal("viewer-7", dispatched.From);
        Assert.Equal(SignalingMessageTypes.Pong, SignalingMessageEnvelope.Deserialize(socket.Sent[1]).Type);
    }

    [Fact]
    public async Task InvalidMessageIsIgnoredAndReceiveLoopContinues()
    {
        var socket = new FakeWebSocketConnection();
        var handler = new RecordingHandler();
        await using var client = CreateClient(new FakeWebSocketFactory(socket), handler);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ConnectAsync("session-1");

        socket.QueueText("{not-json}");
        socket.QueueText(new SignalingMessageEnvelope(SignalingMessageTypes.ViewerReady, "session-1", From: "viewer-9"));
        var dispatched = await handler.NextAsync(timeout.Token);

        Assert.Equal("viewer-9", dispatched.From);
        Assert.Equal(SignalingConnectionState.Connected, client.State);
    }

    [Fact]
    public async Task ConnectIsIdempotentForSameIdentityAndRejectsAnotherSession()
    {
        var factory = new FakeWebSocketFactory(new FakeWebSocketConnection());
        await using var client = CreateClient(factory);
        await client.ConnectAsync("session-1");

        await client.ConnectAsync("session-1");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync("session-2"));

        Assert.Equal(1, factory.CreatedCount);
        Assert.Contains("already active", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SessionEndedDispatchesAndClosesWithoutReconnect()
    {
        var socket = new FakeWebSocketConnection();
        var factory = new FakeWebSocketFactory(socket);
        var handler = new RecordingHandler();
        await using var client = CreateClient(factory, handler);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ConnectAsync("session-1");

        socket.QueueText(new SignalingMessageEnvelope(SignalingMessageTypes.SessionEnded, "session-1"));
        await handler.NextAsync(timeout.Token);
        await WaitUntilAsync(() => client.State == SignalingConnectionState.Closed, timeout.Token);

        Assert.Equal(1, factory.CreatedCount);
        Assert.Equal(System.Net.WebSockets.WebSocketState.Closed, socket.State);
    }

    [Fact]
    public async Task ClosePublishesObservableStateTransitions()
    {
        var states = new List<SignalingConnectionState>();
        await using var client = CreateClient(new FakeWebSocketFactory(new FakeWebSocketConnection()));
        client.StateChanged += states.Add;

        await client.ConnectAsync("session-1");
        await client.CloseAsync();

        Assert.Equal(
            [SignalingConnectionState.Connecting, SignalingConnectionState.Connected, SignalingConnectionState.Closing, SignalingConnectionState.Closed],
            states);
    }

    [Fact]
    public async Task TransientCloseReconnectsWithBackoffAndSendsPublisherReadyAgain()
    {
        var first = new FakeWebSocketConnection();
        var replacement = new FakeWebSocketConnection();
        var factory = new FakeWebSocketFactory(first, replacement);
        var delay = new ImmediateReconnectDelay();
        var tokens = new SequenceAccessTokenProvider("token-1", "token-2");
        var states = new List<SignalingConnectionState>();
        await using var client = CreateClient(factory, delay: delay, tokenProvider: tokens);
        client.StateChanged += states.Add;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ConnectAsync("session-1");

        first.QueueClose(WebSocketCloseStatus.EndpointUnavailable);
        await WaitUntilAsync(() => factory.CreatedCount == 2 && client.State == SignalingConnectionState.Connected, timeout.Token);

        Assert.Equal([TimeSpan.FromSeconds(1)], delay.Delays);
        Assert.Contains(SignalingConnectionState.Reconnecting, states);
        Assert.Equal("token-1", first.AccessToken);
        Assert.Equal("token-2", replacement.AccessToken);
        Assert.Equal([false, false], tokens.ForceRefreshCalls);
        Assert.Equal(SignalingMessageTypes.PublisherReady, SignalingMessageEnvelope.Deserialize(Assert.Single(replacement.Sent)).Type);
    }

    [Fact]
    public async Task TransientTokenFailureDuringReconnectRetriesAndUsesNextToken()
    {
        var first = new FakeWebSocketConnection();
        var replacement = new FakeWebSocketConnection();
        var factory = new FakeWebSocketFactory(first, replacement);
        var delay = new ImmediateReconnectDelay();
        var transientFailure = new InvalidOperationException("token service unavailable");
        var tokens = new ScriptedAccessTokenProvider(
            transientFailure,
            "token-1",
            transientFailure,
            "token-2");
        var client = CreateClient(factory, delay: delay, tokenProvider: tokens);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ConnectAsync("session-1");

        first.QueueClose(WebSocketCloseStatus.EndpointUnavailable);
        await WaitUntilAsync(
            () => factory.CreatedCount == 2 && client.State == SignalingConnectionState.Connected,
            timeout.Token);

        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], delay.Delays);
        Assert.Equal("token-2", replacement.AccessToken);
        Assert.Equal(3, tokens.CallCount);
        await client.CloseAsync();
        await client.DisposeAsync();
    }

    [Fact]
    public async Task PermanentTokenFailureDoesNotPreventCloseOrReleaseSessionIdentity()
    {
        var first = new FakeWebSocketConnection();
        var fresh = new FakeWebSocketConnection();
        var factory = new FakeWebSocketFactory(first, fresh);
        var invalidCredential = new InvalidOperationException("credential revoked");
        var tokens = new ScriptedAccessTokenProvider(
            transientFailure: null,
            "token-1",
            invalidCredential,
            "token-2");
        var client = CreateClient(factory, tokenProvider: tokens);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ConnectAsync("session-1");

        first.QueueClose(WebSocketCloseStatus.EndpointUnavailable);
        await WaitUntilAsync(() => tokens.CallCount == 2, timeout.Token);
        await WaitUntilAsync(() => client.State == SignalingConnectionState.Faulted, timeout.Token);

        await client.CloseAsync();

        Assert.Equal(SignalingConnectionState.Closed, client.State);
        await client.ConnectAsync("session-2");
        Assert.Equal(SignalingConnectionState.Connected, client.State);
        Assert.Equal("token-2", fresh.AccessToken);
        await client.CloseAsync();
        await client.DisposeAsync();
    }

    [Fact]
    public async Task ReconnectStopsAfterConfiguredMaxAttempts()
    {
        var initial = new FakeWebSocketConnection();
        var failure = new WebSocketException("transient");
        var factory = new FakeWebSocketFactory(
            initial,
            new FakeWebSocketConnection { ConnectException = failure },
            new FakeWebSocketConnection { ConnectException = failure },
            new FakeWebSocketConnection { ConnectException = failure });
        var delay = new ImmediateReconnectDelay();
        await using var client = CreateClient(factory, delay: delay,
            policy: new SignalingReconnectPolicy { MaxAttempts = 3 });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ConnectAsync("session-1");

        initial.QueueClose(WebSocketCloseStatus.EndpointUnavailable);
        await WaitUntilAsync(() => client.State == SignalingConnectionState.Faulted, timeout.Token);

        Assert.Equal(4, factory.CreatedCount);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], delay.Delays);
    }

    [Fact]
    public async Task ReconnectKeepsRetryingPastThreeFailuresByDefault()
    {
        var initial = new FakeWebSocketConnection();
        var failure = new WebSocketException("transient");
        var factory = new FakeWebSocketFactory(
            initial,
            new FakeWebSocketConnection { ConnectException = failure },
            new FakeWebSocketConnection { ConnectException = failure },
            new FakeWebSocketConnection { ConnectException = failure },
            new FakeWebSocketConnection { ConnectException = failure },
            new FakeWebSocketConnection()); // sixth attempt finally succeeds
        var delay = new ImmediateReconnectDelay();
        await using var client = CreateClient(factory, delay: delay);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ConnectAsync("session-1");

        initial.QueueClose(WebSocketCloseStatus.EndpointUnavailable);
        await WaitUntilAsync(
            () => factory.CreatedCount == 6 && client.State == SignalingConnectionState.Connected,
            timeout.Token);

        // Backoff is capped exponential: 1, 2, 4, 8, 16 s across the five retries.
        Assert.Equal(
            [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(16)],
            delay.Delays);
    }

    [Fact]
    public async Task ReconnectAppliesConfiguredJitterToTheBackoffDelay()
    {
        var initial = new FakeWebSocketConnection();
        var replacement = new FakeWebSocketConnection();
        var factory = new FakeWebSocketFactory(initial, replacement);
        var delay = new ImmediateReconnectDelay();
        await using var client = CreateClient(factory, delay: delay,
            policy: new SignalingReconnectPolicy { JitterRatio = 0.5 },
            jitter: new FixedReconnectJitter(1));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ConnectAsync("session-1");

        initial.QueueClose(WebSocketCloseStatus.EndpointUnavailable);
        await WaitUntilAsync(() => factory.CreatedCount == 2 && client.State == SignalingConnectionState.Connected, timeout.Token);

        // 1s base delay, +50% from a 0.5 jitter ratio scaled by the maximal +1 fixed jitter draw.
        Assert.Equal([TimeSpan.FromSeconds(1.5)], delay.Delays);
    }

    [Fact]
    public async Task ReconnectJitterNeverPushesTheDelayBelowZeroOrAboveMaxDelay()
    {
        var initial = new FakeWebSocketConnection();
        var replacement = new FakeWebSocketConnection();
        var factory = new FakeWebSocketFactory(initial, replacement);
        var delay = new ImmediateReconnectDelay();
        await using var client = CreateClient(factory, delay: delay,
            policy: new SignalingReconnectPolicy { JitterRatio = 1, MaxDelay = TimeSpan.FromSeconds(1) },
            jitter: new FixedReconnectJitter(-1));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ConnectAsync("session-1");

        initial.QueueClose(WebSocketCloseStatus.EndpointUnavailable);
        await WaitUntilAsync(() => factory.CreatedCount == 2 && client.State == SignalingConnectionState.Connected, timeout.Token);

        Assert.Equal([TimeSpan.Zero], delay.Delays);
    }

    [Fact]
    public async Task SessionGoneDuringReconnectStopsRetryingAndReleasesTheSession()
    {
        var initial = new FakeWebSocketConnection();
        var gone = new FakeWebSocketConnection
        {
            ConnectException = new SignalingSessionGoneException(HttpStatusCode.Gone),
        };
        var fresh = new FakeWebSocketConnection();
        var factory = new FakeWebSocketFactory(initial, gone, fresh);
        var delay = new ImmediateReconnectDelay();
        await using var client = CreateClient(factory, delay: delay);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ConnectAsync("session-1");

        // The socket drops (transient), and the single reconnect finds the session gone.
        initial.QueueClose(WebSocketCloseStatus.EndpointUnavailable);
        await WaitUntilAsync(() => client.State == SignalingConnectionState.Closed, timeout.Token);

        // Exactly one reconnect attempt was made — no infinite 410 loop.
        Assert.Equal(2, factory.CreatedCount);

        // The identity is released, so a brand-new session starts without the
        // "already active for another session" lock.
        await client.ConnectAsync("session-2");
        Assert.Equal(3, factory.CreatedCount);
        Assert.Equal(SignalingConnectionState.Connected, client.State);
    }

    [Fact]
    public async Task PingReplyDoesNotStopTheLoopAndMessagesKeepFlowing()
    {
        var connection = new FakeWebSocketConnection();
        var factory = new FakeWebSocketFactory(connection);
        var handler = new RecordingHandler();
        await using var client = CreateClient(factory, handler: handler);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ConnectAsync("session-1");

        connection.QueueText(new SignalingMessageEnvelope(SignalingMessageTypes.Ping, "session-1", From: "server"));
        connection.QueueText(new SignalingMessageEnvelope(SignalingMessageTypes.ViewerReady, "session-1", To: "publisher"));

        // The ViewerReady message is still dispatched after the ping was handled.
        var received = await handler.NextAsync(timeout.Token);
        while (received.Type == SignalingMessageTypes.Ping) received = await handler.NextAsync(timeout.Token);
        Assert.Equal(SignalingMessageTypes.ViewerReady, received.Type);
        Assert.Equal(SignalingConnectionState.Connected, client.State);
    }

    [Fact]
    public async Task ThrowingHandlerDoesNotStopSubsequentDispatch()
    {
        var connection = new FakeWebSocketConnection();
        var factory = new FakeWebSocketFactory(connection);
        var throwing = new ThrowingHandler();
        var recording = new RecordingHandler();
        var faults = new List<Exception>();
        await using var client = CreateClient(factory, handlers: [throwing, recording]);
        client.HandlerFaulted += faults.Add;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ConnectAsync("session-1");

        connection.QueueText(new SignalingMessageEnvelope(SignalingMessageTypes.ViewerReady, "session-1", To: "publisher"));

        var received = await recording.NextAsync(timeout.Token);
        Assert.Equal(SignalingMessageTypes.ViewerReady, received.Type);
        Assert.Equal(SignalingConnectionState.Connected, client.State);
        Assert.NotEmpty(faults);
    }

    [Fact]
    public async Task ReconnectAttemptingFiresOnceForEachAttemptBeforeItsDelay()
    {
        var initial = new FakeWebSocketConnection();
        var replacement = new FakeWebSocketConnection();
        var factory = new FakeWebSocketFactory(initial, replacement);
        var delay = new ImmediateReconnectDelay();
        var attempts = new List<int>();
        await using var client = CreateClient(factory, delay: delay);
        client.ReconnectAttempting += attempts.Add;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ConnectAsync("session-1");

        initial.QueueClose(WebSocketCloseStatus.EndpointUnavailable);
        await WaitUntilAsync(() => factory.CreatedCount == 2 && client.State == SignalingConnectionState.Connected, timeout.Token);

        Assert.Equal([0], attempts);
    }

    [Fact]
    public async Task ClosedFiresWithNormalClosureAfterAnExplicitClose()
    {
        var reasons = new List<SignalingCloseReason>();
        await using var client = CreateClient(new FakeWebSocketFactory(new FakeWebSocketConnection()));
        client.Closed += reasons.Add;
        await client.ConnectAsync("session-1");

        await client.CloseAsync();

        Assert.Equal([SignalingCloseReason.NormalClosure], reasons);
    }

    [Fact]
    public async Task ClosedFiresWithSessionEndedWhenTheServerEndsTheSession()
    {
        var socket = new FakeWebSocketConnection();
        var reasons = new List<SignalingCloseReason>();
        await using var client = CreateClient(new FakeWebSocketFactory(socket));
        client.Closed += reasons.Add;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ConnectAsync("session-1");

        socket.QueueText(new SignalingMessageEnvelope(SignalingMessageTypes.SessionEnded, "session-1"));
        await WaitUntilAsync(() => reasons.Count > 0, timeout.Token);

        Assert.Equal([SignalingCloseReason.SessionEnded], reasons);
    }

    [Fact]
    public async Task ClosedFiresWithReconnectExhaustedAfterMaxAttempts()
    {
        var initial = new FakeWebSocketConnection();
        var failure = new WebSocketException("transient");
        var factory = new FakeWebSocketFactory(
            initial,
            new FakeWebSocketConnection { ConnectException = failure });
        var delay = new ImmediateReconnectDelay();
        var reasons = new List<SignalingCloseReason>();
        await using var client = CreateClient(factory, delay: delay, policy: new SignalingReconnectPolicy { MaxAttempts = 1 });
        client.Closed += reasons.Add;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ConnectAsync("session-1");

        initial.QueueClose(WebSocketCloseStatus.EndpointUnavailable);
        await WaitUntilAsync(() => client.State == SignalingConnectionState.Faulted, timeout.Token);

        Assert.Equal([SignalingCloseReason.ReconnectExhausted], reasons);
    }

    [Fact]
    public async Task ClosedFiresWithSessionGoneWhenTheBackendReportsTheSessionIsGone()
    {
        var initial = new FakeWebSocketConnection();
        var gone = new FakeWebSocketConnection { ConnectException = new SignalingSessionGoneException(HttpStatusCode.Gone) };
        var factory = new FakeWebSocketFactory(initial, gone);
        var delay = new ImmediateReconnectDelay();
        var reasons = new List<SignalingCloseReason>();
        await using var client = CreateClient(factory, delay: delay);
        client.Closed += reasons.Add;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ConnectAsync("session-1");

        initial.QueueClose(WebSocketCloseStatus.EndpointUnavailable);
        await WaitUntilAsync(() => client.State == SignalingConnectionState.Closed, timeout.Token);

        Assert.Equal([SignalingCloseReason.SessionGone], reasons);
    }

    private static SignalingClient CreateClient(
        IWebSocketConnectionFactory factory,
        ISignalingMessageHandler? handler = null,
        IReadOnlyList<ISignalingMessageHandler>? handlers = null,
        IReconnectDelay? delay = null,
        SignalingReconnectPolicy? policy = null,
        IDeviceAccessTokenProvider? tokenProvider = null,
        IReconnectJitter? jitter = null) =>
        new(
            new PublisherConfiguration(new Uri("https://api.example/"), new Uri("https://signal.example/ws?tenant=blue"), 4),
            tokenProvider ?? new SequenceAccessTokenProvider("access-secret"),
            handlers ?? (handler is null ? [] : [handler]),
            factory,
            delay ?? new ImmediateReconnectDelay(),
            policy,
            // Deterministic zero jitter by default so the many exact-delay assertions in this
            // file stay stable; tests that care about jitter pass a FixedReconnectJitter.
            jitter ?? new FixedReconnectJitter(0));

    private sealed class FixedReconnectJitter(double ratio) : IReconnectJitter
    {
        public double NextRatio() => ratio;
    }

    private sealed class SequenceAccessTokenProvider(params string[] tokens) : IDeviceAccessTokenProvider
    {
        private readonly Queue<string> tokens = new(tokens);
        public List<bool> ForceRefreshCalls { get; } = [];

        public Task<string> GetAccessTokenAsync(
            bool forceRefresh = false,
            CancellationToken cancellationToken = default)
        {
            ForceRefreshCalls.Add(forceRefresh);
            return Task.FromResult(tokens.Count > 1 ? tokens.Dequeue() : tokens.Peek());
        }
    }

    private sealed class ScriptedAccessTokenProvider(
        Exception? transientFailure,
        params object[] outcomes) : IDeviceAccessTokenProvider
    {
        private readonly Queue<object> outcomes = new(outcomes);
        public int CallCount { get; private set; }

        public Task<string> GetAccessTokenAsync(
            bool forceRefresh = false,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            var outcome = outcomes.Count > 1 ? outcomes.Dequeue() : outcomes.Peek();
            return outcome is Exception exception
                ? Task.FromException<string>(exception)
                : Task.FromResult((string)outcome);
        }

        public bool IsTransientFailure(Exception exception) =>
            ReferenceEquals(exception, transientFailure);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            await Task.Delay(10, cancellationToken);
        }
    }
}
