<#
.SYNOPSIS
    Monitors RuneshapePriceChecker performance (CPU, memory, disk, .NET runtime).

.DESCRIPTION
    Launches the app (or attaches to a running instance) and collects:
    - OS metrics: CPU%, memory, handles, threads, disk I/O (PowerShell)
    - .NET runtime: GC, thread pool, exceptions (dotnet-counters)
    - CPU hotspots: per-method profiling (dotnet-trace, with -CpuSample)

.EXAMPLE
    # Launch app and monitor for 30s (no CPU sampling):
    ./scripts/monitor-performance.ps1

.EXAMPLE
    # Launch app, monitor for 60s with CPU sampling:
    ./scripts/monitor-performance.ps1 -DurationSeconds 60 -CpuSample

.EXAMPLE
    # Attach to an already-running instance (find PID in Task Manager):
    ./scripts/monitor-performance.ps1 -ProcessId 12345

.EXAMPLE
    # Analyze the CPU trace afterward:
    ./scripts/analyze-trace.ps1 -TraceFile perf-logs/20260604-120000/cpu-trace.nettrace
#>
param(
    [int]$DurationSeconds = 15,
    [string]$OutputDir = "$PSScriptRoot\..\perf-logs",
    [switch]$SkipInstall,
    [string]$AppArgs = "",
    [int]$ProcessId = 0
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

# ------------------------------------------------------------------
Write-Section "Setup"
# ------------------------------------------------------------------

$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { $PWD }
$projectRoot = [System.IO.Path]::GetFullPath("$scriptDir\..")
$projectPath = Join-Path $projectRoot "RuneshapePriceChecker.csproj"

if ($OutputDir -notmatch '^[A-Za-z]:\\') {
    $OutputDir = [System.IO.Path]::GetFullPath("$scriptDir\$OutputDir")
}
$runDir = [System.IO.Path]::GetFullPath((Join-Path $OutputDir (Get-Date -Format "yyyyMMdd-HHmmss")))
New-Item -ItemType Directory -Force -Path $runDir | Out-Null

Write-Host "Output directory: $runDir"

# Install diagnostic tools
Ensure-DotNetTool "dotnet-counters" "dotnet-counters"
Ensure-DotNetTool "dotnet-trace" "dotnet-trace"

# ------------------------------------------------------------------
Write-Section "Launching Application"
# ------------------------------------------------------------------

$ownedProcess = $false
if ($ProcessId -eq 0) {
    Write-Host "Building Release..."
    dotnet build $projectPath -c Release --nologo 2>&1 | Select-Object -Last 3

    $appProcess = Start-Process -FilePath "dotnet" `
        -ArgumentList "run --project `"$projectPath`" -c Release --nologo $AppArgs" `
        -PassThru -NoNewWindow
    $ownedProcess = $true

    Write-Host "App started (PID: $($appProcess.Id)). Waiting 5s for warm-up..."
    Start-Sleep -Seconds 5

    if ($appProcess.HasExited) {
        throw "App exited immediately (code $($appProcess.ExitCode)). Check console output."
    }
}
else {
    $appProcess = Get-Process -Id $ProcessId -ErrorAction Stop
    Write-Host "Attached to running process (PID: $($appProcess.Id)). Collecting immediately..."
}

# ------------------------------------------------------------------
Write-Section "Collecting Metrics (${DurationSeconds}s in parallel)"
# ------------------------------------------------------------------

$procCount = [Environment]::ProcessorCount
$osLogPath = Join-Path $runDir "os-metrics.csv"
$dwmLogPath = Join-Path $runDir "dwm-metrics.csv"
$counterLogPath = Join-Path $runDir "dotnet-counters.csv"
$traceLogPath = Join-Path $runDir "cpu-trace.nettrace"

# ---- Job 1: OS metrics (CPU, memory, handles, threads) ----
$osJob = Start-Job -Name "OSMetrics" -ScriptBlock {
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
            $gdi = $proc.HandleCount
            $timestamp = $now.ToString("HH:mm:ss")

            "$timestamp,$cpu,$mem,$privateMem,$($proc.HandleCount),$($proc.Threads.Count),$gdi,0,0" | Out-File $logPath -Append
        }
        catch {
            break
        }
    }
} -ArgumentList $appProcess.Id, $osLogPath, $DurationSeconds, 1, $procCount

# ---- Job 2: DWM metrics ----
$dwmJob = Start-Job -Name "DWMMetrics" -ScriptBlock {
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
} -ArgumentList $dwmLogPath, $DurationSeconds, 1, $procCount

# ---- Job 3: .NET runtime counters ----
$counterJob = Start-Job -Name "DotNetCounters" -ScriptBlock {
    param($targetPid, $logPath, $duration)
    dotnet-counters collect --process-id $targetPid --format csv --output $logPath --duration $duration 2>&1 | Out-Null
} -ArgumentList $appProcess.Id, $counterLogPath, $DurationSeconds

