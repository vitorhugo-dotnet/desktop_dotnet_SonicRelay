using SonicRelay.Windows.Audio;

namespace SonicRelay.Windows.Audio.Tests;

public sealed class AudioCaptureServiceTests
{
    [Fact]
    public async Task LifecycleTransitionsAreObservableAndIdempotent()
    {
        var backend = new FakeAudioCaptureBackend();
        await using var service = new AudioCaptureService(backend);
        var states = new List<AudioCaptureState>();
        service.StateChanged += states.Add;

        await service.StartAsync();
        await service.StartAsync();
        await service.PauseAsync();
        await service.PauseAsync();
        await service.ResumeAsync();
        await service.StopAsync();
        await service.StopAsync();

        Assert.Equal(1, backend.StartCount);
        Assert.Equal(1, backend.PauseCount);
        Assert.Equal(1, backend.ResumeCount);
        Assert.Equal(1, backend.StopCount);
        Assert.Equal(
            [AudioCaptureState.Starting, AudioCaptureState.Capturing, AudioCaptureState.Paused,
             AudioCaptureState.Capturing, AudioCaptureState.Stopping, AudioCaptureState.Stopped],
            states);
    }

    [Fact]
    public async Task CreateFactoryProducesAWorkingService()
    {
        var backend = new FakeAudioCaptureBackend();
        var probe = new FakeOutputDeviceProbe([new AudioOutputDevice("sink-1", "Sink 1", true)]);

        await using var service = AudioCaptureService.Create(backend, probe);

        Assert.Equal(AudioCaptureState.Stopped, service.State);
        Assert.Single(service.GetOutputDevices());

        await service.StartAsync();

        Assert.Equal(AudioCaptureState.Capturing, service.State);
        Assert.Equal(1, backend.StartCount);
    }

    [Fact]
    public async Task FramesUpdateDiagnosticsAndAreForwarded()
    {
        var backend = new FakeAudioCaptureBackend();
        await using var service = new AudioCaptureService(backend);
        AudioFrame? received = null;
        service.FrameCaptured += frame => received = frame;
        await service.StartAsync();
        var frame = new AudioFrame([0, 0, 255, 127], 48_000, 1, AudioSampleFormat.Pcm16, TimeSpan.Zero);

        backend.Emit(frame, new AudioLevelSnapshot(1f, 0.707f));

        Assert.Same(frame, received);
        Assert.Equal(4, service.Diagnostics.BytesCaptured);
        Assert.Equal(1, service.Diagnostics.FramesCaptured);
        Assert.Equal(1f, service.Diagnostics.Level.Peak);
        Assert.Equal("Default speakers", service.Diagnostics.Device?.Name);
    }

    [Fact]
    public async Task FrameEmittedDuringStartAsyncIsNotDropped()
    {
        // A backend's first frame can legitimately arrive before its own
        // StartAsync returns (PipeWireProcessBackend does exactly this), which
        // means the service is still in Starting when FrameAvailable fires.
        // That frame must still be accepted, not silently dropped.
        var backend = new FakeAudioCaptureBackend { EmitFrameOnStart = true };
        await using var service = new AudioCaptureService(backend);
        AudioFrame? received = null;
        service.FrameCaptured += frame => received = frame;

        await service.StartAsync();

        Assert.NotNull(received);
        Assert.Equal(1, service.Diagnostics.FramesCaptured);
    }

    [Fact]
    public async Task StartFailureIsMappedWithoutEscapingAndCanBeStopped()
    {
        var backend = new FakeAudioCaptureBackend { StartError = new AudioCaptureException(AudioCaptureError.NoDevice, "No render device is available.") };
        await using var service = new AudioCaptureService(backend);

        await service.StartAsync();

        Assert.Equal(AudioCaptureState.Faulted, service.State);
        Assert.Equal(AudioCaptureError.NoDevice, service.Diagnostics.LastError?.Code);
        Assert.Contains("No render device", service.Diagnostics.LastError?.Message);
        await service.StopAsync();
        Assert.Equal(AudioCaptureState.Stopped, service.State);
    }

    [Theory]
    [InlineData(unchecked((int)0x80070490), AudioCaptureError.NoDevice)]
    [InlineData(unchecked((int)0x88890004), AudioCaptureError.DeviceLost)]
    [InlineData(unchecked((int)0x80070005), AudioCaptureError.AccessDenied)]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void WasapiErrorCodesMapToStableErrors(int errorCode, AudioCaptureError expected)
    {
        Assert.Equal(expected, WasapiLoopbackBackend.MapHResult(errorCode).Error);
    }

    [Fact]
    public async Task PauseFailureFaultsServiceWithoutEscaping()
    {
        var backend = new FakeAudioCaptureBackend
        {
            PauseError = new AudioCaptureException(AudioCaptureError.DeviceLost, "The render device was disconnected.")
        };
        await using var service = new AudioCaptureService(backend);
        await service.StartAsync();

        await service.PauseAsync();

        Assert.Equal(AudioCaptureState.Faulted, service.State);
        Assert.Equal(AudioCaptureError.DeviceLost, service.Diagnostics.LastError?.Code);
    }

