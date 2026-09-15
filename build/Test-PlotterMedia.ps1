[CmdletBinding()]
param(
    [string]$PmpPath
)

# 核对 BatchPdfPublisher.pmp 里的自定义纸张是否覆盖 GB/T 50001-2017 全集。
#
# PIA/PMP 是 zlib 压缩的文本：[60 字节头][78 DA][raw deflate][adler32]。
# 每个自定义纸张在文件里有两处记录，按同一个序号对齐：
#
#   size{ N{ name="UserDefinedMetric (420.00 x 1338.00毫米)   <- 规范介质名
#             localized_name="BPP_A2_420x1338_MM_FULL_BLEED  <- 下拉列表标签
#             landscape_mode=FALSE } }
#   description{ N{ name="UserDefinedMetric 纵向 420.00W x 1338.00H - ... =561960
#                   media_bounds_urx=420.0
#                   media_bounds_ury=1338.0 } }                  <- 真正的纸张尺寸
#
# 插件调用 AutoCAD 的 GetCanonicalMediaNameList() 拿到的就是 size 里的
# name 字符串，再从中解析宽高（发布日志里的 介质= 就是它）。所以：
#   * 判断“缺哪些规格”要看 size.name —— 那才是插件看到的东西；
#   * 如果 size.name 与 description 的 media_bounds 不一致（编辑已有纸张时
#     AutoCAD 不一定会刷新 name 字符串），必须单独指出来，否则可能选到尺寸
#     不符的纸张。本脚本两种都报。

$ErrorActionPreference = 'Stop'

# GB/T 50001-2017 表 3.1.3：短边 b × 长边 l（毫米），含基本幅面与表注里的特殊幅面。
$standard = @(
    @{ Name = 'A4';       W = 210;  H = 297  }
    @{ Name = 'A3';       W = 297;  H = 420  }
    @{ Name = 'A3+1/2l';  W = 297;  H = 630  }
    @{ Name = 'A3+l';     W = 297;  H = 841  }
    @{ Name = 'A3+3/2l';  W = 297;  H = 1051 }
    @{ Name = 'A3+2l';    W = 297;  H = 1261 }
    @{ Name = 'A3+5/2l';  W = 297;  H = 1471 }
    @{ Name = 'A3+3l';    W = 297;  H = 1682 }
    @{ Name = 'A3+7/2l';  W = 297;  H = 1892 }
    @{ Name = 'A2';       W = 420;  H = 594  }
    @{ Name = 'A2+1/4l';  W = 420;  H = 743  }
    @{ Name = 'A2+1/2l';  W = 420;  H = 891  }
    @{ Name = 'A2+3/4l';  W = 420;  H = 1041 }
    @{ Name = 'A2+l';     W = 420;  H = 1189 }
    @{ Name = 'A2+5/4l';  W = 420;  H = 1338 }
    @{ Name = 'A2+3/2l';  W = 420;  H = 1486 }
    @{ Name = 'A2+7/4l';  W = 420;  H = 1635 }
    @{ Name = 'A2+2l';    W = 420;  H = 1783 }
    @{ Name = 'A2+9/4l';  W = 420;  H = 1932 }
    @{ Name = 'A2+5/2l';  W = 420;  H = 2080 }
    @{ Name = 'A1';       W = 594;  H = 841  }
    @{ Name = 'A1+1/4l';  W = 594;  H = 1051 }
    @{ Name = 'A1+1/2l';  W = 594;  H = 1261 }
    @{ Name = 'A1+3/4l';  W = 594;  H = 1471 }
    @{ Name = 'A1+l';     W = 594;  H = 1682 }
    @{ Name = 'A1+5/4l';  W = 594;  H = 1892 }
    @{ Name = 'A1+3/2l';  W = 594;  H = 2102 }
    @{ Name = 'A0';       W = 841;  H = 1189 }
    @{ Name = 'A0+1/4l';  W = 841;  H = 1486 }
    @{ Name = 'A0+1/2l';  W = 841;  H = 1783 }
    @{ Name = 'A0+3/4l';  W = 841;  H = 2080 }
    @{ Name = 'A0+l';     W = 841;  H = 2378 }
    @{ Name = '特殊 841x891';   W = 841;  H = 891  }
    @{ Name = '特殊 1189x1261'; W = 1189; H = 1261 }
)

function Resolve-PmpPath {
    param([string]$Explicit)
    if (-not [string]::IsNullOrWhiteSpace($Explicit)) {
        if (-not (Test-Path -LiteralPath $Explicit)) { throw "找不到文件：$Explicit" }
        return (Resolve-Path -LiteralPath $Explicit).Path
    }
    $candidates = New-Object System.Collections.Generic.List[string]
    $candidates.Add((Join-Path (Split-Path $PSScriptRoot -Parent) 'Resources\Plotters\BatchPdfPublisher.pmp'))
    if (-not [string]::IsNullOrWhiteSpace($env:APPDATA)) {
        foreach ($year in 2021..2026) {
            foreach ($ver in 'R24.0','R24.1','R24.2','R24.3','R25.0','R25.1') {
                $candidates.Add((Join-Path $env:APPDATA "Autodesk\AutoCAD $year\$ver\chs\Plotters\PMP Files\BatchPdfPublisher.pmp"))
            }
        }
    }
    $found = $candidates | Where-Object { Test-Path -LiteralPath $_ } |
        Sort-Object { (Get-Item -LiteralPath $_).LastWriteTime } -Descending
    if (-not $found) { throw '找不到 BatchPdfPublisher.pmp，请用 -PmpPath 指定。' }
    return (Resolve-Path -LiteralPath $found[0]).Path
}

