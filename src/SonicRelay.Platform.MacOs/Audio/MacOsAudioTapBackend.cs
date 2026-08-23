using SonicRelay.Windows.Audio;
using SonicRelay.Windows.Core.Processes;

namespace SonicRelay.Platform.MacOs.Audio;

/// <summary>
/// Supervises exactly one <c>sonicrelay-audio-tap</c> process per instance,
/// capturing the macOS system audio mix through ScreenCaptureKit as raw PCM16
/// stereo 48 kHz on the helper's stdout (issue #62).
///
/// This mirrors the Linux <c>PipeWireProcessBackend</c> — same shared
/// <see cref="IChildProcessRunner"/> supervision and the same
/// <see cref="PcmFrameAssembler"/> framing — but keeps its own lifecycle rather
/// than sharing a base class, because the two platforms' start paths differ in
/// substance and not just in arguments: Linux resolves (and can fall back
/// between) sink targets on every start, while macOS has a single system-wide
/// target and instead has to distinguish a revoked privacy grant from a real
/// device fault. Folding both into one template would hide exactly the parts
/// that differ.
///
/// Pause performs a controlled stop and resume starts a new helper, matching
/// the Linux adapter: ScreenCaptureKit has no pause primitive, and the brief
/// discontinuity is preferable to holding an idle capture (and its privacy
/// indicator) open while paused.
/// </summary>
public sealed class MacOsAudioTapBackend : IAudioCaptureBackend
{
    /// <summary>
    /// Longer than the Linux adapter's 5 s: a cold ScreenCaptureKit stream has
    /// to negotiate with the window server, and on the very first run the
    /// helper may also be showing the Screen Recording consent prompt. A denied
    /// or broken helper still fails immediately through its exit code, so this
    /// only bounds the genuinely-slow-start case.
    /// </summary>
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StopGracePeriod = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan EmptyReadPollDelay = TimeSpan.FromMilliseconds(5);

    private const int SampleRate = 48_000;
    private const int ChannelCount = 2;

    /// <summary>
    /// ScreenCaptureKit captures the system output mix, not a chosen endpoint,
    /// so this device is fixed rather than resolved. See
    /// <see cref="MacOsOutputDeviceProbe"/> for why the picker is not offered
    /// per-endpoint entries on macOS.
    /// </summary>
    public static readonly AudioDeviceInfo SystemAudioDevice =
        new("system-audio-mix", "System audio (ScreenCaptureKit)", SampleRate, ChannelCount, AudioSampleFormat.Pcm16);

    private readonly IChildProcessRunner processRunner;
    private readonly string helperPath;
    private readonly ScreenRecordingPermissionProbe permissionProbe;

