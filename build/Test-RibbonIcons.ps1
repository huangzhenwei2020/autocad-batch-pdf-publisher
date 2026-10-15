[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$PluginPath)
$ErrorActionPreference = 'Stop'
if ([Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA') { throw 'Ribbon artwork tests require an STA process.' }
Add-Type -AssemblyName PresentationCore,PresentationFramework,WindowsBase
# Byte loading avoids locking a staged DLL after the test process exits.
$assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes([IO.Path]::GetFullPath($PluginPath)))
$flags = [Reflection.BindingFlags]'Static,NonPublic'
$assets = $assembly.GetType('BatchPdfPublisher.Services.RibbonIconAssets', $true)
$registry = $assembly.GetType('BatchPdfPublisher.Services.FeatureRegistry', $true)
$features = @($registry.GetProperty('Items', $flags).GetValue($null, $null))
$ids = @($features | ForEach-Object { $_.Id })
if ($features.Count -eq 0 -or @($ids | Select-Object -Unique).Count -ne $ids.Count) { throw 'Feature registration is empty or duplicated.' }
$maps = @('Images','SmallImages','ScaledImages')
foreach ($map in $maps) {
    $images = $assets.GetField($map, $flags).GetValue($null)
    foreach ($feature in $features) {
        if (-not $images.ContainsKey($feature.Id)) { throw "Missing $map icon: $($feature.Id)" }
        $image = $images[$feature.Id]
        $size = if ($map -eq 'Images') { 32 } elseif ($map -eq 'SmallImages') { 16 } else { 128 }
        if (-not $image.IsFrozen -or [Math]::Abs($image.Width-$size) -gt .01 -or [Math]::Abs($image.Height-$size) -gt .01) { throw "Invalid $map icon dimensions: $($feature.Id)" }
        $presenter = New-Object Windows.Controls.Image
        $presenter.Source = $image
        $presenter.Stretch = [Windows.Media.Stretch]::None
        $presenter.Width = $size; $presenter.Height = $size
        $presenter.Measure([Windows.Size]::new($size,$size))
        $presenter.Arrange([Windows.Rect]::new(0,0,$size,$size))
        $presenter.UpdateLayout()
        $bitmap = New-Object Windows.Media.Imaging.RenderTargetBitmap($size,$size,96,96,[Windows.Media.PixelFormats]::Pbgra32)
        $bitmap.Render($presenter)
        $pixels = New-Object byte[] ($size*$size*4)
        $bitmap.CopyPixels($pixels,$size*4,0)
        $visible = 0
        for ($i=3; $i -lt $pixels.Length; $i+=4) { if ($pixels[$i] -gt 128) { $visible++ } }
        if ($visible -lt $size*$size*.25) { throw "Blank or clipped $map icon: $($feature.Id)" }
    }
}
foreach ($pair in @(@('room_statistics','cad_table_xlsx'),@('room_statistics_summary','catalog'))) {
    $images=$assets.GetField('Images',$flags).GetValue($null)
    if (-not [Object]::ReferenceEquals($images[$pair[0]],$images[$pair[1]])) { throw "Room icon mapping missing: $($pair[0])" }
}
Write-Output "RIBBON_ICONS_OK $($features.Count) features large small preview frozen nonblank roomMappings"
