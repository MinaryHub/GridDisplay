<#
.SYNOPSIS
    Capture the store-listing screenshots into screenshots\en\ and screenshots\ko\.

.DESCRIPTION
    The app keeps its data in a fixed folder (%APPDATA%\GridDisplay), so this swaps in demo sets
    showing the fictional dashboards under screenshots\demo\<lang>\, drives the app with synthetic input,
    and always puts the original folder back afterwards — even when a step fails.

    Runs on the primary monitor and takes it over for about a minute. Do not touch the mouse
    or keyboard meanwhile: the input hook would see your input the same as the script's.

.EXAMPLE
    .\tools\capture-screenshots.ps1
    .\tools\capture-screenshots.ps1 -Exe .\src\GridDisplay\bin\Release\net8.0-windows10.0.19041.0\win-x64\GridDisplay.exe
    .\tools\capture-screenshots.ps1 -PauseProcesses VirtualDesktopIndicator
    .\tools\capture-screenshots.ps1 -Language ko
#>
[CmdletBinding()]
param(
    [string]$Exe,

    # Programs that draw over the screen (desktop indicators, recorders…). Closed for the
    # capture and started again from the same path afterwards.
    [string[]]$PauseProcesses = @(),

    # UI languages to shoot, each into screenshots\<lang>\ with the matching demo pages.
    [ValidateSet('en', 'ko')]
    [string[]]$Language = @('en', 'ko')
)

$ErrorActionPreference = 'Stop'

$root    = Split-Path -Parent $PSScriptRoot
$outDir  = Join-Path $root 'screenshots'
$demoDir = Join-Path $outDir 'demo'
$data    = Join-Path $env:APPDATA 'GridDisplay'
$backup  = Join-Path $env:TEMP ("GridDisplay-backup-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))

if (-not $Exe) { $Exe = Join-Path $root 'dist\publish\GridDisplay.exe' }
if (-not (Test-Path $Exe)) { throw "Executable not found: $Exe  (run release.ps1 or dotnet publish first)" }
if (Get-Process GridDisplay -ErrorAction SilentlyContinue) { throw 'GridDisplay is running. Close it and run this again.' }

Add-Type -AssemblyName System.Windows.Forms, System.Drawing, UIAutomationClient, UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Input {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, int x, int y, uint d, UIntPtr e);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint f, UIntPtr e);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    public static void Click(int x, int y, bool right) {
        SetCursorPos(x, y); System.Threading.Thread.Sleep(120);
        mouse_event(right ? 0x0008u : 0x0002u, 0, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(60);
        mouse_event(right ? 0x0010u : 0x0004u, 0, 0, 0, UIntPtr.Zero);
    }
    public static void Key(byte vk, bool ctrl) {
        if (ctrl) keybd_event(0x11, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, 0, UIntPtr.Zero); keybd_event(vk, 0, 2, UIntPtr.Zero);
        if (ctrl) keybd_event(0x11, 0, 2, UIntPtr.Zero);
    }
}
'@

$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$park   = { [Input]::SetCursorPos($screen.Right + 400, $screen.Top + 400) | Out-Null }   # off the wall, so no hover frame

function Shot([string]$name) {
    $bmp = New-Object System.Drawing.Bitmap $screen.Width, $screen.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($screen.Location, [System.Drawing.Point]::Empty, $screen.Size)
    $g.Dispose()
    $path = Join-Path $script:shotDir $name
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "  $name" -ForegroundColor Green
}

# Centre of cell (col,row) of a cols×rows grid filling the primary screen. Approximate is
# enough: a click anywhere inside the cell counts.
function CellCenter([int]$cols, [int]$rows, [int]$col, [int]$row) {
    [pscustomobject]@{
        X = [int]($screen.Left + $screen.Width * ($col + 0.5) / $cols)
        Y = [int]($screen.Top + ($screen.Height - 30) * ($row + 0.5) / $rows)
    }
}

function FindByName([int]$procId, [string]$like, [int]$timeoutMs = 5000) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $procId)
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        foreach ($top in [System.Windows.Automation.AutomationElement]::RootElement.FindAll('Children', $cond)) {
            foreach ($e in @($top) + @($top.FindAll('Descendants', [System.Windows.Automation.Condition]::TrueCondition))) {
                if ($e.Current.Name -like $like) { return $e }
            }
        }
        Start-Sleep -Milliseconds 250
    }
    throw "Could not find '$like' on screen."
}

function ClickElement($e) {
    $r = $e.Current.BoundingRectangle
    [Input]::Click([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2), $false)
}

