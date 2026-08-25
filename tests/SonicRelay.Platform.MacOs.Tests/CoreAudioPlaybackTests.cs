using SonicRelay.Platform.MacOs.Audio;
using SonicRelay.Windows.Audio;

namespace SonicRelay.Platform.MacOs.Tests;

/// <summary>
/// What a non-macOS agent can check about the CoreAudio backend: the stream description it
/// hands the device, and that it refuses rather than calling into a framework that is not
/// there. The queue itself needs a Mac.
/// </summary>
public sealed class CoreAudioStreamFormatTests
{
    [Fact]
    public void DescribesInterleavedPackedLittleEndianPcm16()
    {
        var format = CoreAudioStreamFormat.CreatePcm16(48000, 2);

        Assert.Equal(48000d, format.SampleRate);
        Assert.Equal(CoreAudioStreamFormat.LinearPcmFormat, format.FormatId);
        Assert.Equal(
            CoreAudioStreamFormat.FormatFlagIsSignedInteger | CoreAudioStreamFormat.FormatFlagIsPacked,
            format.FormatFlags);
        Assert.Equal(2u, format.ChannelsPerFrame);
        Assert.Equal(16u, format.BitsPerChannel);
        // Interleaved PCM is one packet per frame, so the two byte counts agree.
        Assert.Equal(4u, format.BytesPerFrame);
        Assert.Equal(4u, format.BytesPerPacket);
        Assert.Equal(1u, format.FramesPerPacket);
    }

    [Fact]
    public void MonoHalvesTheFrame()
    {
        var format = CoreAudioStreamFormat.CreatePcm16(48000, 1);

        Assert.Equal(1u, format.ChannelsPerFrame);
        Assert.Equal(2u, format.BytesPerFrame);
    }

    [Fact]
    public void NoBigEndianFlagIsSet()
    {
        // Absent means little-endian, which is what the Opus decoder produces and what every
        // Mac this runs on uses natively. Setting it would play noise.
        const uint bigEndian = 1 << 1;
        Assert.Equal(0u, CoreAudioStreamFormat.CreatePcm16(48000, 2).FormatFlags & bigEndian);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(48000, 0)]
    [InlineData(48000, 3)]
    public void RejectsAFormatTheQueueCouldNotOpen(int sampleRate, int channels)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CoreAudioStreamFormat.CreatePcm16(sampleRate, channels));
    }
}

// The backend is declared macOS-only, and these tests exist precisely to pin down what it
// does when it is reached anywhere else — so the platform-compatibility warning is expected
// here and nowhere else.
#pragma warning disable CA1416
public sealed class CoreAudioPlaybackBackendTests
{
    [Fact]
    public async Task RefusesToStartAwayFromMacOs()
    {
        if (OperatingSystem.IsMacOS()) return;

        var backend = new CoreAudioPlaybackBackend();

        var error = await Assert.ThrowsAsync<AudioCaptureException>(
            () => backend.StartAsync(48000, 2, CancellationToken.None));

        // A clear refusal rather than a missing-library crash: the composition root only wires
        // this up on macOS, so reaching it elsewhere is a bug worth naming.
        Assert.Equal(AudioCaptureError.PlatformFailure, error.Error);
        Assert.Contains("macOS", error.Message, StringComparison.Ordinal);
        await backend.DisposeAsync();
    }

    [Fact]
    public async Task WritingBeforeStartIsHarmless()
    {
        var backend = new CoreAudioPlaybackBackend();

        // Write runs on the WebRTC receive path, so it must never throw — not even when the
        // device was never opened.
        backend.Write([1, 2, 3, 4]);

        Assert.Null(backend.Device);
        await backend.StopAsync(CancellationToken.None);
        await backend.DisposeAsync();
    }
}
#pragma warning restore CA1416
