<#
.SYNOPSIS
    Collect what Windows recorded around a freeze, into one text file.

.DESCRIPTION
    Run this on the machine that froze, after it has been rebooted. It reads the event log and a
    few system facts and writes a single report to the desktop; nothing is changed and nothing
    leaves the machine.

    The point is to separate three very different causes that all look identical from the chair:
    a graphics driver hang, memory exhaustion, and the application holding up input. They are
    told apart by which events appear in the minutes before the machine stopped, which is why
    this collects a window either side rather than a snapshot.

    This file must stay UTF-8 with BOM; Windows PowerShell 5.1 reads a BOM-less .ps1 as ANSI.

.EXAMPLE
    .\tools\collect-freeze-report.ps1
    .\tools\collect-freeze-report.ps1 -HoursBack 48
#>
[CmdletBinding()]
param(
    [int]$HoursBack = 36,
    [string]$OutputPath = (Join-Path ([Environment]::GetFolderPath('Desktop')) "GridDisplay-freeze-report.txt")
)

$ErrorActionPreference = 'Continue'
$since = (Get-Date).AddHours(-$HoursBack)
$lines = New-Object System.Collections.Generic.List[string]

function Section($title) {
    $lines.Add('')
    $lines.Add('=' * 78)
    $lines.Add($title)
    $lines.Add('=' * 78)
}

function Add($text) { $lines.Add([string]$text) }

function Events($log, $ids, $label, $provider) {
    Section $label
    try {
        $filter = @{LogName = $log; StartTime = $since }
        if ($ids) { $filter['Id'] = $ids }
        if ($provider) { $filter['ProviderName'] = $provider }
        $found = Get-WinEvent -FilterHashtable $filter -ErrorAction Stop
        if (-not $found) { Add '  (해당 기간에 없음)'; return }
        foreach ($e in $found | Sort-Object TimeCreated) {
            Add ("  {0}  [{1}] {2}" -f $e.TimeCreated, $e.Id, $e.ProviderName)

            # Event 41 says almost nothing in prose; BugcheckCode is the field that separates
            # "the kernel died and left a dump" from "it simply stopped responding".
            if ($e.Id -eq 41) {
                try {
                    $x = [xml]$e.ToXml()
                    $get = { param($n) ($x.Event.EventData.Data | Where-Object { $_.Name -eq $n }).'#text' }
                    $code = & $get 'BugcheckCode'
                    Add ("      BugcheckCode = {0}{1}" -f $code,
                        $(if ($code -eq '0') { '  → 크래시 아님. 그대로 얼어붙었거나 전원이 끊긴 경우' }
                          else { '  → 커널 크래시. 10번 항목의 덤프를 분석해야 합니다' }))
                    Add ("      PowerButtonTimestamp = {0}   전원 버튼으로 껐는지 여부" -f (& $get 'PowerButtonTimestamp'))
                }
                catch { Add '      (세부 값을 읽지 못했습니다)' }
            }

            foreach ($l in ($e.Message -split "`n" | Select-Object -First 3)) {
                if ($l.Trim()) { Add ("      " + $l.Trim()) }
            }
        }
    }
    catch { Add '  (해당 기간에 없음)' }
}

Add "Grid Display 멈춤 보고서"
Add "생성: $(Get-Date)   대상 기간: 최근 $HoursBack 시간 (>= $since)"
Add "컴퓨터: $env:COMPUTERNAME   사용자: $env:USERNAME"

Section '1. 시스템 개요'
$os = Get-CimInstance Win32_OperatingSystem
$cs = Get-CimInstance Win32_ComputerSystem
Add "  OS        : $($os.Caption) $($os.Version)"
Add "  마지막 부팅 : $($os.LastBootUpTime)"
Add "  물리 메모리 : $([math]::Round($cs.TotalPhysicalMemory / 1GB, 1)) GB"
Add "  현재 여유  : $([math]::Round($os.FreePhysicalMemory / 1MB, 1)) GB"
foreach ($pf in Get-CimInstance Win32_PageFileUsage -ErrorAction SilentlyContinue) {
    Add "  페이지 파일 : $($pf.Name)  할당 $($pf.AllocatedBaseSize) MB  최대사용 $($pf.PeakUsage) MB"
}

Section '2. 그래픽 드라이버'
foreach ($g in Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue) {
    Add "  $($g.Name)"
    Add "      드라이버 $($g.DriverVersion)  ($($g.DriverDate))"
}
$tdr = Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers' -ErrorAction SilentlyContinue
Add "  TdrDelay  : $(if ($tdr.TdrDelay) { $tdr.TdrDelay } else { '기본값(2초)' })"
Add "  TdrLevel  : $(if ($null -ne $tdr.TdrLevel) { $tdr.TdrLevel } else { '기본값(3=복구)' })"

