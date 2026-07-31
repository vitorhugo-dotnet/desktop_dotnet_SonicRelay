namespace SonicRelay.Windows.Audio;

/// <summary>
/// Enumerates and selects the audio output (render) endpoint to capture. Split from
/// <see cref="IAudioCaptureService"/> as one of the platform contracts of issue #32:
/// on Windows this surfaces WASAPI render endpoints, on Linux it will surface
/// PipeWire sinks/monitors. UI layers consume only this contract.
/// </summary>
public interface IAudioDeviceEnumerator
{
    /// <summary>The selected render device id, or null for the system default.</summary>
    string? PreferredDeviceId { get; }

    /// <summary>Lists the active render endpoints for the source picker.</summary>
    IReadOnlyList<AudioOutputDevice> GetOutputDevices();

    /// <summary>
    /// Selects which render endpoint to capture (null = system default). Applies to
    /// the next capture start; it does not interrupt an in-progress capture.
    /// </summary>
    void SelectOutputDevice(string? deviceId);
}

public interface IAudioCaptureService : IAudioDeviceEnumerator, IAsyncDisposable
{
    AudioCaptureState State { get; }
    AudioCaptureDiagnostics Diagnostics { get; }

    event Action<AudioCaptureState>? StateChanged;
    event Action<AudioFrame>? FrameCaptured;
    event Action<AudioLevelSnapshot>? LevelChanged;
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task PauseAsync(CancellationToken cancellationToken = default);
    Task ResumeAsync(CancellationToken cancellationToken = default);
}

public interface IAudioCaptureBackend : IAsyncDisposable
{
    AudioDeviceInfo? Device { get; }
    event Action<AudioFrame, AudioLevelSnapshot>? FrameAvailable;
    event Action<AudioCaptureException>? Faulted;
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
    Task PauseAsync(CancellationToken cancellationToken);
    Task ResumeAsync(CancellationToken cancellationToken);
}
