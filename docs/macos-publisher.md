# macOS Publisher

The SonicRelay publisher ships on macOS from the same Avalonia shell as Windows and Linux (issue #62). This document covers installation, the permissions macOS requires for system audio capture, supported systems, and known limitations for the macOS release assets built by `.github/workflows/release.yml`.

## Release assets

Every tagged release publishes four macOS assets alongside the Windows and Linux ones, all built from the same tag/commit:

- `SonicRelay-MacPublisher-osx-arm64-<version>.dmg` — Apple Silicon disk image; drag `SonicRelay.app` to Applications.
- `SonicRelay-MacPublisher-osx-arm64-<version>.zip` — the same Apple Silicon app bundle as an archive.
- `SonicRelay-MacPublisher-osx-x64-<version>.dmg` — Intel disk image.
- `SonicRelay-MacPublisher-osx-x64-<version>.zip` — Intel app bundle archive.
- `checksums-sha256.txt` — SHA-256 checksums for every release asset (Windows, Linux, and macOS).

Each bundle embeds a `BUILD-INFO.txt` (version, commit, runtime, build timestamp, and whether the build was signed and notarized) in `SonicRelay.app/Contents/MacOS/`.

Apple Silicon (`osx-arm64`) is the primary target; `osx-x64` is built from the same source and packaged identically. There is no Universal Binary — see [Known limitations](#known-limitations) for why.

## Supported systems

| Tier | Systems |
| --- | --- |
| Officially supported | macOS 15 (Sequoia) on Apple Silicon |
| Best effort | macOS 14 (Sonoma) and macOS 26 on Apple Silicon; macOS 14+ on Intel |
| Out of scope | macOS 13 (Ventura) and earlier; Mac App Store distribution; iOS/iPadOS; capturing a single application's audio; capturing a chosen output device rather than the system mix |

### Why macOS 14 is a hard floor

Two independent constraints apply, and the **higher** of the two decides:

1. **The .NET 10 runtime supports macOS 14, 15, and 26 only.** This is the binding constraint. It is not something SonicRelay chooses or can waive — see [.NET 10 supported operating systems](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md).
2. **ScreenCaptureKit audio capture requires macOS 13+.** System audio capture uses `SCStreamConfiguration.capturesAudio`, which did not exist before Ventura.

So the bundle's `LSMinimumSystemVersion` is **14.0** (constraint 1), while the native helper's own deployment target stays at 13.0 (constraint 2, its real API requirement). An older system refuses to launch the app rather than failing later with no audio.

### Could macOS 13 or earlier be supported?

Not without changes well outside this feature's scope, and not at all below macOS 13 for audio:

- **macOS 13 (Ventura)** is blocked only by the .NET runtime, not by the capture design. Supporting it would mean targeting an older .NET whose macOS 13 support is itself ending, and would put the whole app — not just macOS — on an unsupported runtime. Not worth it for one OS version.
- **macOS 12 and earlier** cannot capture system audio through any supported Apple API. ScreenCaptureKit has no audio before 13, and Core Audio process taps (`AudioHardwareCreateProcessTap`) are *newer* still, macOS 14.2+. The only remaining route is a virtual audio device (a HAL plug-in such as BlackHole, or one SonicRelay would ship itself).

  Shipping our own driver is ruled out by [the non-admin checklist](non-admin-checklist.md), which forbids a custom audio driver, a kernel-mode component, and any admin-required or machine-wide dependency for normal usage. Supporting a *user-installed* third-party device is technically possible — it would be a separate capture backend behind the same `IAudioCaptureBackend` seam, much as PipeWire is on Linux — but it needs the user to install a driver and re-route their system output, which also means they stop hearing their own audio unless they build an aggregate device. That is a different product decision, not a version bump; raise it as its own issue if the audience needs it.

## Installing

Open the `.dmg` and drag **SonicRelay** to your Applications folder, then launch it from Applications or Spotlight. The `.zip` contains the same `SonicRelay.app`; unzip it anywhere and run it.

Installation never requires an administrator password, and SonicRelay installs no launch daemon, no system extension, no audio driver, and no login item. Everything it writes lives under your own home directory.

**Uninstall:** drag `SonicRelay.app` to the Trash. To also remove its stored settings, delete `~/.local/share/SonicRelay/WindowsPublisher/` (see [Where SonicRelay stores data](#where-sonicrelay-stores-data)).

### If macOS refuses to open the app

Releases are signed with a Developer ID certificate and notarized by Apple **when the repository has Apple Developer credentials configured**. Builds produced without those credentials — including every pull-request build and any fork's build — are unsigned, and Gatekeeper will refuse them with "SonicRelay.app is damaged" or "cannot be opened because the developer cannot be verified". For an unsigned build you trust, right-click the app and choose **Open**, then confirm; or clear the download quarantine flag:

```bash
xattr -dr com.apple.quarantine /Applications/SonicRelay.app
```

`BUILD-INFO.txt` records which kind of build you have (`signing=unsigned`, `signed`, or `signed+notarized`).

## Required permissions

SonicRelay needs exactly one macOS privacy permission: **Screen & System Audio Recording** (Screen Recording on macOS 13/14).

This is not a mistake in the prompt. macOS has no separate "record system audio" permission and no loopback capture device: the only supported way for an app to read the system output mix is ScreenCaptureKit, which is gated behind the Screen Recording grant. SonicRelay captures **audio only** — its capture stream is configured with a 2×2-pixel, one-frame-per-second video path purely because ScreenCaptureKit requires a display target. No screen content is captured, transmitted, or stored.

**Granting it:** the first time you start capture, macOS shows the consent prompt. If you dismiss it, or you need to grant it later, open **System Settings → Privacy & Security → Screen & System Audio Recording** and enable SonicRelay. macOS requires the app to be restarted after the grant changes.

**When it is missing,** capture stops with an actionable message pointing at System Settings, and SonicRelay does *not* retry in a loop — a revoked grant is something only you can restore. Sign-in, pairing, session, and viewer flows are unaffected; only audio capture is blocked.

SonicRelay deliberately requests **no** microphone permission, no camera permission, no accessibility permission, no full disk access, and no App Sandbox entitlements. All network traffic is outbound (API, signaling, WebRTC/STUN/TURN).

## How capture works

macOS capture is a small native helper, `sonicrelay-audio-tap`, that lives inside the app bundle at `SonicRelay.app/Contents/MacOS/`. It is written in Swift against ScreenCaptureKit and streams raw PCM16 stereo 48 kHz on its stdout; the .NET side supervises exactly one helper per capture session and feeds those frames into the same Opus/WebRTC pipeline every platform uses.

The helper must stay inside the bundle. macOS grants Screen Recording consent to a code-signed bundle identity, so a copy elsewhere on disk would not carry your grant — SonicRelay therefore only ever resolves the helper from inside its own bundle, never from `PATH`. (For local development against a helper built by `packaging/macos/build-audio-tap.sh` outside a bundle, set `SONICRELAY_AUDIO_TAP` to its path.)

### Output device selection

The audio page's device picker shows only **System default** on macOS. ScreenCaptureKit taps the system output mix rather than a chosen endpoint: whichever device you send output to is what gets captured. Listing CoreAudio output devices would produce a picker whose entries could not change what is captured, so macOS reports no selectable endpoints instead of offering a control that does nothing.

## Where SonicRelay stores data

Settings and diagnostics live under `~/.local/share/SonicRelay/WindowsPublisher/` — .NET maps `LocalApplicationData` to `~/.local/share` on macOS rather than `~/Library/Application Support`. This is shared with the Linux build rather than being idiomatically Mac; moving it is a follow-up that needs a migration step for existing installs.

Device credentials are **not** persisted on macOS. The device-credential store is protected with Windows DPAPI, which has no macOS equivalent wired up yet, so bootstrap reports secure storage as unavailable rather than writing an unprotected token to disk. In practice: pairing and streaming work normally, but the device re-bootstraps its identity after a restart instead of reconnecting automatically. A Keychain-backed store is the macOS half of the same follow-up tracked for Linux's Secret Service store (issue #26).

## Known limitations

- No Universal Binary. `osx-arm64` and `osx-x64` are separate downloads. A universal app would mean `lipo`-merging every native dylib the self-contained .NET runtime and Avalonia ship, for a bundle roughly twice the size that most users only use half of; two clearly labelled downloads were judged the better trade. Both are built from the same commit by the same job.
- No Mac App Store build. The store requires the App Sandbox, and sandboxed system audio capture needs a different, entitlement-based design.
- No Keychain-backed credential storage yet (see above) — the device re-bootstraps after a restart.
- No login item / "start at login" support.
- Capture follows the system output mix only; there is no per-application or per-output-device capture.
- macOS shows its purple screen-capture indicator in the menu bar while capture is running. That is the system's own indicator for ScreenCaptureKit and cannot be suppressed; SonicRelay stops the helper as soon as capture stops so the indicator clears promptly.
- Audio capture, the permission flow, and Gatekeeper behaviour need validation on a real Mac. CI compiles, packages, signs, and launches the app, but a GitHub runner cannot grant Screen Recording consent or produce real system audio.

## Diagnostics

The Diagnostics page reports platform state for support requests, including `osPlatform`, the resolved helper path, Screen Recording permission state, and the selected audio device — never tokens, raw environment variables, or unbounded process output. See [the Windows publisher's diagnostics section](windows-publisher.md#diagnostics-and-safe-sharing) for the shared export/redaction model.

## CI and release process

`.github/workflows/ci.yml` builds and tests the solution on `windows-latest`, `ubuntu-24.04`, and `macos-15` for every pull request and push to `main`. The macOS leg additionally runs a startup smoke test that builds the real `SonicRelay.app` — compiling the native ScreenCaptureKit helper with `swiftc` and generating the icon set and `Info.plist` — and then launches the bundled binary, so a change that breaks either macOS packaging or macOS startup fails on the pull request rather than at release time. On non-PR runs, a `package-release-macos` job publishes `osx-arm64` and `osx-x64`, packages both, and extends the run's prerelease.

`.github/workflows/release.yml` releases on `v*` tags (or manual dispatch): the Windows job creates the GitHub Release, the Linux job extends it, and then a `macos-package` job checks out the identical commit and adds the macOS assets. The macOS jobs run after the Linux ones rather than beside them because both extend the same release notes and the same canonical `checksums-sha256.txt`.

**No contributor needs a Mac.** All macOS-specific tooling (`swiftc`, `iconutil`, `codesign`, `notarytool`, `hdiutil`) runs on GitHub-hosted `macos-15` runners via [`packaging/macos/build-app-bundle.sh`](../packaging/macos/build-app-bundle.sh).

### Configuring signing and notarization

Signing and notarization are opt-in. With no credentials configured, the jobs still produce working, clearly-marked unsigned bundles. To enable them, set these repository secrets:

| Secret | Purpose |
| --- | --- |
| `MACOS_CERTIFICATE_P12_BASE64` | Base64-encoded `.p12` holding the Developer ID Application certificate and its private key |
| `MACOS_CERTIFICATE_PASSWORD` | Password protecting that `.p12` |
| `MACOS_SIGN_IDENTITY` | The identity to sign with, e.g. `Developer ID Application: Example Ltd (TEAMID1234)` |
| `MACOS_NOTARY_APPLE_ID` | Apple ID used with `notarytool` |
| `MACOS_NOTARY_TEAM_ID` | Developer Team ID |
| `MACOS_NOTARY_PASSWORD` | App-specific password for that Apple ID |

The certificate is imported into a throwaway keychain by [`.github/scripts/import-macos-certificate.sh`](../.github/scripts/import-macos-certificate.sh), and the `.p12` is deleted from disk as soon as it is imported. Signing uses the Hardened Runtime with the exceptions in [`packaging/macos/SonicRelay.entitlements`](../packaging/macos/SonicRelay.entitlements) — JIT, unsigned executable memory, and library validation disabled — which a self-contained .NET application requires and Apple's notary service expects to be declared. Nested binaries are signed before the bundle that contains them; `--deep` is deliberately not used, since Apple documents it as unsuitable for distribution builds.

Notarization only runs when the bundle was signed, and the ticket is stapled before the `.zip` and `.dmg` are built, so Gatekeeper accepts the download offline.
