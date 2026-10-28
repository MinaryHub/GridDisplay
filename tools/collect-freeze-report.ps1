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
        if (-not $found) { Add '  (none in this period)'; return }
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
                        $(if ($code -eq '0') { '  -> not a crash: the machine froze in place or lost power' }
                          else { '  -> kernel crash: analyse the dump listed in section 12' }))
                    Add ("      PowerButtonTimestamp = {0}   whether it was switched off with the power button" -f (& $get 'PowerButtonTimestamp'))
                }
                catch { Add '      (could not read the details)' }
            }

            foreach ($l in ($e.Message -split "`n" | Select-Object -First 3)) {
                if ($l.Trim()) { Add ("      " + $l.Trim()) }
            }
        }
    }
    catch { Add '  (none in this period)' }
}

Add "Grid Display freeze report"
Add "Created: $(Get-Date)   Period: last $HoursBack hours (>= $since)"
Add "Computer: $env:COMPUTERNAME   User: $env:USERNAME"

Section '1. System overview'
$os = Get-CimInstance Win32_OperatingSystem
$cs = Get-CimInstance Win32_ComputerSystem
Add "  OS            : $($os.Caption) $($os.Version)"
Add "  Last boot     : $($os.LastBootUpTime)"
Add "  Physical RAM  : $([math]::Round($cs.TotalPhysicalMemory / 1GB, 1)) GB"
Add "  Free now      : $([math]::Round($os.FreePhysicalMemory / 1MB, 1)) GB"
foreach ($pf in Get-CimInstance Win32_PageFileUsage -ErrorAction SilentlyContinue) {
    Add "  Page file     : $($pf.Name)  allocated $($pf.AllocatedBaseSize) MB  peak use $($pf.PeakUsage) MB"
}

Section '2. Graphics driver'
foreach ($g in Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue) {
    Add "  $($g.Name)"
    Add "      driver $($g.DriverVersion)  ($($g.DriverDate))"
}
$tdr = Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers' -ErrorAction SilentlyContinue
Add "  TdrDelay  : $(if ($tdr.TdrDelay) { $tdr.TdrDelay } else { 'default (2 s)' })"
Add "  TdrLevel  : $(if ($null -ne $tdr.TdrLevel) { $tdr.TdrLevel } else { 'default (3 = recover)' })"

Section '3. Storage health  <- start here if I/O errors were seen'
foreach ($d in Get-PhysicalDisk -ErrorAction SilentlyContinue) {
    Add ("  [{0}] {1}" -f $d.DeviceId, $d.FriendlyName)
    Add ("      serial {0}" -f $d.SerialNumber)
    Add ("      status {0} / {1}   bus {2}   size {3} GB" -f $d.HealthStatus, $d.OperationalStatus, $d.BusType,
        [math]::Round($d.Size / 1GB, 0))
    # SMART-ish counters. A disk can report Healthy and still be failing reads, so the raw
    # error counts matter more than the summary word next to them.
    $rc = $d | Get-StorageReliabilityCounter -ErrorAction SilentlyContinue
    if ($rc) {
        Add ("      read errors (corrected) {0}  read errors (uncorrected) {1}  write errors (corrected) {2}  write errors (uncorrected) {3}" -f
            $rc.ReadErrorsCorrected, $rc.ReadErrorsUncorrected, $rc.WriteErrorsCorrected, $rc.WriteErrorsUncorrected)
        Add ("      wear {0}%  temperature {1}C  power-on {2} h" -f $rc.Wear, $rc.Temperature, $rc.PowerOnHours)
    }
}
foreach ($v in Get-Volume -ErrorAction SilentlyContinue | Where-Object DriveLetter) {
    Add ("  {0}: {1}  free {2} GB / {3} GB   status {4}" -f $v.DriveLetter, $v.FileSystemLabel,
        [math]::Round($v.SizeRemaining / 1GB, 1), [math]::Round($v.Size / 1GB, 1), $v.HealthStatus)
}
# Which disk holds the pagefile decides whether a storage stall freezes the whole machine.
foreach ($pf in Get-CimInstance Win32_PageFileUsage -ErrorAction SilentlyContinue) {
    Add ("  Page file on: {0}  <- if this disk stalls, the whole system stalls" -f $pf.Name)
}

