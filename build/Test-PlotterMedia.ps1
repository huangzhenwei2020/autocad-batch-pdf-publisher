[CmdletBinding()]
param(
    [string]$PmpPath
)

# 核对 BatchPdfPublisher.pmp 里的自定义纸张是否覆盖 GB/T 50001-2017 全集。
#
# PIA/PMP 是 zlib 压缩的文本：文件头是明文
#   PIAFILEVERSION_2.0,PC3VER1,compress<CRLF>pmzlibcodec
# 紧接着是一段 zlib 流。纸张尺寸写在每个 size 块的
#   name="UserDefinedMetric (420.00 x 891.00毫米)"
# 里（"毫米"是 GBK 编码，本脚本只取其中的宽高数字，不做中文解码）。
# 插件真正比对的正是这串规范介质名里的数字，所以这里列出的就是插件看到的东西。

$ErrorActionPreference = 'Stop'

# GB/T 50001-2017 表 3.1.3：短边 b × 长边 l（毫米）。
# 基本幅面 + A0~A3 的长边加长尺寸全集，再加表注里的两个特殊幅面。
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
    # zlib 流的头两个字节是 0x78 0x9C / 0x78 0x01 / 0x78 0xDA / 0x78 0x5E
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

$resolved = Resolve-PmpPath $PmpPath
Write-Host "纸张库：$resolved"
$text = Expand-Pia $resolved

$media = New-Object System.Collections.Generic.List[object]
# PIA 的字符串只有开头的引号（值以换行结束），所以按行配对
#   name="UserDefinedMetric (420.00 x 891.00毫米)
#   localized_name="BPP_A2_420x891_MM_FULL_BLEED
# 同时保留两边的信息，用来核对“名称写的尺寸”和“实际宽高”是否一致。
$pending = $null
foreach ($line in ($text -split "`n")) {
    $m = [regex]::Match($line, 'name="UserDefinedMetric \(([\d.]+) x ([\d.]+)')
    if ($m.Success) {
        $pending = [pscustomobject]@{
            W     = [int][double]$m.Groups[1].Value
            H     = [int][double]$m.Groups[2].Value
            Label = ''
        }
        continue
    }
    $m = [regex]::Match($line, 'localized_name="([^"\r\n]*)')
    if ($m.Success -and $null -ne $pending) {
        $pending.Label = $m.Groups[1].Value
        $media.Add($pending)
        $pending = $null
    }
}

$mismatched = New-Object System.Collections.Generic.List[object]
Write-Host ''
Write-Host ("纸库里的自定义纸张（共 {0} 个）：" -f $media.Count)
$media | Sort-Object W, H | ForEach-Object {
    $orient = if ($_.W -gt $_.H) { '横式' } else { '立式' }
    $warn = ''
    $lm = [regex]::Match($_.Label, '(\d+)x(\d+)')
    if ($lm.Success) {
        $lw = [int]$lm.Groups[1].Value
        $lh = [int]$lm.Groups[2].Value
        if (-not (($lw -eq $_.W -and $lh -eq $_.H) -or ($lw -eq $_.H -and $lh -eq $_.W))) {
            $warn = "   <<< 名称写的是 ${lw}x${lh}，与实际宽高不符"
            $mismatched.Add($_)
        }
    }
    Write-Host ("  {0,5} x {1,-5} mm  {2}  {3}{4}" -f $_.W, $_.H, $orient, $_.Label, $warn)
}

$missing = New-Object System.Collections.Generic.List[object]
$missingOptional = New-Object System.Collections.Generic.List[object]
foreach ($item in $standard) {
    $hit = $media | Where-Object { ($_.W -eq $item.W -and $_.H -eq $item.H) -or ($_.W -eq $item.H -and $_.H -eq $item.W) }
    if ($hit) { continue }
    # 基本幅面 A0~A4 由 AutoCAD 自带的 ISO_full_bleed_* 纸张覆盖；表 3.1.3 注里的
    # 两个特殊幅面插件登记模型表达不了（选不到），都属可选，不计入缺项。
    if ($item.Name -match '^A[0-4]$' -or $item.Name -like '特殊*') { $missingOptional.Add($item) } else { $missing.Add($item) }
}

$requiredCount = ($standard | Where-Object { $_.Name -notmatch '^A[0-4]$' -and $_.Name -notlike '特殊*' }).Count
Write-Host ''
Write-Host ("GB/T 50001-2017 加长幅面核对：应有 {0} 个，纸库缺少 {1} 个。" -f $requiredCount, $missing.Count)
if ($missing.Count -gt 0) {
    $missing | Sort-Object W, H | ForEach-Object {
        Write-Host ("  缺 {0,5} x {1,-5} mm   {2}" -f $_.W, $_.H, $_.Name)
    }
}

if ($missingOptional.Count -gt 0) {
    Write-Host ''
    Write-Host '未加自定义条目的基本幅面（AutoCAD 自带 ISO 纸张已覆盖，正常）：'
    $missingOptional | Sort-Object W, H | ForEach-Object {
        Write-Host ("  {0,5} x {1,-5} mm   {2}" -f $_.W, $_.H, $_.Name)
    }
}

Write-Host ''
if ($mismatched.Count -gt 0) {
    Write-Host ("有 {0} 个条目的名称与实际宽高不符（名称只是标签，出图按实际宽高走，但建议改正以免混淆）。" -f $mismatched.Count)
}

if ($missing.Count -gt 0) {
    Write-Host '缺少的加长幅面在出图时会退回“打印到更大的备用纸 + 矢量裁切”：尺寸比例仍然正确，'
    Write-Host '但每张多一次打印往返，内容被等比放大（线宽也会一起放大）。请按'
    Write-Host 'docs\绘图仪纸张尺寸清单-GBT50001-2017.md 补齐。'
    exit 1
}
Write-Host '加长幅面齐全，横竖两个方向都能 1:1 出图。'
exit 0
