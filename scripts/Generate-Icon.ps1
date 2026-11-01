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
function New-IconPng([int]$size, [int]$height = 0) {
    if ($height -eq 0) { $height = $size }
    $view = @($svg.DocumentElement.GetAttribute('viewBox') -split '\s+' | ForEach-Object { [double]::Parse($_, [Globalization.CultureInfo]::InvariantCulture) })
    if ($view.Count -ne 4 -or $view[2] -le 0 -or $view[3] -le 0) { throw 'A bounded SVG viewBox is required.' }
    $scale = [Math]::Min($size / $view[2], $height / $view[3])
    $visual = [Windows.Media.DrawingVisual]::new()
    $drawing = $visual.RenderOpen()
    try {
        $drawing.PushTransform([Windows.Media.MatrixTransform]::new([Windows.Media.Matrix]::new($scale, 0, 0, $scale,
            ($size - $view[2] * $scale) / 2 - $view[0] * $scale, ($height - $view[3] * $scale) / 2 - $view[1] * $scale)))
        foreach ($element in $svg.DocumentElement.ChildNodes) {
            if ($element.LocalName -notin @('rect', 'path')) { continue }
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
                $geometry = [Windows.Media.Geometry]::Parse($element.d).Clone()
                if ($element.HasAttribute('transform')) {
                    $transform = $element.GetAttribute('transform')
                    if ($transform -notmatch '^matrix\(([-0-9. ]+)\)$') { throw 'Only explicit affine SVG transforms are supported.' }
                    $geometry.Transform = [Windows.Media.MatrixTransform]::new([Windows.Media.Matrix]::Parse(($Matches[1] -replace '\s+', ',')))
                }
                $drawing.DrawGeometry($fill, $pen, $geometry)
            }
        }
        $drawing.Pop()
    }
    finally { $drawing.Close() }
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($size, $height, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.MemoryStream]::new()
    try { $encoder.Save($stream); return ,$stream.ToArray() }
    finally { $stream.Dispose() }
}
$wave = $svg.SelectSingleNode('//*[@id="wave"]').GetAttribute('d')
if (!$wave) { throw 'The approved A2 wave geometry is missing.' }
$utf8 = [Text.UTF8Encoding]::new($false)
foreach ($variant in @(
    @{ Name='mic-studio-mark'; Color='#19C8AA' },
    @{ Name='mic-studio-tray-light'; Color='#102C32' },
    @{ Name='mic-studio-tray-dark'; Color='#F3FCFA' }
)) {
    $document = '<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="32 32 192 192" role="img" aria-label="Mic Studio sound pulse"><path d="' + $wave + '" fill="' + $variant.Color + '"/></svg>'
    [IO.File]::WriteAllText((Join-Path $directory ($variant.Name + '.svg')), $document + "`n", $utf8)
}
# Outlined text keeps the exported logo independent of installed fonts.
$culture = [Globalization.CultureInfo]::InvariantCulture
$typeface = [Windows.Media.Typeface]::new([Windows.Media.FontFamily]::new('Segoe UI'), [Windows.FontStyles]::Normal, [Windows.FontWeights]::SemiBold, [Windows.FontStretches]::Normal)
$wordmark = [Windows.Media.FormattedText]::new('Mic Studio', $culture, [Windows.FlowDirection]::LeftToRight, $typeface, 64, [Windows.Media.Brushes]::Black, 1.0)
$letters = [Windows.Media.PathGeometry]::CreateFromGeometry($wordmark.BuildGeometry([Windows.Point]::new(150, 38))).ToString($culture) -replace '^F[01]', ''
$logoWidth = [int][Math]::Ceiling(150 + $wordmark.WidthIncludingTrailingWhitespace + 22)
$logo = '<svg xmlns="http://www.w3.org/2000/svg" width="' + $logoWidth + '" height="160" viewBox="0 0 ' + $logoWidth + ' 160" role="img" aria-label="Mic Studio"><path d="' + $wave + '" transform="matrix(0.6 0 0 0.6 0 0)" fill="#19C8AA"/><path d="' + $letters + '" fill="#122E34"/></svg>'
[IO.File]::WriteAllText((Join-Path $directory 'mic-studio-logo.svg'), $logo + "`n", $utf8)
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
foreach ($name in @('mic-studio', 'mic-studio-tray-light', 'mic-studio-tray-dark')) {
    [xml]$svg = [IO.File]::ReadAllText((Join-Path $directory ($name + '.svg')))
    $namespaces = [Xml.XmlNamespaceManager]::new($svg.NameTable)
    $namespaces.AddNamespace('s', 'http://www.w3.org/2000/svg')
    [IO.File]::WriteAllBytes((Join-Path $directory ($name + '.png')), (New-IconPng 512))
$frames = @()
foreach ($size in $sizes) { $frames += ,(New-IconPng $size) }
$file = [IO.File]::Create((Join-Path $directory ($name + '.ico')))
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
}
foreach ($name in @('mic-studio-mark', 'mic-studio-logo')) {
    [xml]$svg = [IO.File]::ReadAllText((Join-Path $directory ($name + '.svg')))
    $namespaces = [Xml.XmlNamespaceManager]::new($svg.NameTable)
    $namespaces.AddNamespace('s', 'http://www.w3.org/2000/svg')
    $width = if ($name -eq 'mic-studio-logo') { $logoWidth * 2 } else { 512 }
    $height = if ($name -eq 'mic-studio-logo') { 320 } else { 512 }
    [IO.File]::WriteAllBytes((Join-Path $directory ($name + '.png')), (New-IconPng $width $height))
}
Write-Output 'Generated A2 tile, outlined wordmark, standalone mark and light/dark tray icons; each ICO contains nine sizes.'
