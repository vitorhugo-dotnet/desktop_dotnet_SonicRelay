# Linux Desktop Publisher — Design Spec (Issue #32, Phases 3–5)

## Provenance / context

- **Date:** 2026-07-14
- **Repository:** `vitorhugo-dotnet/windows_SonicRelay`
- **Driven by:** GitHub issue [#32 — Add Linux support with a cross-platform architecture](https://github.com/vitorhugo-dotnet/windows_SonicRelay/issues/32)
- **Previous design:** [`2026-07-11-avalonia-desktop-shell-design.md`](./2026-07-11-avalonia-desktop-shell-design.md)
- **Current state:** the Avalonia shell reached Windows parity and replaced the former WinUI application on `main`.

The previous design deliberately stopped before Linux capture and distribution. This document defines the remaining work: run the existing Avalonia publisher as a real Linux application, capture a selected system-output sink through PipeWire, reuse the existing authentication/signaling/WebRTC/Opus runtime, and publish supported Linux artifacts.

## Scope

This design covers:

1. **Phase 3 — Linux audio adapter and headless validation**
2. **Phase 4 — Linux desktop composition using the existing Avalonia shell**
3. **Phase 5 — Linux CI, packaging, release assets, and documentation**

Implement these as three reviewable pull requests. Every PR must leave `main` buildable and preserve Windows behavior.

## Current codebase state

Relevant facts on `main`:

- `src/SonicRelay.Windows.Desktop` is the shipped Avalonia application.
- `App.axaml.cs` attaches a live `PublisherRuntime` only on Windows. Linux currently opens `MainWindowViewModel.CreatePreview()`.
- `PublisherRuntime.Create(Uri, IAudioCaptureService)` already accepts a platform-neutral capture service, but still constructs token storage internally.
- `AudioCaptureService` already owns reusable lifecycle, diagnostics, retry, device selection, and recovery behavior.
- The default `AudioCaptureService` constructor creates WASAPI implementations internally and is Windows-only.
- `IAudioCaptureBackend` is internal; `IAudioOutputDeviceProbe` is already public.
- `UserScopedTokenStore` uses Windows DPAPI and a Windows-specific directory.
- WebRTC uses managed `SIPSorcery` and `Concentus` packages and has no intentional WASAPI/WinUI dependency.
- CI and release packaging currently run only on Windows and publish `win-x64` ZIP, EXE, and MSI assets.
- Historical `SonicRelay.Windows.*` names are not a functional blocker. Renaming all projects is unrelated churn and is outside this phase.

## Objective

A user on the initially supported Linux environment must be able to:

1. install or extract SonicRelay without installing .NET;
2. launch the same Avalonia shell used on Windows;
3. authenticate against the existing API;
4. list available system-output sinks;
5. select the default or a specific sink;
6. create a publisher session;
7. capture desktop output through PipeWire without root;
8. stream through the existing WebRTC/Opus path in Direct or Relay mode;
9. see real diagnostics and reconnection state;
10. use tray behavior where the desktop exposes a compatible implementation;
11. receive actionable errors when Linux desktop dependencies are unavailable.

## Initial support matrix

### Officially supported

- **Architecture:** `linux-x64`
- **Distribution:** Ubuntu 24.04 LTS Desktop
- **Desktop:** GNOME
- **Sessions:** Ubuntu Wayland and Ubuntu on Xorg
- **Audio:** PipeWire with WirePlumber
- **Artifacts:** self-contained `.deb` and portable `.tar.gz`

Avalonia may render through X11/XWayland under a Wayland desktop. Audio capture talks to the user's PipeWire session and is independent from the display protocol.

### Best effort

- Ubuntu 26.04 LTS
- Debian 13 and compatible Debian-based systems
- KDE Plasma with compatible PipeWire and tray services
- other x64 distributions using the portable archive

### Out of scope

- `linux-arm64`
- Flatpak, Snap, AppImage, or RPM
- macOS
- native Wayland-only rendering as a requirement
- PulseAudio legacy capture as the primary adapter
- Wine
- support for every desktop environment/tray protocol
- broad repository/project renaming
- Linux autostart in the first release

## Selected capture approach

### Decision

Use supervised official PipeWire/WirePlumber tools for the first supported release:

- `pw-dump` for JSON node discovery;
- `wpctl inspect` for resolving the current default sink and inspecting a selected sink;
- `pw-record` for raw PCM capture;
- `secret-tool` for Secret Service-backed token persistence when available.

The adapter launches tools directly with `ProcessStartInfo.ArgumentList`, never through a shell.

### Why

This is the smallest maintainable implementation that uses the installed PipeWire stack and preserves the existing managed audio/WebRTC pipeline. It avoids:

- custom `libpipewire` P/Invoke and ABI/resource-lifetime risk;
- GStreamer and its native plugin matrix;
- an unproven third-party .NET PipeWire wrapper;
- duplicating `AudioCaptureService` lifecycle/recovery logic;
- coupling the primary implementation to PulseAudio compatibility.

`pw-record` can stream raw PCM to stdout with explicit rate, channels, format, latency, and target. That matches the existing `AudioFrame` boundary.

### Alternatives rejected for this phase

#### Native `libpipewire`

Provides the most control, but adds unsafe interop and native lifecycle complexity. Consider it only if profiling proves the supervised-process adapter misses defined latency or reliability targets.

#### GStreamer `pipewiresrc`

Mature, but adds a large runtime/plugin dependency for conversion and media graph features SonicRelay already implements.

#### PulseAudio monitor source

Historically broad, but issue #32 explicitly selected PipeWire. PulseAudio compatibility may be a future fallback, not the main design.

#### `xdg-desktop-portal` ScreenCast

The ScreenCast portal selects monitor/window/virtual visual sources and returns screencast PipeWire streams. It is not a general desktop-output audio picker. Sandboxed packaging therefore requires a separate design.

## Target architecture

```text
SonicRelay.Windows.Desktop (existing Avalonia shell)
  |
  +-- DesktopRuntimeFactory
        |
        +-- WindowsPlatformComposition
        |     +-- AudioCaptureService + WASAPI backend/probe
        |     +-- DPAPI token store
        |
        +-- LinuxPlatformComposition
              +-- AudioCaptureService + PipeWire backend/probe
              +-- Secret Service token store
                    +-- in-memory fallback

PublisherRuntime / Presentation / WebRTC / Signaling / API Client
remain shared and platform-neutral.
```

### New project

```text
src/SonicRelay.Platform.Linux/
  Audio/
    LinuxProcessRunner.cs
    PipeWireCommandLocator.cs
    PipeWireNode.cs
    PipeWireNodeParser.cs
    PipeWireSinkResolver.cs
    PipeWireOutputDeviceProbe.cs
    PipeWireProcessBackend.cs
    PcmFrameAssembler.cs
  Storage/
    SecretServiceTokenStore.cs
    SecretToolProcess.cs
  LinuxPlatformComposition.cs

tests/SonicRelay.Platform.Linux.Tests/
```

Keep the Windows implementation in `SonicRelay.Windows.Audio` during this phase. The asymmetry is temporary technical naming debt, not a reason to mix a large rename with Linux support.

## Shared audio seams

Keep `AudioCaptureService` as the single owner of start/stop/pause/recovery/diagnostics.

Make only the internal backend seam public:

```csharp
public interface IAudioCaptureBackend : IAsyncDisposable
{
    AudioDeviceInfo? Device { get; }
    event Action<AudioFrame, AudioLevelSnapshot>? FrameAvailable;
    event Action<AudioCaptureException>? Faulted;
    Task StartAsync(CancellationToken cancellationToken);
    Task PauseAsync(CancellationToken cancellationToken);
    Task ResumeAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}
```

Expose a platform-neutral factory while keeping the current Windows constructor for compatibility:

```csharp
public static AudioCaptureService Create(
    IAudioCaptureBackend backend,
    IAudioOutputDeviceProbe deviceProbe,
    AudioRecoveryPolicy? recoveryPolicy = null);
```

Move PCM16/float peak and RMS calculation into a shared pure helper so WASAPI and PipeWire do not duplicate it.

Replace generic Windows-specific messages inside `AudioCaptureService` with platform-neutral text. Concrete adapters remain responsible for actionable platform details.

## Sink discovery and selection

### Enumerating sinks

Run `pw-dump` and parse JSON objects whose type is `PipeWire:Interface:Node` and whose `media.class` represents an audio sink.

Map each sink to the existing model:

```csharp
new AudioOutputDevice(
    Id: nodeName,
    Name: description,
    IsDefault: isDefault);
```

Rules:

- `Id` is `node.name`, not a numeric PipeWire ID;
- display name fallback order: `node.description`, `device.description`, `node.nick`, `node.name`;
- malformed/oversized output returns an empty list and a bounded diagnostic rather than crashing Settings;
- the existing `System default` row remains represented by `null` preference.

### Resolving the default sink

`pw-record` must never rely on automatic target selection for desktop-output capture. In record mode, an automatic target may resolve to a capture source such as a microphone.

For `System default`:

1. run `wpctl inspect @DEFAULT_AUDIO_SINK@`;
2. extract `node.name` and, when available, `object.serial`;
3. pass that resolved sink explicitly to `pw-record`.

If no default sink can be resolved, return `AudioCaptureError.NoDevice`.

### Resolving a selected sink

Persist `node.name`. Before every capture start:

1. re-run discovery;
2. find the saved `node.name`;
3. inspect the current node;
4. prefer `object.serial` as the live `pw-record` target, otherwise use `node.name`;
5. if missing, fall back to the current default sink and emit a diagnostic event.

Numeric PipeWire object IDs must not be persisted because they can be reused.

## PipeWire capture process

### Invocation

```text
pw-record
  --raw
  --rate=48000
  --channels=2
  --format=s16
  --latency=20ms
  --target=<resolved object.serial or node.name>
  -
```

Configuration:

```text
sample rate: 48000 Hz
channels: 2
format: signed 16-bit little-endian PCM
requested latency: 20 ms
stdout: raw PCM
stderr: bounded diagnostics
stdin: closed
UseShellExecute: false
```

A target is mandatory. The adapter must verify that it resolves to an audio sink/output monitor path, not a microphone source.

### Frame assembly

Emit exact 20 ms frames:

```text
48000 × 0.020 × 2 channels × 2 bytes = 3840 bytes
```

`PcmFrameAssembler` must:

- tolerate arbitrary pipe read boundaries;
- never emit partial samples;
- retain at most one incomplete frame;
- timestamp from a monotonic `Stopwatch`;
- calculate levels using the shared helper;
- stop promptly on cancellation;
- never run reads on the UI thread.

### Pause/resume

For the first adapter, pause performs a controlled process stop and resume starts a new process against the same resolved sink. The small discontinuity is preferable to Unix signal interop in the MVP.

### Supervision

- Exactly one `pw-record` process per backend instance.
- `StartAsync` completes only after the first complete PCM frame arrives, with a bounded timeout.
- Unexpected exit after startup raises `Faulted`.
- `StopAsync` cancels reads, requests termination, waits briefly, then kills the process tree if needed.
- Stderr is capped and redacted.
- Disposal is idempotent.

### Error mapping

| Failure | `AudioCaptureError` | Behavior |
|---|---|---|
| missing `pw-record`, `pw-dump`, or `wpctl` | `PlatformFailure` | name the missing dependency/package |
| PipeWire session unavailable | `PlatformFailure` | tell the user to verify user PipeWire/WirePlumber services |
| no/default sink missing | `NoDevice` | existing recovery policy retries |
| sink/process lost during capture | `DeviceLost` | existing recovery policy restarts capture |
| permission/socket denied | `AccessDenied` | explain the app must run as the desktop user, not root |
| malformed discovery/inspection output | `PlatformFailure` | bounded diagnostic; UI/signaling stay alive |

A capture failure must not terminate signaling or the Avalonia process.

## Platform runtime composition

Add `DesktopRuntimeFactory` to the desktop project.

```csharp
public sealed record DesktopRuntimeDependencies(
    IAudioCaptureService AudioCapture,
    ITokenStore TokenStore,
    string DeviceName);
```

Change runtime composition to accept dependencies:

```csharp
public static PublisherRuntime Create(
    Uri backendBaseUrl,
    DesktopRuntimeDependencies dependencies);
```

Selection:

```text
Windows -> WASAPI + DPAPI
Linux   -> PipeWire + Secret Service
Other   -> explicit unsupported-platform state
```

`App.axaml.cs` must stop calling `CreatePreview()` on Linux. Preview remains only for the Avalonia designer and explicit visual tests.

Startup failures leave the real shell visible with an actionable platform error and retry action. They must not show fake session metrics.

## Linux token storage

### Primary

Implement `SecretServiceTokenStore` through `secret-tool`:

- serialize token data and provide it through stdin;
- use fixed attributes such as `application=sonicrelay` and `purpose=publisher-token`;
- lookup/clear using the same attributes;
- never place token contents in arguments or environment variables;
- never log token-bearing stdout;
- use bounded timeout/cancellation;
- map unavailable/locked Secret Service to `SecureStorageUnavailable`.

### Fallback

When `secret-tool` or Secret Service is unavailable, use an in-memory store and show:

```text
Secure session storage is unavailable. You can continue, but you will need to sign in again after restarting SonicRelay.
```

Never create a plaintext token file. Existing Windows DPAPI behavior and files remain unchanged.

## Linux paths

Add a platform path resolver:

```text
config: $XDG_CONFIG_HOME/sonicrelay
        fallback ~/.config/sonicrelay
state:  $XDG_STATE_HOME/sonicrelay
        fallback ~/.local/state/sonicrelay
cache:  $XDG_CACHE_HOME/sonicrelay
        fallback ~/.cache/sonicrelay
```

Use it for Linux preferences, diagnostics, and configuration. Preserve current Windows paths.

## Tray and lifecycle

Keep the Avalonia tray integration but expose capability explicitly:

- tray initialization failure never blocks launch;
- minimize/close-to-tray is enabled only after tray creation succeeds;
- without tray support, closing exits instead of leaving an unreachable hidden process;
- diagnostics report tray availability;
- Ubuntu 24.04 GNOME is the release gate.

Autostart is deferred to a separate design.

## Desktop project changes

- use `Exe` for Linux and `WinExe` for Windows;
- condition `app.manifest` and `BuiltInComInteropSupport` to Windows targets;
- keep the current Avalonia version unless a separate upgrade is required;
- reference `SonicRelay.Platform.Linux`;
- keep `UsePlatformDetect()`;
- do not expose Linux tool/process details to views or view models.

## CI

### Required matrix

```text
windows-latest
ubuntu-24.04
```

Linux unit tests use fake process runners and do not require a live PipeWire session in GitHub Actions.

### Linux tests

- `pw-dump` parsing and size limits;
- sink filtering/name fallback;
- default sink `wpctl inspect` parsing;
- stable `node.name` persistence and live target resolution;
- mandatory target enforcement;
- command argument construction without shell interpolation;
- PCM frame assembly across arbitrary reads;
- peak/RMS parity with WASAPI;
- startup timeout/cancellation;
- exit/error mapping;
- device-loss recovery through `AudioCaptureService`;
- Secret Service invocation without leakage;
- platform composition;
- Avalonia Linux startup smoke test;
- repository/release structure checks.

### Manual first-release gate

Validate on a real Ubuntu 24.04 desktop:

1. Wayland session;
2. Xorg session;
3. default sink capture;
4. HDMI/headset sink selection;
5. prove output is captured and microphone is not;
6. sink disconnect/reconnect;
7. Direct WebRTC;
8. forced TURN Relay;
9. tray minimize/restore;
10. restart with Secret Service available;
11. restart with Secret Service unavailable;
12. `.deb` install/upgrade/uninstall;
13. portable archive launch.

## Packaging and release

### Assets

```text
SonicRelay-LinuxPublisher-linux-x64-<version>.tar.gz
SonicRelay-LinuxPublisher-linux-x64-<version>.deb
checksums-sha256.txt
```

Use folder-based self-contained publish for the first release. Do not publish a Linux single-file binary yet.

### Debian layout

```text
/usr/lib/sonicrelay/                 publish output
/usr/bin/sonicrelay                  exec wrapper
/usr/share/applications/sonicrelay.desktop
/usr/share/icons/hicolor/.../sonicrelay.png|svg
```

Package dependencies must provide:

- `pw-record` and `pw-dump`;
- `wpctl`/WirePlumber;
- `secret-tool`;
- Avalonia Linux/X11 native libraries;
- CA certificates and required native runtime libraries.

Installing the package may require administrator authorization. Running SonicRelay must never require root.

### Desktop entry

- `Exec=sonicrelay`
- `Terminal=false`
- stable SonicRelay icon and WM class
- suitable Audio/Network/Utility categories
- no environment-specific absolute user paths

### Release workflow

Keep Windows assets unchanged. Add a Linux packaging job that:

1. restores for `linux-x64`;
2. publishes self-contained output;
3. writes `BUILD-INFO.txt`;
4. creates `.tar.gz` and `.deb`;
5. computes SHA-256 hashes;
6. uploads artifacts;
7. attaches Linux assets to the same GitHub Release.

An official release fails if either platform packaging fails, unless a deliberate manual override is added later.

## Diagnostics

Add:

```text
osPlatform=linux
osDescription=<redacted>
desktopSession=wayland|x11|unknown
pipeWireAvailable=true|false
wirePlumberAvailable=true|false
pwRecordVersion=<version or unavailable>
secretServiceAvailable=true|false
trayAvailable=true|false
selectedAudioDevice=<friendly name>
```

Never include tokens, Secret Service output, arbitrary environment variables, unbounded stderr, or unredacted home paths.

## Security

- never run through `sudo`;
- never invoke `/bin/sh -c`;
- use `ProcessStartInfo.ArgumentList`;
- bound startup, shutdown, reads, and parsed output sizes;
- kill child processes during cancellation/disposal;
- never persist tokens in plaintext;
- never place tokens in args/env/logs;
- treat process/JSON output as untrusted;
- preserve current HTTPS/WSS validation;
- do not weaken Windows DPAPI or Windows release behavior.

## Performance targets

```text
capture: 48 kHz stereo PCM16
frame: 20 ms
capture startup: <= 1.5 s
steady adapter CPU: <= 5% of one logical core
memory: no unbounded growth
capture-path latency regression: <= 40 ms versus equivalent Windows path, excluding network variance
```

These are validation targets, not claims for every machine. Native PipeWire interop requires profiling evidence that this adapter cannot meet them.

## Implementation slices

### PR 1 — Adapter and shared seams

- expose `IAudioCaptureBackend` and factory construction;
- centralize level calculation;
- add Linux project/tests;
- implement discovery/default resolution;
- implement supervised `pw-record` capture;
- validate frames through the existing WebRTC audio bridge;
- keep Linux desktop preview behavior until the adapter is proven.

### PR 2 — Desktop composition

- add `DesktopRuntimeFactory`;
- inject token/runtime dependencies;
- add Secret Service plus in-memory fallback;
- add XDG paths;
- attach real Linux runtime instead of preview;
- make tray capability explicit;
- validate auth, sinks, sessions, reconnect, Direct, and Relay.

### PR 3 — CI and distribution

- add Ubuntu build/test matrix;
- add Linux startup smoke test;
- publish `linux-x64`;
- build `.tar.gz` and `.deb`;
- add icon/desktop entry;
- extend release notes/checksums;
- document install, dependencies, supported systems, diagnostics, and limitations.

## Acceptance criteria

### Architecture

- [ ] Windows and Linux use the same Avalonia views/view models.
- [ ] Linux-specific code is isolated in `SonicRelay.Platform.Linux` and desktop composition.
- [ ] Presentation, signaling, API client, and WebRTC do not reference Linux tools.
- [ ] Shared `AudioCaptureService` owns lifecycle/recovery on both platforms.
- [ ] No broad project rename is required.

### Runtime/security

- [ ] Linux launches real state, not `CreatePreview()`.
- [ ] App starts without root on Ubuntu 24.04 x64.
- [ ] Missing dependencies produce actionable errors.
- [ ] User authenticates against the existing backend.
- [ ] Secret Service persists tokens when available.
- [ ] No plaintext token file is created.
- [ ] Session-only authentication works when secure storage is unavailable.

### Audio

- [ ] Default sink is explicitly resolved and targeted.
- [ ] Available sinks have human-readable names.
- [ ] Selected sink persists by `node.name`.
- [ ] Audio is 48 kHz stereo PCM16 and reaches existing Opus/WebRTC code.
- [ ] Desktop output is captured; microphone is not.
- [ ] Sink loss triggers bounded recovery without crashing UI/signaling.
- [ ] Pause/resume/stop/dispose leave no orphan process.

### Streaming/desktop

- [ ] Flutter viewer receives Linux desktop audio.
- [ ] Direct and forced Relay modes work.
- [ ] real RTT/jitter/loss/bitrate/viewer/ICE/audio metrics remain available.
- [ ] reconnect and session termination match Windows behavior.
- [ ] UI works on Ubuntu Wayland and Xorg sessions.
- [ ] tray works on the release-gating GNOME environment.
- [ ] without tray, closing exits normally.

### CI/distribution

- [ ] Windows and Ubuntu checks are required.
- [ ] Windows assets remain unchanged.
- [ ] `.tar.gz` and `.deb` come from the same tag/commit.
- [ ] artifacts include checksums/build metadata.
- [ ] `.deb` install/upgrade/uninstall are validated.
- [ ] normal execution requires no administrator privileges.
- [ ] installation and limitations are documented.

## Risks

| Risk | Impact | Mitigation |
|---|---:|---|
| CLI output differs across versions | Medium | one official Ubuntu LTS, defensive parsing, tool versions in diagnostics |
| `pw-record` adds latency/lifecycle edges | Medium | fixed frames, bounded supervision, profiling gate before native interop |
| IDs change | Medium | persist `node.name`; resolve live serial/name every start |
| default auto-target captures microphone | High | never use auto; explicitly inspect and target `@DEFAULT_AUDIO_SINK@` |
| tray unavailable | Medium | capability-based behavior; never hide unreachable app |
| Secret Service unavailable | Low | in-memory fallback, warning, no plaintext |
| CI has no real audio session | Medium | fake process tests plus real-desktop manual gate |
| Windows regression | High | keep compatibility seams and full Windows CI/release tests |

## Documentation required

Update:

- `README.md`;
- `docs/architecture.md`;
- `docs/windows-publisher.md` or split it without breaking links;
- add `docs/linux-publisher.md`;
- release notes;
- issue #32 with phase checklist and validation evidence.

## Final decisions

```text
ADR-LINUX-001: Reuse the existing Avalonia shell.
ADR-LINUX-002: Use PipeWire/WirePlumber as the Linux audio stack.
ADR-LINUX-003: Use supervised pw-dump/wpctl/pw-record for the first release.
ADR-LINUX-004: Explicitly resolve and target an output sink; never use record auto-target.
ADR-LINUX-005: Normalize capture to 48 kHz stereo PCM16 with 20 ms frames.
ADR-LINUX-006: Persist sink preference by node.name.
ADR-LINUX-007: Use Secret Service via secret-tool; never plaintext fallback.
ADR-LINUX-008: Officially support Ubuntu 24.04 x64 first.
ADR-LINUX-009: Ship .deb and portable .tar.gz; defer sandboxed packages.
ADR-LINUX-010: Tray is capability-based.
ADR-LINUX-011: Keep historical Windows-prefixed project names during this phase.
```

## Primary references

- Avalonia Linux deployment: https://docs.avaloniaui.net/docs/deployment/linux
- Avalonia TrayIcon: https://docs.avaloniaui.net/controls/navigation/trayicon
- PipeWire `pw-record`: https://docs.pipewire.org/page_man_pw-cat_1.html
- WirePlumber `wpctl`: https://pipewire.pages.freedesktop.org/wireplumber/tools/wpctl.html
- XDG ScreenCast portal: https://flatpak.github.io/xdg-desktop-portal/docs/doc-org.freedesktop.portal.ScreenCast.html
