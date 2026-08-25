<#
.SYNOPSIS
    Build a release package and the manifest the in-app updater reads.

.DESCRIPTION
    Stamps the version into GridDisplay.csproj, publishes, zips the output, hashes it and
    writes latest.json. With -Publish it also creates the GitHub release and uploads both
    assets, which is what makes the running copies see the update: the app's default feed is
    https://github.com/knoxxr/GridDisplay/releases/latest/download/latest.json, and that path
    always redirects to the newest release's asset of that name.

.EXAMPLE
    .\tools\release.ps1 -Version 1.1.0 -Notes "셀 확대 속도 개선" -Publish
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

if (-not (Test-Path $csproj)) { throw "프로젝트를 찾을 수 없습니다: $csproj" }

# --- version ---------------------------------------------------------------
Write-Host "버전 $Version 기록 중..." -ForegroundColor Cyan
$xml = Get-Content $csproj -Raw
$updated = $xml -replace '<Version>[^<]*</Version>', "<Version>$Version</Version>"
if ($updated -eq $xml -and $xml -notmatch "<Version>$([regex]::Escape($Version))</Version>") {
    throw "csproj에 <Version> 요소가 없습니다. 먼저 추가하세요."
}
# WriteAllText with an explicit BOM-less encoding, not Set-Content: PowerShell 5.1's -Encoding utf8
# prepends a BOM, which rewrites the whole file in every diff for a one-line version bump.
[System.IO.File]::WriteAllText(
    (Resolve-Path $csproj), $updated, (New-Object System.Text.UTF8Encoding($false)))

# --- publish ---------------------------------------------------------------
Write-Host '게시 중...' -ForegroundColor Cyan
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null

dotnet publish $csproj -c Release -r win-x64 --self-contained false -o $stage
if ($LASTEXITCODE -ne 0) { throw '게시에 실패했습니다.' }

# The updater expects GridDisplay.exe at the root of the extracted package, so zip the
# folder's contents rather than the folder itself.
Write-Host "압축 중: $zipName" -ForegroundColor Cyan
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zipPath -Force

$hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash
$size = (Get-Item $zipPath).Length

# --- manifest --------------------------------------------------------------
$manifest = [ordered]@{
    version   = $Version
    url       = "https://github.com/knoxxr/GridDisplay/releases/download/$tag/$zipName"
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
    Write-Host '설치 파일 만드는 중...' -ForegroundColor Cyan
    $iss = Join-Path $root 'installer\GridDisplay.iss'
    & $iscc "/DAppVersion=$Version" "/DPayloadDir=$stage" "/DOutDir=$dist" $iss | Out-Null
    if ($LASTEXITCODE -ne 0) { throw '설치 파일 컴파일에 실패했습니다.' }
    $setupPath = Join-Path $dist "GridDisplay-Setup-$Version.exe"
} else {
    Write-Host 'Inno Setup 을 찾지 못해 설치 파일은 건너뜁니다.' -ForegroundColor Yellow
    Write-Host '  winget install JRSoftware.InnoSetup' -ForegroundColor Yellow
}

Write-Host ''
Write-Host "패키지 : $zipPath  ($([math]::Round($size / 1MB, 2)) MB)"
Write-Host "SHA256 : $hash"
Write-Host "매니페스트: $manifestPath"
if ($setupPath) {
    Write-Host "설치 파일: $setupPath  ($([math]::Round((Get-Item $setupPath).Length / 1MB, 2)) MB)"
}

# --- publish to GitHub -----------------------------------------------------
if (-not $Publish) {
    Write-Host ''
    Write-Host '게시하려면 -Publish 를 붙여 다시 실행하세요.' -ForegroundColor Yellow
    return
}

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw 'gh CLI를 찾을 수 없습니다. https://cli.github.com'
}

$releaseNotes = if ($Notes) { $Notes } else { "GridDisplay $Version" }

Write-Host ''
Write-Host "GitHub 릴리스 $tag 생성 중..." -ForegroundColor Cyan
$assets = @($zipPath, $manifestPath)
if ($setupPath) { $assets += $setupPath }
gh release create $tag @assets --title $tag --notes $releaseNotes
if ($LASTEXITCODE -ne 0) { throw 'gh release create 에 실패했습니다.' }

Write-Host "완료. 실행 중인 GridDisplay가 6시간 내에, 또는 '업데이트 확인…'에서 즉시 감지합니다." -ForegroundColor Green
