[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase
$assetDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../src/HS2Manager.NativeApp/Assets/Brand'))
[xml]$svg = Get-Content -LiteralPath (Join-Path $assetDirectory 'hs2-manager.svg') -Raw
$culture = [Globalization.CultureInfo]::InvariantCulture

# This renderer deliberately supports only the paths, rotations and gradient in
# the adjacent source SVG. The SVG remains the editable source of truth.
function Draw-BrandNode($context, $node) {
    foreach ($child in $node.ChildNodes) {
        if ($child.LocalName -eq 'g') {
            if ($child.transform -notmatch '^rotate\((-?[\d.]+) ([\d.]+) ([\d.]+)\)$') { throw 'Unsupported SVG transform.' }
            $context.PushTransform([Windows.Media.RotateTransform]::new(
                [double]::Parse($Matches[1], $culture), [double]::Parse($Matches[2], $culture), [double]::Parse($Matches[3], $culture)))
            Draw-BrandNode $context $child
            $context.Pop()
        }
        elseif ($child.LocalName -eq 'path') {
            if ($child.fill -eq 'url(#forest)') {
                $gradient = $svg.svg.defs.linearGradient
                $brush = [Windows.Media.LinearGradientBrush]::new()
                $brush.StartPoint = [Windows.Point]::new(0, 0)
                $brush.EndPoint = [Windows.Point]::new(1, 1)
                foreach ($stop in $gradient.stop) {
                    $color = [Windows.Media.ColorConverter]::ConvertFromString($stop.'stop-color')
                    $brush.GradientStops.Add([Windows.Media.GradientStop]::new($color, [double]::Parse($stop.offset, $culture)))
                }
            }
            else { $brush = [Windows.Media.BrushConverter]::new().ConvertFromInvariantString($child.fill) }
            $geometry = [Windows.Media.Geometry]::Parse($child.d)
            $context.DrawGeometry($brush, $null, $geometry)
        }
        elseif ($child.LocalName -notin @('title', 'desc', 'defs', '#comment')) { throw "Unsupported SVG element: $($child.LocalName)" }
    }
}

function Get-BrandPng([int]$size) {
    $visual = [Windows.Media.DrawingVisual]::new()
    $context = $visual.RenderOpen()
    try {
        $context.PushTransform([Windows.Media.ScaleTransform]::new($size / 1024.0, $size / 1024.0))
        Draw-BrandNode $context $svg.svg
        $context.Pop()
    }
    finally { $context.Close() }
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.MemoryStream]::new()
    try { $encoder.Save($stream); return ,$stream.ToArray() }
    finally { $stream.Dispose() }
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$images = @{}
foreach ($size in $sizes + @(1024)) { $images[$size] = Get-BrandPng $size }
[IO.File]::WriteAllBytes((Join-Path $assetDirectory 'hs2-manager.png'), $images[1024])
[IO.File]::WriteAllBytes((Join-Path $assetDirectory 'hs2-manager-64.png'), $images[64])

# PNG-compressed ICO entries retain alpha at every Windows scale factor.
$icon = [IO.File]::Create((Join-Path $assetDirectory 'hs2-manager.ico'))
$writer = [IO.BinaryWriter]::new($icon)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    foreach ($size in $sizes) {
        $dimension = if ($size -eq 256) { 0 } else { $size }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$images[$size].Length); $writer.Write([uint32]$offset)
        $offset += $images[$size].Length
    }
    foreach ($size in $sizes) { $writer.Write([byte[]]$images[$size]) }
}
finally { $writer.Dispose(); $icon.Dispose() }
Write-Output "Exported SVG-derived PNG and 9-resolution ICO to $assetDirectory"
