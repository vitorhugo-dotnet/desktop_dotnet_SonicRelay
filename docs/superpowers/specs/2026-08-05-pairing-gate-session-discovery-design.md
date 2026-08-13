# Pairing Gate, Session Discovery and Local Relay Settings Design

## Status and scope

This design collects six defects and gaps reported against the shipped
pairing/relay work (PRs #34 / #27 / #49). It spans the same three
repositories as the Phase 3 device-identity work:

- `dotnet_SonicRelay`: remove the shared relay-settings table, distinguish
  "not paired" from "bad code" on join, and add paired-session discovery;
- `windows_SonicRelay`: gate the shell on device identity, fix the technical
  console overlap, replace the dead account label, and make sign-out
  non-destructive to pairings;
- `flutter_SonicRelay`: make Settings reachable before pairing, align session
  code validation with the backend, and surface discovered sessions.

The repositories remain independently buildable and testable. Backend work
lands first (it is the contract oracle), then Windows and Flutter.

## Reported defects and their root causes

Each item below was traced to a specific line before this design was written.

**1. Flutter Settings is unreachable before pairing.**
`lib/app/router/app_router.dart:26` — `deviceIdentityRedirect` returns `/pair`
for every location when the readiness status is `pairingRequired`. The
Settings action added to the pairing page (`pairing_page.dart:39`) navigates
to `/settings` and is immediately redirected back. The user therefore cannot
correct a wrong backend URL without pairing first, and cannot pair because
the URL is wrong.

**2. Windows technical console overlaps the dashboard cards.**
`Views/MainWindow.axaml:70` — `TechnicalConsole` is `DockPanel.Dock="Bottom"`
with no height constraint. Its `ItemsControl` grows with the activity log
(`PublisherWorkflow.SetState` retains 100 entries), so the control's desired
height exceeds the space the `DockPanel` can give it and it renders over the
cards above. The inner `ScrollViewer` never scrolls because nothing bounds it.

**3. Windows shell is ungated and the top bar always reads "Not signed in".**
Two independent causes:

- `ViewModels/DashboardShellViewModel.cs:120` —
  `AccountLabel => accountEmail ?? "Not signed in"`, fed by
  `AccountEmail = snapshot?.UserEmail`. Identity was removed from the backend
  in Phase 3, so `UserEmail` is permanently `null`. The label is a leftover
  from human accounts and can never render anything else.
- Nothing gates the shell. `PairingView` became an ordinary nav page in the
  previous change (correctly, because the old full-shell gate also hid
  Settings), but no gate replaced it, so Dashboard/Audio/Session/Diagnostics
  are reachable before the device has an identity.

**4. Flutter cannot join a desktop session by code.**
The dominant cause is destructive sign-out. `PublisherWorkflow.LogoutAsync`
calls `deviceIdentity.ResetAsync`, which deletes the stored credential
(`DeviceIdentitySession.ResetAsync`); the subsequent re-bootstrap registers a
**new** device and therefore a new `DeviceId`. Every existing
`DevicePairing` still points at the previous publisher device and is dead.
`SessionEndpoints.JoinAsync` requires `HasActivePairingAsync(session.SourceDeviceId, viewer.Id)`
and returns the same generic `404 "Invalid or expired session code."` when
that check fails, so the viewer reports a bad code for what is really a
missing pairing. The reported log shows five consecutive
`Signed out` / `Publisher device identity is ready` cycles.

Two secondary contributors compound it:

- The backend requires exactly six alphanumeric characters
  (`JoinAsync`), while Flutter validates `^[A-Z0-9-]{4,12}$`
  (`join_session_view_model.dart:35`) and the field hint is `SR-4F8K`
  (`session_code_input.dart:27`) — a format the server rejects outright.
- `updateCode` upper-cases and trims but does not strip separators, so a
  pasted or hyphenated code reaches the server unchanged.

**5. Relay/coturn settings are global to the whole backend.**
`SonicRelay.Domain.RelaySettings.RelaySettings` is a singleton row
(`SingletonId = ...0001`) read by `TurnCredentialService.BuildAsync` for every
caller. Any device editing the coturn URL changes it for every other device
the backend serves.

**6. Flutter cannot discover an open session.**
`GET /api/sessions/active` filters to sessions where the caller is already the
source or a participant, so a paired viewer sees nothing until after it has
joined. There is no endpoint that answers "which of my paired publishers is
broadcasting right now".

## Where the TURN configuration actually comes from

Established while investigating item 5, and recorded here because it
determines the chosen approach.

No real TURN host has ever been committed to any of the three repositories;
every occurrence in git history is a test or documentation placeholder
(`relay.example.com`, `turn.seudominio.com`, and similar). The GitHub Actions
workflow (`.github/workflows/vps-ci-cd.yml`) does not set any `TURN_*`
variable — it builds the image, copies `docker-compose.prod.yml`,
`deploy.sh` and `efbundle` over SCP, and runs `deploy.sh` over SSH. Its only
secrets are `VPS_HOST`, `VPS_PORT`, `VPS_USER`, `VPS_SSH_KEY`, `VPS_APP_DIR`
and `GITHUB_TOKEN`.

`deploy.sh:14-18` requires a pre-existing `.env` in the app directory
(`/docker/sonicRelay` by default) and `docker-compose.prod.yml` loads it via
`env_file`. The relay host therefore lives only in that hand-created file on
the VPS, as `TURN_PUBLIC_HOST`. `services/SonicRelay.Api/Program.cs:46-63`
derives the ICE configuration from it:

- `turn:<TURN_PUBLIC_HOST>:3478?transport=udp`
- `turn:<TURN_PUBLIC_HOST>:3478?transport=tcp`
- `stun:<TURN_PUBLIC_HOST>:3478`

A `turns:5349` entry is deliberately not derived; it requires a TLS
certificate mounted into coturn and stays opt-in through an explicit
`TURN_URIS`. This env-derived path is what has been serving relay in
production, and it keeps working unchanged under this design.

## Approved decisions

Confirmed with the product owner before this spec was written.

- **Relay settings return to how they worked before the settings API**, with
  no database table involved at all. The server-derived URL remains the
  default; the user may override it locally; the server's value is never
  displayed in the UI.
- **"Signed in" on Windows means the device has an identity** — a bootstrapped
  device credential. Active pairings are not part of the gate.
- **A discovered session is joined directly**, without typing a code. The code
  path remains for manual entry.
- The relay mode stays a three-way choice (`automatic`, `forceRelay`,
  `disableFallback`); only its storage location changes.

## Design

### Backend: remove the relay settings table

`GET/PUT /api/settings/relay` and everything behind it are removed:
`SettingsEndpoints`, the `RelaySettings` entity and its `DbSet`, the
`UpdateRelaySettingsRequest`/`RelaySettingsResponse` contracts, and the
`RelaySettingsPersistenceTests` fixture. A new EF Core migration drops the
`RelaySettings` table.

`TurnCredentialService.BuildAsync` loses its `AppDbContext` dependency and its
override lookup, returning to a pure projection of `TurnOptions`. The
`disableFallback` branch is removed from the service: with the mode now a
per-device preference, suppressing TURN is the client's job, and a server that
withheld TURN entries would impose one device's preference on every other.

The dropped table is safe to discard. Production has been served by
`TURN_PUBLIC_HOST` throughout; if a row exists it holds the same host the env
already derives.

### Backend: distinguish "not paired" from "bad code"

`JoinAsync` currently funnels four distinct failures into one `404`. It is
split so the client can explain what happened:

- unknown/expired/consumed code, or an ended or expired session → unchanged
  `404 { error, code: "invalid_code" }`;
- code valid and session live, but no active pairing between the caller and
  `session.SourceDeviceId` → `403 { error, code: "not_paired" }`.

The pairing check keeps its current position after code redemption, so an
unpaired caller still cannot probe for valid codes: it must already hold a
live code to reach the `403`, and the code store's `RedeemAsync` is
non-destructive, so no legitimate join is consumed by a failed one.

The viewer-limit conflict (`409`) is unchanged.

### Backend: paired-session discovery

`GET /api/sessions/discoverable`, authorized by `session:join`, returns
sessions that are `Waiting` or `Active` whose `SourceDeviceId` is a publisher
holding an `Active` `DevicePairing` with the caller. Each entry carries
`sessionId`, `publisherDeviceId`, `publisherDeviceName`, `status`,
`viewerCount`, `maxViewers` and `createdAt`. No join code is ever returned —
the code is a separate, short-lived secret and discovery must not become a way
to read it.

`POST /api/sessions/{sessionId:guid}/join`, also `session:join`, joins a
discovered session by identifier. It applies exactly the checks the code path
applies after redemption — session live, active pairing with the publisher,
viewer limit — and shares its participant-creation and reconnect logic with
`JoinAsync` so the two paths cannot drift. The pairing is the authorization;
the code is not required because it only ever proved that the viewer could
see the publisher's screen, which pairing already establishes more strongly.

Both endpoints reuse the existing `join-session` rate-limit policy.

### Windows: gate the shell on device identity

`MainWindowViewModel` exposes `HasDeviceIdentity`, projected from
`PublisherSnapshot.HasDeviceIdentity` (already `IsAuthenticated && DeviceId.HasValue`).
While it is false:

- the Pairing page is selected and shown;
- Dashboard, Audio, Session and Diagnostics are disabled in the sidebar —
  `NavigationItem.IsEnabled` is already bound in
  `SidebarNavigation.axaml:35`, so this needs no new binding machinery;
- Settings stays enabled, so a wrong backend URL is still correctable. This is
  the constraint that made the previous full-shell gate wrong, and it is
  preserved deliberately.

When it becomes true the shell unlocks and the selection moves to Dashboard.
Pairing remains reachable afterwards as an ordinary nav page.

### Windows: replace the account label

`DashboardShellViewModel.AccountEmail` and `AccountInitials` are replaced by a
device-identity projection: the label renders the device name (falling back to
"No device identity" while none exists) and the avatar shows initials derived
from it. The secondary line already binds `UiStateText` and is left alone — it
needs no pairing lookup, consistent with pairings being outside the gate.
Nothing reads `PublisherSnapshot.UserEmail` or
`UserDisplayName` any more; both fields are removed from the snapshot, along
with the assignments in `LogoutAsync` and `ExecuteAsync`'s unauthorized
branch.

### Windows: bound the technical console

The console's card gets an explicit `MaxHeight` so the `DockPanel` can never
hand it more room than the dashboard has to spare, which lets the existing
inner `ScrollViewer` do its job. The auto-scroll behaviour in
`TechnicalConsole.axaml.cs` is unaffected.

### Windows: non-destructive sign-out

The top-bar `Sign out` button becomes `Unpair this device` and requires
confirmation before acting. On confirmation it revokes this device's active
pairings through `DELETE /api/pairings/{id}` and only then clears the local
identity, so no orphaned pairing rows are left behind and the user is told, up
front, that paired phones will need to pair again.

If revocation fails the local identity is still cleared — the button's reason
for existing is recovering from a rejected credential, and a backend that
cannot be reached must not block that recovery. The failure is surfaced in the
activity log rather than silently swallowed.

### Flutter: reach Settings before pairing

`deviceIdentityRedirect` gains `/settings` as an allowed location in the
`pairingRequired` branch. The `deviceSetup` and `restoring` branches are
unchanged: before a device credential exists there is no authenticated client
to render meaningful settings with, and the existing `_DeviceSetupPage` retry
path already covers that state.

### Flutter: align session code entry

`JoinSessionViewModel._validCode` becomes `^[A-Z0-9]{6}$`, matching
`JoinAsync`. `updateCode` strips whitespace and hyphens before upper-casing,
so a code pasted as `SR-4F8K` or `FE23 7F` normalises rather than failing.
`SessionCodeInput` gets a six-character limit and its hint changes to a
realistic code. The repository maps the new `403 not_paired` to a distinct
`SessionsFailureKind.notPaired` with a message that tells the user to pair
with the publisher again, instead of blaming the code.

### Flutter: discovered sessions

A `discoverableSessionsProvider` polls `GET /api/sessions/discoverable` while
the join page is mounted and renders the results directly below the code
field: publisher device name, status, and viewer occupancy. Tapping an entry
calls `POST /api/sessions/{id}/join` and follows the same navigation the code
path uses.

The list is absent when empty rather than showing an empty-state card — an
idle publisher is the normal case, and a permanent "no sessions" panel would
add noise to the primary manual-entry flow. Discovery failures are silent for
the same reason: the code field remains fully functional and is the fallback.

### Both clients: local relay preferences

The relay mode reverts to the local stores that still exist for it —
`RelayModeStorage` (Flutter) and `RelayPreferenceStore` (Windows), including
their migrations from the legacy boolean flag. `RelaySettingsApi`,
`relaySettingsApiProvider` and `RelaySettingsApiClient` are deleted, as are the
server-sync call sites in `SettingsPage._ConnectionSection`,
`MainWindowViewModel.SelectedNavigation` and the pre-session refresh.

The coturn field becomes a local override, empty by default. Empty means "use
what the backend sends". When set, the client rewrites the `urls` of the TURN
entry returned by `/api/webrtc/ice-servers`, preserving the server-issued
`username` and `credential`. The value that came from the server is never
written into the field, so the deployment's relay host is not disclosed
through the UI.

`disableFallback` becomes a client-side filter: the client discards TURN
entries before handing the list to the peer connection. `forceRelay` continues
to work as it does today, through the ICE transport policy.

The copy stating that these settings apply to every paired device is removed
from both clients; they now apply to the device you are holding.

**Known limitation, accepted.** The TURN credential is
`Base64(HMAC-SHA1(TURN_STATIC_AUTH_SECRET, "<expiry>:<deviceId>"))`, signed by
the backend. Because the override reuses that credential, it authenticates
only against a coturn sharing the same static secret. This covers the intended
use — redirecting to another host or port of the same relay deployment — but
not pointing at a third-party TURN server, which would additionally require
user-supplied username and credential fields. That is out of scope here.

## Testing

Backend, using the existing `SonicRelayApiFactory` and
`DeviceIdentityTestHelper`:

- joining with a live code and no pairing returns `403 not_paired`, while an
  unknown code still returns `404 invalid_code`;
- a failed join does not consume the code — a paired viewer can still join
  with the same code afterwards;
- discovery lists only `Waiting`/`Active` sessions of actively paired
  publishers, excludes revoked pairings and ended sessions, and never
  includes a join code in the payload;
- join-by-id enforces pairing and the viewer limit, and is idempotent for a
  viewer that is already a participant;
- ice-servers returns the env-derived TURN/STUN with no database involvement;
- a migration round-trip leaves no `RelaySettings` table.

Windows, extending `MainWindowViewModelStateTests` (already routed off the
real preferences file):

- the shell is gated and only Pairing plus Settings are enabled without a
  device identity, and unlocks when one arrives;
- the top bar renders device identity and never the string `Not signed in`.
  `DashboardShellViewModelTests` currently asserts the email label and its
  `VH` initials (lines 19, 70-71); those assertions move to the device-name
  projection rather than being deleted;
- unpair revokes pairings before clearing the identity, and still clears it
  when revocation fails;
- the coturn override rewrites TURN urls while preserving credentials, and an
  empty override passes the backend list through untouched;
- `disableFallback` drops TURN entries client-side.

Flutter:

- the router allows `/settings` while pairing is required, and still redirects
  other locations;
- code validation accepts exactly six alphanumerics and normalises separators;
- `403 not_paired` surfaces a pairing message, not an invalid-code message;
- the discovery list renders paired sessions, is hidden when empty, stays
  hidden on fetch failure, and joining by tap navigates like the code path.

Manual cross-repository verification: pair a phone with the desktop, confirm
the top bar shows the device and the shell unlocks, create a session, confirm
it appears in the phone's discovery list, join by tap and by code, then
unpair and confirm both paths refuse with a pairing message rather than an
invalid-code message.

## Out of scope

- Per-publisher or per-pairing relay settings. Explicitly rejected in favour
  of the local override.
- User-supplied TURN username and credential.
- Any change to the pairing challenge or QR flows.
- Any change to the CI/CD pipeline or the VPS `.env`.
