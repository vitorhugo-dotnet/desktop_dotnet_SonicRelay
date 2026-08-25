using SonicRelay.Windows.Audio;

namespace SonicRelay.Windows.Audio.Tests;

/// <summary>
/// The hand-off every playback backend sits on. Its two rules are what the tests are about:
/// a write never blocks, and back-pressure costs the oldest audio rather than the newest.
/// </summary>
public sealed class PcmPlaybackBufferTests
{
    [Fact]
    public void FillsFromWhatWasWritten()
    {
        var buffer = new PcmPlaybackBuffer(64);
        buffer.Write([1, 2, 3, 4]);

        var destination = new byte[4];
        var filled = buffer.Fill(destination);

        Assert.Equal(4, filled);
        Assert.Equal([1, 2, 3, 4], destination);
        Assert.Equal(0, buffer.PendingBytes);
    }

    [Fact]
    public void ZeroFillsWhatThePeerHasNotSent()
    {
        var buffer = new PcmPlaybackBuffer(64);
        buffer.Write([7, 7]);

        var destination = new byte[6];
        destination.AsSpan().Fill(0xFF);
        var filled = buffer.Fill(destination);

        // Silence is the only honest thing to play for a slice that has not arrived; leaving
        // the previous contents would repeat audio the listener already heard.
        Assert.Equal(2, filled);
        Assert.Equal([7, 7, 0, 0, 0, 0], destination);
    }

    [Fact]
    public void SpansBlocksAndResumesMidBlock()
    {
        var buffer = new PcmPlaybackBuffer(64);
        buffer.Write([1, 2, 3]);
        buffer.Write([4, 5, 6]);

        var first = new byte[4];
        buffer.Fill(first);
        var second = new byte[2];
        buffer.Fill(second);

        Assert.Equal([1, 2, 3, 4], first);
        Assert.Equal([5, 6], second);
    }

    [Fact]
    public void DropsTheOldestAudioWhenTheDeviceFallsBehind()
    {
        var buffer = new PcmPlaybackBuffer(capacityBytes: 4);
        buffer.Write([1, 1]);
        buffer.Write([2, 2]);
        buffer.Write([3, 3]);

        var destination = new byte[4];
        buffer.Fill(destination);

        // The newest audio is the one the listener is waiting on; the first block is what goes.
        Assert.Equal([2, 2, 3, 3], destination);
        Assert.Equal(2, buffer.DroppedBytes);
    }

    [Fact]
    public void NeverDropsTheOnlyBlock()
    {
        // A capacity smaller than a single block would otherwise play permanent silence
        // instead of slightly late audio.
        var buffer = new PcmPlaybackBuffer(capacityBytes: 2);
        buffer.Write([9, 9, 9, 9]);

        var destination = new byte[4];
        Assert.Equal(4, buffer.Fill(destination));
        Assert.Equal(0, buffer.DroppedBytes);
    }

    [Fact]
    public void ClearingDiscardsEverythingPending()
    {
        var buffer = new PcmPlaybackBuffer(64);
        buffer.Write([1, 2, 3, 4]);

        buffer.Clear();

        Assert.Equal(0, buffer.PendingBytes);
        var destination = new byte[2];
        Assert.Equal(0, buffer.Fill(destination));
    }

    [Fact]
    public void AnEmptyWriteOrFillIsANoOp()
    {
        var buffer = new PcmPlaybackBuffer(64);
        buffer.Write([]);
        Assert.Equal(0, buffer.PendingBytes);
        Assert.Equal(0, buffer.Fill([]));
    }

    [Fact]
    public void CapacityCoversTheRequestedLatency()
    {
        // 150 ms of 48 kHz stereo PCM16 = 0.150 * 48000 * 2 * 2.
        Assert.Equal(28800, PcmPlaybackBuffer.CapacityFor(TimeSpan.FromMilliseconds(150), 48000, 2, sizeof(short)));
        // Never zero: a zero-capacity buffer could not hold even one block.
        Assert.Equal(1, PcmPlaybackBuffer.CapacityFor(TimeSpan.Zero, 48000, 2, sizeof(short)));
    }

    [Fact]
    public void ARejectedCapacityIsAProgrammingError()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PcmPlaybackBuffer(0));
    }
}
