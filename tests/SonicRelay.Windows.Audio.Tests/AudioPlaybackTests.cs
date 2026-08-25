using SonicRelay.Windows.Audio;

namespace SonicRelay.Windows.Audio.Tests;

public sealed class AudioPlaybackServiceTests
{
    [Fact]
    public async Task TheFirstFrameOpensTheEndpointWithItsFormat()
    {
        var backend = new FakePlaybackBackend();
        await using var service = new AudioPlaybackService(backend);

        service.Play([1, 2, 3, 4], 48000, 2);
        await backend.Started.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal((48000, 2), backend.StartedWith);
        Assert.Equal(AudioPlaybackState.Playing, service.State);
        // The opening frames are dropped rather than buffered against a device that is not
        // open yet: a moment of speech costs less than a queue that plays late all call.
        Assert.Empty(backend.Written);
    }

    [Fact]
    public async Task SubsequentFramesAreWrittenThrough()
    {
        var backend = new FakePlaybackBackend();
        await using var service = new AudioPlaybackService(backend);
        service.Play([1], 48000, 1);
        await backend.Started.WaitAsync(TimeSpan.FromSeconds(5));

        service.Play([7, 8], 48000, 1);

        Assert.Equal([7, 8], Assert.Single(backend.Written));
    }

    [Fact]
    public async Task AFormatChangeReopensTheEndpoint()
    {
        var backend = new FakePlaybackBackend();
        await using var service = new AudioPlaybackService(backend);
        service.Play([1], 48000, 1);
        await backend.Started.WaitAsync(TimeSpan.FromSeconds(5));
        backend.Reset();

        // A peer that renegotiates from mono to stereo must not keep playing through a mono
        // endpoint, which would halve its speed.
        service.Play([1, 2], 48000, 2);
        await backend.Started.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal((48000, 2), backend.StartedWith);
        Assert.Equal(2, backend.StartCount);
    }

    [Fact]
    public async Task AFaultedDeviceRecoversOnTheNextFrame()
    {
        var backend = new FakePlaybackBackend();
        await using var service = new AudioPlaybackService(backend);
        service.Play([1], 48000, 1);
        await backend.Started.WaitAsync(TimeSpan.FromSeconds(5));
        backend.Reset();

        backend.Fault(new AudioCaptureException(AudioCaptureError.DeviceLost, "unplugged"));
        Assert.Equal(AudioPlaybackState.Faulted, service.State);

        service.Play([1], 48000, 1);
        await backend.Started.WaitAsync(TimeSpan.FromSeconds(5));

        // Faulted, not stopped: a playback device that was unplugged and plugged back in
        // recovers on its own without anything having to notice and restart it.
        Assert.Equal(AudioPlaybackState.Playing, service.State);
    }

    [Fact]
    public async Task AFailingStartLeavesTheServiceFaultedInsteadOfThrowing()
    {
        var backend = new FakePlaybackBackend { StartException = new AudioCaptureException(AudioCaptureError.NoDevice, "no device") };
        await using var service = new AudioPlaybackService(backend);

        service.Play([1], 48000, 1);
        await WaitForAsync(() => service.State == AudioPlaybackState.Faulted);

        // Play is called from the WebRTC receive path: throwing there would look like a
        // connection failure rather than a missing speaker.
        Assert.Equal(AudioCaptureError.NoDevice, service.LastError!.Code);
    }

