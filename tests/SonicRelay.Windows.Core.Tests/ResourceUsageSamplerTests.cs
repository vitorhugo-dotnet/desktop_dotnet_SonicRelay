using SonicRelay.Windows.Core.Diagnostics;

namespace SonicRelay.Windows.Core.Tests;

public sealed class ResourceUsageSamplerTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch;

    private sealed class FakeCounterSource(params RawResourceCounters[] samples) : IRawResourceCounterSource
    {
        private int index;
        public int CaptureCalls { get; private set; }
        public Func<int, bool>? ThrowOnCall { get; init; }

        public RawResourceCounters Capture()
        {
            CaptureCalls++;
            if (ThrowOnCall?.Invoke(CaptureCalls) == true)
            {
                throw new InvalidOperationException("simulated OS counter failure");
            }
            var sample = samples[Math.Min(index, samples.Length - 1)];
            index++;
            return sample;
        }
    }

    /// <summary>Lets the loop run immediately for a bounded number of ticks, then parks it — so a
    /// test can await exactly the writes it expects without a real 15s interval or a race against
    /// an unbounded background loop.</summary>
    private sealed class BoundedTicksDelay(int immediateTicks) : IResourceSampleDelay
    {
        private int calls;

        public async Task DelayAsync(TimeSpan interval, CancellationToken cancellationToken)
        {
            calls++;
            if (calls > immediateTicks)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not met within the timeout.");
            }
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task WritesAResourceUsageLineWithTheComputedPropertiesAfterTheSecondTick()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"sonicrelay-{Guid.NewGuid():N}");
        try
        {
            var log = new DiagnosticLog(directory);
            var counters = new FakeCounterSource(
                new RawResourceCounters(Start, TimeSpan.Zero, 100, 50, 0, 0),
                new RawResourceCounters(Start + TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 209_715_200, 52_428_800, 102_400, 204_800));
            await using var sampler = new ResourceUsageSampler(
                counters, log, interval: TimeSpan.FromMilliseconds(1), processorCount: 1, delay: new BoundedTicksDelay(2));

            await WaitUntilAsync(() => log.RecentEvents.Any(e => e.Category == "resource-usage"), TimeSpan.FromSeconds(5));

            var entry = log.RecentEvents.Single(e => e.Category == "resource-usage");
            Assert.Equal("100.0", entry.Properties["cpuPercent"]);
            Assert.Equal("200.0", entry.Properties["workingSetMb"]);
            Assert.Equal("50.0", entry.Properties["managedHeapMb"]);
            Assert.Equal("100.0", entry.Properties["networkSentKBps"]);
            Assert.Equal("200.0", entry.Properties["networkReceivedKBps"]);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task WritesNothingAfterOnlyOneTickSinceARateNeedsTwoSamples()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"sonicrelay-{Guid.NewGuid():N}");
        try
        {
            var log = new DiagnosticLog(directory);
            var counters = new FakeCounterSource(new RawResourceCounters(Start, TimeSpan.Zero, 100, 50, 0, 0));
            await using var sampler = new ResourceUsageSampler(
                counters, log, interval: TimeSpan.FromMilliseconds(1), processorCount: 1, delay: new BoundedTicksDelay(1));

            await WaitUntilAsync(() => counters.CaptureCalls >= 1, TimeSpan.FromSeconds(5));
            // Give a would-be (wrong) write a moment to land before asserting its absence.
            await Task.Delay(50);

            Assert.DoesNotContain(log.RecentEvents, e => e.Category == "resource-usage");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ACounterCaptureFailureIsSkippedRatherThanStoppingTheSampler()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"sonicrelay-{Guid.NewGuid():N}");
        try
        {
            var log = new DiagnosticLog(directory);
            var counters = new FakeCounterSource(
                new RawResourceCounters(Start, TimeSpan.Zero, 100, 50, 0, 0),
                new RawResourceCounters(Start + TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 100, 50, 0, 0))
            {
                ThrowOnCall = call => call == 1,
            };
            await using var sampler = new ResourceUsageSampler(
                counters, log, interval: TimeSpan.FromMilliseconds(1), processorCount: 1, delay: new BoundedTicksDelay(3));

            await WaitUntilAsync(() => counters.CaptureCalls >= 3, TimeSpan.FromSeconds(5));

            // Call 1 threw (dropped, no "previous" set); calls 2 and 3 are a valid pair, so
            // exactly one resource-usage line lands despite the earlier failure.
            Assert.Single(log.RecentEvents, e => e.Category == "resource-usage");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
