using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SonicRelay.Windows.Audio;

/// <summary>
/// Captures the Windows microphone (WASAPI shared mode) for two-way sessions.
///
/// It is a sibling of <see cref="WasapiLoopbackBackend"/> rather than a mode of it, and the
/// difference between them is exactly two lines: this resolves a <see cref="EDataFlow.Capture"/>
/// endpoint instead of a render one, and initializes the client without
/// <c>AUDCLNT_STREAMFLAGS_LOOPBACK</c>. Everything else — the packet loop, format resolution,
/// error mapping — is the shared interop the loopback backend already owns. They are kept
/// apart because their failure messages are what a user reads when audio does not work, and
/// "no microphone" and "no output device to capture" send them to different places.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WasapiMicrophoneBackend : IAudioCaptureBackend
{
    private readonly Func<string?> preferredDeviceId;
    private CancellationTokenSource? captureCancellation;
    private Task? captureTask;
    private IAudioClient? audioClient;
    private volatile bool paused;

    public WasapiMicrophoneBackend(Func<string?>? preferredDeviceId = null)
    {
        // Read at each StartAsync so a settings change applies to the next capture.
        this.preferredDeviceId = preferredDeviceId ?? (() => null);
    }

    public AudioDeviceInfo? Device { get; private set; }
    public event Action<AudioFrame, AudioLevelSnapshot>? FrameAvailable;
    public event Action<AudioCaptureException>? Faulted;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (captureTask is not null) return;
        cancellationToken.ThrowIfCancellationRequested();
        captureCancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        captureTask = Task.Run(() => CaptureLoop(started, captureCancellation.Token), CancellationToken.None);
        try { await started.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch
        {
            await captureCancellation.CancelAsync().ConfigureAwait(false);
            try { await captureTask.ConfigureAwait(false); } catch { }
            captureTask = null;
            captureCancellation.Dispose();
            captureCancellation = null;
            throw;
        }
    }

    public Task PauseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (paused) return Task.CompletedTask;
        try { CheckHResult(audioClient?.Stop() ?? 0, "Windows could not pause microphone capture."); }
        catch (Exception error) { throw Map(error); }
        paused = true;
        return Task.CompletedTask;
    }

    public Task ResumeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!paused) return Task.CompletedTask;
        try { CheckHResult(audioClient?.Start() ?? 0, "Windows could not resume microphone capture."); }
        catch (Exception error) { throw Map(error); }
        paused = false;
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (captureTask is null) return;
        if (captureCancellation is not null) await captureCancellation.CancelAsync().ConfigureAwait(false);
        await captureTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        captureTask = null;
        captureCancellation?.Dispose();
        captureCancellation = null;
        paused = false;
        Device = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);

    private void CaptureLoop(TaskCompletionSource started, CancellationToken cancellationToken)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? endpoint = null;
        IAudioCaptureClient? captureClient = null;
        var mixFormatPointer = IntPtr.Zero;
        var comInitialized = NativeMethods.CoInitializeEx(IntPtr.Zero, 0) >= 0;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            endpoint = ResolvePreferredEndpoint(enumerator);
            var deviceId = WasapiLoopbackBackend.GetDeviceId(endpoint);
            var deviceName = WasapiLoopbackBackend.TryGetDeviceName(endpoint) ?? "Default microphone";
            var audioClientGuid = typeof(IAudioClient).GUID;
            CheckHResult(endpoint.Activate(ref audioClientGuid, 23, IntPtr.Zero, out var audioClientObject),
                "The microphone could not be activated.");
            audioClient = (IAudioClient)audioClientObject;
            CheckHResult(audioClient.GetMixFormat(out mixFormatPointer), "The microphone mix format is unavailable.");
            var waveFormat = Marshal.PtrToStructure<WaveFormatEx>(mixFormatPointer);
            var sampleFormat = ResolveFormat(mixFormatPointer, waveFormat);
            Device = new AudioDeviceInfo(deviceId, deviceName, (int)waveFormat.SamplesPerSec, waveFormat.Channels, sampleFormat);
            // Shared mode, no loopback flag: this endpoint already produces capture data.
            CheckHResult(audioClient.Initialize(0, 0, 10_000_000, 0, mixFormatPointer, IntPtr.Zero),
                "WASAPI microphone initialization failed.");
            var captureGuid = typeof(IAudioCaptureClient).GUID;
            CheckHResult(audioClient.GetService(ref captureGuid, out var captureObject), "The WASAPI capture service is unavailable.");
            captureClient = (IAudioCaptureClient)captureObject;
            CheckHResult(audioClient.Start(), "Microphone capture could not start.");
            started.TrySetResult();

            var stopwatch = Stopwatch.StartNew();
            while (!cancellationToken.IsCancellationRequested)
            {
                if (paused) { cancellationToken.WaitHandle.WaitOne(10); continue; }
                CheckHResult(captureClient.GetNextPacketSize(out var nextFrames), "Windows could not query the next microphone packet.");
                if (nextFrames == 0) { cancellationToken.WaitHandle.WaitOne(5); continue; }
                ReadAvailablePackets(captureClient, waveFormat, sampleFormat, stopwatch.Elapsed);
            }
        }
        catch (Exception error)
        {
            var mapped = Map(error);
            if (!started.TrySetException(mapped) && !cancellationToken.IsCancellationRequested) Faulted?.Invoke(mapped);
        }
        finally
        {
            audioClient?.Stop();
            ReleaseCom(captureClient);
            ReleaseCom(audioClient);
            ReleaseCom(endpoint);
            ReleaseCom(enumerator);
            audioClient = null;
            if (mixFormatPointer != IntPtr.Zero) Marshal.FreeCoTaskMem(mixFormatPointer);
            if (comInitialized) NativeMethods.CoUninitialize();
        }
    }

    /// <summary>
    /// Opens the user-selected capture endpoint, falling back to the communications default —
    /// not the multimedia one — because that is the endpoint Windows itself routes calls to,
    /// and a headset the user chose for calls is the microphone they expect to talk into.
    /// </summary>
    private IMMDevice ResolvePreferredEndpoint(IMMDeviceEnumerator enumerator)
    {
        var preferredId = preferredDeviceId();
        if (!string.IsNullOrWhiteSpace(preferredId)
            && enumerator.GetDevice(preferredId, out var selected) >= 0
            && selected is not null)
        {
            return selected;
        }
        CheckHResult(
            enumerator.GetDefaultAudioEndpoint(EDataFlow.Capture, ERole.Communications, out var endpoint),
            "No microphone is available.");
        return endpoint;
    }

    private void ReadAvailablePackets(IAudioCaptureClient captureClient, WaveFormatEx format, AudioSampleFormat sampleFormat, TimeSpan timestamp)
    {
        while (true)
        {
            CheckHResult(captureClient.GetNextPacketSize(out var nextFrames), "Windows could not query a microphone packet.");
            if (nextFrames == 0) return;
            CheckHResult(captureClient.GetBuffer(out var buffer, out var frameCount, out var flags, out _, out _),
                "Windows could not read a microphone packet.");
            try
            {
                var byteCount = checked((int)frameCount * format.BlockAlign);
                if (byteCount == 0) continue;
                var data = new byte[byteCount];
                // AUDCLNT_BUFFERFLAGS_SILENT: the buffer contents are undefined and must be
                // treated as silence rather than copied.
                if ((flags & 0x2) == 0) Marshal.Copy(buffer, data, 0, byteCount);
                var level = AudioLevelCalculator.Calculate(data, sampleFormat);
                FrameAvailable?.Invoke(new AudioFrame(data, (int)format.SamplesPerSec, format.Channels, sampleFormat, timestamp), level);
            }
            finally { CheckHResult(captureClient.ReleaseBuffer(frameCount), "Windows could not release a microphone packet."); }
        }
    }

    private static AudioSampleFormat ResolveFormat(IntPtr pointer, WaveFormatEx format)
    {
        if (format.FormatTag == 3 && format.BitsPerSample == 32) return AudioSampleFormat.IeeeFloat32;
        if (format.FormatTag == 1 && format.BitsPerSample == 16) return AudioSampleFormat.Pcm16;
        if (format.FormatTag == 0xFFFE && format.ExtraSize >= 22)
        {
            var subFormat = Marshal.PtrToStructure<Guid>(pointer + 24);
            if (subFormat == new Guid("00000003-0000-0010-8000-00aa00389b71") && format.BitsPerSample == 32) return AudioSampleFormat.IeeeFloat32;
            if (subFormat == new Guid("00000001-0000-0010-8000-00aa00389b71") && format.BitsPerSample == 16) return AudioSampleFormat.Pcm16;
        }
        throw new AudioCaptureException(AudioCaptureError.UnsupportedFormat,
            $"Unsupported microphone format: tag {format.FormatTag}, {format.BitsPerSample} bits.");
    }

    private static AudioCaptureException Map(Exception error)
    {
        if (error is AudioCaptureException captureError) return captureError;
        if (error is WasapiException comError)
        {
            // Windows 10+ returns E_ACCESSDENIED when the user has denied microphone access to
            // desktop apps in Privacy settings, which is by far the most common failure here
            // and needs to say so rather than read as a generic platform fault.
            var kind = comError.HResult switch
            {
                unchecked((int)0x80070005) => AudioCaptureError.AccessDenied,
                unchecked((int)0x88890004) => AudioCaptureError.DeviceLost,
                unchecked((int)0x80070490) => AudioCaptureError.NoDevice,
                _ => AudioCaptureError.PlatformFailure
            };
            var message = kind switch
            {
                AudioCaptureError.AccessDenied =>
                    "Windows denied microphone access. Allow desktop apps to use the microphone in Privacy settings.",
                AudioCaptureError.DeviceLost => "The selected microphone was disconnected or changed.",
                AudioCaptureError.NoDevice => "No microphone is available.",
                _ => "Windows microphone capture failed."
            };
            return new AudioCaptureException(kind, message, error);
        }
        return new AudioCaptureException(AudioCaptureError.PlatformFailure, "Windows microphone capture failed.", error);
    }

    private static void CheckHResult(int result, string message)
    {
        if (result < 0) throw new WasapiException(message, result);
    }

    private static void ReleaseCom(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }
}
