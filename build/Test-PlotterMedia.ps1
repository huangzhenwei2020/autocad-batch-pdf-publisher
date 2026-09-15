[CmdletBinding()]
param(
    [string]$PmpPath
)

# 核对 BatchPdfPublisher.pmp 里的自定义纸张是否覆盖 GB/T 50001-2017 全集，
# 并检查每个条目的“规范介质名”与“实际可打印尺寸”是否一致。
#
# PIA/PMP 是 zlib 压缩的文本：[60 字节头][78 DA][raw deflate][adler32]。解压后
# 里面有两个**互相独立**的列表，块编号各自从 0 开始，**不能按编号配对**：
#
#   size{ N{ name="UserDefinedMetric (420.00 x 1338.00毫米)          <- 规范介质名
#             localized_name="BPP_A2_420x1338_MM_FULL_BLEED         <- 下拉列表标签
#             media_description_name="UserDefinedMetric 纵向 420.00W x 1338.00H - ..." } }
#   description{ M{ name="UserDefinedMetric 纵向 420.00W x 1338.00H - ..."   <- 同一个描述名
#                   media_bounds_urx=420.0
#                   media_bounds_ury=1338.0 } }                     <- 实际纸张尺寸
#
# 两个列表靠**描述名字符串**关联；删除/编辑过纸张后 size 与 description 的数量
# 和编号都可能对不上（description 里会留下孤立块），所以必须按名字配对。
#
# 插件调用 AutoCAD 的 GetCanonicalMediaNameList() 拿到的就是 size 里的 name
# 字符串，再从中解析宽高（发布日志里的 介质= 就是它），所以“缺哪些规格”按
# size.name 判断。

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

