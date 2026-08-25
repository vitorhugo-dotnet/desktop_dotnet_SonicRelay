<#
.SYNOPSIS
    Behavioural tests for packaging/windows/Build-MsixPackage.ps1.

.DESCRIPTION
    Drives the Store packaging script against a fake MakeAppx, so everything that decides
    whether Partner Center accepts the upload - the rendered AppxManifest.xml, the package
    version rules, the staged layout, the symbol/upload containers - is verified on every
    OS in the CI matrix, not only on the runner that owns the Windows SDK.

    The fake is a .ps1 rather than a shell script so these run on Windows too, where the
    real packaging happens.
#>

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$buildScript = Join-Path $root 'packaging/windows/Build-MsixPackage.ps1'

if (-not (Test-Path -LiteralPath $buildScript)) {
    Write-Error "Store packaging script not found: $buildScript"
}

$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) "sonicrelay-msix-tests-$([guid]::NewGuid().ToString('n'))"
New-Item -ItemType Directory -Force -Path $testRoot | Out-Null

$failures = [System.Collections.Generic.List[string]]::new()
$assertions = 0

function Assert-True {
    param([string] $Because, [bool] $Condition)

    $script:assertions++
    if (-not $Condition) {
        $script:failures.Add($Because)
    }
}

function Assert-Contains {
    param([string] $Because, [string] $Haystack, [string] $Needle)

    Assert-True -Because "$Because (expected to find '$Needle')" -Condition $Haystack.Contains($Needle)
}

# MakeAppx stand-in: records how it was called, snapshots the layout it was handed, and
# produces the package file the build script then expects to exist.
$fakeMakeAppx = Join-Path $testRoot 'fake-makeappx.exe.ps1'
@'
$ErrorActionPreference = 'Stop'

$sourceDirectory = $null
$packagePath = $null
for ($index = 0; $index -lt $args.Count; $index++) {
    switch ($args[$index]) {
        '/d' { $sourceDirectory = $args[$index + 1] }
        '/p' { $packagePath = $args[$index + 1] }
    }
}

Set-Content -LiteralPath $env:FAKE_MAKEAPPX_ARGS -Value ($args -join ' ')

