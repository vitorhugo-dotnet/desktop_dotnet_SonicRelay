using System.Diagnostics;
using SonicRelay.Windows.Audio;

namespace SonicRelay.Platform.Linux.Audio;

/// <summary>
/// Buffers raw `pw-record` stdout PCM16 bytes into exact 20 ms frames,
/// tolerating arbitrary pipe read boundaries and never emitting partial
/// samples. `Append` is not thread-safe; the backend calls it from a single
/// read loop (spec: docs/superpowers/specs/2026-07-14-linux-desktop-publisher-design.md).
/// </summary>
public sealed class PcmFrameAssembler(int sampleRate = 48_000, int channelCount = 2, int frameDurationMs = 20)
{
    private readonly int bytesPerFrame = sampleRate / 1000 * frameDurationMs * channelCount * 2;
    private readonly List<byte> pending = [];
    private readonly Stopwatch stopwatch = Stopwatch.StartNew();

    public IReadOnlyList<(AudioFrame Frame, AudioLevelSnapshot Level)> Append(ReadOnlySpan<byte> chunk)
    {
        pending.AddRange(chunk.ToArray());
        if (pending.Count < bytesPerFrame) return [];

        var frames = new List<(AudioFrame, AudioLevelSnapshot)>();
        while (pending.Count >= bytesPerFrame)
        {
            var frameBytes = pending.GetRange(0, bytesPerFrame).ToArray();
            pending.RemoveRange(0, bytesPerFrame);
            var level = AudioLevelCalculator.Calculate(frameBytes, AudioSampleFormat.Pcm16);
            var frame = new AudioFrame(frameBytes, sampleRate, channelCount, AudioSampleFormat.Pcm16, stopwatch.Elapsed);
            frames.Add((frame, level));
        }
        return frames;
    }
}
