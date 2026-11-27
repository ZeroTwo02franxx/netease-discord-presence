param(
    [string]$Source = (Join-Path $PSScriptRoot 'icon-concepts\B-moonwave.png'),
    [string]$Destination = (Join-Path $PSScriptRoot 'moonwave.ico')
)
Add-Type -AssemblyName System.Drawing
$iconSizes = @(16, 24, 32, 48, 64, 128, 256)
$iconFrames = [System.Collections.Generic.List[byte[]]]::new()
$sourceImage = [System.Drawing.Image]::FromFile($Source)
try {
    foreach ($iconSize in $iconSizes) {
        $bitmap = [System.Drawing.Bitmap]::new($iconSize, $iconSize, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $attributes = [System.Drawing.Imaging.ImageAttributes]::new()
        $attributes.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)
        $graphics.DrawImage($sourceImage, [System.Drawing.Rectangle]::new(0, 0, $iconSize, $iconSize), 0, 0, $sourceImage.Width, $sourceImage.Height, [System.Drawing.GraphicsUnit]::Pixel, $attributes)
        $memory = [System.IO.MemoryStream]::new()
        $bitmap.Save($memory, [System.Drawing.Imaging.ImageFormat]::Png)
        $iconFrames.Add($memory.ToArray())
        if ($iconSize -in @(16,32,256)) { $bitmap.Save((Join-Path $PSScriptRoot "moonwave-$iconSize.png"), [System.Drawing.Imaging.ImageFormat]::Png) }
        $memory.Dispose(); $attributes.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
    }
} finally { $sourceImage.Dispose() }
$output = [System.IO.File]::Create($Destination)
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
