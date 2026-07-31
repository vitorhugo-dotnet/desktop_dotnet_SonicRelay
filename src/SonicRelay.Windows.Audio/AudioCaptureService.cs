namespace SonicRelay.Windows.Audio;

internal interface IRetryDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

internal sealed class RetryDelay : IRetryDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}

/// <summary>
/// How capture recovers from a transient device fault (default render endpoint
/// invalidated/disconnected or its format changed mid-stream). Restarting the
/// backend re-resolves the current default endpoint.
/// </summary>
public sealed record AudioRecoveryPolicy
{
    public int MaxAttempts { get; init; } = 5;
    public IReadOnlyList<TimeSpan> Delays { get; init; } =
    [
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(8)
    ];

    public TimeSpan DelayFor(int attempt) =>
        Delays.Count == 0 ? TimeSpan.Zero : Delays[Math.Min(attempt, Delays.Count - 1)];
}

/// <summary>Mutable holder for the selected output device id, shared between the
/// service and the backend so a selection applies to the next capture start.</summary>
internal sealed class OutputDeviceSelection
{
    public string? PreferredId;
}

public sealed class AudioCaptureService : IAudioCaptureService
{
    private readonly IAudioCaptureBackend _backend;
    private readonly IRetryDelay _retryDelay;
    private readonly AudioRecoveryPolicy _recoveryPolicy;
    private readonly IAudioOutputDeviceProbe _deviceProbe;
    private readonly OutputDeviceSelection _selection;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private AudioCaptureDiagnostics _diagnostics = new(AudioCaptureState.Stopped, null, null, AudioLevelSnapshot.Silence, 0, 0);
    private CancellationTokenSource? _recoveryCancellation;
    private Task _recoveryTask = Task.CompletedTask;
    private bool _disposed;

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public AudioCaptureService() : this(new OutputDeviceSelection()) { }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private AudioCaptureService(OutputDeviceSelection selection)
        : this(
            new WasapiLoopbackBackend(() => selection.PreferredId),
            deviceProbe: new WasapiOutputDeviceProbe(),
            selection: selection)
    {
    }

    internal AudioCaptureService(
        IAudioCaptureBackend backend,
        IRetryDelay? retryDelay = null,
        AudioRecoveryPolicy? recoveryPolicy = null,
        IAudioOutputDeviceProbe? deviceProbe = null,
        OutputDeviceSelection? selection = null)
    {
        _backend = backend;
        _retryDelay = retryDelay ?? new RetryDelay();
        _recoveryPolicy = recoveryPolicy ?? new AudioRecoveryPolicy();
        _deviceProbe = deviceProbe ?? new NullOutputDeviceProbe();
        _selection = selection ?? new OutputDeviceSelection();
        _backend.FrameAvailable += OnFrameAvailable;
        _backend.Faulted += OnBackendFaulted;
    }

    /// <summary>
    /// Platform-neutral composition entry point (issue #32): any platform shell
    /// supplies its own <see cref="IAudioCaptureBackend"/> (WASAPI on Windows,
    /// PipeWire on Linux) and device probe, and gets the same lifecycle,
    /// recovery, and diagnostics behavior either way.
    /// </summary>
    public static AudioCaptureService Create(
        IAudioCaptureBackend backend,
        IAudioOutputDeviceProbe deviceProbe,
        AudioRecoveryPolicy? recoveryPolicy = null) =>
        new(backend, recoveryPolicy: recoveryPolicy, deviceProbe: deviceProbe);

    private static bool IsRetryable(AudioCaptureError error) =>
        error is AudioCaptureError.DeviceLost or AudioCaptureError.NoDevice;

    public AudioCaptureState State => _diagnostics.State;
    public AudioCaptureDiagnostics Diagnostics => _diagnostics;
    public string? PreferredDeviceId => _selection.PreferredId;
    public event Action<AudioCaptureState>? StateChanged;
    public event Action<AudioFrame>? FrameCaptured;
    public event Action<AudioLevelSnapshot>? LevelChanged;

    public IReadOnlyList<AudioOutputDevice> GetOutputDevices() => _deviceProbe.GetOutputDevices();