function Expand-Pia([string]$Path) {
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $marker = [System.Text.Encoding]::ASCII.GetBytes('pmzlibcodec')
    $start = -1
    for ($i = 0; $i -le $bytes.Length - $marker.Length; $i++) {
        if ($bytes[$i] -ne $marker[0]) { continue }
        $hit = $true
        for ($j = 1; $j -lt $marker.Length; $j++) { if ($bytes[$i + $j] -ne $marker[$j]) { $hit = $false; break } }
        if ($hit) { $start = $i + $marker.Length; break }
    }
    if ($start -lt 0) { throw '这不是压缩的 PIA 文件（找不到 pmzlibcodec）。' }
    $zlib = -1
    for ($i = $start; $i -lt $bytes.Length - 1; $i++) {
        if ($bytes[$i] -eq 0x78 -and @(0x9C, 0x01, 0xDA, 0x5E) -contains $bytes[$i + 1]) { $zlib = $i; break }
    }
    if ($zlib -lt 0) { throw '在 PIA 文件里找不到 zlib 数据段。' }
    $input = New-Object System.IO.MemoryStream (,$bytes[$zlib..($bytes.Length - 1)])
    $output = New-Object System.IO.MemoryStream
    $inflate = New-Object System.IO.Compression.ZLibStream($input, [System.IO.Compression.CompressionMode]::Decompress)
    try { $inflate.CopyTo($output) } finally { $inflate.Dispose() }
    return [System.Text.Encoding]::Latin1.GetString($output.ToArray())
}

# 取出 "段名{" 之后、同一层级的所有 "N{ ... }" 块，返回 序号 -> 字段数组。
function Read-Section {
    param([string[]]$Lines, [string]$SectionName)
    $result = @{}
    $started = $false
    $current = -1
    $fields = $null
    foreach ($line in $Lines) {
        $t = $line.Trim()
        if (-not $started) {
            if ($t -eq ($SectionName + '{')) { $started = $true }
            continue
        }
        if ($t -eq '}') {
            if ($current -ge 0) { $result[$current] = $fields; $current = -1; $fields = $null; continue }
            break
        }
        $bm = [regex]::Match($t, '^(\d+)\{$')
        if ($bm.Success) { $current = [int]$bm.Groups[1].Value; $fields = New-Object System.Collections.Generic.List[string]; continue }
        if ($current -ge 0) { $fields.Add($t) }
    }
    return $result
}

function Field-Value {
    param($Fields, [string]$Key)
    foreach ($f in $Fields) {
        if ($f.StartsWith($Key + '=')) {
            $value = $f.Substring($Key.Length + 1)
            # PIA 的字符串只有开头的引号，值以换行结束。
            if ($value.StartsWith('"')) { $value = $value.Substring(1) }
            return $value.TrimEnd("`r")
        }
    }
    return $null
}

$resolved = Resolve-PmpPath $PmpPath
Write-Host "纸张库：$resolved"
$text = Expand-Pia $resolved
$lines = $text -split "`n"

$sizeSection = Read-Section $lines 'size'
$descSection = Read-Section $lines 'description'

$media = New-Object System.Collections.Generic.List[object]
foreach ($idx in ($sizeSection.Keys | Sort-Object)) {
    $fields = $sizeSection[$idx]
    $canonical = Field-Value $fields 'name'
    if ($null -eq $canonical) { continue }
    $m = [regex]::Match($canonical, '\(([\d.]+) x ([\d.]+)')
    if (-not $m.Success) { continue }
    $label = Field-Value $fields 'localized_name'
    if ($null -eq $label) { $label = '' }

    $boundsW = $null; $boundsH = $null
    if ($descSection.ContainsKey($idx)) {
        $d = $descSection[$idx]
        $rx = Field-Value $d 'media_bounds_urx'
        $ry = Field-Value $d 'media_bounds_ury'
        if ($rx) { $boundsW = [double]$rx }
        if ($ry) { $boundsH = [double]$ry }
    }
    $media.Add([pscustomobject]@{
        Index     = $idx
        NameW     = [int][math]::Round([double]$m.Groups[1].Value)
        NameH     = [int][math]::Round([double]$m.Groups[2].Value)
        BoundsW   = if ($null -ne $boundsW) { [int][math]::Round($boundsW) } else { $null }
        BoundsH   = if ($null -ne $boundsH) { [int][math]::Round($boundsH) } else { $null }
        Label     = $label
        Canonical = $canonical
    })
}