function OpenMenuItem([int]$procId, [int]$x, [int]$y, [string]$item) {
    # Right after a dialog closes the menu sometimes does not come up on the first right-click,
    # so open it again rather than wait on a menu that is not there.
    for ($attempt = 1; ; $attempt++) {
        [Input]::Click($x, $y, $true)
        Start-Sleep -Milliseconds 700
        try { ClickElement (FindByName $procId $item 2500); return }
        catch { if ($attempt -ge 3) { throw } }
        [Input]::Key(0x1B, $false); Start-Sleep -Milliseconds 500
    }
}

function CloseWindow($e) {
    $e.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    Start-Sleep -Milliseconds 600
}

# --- demo data -----------------------------------------------------------------
# Per language: the demo set and cell names, and the captions this script looks for on screen.
# The captions must match the app's L.T(...) strings exactly.
$texts = @{
    en = @{
        Sets = 'Factory overview', 'Business KPIs', 'Server room'
        Cells = @{ production = 'Production'; servers = 'Server monitoring'; logistics = 'Logistics'
                   sales = 'Sales'; alarms = 'Equipment alarms'; energy = 'Energy usage' }
        CellSettingsItem = 'Cell settings*'; CellSettingsTitle = 'Cell 3 settings'; BrowserRadio = 'Browser (WebView2)'
        ManageSetsItem = 'Manage sets*'; ManageSetsTitle = 'Manage sets'
    }
    ko = @{
        Sets = '공장 상황판', '경영 지표', '서버실'
        Cells = @{ production = '생산 현황'; servers = '서버 모니터링'; logistics = '물류 현황'
                   sales = '매출 대시보드'; alarms = '설비 알람'; energy = '에너지 사용량' }
        CellSettingsItem = '셀 설정*'; CellSettingsTitle = '셀 3 설정'; BrowserRadio = '브라우저 (WebView2)'
        ManageSetsItem = '세트 관리*'; ManageSetsTitle = '세트 관리'
    }
}

function Cell([string]$lang, [string]$page) {
    [ordered]@{
        Kind = 1   # CellKind.Browser — the app reads enums as numbers
        Title = $texts[$lang].Cells[$page]
        Url = ([Uri](Join-Path $demoDir "$lang\$page.html")).AbsoluteUri
        LiveInGrid = $true   # live rather than a still capture, so the shot needs no settling time
    }
}

function DemoSets([string]$lang) {
    $n = $texts[$lang].Sets
    @(
        [ordered]@{ SchemaVersion = 1; Id = [guid]::NewGuid().ToString('N'); Name = $n[0]; Order = 1; Columns = 3; Rows = 2; Cells = @(
            (Cell $lang 'production'), (Cell $lang 'servers'), (Cell $lang 'logistics'),
            (Cell $lang 'sales'), (Cell $lang 'alarms'), (Cell $lang 'energy')) },
        [ordered]@{ SchemaVersion = 1; Id = [guid]::NewGuid().ToString('N'); Name = $n[1]; Order = 2; Columns = 2; Rows = 2; Cells = @(
            (Cell $lang 'sales'), (Cell $lang 'production'),
            (Cell $lang 'energy'), (Cell $lang 'logistics')) },
        [ordered]@{ SchemaVersion = 1; Id = [guid]::NewGuid().ToString('N'); Name = $n[2]; Order = 3; Columns = 2; Rows = 1; Cells = @(
            (Cell $lang 'servers'), (Cell $lang 'alarms')) }
    )
}

function DemoSettings([string]$lang, [string]$firstSetId) {
    [ordered]@{
        SettingsVersion = 4         # current, or Normalize would treat it as old and reset StartFullScreen
        Language = $lang            # UI language matching the demo pages
        LastSetId = $firstSetId
        StartFullScreen = $true
        CellGap = 8
        ShowCellLabels = $true
        AutoCheckUpdates = $false   # no update banner across the shots
    }
}

$utf8 = New-Object System.Text.UTF8Encoding($false)
$proc = $null

$paused = @()
foreach ($name in $PauseProcesses) {
    foreach ($p in @(Get-Process $name -ErrorAction SilentlyContinue)) {
        if ($p.Path) { $paused += $p.Path }
        Write-Host "Pausing: $($p.ProcessName)" -ForegroundColor Cyan
        $p.CloseMainWindow() | Out-Null
        if (-not $p.WaitForExit(3000)) { $p.Kill() }
    }
}

