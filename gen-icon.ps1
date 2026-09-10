# Generates app.ico (PNG-compressed multi-size icon) with System.Drawing.
Add-Type -AssemblyName System.Drawing
$sizes = 16, 24, 32, 48, 64, 128, 256
$frames = @()
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.Clear([System.Drawing.Color]::Transparent)
    $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(59, 130, 246))
    $pad = [Math]::Max(1, [int]($s * 0.04))
    $rect = New-Object System.Drawing.Rectangle $pad, $pad, ($s - 2 * $pad), ($s - 2 * $pad)
    $radius = [int]($s * 0.22)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $g.FillPath($brush, $path)
    # clipboard clip at the top
    $clipBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(230, 240, 255))
    $cw = [int]($s * 0.36); $ch = [Math]::Max(1, [int]($s * 0.12))
    $g.FillRectangle($clipBrush, [int](($s - $cw) / 2), $pad, $cw, $ch)
    $font = New-Object System.Drawing.Font "Segoe UI", ([float]($s * 0.55)), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $fmt = New-Object System.Drawing.StringFormat
    $fmt.Alignment = [System.Drawing.StringAlignment]::Center
    $fmt.LineAlignment = [System.Drawing.StringAlignment]::Center
    $textRect = New-Object System.Drawing.RectangleF 0, ($s * 0.08), $s, $s
    $g.DrawString("9", $font, [System.Drawing.Brushes]::White, $textRect, $fmt)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $frames += ,@{ Size = $s; Bytes = $ms.ToArray() }
    $bmp.Dispose()
}
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $sz = $f.Size; if ($sz -ge 256) { $sz = 0 }
    $bw.Write([Byte]$sz); $bw.Write([Byte]$sz); $bw.Write([Byte]0); $bw.Write([Byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$f.Bytes.Length); $bw.Write([UInt32]$offset)
    $offset += $f.Bytes.Length
}
foreach ($f in $frames) { $bw.Write($f.Bytes) }
$bw.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $PSScriptRoot "app.ico"), $out.ToArray())
Write-Host "app.ico written ($($out.Length) bytes)"
