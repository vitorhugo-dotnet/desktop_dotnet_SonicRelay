namespace SonicRelay.Windows.Audio;

public enum AudioPlaybackState { Stopped, Starting, Playing, Stopping, Faulted }

/// <summary>
/// A platform audio render endpoint: WASAPI on Windows, a PipeWire playback process on Linux.
/// Writes are PCM16, interleaved when stereo, at the rate and channel count the stream was
/// started with.
///
/// Deliberately a sibling of <see cref="IAudioCaptureBackend"/> rather than a mode of it: the
/// two have opposite data flow and opposite failure behavior — a capture backend that stalls
/// loses audio nobody has heard yet, while a render backend that stalls must drop rather than
/// block the WebRTC receive path behind it.
/// </summary>
public interface IAudioPlaybackBackend : IAsyncDisposable
{
    AudioDeviceInfo? Device { get; }

    event Action<AudioCaptureException>? Faulted;

    Task StartAsync(int sampleRate, int channelCount, CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Queues interleaved PCM16 for playback. Must never block the caller: this runs on the
    /// WebRTC receive path, and back-pressure has to be resolved by discarding the oldest
    /// audio rather than by stalling the connection.
    /// </summary>
    void Write(ReadOnlySpan<short> samples);
}

/// <summary>No-op backend for platforms without a playback implementation, and for tests.</summary>
public sealed class NullAudioPlaybackBackend : IAudioPlaybackBackend
{
    public AudioDeviceInfo? Device => null;

    public event Action<AudioCaptureException>? Faulted;

    public Task StartAsync(int sampleRate, int channelCount, CancellationToken cancellationToken)
    {
        _ = Faulted;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Write(ReadOnlySpan<short> samples) { }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
