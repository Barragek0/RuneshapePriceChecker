<#
.SYNOPSIS
    Monitors RuneshapePriceChecker performance (CPU, memory, disk, .NET runtime).
    Compares baseline (v0.2.2) against the latest built version.

.DESCRIPTION
    Runs the baseline v0.2.2 release, collects metrics, then builds and runs the
    latest version, and prints a side-by-side comparison.  Use -SkipBaseline to
    monitor only the latest version.  Use -DownloadBaseline to auto-download the
    v0.2.2 release zip from GitHub.

    Collects:
    - OS metrics: CPU%, memory, handles, threads, disk I/O (PowerShell)
    - DWM metrics: CPU%, memory, handles for dwm.exe
    - .NET runtime: GC, thread pool, exceptions (dotnet-counters)
    - CPU hotspots: per-method profiling (dotnet-trace)

.EXAMPLE
    # Compare baseline v0.2.2 vs latest build (15s each), auto-download:
    ./scripts/monitor-performance.ps1 -DownloadBaseline

.EXAMPLE
    # Compare with longer duration:
    ./scripts/monitor-performance.ps1 -DownloadBaseline -DurationSeconds 60

.EXAMPLE
    # Skip baseline, monitor latest only:
    ./scripts/monitor-performance.ps1 -SkipBaseline

.EXAMPLE
    # Attach to an already-running instance:
    ./scripts/monitor-performance.ps1 -ProcessId 12345

.EXAMPLE
    # Analyze a CPU trace afterward:
    ./scripts/analyze-trace.ps1 -TraceFile perf-logs/20260604-120000/cpu-trace.nettrace
#>
param(
    [int]$DurationSeconds = 15,
    [string]$OutputDir = "$PSScriptRoot\..\perf-logs",
    [switch]$SkipInstall,
    [string]$AppArgs = "",
    [int]$ProcessId = 0,
    [string]$BaselinePath = "$env:LOCALAPPDATA\RuneshapePriceChecker\RuneshapePriceChecker.exe",
    [switch]$SkipBaseline,
    [switch]$DownloadBaseline,
    [string]$BaselineVersion = "0.2.2",
    [string]$BaselineDownloadUrl = "https://github.com/Barragek0/RuneshapePriceChecker/releases/download/0.2.2/RuneshapePriceChecker.zip"
)

$ErrorActionPreference = "Stop"
$script:startTime = Get-Date

function Write-Section($title) {
    Write-Host "`n=== $title ===" -ForegroundColor Cyan
}

function Ensure-DotNetTool($toolName, $packageName) {
    if (-not $SkipInstall) {
        $installed = dotnet tool list --global 2>$null | Select-String $toolName
        if (-not $installed) {
            Write-Host "Installing $toolName..." -ForegroundColor Yellow
            dotnet tool install --global $packageName 2>&1 | Out-Null
        }
    }
}

function Ensure-BaselineDownloaded {
    param(
        [string]$Version,
        [string]$DownloadUrl,
        [string]$TargetPath
    )

    if (Test-Path $TargetPath) {
        Write-Host "Baseline v$Version found at: $TargetPath"
        return $TargetPath
    }

    $targetDir = Split-Path $TargetPath -Parent
    $zipPath = Join-Path $targetDir "RuneshapePriceChecker-$Version.zip"

    Write-Host "Downloading v$Version from $DownloadUrl ..." -ForegroundColor Yellow
    New-Item -ItemType Directory -Force -Path $targetDir | Out-Null

    try {
        Invoke-WebRequest -Uri $DownloadUrl -OutFile $zipPath -ErrorAction Stop
        Write-Host "Downloaded: $((Get-Item $zipPath).Length) bytes"

        Write-Host "Extracting v$Version..." -ForegroundColor Yellow
        Expand-Archive -Force -Path $zipPath -DestinationPath $targetDir

        if (Test-Path $TargetPath) {
            Write-Host "Baseline v$Version ready at: $TargetPath" -ForegroundColor Green
            Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
            return $TargetPath
        }

        Write-Host "Warning: Expected exe not found after extraction at $TargetPath" -ForegroundColor Red
        Get-ChildItem $targetDir | ForEach-Object { Write-Host "  $($_.Name)" }
        return $null
    }
    catch {
        Write-Host "Failed to download v${Version}: $_" -ForegroundColor Red
        return $null
    }
}

