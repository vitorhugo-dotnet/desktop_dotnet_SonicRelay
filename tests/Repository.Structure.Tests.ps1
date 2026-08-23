$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$requiredPaths = @(
    'SonicRelay.Windows.slnx'
    'global.json'
    'Directory.Build.props'
    '.editorconfig'
    '.gitignore'
    '.github/workflows/ci.yml'
    '.github/workflows/release.yml'
    '.github/scripts/Publish-GitHubRelease.ps1'
    '.github/scripts/gh-retry.sh'
    'tests/Publish-GitHubRelease.Tests.ps1'
    'tests/gh-retry.Tests.sh'
    'src/SonicRelay.Windows.Desktop/SonicRelay.Windows.Desktop.csproj'
    'src/SonicRelay.Windows.Core/SonicRelay.Windows.Core.csproj'
    'src/SonicRelay.Windows.ApiClient/SonicRelay.Windows.ApiClient.csproj'
    'src/SonicRelay.Windows.Signaling/SonicRelay.Windows.Signaling.csproj'
    'src/SonicRelay.Windows.Audio/SonicRelay.Windows.Audio.csproj'
    'src/SonicRelay.Windows.WebRtc/SonicRelay.Windows.WebRtc.csproj'
    'tests/SonicRelay.Windows.Core.Tests/SonicRelay.Windows.Core.Tests.csproj'
    'tests/SonicRelay.Windows.ApiClient.Tests/SonicRelay.Windows.ApiClient.Tests.csproj'
    'docs/windows-publisher.md'
    'docs/architecture.md'
    'docs/non-admin-checklist.md'
    'docs/release-smoke-test.md'
    'docs/linux-publisher.md'
    'src/SonicRelay.Platform.Linux/SonicRelay.Platform.Linux.csproj'
    'packaging/linux/build-packages.sh'
    'packaging/linux/sonicrelay'
    'packaging/linux/sonicrelay.desktop'
    'packaging/linux/after-install.sh'
    'packaging/linux/after-remove.sh'
    'packaging/linux/icons/sonicrelay.svg'
    'packaging/linux/icons/sonicrelay.png'
    'docs/macos-publisher.md'
    'src/SonicRelay.Platform.MacOs/SonicRelay.Platform.MacOs.csproj'
    'src/SonicRelay.Platform.MacOs/native/SonicRelayAudioTap.swift'
    'packaging/macos/build-app-bundle.sh'
    'packaging/macos/build-audio-tap.sh'
    'packaging/macos/Info.plist'
    'packaging/macos/SonicRelay.entitlements'
    '.github/scripts/import-macos-certificate.sh'
    '.github/scripts/publish-macos-assets.sh'
)

$missingPaths = $requiredPaths | Where-Object {
    -not (Test-Path -LiteralPath (Join-Path $root $_))
}

if ($missingPaths.Count -gt 0) {
    Write-Error "Missing required repository paths:`n$($missingPaths -join "`n")"
}

$readme = Get-Content -Raw -LiteralPath (Join-Path $root 'README.md')
$checklistPath = Join-Path $root 'docs/non-admin-checklist.md'

if ($readme -notmatch '\(docs/non-admin-checklist\.md\)') {
    Write-Error 'README.md must link to docs/non-admin-checklist.md.'
}

$checklist = Get-Content -Raw -LiteralPath $checklistPath
$requiredNonAdminGuardrails = @(
    'no mandatory admin-required installer'
    'no mandatory Windows service'
    'no custom audio driver'
    'no kernel-mode component'
    'no mandatory inbound local firewall port'
    'no write access to Program Files for runtime data'
    'no write access to HKLM registry for runtime configuration'
    'no machine-wide dependency required for normal usage'
    'app data must go to user-scoped folders'
    'network communication must be outbound-only for API/signaling/WebRTC/TURN/STUN'
    'any dependency requiring elevation must be rejected or documented as incompatible'
)

$missingGuardrails = $requiredNonAdminGuardrails | Where-Object {
    $checklist.IndexOf($_, [StringComparison]::OrdinalIgnoreCase) -lt 0
}

if ($missingGuardrails.Count -gt 0) {
    Write-Error "Missing non-admin guardrails:`n$($missingGuardrails -join "`n")"
}

if ($readme -notmatch '\(docs/release-smoke-test\.md\)') {
    Write-Error 'README.md must link to docs/release-smoke-test.md.'
}

