<#
.SYNOPSIS
    Behavioural tests for .github/scripts/Publish-GitHubRelease.ps1.

.DESCRIPTION
    Drives the publish script against a fake `gh` executable so the retry and recovery paths
    are exercised for real, including the native-process stderr capture the retry decision
    depends on. Needs a POSIX shell for the fake executable, so it is skipped on Windows.
#>

$ErrorActionPreference = 'Stop'

if (-not ($IsLinux -or $IsMacOS)) {
    Write-Host 'Skipping: these tests need a POSIX shell for the fake gh executable.'
    exit 0
}

$root = Split-Path -Parent $PSScriptRoot
$publishScript = Join-Path $root '.github/scripts/Publish-GitHubRelease.ps1'

if (-not (Test-Path -LiteralPath $publishScript)) {
    Write-Error "Publish script not found: $publishScript"
}

$fakeGhBody = @'
#!/usr/bin/env bash
subcommand="$2"
printf '%s\n' "$*" >> "$FAKE_GH_LOG"

countFile="$FAKE_GH_STATE/$subcommand.count"
attempt=$(( $(cat "$countFile" 2>/dev/null || echo 0) + 1 ))
echo "$attempt" > "$countFile"

case "$subcommand" in
  create)
    if (( attempt <= ${FAKE_GH_CREATE_TRANSIENT_FAILURES:-0} )); then
      echo "HTTP 503: No server is currently available to service your request. Sorry about that. (https://api.github.com/repos/o/r/releases)" >&2
      exit 1
    fi
    if [[ "${FAKE_GH_CREATE_FATAL:-0}" == "1" ]]; then
      echo "HTTP 422: Validation Failed (https://api.github.com/repos/o/r/releases)" >&2
      exit 1
    fi
    echo "https://github.com/o/r/releases/tag/$3"
    ;;
  view)
    if [[ "${FAKE_GH_VIEW_EXISTS:-0}" == "1" ]]; then
      echo '{"tagName":"dev-1"}'
    else
      echo "release not found" >&2
      exit 1
    fi
    ;;
  upload)
    if (( attempt <= ${FAKE_GH_UPLOAD_TRANSIENT_FAILURES:-0} )); then
      echo "HTTP 502: Bad gateway (https://api.github.com/repos/o/r/releases)" >&2
      exit 1
    fi
    ;;
esac

exit 0
'@

$failures = New-Object System.Collections.Generic.List[string]

function Assert-True {
    param([bool]$Condition, [string]$Message)

    if (-not $Condition) {
        $script:failures.Add($Message)
        Write-Host "FAIL: $Message"
    }
    else {
        Write-Host "PASS: $Message"
    }
}

function Invoke-PublishScenario {
    param(
        [hashtable]$Environment = @{},
        [string[]]$Asset = @('/tmp/does-not-need-to-exist.zip')
    )

    $workspace = Join-Path ([System.IO.Path]::GetTempPath()) ([System.Guid]::NewGuid().ToString())
    New-Item -ItemType Directory -Force -Path $workspace | Out-Null

    $fakeGhPath = Join-Path $workspace 'gh'
    # -NoNewline keeps the shebang on line 1 with LF endings the shell can execute.
    Set-Content -LiteralPath $fakeGhPath -Value ($fakeGhBody -replace "`r`n", "`n") -NoNewline
    chmod +x $fakeGhPath

    $logPath = Join-Path $workspace 'calls.log'
    New-Item -ItemType File -Force -Path $logPath | Out-Null

    $previous = @{}
    $applied = @{
        FAKE_GH_LOG   = $logPath
        FAKE_GH_STATE = $workspace
    }
    foreach ($key in $Environment.Keys) {
        $applied[$key] = [string]$Environment[$key]
    }

    foreach ($key in $applied.Keys) {
        $previous[$key] = [System.Environment]::GetEnvironmentVariable($key)
        [System.Environment]::SetEnvironmentVariable($key, $applied[$key])
    }

    $threw = $false
    try {
        & $publishScript `
            -Tag 'dev-1' `
            -Title 'SonicRelay Windows Publisher 0.0.1' `
            -Asset $Asset `
            -ExtraArgument @('--target', 'abc123', '--prerelease') `
            -GhCommand $fakeGhPath `
            -InitialDelaySeconds 0 | Out-Null
    }
    catch {
        $threw = $true
    }
    finally {
        foreach ($key in $previous.Keys) {
            [System.Environment]::SetEnvironmentVariable($key, $previous[$key])
        }
    }

    $calls = @(Get-Content -LiteralPath $logPath -ErrorAction SilentlyContinue)
    Remove-Item -LiteralPath $workspace -Recurse -Force -ErrorAction SilentlyContinue

    return [pscustomobject]@{
        Threw = $threw
        Calls = $calls
    }
}