# ---- Job 3: CPU trace (shorter — sampling only needs a few seconds) ----
$traceDuration = [math]::Min(10, $DurationSeconds)
$traceJob = Start-Job -Name "DotNetTrace" -ScriptBlock { # Job 4
    param($targetPid, $logPath, $duration)
    dotnet-trace collect --process-id $targetPid --format nettrace --output $logPath --duration $duration 2>&1 | Out-Null
} -ArgumentList $appProcess.Id, $traceLogPath, $traceDuration

Write-Host "OS metrics, DWM metrics, .NET counters, and CPU trace collecting in parallel..."
Write-Host "Waiting for all collectors (timeout: $($DurationSeconds + 10)s)..."

$timeoutSeconds = $DurationSeconds + 10
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

# ---- Collection report ----
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

# ------------------------------------------------------------------
Write-Section "Stopping"
# ------------------------------------------------------------------

if ($ownedProcess -and -not $appProcess.HasExited) {
    Write-Host "Stopping app..."
    $appProcess.CloseMainWindow()
    $appProcess.WaitForExit(5000) | Out-Null
    if (-not $appProcess.HasExited) {
        $appProcess.Kill()
    }
}
else {
    Write-Host "App left running (attached mode)."
}

# ------------------------------------------------------------------
Write-Section "Summary"
# ------------------------------------------------------------------

$summaryPath = Join-Path $runDir "summary.txt"

$cpuAvg = "N/A"; $cpuMax = "N/A"; $memAvg = "N/A"; $memMax = "N/A"
$handleAvg = "N/A"; $threadAvg = "N/A"

if (Test-Path $osLogPath) {
    $osData = Import-Csv $osLogPath
    if ($osData.Count -gt 0) {
        $cpuAvg = [math]::Round(($osData | Measure-Object -Property 'CPU%' -Average).Average, 2)
        $cpuMax = [math]::Round(($osData | Measure-Object -Property 'CPU%' -Maximum).Maximum, 2)
        $memAvg = [math]::Round(($osData | Measure-Object -Property 'MemoryMB' -Average).Average, 2)
        $memMax = [math]::Round(($osData | Measure-Object -Property 'MemoryMB' -Maximum).Maximum, 2)
        $handleAvg = [math]::Round(($osData | Measure-Object -Property 'Handles' -Average).Average, 0)
        $threadAvg = [math]::Round(($osData | Measure-Object -Property 'Threads' -Average).Average, 0)
    }
}

$dwmCpuAvg = "N/A"; $dwmCpuMax = "N/A"; $dwmMemAvg = "N/A"; $dwmHandleAvg = "N/A"

if (Test-Path $dwmLogPath) {
    $dwmData = Import-Csv $dwmLogPath
    if ($dwmData.Count -gt 0) {
        $dwmCpuAvg = [math]::Round(($dwmData | Measure-Object -Property 'CPU%' -Average).Average, 2)
        $dwmCpuMax = [math]::Round(($dwmData | Measure-Object -Property 'CPU%' -Maximum).Maximum, 2)
        $dwmMemAvg = [math]::Round(($dwmData | Measure-Object -Property 'MemoryMB' -Average).Average, 2)
        $dwmHandleAvg = [math]::Round(($dwmData | Measure-Object -Property 'Handles' -Average).Average, 0)
    }
}

$dwmWarning = ""
if ($dwmCpuAvg -ne "N/A" -and [double]$dwmCpuAvg -gt 2.0) {
    $dwmWarning = "  ** WARNING: DWM CPU is elevated. Tool may be overloading the compositor. **"
}

$summary = @"

Performance Monitoring Summary
==============================
Date: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
Duration: ${DurationSeconds}s
Process: RuneshapePriceChecker (PID: $($appProcess.Id))

App Metrics:
  CPU (avg):     $cpuAvg%
  CPU (max):     $cpuMax%
  Memory (avg):  $memAvg MB
  Memory (max):  $memMax MB
  Handles (avg): $handleAvg
  Threads (avg): $threadAvg

DWM (dwm.exe) Metrics:
  CPU (avg):     $dwmCpuAvg%
  CPU (max):     $dwmCpuMax%
  Memory (avg):  $dwmMemAvg MB
  Handles (avg): $dwmHandleAvg
$dwmWarning
"@

$summary | Out-File $summaryPath
Write-Host $summary

Write-Host "All logs saved to: $runDir" -ForegroundColor Green
if (Test-Path $traceLogPath) {
    Write-Host ""
    Write-Host "CPU trace captured. Generating hot-path report..." -ForegroundColor Cyan
    $reportPath = Join-Path $runDir "cpu-hotpath.txt"
    dotnet-trace report $traceLogPath topN --inclusive 2>&1 | Out-File $reportPath -Encoding UTF8
    if (Test-Path $reportPath) {
        Write-Host "Top CPU consumers:" -ForegroundColor Yellow
        Get-Content $reportPath | Select-Object -First 25 | ForEach-Object { Write-Host "  $_" }
        Write-Host "Full report: $reportPath" -ForegroundColor DarkGray
    }
}