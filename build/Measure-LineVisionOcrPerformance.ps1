[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$WorkerPath,

    [Parameter(Mandatory = $true)]
    [string]$ImagePath,

    [ValidateRange(1, 20)]
    [int]$Runs = 3,

    [ValidateRange(10, 1000)]
    [int]$SampleIntervalMilliseconds = 100,

    [string]$JsonOutputPath
)

$ErrorActionPreference = 'Stop'

function Resolve-ExistingFile {
    param([string]$Path, [string]$Label)

    $resolved = [System.IO.Path]::GetFullPath($Path)
    if (-not [System.IO.File]::Exists($resolved)) {
        throw "$Label does not exist: $resolved"
    }

    return $resolved
}

function Get-DirectorySizeBytes {
    param([string]$Path)

    $sum = 0L
    Get-ChildItem -LiteralPath $Path -File -Recurse -Force | ForEach-Object {
        $sum += $_.Length
    }
    return $sum
}

$resolvedWorker = Resolve-ExistingFile -Path $WorkerPath -Label 'OCR worker'
$resolvedImage = Resolve-ExistingFile -Path $ImagePath -Label 'Test image'
$componentDirectory = Split-Path -LiteralPath $resolvedWorker
$componentBytes = Get-DirectorySizeBytes -Path $componentDirectory

$tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$benchmarkDirectory = Join-Path $tempRoot ('WanLuo-LineVision-Ocr-Benchmark-' + [Guid]::NewGuid().ToString('N'))
[System.IO.Directory]::CreateDirectory($benchmarkDirectory) | Out-Null

$utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
$measurements = @()
$completed = $false

