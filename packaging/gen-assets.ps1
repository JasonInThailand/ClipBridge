<#
  Regenerates the MSIX tile icons (packaging\Images) from app.ico and the Store screenshots
  (packaging\store) from docs\list.png and docs\hud.png. Run from any folder:
     powershell -ExecutionPolicy Bypass -File packaging\gen-assets.ps1
#>
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent
$img = Join-Path $PSScriptRoot "Images"; $store = Join-Path $PSScriptRoot "store"
New-Item -ItemType Directory -Force $img, $store | Out-Null

# Largest frame of app.ico. The 256px frame is PNG-compressed, which System.Drawing.Icon cannot read, so decode it directly.
$ico = [IO.File]::ReadAllBytes((Join-Path $root "app.ico"))
$count = [BitConverter]::ToUInt16($ico, 4); $bestSize = 0; $bestLen = 0; $bestOff = 0
for ($i = 0; $i -lt $count; $i++) {
    $o = 6 + 16 * $i; $w = [int]$ico[$o]; if ($w -eq 0) { $w = 256 }
    if ($w -gt $bestSize) { $bestSize = $w; $bestLen = [BitConverter]::ToUInt32($ico, $o + 8); $bestOff = [BitConverter]::ToUInt32($ico, $o + 12) }
}
$frame = New-Object byte[] $bestLen
[Array]::Copy($ico, [int]$bestOff, $frame, 0, [int]$bestLen)
if ($frame[0] -eq 0x89 -and $frame[1] -eq 0x50) { $src = [System.Drawing.Image]::FromStream((New-Object IO.MemoryStream(,$frame))) }
else { $src = (New-Object System.Drawing.Icon((Join-Path $root "app.ico"), $bestSize, $bestSize)).ToBitmap() }

function Save-Canvas([int]$w, [int]$h, [int]$x, [int]$y, [int]$size, [string]$path, $image = $src, $bg = $null) {
    $bmp = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    if ($bg) { $g.Clear($bg) } else { $g.Clear([System.Drawing.Color]::Transparent) }
    if ($size -gt 0) { $g.DrawImage($image, $x, $y, $size, $size) }
    $g.Dispose(); $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
}

Save-Canvas 44 44 0 0 44 (Join-Path $img "Square44x44Logo.png")
foreach ($t in 16, 24, 32, 48, 256) {
    Save-Canvas $t $t 0 0 $t (Join-Path $img "Square44x44Logo.targetsize-$t.png")
    Save-Canvas $t $t 0 0 $t (Join-Path $img "Square44x44Logo.targetsize-${t}_altform-unplated.png")
}
Save-Canvas 150 150 30 30 90 (Join-Path $img "Square150x150Logo.png")
Save-Canvas 50 50 0 0 50 (Join-Path $img "StoreLogo.png")
Save-Canvas 310 150 110 30 90 (Join-Path $img "Wide310x150Logo.png")

# Store screenshots, 1920x1080, from the real UI renders in docs\
$dark = [System.Drawing.Color]::FromArgb(24, 26, 30)
function Save-Shot([string]$file, [double]$targetW, [string]$out) {
    $im = [System.Drawing.Image]::FromFile((Join-Path $root "docs\$file"))
    $w = [int]$targetW; $h = [int]($im.Height * $targetW / $im.Width)
    if ($h -gt 1000) { $h = 1000; $w = [int]($im.Width * 1000 / $im.Height) }
    $bmp = New-Object System.Drawing.Bitmap(1920, 1080)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear($dark); $g.DrawImage($im, [int]((1920 - $w) / 2), [int]((1080 - $h) / 2), $w, $h)
    $g.Dispose(); $bmp.Save((Join-Path $store $out), [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose(); $im.Dispose()
}
Save-Shot "list.png" 1000 "screenshot-1-list.png"
Save-Shot "hud.png" 1820 "screenshot-2-hud.png"
Write-Host "Assets written to $img and $store"
