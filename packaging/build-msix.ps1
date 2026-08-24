<#
.SYNOPSIS
    Publishes GridDisplay and packs it into an MSIX - either a signed package plus its .appinstaller
    update feed for sideloading, or a .msixupload for Microsoft Store.

.DESCRIPTION
    One command from source to something installable. The version comes from <Version> in
    GridDisplay.csproj and is stamped into the staged manifest and the feed, so the package version,
    the binary's version and the version the app reports can never drift apart - bump it in the
    csproj and nowhere else.

    The two modes differ in more than the file extension:

      sideload (default)  Framework-dependent: smaller, but every wall needs the .NET 8 Desktop
                          Runtime installed. Signed with your own certificate and shipped with an
                          .appinstaller feed the app updates itself from.

      -Store              Self-contained: nothing to pre-install, because the Store cannot install
                          the .NET runtime for a customer. No feed - the Store owns updating, and
                          AppVersion.IsStoreInstall disables the app's own update path to match.
                          Signing is optional: Partner Center strips any signature and re-signs
                          with the Store certificate.

    makeappx.exe / signtool.exe come from an installed Windows SDK when there is one, and otherwise
    from the Microsoft.Windows.SDK.BuildTools NuGet package - restoring packaging\SdkTools.proj is
    enough, no SDK install required.

.PARAMETER Store
    Build for Microsoft Store: self-contained, no update feed, .msixupload output.

