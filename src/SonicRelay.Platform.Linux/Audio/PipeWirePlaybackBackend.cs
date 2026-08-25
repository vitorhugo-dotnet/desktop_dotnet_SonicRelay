using System.Buffers.Binary;
using SonicRelay.Windows.Audio;
using SonicRelay.Windows.Core.Processes;

namespace SonicRelay.Platform.Linux.Audio;

/// <summary>
/// Plays remote audio on Linux by feeding raw PCM16 to a long-lived `pw-play` process over its
/// stdin, mirroring how <see cref="PipeWireProcessBackend"/> reads capture from `pw-record`.
///
/// PipeWire resamples and re-routes on its side, so the stream is handed over at the rate and
/// channel count it arrives in and nothing is converted here. Writes go through a bounded
/// queue drained by one writer task: <see cref="Write"/> runs on the WebRTC receive path and
/// must never block on a pipe, and audio that has fallen further behind than the latency
/// budget is dropped rather than played late.
/// </summary>
public sealed class PipeWirePlaybackBackend : IAudioPlaybackBackend
{
    /// <summary>
    /// How much audio may wait to be written before the oldest is discarded. Two-way audio is
    /// a conversation: a gap is recoverable, accumulating delay is not.
    /// </summary>
    private static readonly TimeSpan LatencyBudget = TimeSpan.FromMilliseconds(150);

    private static readonly TimeSpan StopGracePeriod = TimeSpan.FromSeconds(2);

    private readonly IChildProcessRunner processRunner;
    private readonly PipeWireCommandPaths commandPaths;
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly object queueLock = new();
    private readonly Queue<byte[]> pending = new();
    private SemaphoreSlim? pendingSignal;
    private int pendingBytes;
    private int maxPendingBytes;

    private IChildProcess? process;
    private CancellationTokenSource? writeCancellation;
    private Task? writeTask;
    private Action<int>? processExitedHandler;
    private bool disposed;

    public PipeWirePlaybackBackend(IChildProcessRunner processRunner, PipeWireCommandPaths commandPaths)
    {
        this.processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        this.commandPaths = commandPaths ?? throw new ArgumentNullException(nameof(commandPaths));
    }

    public AudioDeviceInfo? Device { get; private set; }

    public event Action<AudioCaptureException>? Faulted;

    public async Task StartAsync(int sampleRate, int channelCount, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        if (channelCount is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(channelCount));
        var playback = commandPaths.PwPlay
            ?? throw new AudioCaptureException(
                AudioCaptureError.PlatformFailure,
                "Playing audio needs the PipeWire tool 'pw-play', which was not found on PATH. "
                + "Install the PipeWire user tools package for your distribution.");

        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (process is not null) return;

            string[] arguments =
            [
                "--playback", "--raw", $"--rate={sampleRate}", $"--channels={channelCount}",
                "--format=s16", "--latency=20ms", "-"
            ];

            var launched = processRunner.Start(playback, arguments);
            var localCancellation = new CancellationTokenSource();

            void OnProcessExited(int exitCode)
            {
                if (localCancellation.IsCancellationRequested) return; // an intentional Stop
                localCancellation.Cancel();
                Faulted?.Invoke(new AudioCaptureException(
                    exitCode == 0 ? AudioCaptureError.DeviceLost : AudioCaptureError.PlatformFailure,
                    exitCode == 0
                        ? "The PipeWire playback process exited unexpectedly."
                        : $"pw-play exited with code {exitCode}."));
            }

            processExitedHandler = OnProcessExited;
            process = launched;
            writeCancellation = localCancellation;
            launched.Exited += OnProcessExited;
            Device = new AudioDeviceInfo("pipewire-default-sink", "Default playback device", sampleRate, channelCount, AudioSampleFormat.Pcm16);
            lock (queueLock)
            {
                pending.Clear();
                pendingBytes = 0;
                maxPendingBytes = (int)(LatencyBudget.TotalSeconds * sampleRate) * channelCount * sizeof(short);
                pendingSignal = new SemaphoreSlim(0);
            }
            writeTask = Task.Run(() => WriteLoopAsync(launched, localCancellation.Token), CancellationToken.None);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public void Write(ReadOnlySpan<short> samples)
    {
        if (samples.IsEmpty || disposed) return;
        var bytes = new byte[samples.Length * sizeof(short)];
        for (var i = 0; i < samples.Length; i++)
        {
            // Explicit little-endian: `--format=s16` is s16le, and writing host order would
            // produce noise rather than a wrong-but-audible signal on a big-endian machine.
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * sizeof(short)), samples[i]);
        }

        SemaphoreSlim? signal;
        lock (queueLock)
        {
            signal = pendingSignal;
            if (signal is null) return;
            pending.Enqueue(bytes);
            pendingBytes += bytes.Length;
            while (pendingBytes > maxPendingBytes && pending.Count > 1)
            {
                pendingBytes -= pending.Dequeue().Length;
                // The signal count intentionally stays as it was: an extra release just makes
                // the writer loop wake to an empty queue once, which it handles.
            }
        }
        try { signal.Release(); } catch (ObjectDisposedException) { }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await StopInternalAsync(cancellationToken).ConfigureAwait(false); }
        finally { lifecycleGate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        lifecycleGate.Dispose();
    }

    private async Task StopInternalAsync(CancellationToken cancellationToken)
    {
        var current = process;
        process = null;
        Device = null;
        SemaphoreSlim? signal;
        lock (queueLock)
        {
            pending.Clear();
            pendingBytes = 0;
            signal = pendingSignal;
            pendingSignal = null;
        }
        if (current is null)
        {
            signal?.Dispose();
            return;
        }
        if (processExitedHandler is not null) current.Exited -= processExitedHandler;
        processExitedHandler = null;
        if (writeCancellation is not null) await writeCancellation.CancelAsync().ConfigureAwait(false);
        // Wake the writer so it observes the cancellation instead of sitting on the semaphore.
        try { signal?.Release(); } catch (ObjectDisposedException) { }
        if (writeTask is not null)
        {
            try { await writeTask.ConfigureAwait(false); } catch { }
            writeTask = null;
        }
        signal?.Dispose();
        // Closing stdin is how pw-play is told the stream is over; StopAsync does that.
        try { await current.StopAsync(StopGracePeriod, cancellationToken).ConfigureAwait(false); } catch { }
        writeCancellation?.Dispose();
        writeCancellation = null;
        await current.DisposeAsync().ConfigureAwait(false);
    }

    private async Task WriteLoopAsync(IChildProcess child, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                SemaphoreSlim? signal;
                lock (queueLock) signal = pendingSignal;
                if (signal is null) return;
                await signal.WaitAsync(cancellationToken).ConfigureAwait(false);

                while (true)
                {
                    byte[] block;
                    lock (queueLock)
                    {
                        if (pending.Count == 0) break;
                        block = pending.Dequeue();
                        pendingBytes -= block.Length;
                    }
                    await child.StandardInput.WriteAsync(block, cancellationToken).ConfigureAwait(false);
                }
                await child.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception exception)
        {
            Faulted?.Invoke(new AudioCaptureException(
                AudioCaptureError.PlatformFailure, "Writing to the PipeWire playback process failed.", exception));
        }
    }
}
