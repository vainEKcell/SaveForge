Add-Type -AssemblyName System.Drawing

$dir = "s:\AgentsWorkers\Tare CN\assets\icon"
$src = [System.Drawing.Bitmap]::FromFile((Join-Path $dir "SaveForge-256.png"))
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$gap = 24

[int]$width = 40 + ($sizes | ForEach-Object { $_ + $gap } | Measure-Object -Sum).Sum
[int]$height = 300
$bmp = New-Object System.Drawing.Bitmap($width, $height)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

# light strip
$g.Clear([System.Drawing.Color]::FromArgb(245, 246, 248))
$font = New-Object System.Drawing.Font("Segoe UI", 8)
$brush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(90, 90, 96))

$x = 20
$maxH = 256
foreach ($s in $sizes) {
    $y = 20 + ($maxH - $s)
    $g.DrawImage($src, $x, $y, $s, $s)
    $g.DrawString("${s}px", $font, $brush, $x, 286)
    $x += $s + $gap
}

$bmp.Save((Join-Path $dir "preview-sizes.png"), [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose()
$bmp.Dispose()
$src.Dispose()
[Console]::WriteLine("preview-sizes.png written")