<#
.SYNOPSIS
    Launches an executable, collects performance metrics, stops it, and returns summary stats.
    When -AttachPid is provided, skips launch and attaches to the existing process instead.
#>
function Invoke-PerformanceRun {
    param(
        [string]$Label,
        [string]$ExePath,
        [string]$RunDir,
        [int]$Duration,
        [string]$Args,
        [int]$AttachPid = 0
    )

    Write-Section "Phase: $Label"

    $procCount = [Environment]::ProcessorCount
    $osLogPath = Join-Path $RunDir "os-metrics.csv"
    $dwmLogPath = Join-Path $RunDir "dwm-metrics.csv"
    $counterLogPath = Join-Path $RunDir "dotnet-counters.csv"
    $traceLogPath = Join-Path $RunDir "cpu-trace.nettrace"
    $summaryPath = Join-Path $RunDir "summary.txt"

    New-Item -ItemType Directory -Force -Path $RunDir | Out-Null

    $ownedProcess = $false
    $appProcess = $null

    if ($AttachPid -ne 0) {
        $appProcess = Get-Process -Id $AttachPid -ErrorAction Stop
        Write-Host "Attached to running process (PID: $($appProcess.Id)). Collecting immediately..."
    }
    elseif ($ExePath) {
        if (-not (Test-Path $ExePath)) {
            throw "Executable not found: $ExePath"
        }
        Write-Host "Launching: $ExePath"
        if ([string]::IsNullOrWhiteSpace($Args)) {
            $appProcess = Start-Process -FilePath $ExePath -PassThru -NoNewWindow
        }
        else {
            $appProcess = Start-Process -FilePath $ExePath -ArgumentList $Args -PassThru -NoNewWindow
        }
        $ownedProcess = $true

        Write-Host "App started (PID: $($appProcess.Id)). Waiting 5s for warm-up..."
        Start-Sleep -Seconds 5

        if ($appProcess.HasExited) {
            throw "App exited immediately (code $($appProcess.ExitCode))."
        }
    }
    else {
        throw "Either -ExePath or -AttachPid must be specified."
    }

    Write-Host "Collecting metrics (${Duration}s in parallel)..."

    $osJob = Start-Job -Name "OSMetrics-$Label" -ScriptBlock {
        param($targetPid, $logPath, $duration, $pollInterval, $procCount)
        "Timestamp,CPU%,MemoryMB,PrivateMB,Handles,Threads,GDIObjects,DiskReadKB,DiskWriteKB" | Out-File $logPath

        $prevCpuTime = $null
        $prevTime = $null
        $endTime = (Get-Date).AddSeconds($duration)

        while ((Get-Date) -lt $endTime) {
            Start-Sleep -Seconds $pollInterval
            try {
                $proc = Get-Process -Id $targetPid -ErrorAction Stop

                $cpu = 0
                $now = Get-Date
                $cpuTime = $proc.TotalProcessorTime

                if ($null -ne $prevCpuTime) {
                    $cpuMs = ($cpuTime - $prevCpuTime).TotalMilliseconds
                    $timeMs = ($now - $prevTime).TotalMilliseconds
                    if ($timeMs -gt 0) {
                        $cpu = [math]::Round(($cpuMs / $timeMs) * 100 / $procCount, 2)
                    }
                }

                $prevCpuTime = $cpuTime
                $prevTime = $now

                $mem = [math]::Round($proc.WorkingSet64 / 1MB, 2)
                $privateMem = [math]::Round($proc.PrivateMemorySize64 / 1MB, 2)
                $timestamp = $now.ToString("HH:mm:ss")

                "$timestamp,$cpu,$mem,$privateMem,$($proc.HandleCount),$($proc.Threads.Count),0,0,0" | Out-File $logPath -Append
            }
            catch {
                break
            }
        }
    } -ArgumentList $appProcess.Id, $osLogPath, $Duration, 1, $procCount

    $dwmJob = Start-Job -Name "DWMMetrics-$Label" -ScriptBlock {
        param($logPath, $duration, $pollInterval, $procCount)
        "Timestamp,CPU%,MemoryMB,Handles,Threads" | Out-File $logPath

        $dwmPid = $null
        $prevCpuTime = $null
        $prevTime = $null
        $endTime = (Get-Date).AddSeconds($duration)

        while ((Get-Date) -lt $endTime) {
            Start-Sleep -Seconds $pollInterval
            try {
                if ($null -eq $dwmPid) {
                    $dwmProc = Get-Process -Name "dwm" -ErrorAction SilentlyContinue
                    if ($dwmProc) { $dwmPid = $dwmProc.Id }
                }

                $proc = Get-Process -Id $dwmPid -ErrorAction Stop

                $cpu = 0
                $now = Get-Date
                $cpuTime = $proc.TotalProcessorTime

                if ($null -ne $prevCpuTime) {
                    $cpuMs = ($cpuTime - $prevCpuTime).TotalMilliseconds
                    $timeMs = ($now - $prevTime).TotalMilliseconds
                    if ($timeMs -gt 0) {
                        $cpu = [math]::Round(($cpuMs / $timeMs) * 100 / $procCount, 2)
                    }
                }

                $prevCpuTime = $cpuTime
                $prevTime = $now

                $mem = [math]::Round($proc.WorkingSet64 / 1MB, 2)
                $timestamp = $now.ToString("HH:mm:ss")

                "$timestamp,$cpu,$mem,$($proc.HandleCount),$($proc.Threads.Count)" | Out-File $logPath -Append
            }
            catch {
                $dwmPid = $null
            }
        }
    } -ArgumentList $dwmLogPath, $Duration, 1, $procCount

    $counterJob = Start-Job -Name "DotNetCounters-$Label" -ScriptBlock {
        param($targetPid, $logPath, $duration)
        dotnet-counters collect --process-id $targetPid --format csv --output $logPath --duration $duration 2>&1 | Out-Null
    } -ArgumentList $appProcess.Id, $counterLogPath, $Duration

    $traceDuration = [math]::Min(10, $Duration)
    $traceJob = Start-Job -Name "DotNetTrace-$Label" -ScriptBlock {
        param($targetPid, $logPath, $duration)
        dotnet-trace collect --process-id $targetPid --format nettrace --output $logPath --duration $duration 2>&1 | Out-Null
    } -ArgumentList $appProcess.Id, $traceLogPath, $traceDuration

    Write-Host "OS metrics, DWM metrics, .NET counters, and CPU trace collecting in parallel..."
    Write-Host "Waiting for all collectors (timeout: $($Duration + 10)s)..."

    $timeoutSeconds = $Duration + 10
    $null = Wait-Job $osJob, $dwmJob, $counterJob, $traceJob -Timeout $timeoutSeconds

    $allJobs = @($osJob, $dwmJob, $counterJob, $traceJob)
    foreach ($job in $allJobs) {
        if ($job.State -eq 'Running') {
            Write-Host "Warning: $($job.Name) timed out; stopping." -ForegroundColor Yellow
            Stop-Job $job -ErrorAction SilentlyContinue
        }
        $output = Receive-Job $job 2>$null
        Remove-Job $job -Force
    }

    Write-Host ""
    if (Test-Path $osLogPath) {
        $osLines = (Get-Content $osLogPath | Measure-Object -Line).Lines
        Write-Host "OS metrics:     $($osLines - 1) samples  -> $osLogPath" -ForegroundColor Green
    }
    else {
        Write-Host "OS metrics:     not collected" -ForegroundColor Yellow
    }

    if (Test-Path $dwmLogPath) {
        $dwmLines = (Get-Content $dwmLogPath | Measure-Object -Line).Lines
        Write-Host "DWM metrics:    $($dwmLines - 1) samples  -> $dwmLogPath" -ForegroundColor Green
    }
    else {
        Write-Host "DWM metrics:    not collected" -ForegroundColor Yellow
    }

    if ((Test-Path $counterLogPath) -and ((Get-Item $counterLogPath).Length -gt 0)) {
        Write-Host ".NET counters:  collected        -> $counterLogPath" -ForegroundColor Green
    }
    else {
        Write-Host ".NET counters:  not collected" -ForegroundColor Yellow
    }

    if (Test-Path $traceLogPath) {
        Write-Host "CPU trace:      collected        -> $traceLogPath" -ForegroundColor Green
        Write-Host "  Analyze with: dotnet-trace report `"$traceLogPath`" topN --inclusive" -ForegroundColor DarkGray
    }
    else {
        Write-Host "CPU trace:      not collected" -ForegroundColor Yellow
    }

    if ($ownedProcess -and -not $appProcess.HasExited) {
        Write-Host "Stopping app..."
        $appProcess.CloseMainWindow()
        $appProcess.WaitForExit(5000) | Out-Null
        if (-not $appProcess.HasExited) {
            $appProcess.Kill()
        }
    }

    $stats = @{
        Label = $Label
        CpuAvg = "N/A"; CpuMax = "N/A"
        MemAvg = "N/A"; MemMax = "N/A"
        HandleAvg = "N/A"; ThreadAvg = "N/A"
        DwmCpuAvg = "N/A"; DwmCpuMax = "N/A"
        DwmMemAvg = "N/A"; DwmHandleAvg = "N/A"
    }

    if (Test-Path $osLogPath) {
        $osData = Import-Csv $osLogPath
        if ($osData.Count -gt 0) {
            $stats.CpuAvg = [math]::Round(($osData | Measure-Object -Property 'CPU%' -Average).Average, 2)
            $stats.CpuMax = [math]::Round(($osData | Measure-Object -Property 'CPU%' -Maximum).Maximum, 2)
            $stats.MemAvg = [math]::Round(($osData | Measure-Object -Property 'MemoryMB' -Average).Average, 2)
            $stats.MemMax = [math]::Round(($osData | Measure-Object -Property 'MemoryMB' -Maximum).Maximum, 2)
            $stats.HandleAvg = [math]::Round(($osData | Measure-Object -Property 'Handles' -Average).Average, 0)
            $stats.ThreadAvg = [math]::Round(($osData | Measure-Object -Property 'Threads' -Average).Average, 0)
        }
    }

    if (Test-Path $dwmLogPath) {
        $dwmData = Import-Csv $dwmLogPath
        if ($dwmData.Count -gt 0) {
            $stats.DwmCpuAvg = [math]::Round(($dwmData | Measure-Object -Property 'CPU%' -Average).Average, 2)
            $stats.DwmCpuMax = [math]::Round(($dwmData | Measure-Object -Property 'CPU%' -Maximum).Maximum, 2)
            $stats.DwmMemAvg = [math]::Round(($dwmData | Measure-Object -Property 'MemoryMB' -Average).Average, 2)
            $stats.DwmHandleAvg = [math]::Round(($dwmData | Measure-Object -Property 'Handles' -Average).Average, 0)
        }
    }

    $dwmWarning = ""
    if ($stats.DwmCpuAvg -ne "N/A" -and [double]$stats.DwmCpuAvg -gt 2.0) {
        $dwmWarning = "  ** WARNING: DWM CPU is elevated. Tool may be overloading the compositor. **"
    }

    $summary = @"

Performance Summary: $Label
==============================$(("-" * $Label.Length))
Date: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
Duration: ${Duration}s
Process: RuneshapePriceChecker (PID: $($appProcess.Id))

App Metrics:
  CPU (avg):     $($stats.CpuAvg)%
  CPU (max):     $($stats.CpuMax)%
  Memory (avg):  $($stats.MemAvg) MB
  Memory (max):  $($stats.MemMax) MB
  Handles (avg): $($stats.HandleAvg)
  Threads (avg): $($stats.ThreadAvg)

DWM (dwm.exe) Metrics:
  CPU (avg):     $($stats.DwmCpuAvg)%
  CPU (max):     $($stats.DwmCpuMax)%
  Memory (avg):  $($stats.DwmMemAvg) MB
  Handles (avg): $($stats.DwmHandleAvg)
$dwmWarning
"@

    $summary | Out-File $summaryPath
    Write-Host $summary

    if (Test-Path $traceLogPath) {
        Write-Host ""
        Write-Host "CPU trace captured. Generating hot-path report..." -ForegroundColor Cyan
        $reportPath = Join-Path $RunDir "cpu-hotpath.txt"
        dotnet-trace report $traceLogPath topN --inclusive 2>&1 | Out-File $reportPath -Encoding UTF8
        if (Test-Path $reportPath) {
            Write-Host "Top CPU consumers:" -ForegroundColor Yellow
            Get-Content $reportPath | Select-Object -First 25 | ForEach-Object { Write-Host "  $_" }
            Write-Host "Full report: $reportPath" -ForegroundColor DarkGray
        }
    }

    return $stats
}

# ==================================================================
# Main
# ==================================================================

$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { $PWD }
$projectRoot = [System.IO.Path]::GetFullPath("$scriptDir\..")
$projectPath = Join-Path $projectRoot "RuneshapePriceChecker.csproj"

if ($OutputDir -notmatch '^[A-Za-z]:\\') {
    $OutputDir = [System.IO.Path]::GetFullPath("$scriptDir\$OutputDir")
}
$runDir = [System.IO.Path]::GetFullPath((Join-Path $OutputDir (Get-Date -Format "yyyyMMdd-HHmmss")))
New-Item -ItemType Directory -Force -Path $runDir | Out-Null

Write-Section "Setup"
Write-Host "Output directory: $runDir"

Ensure-DotNetTool "dotnet-counters" "dotnet-counters"
Ensure-DotNetTool "dotnet-trace" "dotnet-trace"

$allStats = @()

# ---- Phase 1: Baseline (v0.2.2) ----
$runBaseline = (-not $SkipBaseline) -and ($ProcessId -eq 0)

if ($runBaseline) {
    if ($DownloadBaseline) {
        $BaselinePath = Ensure-BaselineDownloaded -Version $BaselineVersion -DownloadUrl $BaselineDownloadUrl -TargetPath $BaselinePath
    }

    if (-not (Test-Path $BaselinePath)) {
        Write-Host "Baseline not found at: $BaselinePath" -ForegroundColor DarkGray
        Write-Host "Use -DownloadBaseline to auto-download v$BaselineVersion, or install manually." -ForegroundColor DarkGray
    }
    else {
        Write-Host ""
        Write-Host "Baseline found: $BaselinePath" -ForegroundColor Magenta
        $baselineDir = Join-Path $runDir "baseline"
        try {
            $baselineStats = Invoke-PerformanceRun -Label "Baseline (v$BaselineVersion)" -ExePath $BaselinePath -RunDir $baselineDir -Duration $DurationSeconds -Args $AppArgs
            $allStats += $baselineStats
        }
        catch {
            Write-Host "Baseline run failed: $_" -ForegroundColor Red
            Write-Host "Continuing with latest only..." -ForegroundColor Yellow
        }

        Write-Host "`nWaiting 3s before launching latest version..." -ForegroundColor DarkGray
        Start-Sleep -Seconds 3
    }
}
elseif ($SkipBaseline) {
    Write-Host "Baseline comparison skipped (-SkipBaseline)." -ForegroundColor DarkGray
}
elseif ($ProcessId -ne 0) {
    Write-Host "Baseline skipped (attaching to existing process)." -ForegroundColor DarkGray
}