    [Fact]
    public async Task DeviceLossRecoversAutomaticallyAfterRetries()
    {
        var lost = new AudioCaptureException(AudioCaptureError.DeviceLost, "device invalidated");
        // First two restarts fail, the third succeeds.
        var backend = new ScriptedRecoveryBackend(lost, lost, null);
        var delay = new ImmediateRetryDelay();
        await using var service = new AudioCaptureService(backend, delay);
        var states = new List<AudioCaptureState>();
        service.StateChanged += states.Add;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await service.StartAsync();

        backend.Fail(lost);

        await WaitUntilAsync(() => service.State == AudioCaptureState.Capturing && backend.StartCount == 4, timeout.Token);
        Assert.Contains(AudioCaptureState.Recovering, states);
        Assert.Null(service.Diagnostics.LastError);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], delay.Delays);
    }

    [Fact]
    public async Task FramesEmittedWhileRecoveringAreAcceptedNotDropped()
    {
        // Previously any frame arriving while State == Recovering was
        // dropped. That silently discarded a backend's legitimate first
        // frame after a successful restart, because RecoverAsync only
        // transitions out of Recovering *after* the backend's own
        // StartAsync returns, and a backend (e.g. PipeWireProcessBackend)
        // can raise FrameAvailable for its first frame before that call
        // returns. Recovering now accepts frames for the same reason
        // Starting does — see OnFrameAvailable.
        var lost = new AudioCaptureException(AudioCaptureError.DeviceLost, "device invalidated");
        var gate = new TaskCompletionSource();
        var backend = new ScriptedRecoveryBackend((AudioCaptureException?)null);
        var delay = new ImmediateRetryDelay(gate);
        await using var service = new AudioCaptureService(backend, delay);
        var frames = 0;
        service.FrameCaptured += _ => frames++;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await service.StartAsync();

        backend.Fail(lost); // -> Recovering, parked on the delay gate
        await WaitUntilAsync(() => service.State == AudioCaptureState.Recovering, timeout.Token);
        backend.Emit(new AudioFrame([1, 0], 48_000, 1, AudioSampleFormat.Pcm16, TimeSpan.Zero), AudioLevelSnapshot.Silence);
        Assert.Equal(1, frames);

        gate.SetResult(); // let recovery proceed and reconnect
        await WaitUntilAsync(() => service.State == AudioCaptureState.Capturing, timeout.Token);
    }

    [Fact]
    public async Task RecoveryExhaustionFaultsTheService()
    {
        var lost = new AudioCaptureException(AudioCaptureError.DeviceLost, "device invalidated");
        // All five restart attempts fail.
        var backend = new ScriptedRecoveryBackend(lost, lost, lost, lost, lost);
        var delay = new ImmediateRetryDelay();
        await using var service = new AudioCaptureService(backend, delay);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await service.StartAsync();

        backend.Fail(lost);

        await WaitUntilAsync(() => service.State == AudioCaptureState.Faulted, timeout.Token);
        Assert.Equal(AudioCaptureError.DeviceLost, service.Diagnostics.LastError?.Code);
        Assert.Equal(5, delay.Delays.Count);
    }

    [Fact]
    public async Task NonRetryableFaultGoesTerminalWithoutRecovering()
    {
        var backend = new ScriptedRecoveryBackend();
        var delay = new ImmediateRetryDelay();
        await using var service = new AudioCaptureService(backend, delay);
        var states = new List<AudioCaptureState>();
        service.StateChanged += states.Add;
        await service.StartAsync();

        backend.Fail(new AudioCaptureException(AudioCaptureError.UnsupportedFormat, "bad format"));

        Assert.Equal(AudioCaptureState.Faulted, service.State);
        Assert.DoesNotContain(AudioCaptureState.Recovering, states);
        Assert.Empty(delay.Delays);
    }

    [Fact]
    public async Task StopDuringRecoveryLeavesServiceStopped()
    {
        var lost = new AudioCaptureException(AudioCaptureError.DeviceLost, "device invalidated");
        var gate = new TaskCompletionSource();
        var backend = new ScriptedRecoveryBackend(lost, lost, lost);
        var delay = new ImmediateRetryDelay(gate);
        await using var service = new AudioCaptureService(backend, delay);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await service.StartAsync();

        backend.Fail(lost);
        await WaitUntilAsync(() => service.State == AudioCaptureState.Recovering, timeout.Token);

        await service.StopAsync(); // cancels the recovery parked on the gate

        Assert.Equal(AudioCaptureState.Stopped, service.State);
    }

    [Fact]
    public void SelectOutputDeviceUpdatesPreferredIdAndTreatsBlankAsDefault()
    {
        var backend = new FakeAudioCaptureBackend();
        var service = new AudioCaptureService(backend);
        Assert.Null(service.PreferredDeviceId);

        service.SelectOutputDevice("{0.0.0.00000000}.{guid}");
        Assert.Equal("{0.0.0.00000000}.{guid}", service.PreferredDeviceId);

        service.SelectOutputDevice("   ");
        Assert.Null(service.PreferredDeviceId);
    }

    [Fact]
    public void GetOutputDevicesReturnsTheProbeList()
    {
        var probe = new FakeOutputDeviceProbe(
        [
            new AudioOutputDevice("id-1", "Speakers", IsDefault: true),
            new AudioOutputDevice("id-2", "Headphones", IsDefault: false),
        ]);
        var service = new AudioCaptureService(new FakeAudioCaptureBackend(), deviceProbe: probe);

        var devices = service.GetOutputDevices();

        Assert.Equal(2, devices.Count);
        Assert.True(devices[0].IsDefault);
        Assert.Equal("Headphones", devices[1].Name);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            await Task.Delay(10, cancellationToken);
        }
    }
}

