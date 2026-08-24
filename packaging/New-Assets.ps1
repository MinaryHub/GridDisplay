<#
.SYNOPSIS
    Regenerates the package logos and the exe icon.

.DESCRIPTION
    The tiles in the glyph are the app: a 3x2 wall with one cell zoomed. Drawn in code rather than
    checked in as opaque binaries so a colour or shape change is a one-line edit and every size
    stays consistent. Run it after editing the palette below; the PNGs it writes are committed.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File packaging\New-Assets.ps1
#>
[CmdletBinding()]
param(
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot 'Assets' }
Add-Type -AssemblyName System.Drawing

# Matches MainForm's BackgroundColor / AccentColor, so the tile reads as the app it launches.
$background = [System.Drawing.Color]::FromArgb(255, 14, 14, 16)
$tile       = [System.Drawing.Color]::FromArgb(255, 58, 62, 74)
$accent     = [System.Drawing.Color]::FromArgb(255, 70, 160, 255)

function New-GridBitmap {
    param(
        [int] $Width,
        [int] $Height,
        [switch] $Transparent
    )

    $bmp = New-Object System.Drawing.Bitmap($Width, $Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        if ($Transparent) {
            $g.Clear([System.Drawing.Color]::Transparent)
        } else {
            $g.Clear($background)
        }

        # 3x2 grid inset from the edges, with the top-left cell drawn in the accent colour to
        # stand for the zoomed cell.
        $cols = 3
        $rows = 2
        $margin = [Math]::Max(1, [int]([Math]::Min($Width, $Height) * 0.14))
        $gap    = [Math]::Max(1, [int]([Math]::Min($Width, $Height) * 0.07))
        $innerW = $Width  - ($margin * 2) - ($gap * ($cols - 1))
        $innerH = $Height - ($margin * 2) - ($gap * ($rows - 1))
        if ($innerW -le 0 -or $innerH -le 0) { throw "Canvas ${Width}x${Height} is too small to draw into." }

        $tileBrush   = New-Object System.Drawing.SolidBrush($tile)
        $accentBrush = New-Object System.Drawing.SolidBrush($accent)
        try {
            for ($r = 0; $r -lt $rows; $r++) {
                for ($c = 0; $c -lt $cols; $c++) {
                    $x = $margin + ($gap * $c) + [int]($innerW * $c / $cols)
                    $w = $margin + ($gap * $c) + [int]($innerW * ($c + 1) / $cols) - $x
                    $y = $margin + ($gap * $r) + [int]($innerH * $r / $rows)
                    $h = $margin + ($gap * $r) + [int]($innerH * ($r + 1) / $rows) - $y
                    $brush = if ($r -eq 0 -and $c -eq 0) { $accentBrush } else { $tileBrush }
                    $g.FillRectangle($brush, $x, $y, [Math]::Max(1, $w), [Math]::Max(1, $h))
                }
            }
        } finally {
            $tileBrush.Dispose()
            $accentBrush.Dispose()
        }
    } finally {
        $g.Dispose()
    }
    return $bmp
}

function Save-Png {
    param([int] $Width, [int] $Height, [string] $Path, [switch] $Transparent)

    $bmp = New-GridBitmap -Width $Width -Height $Height -Transparent:$Transparent
    try {
        $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
        Write-Host "  $([IO.Path]::GetFileName($Path))  ${Width}x${Height}"
    } finally {
        $bmp.Dispose()
    }
}

# ICO written by hand as a container of PNG frames (supported since Vista). System.Drawing can
# read icons but not author multi-size ones, and a single-size icon looks wrong in the task bar.
function Save-Ico {
    param([string] $Path, [int[]] $Sizes = @(16, 32, 48, 64, 256))

    $frames = foreach ($size in $Sizes) {
        $bmp = New-GridBitmap -Width $size -Height $size
        try {
            $ms = New-Object System.IO.MemoryStream
            $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
            [pscustomobject]@{ Size = $size; Bytes = $ms.ToArray() }
        } finally {
            $bmp.Dispose()
            if ($ms) { $ms.Dispose() }
        }
    }

    $fs = [System.IO.File]::Create($Path)
    $bw = New-Object System.IO.BinaryWriter($fs)
    try {
        $bw.Write([uint16]0)                 # reserved
        $bw.Write([uint16]1)                 # type: icon
        $bw.Write([uint16]$frames.Count)

        # Image data starts after the 6-byte header plus one 16-byte directory entry per frame.
        $offset = 6 + (16 * $frames.Count)
        foreach ($frame in $frames) {
            # 256 is encoded as 0 in the directory, which is why the field is a single byte.
            $bw.Write([byte]($frame.Size % 256))
            $bw.Write([byte]($frame.Size % 256))
            $bw.Write([byte]0)               # palette count
            $bw.Write([byte]0)               # reserved
            $bw.Write([uint16]1)             # colour planes
            $bw.Write([uint16]32)            # bits per pixel
            $bw.Write([uint32]$frame.Bytes.Length)
            $bw.Write([uint32]$offset)
            $offset += $frame.Bytes.Length
        }
        foreach ($frame in $frames) { $bw.Write($frame.Bytes) }
    } finally {
        $bw.Dispose()
        $fs.Dispose()
    }
    Write-Host "  $([IO.Path]::GetFileName($Path))  $($Sizes -join '/')"
}

if (-not (Test-Path $OutputDirectory)) { New-Item -ItemType Directory -Path $OutputDirectory | Out-Null }

Write-Host "Writing assets to $OutputDirectory"
Save-Png -Width 44  -Height 44  -Path (Join-Path $OutputDirectory 'Square44x44Logo.png')
Save-Png -Width 150 -Height 150 -Path (Join-Path $OutputDirectory 'Square150x150Logo.png')
Save-Png -Width 310 -Height 150 -Path (Join-Path $OutputDirectory 'Wide310x150Logo.png')
Save-Png -Width 50  -Height 50  -Path (Join-Path $OutputDirectory 'StoreLogo.png')
# Unplated: what the task bar and Start's icon-only surfaces show, so it has no square behind it.
Save-Png -Width 44  -Height 44  -Path (Join-Path $OutputDirectory 'Square44x44Logo.targetsize-44_altform-unplated.png') -Transparent
Save-Ico -Path (Join-Path $OutputDirectory 'GridDisplay.ico')
Write-Host 'Done.'
