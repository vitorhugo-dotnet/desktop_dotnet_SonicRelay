// SonicRelay macOS system-audio tap helper (issue #62).
//
// macOS has no loopback capture device: an application cannot open the system
// output mix the way WASAPI loopback does on Windows, and there is no
// first-party CLI equivalent of PipeWire's `pw-record`. The supported route is
// ScreenCaptureKit's audio capture (macOS 13+), which is an Objective-C/Swift
// API built on delegates and CMSampleBuffers.
//
// Rather than marshalling that API through P/Invoke, this helper isolates it in
// a tiny native executable that speaks the same contract the .NET side already
// supervises on Linux: raw interleaved PCM on stdout, diagnostics on stderr,
// and a meaningful exit code. MacOsAudioTapBackend supervises exactly one of
// these per capture session through the shared ChildProcessRunner.
//
// Output format (fixed, matching the Linux adapter's `pw-record` invocation):
//   signed 16-bit little-endian, interleaved, 2 channels, 48 kHz.
//
// Build: packaging/macos/build-audio-tap.sh (swiftc, macOS 13 deployment target).

import AVFoundation
import CoreGraphics
import CoreMedia
import Foundation
import ScreenCaptureKit

// MARK: - Exit codes
//
// These are the .NET side's only structured signal about *why* capture failed,
// so they are part of the helper's contract and are mapped one-for-one in
// AudioTapExitCodes.cs. Values follow sysexits.h conventions.
enum ExitCode: Int32 {
    case success = 0
    case usage = 64          // bad arguments
    case unavailable = 69    // no capturable display / ScreenCaptureKit unavailable
    case internalFailure = 70
    case permissionDenied = 77  // Screen Recording (TCC) not granted
    case unsupportedOs = 78     // older than macOS 13
}

let sampleRate = 48_000
let channelCount = 2

func fail(_ code: ExitCode, _ message: String) -> Never {
    FileHandle.standardError.write("sonicrelay-audio-tap: error: \(code): \(message)\n".data(using: .utf8)!)
    exit(code.rawValue)
}

func note(_ message: String) {
    FileHandle.standardError.write("sonicrelay-audio-tap: \(message)\n".data(using: .utf8)!)
}

// MARK: - PCM conversion

/// Converts one ScreenCaptureKit audio buffer to interleaved signed 16-bit PCM.
///
/// ScreenCaptureKit delivers 32-bit float samples, normally non-interleaved
/// (one `AudioBuffer` per channel). Both layouts are handled because the
/// stream's format is negotiated by the system, not guaranteed by us. Channel
/// count is normalised to exactly `channelCount`: mono is duplicated across
/// both output channels and anything wider is truncated, so the byte stream on
/// stdout always matches the frame geometry the .NET assembler expects.
func interleavedPcm16(from buffers: UnsafeMutableAudioBufferListPointer, isInterleaved: Bool) -> Data? {
    guard buffers.count > 0 else { return nil }

    var planes: [UnsafePointer<Float>] = []
    var frameCount = 0

    if isInterleaved {
        guard let raw = buffers[0].mData else { return nil }
        let sourceChannels = Int(buffers[0].mNumberChannels)
        guard sourceChannels > 0 else { return nil }
        frameCount = Int(buffers[0].mDataByteSize) / (MemoryLayout<Float>.size * sourceChannels)
        let samples = raw.assumingMemoryBound(to: Float.self)
        var output = Data(capacity: frameCount * channelCount * 2)
        for frame in 0..<frameCount {
            for channel in 0..<channelCount {
                let sourceChannel = min(channel, sourceChannels - 1)
                appendSample(samples[frame * sourceChannels + sourceChannel], to: &output)
            }
        }
        return output
    }

    for buffer in buffers {
        guard let raw = buffer.mData else { return nil }
        planes.append(UnsafePointer(raw.assumingMemoryBound(to: Float.self)))
        frameCount = max(frameCount, Int(buffer.mDataByteSize) / MemoryLayout<Float>.size)
    }
    guard frameCount > 0 else { return nil }

    var output = Data(capacity: frameCount * channelCount * 2)
    for frame in 0..<frameCount {
        for channel in 0..<channelCount {
            appendSample(planes[min(channel, planes.count - 1)][frame], to: &output)
        }
    }
    return output
}

/// Clamps before scaling: a float sample slightly outside [-1, 1] (legal in a
/// float mix) would otherwise wrap to the opposite polarity as a loud click.
@inline(__always)
func appendSample(_ sample: Float, to output: inout Data) {
    let clamped = max(-1.0, min(1.0, sample))
    let value = Int16(clamped * Float(Int16.max))
    withUnsafeBytes(of: value.littleEndian) { output.append(contentsOf: $0) }
}

// MARK: - Stream output

@available(macOS 13.0, *)
final class SystemAudioTap: NSObject, SCStreamOutput, SCStreamDelegate {
    private let standardOutput = FileHandle.standardOutput
    private let writeQueue = DispatchQueue(label: "dev.sonicrelay.audio-tap.write")
    private var stopped = false

    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard type == .audio, sampleBuffer.isValid, !stopped else { return }
        let isInterleaved = sampleBuffer.formatDescription
            .flatMap { $0.audioStreamBasicDescription }
            .map { $0.mFormatFlags & kAudioFormatFlagIsNonInterleaved == 0 } ?? false