# ---- Phase 2: Latest ----
if ($ProcessId -ne 0) {
    $latestDir = Join-Path $runDir "latest"
    $latestStats = Invoke-PerformanceRun -Label "Latest (attached PID $ProcessId)" -RunDir $latestDir -Duration $DurationSeconds -AttachPid $ProcessId
    $allStats += $latestStats
}
else {
    Write-Section "Building Latest Version"
    Write-Host "Building Release..."
    dotnet build $projectPath -c Release --nologo 2>&1 | Select-Object -Last 3

    $appProcess = Start-Process -FilePath "dotnet" `
        -ArgumentList "run --project `"$projectPath`" -c Release --nologo $AppArgs" `
        -PassThru -NoNewWindow

    Write-Host "App started (PID: $($appProcess.Id)). Waiting 5s for warm-up..."
    Start-Sleep -Seconds 5

    if ($appProcess.HasExited) {
        throw "App exited immediately (code $($appProcess.ExitCode)). Check console output."
    }

    $latestDir = Join-Path $runDir "latest"
    $latestStats = Invoke-PerformanceRun -Label "Latest (built from source)" -RunDir $latestDir -Duration $DurationSeconds -AttachPid $appProcess.Id
    $allStats += $latestStats

    if (-not $appProcess.HasExited) {
        Write-Host "Stopping dotnet run process..."
        $appProcess.CloseMainWindow()
        $appProcess.WaitForExit(5000) | Out-Null
        if (-not $appProcess.HasExited) { $appProcess.Kill() }
    }
}

