# Draws the app icon (assets\app.ico with 16-256 px images, and assets\app.png at 256 px):
# a white speedometer on a blue-to-violet rounded square.
Add-Type -AssemblyName System.Drawing

$root = Split-Path $PSScriptRoot
$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256

function Draw([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded square, a little inset like the Windows 11 app icons.
    $inset = [Math]::Max(0.5, $size * 0.06)
    $box = New-Object System.Drawing.RectangleF $inset, $inset, ($size - 2 * $inset), ($size - 2 * $inset)
    $radius = $box.Width * 0.24
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc($box.X, $box.Y, $d, $d, 180, 90)
    $path.AddArc($box.Right - $d, $box.Y, $d, $d, 270, 90)
    $path.AddArc($box.Right - $d, $box.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($box.X, $box.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $top = [System.Drawing.Color]::FromArgb(255, 0x1E, 0xA7, 0xF2)
    $bottom = [System.Drawing.Color]::FromArgb(255, 0x5B, 0x3F, 0xD6)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $box, $top, $bottom, 50
    $g.FillPath($brush, $path)

    # Gauge: a faint track, the white arc up to the needle, the needle and its hub.
    $cx = $size / 2.0
    $cy = $size * 0.56
    $r = $size * 0.28
    $w = [Math]::Max(1.6, $size * 0.085)
    $arc = New-Object System.Drawing.RectangleF ($cx - $r), ($cy - $r), (2 * $r), (2 * $r)
    $track = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(90, 255, 255, 255)), $w
    $track.StartCap = 'Round'; $track.EndCap = 'Round'
    $g.DrawArc($track, $arc, 150, 240)
    $fill = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), $w
    $fill.StartCap = 'Round'; $fill.EndCap = 'Round'
    $g.DrawArc($fill, $arc, 150, 165)

    $angle = (150 + 165) * [Math]::PI / 180
    $len = $r * 0.78
    $needle = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([Math]::Max(1.4, $size * 0.07))
    $needle.StartCap = 'Round'; $needle.EndCap = 'Round'
    $g.DrawLine($needle, $cx, $cy, ($cx + [Math]::Cos($angle) * $len), ($cy + [Math]::Sin($angle) * $len))
    $hub = [Math]::Max(1.5, $size * 0.075)
    $g.FillEllipse([System.Drawing.Brushes]::White, ($cx - $hub), ($cy - $hub), (2 * $hub), (2 * $hub))

    $g.Dispose()
    return $bmp
}

# ICO with PNG-compressed images (supported since Windows Vista).
$images = foreach ($s in $sizes) {
    $bmp = Draw $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($s -eq 256) { $bmp.Save((Join-Path $root "assets\app.png"), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
    , @($s, $ms.ToArray())
}

$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($img in $images) {
    $s = $img[0]; $data = $img[1]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$data.Length); $bw.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($img in $images) { $bw.Write([byte[]]$img[1]) }
$bw.Flush()
[IO.File]::WriteAllBytes((Join-Path $root "assets\app.ico"), $out.ToArray())
Write-Host "Wrote assets\app.ico and assets\app.png"
