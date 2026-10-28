# Win11 Fluent 风格图标：连续圆角底板 + 对角渐变 + 白色文档卡片 + 编辑标记
Add-Type -AssemblyName System.Drawing

$outDir = "s:\AgentsWorkers\Tare CN\assets\icon"
if (!(Test-Path $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }

$S = 256
$bmp = New-Object System.Drawing.Bitmap($S, $S, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$g.Clear([System.Drawing.Color]::Transparent)

function New-RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

# ---------- 1. 底板：连续圆角 + 对角渐变 ----------
$plate = New-RoundedPath 12 12 232 232 54

$grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    (New-Object System.Drawing.Point(24, 20)),
    (New-Object System.Drawing.Point(232, 244)),
    [System.Drawing.Color]::FromArgb(255, 88, 132, 255),
    [System.Drawing.Color]::FromArgb(255, 124, 92, 240))
$g.FillPath($grad, $plate)

# 顶部高光（模拟 Fluent 亚克力分层）
$glossPath = New-RoundedPath 12 12 232 116 54
$glossBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    (New-Object System.Drawing.Point(0, 12)),
    (New-Object System.Drawing.Point(0, 128)),
    [System.Drawing.Color]::FromArgb(46, 255, 255, 255),
    [System.Drawing.Color]::FromArgb(0, 255, 255, 255))
$g.FillPath($glossBrush, $glossPath)

# 内描边：Fluent 图标典型的边缘收口，让底板在浅色背景上也有轮廓
$edgePath = New-RoundedPath 13 13 230 230 53
$edgePen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(56, 255, 255, 255), 1.6)
$g.DrawPath($edgePen, $edgePath)

# ---------- 2. 文档卡片（白色，悬浮在底板上） ----------
$docX = 74.0; $docY = 58.0; $docW = 108.0; $docH = 140.0; $docR = 16.0

# 投影
$shadowPath = New-RoundedPath ($docX + 2) ($docY + 6) $docW $docH $docR
$shadowBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(40, 20, 30, 70))
$g.FillPath($shadowBrush, $shadowPath)

$docPath = New-RoundedPath $docX $docY $docW $docH $docR
$docBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 252, 253, 255))
$g.FillPath($docBrush, $docPath)

# ---------- 3. 文档内的数据行（三条，长度递减） ----------
$lineBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 96, 116, 158))
$lineW = 10.0
$rows = @(
    @(94.0, 90.0, 56.0),
    @(94.0, 114.0, 44.0),
    @(94.0, 138.0, 30.0)
)
foreach ($row in $rows) {
    $lp = New-RoundedPath $row[0] $row[1] $row[2] $lineW 5
    $g.FillPath($lineBrush, $lp)
}

# ---------- 4. 右下角编辑标记（渐变圆点 + 白色对勾） ----------
$dotPath = New-Object System.Drawing.Drawing2D.GraphicsPath
$dotPath.AddEllipse(164, 148, 52, 52)

$dotGrad = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    (New-Object System.Drawing.Point(164, 148)),
    (New-Object System.Drawing.Point(216, 200)),
    [System.Drawing.Color]::FromArgb(255, 255, 196, 74),
    [System.Drawing.Color]::FromArgb(255, 255, 148, 52))
$g.FillPath($dotGrad, $dotPath)

$pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, 7.0)
$pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
$g.DrawLines($pen, @(
    (New-Object System.Drawing.Point(177, 175)),
    (New-Object System.Drawing.Point(186, 185)),
    (New-Object System.Drawing.Point(203, 163))
))

# ---------- 导出 PNG 预览 ----------
$bmp.Save((Join-Path $outDir "SaveForge-256.png"), [System.Drawing.Imaging.ImageFormat]::Png)

# ---------- 打包 ICO（多尺寸，PNG 压缩） ----------
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$pngData = @()

foreach ($s in $sizes) {
    $small = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $sg = [System.Drawing.Graphics]::FromImage($small)
    $sg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $sg.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $sg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $sg.Clear([System.Drawing.Color]::Transparent)
    $sg.DrawImage($bmp, 0, 0, $s, $s)
    $sg.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $small.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngData += , $ms.ToArray()
    $small.Dispose()
    $ms.Dispose()
}

$icoPath = Join-Path $outDir "SaveForge.ico"
$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter($fs)

$bw.Write([UInt16]0)
$bw.Write([UInt16]1)
$bw.Write([UInt16]$sizes.Count)

$offset = 6 + (16 * $sizes.Count)
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    if ($s -ge 256) { $dim = 0 } else { $dim = $s }
    $bw.Write([byte]$dim)
    $bw.Write([byte]$dim)
    $bw.Write([byte]0)
    $bw.Write([byte]0)
    $bw.Write([UInt16]1)
    $bw.Write([UInt16]32)
    $bw.Write([UInt32]$pngData[$i].Length)
    $bw.Write([UInt32]$offset)
    $offset += $pngData[$i].Length
}
foreach ($d in $pngData) { $bw.Write($d) }
$bw.Flush()
$bw.Close()
$fs.Close()

$g.Dispose()
$bmp.Dispose()

[Console]::WriteLine("PNG: " + (Join-Path $outDir "SaveForge-256.png"))
[Console]::WriteLine("ICO: $icoPath  ($([math]::Round((Get-Item $icoPath).Length / 1KB, 1)) KB, $($sizes.Count) sizes)")
