# Microsoft Store package (MSIX, x64)

How the SonicRelay Windows publisher is packaged for the Microsoft Store, how to reproduce
the package locally, and what has to be validated before uploading it to the Partner Center.

Implements [issue #66](https://github.com/vitorhugo-dotnet/desktop_dotnet_SonicRelay/issues/66),
a subtask of [dotnet_SonicRelay#43](https://github.com/vitorhugo-dotnet/dotnet_SonicRelay/issues/43).

## What is produced

| Artifact | What it is |
| --- | --- |
| `SonicRelay.WindowsPublisher-win-x64-<version>.msix` | The package itself. Unsigned unless you sign it for local validation. |
| `SonicRelay.WindowsPublisher-win-x64-<version>.appxsym` | The published `.pdb` files, so Partner Center can symbolicate crash analytics. |
| `SonicRelay.WindowsPublisher-win-x64-<version>.msixupload` | A ZIP of the two above. **This is the file to upload** on the Partner Center *Packages* page. |

The first submission is **x64 only**. ARM64 and a multi-architecture `.msixbundle` are
deliberately out of scope; a single-architecture MSIX is a complete, valid Store package, and
adding architectures is an additive change to the same pipeline later.

The `.msix` alone is also accepted by Partner Center. Prefer the `.msixupload` — it is the
only one of the two that carries symbols.

## How it is packaged, and why not the .NET MSIX tooling

The app is a full-trust Win32 application (Avalonia, not WinUI), and the same
`SonicRelay.Windows.Desktop` project also builds for Linux and macOS. Turning on the Windows
App SDK single-project MSIX tooling would pull a Windows-only TFM and the Windows App SDK
into that shared project.

So the package is built the desktop-bridge way instead: the ordinary
`dotnet publish --runtime win-x64 --self-contained` output is staged into an MSIX layout with
a hand-authored manifest and packed with `MakeAppx.exe` from the Windows SDK. Nothing in any
`.csproj` changes, and the manifest is reviewable as a file rather than as generated output.

- `packaging/windows/Build-MsixPackage.ps1` — the build script.
- `packaging/windows/msix/AppxManifest.template.xml` — the manifest, with placeholders.
- `packaging/windows/msix/store-identity.json` — the Partner Center identity.
- `packaging/windows/msix/Assets/` — tile, app-list and Store logos, generated from the app icon.
- `tests/Build-MsixPackage.Tests.ps1` — runs on every OS in CI against a fake `MakeAppx`.

`Application/@EntryPoint` is `Windows.FullTrustApplication` and the target device family is
`Windows.Desktop` (min `10.0.17763.0`, the oldest Windows 10 build the Store still accepts for
MSIX), so the Store never offers the package to a device family it cannot run on.

## Package identity

`Identity/Name`, `Identity/Publisher` and `Properties/PublisherDisplayName` must match the
values reserved for SonicRelay in the Partner Center **exactly**, or the *Packages* page
rejects the upload. Find them under **App management → Product identity**.

`packaging/windows/msix/store-identity.json` currently holds **placeholders**, so the package
builds and can be sideloaded before an account exists. A build that still uses them prints a
warning. Replace them one of two ways:

1. **In the repository** — edit `store-identity.json` and drop `"isPlaceholder": true`.
2. **In CI, without a commit** — set the repository variables `MSIX_IDENTITY_NAME`,
   `MSIX_PUBLISHER`, `MSIX_PUBLISHER_DISPLAY_NAME` and `MSIX_DISPLAY_NAME`
   (*Settings → Secrets and variables → Actions → Variables*). Both workflows pass them to
   the packaging job as environment variables of the same name, and the build script prefers
   them over the file. `MSIX_DESCRIPTION` works the same way. Setting all three identity
   variables also silences the placeholder warning.

The precedence is: explicit script parameter, then `MSIX_*` environment variable, then
`store-identity.json`.

`Identity/Publisher` is an X.500 distinguished name (`CN=A1B2C3D4-...`), not the company
name; the build fails early if it does not look like one.

## Versioning

`Identity/Version` is `Major.Minor.Build.Revision`. Pass a three-part version and the script
pads it. Two Store rules are enforced at build time rather than discovered at upload time:

- **Revision must be `0`.** The Store reserves it for its own re-signing.
- **`0.0.0.0` is rejected.** Every part is also capped at 65535.

Each submission must have a higher `Major.Minor.Build` than the previous one. The release
workflow derives it from the `v*` tag; for the prerelease builds that would otherwise resolve
to `0.0.0`, it substitutes `0.0.<run-number>` so the packaging step still exercises a valid
version.

## Capabilities

The manifest declares exactly one capability:

```xml
<rescap:Capability Name="runFullTrust" />
```

A full-trust desktop package does not run in an AppContainer, so the capability list does not
gate what it may do: outbound HTTPS/WSS to the backend and signaling, the UDP/TCP of WebRTC
and STUN/TURN, and the WASAPI **render**-endpoint loopback capture all work under
`runFullTrust` alone.

Deliberately not declared:

- **`microphone`** — SonicRelay never opens a capture endpoint. What it shares is the system
  output mix, in broadcast and duplex sessions alike (see [two-way audio](two-way-audio.md)).
  Declaring it would ask users for consent the app has no use for.
- **`internetClient` / `privateNetworkClientServer`** — AppContainer network capabilities,
  inert for a full-trust package and flagged by review as unused.

## Prerequisites for a local build

- The **Windows 10/11 SDK**, for `MakeAppx.exe`, `SignTool.exe` and the Windows App
  Certification Kit. Install it with the *Windows 11 SDK* component in the Visual Studio
  Installer, or standalone from
  <https://developer.microsoft.com/windows/downloads/windows-sdk/>.
- The .NET SDK from `global.json`.

The script finds `MakeAppx.exe` under `%ProgramFiles(x86)%\Windows Kits\10\bin\<version>\x64`,
on `PATH`, or wherever `MAKEAPPX_PATH` / `-MakeAppxPath` points.

## Build the package locally

```powershell
dotnet publish src/SonicRelay.Windows.Desktop/SonicRelay.Windows.Desktop.csproj `
  --configuration Release --runtime win-x64 --self-contained true `
  --output artifacts/publish/win-x64

./packaging/windows/Build-MsixPackage.ps1 `
  -PublishDirectory artifacts/publish/win-x64 `
  -Version 1.0.0 `
  -OutputDirectory artifacts/store
```

Useful switches: `-SkipUploadPackage` (only the `.msix`), `-Architecture`,
`-IdentityName`/`-Publisher`/`-PublisherDisplayName`/`-DisplayName` (override the identity
file), `-CertificatePath`/`-CertificatePassword` (sign, see below).

## Sign a package for local validation

**A package uploaded to the Store must stay unsigned.** The Store re-signs everything it
ingests with its own certificate and rejects a package signed by someone else. Signing is only
for validating install/update/uninstall on your own machine, where Windows refuses to install
an unsigned package.

The certificate subject must equal `Identity/Publisher` character for character.

```powershell
# Create and trust a test certificate once. Installing into LocalMachine\Root needs an
# elevated shell - it is a validation-only step and no part of shipping the app.
$publisher = 'CN=SonicRelay'
$certificate = New-SelfSignedCertificate -Type Custom -Subject $publisher `
  -KeyUsage DigitalSignature -FriendlyName 'SonicRelay MSIX test' `
  -CertStoreLocation 'Cert:\CurrentUser\My' `
  -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')

$password = ConvertTo-SecureString -String 'test-only' -Force -AsPlainText
Export-PfxCertificate -Cert $certificate -FilePath sonicrelay-test.pfx -Password $password
Import-PfxCertificate -FilePath sonicrelay-test.pfx -Password $password `
  -CertStoreLocation 'Cert:\LocalMachine\Root'
```

Then rebuild with `-CertificatePath sonicrelay-test.pfx -CertificatePassword 'test-only'`,
and install with `Add-AppxPackage -Path <package>.msix`.

## Manual validation before uploading

Run this on a clean Windows 10 and a Windows 11 x64 machine, as a **standard user** — the
non-admin guarantee in [the non-admin checklist](non-admin-checklist.md) still applies to the
packaged app. Every item is a gate.

### Clean install

- [ ] `Add-AppxPackage -Path <package>.msix` succeeds without an elevation prompt.
- [ ] The app appears in the Start menu under its Store display name, with the SonicRelay icon
      on the tile, the app list and the taskbar.
- [ ] It launches from the Start menu and the window renders.

### Functional check in the MSIX context

MSIX redirects the app's writes, so these are the paths most likely to behave differently from
the MSI build:

- [ ] Audio capture starts and the level meter moves.
- [ ] A session can be created and shows a join code.
- [ ] Signaling connects (Diagnostics shows the WebSocket connected).
- [ ] A viewer receives audio over WebRTC.
- [ ] Pulling the network and restoring it reconnects the session.
- [ ] Device credentials survive a restart of the app (pairing is not asked for again).
- [ ] Settings written in the app survive a restart.

### Update

- [ ] Build a second package with a higher `Major.Minor.Build`, signed with the same
      certificate.
- [ ] `Add-AppxPackage -Path <newer>.msix` upgrades in place.
- [ ] `Get-AppxPackage <identity-name>` reports the new version, and only one entry exists.
- [ ] The app still starts, and the device credentials and settings from before the update are
      still there.

### Uninstall

- [ ] Uninstall from the Start menu (or `Remove-AppxPackage`) completes.
- [ ] No `SonicRelay.Windows.Desktop` process is left running.
- [ ] `Get-AppxPackage <identity-name>` returns nothing.
- [ ] `%LOCALAPPDATA%\Packages\<identity-name>_<publisher-hash>` is gone.

### Windows App Certification Kit

WACK ships with the Windows SDK. Run it against the **signed** package and fix every failure
before uploading — the Store runs the same tests during certification:

```powershell
& "${env:ProgramFiles(x86)}\Windows Kits\10\App Certification Kit\appcert.exe" reset
& "${env:ProgramFiles(x86)}\Windows Kits\10\App Certification Kit\appcert.exe" test `
  -appxpackagepath <package>.msix -reportoutputpath wack-report.xml
```

Or run *Windows App Cert Kit* from the Start menu and choose **Validate Store App**.

- [ ] The report has no failures. Warnings are acceptable; record them in the pull request.

## Upload to the Partner Center

1. Partner Center → the SonicRelay app → **Submissions → Packages**.
2. Upload the `.msixupload` produced by the release workflow. It is attached to the GitHub
   Release for that version, next to the `.msi` and `.exe` installers; the same file is also
   the `store-package-<version>` workflow artifact, which expires after 30 days.
3. Confirm the page shows the expected package name, publisher, version and the `x64`
   architecture. A mismatch here means the identity in `store-identity.json` (or in the
   repository variables) is not the reserved one.

## CI

- **`.github/workflows/ci.yml`** — the `package-release` job builds the MSIX after the build
  and test jobs pass, on pushes to `main` and manual runs, adds it to the release's
  `checksums-sha256.txt` and notes, attaches it to the GitHub Release it publishes, and
  uploads it as the `store-package-<version>` workflow artifact as well.
- **`.github/workflows/release.yml`** — the `store-package` job runs after
  `build-test-and-release` succeeds, checks out the exact commit that was released, and
  uploads the same artifact. The `store-release-assets` job then attaches that artifact to
  the release through `.github/scripts/publish-store-assets.sh`. It waits for
  `macos-package` because `linux-package`, `macos-package` and it all rewrite the same
  release notes and the same canonical `checksums-sha256.txt`, so they have to run one
  after the other.

The `.msix` and `.msixupload` on the release are **unsigned**, exactly as the Store requires,
so Windows will not install the `.msix` by double-clicking it — it is not a substitute for the
`.msi` or `.exe` installers. They are published so a Partner Center submission can be
reproduced from the release at any time instead of from a workflow artifact that expires
after 30 days. The `.appxsym` is not attached separately: it is already inside the
`.msixupload`.

## Out of scope

- ARM64 and multi-architecture `.msixbundle`.
- Automating the submission itself through the Partner Center API.
- Replacing the MSI/EXE installers — they remain the direct-download path.