.PARAMETER BaseUri
    Sideload only. Where the .msix and .appinstaller will be published, without a trailing slash.
    Baked into the feed, so it has to be the address the wall can actually reach - a plain file
    share (\\server\share or file:///) works as well as https.

.PARAMETER IdentityName
    Package/Identity/Name. For the Store, the value shown on Partner Center's product identity page,
    e.g. 12345SMIC.GridDisplay.

.PARAMETER Publisher
    Package/Identity/Publisher. For the Store, the value from the same page, e.g.
    CN=A1B2C3D4-1111-2222-3333-444455556666. For sideloading, the subject of your signing
    certificate, matched character for character or Windows rejects the package.

.PARAMETER PublisherDisplayName
    Properties/PublisherDisplayName. For the Store it must equal the publisher display name on the
    product identity page.

.PARAMETER DisplayName
    Properties/DisplayName. For the Store it must equal the app name reserved in Partner Center.

.PARAMETER SelfContained
    Bundle the .NET runtime. Implied by -Store; use it for a sideloaded package too when the walls
    have no .NET 8 Desktop Runtime.

.PARAMETER CertificateThumbprint
    Signs with a certificate already in the current user's store. Preferred on a build machine.

.PARAMETER CertificatePath
    Signs with a .pfx file instead. Use -CertificatePassword with it.

.EXAMPLE
    # Store submission. Identity values come from Partner Center > your app > Product identity.
    powershell -ExecutionPolicy Bypass -File packaging\build-msix.ps1 -Store `
        -IdentityName '12345SMIC.GridDisplay' -Publisher 'CN=A1B2C3D4-1111-2222-3333-444455556666' `
        -PublisherDisplayName 'SMIC' -DisplayName 'Grid Display'

.EXAMPLE
    # Sideloaded package plus update feed.
    powershell -ExecutionPolicy Bypass -File packaging\build-msix.ps1 `
        -BaseUri 'https://intranet.example/griddisplay' -CertificatePath .\GridDisplay.pfx -CertificatePassword 'pw'
#>
[CmdletBinding()]
param(
    [switch] $Store,
    [string] $BaseUri = 'https://example.invalid/griddisplay',
    [string] $Publisher = 'CN=SMIC',
    [string] $IdentityName = 'SMIC.GridDisplay',
    [string] $PublisherDisplayName = 'SMIC',
    [string] $DisplayName = 'Grid Display',
    [string] $Version,
    [string] $Configuration = 'Release',
    [switch] $SelfContained,
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
# The Store cannot install the .NET Desktop Runtime on a customer's machine, so a Store package that
# depends on it simply fails to start for everyone who does not already happen to have it.
if ($Store) { $SelfContained = $true }

# ---------------------------------------------------------------- version

if (-not $Version) {
    $csproj = New-Object System.Xml.XmlDocument
    $csproj.Load($projectPath)
    $node = $csproj.SelectSingleNode('//PropertyGroup/Version')
    if (-not $node) { throw "No <Version> in $projectPath. Add one, or pass -Version." }
    $Version = $node.InnerText.Trim()
}
# MSIX versions are always four parts. Revision stays 0: the Store reserves it, and keeping it 0
# for sideloaded builds too means the same number can be published either way later.
$packageVersion = $Version
while (($packageVersion -split '\.').Count -lt 4) { $packageVersion += '.0' }
if (($packageVersion -split '\.')[3] -ne '0') {
    throw "Package version must end in .0 - the Store reserves the revision field. Got $packageVersion."
}

$modeText = if ($Store) { 'Microsoft Store (.msixupload)' } else { 'sideload (.msix + .appinstaller)' }
$runtimeText = if ($SelfContained) { 'self-contained' } else { 'framework-dependent (.NET 8 Desktop Runtime required)' }
Write-Host "Mode            $modeText"
Write-Host "Version         $packageVersion"
Write-Host "Identity name   $IdentityName"
Write-Host "Publisher       $Publisher"
Write-Host "Display name    $DisplayName"
if (-not $Store) { Write-Host "Base URI        $BaseUri" }
Write-Host "Runtime         $runtimeText"

# A Store publisher id is a GUID inside a CN=. Anything else is almost certainly the sideload
# placeholder, and the upload would be rejected after a full build - so say so now, while it is
# still cheap to fix.
if ($Store -and $Publisher -notmatch '^CN=[0-9A-Fa-f]{8}-') {
    Write-Warning "-Publisher '$Publisher' does not look like a Store publisher id (CN=<guid>). Copy it from Partner Center > your app > Product identity, or the upload will be rejected."
}
if ($Store -and $IdentityName -eq 'SMIC.GridDisplay') {
    Write-Warning '-IdentityName is still the sideload placeholder. The Store needs the reserved Package/Identity/Name.'
}

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

# Fallback for a machine with no Windows SDK: the same tools ship in a NuGet package. Restoring
# SdkTools.proj drops them in the package cache; nothing is compiled and nothing is installed.
function Find-NuGetSdkTool {
    param([string] $Name)

    $cache = Join-Path $env:USERPROFILE '.nuget\packages\microsoft.windows.sdk.buildtools'
    if (-not (Test-Path $cache)) {
        $toolsProject = Join-Path $PSScriptRoot 'SdkTools.proj'
        if (-not (Test-Path $toolsProject)) { return $null }
        Write-Host 'No Windows SDK found; restoring packaging tools from NuGet ...'
        & dotnet restore $toolsProject --verbosity quiet
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path $cache)) { return $null }
    }

    Get-ChildItem -LiteralPath $cache -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending |
        ForEach-Object { Get-ChildItem -LiteralPath $_.FullName -Recurse -Filter $Name -ErrorAction SilentlyContinue } |
        Where-Object { $_.DirectoryName -like '*\x64' } |
        Select-Object -First 1 -ExpandProperty FullName
}

function Get-PackagingTool {
    param([string] $Name)

    $found = Find-SdkTool $Name
    if (-not $found) { $found = Find-NuGetSdkTool $Name }
    return $found
}

$makeAppx = Get-PackagingTool 'makeappx.exe'
if (-not $makeAppx) {
    throw @'
makeappx.exe not found, and it could not be restored from NuGet.
Either let `dotnet restore packaging\SdkTools.proj` reach nuget.org, or install the Windows SDK:
  winget install --id Microsoft.WindowsSDK.10.0.26100
  https://developer.microsoft.com/windows/downloads/windows-sdk/
Only the "MSIX Packaging Tools" / "Windows SDK Signing Tools" components are needed.
'@
}
Write-Host "makeappx        $makeAppx"

# ---------------------------------------------------------------- publish

if (-not $SkipPublish) {
    Write-Host "`nPublishing $Configuration ..."
    if (Test-Path $stageDirectory) { Remove-Item -LiteralPath $stageDirectory -Recurse -Force }
    $selfContainedArg = if ($SelfContained) { 'true' } else { 'false' }
    & dotnet publish $projectPath -c $Configuration -r win-x64 --self-contained $selfContainedArg `
        -p:PublishSingleFile=false -p:Version=$Version -o $stageDirectory
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }
} elseif (-not (Test-Path $stageDirectory)) {
    throw "-SkipPublish was given but $stageDirectory does not exist yet."
}

# ---------------------------------------------------------------- symbols

if (-not (Test-Path $OutputDirectory)) { New-Item -ItemType Directory -Path $OutputDirectory | Out-Null }

$symbolPath = $null
$pdbs = @(Get-ChildItem -LiteralPath $stageDirectory -Filter '*.pdb' -Recurse)
if ($Store -and $pdbs.Count -gt 0) {
    # .appxsym is a zip of the pdbs, uploaded next to the package so Partner Center can symbolise
    # the crash reports it collects. Built before the pdbs are deleted, for obvious reasons.
    $symbolPath = Join-Path $OutputDirectory "GridDisplay_${packageVersion}_x64.appxsym"
    $symbolZip = [System.IO.Path]::ChangeExtension($symbolPath, '.zip')
    if (Test-Path $symbolZip) { Remove-Item -LiteralPath $symbolZip -Force }
    Compress-Archive -Path ($pdbs | Select-Object -ExpandProperty FullName) -DestinationPath $symbolZip -Force
    if (Test-Path $symbolPath) { Remove-Item -LiteralPath $symbolPath -Force }
    Move-Item -LiteralPath $symbolZip -Destination $symbolPath
    Write-Host "Symbols         $symbolPath"
}
# A .pdb has no business in a package a wall downloads over a slow link, and neither do the
# IntelliSense .xml files the WebView2 package brings along - a megabyte and a half of API docs
# nothing at runtime ever opens.
$pdbs | Remove-Item -Force
Get-ChildItem -LiteralPath $stageDirectory -Filter '*.xml' -Recurse | Remove-Item -Force

# ---------------------------------------------------------------- manifest + assets

Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Assets') -Destination (Join-Path $stageDirectory 'Assets') -Recurse -Force
# The exe icon lives in Assets for the packaging step; it is not a package asset itself.
Remove-Item -LiteralPath (Join-Path $stageDirectory 'Assets\GridDisplay.ico') -Force -ErrorAction SilentlyContinue

$manifestPath = Join-Path $stageDirectory 'AppxManifest.xml'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'AppxManifest.xml') -Destination $manifestPath -Force

# XmlDocument.Load, not [xml](Get-Content -Raw): Get-Content on Windows PowerShell decodes a
# BOM-less file with the system ANSI codepage, and on a Korean install the manifest's Korean
# description comes back as mojibake that no longer parses as XML.
$manifest = New-Object System.Xml.XmlDocument
$manifest.Load($manifestPath)
$identity = $manifest.Package.Identity
$identity.SetAttribute('Version', $packageVersion)
$identity.SetAttribute('Publisher', $Publisher)
$identity.SetAttribute('Name', $IdentityName)
# Both of these are matched against the Partner Center reservation at upload time, so they are
# stamped here rather than left to drift in the checked-in manifest.
$manifest.Package.Properties.DisplayName = $DisplayName
$manifest.Package.Properties.PublisherDisplayName = $PublisherDisplayName
$manifest.Save($manifestPath)
Write-Host "Stamped         $manifestPath"

# Every logo the manifest names has to exist in the stage. makeappx reports a missing one as a
# generic pack failure, and a path typo is otherwise found only by a rejected upload.
$namespaces = New-Object System.Xml.XmlNamespaceManager($manifest.NameTable)
$namespaces.AddNamespace('m', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
$namespaces.AddNamespace('uap', 'http://schemas.microsoft.com/appx/manifest/uap/windows10')
$declared = @($manifest.SelectSingleNode('//m:Properties/m:Logo', $namespaces).InnerText)
foreach ($element in $manifest.SelectNodes('//uap:VisualElements | //uap:DefaultTile', $namespaces)) {
    foreach ($attribute in $element.Attributes) {
        if ($attribute.Name -like '*Logo') { $declared += $attribute.Value }
    }
}
$declared = $declared | Sort-Object -Unique
$missing = $declared | Where-Object { -not (Test-Path (Join-Path $stageDirectory $_)) }
if ($missing) {
    throw "The manifest references assets that are not in the package: $($missing -join ', '). Run packaging\New-Assets.ps1."
}
Write-Host "Assets          $($declared.Count) logos verified"

# ---------------------------------------------------------------- pack

$msixName = "GridDisplay_${packageVersion}_x64.msix"
$msixPath = Join-Path $OutputDirectory $msixName

Write-Host "`nPacking $msixName ..."
& $makeAppx pack /d $stageDirectory /p $msixPath /o
if ($LASTEXITCODE -ne 0) { throw "makeappx failed ($LASTEXITCODE)." }

# ---------------------------------------------------------------- sign

$signTool = Get-PackagingTool 'signtool.exe'
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
} elseif ($Store) {
    Write-Host "`nUnsigned, as intended: Partner Center re-signs the package with the Store certificate."
} else {
    Write-Warning 'Unsigned package. Windows will not install it. Sign it with -CertificateThumbprint or -CertificatePath (see New-DevCertificate.ps1).'
}

