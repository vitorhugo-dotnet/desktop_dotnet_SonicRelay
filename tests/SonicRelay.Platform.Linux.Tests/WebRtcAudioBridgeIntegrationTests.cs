using SonicRelay.Platform.Linux.Audio;
using SonicRelay.Platform.Linux.Tests.Fakes;
using SonicRelay.Windows.Audio;
using SonicRelay.Windows.Presentation;
using SonicRelay.Windows.Signaling;
using SonicRelay.Windows.WebRtc;

namespace SonicRelay.Platform.Linux.Tests;

internal sealed class FakeWebRtcPublisher : IWebRtcPublisher
{
    public List<WebRtcAudioFrame> PushedFrames { get; } = [];
    public WebRtcPublisherDiagnostics Diagnostics { get; } = new(0, []);
    public event Action<WebRtcPublisherDiagnostics>? DiagnosticsChanged;
    public event Action<string>? IceRestartRequested;

    public Task HandleAsync(SignalingMessageEnvelope message, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PushAudioFrameAsync(WebRtcAudioFrame frame, CancellationToken cancellationToken = default)
    {
        PushedFrames.Add(frame);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class WebRtcAudioBridgeIntegrationTests
{
    private const int BytesPerFrame = 3840;
    private static readonly PipeWireCommandPaths Paths = new("pw-dump", "pw-record", "wpctl", "secret-tool");

    private const string DefaultInspectOutput = """
    id 55, type PipeWire:Interface:Node
     * node.name = "alsa_output.default"
     object.serial = "55"
    """;

    [Fact]
    public async Task FramesCapturedByThePipeWireBackendReachTheWebRtcPublisher()
    {
        var runner = new FakeLinuxProcessRunner();
        runner.Script("wpctl", new LinuxProcessResult(0, DefaultInspectOutput, string.Empty));
        var resolver = new PipeWireSinkResolver(runner, Paths);
        var backend = new PipeWireProcessBackend(runner, Paths, resolver);
        var probe = new PipeWireOutputDeviceProbe(runner, Paths);

        await using var audio = AudioCaptureService.Create(backend, probe);
        var publisher = new FakeWebRtcPublisher();
        await using var bridge = new WebRtcAudioBridge(audio, publisher);

        var startTask = audio.StartAsync();
        await Task.Delay(50);
        runner.LastStartedProcess!.Write(new byte[BytesPerFrame]);
        await startTask;

        // The frame written above unblocks PipeWireProcessBackend's own `started`
        // signal before AudioCaptureService transitions out of "Starting". That frame
        // is still delivered to the bridge (AudioCaptureService.OnFrameAvailable now
        // accepts frames raised during Starting/Recovering, not just Capturing), but
        // write a second frame here too so the assertions below exercise steady-state
        // Capturing delivery as well, not only the startup-race frame.
        runner.LastStartedProcess.Write(new byte[BytesPerFrame]);

        await WaitUntilAsync(() => publisher.PushedFrames.Count > 0, TimeSpan.FromSeconds(2));

        Assert.NotEmpty(publisher.PushedFrames);
        var pushed = publisher.PushedFrames[0];
        Assert.Equal(48_000, pushed.SampleRate);
        Assert.Equal(2, pushed.ChannelCount);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition was not met in time.");
            await Task.Delay(10);
        }
    }
}
