using System.Net.WebSockets;
using SonicRelay.Windows.Core.Authentication;
using SonicRelay.Windows.Core.Configuration;
using SonicRelay.Windows.Core.Diagnostics;
using SonicRelay.Windows.Signaling.WebSockets;

namespace SonicRelay.Windows.Signaling.Tests;

/// <summary>
/// A reconnect budget is meant to answer "is the backend reachable?", but a machine with no
/// usable interface at all cannot answer that question — every attempt it makes fails for a
/// reason that has nothing to do with the backend. Spending the budget there is what leaves a
/// publisher permanently Faulted after a 40-second outage: the retries all burned while the
/// cable was out, and the loop had already given up by the time the network came back.
/// </summary>
public sealed class SignalingClientNetworkGateTests
{
    [Fact]
    public async Task An_offline_machine_parks_in_waiting_for_network_instead_of_spending_attempts()
    {
        var initial = new FakeWebSocketConnection();
        var factory = new FakeWebSocketFactory(initial);
        var network = new FakeNetworkAvailability(available: true);
        var delay = new ImmediateReconnectDelay();
        await using var client = CreateClient(factory, network, delay,
            policy: new SignalingReconnectPolicy { MaxAttempts = 1 });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ConnectAsync("session-1");

        network.SetAvailable(false);
        initial.QueueClose(WebSocketCloseStatus.EndpointUnavailable);
        await WaitUntilAsync(() => client.State == SignalingConnectionState.WaitingForNetwork, timeout.Token);
        await Task.Delay(50, timeout.Token);

        // With MaxAttempts=1 the old loop would have burned its single attempt and faulted here.
        Assert.Equal(SignalingConnectionState.WaitingForNetwork, client.State);
        Assert.Equal(1, factory.CreatedCount);
        Assert.Empty(delay.Delays);
    }

    [Fact]
    public async Task The_network_returning_resumes_the_backoff_from_its_first_step()
    {
        var initial = new FakeWebSocketConnection();
        var replacement = new FakeWebSocketConnection();
        var factory = new FakeWebSocketFactory(initial, replacement);
        var network = new FakeNetworkAvailability(available: true);
        var delay = new ImmediateReconnectDelay();
        await using var client = CreateClient(factory, network, delay);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ConnectAsync("session-1");

        network.SetAvailable(false);
        initial.QueueClose(WebSocketCloseStatus.EndpointUnavailable);
        await WaitUntilAsync(() => client.State == SignalingConnectionState.WaitingForNetwork, timeout.Token);
        network.SetAvailable(true);
        await WaitUntilAsync(() => client.State == SignalingConnectionState.Connected, timeout.Token);

        // Stabilization window first (a freshly-up interface reports usable before it can carry
        // a TLS handshake), then the first backoff step — never the capped delay the loop would
        // have climbed to had it kept retrying against a dead route.
        Assert.Equal([SignalingClient.DefaultNetworkStabilizationDelay, TimeSpan.FromSeconds(1)], delay.Delays);
        Assert.Equal(2, factory.CreatedCount);
    }

    [Fact]
    public async Task A_failed_attempt_before_the_network_drops_does_not_carry_its_backoff_across_the_outage()
    {
        var initial = new FakeWebSocketConnection();
        var failure = new WebSocketException("transient");
        var factory = new FakeWebSocketFactory(
            initial,
            new FakeWebSocketConnection { ConnectException = failure },
            new FakeWebSocketConnection { ConnectException = failure },
            new FakeWebSocketConnection());
        var network = new FakeNetworkAvailability(available: true);
        var delay = new ManualReconnectDelay();
        await using var client = CreateClient(factory, network, delay);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ConnectAsync("session-1");

        initial.QueueClose(WebSocketCloseStatus.EndpointUnavailable);
        await WaitUntilAsync(() => delay.Recorded == 1, timeout.Token);
        delay.Release();
        await WaitUntilAsync(() => delay.Recorded == 2, timeout.Token);
        network.SetAvailable(false);
        delay.Release();
        await WaitUntilAsync(() => client.State == SignalingConnectionState.WaitingForNetwork, timeout.Token);
        network.SetAvailable(true);
        await WaitUntilAsync(() => delay.Recorded == 3, timeout.Token);
        delay.Release();
        await WaitUntilAsync(() => delay.Recorded == 4, timeout.Token);
        delay.Release();
        await WaitUntilAsync(() => client.State == SignalingConnectionState.Connected, timeout.Token);

        // Two real failures (1s, 2s), then the outage, then a clean restart at 1s rather than
        // the 4s the counter had climbed to — the machine that just came back is a new
        // situation, not the continuation of an escalating failure against a live network.
        Assert.Equal(
            [
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(2),
                SignalingClient.DefaultNetworkStabilizationDelay,
                TimeSpan.FromSeconds(1),
            ],
            delay.Delays);
    }