# ---- Comparison Summary ----
if ($allStats.Count -ge 2) {
    Write-Section "Comparison: Baseline vs Latest"

    function Format-Delta($oldVal, $newVal, $unit) {
        if ($oldVal -eq "N/A" -or $newVal -eq "N/A") { return "N/A" }
        $old = [double]$oldVal
        $new = [double]$newVal
        $delta = $new - $old
        $pct = if ($old -ne 0) { [math]::Round(($delta / $old) * 100, 1) } else { "N/A" }
        $sign = if ($delta -gt 0.05) { "+" } elseif ($delta -lt -0.05) { "" } else { " " }
        $arrow = if ($delta -gt 0.05) { "[UP]" } elseif ($delta -lt -0.05) { "[DOWN]" } else { "[--]" }
        return "$arrow $sign$delta$unit ($pct%)"
    }

    $old = $allStats[0]
    $new = $allStats[1]

    $comparison = @"

Side-by-Side Comparison
========================
$($old.Label.PadRight(32)) vs $($new.Label)

App CPU (avg):      $($old.CpuAvg.ToString().PadLeft(7))%  ->  $($new.CpuAvg.ToString().PadLeft(7))%   $(Format-Delta $old.CpuAvg $new.CpuAvg '%')
App CPU (max):      $($old.CpuMax.ToString().PadLeft(7))%  ->  $($new.CpuMax.ToString().PadLeft(7))%   $(Format-Delta $old.CpuMax $new.CpuMax '%')
App Memory (avg):   $($old.MemAvg.ToString().PadLeft(7)) MB ->  $($new.MemAvg.ToString().PadLeft(7)) MB  $(Format-Delta $old.MemAvg $new.MemAvg ' MB')
App Memory (max):   $($old.MemMax.ToString().PadLeft(7)) MB ->  $($new.MemMax.ToString().PadLeft(7)) MB  $(Format-Delta $old.MemMax $new.MemMax ' MB')
App Handles (avg):  $($old.HandleAvg.ToString().PadLeft(7))    ->  $($new.HandleAvg.ToString().PadLeft(7))     $(Format-Delta $old.HandleAvg $new.HandleAvg '')
App Threads (avg):  $($old.ThreadAvg.ToString().PadLeft(7))    ->  $($new.ThreadAvg.ToString().PadLeft(7))     $(Format-Delta $old.ThreadAvg $new.ThreadAvg '')

DWM CPU (avg):      $($old.DwmCpuAvg.ToString().PadLeft(7))%  ->  $($new.DwmCpuAvg.ToString().PadLeft(7))%   $(Format-Delta $old.DwmCpuAvg $new.DwmCpuAvg '%')
DWM CPU (max):      $($old.DwmCpuMax.ToString().PadLeft(7))%  ->  $($new.DwmCpuMax.ToString().PadLeft(7))%   $(Format-Delta $old.DwmCpuMax $new.DwmCpuMax '%')
DWM Memory (avg):   $($old.DwmMemAvg.ToString().PadLeft(7)) MB ->  $($new.DwmMemAvg.ToString().PadLeft(7)) MB  $(Format-Delta $old.DwmMemAvg $new.DwmMemAvg ' MB')
DWM Handles (avg):  $($old.DwmHandleAvg.ToString().PadLeft(7))    ->  $($new.DwmHandleAvg.ToString().PadLeft(7))     $(Format-Delta $old.DwmHandleAvg $new.DwmHandleAvg '')

Legend: [DOWN] = lower is better (CPU, memory, handles, threads)
        [UP] = higher is worse
        [--] = no meaningful change
"@

    $comparisonPath = Join-Path $runDir "comparison.txt"
    $comparison | Out-File $comparisonPath
    Write-Host $comparison

    if ($new.DwmCpuAvg -ne "N/A" -and [double]$new.DwmCpuAvg -gt 2.0) {
        Write-Host "** WARNING: DWM CPU is elevated in latest. Check for compositor disruption. **" -ForegroundColor Red
    }
}

Write-Host "All logs saved to: $runDir" -ForegroundColor Green
