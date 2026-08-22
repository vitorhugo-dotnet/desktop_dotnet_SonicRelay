# macOS Desktop Publisher — Design

Issue: [#62](https://github.com/vitorhugo-dotnet/desktop_dotnet_SonicRelay/issues/62)
Status: implemented (pending validation on real macOS hardware)

## Goal

Ship the SonicRelay publisher on macOS from the existing Avalonia shell, with system audio capture through supported Apple APIs, and with all macOS-specific build, signing, packaging, and notarization performed by GitHub-hosted runners — so no contributor needs to own a Mac for normal development or for a release.

## Constraints that shaped the design

1. **macOS has no loopback capture device.** There is no WASAPI-loopback equivalent, and no first-party CLI equivalent of PipeWire's `pw-record`. The supported route for reading the system output mix is ScreenCaptureKit's audio capture (macOS 13+).
2. **ScreenCaptureKit is a delegate/CMSampleBuffer Objective-C API.** Reaching it from .NET means either substantial Objective-C runtime interop or a native helper.
3. **macOS privacy enforcement is bundle-scoped.** TCC grants Screen Recording consent to a code-signed bundle identity, not to a path or a user.
4. **Capture is system-wide.** ScreenCaptureKit taps the output mix; it cannot be pointed at a chosen output endpoint.
5. **CI cannot validate audio.** A GitHub runner has no way to grant Screen Recording consent or produce real system audio.

## Decisions

### ADR-MACOS-001: Capture through a bundled native helper, not P/Invoke

The macOS adapter shells out to `sonicrelay-audio-tap`, a small Swift executable built against ScreenCaptureKit, which writes raw interleaved PCM16 stereo 48 kHz to stdout and reports failures through structured exit codes.

*Why:* it keeps the Objective-C-shaped API in the language designed for it, and it reuses a supervision model this repository already has working — the Linux adapter supervises `pw-record` exactly this way. The .NET side stays testable against an in-memory process double, with no native dependency in the test run.

*Cost:* an extra build step that only runs on macOS, and a process boundary on the audio path. The boundary is the same one Linux already pays, at the same 20 ms framing.

### ADR-MACOS-002: Share the process runner and PCM framing; do not share the backend

`ChildProcessRunner` (moved to `SonicRelay.Windows.Core.Processes`) and `PcmFrameAssembler` (moved to `SonicRelay.Windows.Audio`) are genuinely platform-neutral and are now shared by both adapters, so the subtle parts — orphan killing, late-exit-subscriber replay, partial-read framing — exist once.

The backends themselves stay separate. `PipeWireProcessBackend` resolves (and falls back between) sink targets on every start; `MacOsAudioTapBackend` has a single system-wide target and instead has to tell a revoked privacy grant apart from a device fault. A shared base class would abstract over the arguments while leaving exactly the interesting differences in overrides, and it would put the repository's most delicate concurrency code at risk for no reduction in real duplication.

### ADR-MACOS-003: A denied permission is terminal, not retryable

`AudioCaptureService` automatically retries `NoDevice` and `DeviceLost`. The helper's `permission-denied` exit maps to `AccessDenied` instead, which is terminal: retrying a grant that only the user can restore in System Settings would spin without progress and bury the actionable message.

Two paths reach it. A helper that exits with the permission code fails `StartAsync` immediately. A helper that starts but never delivers audio — what a grant revoked *after* launch looks like, since ScreenCaptureKit reports it as silence rather than an error — has its startup timeout re-interpreted by asking the non-prompting `check-permission` command, turning an opaque timeout into the same actionable failure.

### ADR-MACOS-004: The helper is resolved only from inside the bundle

`AudioTapLocator` looks beside the app binary and in `Contents/Resources`, never on `PATH`. This follows from constraint 3: a copy found elsewhere would not carry the user's consent, so resolving one would produce a capture process the user never approved. A `SONICRELAY_AUDIO_TAP` override exists for development against a helper built outside a bundle.

### ADR-MACOS-005: No output-device picker entries on macOS

`MacOsOutputDeviceProbe` returns an empty list, so `AudioPageViewModel` shows only its built-in "System default" entry. Enumerating CoreAudio output devices was rejected: per constraint 4, selecting one could not change what is captured, so the picker would be a control that silently does nothing.

### ADR-MACOS-006: Two architecture-specific builds, no Universal Binary

The issue asked for `osx-x64` and/or a Universal Binary to be evaluated. Both `osx-arm64` (primary) and `osx-x64` are built and published from the same job and commit; a universal bundle is **not**.

*Why:* .NET publishes per-RID, so a universal app means `lipo`-merging the app host, every runtime dylib, and every Avalonia native library — roughly double the download for a bundle where each user runs one half. Two clearly labelled downloads carry the same coverage at a fraction of the size and none of the merge fragility. Revisit if a single-download requirement appears.

### ADR-MACOS-007: Signing and notarization are opt-in, and failure to configure them degrades visibly

`build-app-bundle.sh` signs when `MACOS_SIGN_IDENTITY` is set and notarizes when the notary credentials are also set; otherwise it produces the same bundle unsigned and records `signing=unsigned` in `BUILD-INFO.txt`.

*Why:* pull requests and forks have no access to repository secrets, and a release pipeline that hard-fails without them would make the macOS build unusable to contributors. Recording the state in `BUILD-INFO.txt` and the docs keeps "unsigned" a visible fact rather than a silent one.

Nested binaries are signed before the enclosing bundle, and `--deep` is deliberately unused: Apple documents it as unsuitable for distribution builds because it applies the app's entitlements to every nested binary.

### ADR-MACOS-008: macOS release jobs run after the Linux ones, not beside them

Both platform jobs extend the same GitHub Release notes and the same canonical `checksums-sha256.txt` by read-modify-write. Running them in parallel would let the later write clobber the earlier one's additions, so the macOS job depends on the Linux job in both workflows.

## What CI does and does not prove

CI **does** prove, on every pull request: that the solution builds and its tests pass on macOS; that the Swift helper compiles; that the app bundle, icon set, and `Info.plist` are produced; and that the real bundled binary starts and stays up.

CI **cannot** prove that audio is actually captured, that the permission prompt reads correctly, or that Gatekeeper accepts a signed release. Those remain a manual first-release gate on real hardware, listed in `docs/macos-publisher.md`.

## Deferred

- Keychain-backed `IDeviceCredentialStore` (the macOS half of the issue #26 follow-up that also covers Linux's Secret Service store). Until then, device identity re-bootstraps after a restart on both platforms.
- `~/Library/Application Support` as the data directory, which needs a migration step for existing installs.
- Login-item support, Mac App Store distribution, and per-application audio capture.