# 取出 "段名{" 之后同一层级的所有 "N{ ... }" 块，返回 列表（保持文件顺序）。
function Read-Section {
    param([string[]]$Lines, [string]$SectionName)
    $blocks = New-Object System.Collections.Generic.List[object]
    $started = $false
    $current = $null
    foreach ($line in $Lines) {
        $t = $line.Trim()
        if (-not $started) {
            if ($t -eq ($SectionName + '{')) { $started = $true }
            continue
        }
        if ($t -eq '}') {
            if ($null -ne $current) { $blocks.Add($current); $current = $null; continue }
            break
        }
        $bm = [regex]::Match($t, '^(\d+)\{$')
        if ($bm.Success) { $current = [pscustomobject]@{ Idx = [int]$bm.Groups[1].Value; Fields = @() }; continue }
        if ($null -ne $current) { $current.Fields += $t }
    }
    return $blocks
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

$sizeBlocks = Read-Section $lines 'size'
$descBlocks = Read-Section $lines 'description'

# 描述名 -> 描述块（可能一对多，编辑/删除后会长出孤立块）
$descByName = @{}
foreach ($d in $descBlocks) {
    $n = Field-Value $d.Fields 'name'
    if ($null -eq $n) { continue }
    if (-not $descByName.ContainsKey($n)) { $descByName[$n] = New-Object System.Collections.Generic.List[object] }
    $descByName[$n].Add($d)
}

$media = New-Object System.Collections.Generic.List[object]
$mismatched = New-Object System.Collections.Generic.List[object]
foreach ($b in $sizeBlocks) {
    $canonical = Field-Value $b.Fields 'name'
    if ($null -eq $canonical) { continue }
    $m = [regex]::Match($canonical, '\(([\d.]+) x ([\d.]+)')
    if (-not $m.Success) { continue }
    $label = Field-Value $b.Fields 'localized_name'
    if ($null -eq $label) { $label = '' }
    $descName = Field-Value $b.Fields 'media_description_name'
    $boundsW = $null; $boundsH = $null; $found = 0
    if ($null -ne $descName -and $descByName.ContainsKey($descName)) {
        $hits = $descByName[$descName]
        $found = $hits.Count
        $rx = Field-Value $hits[0].Fields 'media_bounds_urx'
        $ry = Field-Value $hits[0].Fields 'media_bounds_ury'
        if ($rx) { $boundsW = [int][math]::Round([double]$rx) }
        if ($ry) { $boundsH = [int][math]::Round([double]$ry) }
    }
    $entry = [pscustomobject]@{
        NameW    = [int][math]::Round([double]$m.Groups[1].Value)
        NameH    = [int][math]::Round([double]$m.Groups[2].Value)
        BoundsW  = $boundsW
        BoundsH  = $boundsH
        Label    = $label
        DescHits = $found
    }
    $media.Add($entry)
    if ($null -eq $boundsW) { $mismatched.Add($entry) }
    elseif ($boundsW -ne $entry.NameW -or $boundsH -ne $entry.NameH) { $mismatched.Add($entry) }
}

Write-Host ''
Write-Host ("纸库里的自定义纸张（共 {0} 个）：" -f $media.Count)
foreach ($item in ($media | Sort-Object NameW, NameH, Label)) {
    $bounds = '（找不到描述块）'
    if ($null -ne $item.BoundsW) { $bounds = "$($item.BoundsW)x$($item.BoundsH)" }
    $verdict = 'OK'
    if ($null -eq $item.BoundsW) { $verdict = '缺描述块' }
    elseif ($item.BoundsW -ne $item.NameW -or $item.BoundsH -ne $item.NameH) { $verdict = '尺寸不符' }
    $tail = ''
    if ($verdict -ne 'OK') { $tail = "   <<< $verdict" }
    if ($item.DescHits -gt 1) { $tail += "   （描述名被 $($item.DescHits) 个描述块共用）" }
    Write-Host ("  {0,5} x {1,-5} mm  {2,-34} 实际 {3,-11}{4}" -f $item.NameW, $item.NameH, $item.Label, $bounds, $tail)
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
Write-Host ("GB/T 50001-2017 加长幅面核对：应有 {0} 个，缺少 {1} 个。" -f $requiredCount, $missing.Count)
if ($missing.Count -gt 0) {
    $missing | Sort-Object W, H | ForEach-Object { Write-Host ("  缺 {0,5} x {1,-5} mm   {2}" -f $_.W, $_.H, $_.Name) }
}
if ($missingOptional.Count -gt 0) {
    Write-Host ''
    Write-Host '未加自定义条目的基本幅面/特殊幅面：'
    $missingOptional | Sort-Object W, H | ForEach-Object { Write-Host ("  {0,5} x {1,-5} mm   {2}" -f $_.W, $_.H, $_.Name) }
}

$usedDescNames = @{}
foreach ($b in $sizeBlocks) {
    $n = Field-Value $b.Fields 'media_description_name'
    if ($null -ne $n) { $usedDescNames[$n] = $true }
}
$orphans = @($descBlocks | Where-Object { $n = (Field-Value $_.Fields 'name'); $null -ne $n -and -not $usedDescNames.ContainsKey($n) })
if ($orphans.Count -gt 0) {
    Write-Host ''
    Write-Host ("description 里有 {0} 个孤立块（没有纸张条目引用，编辑/删除后留下的，不影响出图）：" -f $orphans.Count)
    foreach ($o in $orphans) {
        Write-Host ("  idx {0}  {1}  实际 {2}x{3}" -f $o.Idx, (Field-Value $o.Fields 'name'), (Field-Value $o.Fields 'media_bounds_urx'), (Field-Value $o.Fields 'media_bounds_ury'))
    }
}

Write-Host ''
if ($mismatched.Count -gt 0) {
    Write-Host ("有 {0} 个条目的规范介质名与实际可打印尺寸不一致。" -f $mismatched.Count)
    Write-Host '插件是从介质名里解析宽高的，这类条目会被当成别的规格，必须删除后重新添加。'
}
if ($missing.Count -gt 0) {
    Write-Host '缺少的加长幅面在出图时会退回“打印到更大的备用纸 + 矢量裁切”：尺寸比例仍然正确，'
    Write-Host '但每张多一次打印往返，内容被等比放大（线宽也会一起放大）。请按'
    Write-Host 'docs\绘图仪纸张尺寸清单-GBT50001-2017.md 补齐。'
    exit 1
}
if ($mismatched.Count -gt 0) { Write-Host ''; Write-Host '加长幅面齐全，但有尺寸不符的条目需要删除重加。'; exit 2 }
Write-Host '加长幅面齐全，所有条目的介质名与实际尺寸一致，横竖两个方向都能 1:1 出图。'
exit 0