# ---------------------------------------------------------------- Store bundle, or update feed

if ($Store) {
    # What Partner Center takes: a zip holding the package and its symbols. Uploading the bare .msix
    # works too, but then the crash reports it collects are never symbolised.
    $uploadPath = Join-Path $OutputDirectory "GridDisplay_${packageVersion}_x64.msixupload"
    $uploadZip = [System.IO.Path]::ChangeExtension($uploadPath, '.zip')
    if (Test-Path $uploadZip) { Remove-Item -LiteralPath $uploadZip -Force }
    $contents = @($msixPath)
    if ($symbolPath) { $contents += $symbolPath }
    Compress-Archive -Path $contents -DestinationPath $uploadZip -Force
    if (Test-Path $uploadPath) { Remove-Item -LiteralPath $uploadPath -Force }
    Move-Item -LiteralPath $uploadZip -Destination $uploadPath

    Write-Host "`nDone."
    Write-Host "  package  $msixPath"
    Write-Host "  upload   $uploadPath"
    Write-Host @"

Next:
  1. Partner Center > your app > Packages: upload
       $uploadPath
  2. Answer the runFullTrust justification, fill in the listing (screenshots, privacy policy URL,
     age rating) and submit. packaging\README.md carries the checklist and the wording.
  3. Next release: bump <Version> in GridDisplay.csproj and re-run. The Store rejects a package
     whose version is not higher than the one already published.
"@
    return
}

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
