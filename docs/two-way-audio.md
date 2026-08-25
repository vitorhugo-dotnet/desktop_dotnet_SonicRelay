# Two-way audio (duplex sessions)

Implements the desktop half of [dotnet_SonicRelay#22](https://github.com/vitorhugo-dotnet/dotnet_SonicRelay/issues/22).

A session's mode is chosen once, when it is created, and never changes (backend ADR 0007).
`broadcast` — the default, and what every session was before this — publishes the system
output mix to viewers that only listen. `duplex` publishes this device's **microphone** and
plays back what the other authorized participants send, over the same peer connection.

## What changes in a duplex session

| | `broadcast` | `duplex` |
| --- | --- | --- |
| Capture source | system output (WASAPI loopback / PipeWire sink monitor) | microphone |
| Audio m-line | `sendonly` | `sendrecv`, from the very first offer |
| Playback | none | decoded Opus from authorized peers |
| Mute | stops the encoder, announced to nobody | stops the encoder, announced to the session |

The `sendrecv` direction is set from the first offer even before anyone is transmitting. This
device is the only offerer in the protocol, so a viewer that later turns its microphone on has
no way to add an m-line of its own — it can only answer into one that already accepts audio.

## Renegotiation

A viewer that adds or drops its microphone sends `webrtc.renegotiate`. This device answers
with a fresh offer on the **existing** peer connection — no ICE restart, because the network
path is fine and only the media description changed — and marks the payload
`"renegotiation": true` so the viewer applies it in place instead of rebuilding. An ordinary
offer is byte-identical to what pre-duplex builds sent, so a viewer that ignores the flag is
unaffected.

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

Two-way audio is composed only where the platform supplies **both** halves. Offering it with
one half missing would let a user start a conversation the build could only half hold up.

| Platform | Microphone | Playback |
| --- | --- | --- |
| Windows | `WasapiMicrophoneBackend` (WASAPI shared mode, communications endpoint) | `WasapiRenderBackend` (WASAPI shared mode) |
| Linux | `PipeWireMicrophoneBackend` (`pw-record`, default source) | `PipeWirePlaybackBackend` (`pw-play` over stdin) |
| macOS | not implemented | not implemented |

On Windows the user must have allowed desktop apps to use the microphone in Privacy settings;
a denial surfaces as an `AccessDenied` capture error naming that setting, not as a generic
failure. On Linux `pw-play` is located optionally, so an install without the full PipeWire
user tools keeps publishing and simply offers no two-way audio.

macOS is deliberately excluded rather than stubbed: the bundled helper is a ScreenCaptureKit
system-audio tap, which is the wrong capture path for a microphone, and there is no playback
backend in this repository yet. `PublisherRuntime.SupportsTwoWayAudio` reports false there and
the controls stay off. Adding an AVAudioEngine input/output helper is its own change.

## Related docs

- [architecture.md](architecture.md) — project boundaries and platform contracts.
- Backend: `docs/protocol.md` and `docs/adr/0007-duplex-audio-sessions.md` in the API repository.