Write-Host '--- A successful create is not retried ---'
$scenario = Invoke-PublishScenario
Assert-True (-not $scenario.Threw) 'A successful create succeeds.'
Assert-True (@($scenario.Calls | Where-Object { $_ -like 'release create*' }).Count -eq 1) 'A successful create calls gh exactly once.'
Assert-True ($scenario.Calls[0] -like '*--title*') 'The release title is forwarded to gh.'
Assert-True ($scenario.Calls[0] -like '*--target abc123*--prerelease*') 'Extra arguments are forwarded to gh.'

Write-Host '--- Transient 5xx responses are retried until the create succeeds ---'
$scenario = Invoke-PublishScenario -Environment @{ FAKE_GH_CREATE_TRANSIENT_FAILURES = 2 }
Assert-True (-not $scenario.Threw) 'A create that 503s twice still succeeds.'
Assert-True (@($scenario.Calls | Where-Object { $_ -like 'release create*' }).Count -eq 3) 'The create is retried once per 5xx response.'
Assert-True (@($scenario.Calls | Where-Object { $_ -like 'release view*' }).Count -eq 0) 'No recovery is attempted once the create succeeds.'

Write-Host '--- Exhausted retries recover when the release exists anyway ---'
$scenario = Invoke-PublishScenario -Environment @{
    FAKE_GH_CREATE_TRANSIENT_FAILURES = 99
    FAKE_GH_VIEW_EXISTS               = 1
}
Assert-True (-not $scenario.Threw) 'A create whose response was lost recovers instead of failing the job.'
Assert-True (@($scenario.Calls | Where-Object { $_ -like 'release create*' }).Count -eq 4) 'The create stops after the configured attempt budget.'
Assert-True (@($scenario.Calls | Where-Object { $_ -like 'release upload dev-1*--clobber*' }).Count -eq 1) 'Assets are uploaded to the existing release.'

Write-Host '--- Exhausted retries fail when the release really is missing ---'
$scenario = Invoke-PublishScenario -Environment @{ FAKE_GH_CREATE_TRANSIENT_FAILURES = 99 }
Assert-True $scenario.Threw 'A create that never lands fails the step.'
Assert-True (@($scenario.Calls | Where-Object { $_ -like 'release upload*' }).Count -eq 0) 'No assets are uploaded when the release is missing.'

Write-Host '--- Non-transient failures are not retried ---'
$scenario = Invoke-PublishScenario -Environment @{ FAKE_GH_CREATE_FATAL = 1 }
Assert-True $scenario.Threw 'A non-5xx failure fails the step.'
Assert-True (@($scenario.Calls | Where-Object { $_ -like 'release create*' }).Count -eq 1) 'A non-5xx failure is not retried.'

Write-Host '--- Asset uploads are retried too ---'
$scenario = Invoke-PublishScenario -Environment @{
    FAKE_GH_CREATE_TRANSIENT_FAILURES = 99
    FAKE_GH_VIEW_EXISTS               = 1
    FAKE_GH_UPLOAD_TRANSIENT_FAILURES = 1
}
Assert-True (-not $scenario.Threw) 'An upload that 502s once still succeeds.'
Assert-True (@($scenario.Calls | Where-Object { $_ -like 'release upload*' }).Count -eq 2) 'The upload is retried once per 5xx response.'

if ($failures.Count -gt 0) {
    Write-Error "Release publishing helper tests failed:`n$($failures -join "`n")"
}

Write-Host 'All release publishing helper tests passed.'
