using SonicRelay.Windows.Audio;
using SonicRelay.Windows.Core.Processes;

namespace SonicRelay.Platform.Linux.Audio;

/// <summary>
/// Captures the default PipeWire audio source (the microphone) as raw PCM16 stereo 48 kHz,
/// for two-way sessions.
///
/// Unlike <see cref="PipeWireProcessBackend"/> this deliberately passes no <c>--target</c>.
/// That backend must name a sink explicitly, because `pw-record`'s automatic target for
/// desktop-output capture can resolve to a microphone instead of a sink monitor
/// (ADR-LINUX-004) — here the microphone *is* what we want, so the automatic target is the
/// default audio source, which is exactly the device the user picked for input in their
/// desktop's sound settings.
/// </summary>
public sealed class PipeWireMicrophoneBackend : IAudioCaptureBackend
{
    private const int SampleRate = 48000;
    private const int Channels = 1;
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StopGracePeriod = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan EmptyReadPollDelay = TimeSpan.FromMilliseconds(5);

    private readonly IChildProcessRunner processRunner;
    private readonly PipeWireCommandPaths commandPaths;
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);

    private IChildProcess? process;
    private CancellationTokenSource? readCancellation;
    private Task? readTask;
    private Action<int>? processExitedHandler;
    private volatile bool paused;
    private bool disposed;

    public PipeWireMicrophoneBackend(IChildProcessRunner processRunner, PipeWireCommandPaths commandPaths)
    {
        this.processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        this.commandPaths = commandPaths ?? throw new ArgumentNullException(nameof(commandPaths));
    }

    public AudioDeviceInfo? Device { get; private set; }
    public event Action<AudioFrame, AudioLevelSnapshot>? FrameAvailable;
    public event Action<AudioCaptureException>? Faulted;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (process is not null) return;

            string[] arguments =
            [
                "--raw", $"--rate={SampleRate}", $"--channels={Channels}", "--format=s16", "--latency=20ms", "-"
            ];

            // Two independent startup signals. `firstBytes` means the microphone is producing
            // audio; `startupFailure` means the process died before it could. Neither is
            // guaranteed to arrive: a muted or silent microphone produces no bytes at all and
            // never exits, which is a working capture, not a failure — so a timeout here is
            // treated as started rather than as a missing device.
            var firstBytes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var startupFailure = new TaskCompletionSource<AudioCaptureException>(TaskCreationOptions.RunContinuationsAsynchronously);
            var launched = processRunner.Start(commandPaths.PwRecord, arguments);
            var localReadCancellation = new CancellationTokenSource();

            void OnProcessExited(int exitCode)
            {
                if (localReadCancellation.IsCancellationRequested) return; // an intentional Stop
                var error = exitCode == 0
                    ? new AudioCaptureException(AudioCaptureError.DeviceLost, "The PipeWire microphone process exited unexpectedly.")
                    : new AudioCaptureException(AudioCaptureError.PlatformFailure, $"pw-record exited with code {exitCode}.");
                localReadCancellation.Cancel();
                // Before startup completes there is no started capture for a caller to react
                // to via Faulted; fail StartAsync itself instead.
                if (!startupFailure.TrySetResult(error)) Faulted?.Invoke(error);
            }

            processExitedHandler = OnProcessExited;
            process = launched;
            readCancellation = localReadCancellation;
            launched.Exited += OnProcessExited;
            Device = new AudioDeviceInfo("pipewire-default-source", "Default microphone", SampleRate, Channels, AudioSampleFormat.Pcm16);
            readTask = Task.Run(() => ReadLoopAsync(launched, firstBytes, startupFailure, localReadCancellation.Token), CancellationToken.None);

            var settled = await Task.WhenAny(
                firstBytes.Task,
                startupFailure.Task,
                Task.Delay(StartupTimeout, cancellationToken)).ConfigureAwait(false);
            if (settled == startupFailure.Task)
            {
                var failure = await startupFailure.Task.ConfigureAwait(false);
                await StopInternalAsync(CancellationToken.None).ConfigureAwait(false);
                throw failure;
            }
            // Nothing further to complete: startupFailure stays pending and its later
            // completion routes through Faulted, which is what a mid-capture death is.
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public Task PauseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        paused = true;
        return Task.CompletedTask;
    }

    public Task ResumeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        paused = false;
        return Task.CompletedTask;
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
        paused = false;
        Device = null;
        if (current is null) return;
        if (processExitedHandler is not null) current.Exited -= processExitedHandler;
        processExitedHandler = null;
        if (readCancellation is not null) await readCancellation.CancelAsync().ConfigureAwait(false);
        try { await current.StopAsync(StopGracePeriod, cancellationToken).ConfigureAwait(false); } catch { }
        if (readTask is not null)
        {
            try { await readTask.ConfigureAwait(false); } catch { }
            readTask = null;
        }
        readCancellation?.Dispose();
        readCancellation = null;
        await current.DisposeAsync().ConfigureAwait(false);
    }

    private async Task ReadLoopAsync(
        IChildProcess child,
        TaskCompletionSource firstBytes,
        TaskCompletionSource<AudioCaptureException> startupFailure,
        CancellationToken cancellationToken)
    {
        // 20 ms of 48 kHz mono PCM16, matching the --latency the process was started with.
        var buffer = new byte[SampleRate / 50 * Channels * sizeof(short)];
        var elapsed = TimeSpan.Zero;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await child.StandardOutput.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    await Task.Delay(EmptyReadPollDelay, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                firstBytes.TrySetResult();
                if (paused) continue;
                var frame = new AudioFrame(buffer.AsSpan(0, read), SampleRate, Channels, AudioSampleFormat.Pcm16, elapsed);
                var level = AudioLevelCalculator.Calculate(buffer.AsSpan(0, read), AudioSampleFormat.Pcm16);
                elapsed += TimeSpan.FromSeconds(read / (double)(SampleRate * Channels * sizeof(short)));
                FrameAvailable?.Invoke(frame, level);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            var error = new AudioCaptureException(AudioCaptureError.PlatformFailure, "Reading the PipeWire microphone failed.", exception);
            if (!startupFailure.TrySetResult(error)) Faulted?.Invoke(error);
        }
    }
}
