# Non-admin release smoke test

Run this checklist before approving every Windows Publisher release. All mandatory steps must pass from a clean, standard user account. A failure cannot be waived by running as administrator or by changing the machine globally.

## Test record

- Release/tag:
- ZIP filename:
- Commit from `BUILD-INFO.txt`:
- Windows edition and version:
- Account type (must be standard user):
- Managed/corporate device policy, if applicable:
- Tester and date:

## Preconditions

- [ ] Sign in with a standard user account that cannot approve elevation with its own credentials.
- [ ] Confirm SonicRelay is not installed, no SonicRelay Windows service exists, and no previous `%LOCALAPPDATA%\SonicRelay\WindowsPublisher` folder remains.
- [ ] Use a normal user-writable folder such as `%USERPROFILE%\Downloads` or `%USERPROFILE%\Desktop`; do not use Program Files.
- [ ] Keep Task Manager available to inspect processes. Do not pre-create firewall rules, install drivers, or install a machine-wide runtime.

## Download and extract the portable ZIP

- [ ] Open GitHub Releases and download `SonicRelay.WindowsPublisher-win-x64-<version>.zip` without elevation.
- [ ] Confirm the ZIP name matches the release version and `BUILD-INFO.txt` identifies the expected version, commit, and `win-x64` runtime.
- [ ] Extract the ZIP into the selected user-writable folder using Windows Explorer.
- [ ] Confirm extraction neither shows an administrator prompt nor requires writing to Program Files.
- [ ] Confirm there is no installer to run and extraction does not install a Windows service or drivers.

## Installers: setup EXE and MSI (per-user, no elevation)

The release ships two installers in addition to the portable ZIP. Both install the same
app into `%LOCALAPPDATA%\SonicRelay\WindowsPublisher` for the current user only. The
release **must not** contain a raw `SonicRelay.Windows.App.exe` as the `.exe` asset — the
`.exe` is a WiX Burn setup wrapper around the MSI.

- [ ] Confirm the release `.exe` is named `SonicRelay.WindowsPublisher-win-x64-<version>.exe`
      and is a **setup** program (double-clicking opens setup), not the app itself.
- [ ] **Setup EXE:** double-click the `.exe` from a standard user account. Confirm setup
      runs and installs **without any UAC/elevation prompt**.
- [ ] **MSI:** double-click the `.msi` from a standard user account. Confirm it installs
      **without any UAC/elevation prompt**.
- [ ] Confirm the installed files are under `%LOCALAPPDATA%\SonicRelay\WindowsPublisher`
      and nothing is written to `Program Files` or `HKLM`.
- [ ] Confirm no Windows service, driver, or firewall rule was created by either installer.
- [ ] Confirm a **Start Menu** shortcut "SonicRelay Windows Publisher" exists and launches
      the installed app.
- [ ] **Uninstall** via Settings → Apps (or Programs and Features) as a standard user and
      confirm it removes the install folder and the Start Menu shortcut without elevation.
- [ ] **SmartScreen (expected, unsigned):** the EXE/MSI are currently unsigned, so Windows
      SmartScreen may warn ("Windows protected your PC"). Confirm the user can proceed via
      *More info → Run anyway*. This is expected until code signing is added.

## Start and configure

Runnable from the portable ZIP extraction **or** the installed location.



- [ ] Run `SonicRelay.Windows.App.exe` directly from the extracted folder without **Run as administrator**.
- [ ] Confirm startup does not show an administrator prompt, request firewall rules, install a service, install drivers, or modify a protected machine location.
- [ ] Confirm the main window opens and remains responsive.
- [ ] Open Settings and confirm the current configuration is displayed.
- [ ] With the app closed, configure `backendBaseUrl` in `appsettings.json` with a valid absolute HTTP(S) test address, reopen the app, and confirm the value persists.
- [ ] Confirm `%LOCALAPPDATA%\SonicRelay\WindowsPublisher\appsettings.json` is created or updated and no runtime data is written beside the executable or under Program Files.

## Device identity, pairing, and local state

- [ ] Confirm startup requires no account login and bootstraps a `windows_publisher/windows` identity against the configured backend.
- [ ] Confirm `%LOCALAPPDATA%\SonicRelay\WindowsPublisher\device-credential.dat` is created for the current user, is protected with DPAPI CurrentUser, and is not readable as plaintext.
- [ ] Confirm DeviceBearer access tokens remain in memory: no access-token or refresh-token file is created.
- [ ] Open Connection and create a pairing challenge. Confirm the pairing challenge ID and pairing code are both visible and can be copied independently.
- [ ] Confirm the QR code scans to the backend-provided pairing payload, and complete pairing either by scanning it or by entering the same challenge ID plus pairing code manually in a viewer.
- [ ] Create a stream session and confirm its separately labeled session join code is not replaced when the pairing challenge is refreshed or expires.
- [ ] Close the app, delete only `device-credential.dat`, and reopen it to reset device identity. Confirm a new publisher identity is bootstrapped and previous pairings must be created again.
- [ ] Confirm the reset and configuration files remain only under `%LOCALAPPDATA%\SonicRelay\WindowsPublisher` and the app can start again.

## Failure handling

- [ ] Set the backend URL to an unused or unreachable address and start the app. Confirm the missing backend produces a clear, non-fatal device-bootstrap error without elevation, repeated modal prompts, or a crash.
- [ ] Disable or disconnect available audio endpoints, then exercise the audio-device/publish path. Confirm a missing audio device produces a clear, non-fatal unavailable-device state without requesting a driver or elevation.
- [ ] Restore the test environment and confirm the app can be closed normally with no SonicRelay process or service left running.

## Release gate

The release is blocked if any mandatory checkbox fails, if any administrator prompt appears, or if the app requires Program Files, a Windows service, drivers, firewall rules, protected registry/filesystem writes, or machine-wide dependencies. Missing backend and missing audio device scenarios pass only when the app reports the condition gracefully and remains usable.

For every failure, capture the failed step, artifact version, Windows/account details, exact visible message, and relevant user-scoped diagnostic output. Do not include credentials or tokens. Fix the problem and rerun the entire checklist from the clean standard-user preconditions before releasing.

## Cleanup

- [ ] Close the app and delete the extracted folder without elevation.
- [ ] Delete `%LOCALAPPDATA%\SonicRelay\WindowsPublisher` and confirm cleanup needs no administrator prompt.
- [ ] Record the final result as **PASS** only when every mandatory item above passes; otherwise record **BLOCKED** with the failure evidence.
