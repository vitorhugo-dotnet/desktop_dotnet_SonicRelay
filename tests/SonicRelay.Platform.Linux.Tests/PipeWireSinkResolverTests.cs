using SonicRelay.Platform.Linux.Audio;
using SonicRelay.Platform.Linux.Tests.Fakes;
using SonicRelay.Windows.Audio;

namespace SonicRelay.Platform.Linux.Tests;

public sealed class PipeWireSinkResolverTests
{
    private static readonly PipeWireCommandPaths Paths = new("pw-dump", "pw-record", "wpctl", "secret-tool");
    private static readonly string[] ExpectedInspectDefaultSinkArguments = ["inspect", "@DEFAULT_AUDIO_SINK@"];

    private const string DefaultInspectOutput = """
    id 55, type PipeWire:Interface:Node
     * node.name = "alsa_output.default"
     object.serial = "55"
    """;

    private const string PwDumpJson = """
    [
      { "type": "PipeWire:Interface:Node", "info": { "props": {
          "media.class": "Audio/Sink", "node.name": "alsa_output.default" } } },
      { "type": "PipeWire:Interface:Node", "info": { "props": {
          "media.class": "Audio/Sink", "node.name": "alsa_output.headset" } } }
    ]
    """;

    [Fact]
    public async Task ResolveDefaultReturnsTheInspectedDefaultSink()
    {
        var runner = new FakeLinuxProcessRunner();
        runner.Script("wpctl", new LinuxProcessResult(0, DefaultInspectOutput, string.Empty));
        var resolver = new PipeWireSinkResolver(runner, Paths);

        var resolved = await resolver.ResolveDefaultAsync(CancellationToken.None);

        Assert.Equal("alsa_output.default", resolved.NodeName);
        Assert.Equal("55", resolved.ObjectSerial);
        Assert.Contains(runner.RunCalls, call => call.Executable == "wpctl" && call.Arguments.SequenceEqual(ExpectedInspectDefaultSinkArguments));
    }

    [Fact]
    public async Task ResolveDefaultThrowsNoDeviceWhenInspectFails()
    {
        var runner = new FakeLinuxProcessRunner();
        runner.Script("wpctl", new LinuxProcessResult(1, string.Empty, "no default sink"));
        var resolver = new PipeWireSinkResolver(runner, Paths);

        var exception = await Assert.ThrowsAsync<AudioCaptureException>(() => resolver.ResolveDefaultAsync(CancellationToken.None));
        Assert.Equal(AudioCaptureError.NoDevice, exception.Error);
    }

    [Fact]
    public async Task ResolveDefaultThrowsNoDeviceWhenInspectOutputIsUnparseable()
    {
        var runner = new FakeLinuxProcessRunner();
        runner.Script("wpctl", new LinuxProcessResult(0, "not a valid inspect tree", string.Empty));
        var resolver = new PipeWireSinkResolver(runner, Paths);

        var exception = await Assert.ThrowsAsync<AudioCaptureException>(() => resolver.ResolveDefaultAsync(CancellationToken.None));
        Assert.Equal(AudioCaptureError.NoDevice, exception.Error);
    }

    [Fact]
    public async Task ResolveByNodeNameInspectsTheLiveNodeWhenStillPresent()
    {
        var runner = new FakeLinuxProcessRunner();
        runner.Script("pw-dump", new LinuxProcessResult(0, PwDumpJson, string.Empty));
        runner.Script("wpctl", new LinuxProcessResult(0, """
        id 60, type PipeWire:Interface:Node
         * node.name = "alsa_output.headset"
         object.serial = "60"
        """, string.Empty));
        var resolver = new PipeWireSinkResolver(runner, Paths);

        var resolved = await resolver.ResolveByNodeNameAsync("alsa_output.headset", CancellationToken.None);

        Assert.Equal("alsa_output.headset", resolved.NodeName);
        Assert.Equal("60", resolved.ObjectSerial);
    }

    [Fact]
    public async Task ResolveByNodeNameFallsBackToDefaultWhenSinkIsGone()
    {
        var runner = new FakeLinuxProcessRunner();
        runner.Script("pw-dump", new LinuxProcessResult(0, PwDumpJson, string.Empty));
        runner.Script("wpctl", new LinuxProcessResult(0, DefaultInspectOutput, string.Empty));
        var resolver = new PipeWireSinkResolver(runner, Paths);

        var resolved = await resolver.ResolveByNodeNameAsync("alsa_output.unplugged", CancellationToken.None);

        Assert.Equal("alsa_output.default", resolved.NodeName);
    }

    [Fact]
    public async Task ResolveByNodeNameFallsBackToDefaultWhenDiscoveryFails()
    {
        var runner = new FakeLinuxProcessRunner();
        runner.Script("pw-dump", new LinuxProcessResult(1, string.Empty, "discovery failed"));
        runner.Script("wpctl", new LinuxProcessResult(0, DefaultInspectOutput, string.Empty));
        var resolver = new PipeWireSinkResolver(runner, Paths);

        var resolved = await resolver.ResolveByNodeNameAsync("alsa_output.headset", CancellationToken.None);

        Assert.Equal("alsa_output.default", resolved.NodeName);
    }

    [Fact]
    public async Task ResolveByNodeNameFallsBackToDefaultWhenSelectedSinkInspectFails()
    {
        var runner = new FakeLinuxProcessRunner();
        runner.Script("pw-dump", new LinuxProcessResult(0, PwDumpJson, string.Empty));
        // Only one scripted result per executable is supported by the fake, so the
        // wpctl call always returns the default sink's inspect output regardless of
        // whether it targets the selected node or @DEFAULT_AUDIO_SINK@. This still
        // exercises the fallback path when the selected node's inspect call fails.
        runner.Script("wpctl", new LinuxProcessResult(1, string.Empty, "no such node"));
        var resolver = new PipeWireSinkResolver(runner, Paths);

        var exception = await Assert.ThrowsAsync<AudioCaptureException>(
            () => resolver.ResolveByNodeNameAsync("alsa_output.headset", CancellationToken.None));

        Assert.Equal(AudioCaptureError.NoDevice, exception.Error);
    }
}
