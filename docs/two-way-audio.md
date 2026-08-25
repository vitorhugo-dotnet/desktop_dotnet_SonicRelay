# Two-way audio (duplex sessions)

Implements the desktop half of [dotnet_SonicRelay#22](https://github.com/vitorhugo-dotnet/dotnet_SonicRelay/issues/22).

**SonicRelay never captures a microphone.** What it shares — in every mode, in every
direction — is the system/app audio mix: whatever the machine is playing. Two-way audio means
both sides share that mix with each other, not that anyone talks into a microphone.

A session's mode is chosen once, when it is created, and never changes (backend ADR 0007).
`broadcast` — the default, and what every session was before this — publishes this machine's
audio to participants that only listen. `duplex` also plays back what the other authorized
participants are sending.

## What changes in a duplex session

| | `broadcast` | `duplex` |
| --- | --- | --- |
| Capture source | system output mix | **the same** system output mix |
| Audio m-line | `sendonly` | `sendrecv`, from the very first offer |
| Playback | none | decoded Opus from authorized peers |
| Mute | stops the encoder, announced to nobody | stops the encoder, announced to the session |

Capture is deliberately identical in both modes: playback is the only thing two-way audio
adds, and it is the only thing a platform can be missing.

The `sendrecv` direction is set from the first offer even before anyone is sending. This
device is the only offerer in the protocol, so a peer that later starts transmitting has no
way to add an m-line of its own — it can only answer into one that already accepts audio.

## Feedback loops

Capturing the system output *and* playing incoming audio onto that same output is a loop: what
the other side sends is played, picked up by the loopback capture, and sent straight back.

The app cannot fix that from the inside, so it detects and reports it rather than working
around it: `PublisherSnapshot.PlaysIntoCapturedOutput` compares the captured endpoint with the
one playback opened, and the Audio page says so in the two-way card. The fix is to capture a
different output (the picker on that page) or to send Windows' *communications* output — which
is where playback opens — to another device such as a headset.

## Renegotiation

A peer that starts or stops transmitting sends `webrtc.renegotiate`. This device answers with
a fresh offer on the **existing** peer connection — no ICE restart, because the network path
is fine and only the media description changed — and marks the payload `"renegotiation": true`
so the peer applies it in place instead of rebuilding. An ordinary offer is byte-identical to
what pre-duplex builds sent, so a peer that ignores the flag is unaffected.

## Who may publish

Publishing is the backend's decision, carried per participant as `audioSendAllowed`, and no
client can raise it. The publishing device can grant or revoke it at any time through
`POST /api/sessions/{id}/participants/{participantId}/audio-permission`; the backend
broadcasts the result to the whole session, the affected participant included.

The API never parses SDP, so it cannot stop a peer from attaching a track it was not
authorized to send. **Dropping that audio before playback is this client's job**, and it is
done in `WebRtcPublisher` against the last state the server published — never against what a
peer claims about itself. A peer with no published state at all is a client from before duplex
existed, whose contract was simply "the publisher publishes", so its audio is played.

## Platform support

| Platform | Capture (send) | Playback (receive) | Two-way |
| --- | --- | --- | --- |
| Windows | WASAPI loopback | `WasapiRenderBackend` (WASAPI shared mode) | yes |
| Linux | PipeWire sink monitor | `PipeWirePlaybackBackend` (`pw-play` over stdin) | yes |
| macOS | ScreenCaptureKit tap | `CoreAudioPlaybackBackend` (AudioQueue) | yes |

On Linux `pw-play` is located optionally, so an install without the full PipeWire user tools
keeps publishing and simply offers no two-way audio.

macOS playback is a CoreAudio output AudioQueue reached by direct interop, not a native
helper: AudioQueue is a plain C API, unlike the Objective-C-only ScreenCaptureKit that forces
the capture side through a compiled Swift binary. So the app bundle carries nothing extra for
playback, and it needs no permission of its own — recording the screen requires TCC consent,
playing audio does not, so a Mac gets two-way audio with the permissions it already had.

All three backends share `PcmPlaybackBuffer`, which is where the two rules that matter live: a
write never blocks (it runs on the WebRTC receive path, where blocking would stall the peer
connection), and when the device falls behind it discards the *oldest* audio, because a gap is
recoverable where accumulating delay is not.

## What is not here

The mobile viewer cannot yet publish *its* system audio, so "play music on the phone and hear
it on the PC" does not work end to end. That is a limitation of the mobile client's media
stack, not of this protocol or of this app — see the note in the Flutter repository. This side
is ready to receive it the moment a peer can send it.

## Related docs

- [architecture.md](architecture.md) — project boundaries and platform contracts.
- Backend: `docs/protocol.md` and `docs/adr/0007-duplex-audio-sessions.md` in the API repository.
