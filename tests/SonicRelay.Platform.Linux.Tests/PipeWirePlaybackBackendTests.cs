using SonicRelay.Platform.Linux.Audio;
using SonicRelay.Tests.Shared.Fakes;
using SonicRelay.Windows.Audio;

namespace SonicRelay.Platform.Linux.Tests;

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
