# make-icon.ps1 -- build CutTimer's app icon: character art on a white rounded square.
#
# Pipeline: crop the source to its real content box -> fit that into the rounded
# plate -> clip to the rounded path. Output is one .ico holding
# 16/24/32/48/64/128/256 so Windows picks the right size per context (taskbar,
# alt-tab, explorer, title bar, tray).
#
# Why it crops before scaling:
#
#   The source has its own margins (its content box starts at x=9, y=1) AND the
#   previous version additionally scaled the art down to 90%. The two stacked up
#   into a very visible white border, especially at the bottom.
#
#   So: crop to the alpha box first, then fit that to the plate at artRatio = 1.0.
#   That keeps the whole figure (nothing gets cut, unlike a 1.1 overscale) while
#   letting it fill the tile, so the white plate only shows at the rounded corners.
#
# On DrawImage overloads: this uses the explicit
# DrawImage(img, destRect, srcRect, GraphicsUnit.Pixel) form. I originally
# suspected the shorter DrawImage(img, destRect) was scaling by physical size
# (the source reports 120 DPI through some paths, 96 through others, which made
# that plausible) -- but measuring both side by side produced identical output,
# so that was NOT what caused the border. The explicit overload is kept anyway
# because it does not depend on the image's DPI metadata at all.
#
# ASCII-only on purpose: powershell.exe reads .ps1 as ANSI unless there is a
# BOM, which mangles non-ASCII bytes inside string literals and breaks parsing.

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# Resolve everything relative to this script so the repo is reproducible on any
# machine -- the previous version hard-coded absolute paths from the dev box.
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $here

$srcPng = Join-Path $here 'dfy-source.png'
$outIco = Join-Path $projectRoot 'Assets\CutTimer.ico'
$outPreview = Join-Path $projectRoot 'Assets\CutTimer-preview.png'

if (-not (Test-Path $srcPng)) { throw "source art not found: $srcPng" }

$sizes = @(16, 24, 32, 48, 64, 128, 256)

# Corner radius as a fraction of the side -- 0.22 is close to the Windows / iOS
# app-icon look.
$cornerRatio = 0.22

# How much of the plate the art fills. 1.0 makes the character fill the tile so
# the white plate only shows at the rounded corners; below ~0.95 a visible white
# margin reappears around the whole figure.
$artRatio = 1.0

# Alpha cutoff when working out the content box. Low enough to keep soft hair
# edges, high enough to ignore stray near-transparent pixels.
$alphaCut = 32

function New-RoundedPath([int]$size, [double]$ratio) {
  $r = [int][Math]::Round($size * $ratio)
  $d = $r * 2
  $path = New-Object System.Drawing.Drawing2D.GraphicsPath
  $path.AddArc(0, 0, $d, $d, 180, 90)
  $path.AddArc($size - $d, 0, $d, $d, 270, 90)
  $path.AddArc($size - $d, $size - $d, $d, $d, 0, 90)
  $path.AddArc(0, $size - $d, $d, $d, 90, 90)
  $path.CloseFigure()
  return $path
}

function Get-ContentBox([System.Drawing.Bitmap]$bmp, [int]$cut) {
  $minX = $bmp.Width; $minY = $bmp.Height; $maxX = -1; $maxY = -1
  for ($y = 0; $y -lt $bmp.Height; $y++) {
    for ($x = 0; $x -lt $bmp.Width; $x++) {
      if ($bmp.GetPixel($x, $y).A -gt $cut) {
        if ($x -lt $minX) { $minX = $x }
        if ($x -gt $maxX) { $maxX = $x }
        if ($y -lt $minY) { $minY = $y }
        if ($y -gt $maxY) { $maxY = $y }
      }
    }
  }
  return New-Object System.Drawing.Rectangle($minX, $minY, ($maxX - $minX + 1), ($maxY - $minY + 1))
}

function New-IconBitmap([System.Drawing.Bitmap]$src, [System.Drawing.Rectangle]$box, [int]$sz) {
  $bmp = New-Object System.Drawing.Bitmap($sz, $sz, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
  $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
  $g.Clear([System.Drawing.Color]::Transparent)

  $path = New-RoundedPath $sz $cornerRatio

  # White plate. A hairline grey outline keeps the icon readable when it sits on
  # a white background (Explorer, light taskbar) -- without it the edges vanish.
  $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
  $g.FillPath($white, $path)
  $white.Dispose()
  $edge = [single]([Math]::Max(1.0, $sz * 0.006))
  $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(38, 0, 0, 0), $edge)
  $g.DrawPath($pen, $path)
  $pen.Dispose()

  # Fit the cropped art inside artRatio of the plate, centred, keeping aspect.
  $target = [double]$sz * $artRatio
  $scale = [Math]::Min($target / $box.Width, $target / $box.Height)
  $aw = [int][Math]::Round($box.Width * $scale)
  $ah = [int][Math]::Round($box.Height * $scale)
  $ox = [int][Math]::Round(($sz - $aw) / 2)
  $oy = [int][Math]::Round(($sz - $ah) / 2)

  # Clip to the rounded plate so nothing spills past a corner.
  $g.SetClip($path)
  $dest = New-Object System.Drawing.Rectangle($ox, $oy, $aw, $ah)
  $g.DrawImage($src, $dest, $box, [System.Drawing.GraphicsUnit]::Pixel)
  $g.ResetClip()

  $path.Dispose()
  $g.Dispose()
  return $bmp
}

$src = New-Object System.Drawing.Bitmap($srcPng)
$box = Get-ContentBox $src $alphaCut
Write-Output ("source: {0}x{1} @{2}dpi  content box: {3},{4} {5}x{6}" -f `
  $src.Width, $src.Height, $src.HorizontalResolution, $box.X, $box.Y, $box.Width, $box.Height)

$preview = New-IconBitmap $src $box 256
$preview.Save($outPreview, [System.Drawing.Imaging.ImageFormat]::Png)
$preview.Dispose()
Write-Output ("preview: {0}" -f $outPreview)

$blobs = New-Object System.Collections.Generic.List[byte[]]
foreach ($sz in $sizes) {
  $ib = New-IconBitmap $src $box $sz
  $ms = New-Object System.IO.MemoryStream
  $ib.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
  $blobs.Add($ms.ToArray())
  Write-Output ("  {0}x{0}: {1} bytes" -f $sz, $blobs[-1].Length)
  $ib.Dispose()
  $ms.Dispose()
}

$fs = [System.IO.File]::Create($outIco)
$bwr = New-Object System.IO.BinaryWriter($fs)
$bwr.Write([uint16]0)                 # reserved
$bwr.Write([uint16]1)                 # type: icon
$bwr.Write([uint16]$sizes.Count)      # image count

$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $sz = $sizes[$i]
  $wv = if ($sz -ge 256) { 0 } else { $sz }   # 256 is encoded as 0
  $bwr.Write([byte]$wv)
  $bwr.Write([byte]$wv)
  $bwr.Write([byte]0)                 # palette colours
  $bwr.Write([byte]0)                 # reserved
  $bwr.Write([uint16]1)               # colour planes
  $bwr.Write([uint16]32)              # bits per pixel
  $bwr.Write([uint32]$blobs[$i].Length)
  $bwr.Write([uint32]$offset)
  $offset += $blobs[$i].Length
}
foreach ($b in $blobs) { $bwr.Write($b) }
$bwr.Flush(); $bwr.Dispose(); $fs.Dispose()

Write-Output ("ICO written: {0} ({1} bytes)" -f $outIco, (Get-Item $outIco).Length)
$src.Dispose()
