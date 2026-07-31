# Avalonia desktop shell (issue #32, phase 2)

The shared desktop shell that will run the publisher on **both Windows and Linux** is being
built in Avalonia UI, staged behind the current WinUI app. This document covers the first
slice: the new shell project, its design system, and the reusable components — bound to the
same shared presentation projection the WinUI dashboard already uses.

Design spec: [`docs/superpowers/specs/2026-07-11-avalonia-desktop-shell-design.md`](superpowers/specs/2026-07-11-avalonia-desktop-shell-design.md).
Reference prototype: <https://delight-fusion-app.lovable.app>.

## What ships in this slice

- **`src/SonicRelay.Windows.Desktop`** — an Avalonia (`net10.0`) desktop app that references
  the existing shared layers (`Presentation`, `Core`, `ApiClient`, `Signaling`, `WebRtc`,
  `Audio`). Views and view models depend only on the `Presentation` abstractions — no WASAPI,
  WinUI, or Win32 knowledge — so the same shell drives the future Linux/PipeWire adapter.
- **Centralized design tokens** (`Styles/Tokens.axaml`) — palette, spacing, radii, typography
  and semantic status brushes, ported from the locked `Sonic.*` WinUI palette so the two UIs
  stay visually aligned. Dark theme is the default; a `Light` variant is defined (not the
  shipping theme this phase) so a future light theme needs no component changes.
  `Styles/Controls.axaml` exposes the recurring style classes (`card`, `card-title`,
  `metric-label`, `metric-value`, `mono`, …). Components never hard-code visual values.
- **The nine reusable components** from issue #32, each self-contained
  (`src/SonicRelay.Windows.Desktop/Controls`): `ConnectionStatusBadge`, `MetricProgressBar`,
  `AudioLevelMonitor`, `SessionCodeCard`, `BandwidthGauge`, `InfrastructureStatusCard`,
  `TechnicalConsole`, `SidebarNavigation`, `AccountStatusHeader`.
- **The dashboard shell** (`Views/MainWindow.axaml`) — sidebar rail, top bar with the global
  transmission status and account area, broadcast-session code, real-time audio monitor,
  signal-infrastructure and stream-quality cards, bandwidth gauge, and the technical console.
  The window is resizable with a minimum size and reflows its cards (they wrap, rather than
  shrink) on narrower widths; the sidebar and actions are keyboard-focusable.

## Real data, no permanent mocks

`DashboardShellViewModel.Update(snapshot, diagnostics, forceRelay)` is the single binding path
every component reads from. It runs the shared, unit-tested projection
(`DashboardViewModel.From` + `PublisherUiStateResolver.Resolve`) and republishes the results
as change-notifying properties — session code, viewer count, Session/Signaling/WebRTC-ICE
status, Direct/Relay mode, audio peak/dB, and RTT all come from real session state.

**Latency (RTT)**, **jitter** and **packet loss** come from the connected viewer's RTCP reports
about our audio stream, and **bitrate** from the negotiated Opus send bitrate — surfaced through
the same shared projection (see [`windows-publisher.md`](windows-publisher.md)). RTT is derived
by correlating each sender report we emit with the receiver report that echoes it
(`RtcpRoundTripEstimator`). Readings with no value yet still render as `—`, never fabricated,
until the first RTCP report correlates.

Command availability comes from the snapshot's own action guards
(`PublisherSnapshot.CanStartAudio`/`CanStopAudio`/`CanCreateSession`/`CanEndSession`, via the
pure `ShellCommandAvailability` helper) rather than the coarser, state-derived
`PublisherUiCapabilities` — so, for example, capture can be stopped while the session is still
`WaitingViewer`. Retry/logout stay capability-based (no snapshot equivalent).

## Sign-in and live runtime

`MainWindowViewModel.Attach(PublisherRuntime)` binds a live runtime: it subscribes to
`Workflow.StateChanged` and `IWebRtcPublisher.DiagnosticsChanged` and rebuilds the shell on the
UI thread. The window opens on the **sign-in surface** (`LoginView` + `AuthViewModel`) whenever
the snapshot is unauthenticated and switches to the dashboard once a session exists — the rule
is the pure `MainWindowViewModel.ShouldShowLogin`. `AuthViewModel` is a thin forwarder: it
collects credentials and dispatches to `PublisherWorkflow.LoginAsync`/`RegisterAsync`, while
validation and the friendly error messages stay with the workflow (surfaced back through the
snapshot).

On **Windows**, `App` composes a real runtime at startup (`PublisherRuntime.Create` with the
WASAPI `AudioCaptureService` and the configured backend) and restores any persisted session, so
a returning user lands on the dashboard. On **other platforms** (Linux today, and the headless
render tests) the WASAPI adapter cannot run, so the app opens on the representative **preview**
snapshot instead — a bootstrap placeholder, overwritten by real data the moment a runtime
attaches. The Linux capture adapter (PipeWire) is a later phase.

## Navigation and pages

The sidebar switches the main content between **Dashboard**, **Audio**, **Session**,
**Diagnostics** and **Settings**; `MainWindowViewModel.SelectedNavigation` (bound two-way to the
rail) drives `CurrentPage` and the top-bar title.

- **Audio** (`AudioPageViewModel`) picks the system output endpoint to capture, surfacing the
  platform `IAudioDeviceEnumerator` and persisting the choice to `AudioOutputPreferenceStore`.
- **Settings** (`SettingsViewModel`) edits the backend endpoint (read-only), the force-relay ICE
  preference (`RelayPreferenceStore`) and the Opus quality profile (`AudioQualityStore`) — the
  same user-scoped stores the WebRTC factory reads, so a change applies to the next stream.

Both are built from the runtime's stores on `Attach`; without an attached runtime they are
`IsConnected = false` and read-only (the standalone preview and the headless tests).

## Tray and background

`DesktopTrayController` wires the platform-agnostic, unit-tested `TrayApplicationController`
(in `Presentation`) to an Avalonia `TrayIcon` and the main window: a status tooltip and context
menu built from the publisher snapshot (Open / Start-Stop stream / Copy code / Reconnect
signaling / Quit), **minimise- and close-to-tray** while a session is active or the state keeps
running in the tray, and a tray **Reconnect signaling** action. All decisions come from the
controller; the Desktop type only performs the UI side effects (show/hide, clipboard, shutdown),
and the tray is created defensively so a missing tray backend never blocks launch.

## Scope boundaries

- WinUI stays the shipped UI until the Avalonia shell reaches minimum functional parity; this
  work runs **side by side** and does not replace it. Flipping the default to Avalonia (and
  retiring WinUI) is the remaining phase-2 step, once parity is validated on Windows.
- Linux/PipeWire capture and packaging are phases 3–5.

## Running and testing

The shell builds and runs cross-platform. Verified on this repo's `net10.0` toolchain:

```bash
dotnet build src/SonicRelay.Windows.Desktop/SonicRelay.Windows.Desktop.csproj
dotnet test  tests/SonicRelay.Windows.Desktop.Tests/SonicRelay.Windows.Desktop.Tests.csproj
```

`tests/SonicRelay.Windows.Desktop.Tests` holds UI-free projection tests
(`DashboardShellViewModelTests`) and headless Avalonia smoke tests (`ShellRenderTests`) that
rasterize the full shell. Set `SHELL_SHOT_DIR` when running the tests to write a
`shell-preview.png` of the rendered shell for visual review against the prototype.
