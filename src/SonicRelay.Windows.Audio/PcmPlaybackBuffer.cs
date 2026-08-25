namespace SonicRelay.Windows.Audio;

/// <summary>
/// The bounded hand-off between the WebRTC receive path and a platform render device.
///
/// Two rules make it what it is. Writes never block: they run on the receive path, where
/// blocking would stall the peer connection itself. And when the device falls behind, the
/// *oldest* audio is discarded rather than the newest — two-way audio is a conversation, and a
/// gap is recoverable where accumulating delay is not.
///
/// Bytes rather than samples because the render devices disagree on format: WASAPI shared mode
/// takes 32-bit float in the endpoint's mix format, CoreAudio here takes packed PCM16. Both
/// convert once on the way in and copy straight out.
/// </summary>
public sealed class PcmPlaybackBuffer
{
    private readonly object gate = new();
    private readonly Queue<byte[]> pending = new();
    private byte[]? partial;
    private int partialOffset;
    private int pendingBytes;

    public PcmPlaybackBuffer(int capacityBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacityBytes);
        CapacityBytes = capacityBytes;
    }

    /// <summary>How much audio may wait before the oldest is dropped.</summary>
    public int CapacityBytes { get; }

    public int PendingBytes
    {
        get { lock (gate) return pendingBytes + (partial is null ? 0 : partial.Length - partialOffset); }
    }

    /// <summary>Total bytes discarded because the device could not keep up.</summary>
    public long DroppedBytes { get; private set; }

    /// <summary>Queues one converted block. Copies, so the caller may reuse its span.</summary>
    public void Write(ReadOnlySpan<byte> block)
    {
        if (block.IsEmpty) return;
        lock (gate)
        {
            pending.Enqueue(block.ToArray());
            pendingBytes += block.Length;
            // Never drop the only block: with a capacity smaller than one block, dropping it
            // would play permanent silence instead of late audio.
            while (pendingBytes > CapacityBytes && pending.Count > 1)
            {
                var dropped = pending.Dequeue();
                pendingBytes -= dropped.Length;
                DroppedBytes += dropped.Length;
            }
        }
    }

    /// <summary>
    /// Fills <paramref name="destination"/> from the queue and zero-fills whatever is left
    /// over, returning how many bytes were real audio. Silence is the only honest thing to
    /// play for a slice the peer has not sent.
    /// </summary>
    public int Fill(Span<byte> destination)
    {
        if (destination.IsEmpty) return 0;
        var written = 0;
        lock (gate)
        {
            while (written < destination.Length)
            {
                if (partial is null)
                {
                    if (pending.Count == 0) break;
                    partial = pending.Dequeue();
                    pendingBytes -= partial.Length;
                    partialOffset = 0;
                }
                var take = Math.Min(destination.Length - written, partial.Length - partialOffset);
                partial.AsSpan(partialOffset, take).CopyTo(destination[written..]);
                written += take;
                partialOffset += take;
                if (partialOffset >= partial.Length)
                {
                    partial = null;
                    partialOffset = 0;
                }
            }
        }
        destination[written..].Clear();
        return written;
    }

    public void Clear()
    {
        lock (gate)
        {
            pending.Clear();
            pendingBytes = 0;
            partial = null;
            partialOffset = 0;
        }
    }

    /// <summary>The byte budget for <paramref name="latency"/> of the given stream format.</summary>
    public static int CapacityFor(TimeSpan latency, int sampleRate, int channelCount, int bytesPerSample) =>
        Math.Max(1, (int)(latency.TotalSeconds * sampleRate) * channelCount * bytesPerSample);
}
