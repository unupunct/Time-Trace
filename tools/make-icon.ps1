# Generates Assets/TimeTrace.ico and TimeTrace.png: a clock combined with a timeline (dark tile, green accent).
param([string]$OutDir = "$PSScriptRoot\..\src\TimeTrace.App\Assets")
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $OutDir | Out-Null

function New-IconBitmap([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.Clear([System.Drawing.Color]::Transparent)
    $green = [System.Drawing.Color]::FromArgb(255, 61, 220, 132)
    $dark1 = [System.Drawing.Color]::FromArgb(255, 34, 38, 44)
    $dark2 = [System.Drawing.Color]::FromArgb(255, 14, 16, 19)

    # rounded tile
    $r = $s * 0.22
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc(0, 0, $d, $d, 180, 90); $path.AddArc($s - $d - 1, 0, $d, $d, 270, 90)
    $path.AddArc($s - $d - 1, $s - $d - 1, $d, $d, 0, 90); $path.AddArc(0, $s - $d - 1, $d, $d, 90, 90); $path.CloseFigure()
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, 0)), ([System.Drawing.PointF]::new($s, $s)), $dark1, $dark2
    $g.FillPath($brush, $path)

    # clock ring (upper area)
    $cx = $s * 0.5; $cy = $s * 0.43; $rad = $s * 0.29
    $pw = [Math]::Max(1.6, $s * 0.075)
    $pen = New-Object System.Drawing.Pen $green, $pw
    $g.DrawEllipse($pen, $cx - $rad, $cy - $rad, 2 * $rad, 2 * $rad)
    # hands
    $hand = New-Object System.Drawing.Pen $green, ([Math]::Max(1.5, $s * 0.07))
    $hand.StartCap = 'Round'; $hand.EndCap = 'Round'
    $g.DrawLine($hand, $cx, $cy, $cx, $cy - $rad * 0.62)
    $g.DrawLine($hand, $cx, $cy, $cx + $rad * 0.48, $cy + $rad * 0.28)

    # timeline segments under the clock
    $ty = $s * 0.80; $th = [Math]::Max(2, $s * 0.085); $x0 = $s * 0.16; $w = $s * 0.68
    $segs = @(@(0.0, 0.34, 1.0), @(0.38, 0.22, 0.55), @(0.64, 0.36, 0.8))
    foreach ($seg in $segs) {
        $a = [int](255 * $seg[2])
        $c = [System.Drawing.Color]::FromArgb($a, 61, 220, 132)
        $b = New-Object System.Drawing.SolidBrush $c
        $g.FillRectangle($b, [float]($x0 + $w * $seg[0]), [float]$ty, [float]($w * $seg[1]), [float]$th)
    }
    $g.Dispose()
    return $bmp
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = @()
foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , @($s, $ms.ToArray())
    if ($s -eq 256) { $bmp.Save("$OutDir\TimeTrace.png", [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
}
$fs = [System.IO.File]::Create("$OutDir\TimeTrace.ico")
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$pngs.Count)
$offset = 6 + 16 * $pngs.Count
foreach ($p in $pngs) {
    $s = $p[0]; $data = $p[1]
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$data.Length); $bw.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($p in $pngs) { $bw.Write($p[1]) }
$bw.Close(); $fs.Close()
Write-Host "Icon written to $OutDir"