    [Fact]
    public async Task Recovery_steps_are_journalled_with_the_generation_that_produced_them()
    {
        var initial = new FakeWebSocketConnection();
        var replacement = new FakeWebSocketConnection();
        var factory = new FakeWebSocketFactory(initial, replacement);
        var network = new FakeNetworkAvailability(available: true);
        var journal = new RecordingRecoveryJournal();
        await using var client = CreateClient(factory, network, new ImmediateReconnectDelay(), journal: journal);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ConnectAsync("session-1");

        network.SetAvailable(false);
        initial.QueueClose(WebSocketCloseStatus.EndpointUnavailable);
        await WaitUntilAsync(() => client.State == SignalingConnectionState.WaitingForNetwork, timeout.Token);
        network.SetAvailable(true);
        await WaitUntilAsync(() => client.State == SignalingConnectionState.Connected, timeout.Token);

        var events = journal.Events.Select(x => x.Event).ToList();
        Assert.Equal(
            [
                RecoveryEvents.RecoveryStarted,
                RecoveryEvents.NetworkLost,
                RecoveryEvents.NetworkRestored,
                RecoveryEvents.SignalingReconnectStarted,
                RecoveryEvents.SignalingReconnectSucceeded,
            ],
            events);
        Assert.All(journal.Events, entry => Assert.Equal(1, entry.Generation));
    }

    [Fact]
    public async Task A_still_offline_machine_never_reports_reconnect_exhausted()
    {
        var initial = new FakeWebSocketConnection();
        var factory = new FakeWebSocketFactory(initial);
        var network = new FakeNetworkAvailability(available: true);
        var reasons = new List<SignalingCloseReason>();
        await using var client = CreateClient(factory, network, new ImmediateReconnectDelay(),
            policy: new SignalingReconnectPolicy { MaxAttempts = 1 });
        client.Closed += reasons.Add;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ConnectAsync("session-1");

        network.SetAvailable(false);
        initial.QueueClose(WebSocketCloseStatus.EndpointUnavailable);
        await WaitUntilAsync(() => client.State == SignalingConnectionState.WaitingForNetwork, timeout.Token);
        await Task.Delay(50, timeout.Token);

        // Reporting exhaustion here would tell the UI the session is unrecoverable purely
        // because the user walked out of Wi-Fi range.
        Assert.Empty(reasons);
    }

    [Fact]
    public async Task Closing_while_waiting_for_the_network_stops_the_recovery()
    {
        var initial = new FakeWebSocketConnection();
        var factory = new FakeWebSocketFactory(initial);
        var network = new FakeNetworkAvailability(available: true);
        await using var client = CreateClient(factory, network, new ImmediateReconnectDelay());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ConnectAsync("session-1");

        network.SetAvailable(false);
        initial.QueueClose(WebSocketCloseStatus.EndpointUnavailable);
        await WaitUntilAsync(() => client.State == SignalingConnectionState.WaitingForNetwork, timeout.Token);
        await client.CloseAsync();

        Assert.Equal(SignalingConnectionState.Closed, client.State);
    }

    private static SignalingClient CreateClient(
        IWebSocketConnectionFactory factory,
        INetworkAvailability network,
        IReconnectDelay delay,
        SignalingReconnectPolicy? policy = null,
        IRecoveryJournal? journal = null) =>
        new(
            new PublisherConfiguration(new Uri("https://api.example/"), new Uri("https://signal.example/ws"), 4),
            new StaticAccessTokenProvider("access-secret"),
            [],
            factory,
            delay,
            policy,
            new ZeroJitter(),
            network,
            journal);

    private sealed class ZeroJitter : IReconnectJitter
    {
        public double NextRatio() => 0;
    }

    private sealed class StaticAccessTokenProvider(string token) : IDeviceAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(bool forceRefresh = false,
            CancellationToken cancellationToken = default) => Task.FromResult(token);

        public bool IsTransientFailure(Exception exception) => false;
    }

    private sealed class FakeNetworkAvailability(bool available) : INetworkAvailability
    {
        public bool IsAvailable { get; private set; } = available;

        public event Action<bool>? AvailabilityChanged;

        public void SetAvailable(bool value)
        {
            if (IsAvailable == value) return;
            IsAvailable = value;
            AvailabilityChanged?.Invoke(value);
        }
    }

    /// <summary>
    /// Records each requested delay and then blocks until the test releases it, so a test can
    /// step the reconnect loop one attempt at a time instead of racing a double that returns
    /// instantly and burns every attempt before the test can change the network.
    /// </summary>
    private sealed class ManualReconnectDelay : IReconnectDelay
    {
        private readonly SemaphoreSlim releases = new(0);
        private readonly List<TimeSpan> delays = [];

        public IReadOnlyList<TimeSpan> Delays
        {
            get { lock (delays) return delays.ToArray(); }
        }

        public int Recorded
        {
            get { lock (delays) return delays.Count; }
        }

        public async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            lock (delays) delays.Add(delay);
            await releases.WaitAsync(cancellationToken);
        }

        public void Release() => releases.Release();
    }

    private sealed record JournalEntry(string Event, int Generation, int Attempt);

    private sealed class RecordingRecoveryJournal : IRecoveryJournal
    {
        private readonly List<JournalEntry> events = [];

        public IReadOnlyList<JournalEntry> Events
        {
            get { lock (events) return events.ToArray(); }
        }

        public Task RecordAsync(string @event, int generation, int attempt,
            IReadOnlyDictionary<string, string>? properties = null,
            CancellationToken cancellationToken = default)
        {
            lock (events) events.Add(new JournalEntry(@event, generation, attempt));
            return Task.CompletedTask;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            await Task.Delay(10, cancellationToken);
        }
    }
}
