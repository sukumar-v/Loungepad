<#
.SYNOPSIS
    Draws the app icon -- Loungepad\Loungepad.ico and loungepad-icon.png.

.DESCRIPTION
    The icon is drawn here, as shapes, at every size Windows asks for, rather than cut out of the
    master and scaled down. The master's badge is a thin glowing outline on a dark tile, and at the
    16-32 px of the tray, the taskbar and Explorer the line was a pixel or less: it smeared into the
    tile, and the dark tile vanished on a dark taskbar. So the drawing is the badge's own, made solid:
    a dark silhouette on an Ember tile, which holds its shape down to 16 px and stands out on a light
    or a dark taskbar alike.

    It is the badge's outline, line for line, and that outline is what makes it both things at once.
    The outside is a controller's: each leg drops STRAIGHT down on its outer side and slants in on its
    inner side to a flat seat bottom, with no arch under it. The inside is a sofa's: a backrest behind,
    and one seam that runs over each arm's top, down its inner side, and across the cushion in a
    gentle curve. Two earlier draws got this wrong -- upright arms with no slant read as an armchair
    only, and arms tilted whole, with a round arch between them, read as neither.

    Laid out on a 256 grid and scaled to each size, so every frame is drawn at its own resolution.
    Below 32 px the seam is left out: under a pixel it only blurs the outline, and the silhouette on
    its own is still the badge.

    20, 24 and 40 px are there for the tray and small icons at 125-150% scaling; without them
    Windows scales the 16 or the 32 and the outline goes soft.

    The master lockup (assets\raw\Loungepad_logo.jpg) is the reference the outline comes from; the
    README's header lockup, assets\loungepad-logo-dark.png and -light.png, is drawn with this icon
    elsewhere and is not written here.

.EXAMPLE
    .\tools\make-icon.ps1
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repo = Split-Path -Parent $PSScriptRoot

# A point. Built through a function because New-Object's (a, b + c) argument syntax binds the comma
# before the plus, which turned more than one coordinate here into an array.
function P([double]$x, [double]$y) { New-Object System.Drawing.PointF -ArgumentList ([single]$x), ([single]$y) }