Section '4. Grid Display installation'
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
        Add "  $f  (version $((Get-Item $f).VersionInfo.FileVersion))"
    }
} else { Add '  No install folder found (MSIX, or installed elsewhere)' }
$pkg = Get-AppxPackage -Name '*GridDisplay*' -ErrorAction SilentlyContinue
if ($pkg) { foreach ($p2 in $pkg) { Add "  MSIX: $($p2.Name) $($p2.Version)" } }
foreach ($dir in @((Join-Path $env:LOCALAPPDATA 'GridDisplay'), (Join-Path $env:APPDATA 'GridDisplay'))) {
    if (Test-Path $dir) {
        $size = (Get-ChildItem $dir -Recurse -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum
        Add "  $dir  ($([math]::Round($size / 1MB, 1)) MB)"
    }
}

Section '5. Logs written by the app (if any)'
$logs = Get-ChildItem (Join-Path $env:LOCALAPPDATA 'GridDisplay') -Recurse -Include 'health.log', 'input-hook.log' -ErrorAction SilentlyContinue
if (-not $logs) {
    Add '  None - no version with diagnostic logging has been deployed here yet.'
} else {
    foreach ($l in $logs) {
        Add "  --- $($l.FullName) (last 40 lines) ---"
        foreach ($line in (Get-Content $l.FullName -Tail 40 -ErrorAction SilentlyContinue)) { Add "      $line" }
    }
}

# The sections below are the ones that tell the causes apart.
Events 'System' @(41, 6008, 1074, 1076) '6. Unexpected shutdown / forced restart  <- a non-zero BugcheckCode means a driver crash'
# By provider, not by number: 117 and 141 are also used by the print spooler and others, and a
# section full of unrelated lines hides the one line that matters.
Events 'System' $null '7. Graphics driver not responding (TDR)  <- the most common cause of a full freeze' @(
    'Display', 'Microsoft-Windows-DXGKrnl', 'Microsoft-Windows-LiveKernelEvent',
    'nvlddmkm', 'amdkmdag', 'amdwddmg', 'igfxn', 'igfx')
Events 'System' @(2004, 2013, 333, 49, 26) '8. Memory / disk exhaustion'
Events 'System' $null '9. Hardware errors (WHEA)' 'Microsoft-Windows-WHEA-Logger'
Events 'Application' @(1000, 1001, 1002) '10. Application crashes / hangs'

Events 'System' $null '11. Storage errors  <- where "I/O on ... has failed" shows up' @(
    'Disk', 'disk', 'storahci', 'stornvme', 'iaStorAC', 'iaStorA', 'Microsoft-Windows-Ntfs',
    'volmgr', 'partmgr', 'Microsoft-Windows-DiskDiagnosticDataCollector')

Section '12. Memory dumps'
foreach ($d in @("$env:SystemRoot\MEMORY.DMP", "$env:SystemRoot\Minidump")) {
    if (Test-Path $d) {
        Get-ChildItem $d -ErrorAction SilentlyContinue | Select-Object -First 5 | ForEach-Object {
            Add "  $($_.FullName)  $([math]::Round($_.Length / 1MB, 1)) MB  $($_.LastWriteTime)"
        }
    }
}
if (-not (Test-Path "$env:SystemRoot\MEMORY.DMP") -and -not (Test-Path "$env:SystemRoot\Minidump")) {
    Add '  No dumps - the kernel most likely did not crash; the machine froze in place.'
}

Set-Content -Path $OutputPath -Value $lines -Encoding utf8
Write-Host ""
Write-Host "Report saved: $OutputPath" -ForegroundColor Green
Write-Host "Please send this file as is. It contains no personal document content." -ForegroundColor Gray
