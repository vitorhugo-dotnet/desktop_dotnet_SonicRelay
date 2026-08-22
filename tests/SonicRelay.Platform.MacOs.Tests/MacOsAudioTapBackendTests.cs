using SonicRelay.Platform.MacOs.Audio;
using SonicRelay.Tests.Shared.Fakes;
using SonicRelay.Windows.Audio;
using SonicRelay.Windows.Core.Processes;

namespace SonicRelay.Platform.MacOs.Tests;

public sealed class MacOsAudioTapBackendTests
{
    private const int BytesPerFrame = 3840; // 48 kHz * 20 ms * 2 channels * 2 bytes
    private const string HelperPath = "/Applications/SonicRelay.app/Contents/MacOS/sonicrelay-audio-tap";

    private static (MacOsAudioTapBackend Backend, FakeChildProcessRunner Runner) CreateBackend()
    {
        var runner = new FakeChildProcessRunner();
        return (new MacOsAudioTapBackend(runner, HelperPath), runner);
    }

    [Fact]
    public async Task StartAsyncLaunchesTheBundledHelperInCaptureMode()
    {
        var (backend, runner) = CreateBackend();
        var startTask = backend.StartAsync(CancellationToken.None);

        // StartAsync only completes once the first frame arrives; feed one.
        await Task.Delay(50);
        runner.LastStartedProcess!.Write(new byte[BytesPerFrame]);
        await startTask;

        var (executable, arguments) = Assert.Single(runner.StartCalls);
        Assert.Equal(HelperPath, executable);
        Assert.Equal(["capture"], arguments);
    }

    [Fact]
    public async Task StartAsyncReportsTheFixedSystemMixDevice()
    {
        var (backend, runner) = CreateBackend();
        var startTask = backend.StartAsync(CancellationToken.None);

        await Task.Delay(50);
        runner.LastStartedProcess!.Write(new byte[BytesPerFrame]);
        await startTask;

        Assert.Equal(MacOsAudioTapBackend.SystemAudioDevice, backend.Device);
        Assert.Equal(48_000, backend.Device!.SampleRate);
        Assert.Equal(2, backend.Device.ChannelCount);
        Assert.Equal(AudioSampleFormat.Pcm16, backend.Device.Format);
    }

    [Fact]
    public async Task StartAsyncCompletesOnlyAfterTheFirstFrameArrives()
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
    public async Task HelperStdoutIsFramedIntoTwentyMillisecondPcm16Frames()
    {
        var (backend, runner) = CreateBackend();
        var frames = new List<AudioFrame>();
        backend.FrameAvailable += (frame, _) => frames.Add(frame);

        var startTask = backend.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        runner.LastStartedProcess!.Write(new byte[BytesPerFrame * 3]);
        await startTask;
        await Task.Delay(50);

        Assert.True(frames.Count >= 3);
        Assert.All(frames, frame =>
        {
            Assert.Equal(BytesPerFrame, frame.Data.Length);
            Assert.Equal(48_000, frame.SampleRate);
            Assert.Equal(2, frame.ChannelCount);
            Assert.Equal(AudioSampleFormat.Pcm16, frame.Format);
        });
    }

