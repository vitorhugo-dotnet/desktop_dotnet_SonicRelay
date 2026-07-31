using SonicRelay.Platform.Linux.Audio;
using SonicRelay.Platform.Linux.Tests.Fakes;
using SonicRelay.Windows.Audio;

namespace SonicRelay.Platform.Linux.Tests;

public sealed class PipeWireProcessBackendTests
{
    private const int BytesPerFrame = 3840;
    private static readonly PipeWireCommandPaths Paths = new("pw-dump", "pw-record", "wpctl", "secret-tool");

    private static readonly string[] ExpectedPwRecordArguments =
        ["--raw", "--rate=48000", "--channels=2", "--format=s16", "--latency=20ms", "--target=55", "-"];

    private const string DefaultInspectOutput = """
    id 55, type PipeWire:Interface:Node
     * node.name = "alsa_output.default"
     object.serial = "55"
    """;

    private static (PipeWireProcessBackend Backend, FakeLinuxProcessRunner Runner) CreateBackend(Func<string?>? preferred = null)
    {
        var runner = new FakeLinuxProcessRunner();
        runner.Script("wpctl", new LinuxProcessResult(0, DefaultInspectOutput, string.Empty));
        var resolver = new PipeWireSinkResolver(runner, Paths);
        var backend = new PipeWireProcessBackend(runner, Paths, resolver, preferred);
        return (backend, runner);
    }

    [Fact]
    public async Task StartAsyncLaunchesPwRecordWithTheResolvedTargetAndExplicitFormat()
    {
        var (backend, runner) = CreateBackend();
        var startTask = backend.StartAsync(CancellationToken.None);

        // StartAsync awaits the first frame; feed one immediately.
        await Task.Delay(50);
        runner.LastStartedProcess!.Write(new byte[BytesPerFrame]);
        await startTask;

        Assert.Equal("alsa_output.default", backend.Device!.Id);
        Assert.Single(runner.StartCalls);
        var (executable, arguments) = runner.StartCalls[0];
        Assert.Equal(Paths.PwRecord, executable);
        Assert.Equal(ExpectedPwRecordArguments, arguments);
    }

    [Fact]
    public async Task StartAsyncCompletesOnlyAfterFirstFrameArrives()
    {
        var (backend, runner) = CreateBackend();
        var startTask = backend.StartAsync(CancellationToken.None);

        await Task.Delay(50);
        Assert.False(startTask.IsCompleted);

        runner.LastStartedProcess!.Write(new byte[BytesPerFrame]);
        await startTask;
        Assert.True(startTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task FramesRaiseFrameAvailableWithPcm16Format()
    {
        var (backend, runner) = CreateBackend();
        AudioFrame? received = null;
        backend.FrameAvailable += (frame, _) => received ??= frame;

        var startTask = backend.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        runner.LastStartedProcess!.Write(new byte[BytesPerFrame]);
        await startTask;

        Assert.NotNull(received);
        Assert.Equal(AudioSampleFormat.Pcm16, received!.Format);
        Assert.Equal(48_000, received.SampleRate);
    }

    [Fact]
    public async Task UnexpectedProcessExitAfterStartupRaisesFaulted()
    {
        var (backend, runner) = CreateBackend();
        var startTask = backend.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        runner.LastStartedProcess!.Write(new byte[BytesPerFrame]);
        await startTask;

        AudioCaptureException? faulted = null;
        backend.Faulted += error => faulted = error;
        runner.LastStartedProcess!.RaiseExited(1);

        await Task.Delay(50);
        Assert.NotNull(faulted);
        Assert.Equal(AudioCaptureError.PlatformFailure, faulted!.Error);
    }

    [Fact]
    public async Task StopAsyncStopsTheProcessAndDoesNotRaiseFaulted()
    {
        var (backend, runner) = CreateBackend();
        var startTask = backend.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        runner.LastStartedProcess!.Write(new byte[BytesPerFrame]);
        await startTask;

        var process = runner.LastStartedProcess!;
        AudioCaptureException? faulted = null;
        backend.Faulted += error => faulted = error;

        var stopTask = backend.StopAsync(CancellationToken.None);
        process.RaiseExited(0); // simulates the real process exiting once StopAsync signals it
        await stopTask;

        Assert.Null(faulted);
        Assert.Null(backend.Device);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task DisposeAsyncIsIdempotent()
    {
        var (backend, _) = CreateBackend();
        await backend.DisposeAsync();
        await backend.DisposeAsync();
    }

    // --- Additional coverage for the lifecycle races flagged in the task brief. ---

    [Fact]
    public async Task ProcessExitingBeforeFirstFrameFailsStartupPromptlyWithoutRaisingFaulted()
    {
        // A bad `--target` (or any pre-first-frame failure) must fail StartAsync
        // itself rather than being silently swallowed or only surfacing after the
        // full 5s startup timeout, and it must not be reported through Faulted --
        // that event is for faults *after* a successful start, when a caller
        // actually has a running backend to react to.
        var (backend, runner) = CreateBackend();
        AudioCaptureException? faulted = null;
        var startTask = backend.StartAsync(CancellationToken.None);
        backend.Faulted += error => faulted = error;

        await Task.Delay(50);
        runner.LastStartedProcess!.RaiseExited(2);

        var error = await Assert.ThrowsAsync<AudioCaptureException>(() => startTask);
        Assert.Equal(AudioCaptureError.PlatformFailure, error.Error);
        Assert.Null(backend.Device);
        Assert.Null(faulted);
    }

    [Fact]
    public async Task StartAsyncAfterUnexpectedExitWithoutStopIsANoOp()
    {
        // Documents the restart invariant on StartAsync's XML doc: after an
        // unexpected exit raises Faulted, a caller must call StopAsync before
        // calling StartAsync again. AudioCaptureService (the only current
        // caller) always stops before restarting, so this is currently
        // unreachable in practice, but it must stay a deliberate no-op rather
        // than an accidental regression if that contract ever changes.
        var (backend, runner) = CreateBackend();
        var startTask = backend.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        runner.LastStartedProcess!.Write(new byte[BytesPerFrame]);
        await startTask;

        runner.LastStartedProcess!.RaiseExited(1);
        await Task.Delay(50);

        await backend.StartAsync(CancellationToken.None);

        Assert.Single(runner.StartCalls);
    }

    [Fact]
    public async Task CallerCancellationDuringStartupStopsTheProcessInsteadOfOrphaningIt()
    {
        // Regression coverage for the class of bug fixed in Task 4: cancelling the
        // caller's own token while StartAsync is still waiting for the first frame
        // must tear down the already-launched process, not leave it running.
        var (backend, runner) = CreateBackend();
        using var cancellation = new CancellationTokenSource();
        var startTask = backend.StartAsync(cancellation.Token);

        await Task.Delay(50);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startTask);
        Assert.True(runner.LastStartedProcess!.Disposed);
        Assert.Null(backend.Device);
    }
}