try {
    for ($run = 1; $run -le $Runs; $run++) {
        $requestId = [Guid]::NewGuid().ToString('N')
        $requestPath = Join-Path $benchmarkDirectory ("request-$run.json")
        $resultPath = Join-Path $benchmarkDirectory ("result-$run.json")
        $stdoutPath = Join-Path $benchmarkDirectory ("stdout-$run.jsonl")
        $stderrPath = Join-Path $benchmarkDirectory ("stderr-$run.log")
        $request = [ordered]@{
            ProtocolVersion = 2
            RequestId = $requestId
            ImagePath = $resolvedImage
            Language = 'zh-Hans-CN'
        }
        [System.IO.File]::WriteAllText(
            $requestPath,
            ($request | ConvertTo-Json -Compress),
            $utf8WithoutBom)

        $arguments = @(
            '--request', ('"' + $requestPath + '"'),
            '--output', ('"' + $resultPath + '"'))
        $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        $process = Start-Process -FilePath $resolvedWorker `
            -ArgumentList $arguments `
            -RedirectStandardOutput $stdoutPath `
            -RedirectStandardError $stderrPath `
            -WindowStyle Hidden `
            -PassThru

        $peakWorkingSetBytes = 0L
        $maximumCpuSeconds = 0.0
        while (-not $process.HasExited) {
            try {
                $process.Refresh()
                if ($process.WorkingSet64 -gt $peakWorkingSetBytes) {
                    $peakWorkingSetBytes = $process.WorkingSet64
                }
                if ($process.TotalProcessorTime.TotalSeconds -gt $maximumCpuSeconds) {
                    $maximumCpuSeconds = $process.TotalProcessorTime.TotalSeconds
                }
            }
            catch {
                # The process may exit between HasExited and Refresh.
            }
            Start-Sleep -Milliseconds $SampleIntervalMilliseconds
        }

        $process.WaitForExit()
        $stopwatch.Stop()
        $process.Refresh()
        if ($process.PeakWorkingSet64 -gt $peakWorkingSetBytes) {
            $peakWorkingSetBytes = $process.PeakWorkingSet64
        }
        try {
            if ($process.TotalProcessorTime.TotalSeconds -gt $maximumCpuSeconds) {
                $maximumCpuSeconds = $process.TotalProcessorTime.TotalSeconds
            }
        }
        catch {
            # The last in-process sample remains valid when PowerShell releases the process handle early.
        }
        $cpuSeconds = $maximumCpuSeconds

        if (-not [System.IO.File]::Exists($resultPath)) {
            $stderr = if ([System.IO.File]::Exists($stderrPath)) {
                [System.IO.File]::ReadAllText($stderrPath, [System.Text.Encoding]::UTF8)
            } else { '' }
            throw "Run $run did not produce a result (exit code $($process.ExitCode)): $stderr"
        }

        $result = [System.IO.File]::ReadAllText($resultPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
        $exitCode = $process.ExitCode
        if (($null -ne $exitCode -and $exitCode -ne 0) -or -not $result.Success) {
            $stderr = if ([System.IO.File]::Exists($stderrPath)) {
                [System.IO.File]::ReadAllText($stderrPath, [System.Text.Encoding]::UTF8)
            } else { '' }
            $serializedResult = $result | ConvertTo-Json -Depth 5 -Compress
            throw "Run $run failed (exit code $exitCode, result $serializedResult): $stderr"
        }
        if ($result.ProtocolVersion -ne 2 -or $result.RequestId -ne $requestId) {
            throw "Run $run returned a mismatched protocol version or request ID."
        }

        $regionCount = if ($null -eq $result.TextRegions) { 0 } else { @($result.TextRegions).Count }
        $measurement = [pscustomobject]@{
            Run = $run
            Kind = if ($run -eq 1) { 'cold-process-start' } else { 'repeated-process-start' }
            ElapsedSeconds = [Math]::Round($stopwatch.Elapsed.TotalSeconds, 3)
            CpuSeconds = [Math]::Round($cpuSeconds, 3)
            PeakWorkingSetMB = [Math]::Round($peakWorkingSetBytes / 1MB, 1)
            TextRegionCount = $regionCount
            ImageWidth = [int]$result.ImageWidth
            ImageHeight = [int]$result.ImageHeight
        }
        $measurements += $measurement
        Write-Host ("[{0}/{1}] {2}: {3:N3} sec, CPU {4:N3} sec, peak {5:N1} MB, regions {6}" -f `
            $run, $Runs, $measurement.Kind, $measurement.ElapsedSeconds, $measurement.CpuSeconds,
            $measurement.PeakWorkingSetMB, $measurement.TextRegionCount)
    }

    $repeatMeasurements = @($measurements | Select-Object -Skip 1)
    $repeatAverage = if ($repeatMeasurements.Count -gt 0) {
        [Math]::Round(($repeatMeasurements | Measure-Object -Property ElapsedSeconds -Average).Average, 3)
    } else { $null }
    $summary = [pscustomobject]@{
        MeasuredAt = [DateTimeOffset]::Now.ToString('o')
        WorkerPath = $resolvedWorker
        ImagePath = $resolvedImage
        RunCount = $Runs
        ColdStartSeconds = $measurements[0].ElapsedSeconds
        RepeatIndependentStartAverageSeconds = $repeatAverage
        MaximumPeakWorkingSetMB = [Math]::Round(($measurements | Measure-Object -Property PeakWorkingSetMB -Maximum).Maximum, 1)
        ComponentSizeMB = [Math]::Round($componentBytes / 1MB, 1)
        Measurements = @($measurements)
    }

    Write-Host ''
    Write-Host ("Cold process start: {0:N3} sec" -f $summary.ColdStartSeconds)
    if ($null -ne $summary.RepeatIndependentStartAverageSeconds) {
        Write-Host ("Repeated process start average: {0:N3} sec" -f $summary.RepeatIndependentStartAverageSeconds)
    }
    Write-Host ("Maximum peak working set: {0:N1} MB" -f $summary.MaximumPeakWorkingSetMB)
    Write-Host ("Published component size: {0:N1} MB" -f $summary.ComponentSizeMB)

    if (-not [string]::IsNullOrWhiteSpace($JsonOutputPath)) {
        $resolvedJsonOutput = [System.IO.Path]::GetFullPath($JsonOutputPath)
        $jsonParent = Split-Path -LiteralPath $resolvedJsonOutput
        if (-not [string]::IsNullOrWhiteSpace($jsonParent)) {
            [System.IO.Directory]::CreateDirectory($jsonParent) | Out-Null
        }
        [System.IO.File]::WriteAllText(
            $resolvedJsonOutput,
            ($summary | ConvertTo-Json -Depth 5),
            $utf8WithoutBom)
        Write-Host "JSON result: $resolvedJsonOutput"
    }

    $completed = $true
    $summary
}
finally {
    $resolvedBenchmarkDirectory = [System.IO.Path]::GetFullPath($benchmarkDirectory)
    $expectedPrefix = $tempRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if ($completed -and
        $resolvedBenchmarkDirectory.StartsWith($expectedPrefix, [System.StringComparison]::OrdinalIgnoreCase) -and
        [System.IO.Path]::GetFileName($resolvedBenchmarkDirectory).StartsWith('WanLuo-LineVision-Ocr-Benchmark-', [System.StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedBenchmarkDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
    elseif (-not $completed) {
        Write-Warning "Benchmark artifacts retained for diagnosis: $resolvedBenchmarkDirectory"
    }
}