    public void SelectOutputDevice(string? deviceId) =>
        _selection.PreferredId = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (State is AudioCaptureState.Capturing or AudioCaptureState.Paused or AudioCaptureState.Starting) return;
            SetState(AudioCaptureState.Starting);
            _diagnostics = _diagnostics with { LastError = null, Level = AudioLevelSnapshot.Silence, BytesCaptured = 0, FramesCaptured = 0 };
            try
            {
                await _backend.StartAsync(cancellationToken).ConfigureAwait(false);
                _diagnostics = _diagnostics with { Device = _backend.Device };
                SetState(AudioCaptureState.Capturing);
            }
            catch (AudioCaptureException error)
            {
                SetFailure(error);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                SetFailure(new AudioCaptureException(AudioCaptureError.PlatformFailure, "Audio capture could not be started.", error));
            }
        }
        finally { _lifecycle.Release(); }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        // Cancel any in-flight recovery before taking the lifecycle lock so a
        // recovery attempt currently holding it observes cancellation and exits.
        await CancelRecoveryAsync().ConfigureAwait(false);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State is AudioCaptureState.Stopped or AudioCaptureState.Stopping) return;
            SetState(AudioCaptureState.Stopping);
            try { await _backend.StopAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _diagnostics = _diagnostics with { LastError = new(AudioCaptureError.PlatformFailure, error.Message) };
            }
            _diagnostics = _diagnostics with { Device = null, Level = AudioLevelSnapshot.Silence };
            SetState(AudioCaptureState.Stopped);
        }
        finally { _lifecycle.Release(); }
    }

    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (State == AudioCaptureState.Paused) return;
            if (State != AudioCaptureState.Capturing) return;
            try
            {
                await _backend.PauseAsync(cancellationToken).ConfigureAwait(false);
                SetState(AudioCaptureState.Paused);
            }
            catch (AudioCaptureException error) { SetFailure(error); }
        }
        finally { _lifecycle.Release(); }
    }

    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (State == AudioCaptureState.Capturing) return;
            if (State != AudioCaptureState.Paused) return;
            try
            {
                await _backend.ResumeAsync(cancellationToken).ConfigureAwait(false);
                SetState(AudioCaptureState.Capturing);
            }
            catch (AudioCaptureException error) { SetFailure(error); }
        }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await StopAsync().ConfigureAwait(false);
        _disposed = true;
        _backend.FrameAvailable -= OnFrameAvailable;
        _backend.Faulted -= OnBackendFaulted;
        await _backend.DisposeAsync().ConfigureAwait(false);
        _recoveryCancellation?.Dispose();
        _lifecycle.Dispose();
    }

    private void OnFrameAvailable(AudioFrame frame, AudioLevelSnapshot level)
    {
        // A backend's first frame after a (re)start can legitimately arrive
        // before the service's own state transition to Capturing catches up
        // (PipeWireProcessBackend deliberately raises FrameAvailable for its
        // first frame before its own StartAsync returns), so Starting and
        // Recovering must also accept frames or that first ~20ms is silently
        // dropped on every cold start and every recovery cycle.
        if (State is not (AudioCaptureState.Capturing or AudioCaptureState.Starting or AudioCaptureState.Recovering)) return;
        _diagnostics = _diagnostics with
        {
            Level = level,
            BytesCaptured = _diagnostics.BytesCaptured + frame.Data.Length,
            FramesCaptured = _diagnostics.FramesCaptured + 1
        };
        FrameCaptured?.Invoke(frame);
        LevelChanged?.Invoke(level);
    }

    private void OnBackendFaulted(AudioCaptureException error)
    {
        // A recoverable device fault (endpoint invalidated/lost mid-stream) is
        // retried automatically instead of going terminal; other faults
        // (unsupported format, access denied) stay terminal.
        if (IsRetryable(error.Error) && State is AudioCaptureState.Capturing or AudioCaptureState.Paused)
        {
            BeginRecovery(error);
        }
        else
        {
            SetFailure(error);
        }
    }

    private void BeginRecovery(AudioCaptureException error)
    {
        _diagnostics = _diagnostics with { LastError = new(error.Error, error.Message), Level = AudioLevelSnapshot.Silence };
        SetState(AudioCaptureState.Recovering);
        _recoveryCancellation?.Dispose();
        _recoveryCancellation = new CancellationTokenSource();
        _recoveryTask = Task.Run(() => RecoverAsync(_recoveryCancellation.Token));
    }

    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        AudioCaptureException? lastError = null;
        for (var attempt = 0; attempt < _recoveryPolicy.MaxAttempts; attempt++)
        {
            try
            {
                await _retryDelay.DelayAsync(_recoveryPolicy.DelayFor(attempt), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await _lifecycle.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                // Stop/Dispose cancelled recovery, or the state moved on — abandon.
                if (cancellationToken.IsCancellationRequested || State != AudioCaptureState.Recovering) return;
                // Restart re-resolves the current default render endpoint.
                await _backend.StopAsync(cancellationToken).ConfigureAwait(false);
                await _backend.StartAsync(cancellationToken).ConfigureAwait(false);
                _diagnostics = _diagnostics with { Device = _backend.Device, LastError = null };
                SetState(AudioCaptureState.Capturing);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (AudioCaptureException retryError)
            {
                lastError = retryError;
                if (!IsRetryable(retryError.Error))
                {
                    SetFailure(retryError);
                    return;
                }
            }
            catch (Exception unexpected)
            {
                SetFailure(new AudioCaptureException(AudioCaptureError.PlatformFailure, "Audio recovery failed.", unexpected));
                return;
            }
            finally
            {
                _lifecycle.Release();
            }
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            SetFailure(lastError ?? new AudioCaptureException(AudioCaptureError.DeviceLost, "Audio capture could not recover."));
        }
    }

    private async Task CancelRecoveryAsync()
    {
        var cancellation = _recoveryCancellation;
        var task = _recoveryTask;
        if (cancellation is null) return;
        await cancellation.CancelAsync().ConfigureAwait(false);
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    private void SetFailure(AudioCaptureException error)
    {
        _diagnostics = _diagnostics with { LastError = new(error.Error, error.Message) };
        SetState(AudioCaptureState.Faulted);
    }

    private void SetState(AudioCaptureState state)
    {
        if (State == state) return;
        _diagnostics = _diagnostics with { State = state };
        StateChanged?.Invoke(state);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
