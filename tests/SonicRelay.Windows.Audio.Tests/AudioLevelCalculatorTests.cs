namespace SonicRelay.Windows.Audio;

public sealed class AudioLevelCalculatorTests
{
    [Fact]
    public void SilentPcm16BufferProducesZeroLevel()
    {
        var data = new byte[8]; // four Int16 zero samples
        var level = AudioLevelCalculator.Calculate(data, AudioSampleFormat.Pcm16);
        Assert.Equal(0f, level.Peak);
        Assert.Equal(0f, level.Rms);
    }

    [Fact]
    public void FullScalePcm16SampleProducesPeakOne()
    {
        var data = BitConverter.GetBytes((short)32767);
        var level = AudioLevelCalculator.Calculate(data, AudioSampleFormat.Pcm16);
        Assert.True(level.Peak > 0.99f);
    }

    [Fact]
    public void FullScaleFloatSampleProducesPeakOne()
    {
        var data = BitConverter.GetBytes(1.0f);
        var level = AudioLevelCalculator.Calculate(data, AudioSampleFormat.IeeeFloat32);
        Assert.Equal(1f, level.Peak);
        Assert.Equal(1f, level.Rms);
    }

    [Fact]
    public void EmptyBufferProducesSilence()
    {
        var level = AudioLevelCalculator.Calculate(ReadOnlySpan<byte>.Empty, AudioSampleFormat.Pcm16);
        Assert.Equal(AudioLevelSnapshot.Silence, level);
    }
}