Section '3. 저장장치 상태  ← I/O 오류가 보였다면 여기부터'
foreach ($d in Get-PhysicalDisk -ErrorAction SilentlyContinue) {
    Add ("  [{0}] {1}" -f $d.DeviceId, $d.FriendlyName)
    Add ("      일련번호 {0}" -f $d.SerialNumber)
    Add ("      상태 {0} / {1}   버스 {2}   크기 {3} GB" -f $d.HealthStatus, $d.OperationalStatus, $d.BusType,
        [math]::Round($d.Size / 1GB, 0))
    # SMART-ish counters. A disk can report Healthy and still be failing reads, so the raw
    # error counts matter more than the summary word next to them.
    $rc = $d | Get-StorageReliabilityCounter -ErrorAction SilentlyContinue
    if ($rc) {
        Add ("      읽기오류(교정) {0}  읽기오류(치명) {1}  쓰기오류(교정) {2}  쓰기오류(치명) {3}" -f
            $rc.ReadErrorsCorrected, $rc.ReadErrorsUncorrected, $rc.WriteErrorsCorrected, $rc.WriteErrorsUncorrected)
        Add ("      마모도 {0}%  온도 {1}C  가동시간 {2}시간" -f $rc.Wear, $rc.Temperature, $rc.PowerOnHours)
    }
}
foreach ($v in Get-Volume -ErrorAction SilentlyContinue | Where-Object DriveLetter) {
    Add ("  {0}: {1}  여유 {2} GB / {3} GB   상태 {4}" -f $v.DriveLetter, $v.FileSystemLabel,
        [math]::Round($v.SizeRemaining / 1GB, 1), [math]::Round($v.Size / 1GB, 1), $v.HealthStatus)
}
# Which disk holds the pagefile decides whether a storage stall freezes the whole machine.
foreach ($pf in Get-CimInstance Win32_PageFileUsage -ErrorAction SilentlyContinue) {
    Add ("  페이지 파일 위치: {0}  ← 이 디스크가 멎으면 시스템 전체가 멈춥니다" -f $pf.Name)
}

Section '4. Grid Display 설치 상태'
$installs = @()
foreach ($root in @($env:LOCALAPPDATA, $env:ProgramFiles, ${env:ProgramFiles(x86)})) {
    if (-not $root) { continue }
    foreach ($leaf in @('Programs\GridDisplay\GridDisplay.exe', 'GridDisplay\GridDisplay.exe')) {
        $exe = Join-Path $root $leaf
        if (Test-Path $exe) { $installs += $exe }
    }
}
if ($installs) {
    foreach ($f in ($installs | Select-Object -Unique)) {
        Add "  $f  (버전 $((Get-Item $f).VersionInfo.FileVersion))"
    }
} else { Add '  설치 폴더를 찾지 못했습니다 (MSIX 이거나 다른 위치)' }
$pkg = Get-AppxPackage -Name '*GridDisplay*' -ErrorAction SilentlyContinue
if ($pkg) { foreach ($p2 in $pkg) { Add "  MSIX: $($p2.Name) $($p2.Version)" } }
foreach ($dir in @((Join-Path $env:LOCALAPPDATA 'GridDisplay'), (Join-Path $env:APPDATA 'GridDisplay'))) {
    if (Test-Path $dir) {
        $size = (Get-ChildItem $dir -Recurse -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum
        Add "  $dir  ($([math]::Round($size / 1MB, 1)) MB)"
    }
}

Section '5. 앱이 남긴 기록 (있는 경우)'
$logs = Get-ChildItem (Join-Path $env:LOCALAPPDATA 'GridDisplay') -Recurse -Include 'health.log', 'input-hook.log' -ErrorAction SilentlyContinue
if (-not $logs) {
    Add '  없음 — 진단 기록이 들어간 버전이 아직 배포되지 않았습니다.'
} else {
    foreach ($l in $logs) {
        Add "  --- $($l.FullName) (마지막 40줄) ---"
        foreach ($line in (Get-Content $l.FullName -Tail 40 -ErrorAction SilentlyContinue)) { Add "      $line" }
    }
}

# 아래 네 가지가 원인을 갈라놓는 핵심입니다.
Events 'System' @(41, 6008, 1074, 1076) '6. 비정상 종료 / 강제 재시작  ← BugcheckCode 가 0 이 아니면 드라이버 크래시'
# By provider, not by number: 117 and 141 are also used by the print spooler and others, and a
# section full of unrelated lines hides the one line that matters.
Events 'System' $null '7. 그래픽 드라이버 응답 없음 (TDR)  ← 전체 프리징의 가장 흔한 원인' @(
    'Display', 'Microsoft-Windows-DXGKrnl', 'Microsoft-Windows-LiveKernelEvent',
    'nvlddmkm', 'amdkmdag', 'amdwddmg', 'igfxn', 'igfx')
Events 'System' @(2004, 2013, 333, 49, 26) '8. 메모리 / 디스크 고갈'
Events 'System' $null '9. 하드웨어 오류 (WHEA)' 'Microsoft-Windows-WHEA-Logger'
Events 'Application' @(1000, 1001, 1002) '10. 응용 프로그램 크래시 / 응답 없음'

Events 'System' $null '11. 저장장치 오류  ← "I/O on ... has failed" 가 나오는 곳' @(
    'Disk', 'disk', 'storahci', 'stornvme', 'iaStorAC', 'iaStorA', 'Microsoft-Windows-Ntfs',
    'volmgr', 'partmgr', 'Microsoft-Windows-DiskDiagnosticDataCollector')

Section '12. 메모리 덤프'
foreach ($d in @("$env:SystemRoot\MEMORY.DMP", "$env:SystemRoot\Minidump")) {
    if (Test-Path $d) {
        Get-ChildItem $d -ErrorAction SilentlyContinue | Select-Object -First 5 | ForEach-Object {
            Add "  $($_.FullName)  $([math]::Round($_.Length / 1MB, 1)) MB  $($_.LastWriteTime)"
        }
    }
}
if (-not (Test-Path "$env:SystemRoot\MEMORY.DMP") -and -not (Test-Path "$env:SystemRoot\Minidump")) {
    Add '  덤프 없음 — 커널이 멈춘 것이 아니라 그대로 얼어붙었을 가능성이 큽니다.'
}

Set-Content -Path $OutputPath -Value $lines -Encoding utf8
Write-Host ""
Write-Host "보고서 저장: $OutputPath" -ForegroundColor Green
Write-Host "이 파일을 그대로 보내 주세요. 개인 문서 내용은 들어가지 않습니다." -ForegroundColor Gray