    /// <summary>
    /// A denied Screen Recording grant must fail the start attempt immediately
    /// rather than waiting out the startup timeout — and must surface as
    /// AccessDenied, which AudioCaptureService treats as terminal instead of
    /// retrying something only the user can fix in System Settings.
    /// </summary>
    [Fact]
    public async Task PermissionDeniedExitFailsStartImmediatelyAsAccessDenied()
    {
        var (backend, runner) = CreateBackend();
        var startTask = backend.StartAsync(CancellationToken.None);

        await Task.Delay(50);
        runner.LastStartedProcess!.RaiseExited(AudioTapExitCode.PermissionDenied);

        var error = await Assert.ThrowsAsync<AudioCaptureException>(() => startTask);
        Assert.Equal(AudioCaptureError.AccessDenied, error.Error);
        Assert.Contains("System Settings", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoCapturableDisplayExitFailsStartAsNoDevice()
    {
        var (backend, runner) = CreateBackend();
        var startTask = backend.StartAsync(CancellationToken.None);

        await Task.Delay(50);
        runner.LastStartedProcess!.RaiseExited(AudioTapExitCode.Unavailable);

        var error = await Assert.ThrowsAsync<AudioCaptureException>(() => startTask);
        Assert.Equal(AudioCaptureError.NoDevice, error.Error);
    }

    /// <summary>
    /// After a capture is running, the same unexpected exit is a fault on the
    /// live capture rather than a failed start, so it must reach subscribers
    /// through Faulted (which drives the shared recovery policy).
    /// </summary>
    [Fact]
    public async Task UnexpectedExitAfterStartRaisesFaultedAsDeviceLost()
    {
        var (backend, runner) = CreateBackend();
        AudioCaptureException? faulted = null;
        backend.Faulted += error => faulted = error;

        var startTask = backend.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        var process = runner.LastStartedProcess!;
        process.Write(new byte[BytesPerFrame]);
        await startTask;

        process.RaiseExited(AudioTapExitCode.Success);
        await Task.Delay(50);

        Assert.NotNull(faulted);
        Assert.Equal(AudioCaptureError.DeviceLost, faulted!.Error);
    }

    [Fact]
    public async Task StopAsyncStopsAndDisposesTheHelperProcess()
    {
        var (backend, runner) = CreateBackend();
        var startTask = backend.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        var process = runner.LastStartedProcess!;
        process.Write(new byte[BytesPerFrame]);
        await startTask;

        await backend.StopAsync(CancellationToken.None);

        Assert.Equal(1, process.StopCount);
        Assert.True(process.Disposed);
        Assert.Null(backend.Device);
    }

    /// <summary>
    /// An intentional stop must not be reported as a capture fault, however the
    /// helper's own exit happens to race the stop.
    /// </summary>
    [Fact]
    public async Task ExitDuringAnIntentionalStopDoesNotRaiseFaulted()
    {
        var (backend, runner) = CreateBackend();
        AudioCaptureException? faulted = null;
        backend.Faulted += error => faulted = error;

        var startTask = backend.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        var process = runner.LastStartedProcess!;
        process.Write(new byte[BytesPerFrame]);
        await startTask;

        await backend.StopAsync(CancellationToken.None);
        process.RaiseExited(AudioTapExitCode.InternalFailure);
        await Task.Delay(50);

        Assert.Null(faulted);
    }

    [Fact]
    public async Task StartAsyncIsANoOpWhileAHelperIsAlreadyRunning()
    {
        var (backend, runner) = CreateBackend();
        var startTask = backend.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        runner.LastStartedProcess!.Write(new byte[BytesPerFrame]);
        await startTask;

        await backend.StartAsync(CancellationToken.None);

        Assert.Single(runner.StartCalls);
    }

    /// <summary>Pause is a controlled stop and resume starts a fresh helper.</summary>
    [Fact]
    public async Task PauseStopsTheHelperAndResumeStartsANewOne()
    {
        var (backend, runner) = CreateBackend();
        var startTask = backend.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        runner.LastStartedProcess!.Write(new byte[BytesPerFrame]);
        await startTask;

        await backend.PauseAsync(CancellationToken.None);
        Assert.Null(backend.Device);

        var resumeTask = backend.ResumeAsync(CancellationToken.None);
        await Task.Delay(50);
        runner.LastStartedProcess!.Write(new byte[BytesPerFrame]);
        await resumeTask;

        Assert.Equal(2, runner.StartCalls.Count);
        Assert.Equal(MacOsAudioTapBackend.SystemAudioDevice, backend.Device);
    }

    [Fact]
    public async Task DisposeAsyncStopsAHelperThatIsStillRunning()
    {
        var (backend, runner) = CreateBackend();
        var startTask = backend.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        var process = runner.LastStartedProcess!;
        process.Write(new byte[BytesPerFrame]);
        await startTask;

        await backend.DisposeAsync();

        Assert.True(process.Disposed);
    }

    [Fact]
    public void ConstructionRejectsAMissingHelperPath()
    {
        Assert.Throws<ArgumentException>(() => new MacOsAudioTapBackend(new FakeChildProcessRunner(), "  "));
    }
}
