<#
.SYNOPSIS
    Build a release package and the manifest the in-app updater reads.

.DESCRIPTION
    Stamps the version into GridDisplay.csproj, publishes, zips the output, hashes it and
    writes latest.json. With -Publish it also creates the GitHub release and uploads both
    assets, which is what makes the running copies see the update: the app's default feed is
    https://github.com/MinaryHub/GridDisplay/releases/latest/download/latest.json, and that path
    always redirects to the newest release's asset of that name.

.EXAMPLE
    .\tools\release.ps1 -Version 1.1.0 -Notes "Faster cell zoom" -Publish
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [string]$Notes = '',

    # Create the GitHub release and upload the assets. Without it the files are left in dist\
    # for inspection.
    [switch]$Publish
)

$ErrorActionPreference = 'Stop'

$root    = Split-Path -Parent $PSScriptRoot
$csproj  = Join-Path $root 'src\GridDisplay\GridDisplay.csproj'
$dist    = Join-Path $root 'dist'
$stage   = Join-Path $dist 'publish'
$zipName = "GridDisplay-$Version.zip"
$zipPath = Join-Path $dist $zipName
$tag     = "v$Version"

if (-not (Test-Path $csproj)) { throw "Project not found: $csproj" }

# --- version ---------------------------------------------------------------
Write-Host "Stamping version $Version..." -ForegroundColor Cyan
$xml = Get-Content $csproj -Raw
$updated = $xml -replace '<Version>[^<]*</Version>', "<Version>$Version</Version>"
if ($updated -eq $xml -and $xml -notmatch "<Version>$([regex]::Escape($Version))</Version>") {
    throw "The csproj has no <Version> element. Add one first."
}
# WriteAllText with an explicit BOM-less encoding, not Set-Content: PowerShell 5.1's -Encoding utf8
# prepends a BOM, which rewrites the whole file in every diff for a one-line version bump.
[System.IO.File]::WriteAllText(
    (Resolve-Path $csproj), $updated, (New-Object System.Text.UTF8Encoding($false)))

# --- publish ---------------------------------------------------------------
Write-Host 'Publishing...' -ForegroundColor Cyan
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null

dotnet publish $csproj -c Release -r win-x64 --self-contained false -o $stage
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

# The updater expects GridDisplay.exe at the root of the extracted package, so zip the
# folder's contents rather than the folder itself.
Write-Host "Zipping: $zipName" -ForegroundColor Cyan
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zipPath -Force

$hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash
$size = (Get-Item $zipPath).Length

# --- manifest --------------------------------------------------------------
$manifest = [ordered]@{
    version   = $Version
    url       = "https://github.com/MinaryHub/GridDisplay/releases/download/$tag/$zipName"
    sha256    = $hash
    notes     = $Notes
    published = (Get-Date).ToString('yyyy-MM-dd')
}
$manifestPath = Join-Path $dist 'latest.json'
$manifest | ConvertTo-Json -Depth 3 | Set-Content $manifestPath -Encoding utf8

# --- installer -------------------------------------------------------------
# The zip is what the in-app updater consumes; the installer is for a machine that has nothing
# yet. Compiled from the same publish output, so the two can never drift apart.
$setupPath = $null
$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if ($iscc) {
    Write-Host 'Building the installer...' -ForegroundColor Cyan
    $iss = Join-Path $root 'installer\GridDisplay.iss'
    & $iscc "/DAppVersion=$Version" "/DPayloadDir=$stage" "/DOutDir=$dist" $iss | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
    $setupPath = Join-Path $dist "GridDisplay-Setup-$Version.exe"
} else {
    Write-Host 'Inno Setup not found; skipping the installer.' -ForegroundColor Yellow
    Write-Host '  winget install JRSoftware.InnoSetup' -ForegroundColor Yellow
}

Write-Host ''
Write-Host "Package  : $zipPath  ($([math]::Round($size / 1MB, 2)) MB)"
Write-Host "SHA256   : $hash"
Write-Host "Manifest : $manifestPath"
if ($setupPath) {
    Write-Host "Installer: $setupPath  ($([math]::Round((Get-Item $setupPath).Length / 1MB, 2)) MB)"
}

# --- publish to GitHub -----------------------------------------------------
if (-not $Publish) {
    Write-Host ''
    Write-Host 'Run again with -Publish to publish the release.' -ForegroundColor Yellow
    return
}

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw 'gh CLI not found. https://cli.github.com'
}

$releaseNotes = if ($Notes) { $Notes } else { "GridDisplay $Version" }

Write-Host ''
Write-Host "Creating GitHub release $tag..." -ForegroundColor Cyan
$assets = @($zipPath, $manifestPath)
if ($setupPath) { $assets += $setupPath }
gh release create $tag @assets --title $tag --notes $releaseNotes
if ($LASTEXITCODE -ne 0) { throw 'gh release create failed.' }

Write-Host "Done. Running copies of GridDisplay will pick it up within 6 hours, or immediately from 'Version and updates…'." -ForegroundColor Green
