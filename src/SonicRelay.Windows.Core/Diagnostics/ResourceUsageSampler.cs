using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;

namespace SonicRelay.Windows.Core.Diagnostics;

/// <summary>
/// A single point-in-time reading of the raw, cumulative OS counters a resource-usage sample is
/// computed from. Two of these (an interval apart) are enough to derive CPU% and network
/// throughput; <see cref="WorkingSetBytes"/> and <see cref="ManagedHeapBytes"/> need only one.
/// </summary>
public readonly record struct RawResourceCounters(
    DateTimeOffset Timestamp,
    TimeSpan TotalProcessorTime,
    long WorkingSetBytes,
    long ManagedHeapBytes,
    long NetworkBytesSent,
    long NetworkBytesReceived);

/// <summary>A resource-usage reading ready to log: instantaneous levels plus rates since the previous sample.</summary>
public readonly record struct ResourceUsageSnapshot(
    double CpuPercent,
    long WorkingSetBytes,
    long ManagedHeapBytes,
    double NetworkSentBytesPerSecond,
    double NetworkReceivedBytesPerSecond);

/// <summary>
/// Turns two <see cref="RawResourceCounters"/> readings into a <see cref="ResourceUsageSnapshot"/>.
/// Kept free of any OS-facing API so the CPU%/throughput math — the part worth trusting — is
/// testable without a real process or network adapter.
/// </summary>
public static class ResourceUsageCalculator
{
    public static ResourceUsageSnapshot ComputeDelta(RawResourceCounters previous, RawResourceCounters current, int processorCount)
    {
        var elapsed = current.Timestamp - previous.Timestamp;
        var elapsedSeconds = elapsed.TotalSeconds;

        double cpuPercent = 0;
        double sentBytesPerSecond = 0;
        double receivedBytesPerSecond = 0;
        if (elapsedSeconds > 0)
        {
            var processorSeconds = Math.Max(0, processorCount) * elapsedSeconds;
            if (processorSeconds > 0)
            {
                var busySeconds = (current.TotalProcessorTime - previous.TotalProcessorTime).TotalSeconds;
                cpuPercent = Math.Max(0, busySeconds) / processorSeconds * 100;
            }

            // A negative delta means the counter rolled over or the adapter reset — not that
            // throughput went backwards — so it reads as zero rather than a negative rate.
            sentBytesPerSecond = Math.Max(0, current.NetworkBytesSent - previous.NetworkBytesSent) / elapsedSeconds;
            receivedBytesPerSecond = Math.Max(0, current.NetworkBytesReceived - previous.NetworkBytesReceived) / elapsedSeconds;
        }

        return new ResourceUsageSnapshot(
            cpuPercent,
            current.WorkingSetBytes,
            current.ManagedHeapBytes,
            sentBytesPerSecond,
            receivedBytesPerSecond);
    }
}

/// <summary>Reads the current raw OS counters. The untested, OS-facing counterpart to <see cref="ResourceUsageCalculator"/>.</summary>
public interface IRawResourceCounterSource
{
    RawResourceCounters Capture();
}

/// <summary>
/// <see cref="IRawResourceCounterSource"/> backed by the current process and the machine's active
/// network interfaces. Loopback and down interfaces are excluded so a WSL/virtual adapter or an
/// unplugged secondary NIC doesn't dilute the one link the audio upload actually goes out over.
/// </summary>
public sealed class ProcessRawResourceCounterSource : IRawResourceCounterSource
{
    public RawResourceCounters Capture()
    {
        using var process = Process.GetCurrentProcess();
        long bytesSent = 0;
        long bytesReceived = 0;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }
            var stats = nic.GetIPStatistics();
            bytesSent += stats.BytesSent;
            bytesReceived += stats.BytesReceived;
        }

        return new RawResourceCounters(
            DateTimeOffset.UtcNow,
            process.TotalProcessorTime,
            process.WorkingSet64,
            GC.GetTotalMemory(forceFullCollection: false),
            bytesSent,
            bytesReceived);
    }
}

/// <summary>Supplies the wait between resource-usage samples. A seam so tests can drive the sampler's loop without a real interval.</summary>
public interface IResourceSampleDelay
{
    Task DelayAsync(TimeSpan interval, CancellationToken cancellationToken);
}

public sealed class ResourceSampleDelay : IResourceSampleDelay
{
    public Task DelayAsync(TimeSpan interval, CancellationToken cancellationToken) => Task.Delay(interval, cancellationToken);
}

/// <summary>
/// Periodically samples CPU, memory, and network throughput into the diagnostic log. Exists to
/// answer, after the fact, whether a connection drop coincided with the machine running hot —
/// sustained audio/video playback on the same machine as the publisher was the suspected trigger
/// for a recurring signaling drop, and prose descriptions of "I was watching a video" are not
/// something a log line can correlate against a timestamp. A resource-usage line every sampling
/// interval is.
/// </summary>
public sealed class ResourceUsageSampler : IAsyncDisposable
{
    private readonly IRawResourceCounterSource counterSource;
    private readonly DiagnosticLog log;
    private readonly TimeSpan interval;
    private readonly int processorCount;
    private readonly IResourceSampleDelay delay;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task loopTask;

    public ResourceUsageSampler(
        IRawResourceCounterSource counterSource,
        DiagnosticLog log,
        TimeSpan? interval = null,
        int? processorCount = null,
        IResourceSampleDelay? delay = null)
    {
        this.counterSource = counterSource ?? throw new ArgumentNullException(nameof(counterSource));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        this.interval = interval ?? TimeSpan.FromSeconds(15);
        this.processorCount = processorCount ?? Math.Max(1, Environment.ProcessorCount);
        this.delay = delay ?? new ResourceSampleDelay();
        loopTask = RunAsync(cancellation.Token);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        RawResourceCounters? previous = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await delay.DelayAsync(interval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            RawResourceCounters current;
            try
            {
                current = counterSource.Capture();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // A failed sample must never take capture/signaling down with it. The next
                // successful capture still pairs with the last known "previous", so a single
                // transient failure only costs that one tick's write, not a data point.
                continue;
            }

            if (previous is { } previousSample)
            {
                var snapshot = ResourceUsageCalculator.ComputeDelta(previousSample, current, processorCount);
                await WriteAsync(snapshot, cancellationToken);
            }
            previous = current;
        }
    }

    private async Task WriteAsync(ResourceUsageSnapshot snapshot, CancellationToken cancellationToken)
    {
        var properties = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["cpuPercent"] = snapshot.CpuPercent.ToString("F1", CultureInfo.InvariantCulture),
            ["workingSetMb"] = (snapshot.WorkingSetBytes / 1024d / 1024d).ToString("F1", CultureInfo.InvariantCulture),
            ["managedHeapMb"] = (snapshot.ManagedHeapBytes / 1024d / 1024d).ToString("F1", CultureInfo.InvariantCulture),
            ["networkSentKBps"] = (snapshot.NetworkSentBytesPerSecond / 1024d).ToString("F1", CultureInfo.InvariantCulture),
            ["networkReceivedKBps"] = (snapshot.NetworkReceivedBytesPerSecond / 1024d).ToString("F1", CultureInfo.InvariantCulture),
        };
        try
        {
            await log.WriteAsync("resource-usage", "Resource usage sample.", properties, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await cancellation.CancelAsync();
        try
        {
            await loopTask;
        }
        catch (OperationCanceledException)
        {
        }
        cancellation.Dispose();
    }
}
