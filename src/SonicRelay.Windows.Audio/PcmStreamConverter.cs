namespace SonicRelay.Windows.Audio;

/// <summary>
/// Converts a PCM16 stream from one sample rate and channel count to another, keeping the
/// fractional read position between calls so successive frames join without a click.
///
/// The resampler is linear interpolation, chosen deliberately: a remote peer sends 48 kHz Opus
/// and the overwhelmingly common Windows/PipeWire mix format is also 48 kHz, so the resampler
/// is a no-op on almost every machine and only earns its keep on a 44.1 kHz endpoint — where
/// speech quality is limited by the network long before it is limited by interpolation order.
/// </summary>
public sealed class PcmStreamConverter
{
    private readonly int sourceRate;
    private readonly int sourceChannels;
    private readonly int targetRate;
    private readonly int targetChannels;
    private readonly double step;

    // Fractional read position into the source stream, in frames, carried across calls.
    private double position;

    // The last source frame of the previous call, so interpolation at a call boundary has a
    // left-hand sample instead of restarting from silence.
    private readonly float[] previousFrame;
    private bool hasPreviousFrame;

    public PcmStreamConverter(int sourceRate, int sourceChannels, int targetRate, int targetChannels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetRate);
        if (sourceChannels is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(sourceChannels));
        if (targetChannels is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(targetChannels));
        this.sourceRate = sourceRate;
        this.sourceChannels = sourceChannels;
        this.targetRate = targetRate;
        this.targetChannels = targetChannels;
        step = (double)sourceRate / targetRate;
        previousFrame = new float[sourceChannels];
    }

    public bool IsPassthrough => sourceRate == targetRate && sourceChannels == targetChannels;

    /// <summary>
    /// Converts <paramref name="samples"/> (interleaved PCM16 at the source format) into
    /// interleaved normalized floats at the target format.
    /// </summary>
    public float[] Convert(ReadOnlySpan<short> samples)
    {
        var sourceFrames = samples.Length / sourceChannels;
        if (sourceFrames == 0) return [];

        // How many target frames this input can produce, given where the fractional read
        // position currently sits.
        var available = sourceFrames - position;
        if (available <= 0)
        {
            position -= sourceFrames;
            return [];
        }
        var targetFrames = (int)Math.Ceiling(available / step);
        if (targetFrames <= 0) return [];

        var output = new float[targetFrames * targetChannels];
        var frame = new float[sourceChannels];
        var next = new float[sourceChannels];
        var written = 0;

        for (var i = 0; i < targetFrames; i++)
        {
            var index = (int)Math.Floor(position);
            if (index >= sourceFrames) break;
            var fraction = position - index;

            ReadFrame(samples, sourceFrames, index, frame);
            ReadFrame(samples, sourceFrames, index + 1, next);
            for (var channel = 0; channel < sourceChannels; channel++)
            {
                frame[channel] += (float)((next[channel] - frame[channel]) * fraction);
            }

            MapChannels(frame, output.AsSpan(written * targetChannels, targetChannels));
            written++;
            position += step;
        }

        // Carry the remainder into the next call and remember the tail frame for its first
        // interpolation.
        position -= sourceFrames;
        if (position < 0) position = 0;
        ReadFrame(samples, sourceFrames, sourceFrames - 1, previousFrame);
        hasPreviousFrame = true;

        return written == targetFrames ? output : output[..(written * targetChannels)];
    }

    private void ReadFrame(ReadOnlySpan<short> samples, int sourceFrames, int index, Span<float> destination)
    {
        if (index < 0)
        {
            if (hasPreviousFrame) previousFrame.CopyTo(destination);
            else destination.Clear();
            return;
        }
        if (index >= sourceFrames)
        {
            // Past the end: hold the last sample rather than dropping to silence, which would
            // put a click at every buffer boundary.
            index = sourceFrames - 1;
        }
        var offset = index * sourceChannels;
        for (var channel = 0; channel < sourceChannels; channel++)
        {
            destination[channel] = samples[offset + channel] / 32768f;
        }
    }

    /// <summary>
    /// Maps one source frame onto the target channel layout: mono fans out to every channel,
    /// stereo folds down to mono as an average, and anything wider takes the source channels
    /// it has and leaves the rest silent (a centre/LFE gets no speech rather than a copy of it).
    /// </summary>
    private void MapChannels(ReadOnlySpan<float> source, Span<float> destination)
    {
        if (sourceChannels == targetChannels)
        {
            source.CopyTo(destination);
            return;
        }
        if (sourceChannels == 1)
        {
            destination.Fill(source[0]);
            return;
        }
        if (targetChannels == 1)
        {
            var sum = 0f;
            for (var channel = 0; channel < sourceChannels; channel++) sum += source[channel];
            destination[0] = sum / sourceChannels;
            return;
        }
        destination.Clear();
        var copy = Math.Min(sourceChannels, targetChannels);
        source[..copy].CopyTo(destination[..copy]);
    }
}
