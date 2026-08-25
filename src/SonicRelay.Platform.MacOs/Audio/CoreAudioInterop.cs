using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SonicRelay.Platform.MacOs.Audio;

/// <summary>
/// The AudioToolbox AudioQueue entry points used for playback.
///
/// AudioQueue is a plain C API, so it is reachable from managed code exactly the way the
/// Windows backend reaches WASAPI: direct interop, no native build step, no Swift helper. That
/// matters here — the capture side already needs a compiled Swift helper because
/// ScreenCaptureKit is Objective-C only, and every extra native artifact is one more thing the
/// packaging and CI have to carry to reach a user.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class CoreAudioInterop
{
    private const string AudioToolbox = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";

    /// <summary>Called on an AudioQueue-owned thread when a buffer has finished playing.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void AudioQueueOutputCallback(IntPtr userData, IntPtr queue, IntPtr buffer);

    [DllImport(AudioToolbox)]
    internal static extern int AudioQueueNewOutput(
        ref AudioStreamBasicDescription format,
        IntPtr callback,
        IntPtr userData,
        IntPtr callbackRunLoop,
        IntPtr callbackRunLoopMode,
        uint flags,
        out IntPtr queue);

    [DllImport(AudioToolbox)]
    internal static extern int AudioQueueAllocateBuffer(IntPtr queue, uint bufferByteSize, out IntPtr buffer);

    [DllImport(AudioToolbox)]
    internal static extern int AudioQueueEnqueueBuffer(
        IntPtr queue,
        IntPtr buffer,
        uint packetDescriptionCount,
        IntPtr packetDescriptions);

    [DllImport(AudioToolbox)]
    internal static extern int AudioQueueStart(IntPtr queue, IntPtr startTime);

    [DllImport(AudioToolbox)]
    internal static extern int AudioQueueStop(IntPtr queue, [MarshalAs(UnmanagedType.U1)] bool immediate);

    [DllImport(AudioToolbox)]
    internal static extern int AudioQueueDispose(IntPtr queue, [MarshalAs(UnmanagedType.U1)] bool immediate);
}

/// <summary>
/// CoreAudio's `AudioStreamBasicDescription`. Field order and widths are the ABI contract —
/// they must match `<CoreAudioTypes/CoreAudioBaseTypes.h>` exactly.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct AudioStreamBasicDescription
{
    public double SampleRate;
    public uint FormatId;
    public uint FormatFlags;
    public uint BytesPerPacket;
    public uint FramesPerPacket;
    public uint BytesPerFrame;
    public uint ChannelsPerFrame;
    public uint BitsPerChannel;
    public uint Reserved;
}

/// <summary>
/// CoreAudio's `AudioQueueBuffer`. Only the first three fields are read or written here; the
/// rest exist so the struct's size and offsets match the native one.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct AudioQueueBuffer
{
    public uint AudioDataBytesCapacity;
    public IntPtr AudioData;
    public uint AudioDataByteSize;
    public IntPtr UserData;
    public uint PacketDescriptionCapacity;
    public IntPtr PacketDescriptions;
    public uint PacketDescriptionCount;
}

/// <summary>
/// Builds the stream description for the PCM16 this app plays. Separated from the interop so
/// the format — the one part of the backend that is pure arithmetic — can be tested on any
/// platform, which is the only part of a CoreAudio backend a Linux or Windows agent can check.
/// </summary>
internal static class CoreAudioStreamFormat
{
    /// <summary>`lpcm` — the four-character code for linear PCM.</summary>
    internal const uint LinearPcmFormat = 0x6C70636D;

    internal const uint FormatFlagIsSignedInteger = 1 << 2;
    internal const uint FormatFlagIsPacked = 1 << 3;

    /// <summary>Interleaved, packed, little-endian signed 16-bit PCM.</summary>
    public static AudioStreamBasicDescription CreatePcm16(int sampleRate, int channelCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        if (channelCount is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(channelCount));

        // Interleaved PCM is one packet per frame, so bytes-per-packet equals bytes-per-frame.
        var bytesPerFrame = (uint)(channelCount * sizeof(short));
        return new AudioStreamBasicDescription
        {
            SampleRate = sampleRate,
            FormatId = LinearPcmFormat,
            // No kAudioFormatFlagIsBigEndian: absent means little-endian, which is what the
            // decoder produces and what every Mac this runs on uses natively.
            FormatFlags = FormatFlagIsSignedInteger | FormatFlagIsPacked,
            BytesPerPacket = bytesPerFrame,
            FramesPerPacket = 1,
            BytesPerFrame = bytesPerFrame,
            ChannelsPerFrame = (uint)channelCount,
            BitsPerChannel = 8 * sizeof(short),
            Reserved = 0,
        };
    }
}