internal sealed class FakeOutputDeviceProbe(IReadOnlyList<AudioOutputDevice> devices) : IAudioOutputDeviceProbe
{
    public IReadOnlyList<AudioOutputDevice> GetOutputDevices() => devices;
}

internal sealed class FakeAudioCaptureBackend : IAudioCaptureBackend
{
    public int StartCount { get; private set; }
    public int PauseCount { get; private set; }
    public int ResumeCount { get; private set; }
    public int StopCount { get; private set; }
    public AudioCaptureException? StartError { get; init; }
    public AudioCaptureException? PauseError { get; init; }
    public AudioDeviceInfo? Device { get; private set; }

    /// <summary>
    /// When true, emits a frame synchronously from within StartAsync, right
    /// before it returns — mirroring PipeWireProcessBackend, whose first frame
    /// is raised before its own StartAsync completes.
    /// </summary>
    public bool EmitFrameOnStart { get; init; }

    public event Action<AudioFrame, AudioLevelSnapshot>? FrameAvailable;
    public event Action<AudioCaptureException>? Faulted;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        StartCount++;
        if (StartError is not null) throw StartError;
        Device = new AudioDeviceInfo("default", "Default speakers", 48_000, 2, AudioSampleFormat.IeeeFloat32);
        if (EmitFrameOnStart)
        {
            Emit(new AudioFrame([0, 0, 255, 127], 48_000, 1, AudioSampleFormat.Pcm16, TimeSpan.Zero), new AudioLevelSnapshot(1f, 0.707f));
        }
        return Task.CompletedTask;
    }

    public Task PauseAsync(CancellationToken cancellationToken)
    {
        PauseCount++;
        return PauseError is null ? Task.CompletedTask : Task.FromException(PauseError);
    }
    public Task ResumeAsync(CancellationToken cancellationToken) { ResumeCount++; return Task.CompletedTask; }
    public Task StopAsync(CancellationToken cancellationToken) { StopCount++; return Task.CompletedTask; }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public void Emit(AudioFrame frame, AudioLevelSnapshot level) => FrameAvailable?.Invoke(frame, level);
    public void Fail(AudioCaptureException error) => Faulted?.Invoke(error);
}

/// <summary>
/// Backend whose restart (StartAsync) fails a scripted number of times before
/// succeeding, so recovery behaviour can be driven deterministically.
/// </summary>
internal sealed class ScriptedRecoveryBackend : IAudioCaptureBackend
{
    private readonly Queue<AudioCaptureException?> _startOutcomes;

    public ScriptedRecoveryBackend(params AudioCaptureException?[] restartOutcomes) =>
        _startOutcomes = new Queue<AudioCaptureException?>(restartOutcomes);

    public int StartCount { get; private set; }
    public int StopCount { get; private set; }
    public AudioDeviceInfo? Device { get; private set; }
    public event Action<AudioFrame, AudioLevelSnapshot>? FrameAvailable;
    public event Action<AudioCaptureException>? Faulted;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        StartCount++;
        // The first StartAsync (initial capture) always succeeds; scripted
        // outcomes drive the recovery restarts that follow.
        if (StartCount > 1 && _startOutcomes.Count > 0 && _startOutcomes.Dequeue() is { } error)
        {
            throw error;
        }
        Device = new AudioDeviceInfo("default", "Default speakers", 48_000, 2, AudioSampleFormat.IeeeFloat32);
        return Task.CompletedTask;
    }

    public Task PauseAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ResumeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) { StopCount++; return Task.CompletedTask; }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public void Fail(AudioCaptureException error) => Faulted?.Invoke(error);
    public void Emit(AudioFrame frame, AudioLevelSnapshot level) => FrameAvailable?.Invoke(frame, level);
}

internal sealed class ImmediateRetryDelay : IRetryDelay
{
    public List<TimeSpan> Delays { get; } = [];
    private readonly TaskCompletionSource? _gate;

    public ImmediateRetryDelay(TaskCompletionSource? gate = null) => _gate = gate;

    public async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        Delays.Add(delay);
        if (_gate is not null) await _gate.Task.WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
    }
}
