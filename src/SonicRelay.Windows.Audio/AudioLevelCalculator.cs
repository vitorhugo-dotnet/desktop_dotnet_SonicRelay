namespace SonicRelay.Windows.Audio;

/// <summary>
/// Pure PCM16/IeeeFloat32 peak+RMS level calculation shared by every capture
/// backend (WASAPI, PipeWire — issue #32) so level math is not duplicated per
/// platform.
/// </summary>
public static class AudioLevelCalculator
{
    public static AudioLevelSnapshot Calculate(ReadOnlySpan<byte> data, AudioSampleFormat format)
    {
        double sum = 0;
        float peak = 0;
        var count = format == AudioSampleFormat.IeeeFloat32 ? data.Length / 4 : data.Length / 2;
        for (var i = 0; i < count; i++)
        {
            var sample = format == AudioSampleFormat.IeeeFloat32
                ? Math.Clamp(BitConverter.ToSingle(data.Slice(i * 4, 4)), -1f, 1f)
                : BitConverter.ToInt16(data.Slice(i * 2, 2)) / 32768f;
            var absolute = Math.Abs(sample);
            peak = Math.Max(peak, absolute);
            sum += sample * sample;
        }
        var rms = count == 0 ? 0 : (float)Math.Sqrt(sum / count);
        return new AudioLevelSnapshot(Math.Clamp(peak, 0, 1), Math.Clamp(rms, 0, 1));
    }
}
