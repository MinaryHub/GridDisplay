<#
.SYNOPSIS
    Publishes GridDisplay and packs it into a signed MSIX plus its .appinstaller update feed.

.DESCRIPTION
    One command from source to something installable. The version comes from <Version> in
    GridDisplay.csproj and is stamped into the staged manifest and the feed, so the package version,
    the binary's version and the version the app reports can never drift apart — bump it in the
    csproj and nowhere else.

    Requires the Windows 10/11 SDK for makeappx.exe and signtool.exe. If neither is found the script
    says so and stops instead of producing half an artifact.

.PARAMETER BaseUri
    Where the .msix and .appinstaller will be published, without a trailing slash. Baked into the
    feed, so it has to be the address the wall can actually reach — a plain file share (\\server\share
    or file:///) works as well as https.

.PARAMETER Publisher
    Subject of the signing certificate. Must match it exactly, or Windows rejects the package.

.PARAMETER CertificateThumbprint
    Signs with a certificate already in the current user's store. Preferred on a build machine.

.PARAMETER CertificatePath
    Signs with a .pfx file instead. Use -CertificatePassword with it.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File packaging\build-msix.ps1 `
        -BaseUri 'https://intranet.example/griddisplay' -CertificatePath .\GridDisplay.pfx -CertificatePassword 'pw'

.EXAMPLE
    # Unsigned, just to see that the package builds. Cannot be installed as-is.
    powershell -ExecutionPolicy Bypass -File packaging\build-msix.ps1
#>
[CmdletBinding()]
param(
    [string] $BaseUri = 'https://example.invalid/griddisplay',
    [string] $Publisher = 'CN=SMIC',
    [string] $IdentityName = 'SMIC.GridDisplay',
    [string] $Version,
    [string] $Configuration = 'Release',
    [string] $CertificateThumbprint,
    [string] $CertificatePath,
    [string] $CertificatePassword,
    [string] $TimestampUrl = 'http://timestamp.digicert.com',
    [string] $OutputDirectory,
    [switch] $SkipPublish
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot 'src\GridDisplay\GridDisplay.csproj'
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repoRoot 'publish\msix' }
$stageDirectory = Join-Path $repoRoot 'publish\msix-stage'

# ---------------------------------------------------------------- version

if (-not $Version) {
    $csproj = [xml](Get-Content -LiteralPath $projectPath -Raw)
    $node = $csproj.SelectSingleNode('//PropertyGroup/Version')
    if (-not $node) { throw "No <Version> in $projectPath. Add one, or pass -Version." }
    $Version = $node.InnerText.Trim()
}
# MSIX versions are always four parts. Revision stays 0: the Store reserves it, and keeping it 0
# for sideloaded builds too means the same number can be published either way later.
$packageVersion = $Version
while (($packageVersion -split '\.').Count -lt 4) { $packageVersion += '.0' }
Write-Host "Version         $packageVersion"
Write-Host "Publisher       $Publisher"
Write-Host "Base URI        $BaseUri"

# ---------------------------------------------------------------- SDK tools

function Find-SdkTool {
    param([string] $Name)

    $roots = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'),
        (Join-Path $env:ProgramFiles 'Windows Kits\10\bin')
    ) | Where-Object { $_ -and (Test-Path $_) }

    foreach ($root in $roots) {
        # Newest SDK first, then the unversioned layout older installs use.
        $versioned = Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match '^10\.' } |
            Sort-Object { [version]($_.Name) } -Descending
        foreach ($dir in $versioned) {
            $candidate = Join-Path $dir.FullName "x64\$Name"
            if (Test-Path $candidate) { return $candidate }
        }
        $flat = Join-Path $root "x64\$Name"
        if (Test-Path $flat) { return $flat }
    }

    $onPath = Get-Command $Name -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    return $null
}

$makeAppx = Find-SdkTool 'makeappx.exe'
if (-not $makeAppx) {
    throw @'
makeappx.exe not found. Install the Windows SDK, either of:
  winget install --id Microsoft.WindowsSDK.10.0.22621
  https://developer.microsoft.com/windows/downloads/windows-sdk/
Only the "MSIX Packaging Tools" / "Windows SDK Signing Tools" components are needed.
'@
}
Write-Host "makeappx        $makeAppx"

# ---------------------------------------------------------------- publish

if (-not $SkipPublish) {
    Write-Host "`nPublishing $Configuration ..."
    if (Test-Path $stageDirectory) { Remove-Item -LiteralPath $stageDirectory -Recurse -Force }
    & dotnet publish $projectPath -c $Configuration -r win-x64 --self-contained false `
        -p:PublishSingleFile=false -p:Version=$Version -o $stageDirectory
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }
} elseif (-not (Test-Path $stageDirectory)) {
    throw "-SkipPublish was given but $stageDirectory does not exist yet."
}

# The publish output carries a .pdb and the WPF flavour of WebView2 that this app never loads.
# Neither belongs in a package a wall downloads over a slow link.
Get-ChildItem -LiteralPath $stageDirectory -Filter '*.pdb' -Recurse | Remove-Item -Force

# ---------------------------------------------------------------- manifest + assets

Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Assets') -Destination (Join-Path $stageDirectory 'Assets') -Recurse -Force
# The exe icon lives in Assets for the packaging step; it is not a package asset itself.
Remove-Item -LiteralPath (Join-Path $stageDirectory 'Assets\GridDisplay.ico') -Force -ErrorAction SilentlyContinue

$manifestPath = Join-Path $stageDirectory 'AppxManifest.xml'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'AppxManifest.xml') -Destination $manifestPath -Force

$manifest = [xml](Get-Content -LiteralPath $manifestPath -Raw)
$identity = $manifest.Package.Identity
$identity.SetAttribute('Version', $packageVersion)
$identity.SetAttribute('Publisher', $Publisher)
$identity.SetAttribute('Name', $IdentityName)
$manifest.Save($manifestPath)
Write-Host "Stamped         $manifestPath"

# ---------------------------------------------------------------- pack

if (-not (Test-Path $OutputDirectory)) { New-Item -ItemType Directory -Path $OutputDirectory | Out-Null }
$msixName = "GridDisplay_${packageVersion}_x64.msix"
$msixPath = Join-Path $OutputDirectory $msixName

Write-Host "`nPacking $msixName ..."
& $makeAppx pack /d $stageDirectory /p $msixPath /o
if ($LASTEXITCODE -ne 0) { throw "makeappx failed ($LASTEXITCODE)." }

# ---------------------------------------------------------------- sign

$signTool = Find-SdkTool 'signtool.exe'
if ($CertificateThumbprint -or $CertificatePath) {
    if (-not $signTool) { throw 'signtool.exe not found, but a certificate was given.' }

    $signArgs = @('sign', '/fd', 'SHA256')
    if ($CertificateThumbprint) {
        $signArgs += @('/sha1', $CertificateThumbprint)
    } else {
        if (-not (Test-Path $CertificatePath)) { throw "Certificate not found: $CertificatePath" }
        $signArgs += @('/f', (Resolve-Path $CertificatePath).Path)
        if ($CertificatePassword) { $signArgs += @('/p', $CertificatePassword) }
    }
    if ($TimestampUrl) { $signArgs += @('/tr', $TimestampUrl, '/td', 'SHA256') }
    $signArgs += $msixPath

    Write-Host "`nSigning ..."
    & $signTool @signArgs
    if ($LASTEXITCODE -ne 0) {
        # The overwhelmingly common cause, and not obvious from signtool's own message.
        throw "signtool failed ($LASTEXITCODE). Check that the certificate subject matches -Publisher exactly ($Publisher)."
    }
} else {
    Write-Warning 'Unsigned package. Windows will not install it. Sign it with -CertificateThumbprint or -CertificatePath (see New-DevCertificate.ps1).'
}

# ---------------------------------------------------------------- update feed

$templatePath = Join-Path $PSScriptRoot 'GridDisplay.appinstaller.template'
$feed = Get-Content -LiteralPath $templatePath -Raw
$feed = $feed.Replace('{{VERSION}}', $packageVersion).
              Replace('{{BASEURI}}', $BaseUri.TrimEnd('/')).
              Replace('{{IDENTITYNAME}}', $IdentityName).
              Replace('{{PUBLISHER}}', $Publisher).
              Replace('{{MSIXNAME}}', $msixName)
$feedPath = Join-Path $OutputDirectory 'GridDisplay.appinstaller'
# UTF-8 without a BOM: App Installer rejects a feed that starts with one.
[System.IO.File]::WriteAllText($feedPath, $feed, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "`nDone."
Write-Host "  package  $msixPath"
Write-Host "  feed     $feedPath"
Write-Host @"

Next:
  1. Copy both files to $BaseUri (same folder, and keep older .msix files there so machines
     that are behind can still find the version they are updating from).
  2. First install on a wall: run GridDisplay.appinstaller (the certificate must be trusted first
     - see packaging\README.md).
  3. Later releases: bump <Version> in GridDisplay.csproj, re-run this script, copy both files up.
     Walls pick it up on the next launch or within ~8 hours, and the in-app dialog can force it.
"@
