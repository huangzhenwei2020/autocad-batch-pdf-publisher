[CmdletBinding()]
param(
    [string]$PluginPath,
    [string]$OutputPath
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $PluginPath) { $PluginPath = Join-Path $root 'dist\WanLuoArchitectureTools\CadApi\R24\BatchPdfPublisher.dll' }
if (-not $OutputPath) { $OutputPath = Join-Path $root '.artifacts\ribbon-icons-preview.png' }
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$assembly = [Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($PluginPath))
$flags = [Reflection.BindingFlags]'Static,NonPublic'
$assets = $assembly.GetType('BatchPdfPublisher.Services.RibbonIconAssets', $true)
$registry = $assembly.GetType('BatchPdfPublisher.Services.FeatureRegistry', $true)
$features = @($registry.GetProperty('Items', $flags).GetValue($null, $null))
$ids = @($assets.GetField('FeatureIds', $flags).GetValue($null))
if ($features.Count -ne $ids.Count -or @($ids | Select-Object -Unique).Count -ne $ids.Count) { throw 'Icon count or uniqueness mismatch.' }
$getImage = $assets.GetMethod('ForFeature', $flags)
$getSmallImage = $assets.GetMethod('SmallForFeature', $flags)
$pictures = @{}
foreach ($feature in $features) {
    if ($ids -notcontains $feature.Id) { throw "Missing icon: $($feature.Id)" }
    $picture = $getImage.Invoke($null, @($feature.Id))
    if (-not $picture.IsFrozen -or $picture.PixelWidth -ne 32 -or $picture.PixelHeight -ne 32 -or $picture.Width -ne 32 -or $picture.Height -ne 32) { throw "Invalid native large icon: $($feature.Id)" }
    $small = $getSmallImage.Invoke($null, @($feature.Id))
    if (-not $small.IsFrozen -or $small.PixelWidth -ne 16 -or $small.PixelHeight -ne 16 -or $small.Width -ne 16 -or $small.Height -ne 16) { throw "Invalid native small icon: $($feature.Id)" }
    $converted = New-Object Windows.Media.Imaging.FormatConvertedBitmap($picture, [Windows.Media.PixelFormats]::Bgra32, $null, 0)
    $pixels = New-Object byte[] (32 * 32 * 4)
    $converted.CopyPixels($pixels, 32 * 4, 0)
    if ($pixels[3] -gt 20) { throw "Icon has an opaque outer corner: $($feature.Id)" }
    if ($pixels[((16 * 32 + 16) * 4 + 3)] -lt 240) { throw "Icon center is unexpectedly transparent: $($feature.Id)" }
    # Reproduce a native-size image presenter that does not auto-scale the source.
    # A 258px crop can pass DrawImage's scaled preview but fail this check in CAD.
    $presenter = New-Object Windows.Controls.Image
    $presenter.Source = $picture
    $presenter.Stretch = [Windows.Media.Stretch]::None
    $presenter.Width = 32
    $presenter.Height = 32
    $presenter.Measure([Windows.Size]::new(32, 32))
    $presenter.Arrange([Windows.Rect]::new(0, 0, 32, 32))
    $presenter.UpdateLayout()
    $native = New-Object Windows.Media.Imaging.RenderTargetBitmap(32, 32, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $native.Render($presenter)
    $native.CopyPixels($pixels, 128, 0)
    $visible = 0
    for ($pixel = 3; $pixel -lt $pixels.Length; $pixel += 4) { if ($pixels[$pixel] -gt 128) { $visible++ } }
    if ($visible -lt 600) { throw "Icon clipped in the native-size presenter: $($feature.Id) ($visible pixels)" }
    $pictures[$feature.Id] = $picture
}

$visual = New-Object Windows.Media.DrawingVisual
$drawing = $visual.RenderOpen()
$font = New-Object Windows.Media.Typeface('Microsoft YaHei')
function Text([string]$Value, [double]$X, [double]$Y, [double]$Size, [string]$Color) {
    $brush = [Windows.Media.BrushConverter]::new().ConvertFromString($Color)
    $text = New-Object Windows.Media.FormattedText($Value, [Globalization.CultureInfo]::GetCultureInfo('zh-CN'), [Windows.FlowDirection]::LeftToRight, $font, $Size, $brush)
    $drawing.DrawText($text, [Windows.Point]::new($X, $Y))
}
$background = [Windows.Media.BrushConverter]::new().ConvertFromString('#202831')
$drawing.DrawRectangle($background, $null, [Windows.Rect]::new(0, 0, 1440, 840))
Text '万落建筑工具 · 实际图标资源预览' 30 20 24 '#FFFFFF'
Text '原生大按钮使用 32px 图标；以下为资源渲染，不是 CAD 实机截图。' 30 60 14 '#A9BAC9'
$i = 0
foreach ($feature in $features) {
    $x = 30 + $i * 77
    $drawing.DrawImage($pictures[$feature.Id], [Windows.Rect]::new($x + 15, 105, 40, 40))
    Text $feature.ShortName $x 151 14 '#FFFFFF'
    $i++
}
for ($i = 0; $i -lt $ids.Count; $i++) {
    $id = $ids[$i]
    $feature = $features | Where-Object Id -eq $id | Select-Object -First 1
    $x = 50 + ($i % 6) * 235
    $y = 205 + [math]::Floor($i / 6) * 175
    $drawing.DrawImage($pictures[$id], [Windows.Rect]::new($x + 45, $y, 110, 110))
    Text $feature.ShortName ($x + 60) ($y + 116) 18 '#FFFFFF'
}
$drawing.DrawRectangle([Windows.Media.Brushes]::WhiteSmoke, $null, [Windows.Rect]::new(0, 745, 1440, 95))
Text '浅色背景 / 32px' 22 780 14 '#263645'
for ($i = 0; $i -lt $ids.Count; $i++) {
    $drawing.DrawImage($pictures[$ids[$i]], [Windows.Rect]::new(185 + $i * 68, 775, 32, 32))
}
$drawing.Close()
$bitmap = New-Object Windows.Media.Imaging.RenderTargetBitmap(1440, 840, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
$bitmap.Render($visual)
$encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
$encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($OutputPath))) | Out-Null
$stream = [IO.File]::Create([IO.Path]::GetFullPath($OutputPath))
try { $encoder.Save($stream) } finally { $stream.Dispose() }
Write-Host "PASS: $($features.Count) registered icons, embedded artwork, frozen images, transparency, sprite dimensions."
Write-Host $OutputPath
