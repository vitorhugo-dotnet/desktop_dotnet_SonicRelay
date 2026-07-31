using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SonicRelay.Windows.Audio;

[SupportedOSPlatform("windows")]
internal sealed class WasapiLoopbackBackend : IAudioCaptureBackend
{
    private const int DeviceInvalidated = unchecked((int)0x88890004);
    private const int DeviceNotFound = unchecked((int)0x80070490);
    private readonly Func<string?> _preferredDeviceId;
    private CancellationTokenSource? _captureCancellation;
    private Task? _captureTask;
    private IAudioClient? _audioClient;
    private volatile bool _paused;

    public WasapiLoopbackBackend(Func<string?>? preferredDeviceId = null)
    {
        // Read at each StartAsync so a settings change applies to the next capture.
        _preferredDeviceId = preferredDeviceId ?? (() => null);
    }

    public AudioDeviceInfo? Device { get; private set; }
    public event Action<AudioFrame, AudioLevelSnapshot>? FrameAvailable;
    public event Action<AudioCaptureException>? Faulted;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_captureTask is not null) return;
        cancellationToken.ThrowIfCancellationRequested();
        _captureCancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _captureTask = Task.Run(() => CaptureLoop(started, _captureCancellation.Token), CancellationToken.None);
        try { await started.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch
        {
            _captureCancellation.Cancel();
            try { await _captureTask.ConfigureAwait(false); } catch { }
            _captureTask = null;
            _captureCancellation.Dispose();
            _captureCancellation = null;
            throw;
        }
    }

    public Task PauseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_paused) return Task.CompletedTask;
        try { CheckHResult(_audioClient?.Stop() ?? 0, "WASAPI could not pause capture."); }
        catch (Exception error) { throw MapException(error); }
        _paused = true;
        return Task.CompletedTask;
    }

    public Task ResumeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_paused) return Task.CompletedTask;
        try { CheckHResult(_audioClient?.Start() ?? 0, "WASAPI could not resume capture."); }
        catch (Exception error) { throw MapException(error); }
        _paused = false;
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_captureTask is null) return;
        _captureCancellation?.Cancel();
        await _captureTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        _captureTask = null;
        _captureCancellation?.Dispose();
        _captureCancellation = null;
        _paused = false;
        Device = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);

    private void CaptureLoop(TaskCompletionSource started, CancellationToken cancellationToken)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? endpoint = null;
        IAudioCaptureClient? captureClient = null;
        IntPtr mixFormatPointer = IntPtr.Zero;
        var comInitialized = NativeMethods.CoInitializeEx(IntPtr.Zero, 0) >= 0;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            endpoint = ResolvePreferredEndpoint(enumerator);
            var deviceId = GetDeviceId(endpoint);
            var deviceName = TryGetDeviceName(endpoint) ?? "Default render device";
            var audioClientGuid = typeof(IAudioClient).GUID;
            CheckHResult(endpoint.Activate(ref audioClientGuid, 23, IntPtr.Zero, out var audioClientObject), "The default render device could not be activated.");
            _audioClient = (IAudioClient)audioClientObject;
            CheckHResult(_audioClient.GetMixFormat(out mixFormatPointer), "The render mix format is unavailable.");
            var waveFormat = Marshal.PtrToStructure<WaveFormatEx>(mixFormatPointer);
            var sampleFormat = ResolveFormat(mixFormatPointer, waveFormat);
            Device = new AudioDeviceInfo(deviceId, deviceName, (int)waveFormat.SamplesPerSec, waveFormat.Channels, sampleFormat);
            CheckHResult(_audioClient.Initialize(0, 0x00020000, 10_000_000, 0, mixFormatPointer, IntPtr.Zero), "WASAPI loopback initialization failed.");
            var captureGuid = typeof(IAudioCaptureClient).GUID;
            CheckHResult(_audioClient.GetService(ref captureGuid, out var captureObject), "The WASAPI capture service is unavailable.");
            captureClient = (IAudioCaptureClient)captureObject;
            CheckHResult(_audioClient.Start(), "WASAPI loopback capture could not start.");
            started.TrySetResult();

            var stopwatch = Stopwatch.StartNew();
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_paused) { cancellationToken.WaitHandle.WaitOne(10); continue; }
                CheckHResult(captureClient.GetNextPacketSize(out var nextFrames), "WASAPI could not query the next packet.");
                if (nextFrames == 0) { cancellationToken.WaitHandle.WaitOne(5); continue; }
                ReadAvailablePackets(captureClient, waveFormat, sampleFormat, stopwatch.Elapsed);
            }
        }
        catch (Exception error)
        {
            var mapped = MapException(error);
            if (!started.TrySetException(mapped) && !cancellationToken.IsCancellationRequested) Faulted?.Invoke(mapped);
        }
        finally
        {
            if (_audioClient is not null) _audioClient.Stop();
            ReleaseCom(captureClient);
            ReleaseCom(_audioClient);
            ReleaseCom(endpoint);
            ReleaseCom(enumerator);
            _audioClient = null;
            if (mixFormatPointer != IntPtr.Zero) Marshal.FreeCoTaskMem(mixFormatPointer);
            if (comInitialized) NativeMethods.CoUninitialize();
        }
    }

    // Opens the user-selected render endpoint when one is set, falling back to the
    // system default if no id is set or the saved device is no longer present.
    private IMMDevice ResolvePreferredEndpoint(IMMDeviceEnumerator enumerator)
    {
        var preferredId = _preferredDeviceId();
        if (!string.IsNullOrWhiteSpace(preferredId)
            && enumerator.GetDevice(preferredId, out var selected) >= 0
            && selected is not null)
        {
            return selected;
        }
        CheckHResult(
            enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out var endpoint),
            "No default render device is available.");
        return endpoint;
    }

    private void ReadAvailablePackets(IAudioCaptureClient captureClient, WaveFormatEx format, AudioSampleFormat sampleFormat, TimeSpan timestamp)
    {
        while (true)
        {
            CheckHResult(captureClient.GetNextPacketSize(out var nextFrames), "WASAPI could not query an audio packet.");
            if (nextFrames == 0) return;
            CheckHResult(captureClient.GetBuffer(out var buffer, out var frameCount, out var flags, out _, out _), "WASAPI could not read an audio packet.");
            try
            {
                var byteCount = checked((int)frameCount * format.BlockAlign);
                var data = new byte[byteCount];
                if ((flags & 0x2) == 0) Marshal.Copy(buffer, data, 0, byteCount);
                var level = AudioLevelCalculator.Calculate(data, sampleFormat);
                FrameAvailable?.Invoke(new AudioFrame(data, (int)format.SamplesPerSec, format.Channels, sampleFormat, timestamp), level);
            }
            finally { CheckHResult(captureClient.ReleaseBuffer(frameCount), "WASAPI could not release an audio packet."); }
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
        throw new AudioCaptureException(AudioCaptureError.UnsupportedFormat, $"Unsupported render mix format: tag {format.FormatTag}, {format.BitsPerSample} bits.");
    }

    internal static string GetDeviceId(IMMDevice endpoint)
    {
        CheckHResult(endpoint.GetId(out var idPointer), "The render device identifier is unavailable.");
        try { return Marshal.PtrToStringUni(idPointer) ?? "default"; }
        finally { Marshal.FreeCoTaskMem(idPointer); }
    }

    internal static string? GetDeviceName(IMMDevice endpoint)
    {
        IPropertyStore? store = null;
        try
        {
            CheckHResult(endpoint.OpenPropertyStore(0, out store), "The render device properties are unavailable.");
            var key = PropertyKey.DeviceFriendlyName;
            CheckHResult(store.GetValue(ref key, out var value), "The render device name is unavailable.");
            try { return value.Type == 31 ? Marshal.PtrToStringUni(value.PointerValue) : null; }
            finally { _ = NativeMethods.PropVariantClear(ref value); }
        }
        finally { ReleaseCom(store); }
    }

    internal static string? TryGetDeviceName(IMMDevice endpoint)
    {
        try { return GetDeviceName(endpoint); }
        catch (WasapiException) { return null; }
    }

    private static AudioCaptureException MapException(Exception error)
    {
        if (error is AudioCaptureException captureError) return captureError;
        if (error is WasapiException comError)
        {
            var mapped = MapHResult(comError.HResult);
            return new AudioCaptureException(mapped.Error, mapped.Message, error);
        }
        return new AudioCaptureException(AudioCaptureError.PlatformFailure, "Windows audio capture failed.", error);
    }

    internal static AudioCaptureException MapHResult(int errorCode)
    {
        var kind = errorCode switch
        {
            DeviceInvalidated => AudioCaptureError.DeviceLost,
            DeviceNotFound => AudioCaptureError.NoDevice,
            unchecked((int)0x80070005) => AudioCaptureError.AccessDenied,
            _ => AudioCaptureError.PlatformFailure
        };
        var message = kind switch
        {
            AudioCaptureError.DeviceLost => "The selected render device was disconnected or changed.",
            AudioCaptureError.NoDevice => "No default render device is available.",
            AudioCaptureError.AccessDenied => "Windows denied access to the render device.",
            _ => "Windows audio capture failed."
        };
        return new AudioCaptureException(kind, message);
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

internal sealed class WasapiException : Exception
{
    public WasapiException(string message, int errorCode) : base(message) => HResult = errorCode;
}

[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct WaveFormatEx
{
    public ushort FormatTag;
    public ushort Channels;
    public uint SamplesPerSec;
    public uint AvgBytesPerSec;
    public ushort BlockAlign;
    public ushort BitsPerSample;
    public ushort ExtraSize;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    public Guid FormatId;
    public uint PropertyId;
    public static PropertyKey DeviceFriendlyName => new() { FormatId = new("a45c254e-df1c-4efd-8020-67d146a850e0"), PropertyId = 14 };
}

[StructLayout(LayoutKind.Explicit)]
internal struct PropVariant
{
    [FieldOffset(0)] public ushort Type;
    [FieldOffset(8)] public IntPtr PointerValue;
}

internal enum EDataFlow { Render, Capture, All }
internal enum ERole { Console, Multimedia, Communications }

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorComObject;

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    // Declaration order must match the COM vtable order.
    [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, [MarshalAs(UnmanagedType.Interface)] out IMMDeviceCollection devices);
    [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
}

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int Item(uint index, out IMMDevice device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid interfaceId, uint classContext, IntPtr activationParameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore properties);
    [PreserveSig] int GetId(out IntPtr id);
    [PreserveSig] int GetState(out uint state);
}

[ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetAt(uint index, out PropertyKey key);
    [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
    [PreserveSig] int Commit();
}

[ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
    [PreserveSig] int GetBufferSize(out uint bufferFrames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint paddingFrames);
    [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
    [PreserveSig] int GetMixFormat(out IntPtr format);
    [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr eventHandle);
    [PreserveSig] int GetService(ref Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object service);
}

[ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong performancePosition);
    [PreserveSig] int ReleaseBuffer(uint frames);
    [PreserveSig] int GetNextPacketSize(out uint frames);
}

internal static class NativeMethods
{
    [DllImport("ole32.dll")]
    internal static extern int CoInitializeEx(IntPtr reserved, uint concurrencyModel);

    [DllImport("ole32.dll")]
    internal static extern void CoUninitialize();

    [DllImport("ole32.dll")]
    internal static extern int PropVariantClear(ref PropVariant value);
}
