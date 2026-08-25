namespace SonicRelay.Windows.Audio;

/// <summary>
/// Plays decoded remote audio through an <see cref="IAudioPlaybackBackend"/>, owning the
/// lifecycle the backends deliberately do not: starting on the first frame's format,
/// restarting the endpoint after a device fault, and re-opening it when the incoming stream
/// changes rate or channel count mid-session (a peer that renegotiates from mono to stereo).
///
/// <see cref="Play"/> never blocks and never throws. It is called from the WebRTC receive
/// path, where blocking would stall the peer connection and throwing would look like a
/// connection failure — a playback device that is missing, faulted or slow costs audio, not
/// the call.
/// </summary>
public sealed class AudioPlaybackService : IAsyncDisposable
{
    private readonly IAudioPlaybackBackend backend;
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly object stateLock = new();
    private int currentSampleRate;
    private int currentChannelCount;
    private AudioPlaybackState state = AudioPlaybackState.Stopped;
    private Task pendingStart = Task.CompletedTask;
    private bool disposed;

    public AudioPlaybackService(IAudioPlaybackBackend backend)
    {
        this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
        this.backend.Faulted += OnBackendFaulted;
    }

    public AudioPlaybackState State
    {
        get { lock (stateLock) return state; }
    }

    /// <summary>The endpoint audio is playing on, once one has been opened.</summary>
    public AudioDeviceInfo? Device => backend.Device;

    public AudioCaptureFailure? LastError { get; private set; }

    public event Action<AudioPlaybackState>? StateChanged;

    /// <summary>
    /// Hands one decoded frame to the output, opening (or re-opening) the endpoint first if
    /// its format does not match. Fire-and-forget by design — see the class remarks.
    /// </summary>
    public void Play(ReadOnlySpan<short> samples, int sampleRate, int channelCount)
    {
        if (disposed || samples.IsEmpty) return;
        if (sampleRate <= 0 || channelCount is < 1 or > 2) return;

        bool needsStart;
        lock (stateLock)
        {
            needsStart = state is not AudioPlaybackState.Playing
                || currentSampleRate != sampleRate
                || currentChannelCount != channelCount;
        }

        if (needsStart)
        {
            EnsureStarted(sampleRate, channelCount);
            // The first frames of a stream are dropped rather than buffered against a device
            // that is not open yet: a few tens of milliseconds of speech is a better cost than
            // a queue that plays back late for the rest of the call.
            return;
        }

        try
        {
            backend.Write(samples);
        }
        catch (Exception exception)
        {
            OnBackendFaulted(exception as AudioCaptureException
                ?? new AudioCaptureException(AudioCaptureError.PlatformFailure, "Audio playback failed.", exception));
        }
    }

    /// <summary>Stops playback and releases the endpoint. Safe to call when already stopped.</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State is AudioPlaybackState.Stopped) return;
            SetState(AudioPlaybackState.Stopping);
            await backend.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LastError = Describe(exception);
        }
        finally
        {
            lock (stateLock)
            {
                currentSampleRate = 0;
                currentChannelCount = 0;
            }
            SetState(AudioPlaybackState.Stopped);
            lifecycle.Release();
        }
    }

    /// <summary>
    /// Starts the endpoint for the given format, at most once at a time. Returns immediately:
    /// opening a device is slow enough that waiting for it on the receive path would stall the
    /// very stream being opened for.
    /// </summary>
    private void EnsureStarted(int sampleRate, int channelCount)
    {
        lock (stateLock)
        {
            if (state is AudioPlaybackState.Starting) return;
            if (!pendingStart.IsCompleted) return;
            state = AudioPlaybackState.Starting;
        }
        StateChanged?.Invoke(AudioPlaybackState.Starting);
        pendingStart = StartAsync(sampleRate, channelCount);
    }

    private async Task StartAsync(int sampleRate, int channelCount)
    {
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            // A format change re-opens the endpoint; a fresh start has nothing to stop.
            await backend.StopAsync(CancellationToken.None).ConfigureAwait(false);
            await backend.StartAsync(sampleRate, channelCount, CancellationToken.None).ConfigureAwait(false);
            lock (stateLock)
            {
                currentSampleRate = sampleRate;
                currentChannelCount = channelCount;
            }
            LastError = null;
            SetState(AudioPlaybackState.Playing);
        }
        catch (Exception exception)
        {
            LastError = Describe(exception);
            SetState(AudioPlaybackState.Faulted);
        }
        finally
        {
            lifecycle.Release();
        }
    }

    private void OnBackendFaulted(AudioCaptureException error)
    {
        LastError = new AudioCaptureFailure(error.Error, error.Message);
        lock (stateLock)
        {
            currentSampleRate = 0;
            currentChannelCount = 0;
        }
        // Faulted rather than Stopped: the next frame re-opens the endpoint, which is how a
        // playback device that was unplugged and plugged back in recovers on its own.
        SetState(AudioPlaybackState.Faulted);
    }

    private static AudioCaptureFailure Describe(Exception exception) => exception is AudioCaptureException audio
        ? new AudioCaptureFailure(audio.Error, audio.Message)
        : new AudioCaptureFailure(AudioCaptureError.PlatformFailure, exception.Message);

    private void SetState(AudioPlaybackState next)
    {
        bool changed;
        lock (stateLock)
        {
            changed = state != next;
            state = next;
        }
        if (changed) StateChanged?.Invoke(next);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        backend.Faulted -= OnBackendFaulted;
        try { await pendingStart.ConfigureAwait(false); } catch { }
        try { await backend.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
        await backend.DisposeAsync().ConfigureAwait(false);
        lifecycle.Dispose();
    }
}
