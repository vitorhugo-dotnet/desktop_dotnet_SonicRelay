using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SonicRelay.Windows.Audio;

namespace SonicRelay.Platform.MacOs.Audio;

/// <summary>
/// Plays remote audio on macOS through a CoreAudio output AudioQueue.
///
/// AudioQueue owns the timing: it hands back a buffer whenever it needs more audio, on its own
/// thread, and this fills it from <see cref="PcmPlaybackBuffer"/> — zero-filling whatever the
/// peer has not sent. Nothing here blocks the WebRTC receive path, which only ever converts a
/// frame to bytes and drops it in the buffer.
///
/// Unlike the capture side, no native helper is involved: AudioQueue is a C API, so this is
/// interop in managed code exactly like the Windows WASAPI backend, and the app bundle carries
/// nothing extra for it. Playback also needs no TCC permission — recording the screen does,
/// playing audio does not — so a Mac gets two-way audio with the permissions it already had.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class CoreAudioPlaybackBackend : IAudioPlaybackBackend
{
    /// <summary>
    /// How much audio may wait ahead of the device before the oldest is dropped. Matches the
    /// Windows and Linux backends: a gap is recoverable, growing delay is not.
    /// </summary>
    private static readonly TimeSpan LatencyBudget = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// Three buffers of 20 ms. Two is the minimum that keeps the device fed while one is being
    /// refilled; the third absorbs a late callback without a dropout, and 60 ms of device-side
    /// buffering still sits well inside the latency budget above.
    /// </summary>
    private const int BufferCount = 3;
    private const int BufferMilliseconds = 20;

    private readonly object gate = new();

    /// Held for the queue's lifetime: AudioQueue calls this from native code, and a collected
    /// delegate would crash the process rather than fail a call.
    private CoreAudioInterop.AudioQueueOutputCallback? callback;
    private GCHandle callbackHandle;

    private IntPtr queue;
    private PcmPlaybackBuffer? buffer;
    private int bytesPerFrame;
    private bool running;

    public AudioDeviceInfo? Device { get; private set; }

    public event Action<AudioCaptureException>? Faulted;

    public Task StartAsync(int sampleRate, int channelCount, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        if (channelCount is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(channelCount));
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsMacOS())
        {
            throw new AudioCaptureException(
                AudioCaptureError.PlatformFailure,
                "CoreAudio playback is only available on macOS.");
        }

        lock (gate)
        {
            if (running) return Task.CompletedTask;

            var format = CoreAudioStreamFormat.CreatePcm16(sampleRate, channelCount);
            bytesPerFrame = (int)format.BytesPerFrame;
            var bufferBytes = sampleRate / 1000 * BufferMilliseconds * bytesPerFrame;
            buffer = new PcmPlaybackBuffer(
                PcmPlaybackBuffer.CapacityFor(LatencyBudget, sampleRate, channelCount, sizeof(short)));

            callback = OnBufferReady;
            // Pinned as well as referenced: the field alone stops collection, but the pointer
            // handed to native code must also survive compaction.
            callbackHandle = GCHandle.Alloc(callback);

            // A null run loop asks AudioQueue for its own internal thread, which is what makes
            // this backend independent of the UI thread and of any CFRunLoop pumping.
            Check(
                CoreAudioInterop.AudioQueueNewOutput(
                    ref format,
                    Marshal.GetFunctionPointerForDelegate(callback),
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    0,
                    out queue),
                "The macOS playback queue could not be created.");

            try
            {
                for (var i = 0; i < BufferCount; i++)
                {
                    Check(
                        CoreAudioInterop.AudioQueueAllocateBuffer(queue, (uint)bufferBytes, out var allocated),
                        "A macOS playback buffer could not be allocated.");
                    // Primed with silence so the device starts on full buffers instead of
                    // underrunning while the first packets are still in flight.
                    FillBuffer(allocated, (uint)bufferBytes);
                }

                Check(CoreAudioInterop.AudioQueueStart(queue, IntPtr.Zero), "macOS playback could not start.");
            }
            catch
            {
                DisposeQueue();
                throw;
            }

            running = true;
            Device = new AudioDeviceInfo(
                "coreaudio-default-output",
                "Default playback device",
                sampleRate,
                channelCount,
                AudioSampleFormat.Pcm16);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!running && queue == IntPtr.Zero) return Task.CompletedTask;
            DisposeQueue();
        }
        return Task.CompletedTask;
    }

    public void Write(ReadOnlySpan<short> samples)
    {
        if (samples.IsEmpty) return;
        PcmPlaybackBuffer? sink;
        lock (gate) sink = buffer;
        if (sink is null) return;
        // The stream is already PCM16 in the format the queue was opened with, so this is a
        // reinterpret rather than a conversion. Little-endian on every Mac this runs on, which
        // is what the queue's format flags declare.
        sink.Write(MemoryMarshal.AsBytes(samples));
    }

    public ValueTask DisposeAsync()
    {
        lock (gate) DisposeQueue();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// AudioQueue has finished with a buffer and wants it filled again. Runs on a CoreAudio
    /// thread, so it does the least possible work: copy, enqueue, return.
    /// </summary>
    private void OnBufferReady(IntPtr userData, IntPtr audioQueue, IntPtr audioBuffer)
    {
        try
        {
            lock (gate)
            {
                if (!running || audioQueue != queue) return;
                var capacity = Marshal.PtrToStructure<AudioQueueBuffer>(audioBuffer).AudioDataBytesCapacity;
                FillBuffer(audioBuffer, capacity);
            }
        }
        catch (Exception exception)
        {
            // Never let an exception cross back into native code: it would tear the process
            // down instead of surfacing as a device fault.
            Faulted?.Invoke(new AudioCaptureException(
                AudioCaptureError.PlatformFailure, "macOS audio playback failed.", exception));
        }
    }

    /// <summary>
    /// Copies pending audio into a queue buffer and hands it back to the device. Whole frames
    /// only: enqueueing a partial frame would shift the channel interleave for the rest of the
    /// stream.
    /// </summary>
    private void FillBuffer(IntPtr audioBuffer, uint capacityBytes)
    {
        var frames = capacityBytes / (uint)Math.Max(1, bytesPerFrame);
        var byteCount = frames * (uint)bytesPerFrame;
        var descriptor = Marshal.PtrToStructure<AudioQueueBuffer>(audioBuffer);

        var block = new byte[byteCount];
        buffer?.Fill(block);
        Marshal.Copy(block, 0, descriptor.AudioData, block.Length);

        descriptor.AudioDataByteSize = byteCount;
        Marshal.StructureToPtr(descriptor, audioBuffer, fDeleteOld: false);

        Check(
            CoreAudioInterop.AudioQueueEnqueueBuffer(queue, audioBuffer, 0, IntPtr.Zero),
            "A macOS playback buffer could not be enqueued.");
    }

    private void DisposeQueue()
    {
        running = false;
        Device = null;
        buffer?.Clear();
        buffer = null;

        var current = queue;
        queue = IntPtr.Zero;
        if (current != IntPtr.Zero)
        {
            // Immediate on both: this runs when the session is going away, and draining would
            // play audio the user has already left behind. The statuses are deliberately
            // ignored — a queue that cannot be stopped is still being disposed, and there is
            // no recovery to attempt on a teardown path.
            try { _ = CoreAudioInterop.AudioQueueStop(current, immediate: true); } catch { }
            try { _ = CoreAudioInterop.AudioQueueDispose(current, immediate: true); } catch { }
        }

        // Only after the queue is gone: freeing the delegate while it could still be called
        // is exactly the crash the handle exists to prevent.
        if (callbackHandle.IsAllocated) callbackHandle.Free();
        callback = null;
    }

    private static void Check(int status, string message)
    {
        if (status == 0) return;
        // OSStatus values are frequently four-character codes; the numeric form is what a user
        // can search for and what Apple's documentation lists.
        throw new AudioCaptureException(
            status == -50 ? AudioCaptureError.UnsupportedFormat : AudioCaptureError.PlatformFailure,
            $"{message} (OSStatus {status})");
    }
}
