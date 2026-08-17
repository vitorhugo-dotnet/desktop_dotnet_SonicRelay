<#
.SYNOPSIS
    Creates a GitHub release and uploads its assets, tolerating transient GitHub API failures.

.DESCRIPTION
    `gh release create` talks to api.github.com, which intermittently answers 5xx
    (for example "HTTP 503: No server is currently available to service your request").
    A bare `gh release create` turns that transient blip into a red build even though every
    package was already produced successfully, so this script retries 5xx responses with
    exponential backoff.

    Retrying `release create` is not blindly safe: the API call can succeed server-side while
    the response is lost, and `gh` uploads assets only after the release exists. So when the
    create ultimately fails, this script checks whether the release is there anyway and, if it
    is, uploads the assets to it with --clobber instead of failing the job. Genuine failures
    (bad tag, missing asset, permission denied) are not retried and still fail the step.

.PARAMETER Tag
    Release tag to create, for example dev-119 or v1.2.3.

.PARAMETER Title
    Release title.

.PARAMETER Asset
    Files to attach to the release.

.PARAMETER ExtraArgument
    Additional `gh release create` arguments, for example --notes-file, --generate-notes,
    --target, --verify-tag, or --prerelease.

.PARAMETER GhCommand
    The gh executable to invoke. Overridable for testing.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Tag,

    [Parameter(Mandatory = $true)]
    [string]$Title,

    [string[]]$Asset = @(),

    [string[]]$ExtraArgument = @(),

    [string]$GhCommand = 'gh',

    [int]$MaxAttempts = 4,

    [int]$InitialDelaySeconds = 2
)

$ErrorActionPreference = 'Stop'

# Retries are driven by the gh exit code captured below, not by native-command exceptions.
$PSNativeCommandUseErrorActionPreference = $false

function Invoke-GhWithRetry {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$GhArgument,

        # Return the failed result instead of throwing, so the caller can decide how to recover.
        [switch]$AllowFailure
    )

    $delaySeconds = $InitialDelaySeconds

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        $stderrPath = [System.IO.Path]::GetTempFileName()

        try {
            $stdout = & $GhCommand @GhArgument 2> $stderrPath
            $exitCode = $LASTEXITCODE
            $stderr = Get-Content -Raw -LiteralPath $stderrPath
        }
        finally {
            Remove-Item -LiteralPath $stderrPath -Force -ErrorAction SilentlyContinue
        }

        if (-not $stderr) {
            $stderr = ''
        }

        $result = [pscustomobject]@{
            ExitCode = $exitCode
            Output   = ($stdout -join [System.Environment]::NewLine)
            Error    = $stderr
        }

        # Replay both streams to the job log. Write-Host keeps them out of the value
        # returned to the caller.
        if ($result.Output.Trim()) {
            Write-Host $result.Output.Trim()
        }

        if ($stderr.Trim()) {
            Write-Host $stderr.Trim()
        }

        if ($exitCode -eq 0) {
            return $result
        }

        $isTransient = $stderr -match 'HTTP 5\d{2}'

        if (-not $isTransient -or $attempt -eq $MaxAttempts) {
            if ($AllowFailure) {
                return $result
            }

            throw "gh $($GhArgument -join ' ') failed with exit code $exitCode."
        }

        Write-Host "::warning::GitHub API returned a 5xx response; retrying in $delaySeconds seconds (attempt $attempt/$MaxAttempts)."
        Start-Sleep -Seconds $delaySeconds
        $delaySeconds *= 2
    }
}

$createArgument = @('release', 'create', $Tag) + $Asset + @('--title', $Title) + $ExtraArgument

$create = Invoke-GhWithRetry -GhArgument $createArgument -AllowFailure

if ($create.ExitCode -eq 0) {
    Write-Host "Created release $Tag."
    return
}

# The create did not report success. It may still have landed server-side, or an earlier
# attempt may have created the release before failing while uploading assets.
$view = Invoke-GhWithRetry -GhArgument @('release', 'view', $Tag, '--json', 'tagName') -AllowFailure

if ($view.ExitCode -ne 0) {
    throw "Creating release $Tag failed and the release does not exist. See the gh output above."
}

Write-Host "Release $Tag already exists; uploading assets to the existing release instead."

if ($Asset.Count -gt 0) {
    Invoke-GhWithRetry -GhArgument (@('release', 'upload', $Tag) + $Asset + @('--clobber')) | Out-Null
}

Write-Host "Published release $Tag."
