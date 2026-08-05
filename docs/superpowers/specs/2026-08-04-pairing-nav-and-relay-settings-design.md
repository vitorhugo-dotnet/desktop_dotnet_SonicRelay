# Always-On Pairing Nav, Configurable Backend/Coturn URLs, and Synced Relay Settings

## Goal

Four related problems reported after shipping the Sign Out button (`claude/logoff-button-flutter-pairing-4n4iww`, PR #48):

1. Clicking Sign Out shows the pairing surface for a moment, then flips back to the
   Dashboard as if nothing happened — the user never gets a chance to see or use a fresh
   pairing code.
2. The Windows Publisher has no way to change its backend API URL from the UI (a
   `SaveBackendAsync` method already exists in `UserConfigurationLoader` but nothing calls
   it), and the entire shell — including Settings — is hidden whenever the device isn't
   bootstrapped, so a bad URL leaves no way back in without editing `appsettings.json` by
   hand.
3. The Flutter viewer already has a working "Server URL" field in Settings, but Settings is
   only reachable from the listener/join screens — the very first screen a fresh or reset
   device sees (`pairing_page.dart`) has no way to reach it, so a bad saved URL traps the
   user exactly like the Windows case.
4. There is no way to disable P2P→relay fallback (only "force relay" exists), and no way to
   change the coturn (TURN) server the backend hands out — both are static per-device or
   static `appsettings.json` config today, with no cross-device sync.

This work spans three repositories (`windows_SonicRelay`, `flutter_SonicRelay`,
`dotnet_SonicRelay`) and lands in a new branch/PR, separate from #48. Every URL introduced
or made editable here still points only at the user's own self-hosted SonicRelay
deployment — no third-party/public server is ever a default or a fallback.

## Root cause of problem 1

`PublisherSnapshot.IsAuthenticated` means "this device successfully bootstrapped its own
device-identity credential" — it has nothing to do with a viewer having paired. Bootstrap
(`/api/devices/bootstrap`) is unconditional: given no stored credential, it always succeeds
immediately if the backend is reachable, no human action required.
`MainWindowViewModel.ShowPairing` is `!IsAuthenticated`, and it gates the *entire* shell
(`MainWindow.axaml` wraps the sidebar + all pages in
`IsVisible="{Binding !ShowPairing}"`). `PairingViewModel` (the thing that actually renders a
QR/code) is only constructed *after* bootstrap succeeds — in the same state transition that
flips `IsAuthenticated` to `true`. So the pairing surface is hidden at exactly the moment it
would have real content to show. `LogoutAsync` calling `runtime.InitializeDeviceIdentityAsync()`
right after sign-out makes this visible by collapsing the whole round trip into under a
second, but the race was already there — it is not a regression introduced by that call, it's
architecture that removing the call would not fix.

`PairingView` (the XAML control) already renders correctly with no device identity —
"Publisher device identity unavailable", disabled buttons, dashes — so it does not need the
outer full-shell gate to degrade gracefully. It was designed to be usable continuously (create
codes, list/revoke paired viewers), not as a one-time login screen.

## Architecture — Windows Publisher shell

- Remove the full-shell `IsVisible="{Binding !ShowPairing}"` wrapper in `MainWindow.axaml`.
  The sidebar, top bar, and all pages render unconditionally once a runtime is attached.
- Add a `Pairing` entry to `MainWindowViewModel.Navigation` (new `PageKey.Pairing`), and swap
  the standalone `<controls:PairingView>` for a normal page inside the existing `Panel` of
  pages (same pattern as `AudioView`/`SettingsView`), bound to `Pairing` exactly as today.
- Drop `ShowPairing`/`ShouldShowPairing` entirely — nothing keys off `IsAuthenticated` for
  visibility anymore. `HasDeviceIdentity`-gated actions (`CanCreateSession` etc.) already
  disable themselves correctly through `ShellCommandAvailability`/`PublisherUiCapabilities`;
  no change needed there.
- Dashboard gains a small inline banner (a bindable string on `DashboardShellViewModel`,
  same pattern as `MainWindowViewModel.DiagnosticsActionMessage`) shown while
  `!IsAuthenticated`: "Bootstrapping this device's identity…" — replaces the old full-screen
  block with a page-local, non-blocking notice.
- `MainWindowViewModel.LogoutAsync` keeps calling `workflow.LogoutAsync()` then
  `runtime.InitializeDeviceIdentityAsync()` (still wrapped in try/catch — a backend hiccup
  during re-bootstrap must not throw into the command). It additionally sets
  `SelectedNavigation` to the new Pairing entry *once*, right after sign-out, so the user
  lands on the pairing page and actually sees the fresh QR/code. Because Pairing is now a
  normal nav page, nothing forces the user back to Dashboard afterward — they navigate freely,
  same as any other page.
- Default landing page on a cold start stays Dashboard (`Navigation[0]`), unchanged.

This is a UI/navigation change only; `PublisherWorkflow`, `PublisherRuntime`,
`DeviceIdentitySession`, and `PublisherUiStateResolver` are untouched.

## Windows Publisher: configurable backend URL

- `SettingsViewModel.BackendUrl` becomes a two-way bindable, editable string with a "Save"
  action. Saving:
  1. Validates the value parses as an absolute `http`/`https` URL (reuse the same rule as
     `UserConfigurationLoader.ParseUri`).
  2. Calls `UserConfigurationLoader.SaveBackendAsync(uri)` (already implemented, currently
     unused).
  3. Tears down the current runtime and calls `DesktopRuntimeFactory.Create(uri)` again,
     then `MainWindowViewModel.Attach(newRuntime)` followed by
     `runtime.InitializeDeviceIdentityAsync()` — the same attach/bootstrap sequence
     `App.axaml.cs` already runs at startup, just re-entered on demand. No process restart
     needed.
  4. Surfaces failures (invalid URL, unreachable backend) as a `SettingsViewModel` error
     string next to the field; the previous runtime/URL keeps running until a save actually
     succeeds, so a bad edit can't strand the user with *nothing* attached.
- Because the shell is always visible now (previous section), Settings is reachable even
  while the device has never bootstrapped — this is what actually fixes the "stuck with a
  bad URL and no way back" case, not just an editable field on its own.

## Flutter viewer: reachable backend URL

The "Server URL" field (`ServerUrlField`, in `SettingsPage`, backed by `ServerConfigStorage`
and `serverUrlProvider`) is already fully implemented — save, validate, restore default. It
was never removed. The gap: `pairing_page.dart` (the first screen for an unpaired/reset
device) has no route to `/settings`. Fix: add a settings `IconButton` to its `AppBar`,
matching the one already on `listener_page.dart`/`join_session_page.dart`
(`onPressed: () => context.push('/settings')`). No other change needed on this item.

## Backend: relay & coturn settings (shared by problems 4)

`dotnet_SonicRelay` has no account/owner concept — every device is implicitly the same
person's own device. So this is one global, singleton setting, not per-user:

- New table `RelaySettings` (EF Core migration), single row, columns:
  - `RelayMode` (`Automatic` | `ForceRelay` | `DisableFallback`) — replaces today's boolean
    `ForceRelay` preference everywhere. The three states are mutually exclusive by
    construction, so there's no "force relay AND disable fallback" contradiction to handle.
  - `TurnUris` (`string[]`, nullable/empty = "use `appsettings.json` default").
  - `TurnStaticAuthSecretHash`-equivalent: stored the same way device credential secrets are
    (never returned in plaintext by `GET`), nullable = "use `appsettings.json` default".
    STUN URIs stay fixed in `appsettings.json` — out of scope, the user only asked to change
    the TURN/coturn endpoint.
- New endpoint group `/api/settings/relay`, `RequireAuthorization("device:manage")` — the
  same policy already used by `rotate-credential`/`revoke`, granted to every bootstrapped
  device today (there is no stricter admin tier in this API). This is the existing trust
  boundary for the whole device API, not a new one introduced here: anything that can
  bootstrap a device can already rotate/revoke credentials, and will now also be able to
  read/write this shared setting.
  - `GET /api/settings/relay` → `{ relayMode, turnUris, hasCustomTurnSecret }` (never the
    secret itself).
  - `PUT /api/settings/relay` → partial update; a field omitted/null leaves the current
    stored value unchanged (so a client can flip `relayMode` without resending TURN config).
- `TurnCredentialService.Build` reads the singleton row first; any null/empty field falls
  back to the existing `TurnOptions` from `appsettings.json` field-by-field (not
  all-or-nothing), preserving today's behavior when nothing has been overridden.
- The existing `/api/webrtc/ice-servers` endpoint (`WebRtcEndpoints`) changes its TURN entry
  based on the effective `RelayMode`: `DisableFallback` omits TURN entries from the response
  entirely (so a client physically cannot fall back, regardless of its own ICE policy);
  `ForceRelay`/`Automatic` include TURN as today.

## Windows Publisher & Flutter: consuming the synced settings

- Both apps replace their local-only "Force relay" boolean with the same three-way
  `RelayMode` used server-side (`RelayPreferenceStore` on Windows, `RelayModeStorage` on
  Flutter). The local store becomes a **last-known-good cache**, not the source of truth:
  read at startup so the app has something to render before the first fetch completes, then
  overwritten whenever a fresh value is fetched. Reading an old boolean-shaped file (from
  before this change) maps `true` → `RelayMode.ForceRelay` and `false`/missing →
  `RelayMode.Automatic`; the file is rewritten in the new shape on the next save.
- Sync is polling, not push — no new WebSocket/pub-sub channel. Each app fetches
  `GET /api/settings/relay` (a) whenever its Settings page is opened, (b) right before
  creating a session/joining one, and (c) every 30s while a session is active. This is
  simple, and a few seconds of propagation delay between two personal devices is an accepted
  trade-off, not a defect — real-time push over the existing signaling socket is a viable
  future upgrade if that ever changes, but is out of scope here.
- Changing `RelayMode` from either app's Settings page calls `PUT /api/settings/relay`
  directly (no local-only apply) so both apps always converge on the same server value
  rather than racing two independent local edits.
- A new "Coturn URL" text field sits next to the relay mode control in both Settings pages,
  gated behind "device has a valid identity / viewer is paired" (i.e. visible once
  authenticated, matching what the user asked for — "after logging in on both Flutter and
  Desktop"). Saving calls the same `PUT /api/settings/relay` with `turnUris: [url]`. A single
  URL in the UI maps to a one-element array in the API — the API supports more, the UI
  doesn't need to.
- `SipSorceryPeerConnectionFactory` (Windows) and the Flutter peer-connection factory both
  already read an ICE server list from the backend
  (`BackendIceServersProvider`/`IceServersRepository`); no client-side change is needed for
  `DisableFallback` beyond consuming whatever list the backend now returns (it simply won't
  contain a TURN entry). `ForceRelay` continues to set `iceTransportPolicy: 'relay'`
  client-side exactly as today, driven off the now-synced `RelayMode` instead of the old local
  boolean.

## Error handling

- Every new network call (Settings save on Windows/Flutter, relay-settings poll) follows the
  existing pattern in this codebase: failures surface as a visible but non-fatal message
  (`ErrorMessage`/snackbar), and the previous good value keeps being used. Nothing here ever
  blocks audio/session flows if `/api/settings/relay` is briefly unreachable — polling failures
  are silently retried on the next scheduled fetch.
- Windows backend-URL save failure keeps the old runtime attached and running; it does not
  tear down before confirming the new one works.

## Testing

- Windows: unit tests for `MainWindowViewModel` navigation (Sign Out selects the Pairing tab;
  Pairing page renders with a null `Pairing` viewmodel without needing `ShowPairing`), for the
  new `SettingsViewModel` backend-URL save/validate path, and for `RelayPreferenceStore`'s
  three-way mode + last-known-good caching.
- Flutter: widget test that `pairing_page.dart` exposes a working route to `/settings`.
- Backend: integration tests for `/api/settings/relay` GET/PUT (partial updates, secret never
  echoed back) and for `TurnCredentialService`/`ice-servers` behavior across all three
  `RelayMode` values, plus a migration test for the new table.

## Sequencing

This lands as three separate PRs, one per repository, on a new branch (not #48). Order
matters because the clients depend on the new endpoint:

1. `dotnet_SonicRelay` — `RelaySettings` table/migration, `/api/settings/relay`,
   `TurnCredentialService`/`ice-servers` changes. Deployed first.
2. `windows_SonicRelay` — shell/navigation fix (problem 1), editable backend URL (problem 2),
   and the `RelayMode`/coturn Settings UI (problem 4), against the now-live backend endpoint.
3. `flutter_SonicRelay` — settings route fix (problem 3) and the same `RelayMode`/coturn
   Settings UI addition.

## Out of scope

- Any account/user/multi-tenant concept — this stays a single-owner deployment.
- Real-time push of settings changes (polling is sufficient for now).
- Making STUN URIs configurable.
- Any change to the pairing challenge/QR mechanism itself, or to `DeviceIdentitySession`,
  `PublisherWorkflow.LogoutAsync`, or `DeviceIdentitySession.ResetAsync` (all already correct
  from PR #48).
