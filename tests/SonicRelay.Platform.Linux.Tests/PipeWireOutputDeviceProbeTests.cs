using SonicRelay.Platform.Linux.Audio;
using SonicRelay.Platform.Linux.Tests.Fakes;

namespace SonicRelay.Platform.Linux.Tests;

public sealed class PipeWireOutputDeviceProbeTests
{
    private static readonly PipeWireCommandPaths Paths = new("pw-dump", "pw-record", "wpctl", "secret-tool");

    private const string PwDumpJson = """
    [
      { "type": "PipeWire:Interface:Node", "info": { "props": {
          "media.class": "Audio/Sink", "node.name": "alsa_output.default", "node.description": "Speakers" } } },
      { "type": "PipeWire:Interface:Node", "info": { "props": {
          "media.class": "Audio/Sink", "node.name": "alsa_output.headset", "node.description": "Headset" } } }
    ]
    """;

    private const string DefaultInspectOutput = """
    id 55, type PipeWire:Interface:Node
     * node.name = "alsa_output.default"
     object.serial = "55"
    """;

    [Fact]
    public void GetOutputDevicesMarksTheDefaultSink()
    {
        var runner = new FakeLinuxProcessRunner();
        runner.Script("pw-dump", new LinuxProcessResult(0, PwDumpJson, string.Empty));
        runner.Script("wpctl", new LinuxProcessResult(0, DefaultInspectOutput, string.Empty));
        var probe = new PipeWireOutputDeviceProbe(runner, Paths);

        var devices = probe.GetOutputDevices();

        Assert.Equal(2, devices.Count);
        Assert.True(devices.Single(d => d.Id == "alsa_output.default").IsDefault);
        Assert.False(devices.Single(d => d.Id == "alsa_output.headset").IsDefault);
        Assert.Equal("Headset", devices.Single(d => d.Id == "alsa_output.headset").Name);
    }

    [Fact]
    public void GetOutputDevicesReturnsEmptyWhenDiscoveryFails()
    {
        var runner = new FakeLinuxProcessRunner();
        runner.Script("pw-dump", new LinuxProcessResult(1, string.Empty, "no session"));
        var probe = new PipeWireOutputDeviceProbe(runner, Paths);

        Assert.Empty(probe.GetOutputDevices());
    }

    [Fact]
    public void GetOutputDevicesStillReturnsSinksWhenDefaultLookupFails()
    {
        var runner = new FakeLinuxProcessRunner();
        runner.Script("pw-dump", new LinuxProcessResult(0, PwDumpJson, string.Empty));
        runner.Script("wpctl", new LinuxProcessResult(1, string.Empty, "no default"));
        var probe = new PipeWireOutputDeviceProbe(runner, Paths);

        var devices = probe.GetOutputDevices();

        Assert.Equal(2, devices.Count);
        Assert.DoesNotContain(devices, d => d.IsDefault);
    }
}