if ($readme -notmatch '\(docs/linux-publisher\.md\)') {
    Write-Error 'README.md must link to docs/linux-publisher.md.'
}

if ($readme -notmatch '\(docs/macos-publisher\.md\)') {
    Write-Error 'README.md must link to docs/macos-publisher.md.'
}

# macOS system audio capture is gated behind the Screen Recording (TCC) grant and
# a bundled, signed helper. Users hit both on first launch, and neither is
# guessable, so the macOS documentation must keep covering them.
$macosPublisher = Get-Content -Raw -LiteralPath (Join-Path $root 'docs/macos-publisher.md')
$requiredMacOsTopics = @(
    'Screen & System Audio Recording'
    'ScreenCaptureKit'
    'System Settings'
    'macOS 14'
    'osx-arm64'
    'osx-x64'
    'notariz'
    'Hardened Runtime'
    'Developer ID'
    'sonicrelay-audio-tap'
    'Universal Binary'
)
$missingMacOsTopics = $requiredMacOsTopics | Where-Object {
    $macosPublisher.IndexOf($_, [StringComparison]::OrdinalIgnoreCase) -lt 0
}
if ($missingMacOsTopics.Count -gt 0) {
    Write-Error "macOS publisher documentation is missing required topics:`n$($missingMacOsTopics -join "`n")"
}

# The helper and its .NET supervisor agree on exit codes only by convention, and
# nothing at build time links the Swift source to AudioTapExitCode.cs. Assert the
# two still describe the same values, so a change to one that forgets the other
# fails here instead of turning a denied permission into an unrecognised failure
# at runtime.
$audioTapSource = Get-Content -Raw -LiteralPath (Join-Path $root 'src/SonicRelay.Platform.MacOs/native/SonicRelayAudioTap.swift')
$audioTapExitCodes = Get-Content -Raw -LiteralPath (Join-Path $root 'src/SonicRelay.Platform.MacOs/Audio/AudioTapExitCode.cs')
$exitCodeContract = [ordered]@{
    'usage' = 64
    'unavailable' = 69
    'internalFailure' = 70
    'permissionDenied' = 77
    'unsupportedOs' = 78
}
$mismatchedExitCodes = @()
foreach ($entry in $exitCodeContract.GetEnumerator()) {
    if ($audioTapSource -notmatch "case\s+$($entry.Key)\s*=\s*$($entry.Value)\b") {
        $mismatchedExitCodes += "SonicRelayAudioTap.swift is missing '$($entry.Key) = $($entry.Value)'"
    }
}
foreach ($value in $exitCodeContract.Values) {
    if ($audioTapExitCodes -notmatch "=\s*$value\s*;") {
        $mismatchedExitCodes += "AudioTapExitCode.cs is missing the value $value"
    }
}
if ($mismatchedExitCodes.Count -gt 0) {
    Write-Error "macOS audio tap exit-code contract drifted:`n$($mismatchedExitCodes -join "`n")"
}

# The WPF/WinUI SonicRelay.Windows.App project (and its PairingCard control) was
# replaced by the cross-platform Avalonia shell in SonicRelay.Windows.Desktop
# (issue #32) in the same window as the device-identity migration (issue #26).
# The identity/device-composition checks below were rewritten against the
# surviving composition roots. NOTE: the manual pairing surface (challenge ID,
# QR code, copy buttons) that PairingCard used to provide has not been ported to
# the Avalonia shell yet — App.axaml.cs still drives the pre-migration sign-in
# view. That gap is tracked separately; this script only guards the non-UI
# composition (no reachable human-Identity code path, and the device-identity
# types actually wired in).
$runtimeSource = Get-Content -Raw -LiteralPath (Join-Path $root 'src/SonicRelay.Windows.Presentation/PublisherRuntime.cs')
$appSource = Get-Content -Raw -LiteralPath (Join-Path $root 'src/SonicRelay.Windows.Desktop/App.axaml.cs')
$desktopRuntimeFactory = Get-Content -Raw -LiteralPath (Join-Path $root 'src/SonicRelay.Windows.Desktop/DesktopRuntimeFactory.cs')
$forbiddenProductionIdentity = @(
    'new AuthApiClient('
    'new DeviceApiClient('
    'new UserScopedTokenStore('
    '/auth/login'
    '/auth/register'
    '/auth/me'
    '/auth/refresh'
)
$activeComposition = $runtimeSource + "`n" + $appSource + "`n" + $desktopRuntimeFactory
$reachableIdentity = $forbiddenProductionIdentity | Where-Object {
    $activeComposition.IndexOf($_, [StringComparison]::Ordinal) -ge 0
}
if ($reachableIdentity.Count -gt 0) {
    Write-Error "Production composition still reaches human Identity:`n$($reachableIdentity -join "`n")"
}

$requiredDeviceComposition = @(
    'UserScopedDeviceCredentialStore'
    'DeviceIdentityApiClient'
    'DeviceIdentitySession'
    'PairingApiClient'
    'InitializeDeviceIdentityAsync'
)
$missingDeviceComposition = $requiredDeviceComposition | Where-Object {
    $activeComposition.IndexOf($_, [StringComparison]::Ordinal) -lt 0
}
if ($missingDeviceComposition.Count -gt 0) {
    Write-Error "Missing device-identity production composition:`n$($missingDeviceComposition -join "`n")"
}

