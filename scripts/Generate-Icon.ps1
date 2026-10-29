[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$directory = Join-Path $workspace 'assets\branding'
$source = Join-Path $directory 'mic-studio.svg'
Add-Type -AssemblyName PresentationCore, WindowsBase
[xml]$svg = [IO.File]::ReadAllText($source)
$namespaces = [Xml.XmlNamespaceManager]::new($svg.NameTable)
$namespaces.AddNamespace('s', 'http://www.w3.org/2000/svg')
function ConvertTo-IconBrush([string]$value) {
    if (!$value -or $value -eq 'none') { return $null }
    if ($value -match '^url\(#([a-z]+)\)$') {
        $id = $Matches[1]
        $gradient = $svg.SelectSingleNode("//s:linearGradient[@id='$id']", $namespaces)
        $brush = [Windows.Media.LinearGradientBrush]::new()
        $brush.StartPoint = [Windows.Point]::new([double]$gradient.x1, [double]$gradient.y1)
        $brush.EndPoint = [Windows.Point]::new([double]$gradient.x2, [double]$gradient.y2)
        foreach ($stop in $gradient.SelectNodes('s:stop', $namespaces)) {
            $color = [Windows.Media.ColorConverter]::ConvertFromString($stop.GetAttribute('stop-color'))
            $brush.GradientStops.Add([Windows.Media.GradientStop]::new($color, [double]$stop.offset))
        }
        return $brush
    }
    return [Windows.Media.SolidColorBrush]::new([Windows.Media.ColorConverter]::ConvertFromString($value))
}
function New-IconPng([int]$size) {
    $visual = [Windows.Media.DrawingVisual]::new()
    $drawing = $visual.RenderOpen()
    try {
        $drawing.PushTransform([Windows.Media.ScaleTransform]::new($size / 256.0, $size / 256.0))
        foreach ($element in $svg.DocumentElement.ChildNodes) {
            if ($element.LocalName -notin @('rect', 'path')) { continue }
            # Optical correction: the monogram stays clear when Windows requests a tiny icon.
            if ($size -lt 32 -and $element.GetAttribute('data-detail') -eq 'true') { continue }
            $fill = ConvertTo-IconBrush $element.GetAttribute('fill')
            $stroke = ConvertTo-IconBrush $element.GetAttribute('stroke')
            $pen = $null
            if ($null -ne $stroke) {
                $pen = [Windows.Media.Pen]::new($stroke, [double]$element.GetAttribute('stroke-width'))
                $pen.StartLineCap = $pen.EndLineCap = [Windows.Media.PenLineCap]::Round
                $pen.LineJoin = [Windows.Media.PenLineJoin]::Round
            }
            if ($element.LocalName -eq 'rect') {
                $rect = [Windows.Rect]::new([double]$element.x, [double]$element.y, [double]$element.width, [double]$element.height)
                $drawing.DrawRoundedRectangle($fill, $pen, $rect, [double]$element.rx, [double]$element.rx)
            }
            else {
                $geometry = [Windows.Media.Geometry]::Parse($element.d)
                $drawing.DrawGeometry($fill, $pen, $geometry)
            }
        }
        $drawing.Pop()
    }
    finally { $drawing.Close() }
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.MemoryStream]::new()
    try { $encoder.Save($stream); return ,$stream.ToArray() }
    finally { $stream.Dispose() }
}
[IO.File]::WriteAllBytes((Join-Path $directory 'mic-studio.png'), (New-IconPng 512))
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = @()
foreach ($size in $sizes) { $frames += ,(New-IconPng $size) }
$file = [IO.File]::Create((Join-Path $directory 'mic-studio.ico'))
$writer = [IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $dimension = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$index].Length); $writer.Write([uint32]$offset)
        $offset += $frames[$index].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
}
finally { $writer.Dispose(); $file.Dispose() }
Write-Host ('Generated SVG-based 512px PNG and 9-size Windows ICO in ' + $directory)
