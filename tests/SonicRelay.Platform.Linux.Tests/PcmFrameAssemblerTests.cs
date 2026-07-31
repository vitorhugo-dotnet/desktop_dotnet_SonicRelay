using SonicRelay.Platform.Linux.Audio;
using SonicRelay.Windows.Audio;

namespace SonicRelay.Platform.Linux.Tests;

public sealed class PcmFrameAssemblerTests
{
    private const int BytesPerFrame = 3840; // 48000 * 0.020 * 2 channels * 2 bytes

    [Fact]
    public void ExactSingleFrameProducesOneFrame()
    {
        var assembler = new PcmFrameAssembler();
        var chunk = new byte[BytesPerFrame];

        var frames = assembler.Append(chunk);

        Assert.Single(frames);
        Assert.Equal(BytesPerFrame, frames[0].Frame.Data.Length);
        Assert.Equal(AudioSampleFormat.Pcm16, frames[0].Frame.Format);
        Assert.Equal(48_000, frames[0].Frame.SampleRate);
        Assert.Equal(2, frames[0].Frame.ChannelCount);
    }

    [Fact]
    public void ArbitraryReadBoundariesStillProduceCorrectFrames()
    {
        var assembler = new PcmFrameAssembler();
        var total = new byte[BytesPerFrame * 2];
        new Random(1).NextBytes(total);

        var frames = new List<(AudioFrame, AudioLevelSnapshot)>();
        foreach (var chunk in total.Chunk(7)) // deliberately not frame- or sample-aligned
        {
            frames.AddRange(assembler.Append(chunk));
        }

        Assert.Equal(2, frames.Count);
        Assert.All(frames, f => Assert.Equal(BytesPerFrame, f.Item1.Data.Length));
    }

    [Fact]
    public void IncompleteTrailingBytesAreRetainedNotEmitted()
    {
        var assembler = new PcmFrameAssembler();
        var frames = assembler.Append(new byte[BytesPerFrame - 10]);

        Assert.Empty(frames);

        var completing = assembler.Append(new byte[10]);
        Assert.Single(completing);
    }

    [Fact]
    public void TimestampsAreMonotonicallyNonDecreasing()
    {
        var assembler = new PcmFrameAssembler();
        var first = assembler.Append(new byte[BytesPerFrame])[0].Frame.Timestamp;
        var second = assembler.Append(new byte[BytesPerFrame])[0].Frame.Timestamp;

        Assert.True(second >= first);
    }
}