function New-RoundRect([double]$x, [double]$y, [double]$w, [double]$h, [double]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [Math]::Min(2 * $r, [Math]::Min($w, $h))
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

# A polygon with every corner rounded: one @(x, y, r) per vertex, in order. Each corner is a cubic
# from the point r back along one edge to the point r along the next, pulled toward the vertex --
# close to a circular fillet, and it rounds an inside corner as readily as an outside one.
function New-RoundPoly($pts) {
    $n = $pts.Count
    $segs = @()
    for ($i = 0; $i -lt $n; $i++) {
        $p = $pts[$i]; $a = $pts[($i - 1 + $n) % $n]; $b = $pts[($i + 1) % $n]
        $ux = $a[0] - $p[0]; $uy = $a[1] - $p[1]; $ul = [Math]::Sqrt($ux * $ux + $uy * $uy)
        $vx = $b[0] - $p[0]; $vy = $b[1] - $p[1]; $vl = [Math]::Sqrt($vx * $vx + $vy * $vy)
        $t = [Math]::Min($p[2], [Math]::Min($ul, $vl) / 2)
        $t1x = $p[0] + $ux / $ul * $t; $t1y = $p[1] + $uy / $ul * $t
        $t2x = $p[0] + $vx / $vl * $t; $t2y = $p[1] + $vy / $vl * $t
        $k = 0.55
        $segs += , @((P $t1x $t1y), (P ($t1x + ($p[0] - $t1x) * $k) ($t1y + ($p[1] - $t1y) * $k)),
                     (P ($t2x + ($p[0] - $t2x) * $k) ($t2y + ($p[1] - $t2y) * $k)), (P $t2x $t2y))
    }
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    for ($i = 0; $i -lt $n; $i++) {
        $s = $segs[$i]
        $path.AddBezier($s[0], $s[1], $s[2], $s[3])
        $path.AddLine($s[3], $segs[($i + 1) % $n][0])
    }
    $path.CloseFigure()
    return $path
}

function New-Icon([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppPArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.CompositingQuality = 'HighQuality'
    $g.ScaleTransform($size / 256.0, $size / 256.0)

    # The tile: Ember (#F0A253, the launcher's default accent), lighter at the top. Everything after
    # it is clipped to it, and the seam is painted in the same gradient, so it reads as cut out.
    $tilePath = New-RoundRect 6 6 244 244 58
    $tile = New-Object System.Drawing.Drawing2D.LinearGradientBrush((P 0 6), (P 0 250),
        [System.Drawing.Color]::FromArgb(255, 0xF8, 0xB7, 0x72), [System.Drawing.Color]::FromArgb(255, 0xE4, 0x82, 0x37))
    $g.FillPath($tile, $tilePath)
    $g.SetClip($tilePath)
    $ink = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 0x17, 0x17, 0x1C))

    # On the 256 grid. NB: PowerShell variables ignore case, so nothing below may be called $l or $r.
    $left = 40; $right = 216          # the legs' straight outer sides
    $arm = 40                         # an arm's width
    $armTop = 92; $seatTop = 128      # the arms' tops, and the seat between them
    $foot = 200                       # the bottom of the legs
    $legW = 34                        # a leg's width at the bottom
    $under = 160; $underL = 98        # the seat's flat underside, and where its slants begin

    # The backrest, behind the arms.
    $back = New-RoundRect 66 56 124 78 26
    $g.FillPath($ink, $back)
    # Arms, seat and legs as one outline: over each arm, straight down the outside, slanting in on
    # the inside to the seat's flat underside.
    $body = New-RoundPoly @(
        @($left, $armTop, ($arm / 2)), @(($left + $arm), $armTop, ($arm / 2)),
        @(($left + $arm), $seatTop, 6), @(($right - $arm), $seatTop, 6),
        @(($right - $arm), $armTop, ($arm / 2)), @($right, $armTop, ($arm / 2)),
        @($right, $foot, 18), @(($right - $legW), $foot, 10),
        @((256 - $underL), $under, 12), @($underL, $under, 12),
        @(($left + $legW), $foot, 10), @($left, $foot, 18)
    )
    $g.FillPath($ink, $body)

    # The seam, from 32 px up: over the left arm's top and down its inner side, across the cushion in
    # a gentle curve, and up and over the right arm. It runs just outside the arms, so it parts them
    # from the backrest and the seat without eating into them; outside the outline it is tile on tile.
    if ($size -ge 32) {
        $w = 8
        $pen = New-Object System.Drawing.Pen($tile, $w)
        $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
        $rad = $arm / 2 + $w / 2
        $cy = $armTop + $arm / 2
        $xi = $left + $arm + $w / 2; $xj = $right - $arm - $w / 2
        $cushion = 128; $sag = 8
        $seam = New-Object System.Drawing.Drawing2D.GraphicsPath
        $seam.AddArc([single]($left + $arm / 2 - $rad), [single]($cy - $rad), [single](2 * $rad), [single](2 * $rad), 180, 180)
        $seam.AddLine((P $xi $cy), (P $xi $cushion))
        $seam.AddBezier((P $xi $cushion), (P ($xi + 30) ($cushion + $sag)), (P ($xj - 30) ($cushion + $sag)), (P $xj $cushion))
        $seam.AddLine((P $xj $cushion), (P $xj $cy))
        $seam.AddArc([single]($right - $arm / 2 - $rad), [single]($cy - $rad), [single](2 * $rad), [single](2 * $rad), 180, 180)
        $g.DrawPath($pen, $seam)
        $seam.Dispose(); $pen.Dispose()
    }

    $back.Dispose(); $body.Dispose(); $ink.Dispose(); $tile.Dispose(); $tilePath.Dispose()
    $g.Dispose()
    return $bmp
}

function Get-Png([int]$size) {
    $bmp = New-Icon $size
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

# The PNG beside the repo root, as before.
[System.IO.File]::WriteAllBytes((Join-Path $repo 'loungepad-icon.png'), (Get-Png 512))

# An .ico of PNG frames: header, one 16-byte entry per frame, then the frames.
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$frames = foreach ($s in $sizes) { , (Get-Png $s) }
$ico = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($ico)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$frames[$i].Length); $w.Write([uint32]$offset)
    $offset += $frames[$i].Length
}
foreach ($f in $frames) { $w.Write($f) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $repo 'Loungepad\Loungepad.ico'), $ico.ToArray())
$w.Dispose()

Write-Host "Wrote Loungepad\Loungepad.ico ($($sizes -join ', ') px) and loungepad-icon.png"
