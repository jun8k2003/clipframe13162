# Generates Assets/appicon.ico — a multi-size PNG-in-ICO for ClipFrame.
# Motif: a "clip frame" (rounded hollow square) with a tag and a corner grab handle.
Add-Type -AssemblyName System.Drawing

$ErrorActionPreference = "Stop"
$outDir = Join-Path $PSScriptRoot "..\Assets"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$out = Join-Path $outDir "appicon.ico"

$accent = [System.Drawing.Color]::FromArgb(255, 0x2F, 0xA8, 0xFF)  # #2FA8FF
$tagCol = [System.Drawing.Color]::FromArgb(255, 0x1E, 0x6F, 0xB0)  # darker blue
$sizes  = 16, 24, 32, 48, 64, 128, 256

function New-FramePng([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $m  = [math]::Max(1.0, $s * 0.16)          # margin
    $pw = [math]::Max(1.5, $s * 0.12)          # frame stroke width
    $x  = $m; $y = $m
    $w  = $s - 2 * $m; $h = $s - 2 * $m
    $r  = [math]::Max(1.5, $s * 0.20)          # corner radius

    # Rounded-rectangle path
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $path.AddArc($x,          $y,          $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y,          $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d,   0, 90)
    $path.AddArc($x,          $y + $h - $d, $d, $d,  90, 90)
    $path.CloseFigure()

    $pen = New-Object System.Drawing.Pen($accent, [single]$pw)
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $g.DrawPath($pen, $path)

    # Tag: small filled rounded rect hanging from the top edge (inside).
    $tagW = $w * 0.42; $tagH = [math]::Max(2.0, $s * 0.12)
    $tagX = $x + ($w - $tagW) / 2.0; $tagY = $y - $pw/2.0
    $tagBrush = New-Object System.Drawing.SolidBrush($tagCol)
    $g.FillRectangle($tagBrush, [single]$tagX, [single]$tagY, [single]$tagW, [single]$tagH)

    # Corner grab handle (bottom-right), filled accent square.
    $hs = [math]::Max(2.0, $s * 0.16)
    $g.FillRectangle((New-Object System.Drawing.SolidBrush($accent)),
        [single]($x + $w - $hs/2.0), [single]($y + $h - $hs/2.0), [single]$hs, [single]$hs)

    $pen.Dispose(); $tagBrush.Dispose(); $g.Dispose(); $path.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return ,$ms.ToArray()
}

# Assemble ICO (PNG-compressed entries — supported on Vista+).
$pngs = @{}
foreach ($s in $sizes) { $pngs[$s] = New-FramePng $s }

$fs = New-Object System.IO.FileStream($out, [System.IO.FileMode]::Create)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([UInt16]0)              # reserved
$bw.Write([UInt16]1)              # type = icon
$bw.Write([UInt16]$sizes.Count)   # image count

$offset = 6 + 16 * $sizes.Count
foreach ($s in $sizes) {
    $data = $pngs[$s]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([Byte]$dim)         # width
    $bw.Write([Byte]$dim)         # height
    $bw.Write([Byte]0)            # palette
    $bw.Write([Byte]0)            # reserved
    $bw.Write([UInt16]1)          # planes
    $bw.Write([UInt16]32)         # bpp
    $bw.Write([UInt32]$data.Length)
    $bw.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($s in $sizes) { $bw.Write($pngs[$s]) }
$bw.Flush(); $bw.Close(); $fs.Close()

Write-Host ("Wrote {0} ({1} bytes, {2} sizes)" -f $out, (Get-Item $out).Length, $sizes.Count)
