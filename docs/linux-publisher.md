# Linux Publisher

The SonicRelay publisher ships on Linux from the same Avalonia shell as Windows (issue #32). This document covers installation, dependencies, supported distributions, and known limitations for the Linux release assets built by `.github/workflows/release.yml` (issue #40, PR 3 of the [Linux desktop publisher design](superpowers/specs/2026-07-14-linux-desktop-publisher-design.md)).

## Release assets

Every tagged release publishes three Linux `linux-x64` assets alongside the existing Windows assets, all built from the same tag/commit:

- `SonicRelay-LinuxPublisher-linux-x64-<version>.tar.gz` — portable, self-contained folder archive. Extract and run; installs nothing.
- `SonicRelay-LinuxPublisher-linux-x64-<version>.deb` — installable package for Ubuntu/Debian.
- `SonicRelay-LinuxPublisher-linux-x64-<version>.rpm` — installable package for Fedora.
- `checksums-sha256.txt` — SHA-256 checksums for every release asset (Windows and Linux).

Each package embeds a `BUILD-INFO.txt` (version, commit, runtime, build timestamp) next to the application binary for support/diagnostics.

## Supported systems

| Tier | Systems |
| --- | --- |
| Officially supported | Ubuntu 24.04 LTS Desktop (GNOME, Wayland or Xorg session) |
| Best effort | Ubuntu 26.04 LTS, Debian 13 and compatible Debian-based systems, Fedora Workstation (current release), KDE Plasma with compatible PipeWire/tray services, other x64 distributions via the portable archive |
| Out of scope | `linux-arm64`; Flatpak, Snap, or AppImage packages; macOS; PulseAudio as the primary capture path; Wine; every desktop environment/tray protocol; Linux autostart |

Fedora's `.rpm` is a best-effort convenience package: it uses the same publish output and dependency set validated on Ubuntu 24.04, but has not gone through the same manual desktop validation pass as Ubuntu 24.04 (see [Known limitations](#known-limitations)).

## Installing the `.deb` (Ubuntu/Debian)

```bash
sudo apt install ./SonicRelay-LinuxPublisher-linux-x64-<version>.deb
```

This installs:

- the application under `/usr/lib/sonicrelay/`;
- an exec wrapper at `/usr/bin/sonicrelay`;
- a desktop entry at `/usr/share/applications/sonicrelay.desktop` (categories Audio/Network/Utility, `Terminal=false`);
- the app icon under `/usr/share/icons/hicolor/`.

Launch it from your application menu ("SonicRelay Publisher") or by running `sonicrelay` from a terminal.

**Upgrade:** `sudo apt install ./SonicRelay-LinuxPublisher-linux-x64-<new-version>.deb` over an existing install.

**Uninstall:** `sudo apt remove sonicrelay`.

Installing, upgrading, or removing the package requires administrator authorization (standard for any system package manager), but **running** SonicRelay afterwards never does — it runs entirely as the desktop user.

## Installing the `.rpm` (Fedora)

```bash
sudo dnf install ./SonicRelay-LinuxPublisher-linux-x64-<version>.rpm
```

Same layout, upgrade (`sudo dnf upgrade ./SonicRelay-LinuxPublisher-linux-x64-<new-version>.rpm`), and removal (`sudo dnf remove sonicrelay`) model as the `.deb`.

## Using the portable `.tar.gz`

```bash
tar -xzf SonicRelay-LinuxPublisher-linux-x64-<version>.tar.gz -C ~/Applications/sonicrelay
~/Applications/sonicrelay/SonicRelay.Windows.Desktop
```

No installation step, no admin privileges, and no desktop-menu entry — extract it anywhere user-writable and run the binary directly. This is the right choice for distributions outside the supported/best-effort list above, or for running multiple versions side by side.

## Runtime dependencies

The publish output is self-contained (it bundles the .NET runtime), but it still depends on system libraries and the PipeWire audio stack, all pulled in automatically by the `.deb`/`.rpm` package managers:

- **Audio capture:** `pw-record` and `pw-dump` (PipeWire) plus `wpctl` (WirePlumber) — used to enumerate output sinks, resolve the default/selected sink, and capture desktop audio. Without them, Settings reports an actionable "PipeWire tools not found" style diagnostic and audio capture cannot start; sign-in, session, and viewer/UI flows are unaffected.
- **Secure token storage:** `secret-tool` (part of `libsecret`), backed by a Secret Service provider (GNOME Keyring on Ubuntu GNOME, KWallet's Secret Service compatibility on KDE). When unavailable, SonicRelay falls back to an in-memory, session-only token store and shows a warning — you will need to sign in again after restarting the app, but no plaintext token file is ever written.
- **Desktop rendering:** Avalonia's X11/XWayland native libraries (`libx11-6`/`libX11`, `libfontconfig1`/`fontconfig`, and related X11 client libraries), already present on Ubuntu Desktop and Fedora Workstation.
- **.NET native dependencies:** `libicu`, `libssl`/`openssl-libs`, `libstdc++`, `zlib`, `ca-certificates`, and Kerberos (`libgssapi-krb5-2`/`krb5-libs`) — required by the bundled .NET runtime, not bundled themselves. The portable `.tar.gz` still needs these installed on the host; the `.deb`/`.rpm` declare them as package dependencies.

On the officially supported Ubuntu 24.04 GNOME desktop, every one of these ships by default or is installed automatically with the `.deb`.

## Known limitations

- The `.rpm`/Fedora path is best effort: it is built and dependency-checked in CI, but has not been through the same manual Wayland/Xorg/tray/sink-switching validation pass as Ubuntu 24.04 (see the design spec's manual first-release gate). Report Fedora-specific issues; they will not block an Ubuntu 24.04 release.
- No `linux-arm64` build.
- No Flatpak/Snap/AppImage; the sandboxed-packaging story (which would need a different, portal-based audio capture design) is deferred.
- No Linux autostart entry yet — SonicRelay does not add itself to your session's startup apps.
- Tray/notification-area integration depends on your desktop's tray protocol support; when unavailable, closing the window quits the app normally instead of minimizing to an unreachable hidden process.
- PulseAudio-only systems without PipeWire are not a supported capture path; the adapter never falls back to a PulseAudio-only monitor source.

## Diagnostics

The Diagnostics page reports platform-specific state for support requests, including `osPlatform`, `desktopSession` (`wayland`/`x11`/`unknown`), PipeWire/WirePlumber/`pw-record` availability and version, Secret Service availability, tray availability, and the selected audio device — never tokens, Secret Service output, raw environment variables, or unbounded process output. See [the Windows publisher's diagnostics section](windows-publisher.md#diagnostics-and-safe-sharing) for the shared export/redaction model; the Linux fields extend the same report.

## CI and release process

`.github/workflows/ci.yml` builds and tests the solution on both `windows-latest` and `ubuntu-24.04` for every pull request and push to `main`, including a Linux startup smoke test that launches the actual published `linux-x64` binary under a virtual display (`xvfb-run`) and confirms it stays up without a live PipeWire session, Secret Service, or backend available.

`.github/workflows/release.yml` builds, tests, and releases on `v*` tags (or manual dispatch): the Windows job publishes the win-x64 assets and creates the GitHub Release first; a dependent `linux-package` job then checks out the exact same commit, publishes the self-contained `linux-x64` output, builds the `.tar.gz`/`.deb`/`.rpm` via [`packaging/linux/build-packages.sh`](../packaging/linux/build-packages.sh) (using [`fpm`](https://fpm.readthedocs.io/)), and extends the release's `checksums-sha256.txt` and notes with the Linux assets — so every release has one canonical checksums file and note set covering both platforms.
