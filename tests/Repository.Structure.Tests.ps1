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
    'src/SonicRelay.Windows.App/SonicRelay.Windows.App.csproj'
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

$runtimeSource = Get-Content -Raw -LiteralPath (Join-Path $root 'src/SonicRelay.Windows.Presentation/PublisherRuntime.cs')
$appSource = Get-Content -Raw -LiteralPath (Join-Path $root 'src/SonicRelay.Windows.App/App.xaml.cs')
$connectionPage = Get-Content -Raw -LiteralPath (Join-Path $root 'src/SonicRelay.Windows.App/Pages/ConnectionPage.xaml')
$connectionPageCode = Get-Content -Raw -LiteralPath (Join-Path $root 'src/SonicRelay.Windows.App/Pages/ConnectionPage.xaml.cs')
$pairingCard = Get-Content -Raw -LiteralPath (Join-Path $root 'src/SonicRelay.Windows.App/Controls/PairingCard.xaml')
$pairingCardCode = Get-Content -Raw -LiteralPath (Join-Path $root 'src/SonicRelay.Windows.App/Controls/PairingCard.xaml.cs')
$forbiddenProductionIdentity = @(
    'new AuthApiClient('
    'new DeviceApiClient('
    'new UserScopedTokenStore('
    'RestoreSessionAsync('
    '/auth/login'
    '/auth/register'
    '/auth/me'
    '/auth/refresh'
)
$activeComposition = $runtimeSource + "`n" + $appSource + "`n" + $connectionPage + "`n" + $connectionPageCode
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

$requiredManualPairingSurface = @(
    'Pairing challenge ID'
    'Copy challenge ID'
    'Copy pairing code'
    'Session join code'
)
$missingManualPairingSurface = $requiredManualPairingSurface | Where-Object {
    $pairingCard.IndexOf($_, [StringComparison]::Ordinal) -lt 0
}
if ($missingManualPairingSurface.Count -gt 0) {
    Write-Error "Missing manual pairing surface:`n$($missingManualPairingSurface -join "`n")"
}

$requiredPairingLifecyclePatterns = @(
    '(?s)private void OnLoaded\(.*?viewModel\.StateChanged -= OnStateChanged;.*?ClearExpiredChallenge\(DateTimeOffset\.UtcNow\).*?viewModel\.StateChanged \+= OnStateChanged;'
    '(?s)private void OnUnloaded\(.*?viewModel\.StateChanged -= OnStateChanged;'
)
$missingPairingLifecycle = $requiredPairingLifecyclePatterns | Where-Object {
    $pairingCardCode -notmatch $_
}
if ($missingPairingLifecycle.Count -gt 0) {
    Write-Error 'PairingCard must clear expired challenges and manage StateChanged across load/unload.'
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
    }

    $missingWorkflowRequirements = $requiredWorkflowPatterns.GetEnumerator() | Where-Object {
        $workflow -notmatch $_.Value
    } | ForEach-Object { $_.Key }

    if ($missingWorkflowRequirements.Count -gt 0) {
        Write-Error "Missing CI workflow requirements:`n$($missingWorkflowRequirements -join "`n")"
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
        'Windows x64 publish' = '(?s)dotnet publish src/SonicRelay\.Windows\.App/SonicRelay\.Windows\.App\.csproj.*?--runtime win-x64'
        'self-contained publish' = '--self-contained true'
        'portable archive name' = 'SonicRelay\.WindowsPublisher-win-x64-\$version\.zip'
        'build metadata' = 'BUILD-INFO\.txt'
        'release creation' = 'gh release create'
        'generated release notes' = '--generate-notes'
    }

    $missingReleaseWorkflowRequirements = $requiredReleaseWorkflowPatterns.GetEnumerator() | Where-Object {
        $releaseWorkflow -notmatch $_.Value
    } | ForEach-Object { $_.Key }

    if ($missingReleaseWorkflowRequirements.Count -gt 0) {
        Write-Error "Missing release workflow requirements:`n$($missingReleaseWorkflowRequirements -join "`n")"
    }
}

Write-Host "Repository structure verified: $($requiredPaths.Count) required paths found."