$releaseSmokeTestPath = Join-Path $root 'docs/release-smoke-test.md'
if (Test-Path -LiteralPath $releaseSmokeTestPath) {
    $releaseSmokeTest = Get-Content -Raw -LiteralPath $releaseSmokeTestPath
    $requiredReleaseSmokeTestGates = @(
        'standard user'
        'GitHub Releases'
        'user-writable folder'
        'administrator prompt'
        'Program Files'
        'Windows service'
        'drivers'
        'firewall rules'
        'open Settings'
        'backend URL'
        'device-credential.dat'
        'DPAPI CurrentUser'
        'DeviceBearer'
        'QR code'
        'pairing challenge ID'
        'pairing code'
        'session join code'
        'reset device identity'
        '%LOCALAPPDATA%\SonicRelay\WindowsPublisher'
        'missing backend'
        'missing audio device'
        'release is blocked'
    )

    $missingReleaseSmokeTestGates = $requiredReleaseSmokeTestGates | Where-Object {
        $releaseSmokeTest.IndexOf($_, [StringComparison]::OrdinalIgnoreCase) -lt 0
    }

    if ($missingReleaseSmokeTestGates.Count -gt 0) {
        Write-Error "Missing release smoke-test gates:`n$($missingReleaseSmokeTestGates -join "`n")"
    }

    $forbiddenReleaseSmokeTestIdentity = @('attempt login', 'tokens.dat', '/auth/login', '/auth/register', '/auth/refresh', '/auth/me')
    $staleReleaseSmokeTestIdentity = $forbiddenReleaseSmokeTestIdentity | Where-Object {
        $releaseSmokeTest.IndexOf($_, [StringComparison]::OrdinalIgnoreCase) -ge 0
    }
    if ($staleReleaseSmokeTestIdentity.Count -gt 0) {
        Write-Error "Release smoke test still requires Identity:`n$($staleReleaseSmokeTestIdentity -join "`n")"
    }
}

$publisherSpecification = Get-Content -Raw -LiteralPath (Join-Path $root 'docs/windows-publisher.md')
$requiredPublisherDeviceIdentity = @(
    '/api/devices/bootstrap'
    '/api/devices/token'
    'DeviceBearer'
    'device-credential.dat'
    'DPAPI'
    'CurrentUser'
    'QR'
    'pairing challenge'
    'pairing challenge ID'
    'pairing code'
    'session join code'
)
$missingPublisherDeviceIdentity = $requiredPublisherDeviceIdentity | Where-Object {
    $publisherSpecification.IndexOf($_, [StringComparison]::OrdinalIgnoreCase) -lt 0
}
if ($missingPublisherDeviceIdentity.Count -gt 0) {
    Write-Error "Publisher specification is missing device-first contracts:`n$($missingPublisherDeviceIdentity -join "`n")"
}
$forbiddenPublisherIdentity = @('/auth/login', '/auth/register', '/auth/refresh', '/auth/me', 'tokens.dat', 'UserScopedTokenStore', 'RestoreSessionAsync')
$stalePublisherIdentity = $forbiddenPublisherIdentity | Where-Object {
    $publisherSpecification.IndexOf($_, [StringComparison]::OrdinalIgnoreCase) -ge 0
}
if ($stalePublisherIdentity.Count -gt 0) {
    Write-Error "Publisher specification still describes Identity:`n$($stalePublisherIdentity -join "`n")"
}

