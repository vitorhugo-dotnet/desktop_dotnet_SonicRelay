using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SonicRelay.Windows.Audio;

/// <summary>
/// Plays remote audio through the Windows default render endpoint (WASAPI shared mode).
///
/// Incoming PCM16 is converted to the endpoint's own mix format up front — WASAPI shared mode
/// accepts nothing else — and queued in a ring bounded by a latency budget. A dedicated pump
/// thread copies from that ring into the render buffer and writes silence when it runs dry, so
/// a late packet costs a gap rather than an underrun the device has to recover from.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WasapiRenderBackend : IAudioPlaybackBackend
{
    /// <summary>
    /// How much audio may sit ahead of the device before the oldest is discarded. Two-way
    /// audio is a conversation: buffering more than this trades a problem the listener can
    /// hear through (a gap) for one they cannot talk through (growing delay).
    /// </summary>
    private static readonly TimeSpan LatencyBudget = TimeSpan.FromMilliseconds(150);

    private readonly Func<string?> preferredDeviceId;
    private readonly object writeLock = new();
    private readonly Queue<float[]> pending = new();
    private CancellationTokenSource? renderCancellation;
    private Task? renderTask;
    private PcmStreamConverter? converter;
    private int deviceChannels;
    private int deviceSampleRate;
    private int pendingSamples;
    private int maxPendingSamples;
    private float[]? partial;
    private int partialOffset;

    public WasapiRenderBackend(Func<string?>? preferredDeviceId = null)
    {
        this.preferredDeviceId = preferredDeviceId ?? (() => null);
    }

    public AudioDeviceInfo? Device { get; private set; }

    public event Action<AudioCaptureException>? Faulted;

    public async Task StartAsync(int sampleRate, int channelCount, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        if (channelCount is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(channelCount));
        if (renderTask is not null) return;
        cancellationToken.ThrowIfCancellationRequested();

        renderCancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        renderTask = Task.Run(
            () => RenderLoop(sampleRate, channelCount, started, renderCancellation.Token),
            CancellationToken.None);
        try { await started.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch
        {
            await renderCancellation.CancelAsync().ConfigureAwait(false);
            try { await renderTask.ConfigureAwait(false); } catch { }
            renderTask = null;
            renderCancellation.Dispose();
            renderCancellation = null;
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (renderTask is null) return;
        if (renderCancellation is not null) await renderCancellation.CancelAsync().ConfigureAwait(false);
        await renderTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        renderTask = null;
        renderCancellation?.Dispose();
        renderCancellation = null;
        Device = null;
        lock (writeLock)
        {
            pending.Clear();
            pendingSamples = 0;
            partial = null;
            partialOffset = 0;
            converter = null;
        }
    }

    public void Write(ReadOnlySpan<short> samples)
    {
        if (samples.IsEmpty) return;
        lock (writeLock)
        {
            var target = converter;
            if (target is null) return;
            var converted = target.Convert(samples);
            if (converted.Length == 0) return;
            pending.Enqueue(converted);
            pendingSamples += converted.Length;
            // Drop from the front: the newest audio is the one the listener is waiting on.
            while (pendingSamples > maxPendingSamples && pending.Count > 1)
            {
                pendingSamples -= pending.Dequeue().Length;
            }
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);

    private void RenderLoop(int sourceRate, int sourceChannels, TaskCompletionSource started, CancellationToken cancellationToken)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? endpoint = null;
        IAudioClient? client = null;
        IAudioRenderClient? renderClient = null;
        var mixFormatPointer = IntPtr.Zero;
        var comInitialized = NativeMethods.CoInitializeEx(IntPtr.Zero, 0) >= 0;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            endpoint = ResolvePreferredEndpoint(enumerator);
            var deviceId = WasapiLoopbackBackend.GetDeviceId(endpoint);
            var deviceName = WasapiLoopbackBackend.TryGetDeviceName(endpoint) ?? "Default playback device";
            var clientGuid = typeof(IAudioClient).GUID;
            CheckHResult(endpoint.Activate(ref clientGuid, 23, IntPtr.Zero, out var clientObject),
                "The playback device could not be activated.");
            client = (IAudioClient)clientObject;
            CheckHResult(client.GetMixFormat(out mixFormatPointer), "The playback mix format is unavailable.");
            var waveFormat = Marshal.PtrToStructure<WaveFormatEx>(mixFormatPointer);
            RequireFloat32(mixFormatPointer, waveFormat);

            deviceChannels = waveFormat.Channels;
            deviceSampleRate = (int)waveFormat.SamplesPerSec;
            Device = new AudioDeviceInfo(deviceId, deviceName, deviceSampleRate, deviceChannels, AudioSampleFormat.IeeeFloat32);
            lock (writeLock)
            {
                converter = new PcmStreamConverter(sourceRate, sourceChannels, deviceSampleRate, deviceChannels);
                maxPendingSamples = (int)(LatencyBudget.TotalSeconds * deviceSampleRate) * deviceChannels;
                pending.Clear();
                pendingSamples = 0;
                partial = null;
                partialOffset = 0;
            }

            CheckHResult(client.Initialize(0, 0, 10_000_000, 0, mixFormatPointer, IntPtr.Zero),
                "WASAPI playback initialization failed.");
            CheckHResult(client.GetBufferSize(out var bufferFrames), "The playback buffer size is unavailable.");
            var renderGuid = typeof(IAudioRenderClient).GUID;
            CheckHResult(client.GetService(ref renderGuid, out var renderObject), "The WASAPI render service is unavailable.");
            renderClient = (IAudioRenderClient)renderObject;

            // Prime with silence so the device starts on a full buffer instead of underrunning
            // while the first remote packets are still in flight.
            WriteFrames(renderClient, bufferFrames, waveFormat);
            CheckHResult(client.Start(), "Audio playback could not start.");
            started.TrySetResult();

            // Poll at roughly a third of the buffer duration: often enough that the device
            // never runs dry, rarely enough that the thread is idle most of the time.
            var pollMs = Math.Max(5, (int)(bufferFrames * 1000L / deviceSampleRate / 3));
            while (!cancellationToken.IsCancellationRequested)
            {
                cancellationToken.WaitHandle.WaitOne(pollMs);
                if (cancellationToken.IsCancellationRequested) break;
                CheckHResult(client.GetCurrentPadding(out var padding), "The playback buffer state is unavailable.");
                var free = bufferFrames - padding;
                if (free > 0) WriteFrames(renderClient, free, waveFormat);
            }
        }
        catch (Exception error)
        {
            var mapped = Map(error);
            if (!started.TrySetException(mapped) && !cancellationToken.IsCancellationRequested) Faulted?.Invoke(mapped);
        }
        finally
        {
            client?.Stop();
            ReleaseCom(renderClient);
            ReleaseCom(client);
            ReleaseCom(endpoint);
            ReleaseCom(enumerator);
            if (mixFormatPointer != IntPtr.Zero) Marshal.FreeCoTaskMem(mixFormatPointer);
            if (comInitialized) NativeMethods.CoUninitialize();
        }
    }

    private void WriteFrames(IAudioRenderClient renderClient, uint frames, WaveFormatEx format)
    {
        if (frames == 0) return;
        CheckHResult(renderClient.GetBuffer(frames, out var buffer), "Windows could not acquire the playback buffer.");
        var sampleCount = checked((int)frames * format.Channels);
        var written = 0;
        var block = new float[sampleCount];
        lock (writeLock)
        {
            while (written < sampleCount)
            {
                if (partial is null)
                {
                    if (pending.Count == 0) break;
                    partial = pending.Dequeue();
                    pendingSamples -= partial.Length;
                    partialOffset = 0;
                }
                var take = Math.Min(sampleCount - written, partial.Length - partialOffset);
                partial.AsSpan(partialOffset, take).CopyTo(block.AsSpan(written, take));
                written += take;
                partialOffset += take;
                if (partialOffset >= partial.Length)
                {
                    partial = null;
                    partialOffset = 0;
                }
            }
        }
        // Anything not filled stays zero: silence is the only honest thing to play when the
        // remote side has not sent audio for this slice.
        Marshal.Copy(block, 0, buffer, sampleCount);
        // AUDCLNT_BUFFERFLAGS_SILENT is deliberately not used even for an all-silent block:
        // the buffer is already zeroed and the flag would only save a copy.
        CheckHResult(renderClient.ReleaseBuffer(frames, 0), "Windows could not release the playback buffer.");
    }

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
            enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Communications, out var endpoint),
            "No playback device is available.");
        return endpoint;
    }

    /// <summary>
    /// Shared-mode WASAPI only accepts the endpoint's own mix format, which is 32-bit float on
    /// every supported Windows version. Refusing anything else keeps the render loop from
    /// silently writing float samples into a 16-bit buffer.
    /// </summary>
    private static void RequireFloat32(IntPtr pointer, WaveFormatEx format)
    {
        if (format.FormatTag == 3 && format.BitsPerSample == 32) return;
        if (format.FormatTag == 0xFFFE && format.ExtraSize >= 22 && format.BitsPerSample == 32)
        {
            var subFormat = Marshal.PtrToStructure<Guid>(pointer + 24);
            if (subFormat == new Guid("00000003-0000-0010-8000-00aa00389b71")) return;
        }
        throw new AudioCaptureException(
            AudioCaptureError.UnsupportedFormat,
            $"Unsupported playback mix format: tag {format.FormatTag}, {format.BitsPerSample} bits.");
    }

    private static AudioCaptureException Map(Exception error)
    {
        if (error is AudioCaptureException captureError) return captureError;
        if (error is WasapiException comError)
        {
            var kind = comError.HResult switch
            {
                unchecked((int)0x88890004) => AudioCaptureError.DeviceLost,
                unchecked((int)0x80070490) => AudioCaptureError.NoDevice,
                unchecked((int)0x80070005) => AudioCaptureError.AccessDenied,
                _ => AudioCaptureError.PlatformFailure
            };
            var message = kind switch
            {
                AudioCaptureError.DeviceLost => "The playback device was disconnected or changed.",
                AudioCaptureError.NoDevice => "No playback device is available.",
                AudioCaptureError.AccessDenied => "Windows denied access to the playback device.",
                _ => "Windows audio playback failed."
            };
            return new AudioCaptureException(kind, message, error);
        }
        return new AudioCaptureException(AudioCaptureError.PlatformFailure, "Windows audio playback failed.", error);
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

[ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioRenderClient
{
    // Declaration order must match the COM vtable order.
    [PreserveSig] int GetBuffer(uint frames, out IntPtr data);
    [PreserveSig] int ReleaseBuffer(uint frames, uint flags);
}