        var pcm: Data?
        try? sampleBuffer.withAudioBufferList { buffers, _ in
            pcm = interleavedPcm16(from: buffers, isInterleaved: isInterleaved)
        }
        guard let pcm, !pcm.isEmpty else { return }

        // Serialised on one queue so frames reach the pipe in capture order.
        // A closed pipe (the .NET side stopped or died) ends the process rather
        // than crashing on SIGPIPE-as-exception; the supervisor treats a
        // non-zero exit while it is still expecting audio as a device fault.
        writeQueue.async { [weak self] in
            guard let self, !self.stopped else { return }
            do {
                try self.standardOutput.write(contentsOf: pcm)
            } catch {
                self.stopped = true
                note("stdout closed; stopping capture")
                exit(ExitCode.success.rawValue)
            }
        }
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        // The system stops the stream when Screen Recording consent is revoked
        // mid-session, when the captured display disappears, or on an internal
        // error. Exiting non-zero surfaces it to the supervisor, which restarts
        // capture through the shared recovery policy.
        fail(.internalFailure, "capture stopped: \(error.localizedDescription)")
    }
}

// MARK: - Commands

/// Screen Recording consent is checked without prompting so the caller can
/// report an actionable state; `capture` prompts once instead, because that is
/// the flow where the user has actually asked to start streaming.
func hasScreenRecordingAccess() -> Bool {
    CGPreflightScreenCaptureAccess()
}

@available(macOS 13.0, *)
func runCapture() async {
    if !hasScreenRecordingAccess() {
        // Shows the system prompt the first time; on later runs it is a no-op
        // and the user must grant access in System Settings.
        _ = CGRequestScreenCaptureAccess()
        if !hasScreenRecordingAccess() {
            fail(.permissionDenied,
                 "Screen Recording permission is required to capture system audio. Grant it in System Settings > Privacy & Security > Screen & System Audio Recording.")
        }
    }

    let content: SCShareableContent
    do {
        content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: false)
    } catch {
        fail(.permissionDenied, "ScreenCaptureKit refused to enumerate shareable content: \(error.localizedDescription)")
    }

    guard let display = content.displays.first else {
        fail(.unavailable, "No capturable display is available.")
    }

    // Audio capture rides on a display stream, so a minimal video path is
    // configured deliberately: the smallest allowed frame at one frame per
    // second keeps the video encoder essentially idle while the audio taps the
    // full system mix. `excludesCurrentProcessAudio` prevents the publisher's
    // own output from feeding back into the capture.
    let configuration = SCStreamConfiguration()
    configuration.capturesAudio = true
    configuration.sampleRate = sampleRate
    configuration.channelCount = channelCount
    configuration.excludesCurrentProcessAudio = true
    configuration.width = 2
    configuration.height = 2
    configuration.minimumFrameInterval = CMTime(value: 1, timescale: 1)
    configuration.queueDepth = 6

    let filter = SCContentFilter(display: display, excludingApplications: [], exceptingWindows: [])
    let tap = SystemAudioTap()
    let stream = SCStream(filter: filter, configuration: configuration, delegate: tap)

    do {
        try stream.addStreamOutput(tap, type: .audio, sampleHandlerQueue: DispatchQueue(label: "dev.sonicrelay.audio-tap.samples"))
        try await stream.startCapture()
    } catch {
        fail(.internalFailure, "Could not start ScreenCaptureKit capture: \(error.localizedDescription)")
    }

    note("capturing system audio: \(sampleRate) Hz, \(channelCount) ch, s16le")

    // The supervisor stops this helper by closing stdin and then terminating
    // it, matching how the Linux adapter stops `pw-record`. Reading stdin to
    // EOF gives a clean shutdown path for the common case.
    let stdin = FileHandle.standardInput
    while let data = try? stdin.read(upToCount: 1024), !data.isEmpty {
        continue
    }

    try? await stream.stopCapture()
    exit(ExitCode.success.rawValue)
}

// MARK: - Entry point

let arguments = Array(CommandLine.arguments.dropFirst())
let command = arguments.first ?? "capture"

switch command {
case "--version", "version":
    print("sonicrelay-audio-tap 1.0 (\(sampleRate) Hz, \(channelCount) ch, s16le)")
    exit(ExitCode.success.rawValue)

case "check-permission":
    guard #available(macOS 13.0, *) else {
        fail(.unsupportedOs, "System audio capture requires macOS 13 (Ventura) or later.")
    }
    exit(hasScreenRecordingAccess() ? ExitCode.success.rawValue : ExitCode.permissionDenied.rawValue)

case "capture":
    guard #available(macOS 13.0, *) else {
        fail(.unsupportedOs, "System audio capture requires macOS 13 (Ventura) or later.")
    }
    // `capture` never returns; the run loop keeps the process alive while the
    // ScreenCaptureKit delegate callbacks arrive on their own queues.
    Task { await runCapture() }
    RunLoop.main.run()

default:
    fail(.usage, "unknown command '\(command)'. Expected: capture | check-permission | --version.")
}