$solutionReferencePattern = '(SonicRelay\.Windows\.slnx|\$env:SOLUTION_PATH)'
$releaseConfigurationPattern = '(Release|\$env:CONFIGURATION)'

$workflowPath = Join-Path $root '.github/workflows/ci.yml'
if (Test-Path -LiteralPath $workflowPath) {
    $workflow = Get-Content -Raw -LiteralPath $workflowPath
    $requiredWorkflowPatterns = [ordered]@{
        'pull request trigger' = '(?m)^\s*pull_request:\s*$'
        'push trigger' = '(?m)^\s*push:\s*$'
        'main branch filter' = '(?m)^\s*-\s*main\s*$'
        'Windows runner' = 'runs-on:\s*windows-latest'
        '.NET setup' = 'actions/setup-dotnet@v4'
        'global.json SDK selection' = 'global-json-file:\s*global\.json'
        'dependency restore' = "dotnet restore $solutionReferencePattern"
        'Release build' = "dotnet build $solutionReferencePattern --configuration $releaseConfigurationPattern --no-restore"
        'solution tests' = "dotnet test $solutionReferencePattern --configuration $releaseConfigurationPattern --no-build --no-restore"
        'TRX results' = '--logger "trx;LogFilePrefix=sonicrelay"'
        'repository structure test' = 'tests/Repository\.Structure\.Tests\.ps1'
        'artifact upload' = 'actions/upload-artifact@v4'
        'always upload results' = 'if:\s*always\(\)'
        'Ubuntu matrix leg' = 'ubuntu-24\.04'
        # Anchored to the matrix line: 'macos-15' on its own also matches the
        # packaging job's runs-on, which would let the build/test leg be dropped
        # while this check still passed.
        'macOS matrix leg' = '(?m)^\s*os:\s*\[[^\]]*macos-15[^\]]*\]\s*$'
        'build-and-test matrix' = '(?m)^\s*matrix:\s*$'
        'Linux startup smoke test' = 'xvfb-run'
        'macOS startup smoke test' = 'packaging/macos/build-app-bundle\.sh'
        'macOS packaging job' = '(?m)^\s*package-release-macos:\s*$'
    }

    $missingWorkflowRequirements = $requiredWorkflowPatterns.GetEnumerator() | Where-Object {
        $workflow -notmatch $_.Value
    } | ForEach-Object { $_.Key }

    if ($missingWorkflowRequirements.Count -gt 0) {
        Write-Error "Missing CI workflow requirements:`n$($missingWorkflowRequirements -join "`n")"
    }

    $unsafeReleaseNoteFragments = @(
        'Built from `${{ github.sha }}`'
        '- `$env:PRODUCT_NAME-$env:RUNTIME_ID-$assetVersion.zip`'
        '- `$env:PRODUCT_NAME-$env:RUNTIME_ID-$assetVersion.exe`'
        '- `$env:PRODUCT_NAME-$env:RUNTIME_ID-$assetVersion.msi`'
    )
    $unsafeReleaseNotes = $unsafeReleaseNoteFragments | Where-Object {
        $workflow.Contains($_)
    }
    if ($unsafeReleaseNotes.Count -gt 0) {
        Write-Error "CI release notes contain unsafe PowerShell backtick interpolation:`n$($unsafeReleaseNotes -join "`n")"
    }

}

# Release publishing is the last step of a long, expensive build, and api.github.com
# intermittently answers 5xx. Every workflow that publishes a release must go through the
# retrying helpers rather than calling `gh` directly, so a transient blip cannot discard a
# green build's packages.
$publishingWorkflows = @('.github/workflows/ci.yml', '.github/workflows/release.yml')
# The macOS jobs delegate their release step to a shared script rather than
# inlining it in both workflows, so the same "never call gh without retries"
# guard has to follow it there.
$macosPublishScript = Get-Content -Raw -LiteralPath (Join-Path $root '.github/scripts/publish-macos-assets.sh')
if ($macosPublishScript -notmatch 'source "\$repo_root/\.github/scripts/gh-retry\.sh"') {
    Write-Error '.github/scripts/publish-macos-assets.sh must source .github/scripts/gh-retry.sh so transient 5xx responses are retried.'
}
if ($macosPublishScript -match '(?m)^\s*gh release ') {
    Write-Error '.github/scripts/publish-macos-assets.sh calls gh release directly; wrap every call in retry_gh.'
}
foreach ($publishingWorkflowPath in $publishingWorkflows) {
    $fullPath = Join-Path $root $publishingWorkflowPath
    if (-not (Test-Path -LiteralPath $fullPath)) {
        continue
    }

    $publishingWorkflow = Get-Content -Raw -LiteralPath $fullPath

    if ($publishingWorkflow -match '(?m)^\s*gh release ') {
        Write-Error "$publishingWorkflowPath calls gh release directly; use .github/scripts/Publish-GitHubRelease.ps1 or retry_gh so transient 5xx responses are retried."
    }

    if ($publishingWorkflow -notmatch 'Publish-GitHubRelease\.ps1') {
        Write-Error "$publishingWorkflowPath must publish releases through .github/scripts/Publish-GitHubRelease.ps1."
    }

    if ($publishingWorkflow -notmatch 'source \.github/scripts/gh-retry\.sh') {
        Write-Error "$publishingWorkflowPath must source .github/scripts/gh-retry.sh for its Linux release steps."
    }
}

