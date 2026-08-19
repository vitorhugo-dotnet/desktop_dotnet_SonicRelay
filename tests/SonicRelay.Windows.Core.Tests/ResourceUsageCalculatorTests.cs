using SonicRelay.Windows.Core.Diagnostics;

namespace SonicRelay.Windows.Core.Tests;

public sealed class ResourceUsageCalculatorTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch;

    [Fact]
    public void FullyBusySingleCoreOverOneSecondIsOneHundredPercentCpu()
    {
        var previous = new RawResourceCounters(Start, TimeSpan.Zero, 0, 0, 0, 0);
        var current = previous with { Timestamp = Start + TimeSpan.FromSeconds(1), TotalProcessorTime = TimeSpan.FromSeconds(1) };

        var snapshot = ResourceUsageCalculator.ComputeDelta(previous, current, processorCount: 1);

        Assert.Equal(100, snapshot.CpuPercent, precision: 3);
    }

    [Fact]
    public void CpuPercentIsDividedAcrossAllLogicalProcessors()
    {
        // One full core busy out of four available is 25% of the machine's total CPU capacity —
        // the metric this exists to catch ("is the machine overloaded?") cares about total
        // capacity, not about how busy a single core is.
        var previous = new RawResourceCounters(Start, TimeSpan.Zero, 0, 0, 0, 0);
        var current = previous with { Timestamp = Start + TimeSpan.FromSeconds(1), TotalProcessorTime = TimeSpan.FromSeconds(1) };

        var snapshot = ResourceUsageCalculator.ComputeDelta(previous, current, processorCount: 4);

        Assert.Equal(25, snapshot.CpuPercent, precision: 3);
    }

    [Fact]
    public void NetworkThroughputIsBytesDividedByElapsedSeconds()
    {
        var previous = new RawResourceCounters(Start, TimeSpan.Zero, 0, 0, NetworkBytesSent: 1_000, NetworkBytesReceived: 2_000);
        var current = previous with
        {
            Timestamp = Start + TimeSpan.FromSeconds(2),
            NetworkBytesSent = 3_000,
            NetworkBytesReceived = 6_000,
        };

        var snapshot = ResourceUsageCalculator.ComputeDelta(previous, current, processorCount: 1);

        Assert.Equal(1_000, snapshot.NetworkSentBytesPerSecond, precision: 3);
        Assert.Equal(2_000, snapshot.NetworkReceivedBytesPerSecond, precision: 3);
    }

    [Fact]
    public void WorkingSetAndManagedHeapAreReportedAsOfTheCurrentSample()
    {
        var previous = new RawResourceCounters(Start, TimeSpan.Zero, WorkingSetBytes: 10, ManagedHeapBytes: 5, 0, 0);
        var current = previous with
        {
            Timestamp = Start + TimeSpan.FromSeconds(1),
            WorkingSetBytes = 200_000_000,
            ManagedHeapBytes = 50_000_000,
        };

        var snapshot = ResourceUsageCalculator.ComputeDelta(previous, current, processorCount: 1);

        Assert.Equal(200_000_000, snapshot.WorkingSetBytes);
        Assert.Equal(50_000_000, snapshot.ManagedHeapBytes);
    }

    [Fact]
    public void ANetworkCounterThatRolledOverReadsAsZeroThroughputRatherThanGoingNegative()
    {
        // NetworkInterface byte counters can reset (adapter reconnect, counter overflow); a
        // negative delta is meaningless as a throughput and must never be reported as one.
        var previous = new RawResourceCounters(Start, TimeSpan.Zero, 0, 0, NetworkBytesSent: 5_000, NetworkBytesReceived: 5_000);
        var current = previous with { Timestamp = Start + TimeSpan.FromSeconds(1), NetworkBytesSent = 100, NetworkBytesReceived = 100 };

        var snapshot = ResourceUsageCalculator.ComputeDelta(previous, current, processorCount: 1);

        Assert.Equal(0, snapshot.NetworkSentBytesPerSecond);
        Assert.Equal(0, snapshot.NetworkReceivedBytesPerSecond);
    }

    [Fact]
    public void ZeroElapsedTimeBetweenSamplesReadsAsAllZeroRatesRatherThanDividingByZero()
    {
        var previous = new RawResourceCounters(Start, TimeSpan.Zero, 10, 10, 10, 10);
        var current = previous;

        var snapshot = ResourceUsageCalculator.ComputeDelta(previous, current, processorCount: 1);

        Assert.Equal(0, snapshot.CpuPercent);
        Assert.Equal(0, snapshot.NetworkSentBytesPerSecond);
        Assert.Equal(0, snapshot.NetworkReceivedBytesPerSecond);
    }
}