    // Serialises Start/Stop/Dispose so a Stop racing an in-flight Start cannot
    // observe half-set state or return having stopped nothing while Start goes
    // on to launch a helper the caller believes it already stopped.
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);

    private IChildProcess? process;
    private CancellationTokenSource? readCancellation;
    private Task? readTask;
    private Action<int>? processExitedHandler;
    private bool disposed;

    public MacOsAudioTapBackend(IChildProcessRunner processRunner, string helperPath, ScreenRecordingPermissionProbe? permissionProbe = null)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentException.ThrowIfNullOrWhiteSpace(helperPath);
        this.processRunner = processRunner;
        this.helperPath = helperPath;
        this.permissionProbe = permissionProbe ?? new ScreenRecordingPermissionProbe(processRunner, helperPath);
    }

    public AudioDeviceInfo? Device { get; private set; }
    public event Action<AudioFrame, AudioLevelSnapshot>? FrameAvailable;
    public event Action<AudioCaptureException>? Faulted;

    /// <summary>
    /// Starts supervising a new helper process. No-op if one is already tracked.
    /// </summary>
    /// <remarks>
    /// Invariant (identical to the Linux adapter): after an unexpected exit
    /// raises <see cref="Faulted"/>, the caller must call
    /// <see cref="StopAsync"/> before starting again — the dead process's
    /// fields are only cleared there, so a direct restart would short-circuit
    /// on the still-set, now-dead process. <c>AudioCaptureService</c>, the only
    /// caller, always stops before restarting.
    /// </remarks>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (process is not null) return;

            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var launchedProcess = processRunner.Start(helperPath, ["capture"]);
            var localReadCancellation = new CancellationTokenSource();

            // Both captured objects are fully constructed before the handler is
            // wired up, so it is safe even when IChildProcess replays `Exited`
            // synchronously to a late subscriber. It touches only this attempt's
            // locals, never the mutable fields, so it cannot race a later
            // Start/Stop cycle either.
            void OnProcessExited(int exitCode)
            {
                if (localReadCancellation.IsCancellationRequested) return; // an intentional Stop already cancelled reads
                var error = AudioTapExitCode.Map(exitCode);
                // The read loop cannot otherwise notice the process is gone (a
                // live pipe blocks rather than reporting EOF), so an unexpected
                // exit cancels it directly instead of leaving it polling.
                localReadCancellation.Cancel();
                // Before the first frame there is no started capture to fault;
                // fail StartAsync itself so a denied permission surfaces at once
                // rather than after the startup timeout.
                if (!started.TrySetException(error)) Faulted?.Invoke(error);
            }

            processExitedHandler = OnProcessExited;
            process = launchedProcess;
            readCancellation = localReadCancellation;
            launchedProcess.Exited += OnProcessExited;

            var assembler = new PcmFrameAssembler(SampleRate, ChannelCount);
            readTask = Task.Run(() => ReadLoopAsync(launchedProcess, assembler, started, localReadCancellation.Token), CancellationToken.None);

            using var startupTimeoutSource = new CancellationTokenSource(StartupTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, startupTimeoutSource.Token);
            try
            {
                await started.Task.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Clean up regardless of which token fired: a caller
                // cancellation must not leave the helper orphaned.
                await StopInternalAsync(CancellationToken.None).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested) throw;
                throw await DescribeStartupTimeoutAsync().ConfigureAwait(false);
            }
            catch (AudioCaptureException)
            {
                await StopInternalAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            Device = SystemAudioDevice;
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    /// <summary>
    /// A helper that starts but never delivers audio is almost always a privacy
    /// grant that was revoked after the process launched, which ScreenCaptureKit
    /// reports as silence rather than an error. Asking the non-prompting
    /// permission check turns an opaque "no audio" timeout into the actionable
    /// System Settings message — and, just as importantly, into
    /// <see cref="AudioCaptureError.AccessDenied"/>, which the capture service
    /// treats as terminal instead of retrying a grant only the user can restore.
    /// </summary>
    private async Task<AudioCaptureException> DescribeStartupTimeoutAsync()
    {
        var permission = await permissionProbe.CheckAsync(CancellationToken.None).ConfigureAwait(false);
        return permission == ScreenRecordingPermission.Denied
            ? AudioTapExitCode.Map(AudioTapExitCode.PermissionDenied)
            : new AudioCaptureException(
                AudioCaptureError.PlatformFailure,
                "macOS system audio capture did not produce audio within the startup timeout.");
    }

    private async Task ReadLoopAsync(IChildProcess launchedProcess, PcmFrameAssembler assembler, TaskCompletionSource started, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await launchedProcess.StandardOutput.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    // A live pipe does not return 0 spuriously, so this is not
                    // the hot path; it degrades the loop to a bounded poll
                    // instead of exiting against a stream that reports "nothing
                    // buffered yet" as a zero-length read. The process's own
                    // exit (via cancelling this token) is what ends the loop.
                    await Task.Delay(EmptyReadPollDelay, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                foreach (var (frame, level) in assembler.Append(buffer.AsSpan(0, read)))
                {
                    // Raise the frame before completing `started`: the
                    // completion source resumes StartAsync on another thread-pool
                    // hop, so signalling first would let a caller observe a
                    // started capture before its first frame had been delivered.
                    FrameAvailable?.Invoke(frame, level);
                    started.TrySetResult();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            var mapped = new AudioCaptureException(AudioCaptureError.PlatformFailure, "macOS system audio capture stream failed.", error);
            if (!started.TrySetException(mapped) && !cancellationToken.IsCancellationRequested) Faulted?.Invoke(mapped);
        }
    }

    /// <summary>Pause performs a controlled stop; ScreenCaptureKit has no pause primitive.</summary>
    public Task PauseAsync(CancellationToken cancellationToken) => StopAsync(cancellationToken);

    /// <summary>Resume starts a new helper process.</summary>
    public Task ResumeAsync(CancellationToken cancellationToken) => StartAsync(cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopInternalAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    /// <summary>Must only be called while holding <see cref="lifecycleGate"/>.</summary>
    private async Task StopInternalAsync(CancellationToken cancellationToken)
    {
        readCancellation?.Cancel();
        if (readTask is not null)
        {
            try { await readTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        if (process is not null)
        {
            if (processExitedHandler is not null) process.Exited -= processExitedHandler;
            // Closing stdin is the helper's clean shutdown signal; the grace
            // period then falls back to killing it, which also releases the
            // macOS screen-capture privacy indicator promptly.
            await process.StopAsync(StopGracePeriod, cancellationToken).ConfigureAwait(false);
            await process.DisposeAsync().ConfigureAwait(false);
        }
        process = null;
        readCancellation?.Dispose();
        readCancellation = null;
        readTask = null;
        processExitedHandler = null;
        Device = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        disposed = true;
        lifecycleGate.Dispose();
    }
}
