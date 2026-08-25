<#
.SYNOPSIS
    Builds the Microsoft Store MSIX package for the SonicRelay Windows publisher.

.DESCRIPTION
    Stages a published, self-contained win-x64 build into an MSIX layout, renders
    packaging/windows/msix/AppxManifest.template.xml with the Partner Center identity, and
    packs it with MakeAppx.exe from the Windows SDK.

    The app is a full-trust Win32 (Avalonia) application, so it is packaged with the desktop
    bridge (EntryPoint="Windows.FullTrustApplication") rather than through a Windows App SDK
    single-project MSIX: nothing in the .csproj changes, and the same project keeps building
    for Linux and macOS.

    Two artifacts are produced next to each other:

      *.msix        the package itself, unsigned unless -CertificatePath is given.
      *.msixupload  a ZIP of the .msix plus a .appxsym symbol bundle, which is what the
                    Partner Center Packages page wants when crash analytics should be able
                    to symbolicate. Suppress it with -SkipUploadPackage.

    Packages destined for the Store are deliberately left unsigned - the Store re-signs
    every package it ingests with its own certificate, and a package signed by someone else
    is rejected. Signing here is only for local install/update/uninstall validation, where
    Windows refuses to install an unsigned package; see docs/microsoft-store-package.md.

.EXAMPLE
    ./packaging/windows/Build-MsixPackage.ps1 `
        -PublishDirectory artifacts/publish/win-x64 `
        -Version 1.2.3 `
        -OutputDirectory artifacts/store
#>
[CmdletBinding()]
param(
    # Output of `dotnet publish --runtime win-x64 --self-contained true`.
    [Parameter(Mandatory)]
    [string] $PublishDirectory,

    # Major.Minor.Build, or Major.Minor.Build.Revision with Revision 0 (Store rule).
    [Parameter(Mandatory)]
    [string] $Version,

    [Parameter(Mandatory)]
    [string] $OutputDirectory,

    [ValidateSet('x64', 'x86', 'arm64')]
    [string] $Architecture = 'x64',

    # Identity overrides. Anything left empty falls back to -IdentityFile.
    [string] $IdentityName,
    [string] $Publisher,
    [string] $PublisherDisplayName,
    [string] $DisplayName,
    [string] $Description,

    [string] $IdentityFile,
    [string] $ManifestTemplate,
    [string] $AssetsDirectory,

    [string] $ExecutableName = 'SonicRelay.Windows.Desktop.exe',
    [string] $PackageBaseName = 'SonicRelay.WindowsPublisher',

    # Explicit tool paths win over discovery; the tests use them to inject fakes.
    [string] $MakeAppxPath,
    [string] $SignToolPath,

    # Local validation only. A Store upload must stay unsigned.
    [string] $CertificatePath,
    [string] $CertificatePassword,
    [string] $TimestampUrl = 'http://timestamp.digicert.com',

    [switch] $SkipUploadPackage
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$packagingRoot = $PSScriptRoot
$msixRoot = Join-Path $packagingRoot 'msix'

if (-not $IdentityFile) { $IdentityFile = Join-Path $msixRoot 'store-identity.json' }
if (-not $ManifestTemplate) { $ManifestTemplate = Join-Path $msixRoot 'AppxManifest.template.xml' }
if (-not $AssetsDirectory) { $AssetsDirectory = Join-Path $msixRoot 'Assets' }

function Resolve-ExistingPath {
    param([string] $Path, [string] $Description)

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "$Description not found: $Path"
    }

    return (Resolve-Path -LiteralPath $Path).Path
}

# Turns "1.2.3" into "1.2.3.0" and rejects anything the Store will not accept. Revision is
# reserved for the Store's own re-signing, so a package that sets it is refused at upload.
function Resolve-PackageVersion {
    param([string] $Value)

    $parts = @($Value.Split('.'))
    if ($parts.Count -eq 3) { $parts += '0' }

    if ($parts.Count -ne 4) {
        throw "Package version must be Major.Minor.Build[.Revision], got '$Value'."
    }

    $numbers = foreach ($part in $parts) {
        if ($part -notmatch '^\d+$') {
            throw "Package version must contain only digits between dots, got '$Value'."
        }

        $number = [int] $part
        if ($number -gt 65535) {
            throw "Every part of the package version must be 0-65535, got '$Value'."
        }

        $number
    }

    if ($numbers[3] -ne 0) {
        throw "The Store reserves the revision part of the package version; it must be 0, got '$Value'."
    }

    if (($numbers[0] -eq 0) -and ($numbers[1] -eq 0) -and ($numbers[2] -eq 0)) {
        throw "The Store rejects package version 0.0.0.0; pass a version with a non-zero part, got '$Value'."
    }

    return ($numbers -join '.')
}

function Get-JsonValue {
    param([psobject] $Object, [string] $Name)

    if ($Object.PSObject.Properties.Name -contains $Name) {
        return $Object.$Name
    }

    return $null
}

# Explicit parameter, then MSIX_* environment variable, then the repository identity file.
# The middle layer is what lets CI carry the real Partner Center identity as repository
# variables instead of committing it.
function Resolve-IdentityValue {
    param([string] $Explicit, [string] $EnvironmentVariable, [psobject] $Identity, [string] $JsonName)

    if ($Explicit) {
        return [pscustomobject]@{ Value = $Explicit; IsOverride = $true }
    }

    $fromEnvironment = [Environment]::GetEnvironmentVariable($EnvironmentVariable)
    if ($fromEnvironment) {
        return [pscustomobject]@{ Value = $fromEnvironment; IsOverride = $true }
    }

    return [pscustomobject]@{ Value = (Get-JsonValue $Identity $JsonName); IsOverride = $false }
}

function Resolve-StoreIdentity {
    $identityPath = Resolve-ExistingPath -Path $IdentityFile -Description 'Store identity file'
    $identity = Get-Content -Raw -LiteralPath $identityPath | ConvertFrom-Json

    $sources = [ordered]@{
        IdentityName = Resolve-IdentityValue -Explicit $IdentityName -EnvironmentVariable 'MSIX_IDENTITY_NAME' -Identity $identity -JsonName 'identityName'
        Publisher = Resolve-IdentityValue -Explicit $Publisher -EnvironmentVariable 'MSIX_PUBLISHER' -Identity $identity -JsonName 'publisher'
        PublisherDisplayName = Resolve-IdentityValue -Explicit $PublisherDisplayName -EnvironmentVariable 'MSIX_PUBLISHER_DISPLAY_NAME' -Identity $identity -JsonName 'publisherDisplayName'
        DisplayName = Resolve-IdentityValue -Explicit $DisplayName -EnvironmentVariable 'MSIX_DISPLAY_NAME' -Identity $identity -JsonName 'displayName'
        Description = Resolve-IdentityValue -Explicit $Description -EnvironmentVariable 'MSIX_DESCRIPTION' -Identity $identity -JsonName 'description'
    }

    $resolved = [ordered]@{}
    foreach ($entry in $sources.GetEnumerator()) {
        if ([string]::IsNullOrWhiteSpace($entry.Value.Value)) {
            throw "Store identity value '$($entry.Key)' is empty; set it in $identityPath or pass -$($entry.Key)."
        }

        $resolved[$entry.Key] = $entry.Value.Value
    }

    if ($resolved.IdentityName -notmatch '^[A-Za-z0-9][A-Za-z0-9.\-]{1,49}$') {
        throw "Package identity name '$($resolved.IdentityName)' is not a valid Store package name."
    }

    # Identity/Publisher is an X.500 distinguished name, not a display string. Catching the
    # common "just typed the company name" mistake here beats a MakeAppx error later.
    if ($resolved.Publisher -notmatch '^\s*[A-Za-z]+\s*=') {
        throw "Package publisher '$($resolved.Publisher)' must be a distinguished name, for example 'CN=...'."
    }

    # Non-fatal on purpose: a placeholder identity still produces a package that is useful
    # for sideload validation and for exercising this script in CI.
    $usesPlaceholder = [bool] (Get-JsonValue $identity 'isPlaceholder')
    $identityIsOverridden = $sources.IdentityName.IsOverride -and $sources.Publisher.IsOverride -and $sources.PublisherDisplayName.IsOverride
    if ($usesPlaceholder -and -not $identityIsOverridden) {
        Write-Warning "Building with the placeholder Store identity from $identityPath. Partner Center will reject this package; see docs/microsoft-store-package.md."
    }

    return $resolved
}

function Find-WindowsSdkTool {
    param([string] $ToolName, [string] $ExplicitPath, [string] $EnvironmentVariable)

    if ($ExplicitPath) {
        return Resolve-ExistingPath -Path $ExplicitPath -Description $ToolName
    }

    $fromEnvironment = [Environment]::GetEnvironmentVariable($EnvironmentVariable)
    if ($fromEnvironment) {
        return Resolve-ExistingPath -Path $fromEnvironment -Description $ToolName
    }

    $onPath = Get-Command $ToolName -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($onPath) {
        return $onPath.Source
    }

    $architectureFolder = if ($Architecture -eq 'x86') { 'x86' } else { 'x64' }
    $searchRoots = @(
        [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
        [Environment]::GetEnvironmentVariable('ProgramFiles')
    ) | Where-Object { $_ }

    # Windows SDK build tools are versioned per directory; take the newest one.
    $candidate = foreach ($root in $searchRoots) {
        $binRoot = Join-Path $root 'Windows Kits/10/bin'
        if (-not (Test-Path -LiteralPath $binRoot)) { continue }

        Get-ChildItem -LiteralPath $binRoot -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match '^10\.' } |
            Sort-Object { [version] ($_.Name -replace '^(\d+\.\d+\.\d+\.\d+).*$', '$1') } -Descending |
            ForEach-Object { Join-Path $_.FullName "$architectureFolder/$ToolName" } |
            Where-Object { Test-Path -LiteralPath $_ }
    }

    $found = $candidate | Select-Object -First 1
    if (-not $found) {
        throw "$ToolName was not found on PATH or under the installed Windows Kits. Install the Windows 10/11 SDK (MSIX packaging tools), set $EnvironmentVariable, or pass an explicit path. See docs/microsoft-store-package.md."
    }

    return $found
}

function Invoke-Tool {
    param([string] $FilePath, [string[]] $ToolArgument)

    & $FilePath @ToolArgument
    if ($LASTEXITCODE -ne 0) {
        throw "$(Split-Path -Leaf $FilePath) failed with exit code $LASTEXITCODE."
    }
}

# Compress-Archive only writes .zip, so every non-.zip container here is built as a zip and
# then renamed. .appxsym and .msixupload are both plain zips by definition.
function New-RenamedArchive {
    param([string[]] $SourcePath, [string] $DestinationPath)

    $stagingZip = "$DestinationPath.zip"
    Remove-Item -LiteralPath $stagingZip, $DestinationPath -Force -ErrorAction SilentlyContinue
    Compress-Archive -LiteralPath $SourcePath -DestinationPath $stagingZip -CompressionLevel Optimal -Force
    Move-Item -LiteralPath $stagingZip -Destination $DestinationPath -Force

    return $DestinationPath
}

$publishPath = Resolve-ExistingPath -Path $PublishDirectory -Description 'Publish directory'
$templatePath = Resolve-ExistingPath -Path $ManifestTemplate -Description 'Manifest template'
$assetsPath = Resolve-ExistingPath -Path $AssetsDirectory -Description 'MSIX assets directory'

if (-not (Test-Path -LiteralPath (Join-Path $publishPath $ExecutableName))) {
    throw "Published application executable not found: $(Join-Path $publishPath $ExecutableName)"
}

$packageVersion = Resolve-PackageVersion -Value $Version
$identity = Resolve-StoreIdentity

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$outputPath = (Resolve-Path -LiteralPath $OutputDirectory).Path

$stagingPath = Join-Path $outputPath "msix-layout-$Architecture"
Remove-Item -LiteralPath $stagingPath -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $stagingPath | Out-Null

Copy-Item -Path (Join-Path $publishPath '*') -Destination $stagingPath -Recurse -Force
Copy-Item -LiteralPath $assetsPath -Destination $stagingPath -Recurse -Force

# Symbols ride along in the .msixupload for Partner Center crash analytics, never inside the
# package the user installs.
$symbolFiles = @(Get-ChildItem -LiteralPath $stagingPath -Filter '*.pdb' -Recurse -File)
$symbolStaging = Join-Path $outputPath "msix-symbols-$Architecture"
Remove-Item -LiteralPath $symbolStaging -Recurse -Force -ErrorAction SilentlyContinue
if ($symbolFiles.Count -gt 0) {
    New-Item -ItemType Directory -Force -Path $symbolStaging | Out-Null
    foreach ($symbolFile in $symbolFiles) {
        Move-Item -LiteralPath $symbolFile.FullName -Destination (Join-Path $symbolStaging $symbolFile.Name) -Force
    }
}

$manifestTokens = @{
    '{{IdentityName}}' = $identity.IdentityName
    '{{Publisher}}' = $identity.Publisher
    '{{Version}}' = $packageVersion
    '{{ProcessorArchitecture}}' = $Architecture
    '{{DisplayName}}' = $identity.DisplayName
    '{{PublisherDisplayName}}' = $identity.PublisherDisplayName
    '{{Description}}' = $identity.Description
    '{{ExecutableName}}' = $ExecutableName
}

$manifest = Get-Content -Raw -LiteralPath $templatePath
foreach ($token in $manifestTokens.GetEnumerator()) {
    # The values land in XML attributes, so they have to be escaped rather than pasted.
    $manifest = $manifest.Replace($token.Key, [System.Security.SecurityElement]::Escape($token.Value))
}

$unresolvedTokens = @([regex]::Matches($manifest, '\{\{[A-Za-z]+\}\}') | ForEach-Object { $_.Value } | Sort-Object -Unique)
if ($unresolvedTokens.Count -gt 0) {
    throw "Manifest template has unresolved placeholders: $($unresolvedTokens -join ', ')"
}

$manifestPath = Join-Path $stagingPath 'AppxManifest.xml'
# MakeAppx and the Windows package parser both want UTF-8 without a BOM.
[System.IO.File]::WriteAllText($manifestPath, $manifest, [System.Text.UTF8Encoding]::new($false))

$packageBase = "$PackageBaseName-win-$Architecture-$Version"
$msixPath = Join-Path $outputPath "$packageBase.msix"
Remove-Item -LiteralPath $msixPath -Force -ErrorAction SilentlyContinue

$makeAppx = Find-WindowsSdkTool -ToolName 'makeappx.exe' -ExplicitPath $MakeAppxPath -EnvironmentVariable 'MAKEAPPX_PATH'
Invoke-Tool -FilePath $makeAppx -ToolArgument @('pack', '/o', '/d', $stagingPath, '/p', $msixPath)

if (-not (Test-Path -LiteralPath $msixPath)) {
    throw "MakeAppx reported success but produced no package: $msixPath"
}

if ($CertificatePath) {
    $certificatePath = Resolve-ExistingPath -Path $CertificatePath -Description 'Signing certificate'
    $signTool = Find-WindowsSdkTool -ToolName 'signtool.exe' -ExplicitPath $SignToolPath -EnvironmentVariable 'SIGNTOOL_PATH'

    $signArguments = @('sign', '/fd', 'SHA256', '/f', $certificatePath)
    if ($CertificatePassword) { $signArguments += @('/p', $CertificatePassword) }
    if ($TimestampUrl) { $signArguments += @('/tr', $TimestampUrl, '/td', 'SHA256') }
    $signArguments += $msixPath

    Invoke-Tool -FilePath $signTool -ToolArgument $signArguments
}

$symbolPath = $null
$uploadPath = $null
if (-not $SkipUploadPackage) {
    $uploadContents = @($msixPath)

    if ($symbolFiles.Count -gt 0) {
        $symbolPath = New-RenamedArchive `
            -SourcePath (Get-ChildItem -LiteralPath $symbolStaging -File | Select-Object -ExpandProperty FullName) `
            -DestinationPath (Join-Path $outputPath "$packageBase.appxsym")
        $uploadContents += $symbolPath
    }

    $uploadPath = New-RenamedArchive -SourcePath $uploadContents -DestinationPath (Join-Path $outputPath "$packageBase.msixupload")
}

Remove-Item -LiteralPath $stagingPath -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $symbolStaging -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "Built MSIX package $($identity.IdentityName) $packageVersion ($Architecture)"
Write-Host "  package: $msixPath"
if ($symbolPath) { Write-Host "  symbols: $symbolPath" }
if ($uploadPath) { Write-Host "  upload:  $uploadPath" }
if (-not $CertificatePath) { Write-Host '  signature: none (the Microsoft Store signs the package it ingests)' }

[pscustomobject]@{
    PackagePath = $msixPath
    UploadPath = $uploadPath
    SymbolPath = $symbolPath
    PackageVersion = $packageVersion
    IdentityName = $identity.IdentityName
    Publisher = $identity.Publisher
    Architecture = $Architecture
}
