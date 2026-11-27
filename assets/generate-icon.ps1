Add-Type -AssemblyName System.Drawing
$iconFolder = $PSScriptRoot
$iconFrames = [System.Collections.Generic.List[byte[]]]::new()
$iconSizes = @(16, 24, 32, 48, 64, 128, 256)
foreach ($iconSize in $iconSizes) {
    $bitmap = [System.Drawing.Bitmap]::new($iconSize, $iconSize)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $graphics.ScaleTransform($iconSize / 256.0, $iconSize / 256.0)
    $outline = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $outline.AddArc(8, 8, 76, 76, 180, 90)
    $outline.AddArc(172, 8, 76, 76, 270, 90)
    $outline.AddArc(172, 172, 76, 76, 0, 90)
    $outline.AddArc(8, 172, 76, 76, 90, 90)
    $outline.CloseFigure()
    $gradient = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        [System.Drawing.Point]::new(25, 10), [System.Drawing.Point]::new(230, 246),
        [System.Drawing.Color]::FromArgb(245, 74, 101), [System.Drawing.Color]::FromArgb(165, 29, 66))
    $graphics.FillPath($gradient, $outline)
    $white = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
    $beam = [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new(102, 72), [System.Drawing.PointF]::new(192, 50),
        [System.Drawing.PointF]::new(192, 81), [System.Drawing.PointF]::new(102, 103))
    $graphics.FillPolygon($white, $beam)
    $graphics.FillRectangle($white, 102, 82, 17, 96)
    $graphics.FillRectangle($white, 175, 68, 17, 92)
    $graphics.FillEllipse($white, 68, 160, 51, 37)
    $graphics.FillEllipse($white, 141, 142, 51, 37)
    $memory = [System.IO.MemoryStream]::new()
    $bitmap.Save($memory, [System.Drawing.Imaging.ImageFormat]::Png)
    $iconFrames.Add($memory.ToArray())
    if ($iconSize -eq 256) { $bitmap.Save((Join-Path $iconFolder 'cloudmusic-preview.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $memory.Dispose(); $white.Dispose(); $gradient.Dispose(); $outline.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
}
$output = [System.IO.File]::Create((Join-Path $iconFolder 'cloudmusic.ico'))
$writer = [System.IO.BinaryWriter]::new($output)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$iconSizes.Count)
    $iconOffset = 6 + 16 * $iconSizes.Count
    for ($iconIndex = 0; $iconIndex -lt $iconSizes.Count; $iconIndex++) {
        $dimension = if ($iconSizes[$iconIndex] -eq 256) { 0 } else { $iconSizes[$iconIndex] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$iconFrames[$iconIndex].Length); $writer.Write([uint32]$iconOffset)
        $iconOffset += $iconFrames[$iconIndex].Length
    }
    foreach ($frame in $iconFrames) { $writer.Write($frame) }
} finally { $writer.Dispose(); $output.Dispose() }
