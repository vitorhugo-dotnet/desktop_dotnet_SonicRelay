using SonicRelay.Platform.Linux.Audio;
using SonicRelay.Tests.Shared.Fakes;
using SonicRelay.Windows.Audio;

namespace SonicRelay.Platform.Linux.Tests;

public sealed class PipeWireMicrophoneBackendTests
{
    private static readonly PipeWireCommandPaths Paths = new("pw-dump", "pw-record", "wpctl", "secret-tool", "pw-play");

    [Fact]
    public async Task StartsPwRecordAgainstTheDefaultSourceWithoutAnExplicitTarget()
    {
        var runner = new FakeChildProcessRunner();
        await using var backend = new PipeWireMicrophoneBackend(runner, Paths);

        var start = backend.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        runner.LastStartedProcess!.Write(new byte[1920]);
        await start;

        var (executable, arguments) = Assert.Single(runner.StartCalls);
        Assert.Equal("pw-record", executable);
        // No --target: the automatic target is the default *source*, which is exactly the
        // microphone the user chose in their desktop's sound settings. The output-capture
        // backend must name a sink explicitly for the opposite reason (ADR-LINUX-004).
        Assert.DoesNotContain(arguments, argument => argument.StartsWith("--target", StringComparison.Ordinal));
        Assert.Equal(
            ["--raw", "--rate=48000", "--channels=1", "--format=s16", "--latency=20ms", "-"],
            arguments);
    }

    [Fact]
    public async Task SurfacesCapturedAudioAsFrames()
    {
        var runner = new FakeChildProcessRunner();
        await using var backend = new PipeWireMicrophoneBackend(runner, Paths);
        var frames = new List<AudioFrame>();
        backend.FrameAvailable += (frame, _) => frames.Add(frame);

        var start = backend.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        runner.LastStartedProcess!.Write(new byte[1920]);
        await start;
        await Task.Delay(50);

        var frame = frames[0];
        Assert.Equal(48000, frame.SampleRate);
        Assert.Equal(1, frame.ChannelCount);
        Assert.Equal(AudioSampleFormat.Pcm16, frame.Format);
    }

    [Fact]
    public async Task AProcessThatDiesDuringStartupFailsTheStart()
    {
        var runner = new FakeChildProcessRunner();
        await using var backend = new PipeWireMicrophoneBackend(runner, Paths);

        var start = backend.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        runner.LastStartedProcess!.RaiseExited(1);

        var error = await Assert.ThrowsAsync<AudioCaptureException>(() => start);
        Assert.Equal(AudioCaptureError.PlatformFailure, error.Error);
    }

    [Fact]
    public async Task StoppingReleasesTheProcess()
    {
        var runner = new FakeChildProcessRunner();
        var backend = new PipeWireMicrophoneBackend(runner, Paths);
        var start = backend.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        runner.LastStartedProcess!.Write(new byte[1920]);
        await start;

        await backend.StopAsync(CancellationToken.None);

        Assert.True(runner.LastStartedProcess!.StopCount >= 1);
        Assert.Null(backend.Device);
        await backend.DisposeAsync();
    }
}

public sealed class PipeWirePlaybackBackendTests
{
    private static readonly PipeWireCommandPaths Paths = new("pw-dump", "pw-record", "wpctl", "secret-tool", "pw-play");

    [Fact]
    public async Task StartsPwPlayWithTheStreamFormat()
    {
        var runner = new FakeChildProcessRunner();
        await using var backend = new PipeWirePlaybackBackend(runner, Paths);

        await backend.StartAsync(48000, 2, CancellationToken.None);

        var (executable, arguments) = Assert.Single(runner.StartCalls);
        Assert.Equal("pw-play", executable);
        Assert.Equal(
            ["--playback", "--raw", "--rate=48000", "--channels=2", "--format=s16", "--latency=20ms", "-"],
            arguments);
    }

    [Fact]
    public async Task WritesSamplesToTheProcessAsLittleEndianPcm16()
    {
        var runner = new FakeChildProcessRunner();
        await using var backend = new PipeWirePlaybackBackend(runner, Paths);
        await backend.StartAsync(48000, 1, CancellationToken.None);

        backend.Write([0x0102, unchecked((short)0xF0FF)]);
        await WaitForAsync(() => runner.LastStartedProcess!.WrittenInput.Length >= 4);

        // `--format=s16` is s16le; writing host order would be noise on a big-endian machine
        // rather than a wrong-but-audible signal.
        Assert.Equal([0x02, 0x01, 0xFF, 0xF0], runner.LastStartedProcess!.WrittenInput.ToArray());
    }

    [Fact]
    public async Task RefusesToStartWhenPwPlayIsMissing()
    {
        var runner = new FakeChildProcessRunner();
        await using var backend = new PipeWirePlaybackBackend(runner, new PipeWireCommandPaths("pw-dump", "pw-record", "wpctl", null));

        var error = await Assert.ThrowsAsync<AudioCaptureException>(
            () => backend.StartAsync(48000, 1, CancellationToken.None));

        // A machine without the full PipeWire user tools keeps publishing; only the two-way
        // half is unavailable, and it says which package supplies it.
        Assert.Contains("pw-play", error.Message, StringComparison.Ordinal);
        Assert.Empty(runner.StartCalls);
    }

    [Fact]
    public async Task StoppingReleasesTheProcess()
    {
        var runner = new FakeChildProcessRunner();
        var backend = new PipeWirePlaybackBackend(runner, Paths);
        await backend.StartAsync(48000, 1, CancellationToken.None);

        await backend.StopAsync(CancellationToken.None);

        Assert.True(runner.LastStartedProcess!.StopCount >= 1);
        Assert.Null(backend.Device);
        await backend.DisposeAsync();
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition());
    }
}