$releaseWorkflowPath = Join-Path $root '.github/workflows/release.yml'
if (Test-Path -LiteralPath $releaseWorkflowPath) {
    $releaseWorkflow = Get-Content -Raw -LiteralPath $releaseWorkflowPath
    $requiredReleaseWorkflowPatterns = [ordered]@{
        'version tag trigger' = '(?m)^\s*-\s*.+v\*.+\s*$'
        'manual trigger' = '(?m)^\s*workflow_dispatch:\s*$'
        'Windows runner' = 'runs-on:\s*windows-latest'
        'release write permission' = '(?ms)permissions:.*?contents:\s*write'
        'dependency restore' = 'dotnet restore SonicRelay\.Windows\.slnx'
        'Release build' = 'dotnet build SonicRelay\.Windows\.slnx --configuration Release --no-restore'
        'repository structure test' = 'tests/Repository\.Structure\.Tests\.ps1'
        'solution tests' = 'dotnet test SonicRelay\.Windows\.slnx --configuration Release --no-build --no-restore'
        'runtime-specific publish restore' = '(?s)dotnet restore src/SonicRelay\.Windows\.Desktop/SonicRelay\.Windows\.Desktop\.csproj.*?--runtime win-x64'
        'Windows x64 publish' = '(?s)dotnet publish src/SonicRelay\.Windows\.Desktop/SonicRelay\.Windows\.Desktop\.csproj.*?--runtime win-x64'
        'self-contained publish' = '--self-contained true'
        'portable archive name' = 'SonicRelay\.WindowsPublisher-win-x64-\$version\.zip'
        'build metadata' = 'BUILD-INFO\.txt'
        'release creation' = 'Publish-GitHubRelease\.ps1'
        'generated release notes' = '--generate-notes'
        'Linux packaging job' = '(?m)^\s*linux-package:\s*$'
        'Ubuntu runner for Linux packaging' = 'runs-on:\s*ubuntu-24\.04'
        'Linux x64 publish' = '(?s)dotnet publish src/SonicRelay\.Windows\.Desktop/SonicRelay\.Windows\.Desktop\.csproj.*?--runtime linux-x64'
        'Linux package build script' = 'packaging/linux/build-packages\.sh'
        'fpm packaging tool' = 'gem install --no-document fpm'
        'checksums extended for Linux' = 'checksums-sha256\.txt'
        'macOS packaging job' = '(?m)^\s*macos-package:\s*$'
        'macOS runner for macOS packaging' = 'runs-on:\s*macos-15'
        'macOS arm64 publish' = '(?s)dotnet publish src/SonicRelay\.Windows\.Desktop/SonicRelay\.Windows\.Desktop\.csproj.*?osx-arm64'
        'macOS app bundle build script' = 'packaging/macos/build-app-bundle\.sh'
        'macOS asset publishing script' = '\.github/scripts/publish-macos-assets\.sh'
    }

    $missingReleaseWorkflowRequirements = $requiredReleaseWorkflowPatterns.GetEnumerator() | Where-Object {
        $releaseWorkflow -notmatch $_.Value
    } | ForEach-Object { $_.Key }

    if ($missingReleaseWorkflowRequirements.Count -gt 0) {
        Write-Error "Missing release workflow requirements:`n$($missingReleaseWorkflowRequirements -join "`n")"
    }
}

Write-Host "Repository structure verified: $($requiredPaths.Count) required paths found."