Write-Host "Backing up existing data: $backup" -ForegroundColor Cyan
if (Test-Path $data) { Copy-Item $data $backup -Recurse }

try {
    foreach ($lang in $Language) {
        $tx = $texts[$lang]
        $script:shotDir = Join-Path $outDir $lang
        New-Item -ItemType Directory $script:shotDir -Force | Out-Null

        if (Test-Path $data) { Remove-Item $data -Recurse -Force }
        New-Item -ItemType Directory (Join-Path $data 'sets') -Force | Out-Null
        $sets = DemoSets $lang
        [IO.File]::WriteAllText((Join-Path $data 'settings.json'), ((DemoSettings $lang $sets[0].Id) | ConvertTo-Json -Depth 5), $utf8)
        foreach ($s in $sets) {
            [IO.File]::WriteAllText((Join-Path $data "sets\$($s.Name).json"), ($s | ConvertTo-Json -Depth 5), $utf8)
        }

        Write-Host "[$lang] Starting the app..." -ForegroundColor Cyan
        & $park
        $proc = Start-Process $Exe -PassThru
        $proc.WaitForInputIdle(15000) | Out-Null
        Start-Sleep -Seconds 12     # cells start staggered; let every page render
        $id = $proc.Id

        Write-Host "[$lang] Capturing into $($script:shotDir):" -ForegroundColor Cyan
        Shot '01-grid.png'

        # Cell menu over the grid.
        $c = CellCenter 3 2 1 0
        [Input]::Click($c.X, $c.Y, $true); Start-Sleep -Milliseconds 900
        Shot '02-cell-menu.png'
        [Input]::Key(0x1B, $false); Start-Sleep -Milliseconds 500

        # One cell zoomed to the whole screen. The pointer sits mid-screen so the close button stays hidden.
        $c = CellCenter 3 2 0 0
        [Input]::Click($c.X, $c.Y, $false); Start-Sleep -Milliseconds 400
        [Input]::SetCursorPos($screen.Left + $screen.Width / 2, $screen.Top + $screen.Height / 2) | Out-Null
        Start-Sleep -Seconds 3
        Shot '03-zoomed-cell.png'
        [Input]::Key(0x1B, $false); Start-Sleep -Seconds 2
        & $park

        # Cell settings.
        $c = CellCenter 3 2 2 0
        OpenMenuItem $id $c.X $c.Y $tx.CellSettingsItem
        $dlg = FindByName $id $tx.CellSettingsTitle
        # Cell 3 is a browser cell, so the browser kind must be the one shown. A stray focus change
        # once left another radio picked; the dialog is cancelled afterwards, so fixing it is harmless.
        $rb = FindByName $id $tx.BrowserRadio
        $sel = $rb.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        if (-not $sel.Current.IsSelected) { Write-Host '  (kind was not Browser; corrected)' -ForegroundColor Yellow; $sel.Select() }
        & $park; Start-Sleep -Milliseconds 800
        Shot '04-cell-settings.png'
        CloseWindow $dlg

        # Set manager.
        OpenMenuItem $id $c.X $c.Y $tx.ManageSetsItem
        $dlg = FindByName $id $tx.ManageSetsTitle
        & $park; Start-Sleep -Milliseconds 800
        Shot '05-set-manager.png'
        CloseWindow $dlg

        # Another set, switched with Ctrl+2.
        [Input]::Key(0x32, $true); Start-Sleep -Seconds 10
        & $park; Start-Sleep -Milliseconds 500
        Shot '06-another-set.png'

        $proc.CloseMainWindow() | Out-Null
        if (-not $proc.WaitForExit(8000)) { $proc.Kill() }
        Start-Sleep -Seconds 2
    }
}
finally {
    if ($proc -and -not $proc.HasExited) {
        $proc.CloseMainWindow() | Out-Null
        if (-not $proc.WaitForExit(8000)) { $proc.Kill() }
    }
    Start-Sleep -Seconds 1
    Write-Host 'Restoring existing data' -ForegroundColor Cyan
    if (Test-Path $data) { Remove-Item $data -Recurse -Force }
    if (Test-Path $backup) { Copy-Item $backup $data -Recurse; Remove-Item $backup -Recurse -Force }
    foreach ($path in $paused | Select-Object -Unique) {
        Write-Host "Restarting: $path" -ForegroundColor Cyan
        Start-Process $path
    }
}

Write-Host "Done: $(($Language | ForEach-Object { Join-Path $outDir $_ }) -join ', ')" -ForegroundColor Green
