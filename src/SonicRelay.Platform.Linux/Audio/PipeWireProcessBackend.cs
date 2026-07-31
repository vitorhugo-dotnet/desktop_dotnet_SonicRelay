using SonicRelay.Windows.Audio;

namespace SonicRelay.Platform.Linux.Audio;

/// <summary>
/// Supervises exactly one `pw-record` process per instance, capturing the
/// explicitly resolved sink target as raw PCM16 stereo 48 kHz. Pause performs
/// a controlled stop; resume re-resolves and starts a new process against the
/// same preferred sink — the small discontinuity is preferable to Unix signal
/// interop in the first release (spec: docs/superpowers/specs/2026-07-14-linux-desktop-publisher-design.md).
/// </summary>
public sealed class PipeWireProcessBackend : IAudioCaptureBackend
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StopGracePeriod = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan EmptyReadPollDelay = TimeSpan.FromMilliseconds(5);

    private readonly ILinuxProcessRunner processRunner;
    private readonly PipeWireCommandPaths commandPaths;
    private readonly PipeWireSinkResolver sinkResolver;
    private readonly Func<string?> preferredSinkNodeName;

    // Serializes Start/Stop/Dispose transitions. Without this, a Stop racing an
    // in-flight Start (e.g. while StartAsync is still awaiting sink resolution
    // or the startup timeout) could observe `process`/`readCancellation` in a
    // half-set state, or return having stopped nothing while Start goes on to
    // launch a process the caller believes it already stopped.
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);

    private ILinuxProcess? process;
    private CancellationTokenSource? readCancellation;
    private Task? readTask;
    private Action<int>? processExitedHandler;
    private bool disposed;

    public PipeWireProcessBackend(
        ILinuxProcessRunner processRunner,
        PipeWireCommandPaths commandPaths,
        PipeWireSinkResolver sinkResolver,
        Func<string?>? preferredSinkNodeName = null)
    {
        this.processRunner = processRunner;
        this.commandPaths = commandPaths;
        this.sinkResolver = sinkResolver;
        this.preferredSinkNodeName = preferredSinkNodeName ?? (() => null);
    }

    public AudioDeviceInfo? Device { get; private set; }
    public event Action<AudioFrame, AudioLevelSnapshot>? FrameAvailable;
    public event Action<AudioCaptureException>? Faulted;

    /// <summary>
    /// Starts supervising a new `pw-record` process. No-op if a process is
    /// already tracked.
    /// </summary>
    /// <remarks>
    /// Invariant: after an unexpected exit raises <see cref="Faulted"/>, the
    /// caller must call <see cref="StopAsync"/> before calling this again.
    /// The dead process's <c>process</c>/<c>readCancellation</c>/<c>readTask</c>
    /// fields are only cleared inside <see cref="StopInternalAsync"/>, so
    /// calling this directly after a <see cref="Faulted"/> notification —
    /// without an intervening <see cref="StopAsync"/> — is a no-op (the
    /// `process is not null` guard below short-circuits against the still-set,
    /// now-dead process). This matches the only current caller,
    /// <c>AudioCaptureService</c>, which always calls <see cref="StopAsync"/>
    /// before restarting.
    /// </remarks>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (process is not null) return;

            var preferredId = preferredSinkNodeName();
            var resolved = string.IsNullOrWhiteSpace(preferredId)
                ? await sinkResolver.ResolveDefaultAsync(cancellationToken).ConfigureAwait(false)
                : await sinkResolver.ResolveByNodeNameAsync(preferredId, cancellationToken).ConfigureAwait(false);

            var target = resolved.ObjectSerial ?? resolved.NodeName;
            string[] arguments =
            [
                "--raw", "--rate=48000", "--channels=2", "--format=s16", "--latency=20ms",
                $"--target={target}", "-"
            ];

            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var launchedProcess = processRunner.Start(commandPaths.PwRecord, arguments);
            var localReadCancellation = new CancellationTokenSource();

            // `started` and `localReadCancellation` are fully constructed before
            // this handler is ever wired up, so it is safe even if `Exited`
            // replays synchronously from inside the `+=` below (ILinuxProcess's
            // real implementation replays immediately to a subscriber that
            // attaches after the process has already exited — see
            // LinuxProcessRunner's late-subscription fix). It never touches the
            // mutable instance fields, only this start attempt's own locals, so
            // it can't race a subsequent Start/Stop cycle either.
            void OnProcessExited(int exitCode)
            {
                if (localReadCancellation.IsCancellationRequested) return; // an intentional Stop already cancelled reads
                var error = exitCode == 0
                    ? new AudioCaptureException(AudioCaptureError.DeviceLost, "The PipeWire capture process exited unexpectedly.")
                    : new AudioCaptureException(AudioCaptureError.PlatformFailure, $"pw-record exited with code {exitCode}.");
                // The read loop otherwise has no way to know the process is gone
                // (a live pipe blocks rather than reporting EOF spuriously — see
                // ReadLoopAsync), so an unexpected exit must cancel it directly
                // to avoid polling forever against a process that no longer exists.
                localReadCancellation.Cancel();
                // Before startup has completed there is no started capture for a
                // caller to react to via Faulted; fail StartAsync itself instead
                // (e.g. a bad --target fails fast rather than waiting out the
                // full startup timeout). Faulted is only for faults after a
                // successful start.
                if (!started.TrySetException(error)) Faulted?.Invoke(error);
            }

            processExitedHandler = OnProcessExited;
            process = launchedProcess;
            readCancellation = localReadCancellation;
            launchedProcess.Exited += OnProcessExited;

            var assembler = new PcmFrameAssembler();
            readTask = Task.Run(() => ReadLoopAsync(launchedProcess, assembler, started, localReadCancellation.Token), CancellationToken.None);

            using var startupTimeoutSource = new CancellationTokenSource(StartupTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, startupTimeoutSource.Token);
            try
            {
                await started.Task.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Clean up regardless of *which* token fired: a caller
                // cancellation must not leave the just-launched process
                // orphaned (the exact bug class fixed in LinuxProcessRunner).
                await StopInternalAsync(CancellationToken.None).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested) throw;
                throw new AudioCaptureException(AudioCaptureError.PlatformFailure, "PipeWire capture did not produce audio within the startup timeout.");
            }
            catch (AudioCaptureException)
            {
                await StopInternalAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            Device = new AudioDeviceInfo(resolved.NodeName, resolved.NodeName, 48_000, 2, AudioSampleFormat.Pcm16);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private async Task ReadLoopAsync(ILinuxProcess launchedProcess, PcmFrameAssembler assembler, TaskCompletionSource started, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await launchedProcess.StandardOutput.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    // A live pipe never returns 0 spuriously: ReadAsync blocks
                    // until data arrives or the writer closes it, so on a real
                    // `pw-record` process this branch is not on the hot path.
                    // It exists so the loop degrades to a bounded poll, rather
                    // than exiting outright, against a stream that reports "no
                    // data buffered yet" as a zero-length read instead of
                    // blocking for it. OnProcessExited (via cancelling this
                    // token) is what ends the loop once the process is truly
                    // gone; an explicit Stop does the same.
                    await Task.Delay(EmptyReadPollDelay, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                foreach (var (frame, level) in assembler.Append(buffer.AsSpan(0, read)))
                {
                    // Raise FrameAvailable before completing `started`: the
                    // completion source uses RunContinuationsAsynchronously, so
                    // the awaiting StartAsync resumes on another thread-pool
                    // hop. Signalling `started` first would let a caller observe
                    // StartAsync's completion before this event had actually
                    // fired for the first frame.
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
            var mapped = new AudioCaptureException(AudioCaptureError.PlatformFailure, "PipeWire capture stream failed.", error);
            if (!started.TrySetException(mapped) && !cancellationToken.IsCancellationRequested) Faulted?.Invoke(mapped);
        }
    }

    /// <summary>Pause performs a controlled stop; there is no separate pause primitive for pw-record.</summary>
    public Task PauseAsync(CancellationToken cancellationToken) => StopAsync(cancellationToken);

    /// <summary>Resume re-resolves the preferred sink and starts a new process.</summary>
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