    [Fact]
    public async Task StoppingReleasesTheEndpoint()
    {
        var backend = new FakePlaybackBackend();
        await using var service = new AudioPlaybackService(backend);
        service.Play([1], 48000, 1);
        await backend.Started.WaitAsync(TimeSpan.FromSeconds(5));

        await service.StopAsync();

        Assert.Equal(AudioPlaybackState.Stopped, service.State);
        Assert.True(backend.StopCount >= 1);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(48000, 0)]
    [InlineData(48000, 3)]
    public async Task AnUnusableFormatIsIgnored(int sampleRate, int channels)
    {
        var backend = new FakePlaybackBackend();
        await using var service = new AudioPlaybackService(backend);

        service.Play([1], sampleRate, channels);

        Assert.Equal(0, backend.StartCount);
        Assert.Equal(AudioPlaybackState.Stopped, service.State);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition());
    }

    private sealed class FakePlaybackBackend : IAudioPlaybackBackend
    {
        private SemaphoreSlim started = new(0);

        public AudioDeviceInfo? Device { get; private set; }
        public (int SampleRate, int Channels)? StartedWith { get; private set; }
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public List<short[]> Written { get; } = [];
        public Exception? StartException { get; set; }

        public SemaphoreSlim Started => started;

        public event Action<AudioCaptureException>? Faulted;

        public void Fault(AudioCaptureException error) => Faulted?.Invoke(error);

        public void Reset()
        {
            started = new SemaphoreSlim(0);
            Written.Clear();
        }

        public Task StartAsync(int sampleRate, int channelCount, CancellationToken cancellationToken)
        {
            if (StartException is not null) return Task.FromException(StartException);
            StartCount++;
            StartedWith = (sampleRate, channelCount);
            Device = new AudioDeviceInfo("fake", "Fake", sampleRate, channelCount, AudioSampleFormat.IeeeFloat32);
            started.Release();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            StopCount++;
            return Task.CompletedTask;
        }

        public void Write(ReadOnlySpan<short> samples) => Written.Add(samples.ToArray());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public sealed class PcmStreamConverterTests
{
    [Fact]
    public void PassesMatchingFormatsThroughUnchanged()
    {
        var converter = new PcmStreamConverter(48000, 2, 48000, 2);
        Assert.True(converter.IsPassthrough);

        var output = converter.Convert([16384, -16384, 32767, -32768]);

        Assert.Equal(4, output.Length);
        Assert.Equal(0.5f, output[0], 3);
        Assert.Equal(-0.5f, output[1], 3);
        Assert.Equal(1f, output[2], 3);
        Assert.Equal(-1f, output[3], 3);
    }

    [Fact]
    public void FansMonoOutToEveryTargetChannel()
    {
        var converter = new PcmStreamConverter(48000, 1, 48000, 2);

        var output = converter.Convert([16384, -16384]);

        Assert.Equal(4, output.Length);
        Assert.Equal(output[0], output[1], 5);
        Assert.Equal(output[2], output[3], 5);
    }

    [Fact]
    public void FoldsStereoDownToMonoAsAnAverage()
    {
        var converter = new PcmStreamConverter(48000, 2, 48000, 1);

        var output = converter.Convert([32767, 0]);

        Assert.Equal(0.5f, Assert.Single(output), 2);
    }

    [Fact]
    public void ResamplesDownToASlowerEndpoint()
    {
        var converter = new PcmStreamConverter(48000, 1, 24000, 1);

        var output = converter.Convert(new short[480]);

        // Half the rate, half the frames — the ratio is what matters, not the exact count,
        // since the fractional position carries between calls.
        Assert.InRange(output.Length, 239, 241);
    }

    [Fact]
    public void KeepsTheStreamContinuousAcrossCalls()
    {
        var converter = new PcmStreamConverter(48000, 1, 44100, 1);

        var total = 0;
        for (var i = 0; i < 50; i++) total += converter.Convert(new short[480]).Length;

        // 50 blocks of 10 ms at 48 kHz is half a second, which is ~22050 frames at 44.1 kHz.
        // A converter that reset its position each call would drift well outside this.
        Assert.InRange(total, 22000, 22100);
    }

    [Fact]
    public void ProducesNothingForAnEmptyBlock()
    {
        var converter = new PcmStreamConverter(48000, 2, 48000, 2);
        Assert.Empty(converter.Convert([]));
    }
}
