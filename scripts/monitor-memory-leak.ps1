<#
.SYNOPSIS
    Monitors RuneshapePriceChecker memory usage over time to detect leaks.

.DESCRIPTION
    Runs the app for a configurable duration (default 10 minutes), samples memory
    every few seconds, and produces a text-based graph showing trends. Use with
    the league panel open and stationary for consistent results.

.EXAMPLE
    # Monitor self-contained published build for 10 min:
    ./scripts/monitor-memory.ps1

.EXAMPLE
    # Monitor framework-dependent build via dotnet run:
    ./scripts/monitor-memory.ps1 -UseDotnetRun

.EXAMPLE
    # Monitor an already-running instance by PID:
    ./scripts/monitor-memory.ps1 -ProcessId 12345
#>
param(
    [int]$DurationSeconds = 600,
    [int]$SampleIntervalSeconds = 5,
    [string]$OutputDir = "$PSScriptRoot\..\perf-logs",
    [int]$ProcessId = 0,
    [switch]$UseDotnetRun
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot | Split-Path -Parent

# --- Setup ---
$runDir = Join-Path $OutputDir "memory-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
New-Item -ItemType Directory -Force $runDir | Out-Null

$exe = if ($UseDotnetRun) { $null } else { "$root\bin\Release\RuneshapePriceChecker.exe" }
$config = "$root\scripts\perf-test-config.json"
$configDest = "$root\bin\Release\config\appsettings.json"

$appProc = $null
$ownProcess = $false

if ($ProcessId -gt 0) {
    $appProc = Get-Process -Id $ProcessId -ErrorAction Stop
    Write-Host "Attached to PID $ProcessId" -ForegroundColor Cyan
}
else {
    Stop-App
    Start-Sleep -Seconds 1

    if ($UseDotnetRun) {
        Write-Host "Starting via dotnet run..." -ForegroundColor Cyan
        $appProc = Start-Process -FilePath "dotnet" -ArgumentList "run --project `"$root\RuneshapePriceChecker.csproj`" -c Release --nologo -- --App:SuppressAlreadyRunningWarning=true" -PassThru -NoNewWindow
    }
    else {
        if (Test-Path $config) {
            New-Item -ItemType Directory -Force (Split-Path $configDest) | Out-Null
            Copy-Item $config $configDest -Force
        }
        $appProc = Start-Process -FilePath $exe -ArgumentList "--App:SuppressAlreadyRunningWarning=true" -PassThru
    }
    $ownProcess = $true
    Write-Host "Started PID $($appProc.Id), warming up 5s..." -ForegroundColor Cyan
    Start-Sleep -Seconds 5
}

if ($appProc.HasExited) {
    throw "App exited immediately (code $($appProc.ExitCode))."
}

# --- Collect ---
$samples = [System.Collections.ArrayList]::new()
$startMem = 0
$totalSamples = [math]::Ceiling($DurationSeconds / $SampleIntervalSeconds)
$barWidth = 50

Write-Host "`nMonitoring for $DurationSeconds seconds (sample every ${SampleIntervalSeconds}s, $totalSamples samples)..." -ForegroundColor Yellow
Write-Host ("─" * 60)

for ($i = 0; $i -lt $totalSamples; $i++) {
    Start-Sleep -Seconds $SampleIntervalSeconds

    if ($appProc.HasExited) {
        Write-Host "App exited at sample $i" -ForegroundColor Red
        break
    }

    try {
        $appProc.Refresh()
        $ws = [math]::Round($appProc.WorkingSet64 / 1MB, 1)
        $priv = [math]::Round($appProc.PrivateMemorySize64 / 1MB, 1)
        $handles = $appProc.HandleCount
        $threads = $appProc.Threads.Count
        $cpu = [math]::Round((Get-Process -Id $appProc.Id).CPU, 1)
    }
    catch {
        Write-Host "Error reading process at sample $i" -ForegroundColor Red
        break
    }

    if ($i -eq 0) { $startMem = $ws }

    $sample = [PSCustomObject]@{
        Elapsed   = $i * $SampleIntervalSeconds
        WorkingMB = $ws
        PrivateMB = $priv
        Handles   = $handles
        Threads   = $threads
        CpuSec    = $cpu
    }
    [void]$samples.Add($sample)

    # Progress bar
    $pct = [math]::Min(100, [math]::Round(($i + 1) / $totalSamples * 100))
    $filled = [math]::Round($barWidth * $pct / 100)
    $bar = ("#" * $filled) + ("-" * ($barWidth - $filled))
    Write-Host ("`r[$bar] $pct%  WS:${ws}MB  Priv:${priv}MB  H:$handles  T:$threads") -NoNewline
}

# --- Stop app ---
if ($ownProcess -and -not $appProc.HasExited) {
    $appProc.Kill()
}
Write-Host "`n"

# --- Report ---
$dataFile = Join-Path $runDir "memory-samples.csv"
$reportFile = Join-Path $runDir "memory-report.txt"

$samples | Export-Csv $dataFile -NoTypeInformation

$peak = ($samples | Sort-Object WorkingMB -Descending)[0]
$end = $samples[-1]
$min = ($samples | Sort-Object WorkingMB)[0]
$avg = [math]::Round(($samples | Measure-Object WorkingMB -Average).Average, 1)
$delta = [math]::Round($end.WorkingMB - $startMem, 1)
$privDelta = [math]::Round($end.PrivateMB - $samples[0].PrivateMB, 1)

$graph = Build-TextGraph $samples $barWidth

$report = @"
Memory Leak Test Report
=======================
Date: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
Duration: ${DurationSeconds}s / $($samples.Count) samples
Config: $(if ($ProcessId -gt 0){"Attached PID $ProcessId"}elseif($UseDotnetRun){"dotnet run"}else{"Self-contained EXE"})

Memory Trend:
$graph

Summary:
  Start:  $startMem MB (WS) / $($samples[0].PrivateMB) MB (private)
  End:    $($end.WorkingMB) MB (WS) / $($end.PrivateMB) MB (private)
  Delta:  $delta MB (WS) / ${privDelta} MB (private)
  Peak:   $($peak.WorkingMB) MB at $($peak.Elapsed)s
  Min:    $($min.WorkingMB) MB at $($min.Elapsed)s
  Avg:    $avg MB
  Handles: $($samples[0].Handles) -> $($end.Handles)
  Threads: $($samples[0].Threads) -> $($end.Threads)

"@

$report | Out-File $reportFile -Encoding UTF8
Write-Host $report

# Leak verdict
if ($delta -gt 50) {
    Write-Host "⚠️  WARNING: Working set grew $delta MB — possible leak!" -ForegroundColor Red
}
elseif ($delta -gt 10) {
    Write-Host "⚡ Moderate growth: +$delta MB — monitor over longer duration" -ForegroundColor Yellow
}
else {
    Write-Host "✅ Stable: +$delta MB over ${DurationSeconds}s — no leak detected" -ForegroundColor Green
}

Write-Host "`nData saved to: $runDir" -ForegroundColor Gray

# --- Helpers ---
function Stop-App {
    Get-Process "RuneshapePriceChecker" -ErrorAction SilentlyContinue | Stop-Process -Force
}

function Build-TextGraph($samples, $width) {
    if ($samples.Count -lt 2) { return "Not enough data" }
    $vals = $samples | ForEach-Object { $_.WorkingMB }
    $min = ($vals | Measure-Object -Minimum).Minimum
    $max = ($vals | Measure-Object -Maximum).Maximum
    $range = if ($max -eq $min) { 1 } else { $max - $min }

    $lines = @()
    $step = [math]::Max(1, [math]::Floor($samples.Count / $width))
    for ($x = 0; $x -lt $samples.Count; $x += $step) {
        $v = $samples[$x].WorkingMB
        $h = [math]::Max(0, [math]::Round(($v - $min) / $range * ($width - 1)))
        $line = (" " * $h) + "●"
        $lines += "$($samples[$x].Elapsed)s".PadLeft(5) + " $line"
    }
    $lines += "      $min MB" + (" " * ($width - "$min MB".Length)) + "$max MB"
    return ($lines -join "`n")
}