$layout = Get-ChildItem -LiteralPath $sourceDirectory -Recurse -File |
    ForEach-Object { $_.FullName.Substring($sourceDirectory.Length).TrimStart('/', '\').Replace('\', '/') }
Set-Content -LiteralPath $env:FAKE_MAKEAPPX_LAYOUT -Value ($layout | Sort-Object)

Copy-Item -LiteralPath (Join-Path $sourceDirectory 'AppxManifest.xml') -Destination $env:FAKE_MAKEAPPX_MANIFEST -Force

Set-Content -LiteralPath $packagePath -Value 'fake msix payload'
exit 0
'@ | Set-Content -LiteralPath $fakeMakeAppx -Encoding UTF8

# A published win-x64 layout: the executable the manifest points at, a satellite-resource
# subdirectory (which must survive staging), and symbols (which must not ship inside the package).
function New-PublishDirectory {
    param([string] $Name = 'publish', [switch] $WithoutExecutable, [switch] $WithoutSymbols)

    $publishDirectory = Join-Path $testRoot $Name
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path (Join-Path $publishDirectory 'pt-BR') | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $publishDirectory 'runtimes/win-x64/native') | Out-Null

    if (-not $WithoutExecutable) {
        Set-Content -LiteralPath (Join-Path $publishDirectory 'SonicRelay.Windows.Desktop.exe') -Value 'MZ'
    }

    Set-Content -LiteralPath (Join-Path $publishDirectory 'SonicRelay.Windows.Core.dll') -Value 'MZ'
    Set-Content -LiteralPath (Join-Path $publishDirectory 'pt-BR/SonicRelay.Windows.Core.resources.dll') -Value 'MZ'
    Set-Content -LiteralPath (Join-Path $publishDirectory 'runtimes/win-x64/native/opus.dll') -Value 'MZ'

    if (-not $WithoutSymbols) {
        Set-Content -LiteralPath (Join-Path $publishDirectory 'SonicRelay.Windows.Desktop.pdb') -Value 'PDB'
        Set-Content -LiteralPath (Join-Path $publishDirectory 'SonicRelay.Windows.Core.pdb') -Value 'PDB'
    }

    return $publishDirectory
}

$identityEnvironmentVariables = @(
    'MSIX_IDENTITY_NAME'
    'MSIX_PUBLISHER'
    'MSIX_PUBLISHER_DISPLAY_NAME'
    'MSIX_DISPLAY_NAME'
    'MSIX_DESCRIPTION'
)

function Invoke-BuildScript {
    param([hashtable] $Parameter, [string] $OutputName, [hashtable] $Environment = @{})

    $outputDirectory = Join-Path $testRoot $OutputName
    Remove-Item -LiteralPath $outputDirectory -Recurse -Force -ErrorAction SilentlyContinue

    # Each case starts from a clean identity environment; otherwise one case's override
    # would silently decide the next one's manifest.
    foreach ($name in $identityEnvironmentVariables) {
        [Environment]::SetEnvironmentVariable($name, $Environment[$name])
    }

    $env:FAKE_MAKEAPPX_ARGS = Join-Path $testRoot "$OutputName.args.txt"
    $env:FAKE_MAKEAPPX_LAYOUT = Join-Path $testRoot "$OutputName.layout.txt"
    $env:FAKE_MAKEAPPX_MANIFEST = Join-Path $testRoot "$OutputName.AppxManifest.xml"
    $warningFile = Join-Path $testRoot "$OutputName.warnings.txt"

    $arguments = @{
        OutputDirectory = $outputDirectory
        MakeAppxPath = $fakeMakeAppx
    } + $Parameter

    $result = & $buildScript @arguments 3> $warningFile
    return [pscustomobject]@{
        Result = $result
        OutputDirectory = $outputDirectory
        Manifest = $env:FAKE_MAKEAPPX_MANIFEST
        Layout = $env:FAKE_MAKEAPPX_LAYOUT
        Arguments = $env:FAKE_MAKEAPPX_ARGS
        Warnings = "$(Get-Content -Raw -LiteralPath $warningFile -ErrorAction SilentlyContinue)"
    }
}

function Get-BuildError {
    param([hashtable] $Parameter, [string] $OutputName, [hashtable] $Environment = @{})

    try {
        Invoke-BuildScript -Parameter $Parameter -OutputName $OutputName -Environment $Environment | Out-Null
    }
    catch {
        return $_.Exception.Message
    }

    return $null
}

try {
    # --- The default build: repository identity, three-part version ---------------------
    $publishDirectory = New-PublishDirectory
    $build = Invoke-BuildScript -OutputName 'default' -Parameter @{
        PublishDirectory = $publishDirectory
        Version = '1.4.2'
    }

    $manifest = [xml] (Get-Content -Raw -LiteralPath $build.Manifest)
    $namespaceManager = [System.Xml.XmlNamespaceManager]::new($manifest.NameTable)
    $namespaceManager.AddNamespace('m', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
    $namespaceManager.AddNamespace('rescap', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities')

    $identityNode = $manifest.SelectSingleNode('/m:Package/m:Identity', $namespaceManager)
    Assert-True -Because 'a three-part version must be padded to the four-part package version' `
        -Condition ($identityNode.GetAttribute('Version') -eq '1.4.2.0')
    Assert-True -Because 'the package must declare the x64 processor architecture by default' `
        -Condition ($identityNode.GetAttribute('ProcessorArchitecture') -eq 'x64')

    $storeIdentity = Get-Content -Raw -LiteralPath (Join-Path $root 'packaging/windows/msix/store-identity.json') | ConvertFrom-Json
    Assert-True -Because 'Identity/Name must match store-identity.json' `
        -Condition ($identityNode.GetAttribute('Name') -eq $storeIdentity.identityName)
    Assert-True -Because 'Identity/Publisher must match store-identity.json' `
        -Condition ($identityNode.GetAttribute('Publisher') -eq $storeIdentity.publisher)

    $targetDeviceFamily = $manifest.SelectSingleNode('/m:Package/m:Dependencies/m:TargetDeviceFamily', $namespaceManager)
    Assert-True -Because 'the package must target Windows.Desktop, not the universal device family' `
        -Condition ($targetDeviceFamily.GetAttribute('Name') -eq 'Windows.Desktop')

    $application = $manifest.SelectSingleNode('/m:Package/m:Applications/m:Application', $namespaceManager)
    Assert-True -Because 'a full-trust Win32 app must enter through Windows.FullTrustApplication' `
        -Condition ($application.GetAttribute('EntryPoint') -eq 'Windows.FullTrustApplication')
    Assert-True -Because 'the manifest must point at the published executable' `
        -Condition ($application.GetAttribute('Executable') -eq 'SonicRelay.Windows.Desktop.exe')

    $capabilities = @($manifest.SelectNodes('/m:Package/m:Capabilities/*', $namespaceManager) |
        ForEach-Object { $_.GetAttribute('Name') })
    Assert-True -Because "runFullTrust must be the only declared capability, got: $($capabilities -join ', ')" `
        -Condition (($capabilities.Count -eq 1) -and ($capabilities[0] -eq 'runFullTrust'))
    Assert-True -Because 'SonicRelay never opens a capture endpoint, so it must not ask for the microphone capability' `
        -Condition (-not ($capabilities -contains 'microphone'))

    $layout = Get-Content -LiteralPath $build.Layout
    Assert-True -Because 'the published executable must be packed at the package root' `
        -Condition ($layout -contains 'SonicRelay.Windows.Desktop.exe')
    Assert-True -Because 'satellite resource subdirectories must keep their structure in the package layout' `
        -Condition ($layout -contains 'pt-BR/SonicRelay.Windows.Core.resources.dll')
    Assert-True -Because 'deeply nested publish subdirectories must keep their structure in the package layout' `
        -Condition ($layout -contains 'runtimes/win-x64/native/opus.dll')

    # Anything the publish produced other than symbols has to reach the package: a staging
    # step that quietly drops files would still pack, install and fail only at runtime.
    $expectedFromPublish = @(Get-ChildItem -LiteralPath $publishDirectory -Recurse -File |
        Where-Object { $_.Extension -ne '.pdb' })
    $stagedFromPublish = @($layout | Where-Object { -not $_.StartsWith('Assets/') -and $_ -ne 'AppxManifest.xml' })
    Assert-True -Because "every published file must be packed, expected $($expectedFromPublish.Count) got $($stagedFromPublish.Count)" `
        -Condition ($stagedFromPublish.Count -eq $expectedFromPublish.Count)
    Assert-True -Because 'the Store logo must be packed for Package/Properties/Logo' `
        -Condition ($layout -contains 'Assets/StoreLogo.png')
    Assert-True -Because 'symbols must not ship inside the installed package' `
        -Condition (-not ($layout | Where-Object { $_.EndsWith('.pdb') }))

    Assert-Contains -Because 'building on the committed placeholder identity must warn that Partner Center will reject it' `
        -Haystack $build.Warnings -Needle 'placeholder Store identity'

    $makeAppxArguments = Get-Content -Raw -LiteralPath $build.Arguments
    Assert-Contains -Because 'MakeAppx must be run in pack mode' -Haystack $makeAppxArguments -Needle 'pack'

    Assert-True -Because 'the .msix must be produced with the repository asset naming convention' `
        -Condition (Test-Path -LiteralPath (Join-Path $build.OutputDirectory 'SonicRelay.WindowsPublisher-win-x64-1.4.2.msix'))
    Assert-True -Because 'a .msixupload must be produced for the Partner Center Packages page' `
        -Condition (Test-Path -LiteralPath (Join-Path $build.OutputDirectory 'SonicRelay.WindowsPublisher-win-x64-1.4.2.msixupload'))
    Assert-True -Because 'symbols must be bundled as .appxsym for crash analytics' `
        -Condition (Test-Path -LiteralPath (Join-Path $build.OutputDirectory 'SonicRelay.WindowsPublisher-win-x64-1.4.2.appxsym'))
    Assert-True -Because 'the staging layout must not be left behind in the output directory' `
        -Condition (-not (Test-Path -LiteralPath (Join-Path $build.OutputDirectory 'msix-layout-x64')))
    Assert-True -Because 'the symbol staging directory must not be left behind either' `
        -Condition (-not (Test-Path -LiteralPath (Join-Path $build.OutputDirectory 'msix-symbols-x64')))

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $uploadEntries = @()
    $upload = [System.IO.Compression.ZipFile]::OpenRead((Join-Path $build.OutputDirectory 'SonicRelay.WindowsPublisher-win-x64-1.4.2.msixupload'))
    try {
        $uploadEntries = @($upload.Entries | ForEach-Object { $_.Name })
    }
    finally {
        $upload.Dispose()
    }
    Assert-True -Because 'the .msixupload must contain the package itself' `
        -Condition ($uploadEntries -contains 'SonicRelay.WindowsPublisher-win-x64-1.4.2.msix')
    Assert-True -Because 'the .msixupload must contain the symbol bundle' `
        -Condition ($uploadEntries -contains 'SonicRelay.WindowsPublisher-win-x64-1.4.2.appxsym')

    Assert-True -Because 'the build script must report the package path it produced' `
        -Condition ($build.Result.PackagePath.EndsWith('SonicRelay.WindowsPublisher-win-x64-1.4.2.msix'))
    Assert-True -Because 'the build script must report the normalised package version' `
        -Condition ($build.Result.PackageVersion -eq '1.4.2.0')

    # --- Partner Center identity overrides ----------------------------------------------
    $override = Invoke-BuildScript -OutputName 'override' -Parameter @{
        PublishDirectory = $publishDirectory
        Version = '2.0.1.0'
        IdentityName = '41968SonicRelay.SonicRelay'
        Publisher = 'CN=1F0A6B2C-9D4E-4A31-8F55-7C2E9B0D1A34'
        PublisherDisplayName = 'Vitor Hugo Alves Ferreira & Co'
        DisplayName = 'SonicRelay'
    }

    $overrideManifest = [xml] (Get-Content -Raw -LiteralPath $override.Manifest)
    $overrideIdentity = $overrideManifest.SelectSingleNode('/m:Package/m:Identity', $namespaceManager)
    Assert-True -Because 'an explicit -IdentityName must win over the repository identity file' `
        -Condition ($overrideIdentity.GetAttribute('Name') -eq '41968SonicRelay.SonicRelay')
    Assert-True -Because 'an explicit -Publisher must win over the repository identity file' `
        -Condition ($overrideIdentity.GetAttribute('Publisher') -eq 'CN=1F0A6B2C-9D4E-4A31-8F55-7C2E9B0D1A34')

    $overridePublisherDisplayName = $overrideManifest.SelectSingleNode('/m:Package/m:Properties/m:PublisherDisplayName', $namespaceManager)
    Assert-True -Because 'an ampersand in a Partner Center display name must be XML-escaped, not pasted into the manifest' `
        -Condition ($overridePublisherDisplayName.InnerText -eq 'Vitor Hugo Alves Ferreira & Co')
    Assert-True -Because 'an already four-part version must be preserved' `
        -Condition ($overrideIdentity.GetAttribute('Version') -eq '2.0.1.0')
    Assert-True -Because 'a fully overridden identity must not warn about the placeholder' `
        -Condition (-not $override.Warnings.Contains('placeholder Store identity'))

    # --- Partner Center identity carried as CI repository variables ----------------------
    $fromEnvironment = Invoke-BuildScript -OutputName 'environment' -Environment @{
        MSIX_IDENTITY_NAME = '41968SonicRelay.SonicRelayFromCi'
        MSIX_PUBLISHER = 'CN=9E7D5C31-0B84-4D2A-A6F1-3C8E2B7A5D40'
        MSIX_PUBLISHER_DISPLAY_NAME = 'SonicRelay from CI'
    } -Parameter @{
        PublishDirectory = $publishDirectory
        Version = '3.1.0'
    }

    $environmentManifest = [xml] (Get-Content -Raw -LiteralPath $fromEnvironment.Manifest)
    $environmentIdentity = $environmentManifest.SelectSingleNode('/m:Package/m:Identity', $namespaceManager)
    Assert-True -Because 'MSIX_IDENTITY_NAME must override the repository identity file' `
        -Condition ($environmentIdentity.GetAttribute('Name') -eq '41968SonicRelay.SonicRelayFromCi')
    Assert-True -Because 'MSIX_PUBLISHER must override the repository identity file' `
        -Condition ($environmentIdentity.GetAttribute('Publisher') -eq 'CN=9E7D5C31-0B84-4D2A-A6F1-3C8E2B7A5D40')
    Assert-True -Because 'a fully overridden identity from the environment must not warn about the placeholder' `
        -Condition (-not $fromEnvironment.Warnings.Contains('placeholder Store identity'))

    $environmentDisplayName = $environmentManifest.SelectSingleNode('/m:Package/m:Properties/m:DisplayName', $namespaceManager)
    Assert-True -Because 'a value with no override must still come from the repository identity file' `
        -Condition ($environmentDisplayName.InnerText -eq $storeIdentity.displayName)

    # --- Opting out of the upload container ---------------------------------------------
    $packageOnly = Invoke-BuildScript -OutputName 'package-only' -Parameter @{
        PublishDirectory = $publishDirectory
        Version = '1.0.0'
        SkipUploadPackage = $true
    }
    Assert-True -Because '-SkipUploadPackage must still produce the .msix' `
        -Condition (Test-Path -LiteralPath (Join-Path $packageOnly.OutputDirectory 'SonicRelay.WindowsPublisher-win-x64-1.0.0.msix'))
    Assert-True -Because '-SkipUploadPackage must not produce a .msixupload' `
        -Condition (-not (Test-Path -LiteralPath (Join-Path $packageOnly.OutputDirectory 'SonicRelay.WindowsPublisher-win-x64-1.0.0.msixupload')))

    # --- A build with no symbols still uploads ------------------------------------------
    $noSymbols = Invoke-BuildScript -OutputName 'no-symbols' -Parameter @{
        PublishDirectory = New-PublishDirectory -Name 'publish-embedded-symbols' -WithoutSymbols
        Version = '1.0.0'
    }
    Assert-True -Because 'a publish with embedded symbols must still produce a .msixupload' `
        -Condition (Test-Path -LiteralPath (Join-Path $noSymbols.OutputDirectory 'SonicRelay.WindowsPublisher-win-x64-1.0.0.msixupload'))
    Assert-True -Because 'no .appxsym must be invented when the publish carries no .pdb files' `
        -Condition (-not (Test-Path -LiteralPath (Join-Path $noSymbols.OutputDirectory 'SonicRelay.WindowsPublisher-win-x64-1.0.0.appxsym')))

    # --- Versions Partner Center refuses --------------------------------------------------
    $rejectedVersions = [ordered]@{
        '1.2.3.4' = 'revision'
        '0.0.0' = '0.0.0.0'
        '1.2' = 'Major.Minor.Build'
        '1.2.3.4.5' = 'Major.Minor.Build'
        '1.2.beta' = 'digits'
        '1.2.70000' = '0-65535'
    }
    foreach ($rejected in $rejectedVersions.GetEnumerator()) {
        $message = Get-BuildError -OutputName "reject-$($rejected.Key -replace '\.', '-')" -Parameter @{
            PublishDirectory = $publishDirectory
            Version = $rejected.Key
        }

        Assert-True -Because "version '$($rejected.Key)' must be rejected, got: $message" `
            -Condition ($null -ne $message -and $message.Contains($rejected.Value))
    }

    # --- Identity values Partner Center refuses -------------------------------------------
    $publisherMessage = Get-BuildError -OutputName 'reject-publisher' -Parameter @{
        PublishDirectory = $publishDirectory
        Version = '1.0.0'
        Publisher = 'SonicRelay'
    }
    Assert-True -Because "a publisher that is not a distinguished name must be rejected, got: $publisherMessage" `
        -Condition ($null -ne $publisherMessage -and $publisherMessage.Contains('distinguished name'))

    $identityNameMessage = Get-BuildError -OutputName 'reject-identity-name' -Parameter @{
        PublishDirectory = $publishDirectory
        Version = '1.0.0'
        IdentityName = 'Sonic Relay!'
    }
    Assert-True -Because "an invalid package identity name must be rejected, got: $identityNameMessage" `
        -Condition ($null -ne $identityNameMessage -and $identityNameMessage.Contains('valid Store package name'))

    # --- A publish that is not the app ----------------------------------------------------
    $missingExecutableMessage = Get-BuildError -OutputName 'reject-missing-exe' -Parameter @{
        PublishDirectory = New-PublishDirectory -Name 'publish-without-exe' -WithoutExecutable
        Version = '1.0.0'
    }
    Assert-True -Because "a publish directory without the application executable must be rejected, got: $missingExecutableMessage" `
        -Condition ($null -ne $missingExecutableMessage -and $missingExecutableMessage.Contains('executable not found'))
}
finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count -gt 0) {
    Write-Error "Build-MsixPackage.ps1 failures:`n$($failures -join "`n")"
}

Write-Host "Build-MsixPackage.ps1 verified: $assertions assertions passed."