Write-Host ''
Write-Host ("纸库里的自定义纸张（共 {0} 个）：" -f $media.Count)
$stale = New-Object System.Collections.Generic.List[object]
$labelBad = New-Object System.Collections.Generic.List[object]
foreach ($item in ($media | Sort-Object NameW, NameH)) {
    $notes = New-Object System.Collections.Generic.List[string]
    if ($null -ne $item.BoundsW -and ($item.BoundsW -ne $item.NameW -or $item.BoundsH -ne $item.NameH)) {
        $notes.Add(("库内部不一致：name 写 {0}x{1}，实际可打印 {2}x{3}" -f $item.NameW, $item.NameH, $item.BoundsW, $item.BoundsH))
        $stale.Add($item)
    }
    $lm = [regex]::Match($item.Label, '(\d+)x(\d+)')
    if ($lm.Success) {
        $lw = [int]$lm.Groups[1].Value; $lh = [int]$lm.Groups[2].Value
        $effW = if ($null -ne $item.BoundsW) { $item.BoundsW } else { $item.NameW }
        $effH = if ($null -ne $item.BoundsH) { $item.BoundsH } else { $item.NameH }
        if (-not (($lw -eq $effW -and $lh -eq $effH) -or ($lw -eq $effH -and $lh -eq $effW))) {
            $notes.Add(("标签写 {0}x{1}，实际 {2}x{3}" -f $lw, $lh, $effW, $effH))
            $labelBad.Add($item)
        }
    }
    $tail = if ($notes.Count -gt 0) { '   <<< ' + ($notes -join '；') } else { '' }
    $boundsMark = ''
    if ($null -eq $item.BoundsW) { $boundsMark = '?' }
    Write-Host ("  {0,5} x {1,-5} mm{2}  {3}{4}" -f $item.NameW, $item.NameH, $boundsMark, $item.Label, $tail)
}

# 覆盖核对按 size.name 走 —— 那才是插件从 AutoCAD 拿到的规范介质名。
$missing = New-Object System.Collections.Generic.List[object]
$missingOptional = New-Object System.Collections.Generic.List[object]
foreach ($item in $standard) {
    $hit = $media | Where-Object { ($_.NameW -eq $item.W -and $_.NameH -eq $item.H) -or ($_.NameW -eq $item.H -and $_.NameH -eq $item.W) }
    if ($hit) { continue }
    if ($item.Name -match '^A[0-4]$' -or $item.Name -like '特殊*') { $missingOptional.Add($item) } else { $missing.Add($item) }
}
$requiredCount = ($standard | Where-Object { $_.Name -notmatch '^A[0-4]$' -and $_.Name -notlike '特殊*' }).Count

Write-Host ''
Write-Host ("GB/T 50001-2017 加长幅面核对（按插件读取的规范介质名）：应有 {0} 个，缺少 {1} 个。" -f $requiredCount, $missing.Count)
if ($missing.Count -gt 0) {
    $missing | Sort-Object W, H | ForEach-Object { Write-Host ("  缺 {0,5} x {1,-5} mm   {2}" -f $_.W, $_.H, $_.Name) }
}
if ($missingOptional.Count -gt 0) {
    Write-Host ''
    Write-Host '未加自定义条目的基本幅面/特殊幅面（AutoCAD 自带 ISO 纸张已覆盖基本幅面，特殊幅面插件登记选不到）：'
    $missingOptional | Sort-Object W, H | ForEach-Object { Write-Host ("  {0,5} x {1,-5} mm   {2}" -f $_.W, $_.H, $_.Name) }
}

Write-Host ''
if ($stale.Count -gt 0) {
    Write-Host ("有 {0} 个条目的规范介质名与实际可打印尺寸不一致。" -f $stale.Count)
    Write-Host '编辑已有的自定义纸张时 AutoCAD 不一定会刷新介质名字符串，而插件正是从这串'
    Write-Host '名字里解析宽高。建议把这类条目删除后重新添加，让 AutoCAD 生成干净的介质名。'
}
if ($labelBad.Count -gt 0) {
    Write-Host ("有 {0} 个条目的下拉标签写的尺寸与实际不符（仅显示问题，出图按实际尺寸走）。" -f $labelBad.Count)
}
if ($missing.Count -gt 0) {
    Write-Host ''
    Write-Host '缺少的加长幅面在出图时会退回“打印到更大的备用纸 + 矢量裁切”：尺寸比例仍然正确，'
    Write-Host '但每张多一次打印往返，内容被等比放大（线宽也会一起放大）。请按'
    Write-Host 'docs\绘图仪纸张尺寸清单-GBT50001-2017.md 补齐。'
    exit 1
}
if ($stale.Count -gt 0) { Write-Host ''; Write-Host '加长幅面齐全，但有库内部不一致的条目需要重加。'; exit 2 }
Write-Host '加长幅面齐全，横竖两个方向都能 1:1 出图。'
exit 0
