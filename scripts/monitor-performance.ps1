<#
.SYNOPSIS
    Compares RuneshapePriceChecker performance (v1.0.0 vs latest) across
    stationary, scrolling, and idle scenarios.

.DESCRIPTION
    Launches and kills the app per scenario, measures CPU avg/max + memory avg/max,
    and reports per-scenario deltas. Waits automatically for PoE2 foreground state.

.EXAMPLE
    ./scripts/monitor-performance.ps1
    ./scripts/monitor-performance.ps1 -SkipBaseline
    ./scripts/monitor-performance.ps1 -DurationSeconds 15 -Scenarios "stationary","idle"
#>
param(
    [int]$DurationSeconds = 10,
    [string]$BaselinePath = "$env:LOCALAPPDATA\RuneshapePriceChecker\RuneshapePriceChecker.exe",
    [switch]$SkipBaseline,
    [switch]$DownloadBaseline,
    [string]$BaselineVersion = "1.0.0",
    [string]$BaselineDownloadUrl = "https://github.com/Barragek0/RuneshapePriceChecker/releases/download/1.0.0/RuneshapePriceChecker.zip",
    [string[]]$Scenarios = @("stationary", "scrolling", "idle"),
    [string]$PerfConfigPath = "",
    [int]$WarmupSeconds = 5,
    [string[]]$OcrBackends = @("tesseract", "windows")
)

$ErrorActionPreference = "Continue"
$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { "C:\1.Path stuff\RuneshapePriceChecker\scripts" }
$projectRoot = [System.IO.Path]::GetFullPath("$scriptDir\..")
if (-not $PerfConfigPath) { $PerfConfigPath = "$scriptDir\perf-test-config.json" }
$cores = [Environment]::ProcessorCount

$scenarioLabels = @{
    stationary = "Stationary (league panel open, don't scroll)"
    scrolling  = "Scrolling (league panel open, scroll constantly)"
    idle       = "Idle (PoE2 not foreground, OCR paused)"
}

# ---- PoE2 foreground detection ----
Add-Type @'
using System; using System.Runtime.InteropServices; using System.Text;
public class Fg { [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder t, int c); }
'@
function Test-Poe2Foreground {
    $h = [Fg]::GetForegroundWindow()
    if ($h -eq [IntPtr]::Zero) { return $false }
    $sb = New-Object Text.StringBuilder 256
    [Fg]::GetWindowText($h, $sb, 256) | Out-Null
    return $sb.ToString() -eq "Path of Exile 2"
}
function Wait-Poe2Foreground {
    Write-Host "Waiting for PoE2 foreground..." -ForegroundColor Yellow
    while (-not (Test-Poe2Foreground)) { Start-Sleep -Milliseconds 500 }
    Write-Host "PoE2 is foreground." -ForegroundColor Green
}
function Wait-Poe2NotForeground {
    Write-Host "Waiting for PoE2 NOT foreground..." -ForegroundColor Yellow
    while (Test-Poe2Foreground) { Start-Sleep -Milliseconds 500 }
    Write-Host "PoE2 is not foreground." -ForegroundColor Green
}

# ---- Core measurement ----
function Measure-Run($exe, $label) {
    Write-Host "`n=== $label ===" -ForegroundColor Cyan
    $p = Start-Process -FilePath $exe -PassThru -WindowStyle Minimized
    Start-Sleep $WarmupSeconds
    Write-Host "Measuring ${DurationSeconds}s..." -ForegroundColor Yellow
    $cpuSamples = @()
    $memSamples = @()
    for ($i = 0; $i -lt $DurationSeconds; $i++) {
        $proc = Get-Process -Id $p.Id -ErrorAction SilentlyContinue
        if ($proc) {
            $cpuSamples += $proc.CPU
            $memSamples += [math]::Round($proc.WorkingSet64 / 1MB, 1)
        } else { Write-Host "Process died at ${i}s"; break }
        Start-Sleep 1
    }
    if (-not $p.HasExited) { $p.Kill(); $p.WaitForExit(5000) | Out-Null }
    Start-Sleep 1
    if ($cpuSamples.Count -le 1) {
        return @{ CpuAvg = 0; CpuMax = 0; MemAvg = 0; MemMax = 0 }
    }
    $cpuDeltas = for ($i = 1; $i -lt $cpuSamples.Count; $i++) {
        $cpuSamples[$i] - $cpuSamples[$i - 1]
    }
    return @{
        CpuAvg = [math]::Round(($cpuDeltas | Measure-Object -Average).Average / $cores * 100, 2)
        CpuMax = [math]::Round(($cpuDeltas | Measure-Object -Maximum).Maximum / $cores * 100, 2)
        MemAvg = [math]::Round(($memSamples | Measure-Object -Average).Average, 0)
        MemMax = [math]::Round(($memSamples | Measure-Object -Maximum).Maximum, 0)
    }
}

# ---- Build latest ----
function Build-Latest {
    Write-Host "`n--- Building Latest ---" -ForegroundColor Cyan
    $publishExe = "$projectRoot\obj\Release\publish\RuneshapePriceChecker.exe"
    if (-not (Test-Path $publishExe)) {
        Write-Host "Publishing (this may take a minute)..." -ForegroundColor Yellow
        dotnet publish "$projectRoot\RuneshapePriceChecker.csproj" -c Release --nologo 2>&1 | Select-Object -Last 3
    } else {
        Write-Host "Using existing publish output." -ForegroundColor DarkGray
    }
    if (-not (Test-Path $publishExe)) { Write-Host "ERROR: v1.0.1 exe not found at $publishExe"; exit 1 }
    return $publishExe
}

# ---- Place perf config ----
function Place-Config($exeDir, $ocrBackend) {
    if (-not (Test-Path $PerfConfigPath)) { return }
    $cfgDir = Join-Path $exeDir "config"
    New-Item -ItemType Directory -Force $cfgDir | Out-Null
    $cfg = Get-Content $PerfConfigPath -Raw | ConvertFrom-Json
    $cfg.OCR | Add-Member -MemberType NoteProperty -Name "OcrBackend" -Value $ocrBackend -Force
    $cfg | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $cfgDir "appsettings.json") -Force
    Write-Host "Config OCR backend: $ocrBackend" -ForegroundColor DarkGray
}

# ==================================================================
# Main
# ==================================================================
$allResults = @{}

# Baseline
if (-not $SkipBaseline) {
    if ($DownloadBaseline) {
        $zip = "$env:TEMP\rpc-baseline-$BaselineVersion.zip"
        $extractDir = "$env:TEMP\rpc-baseline-$BaselineVersion"
        if (-not (Test-Path "$extractDir\RuneshapePriceChecker.exe")) {
            Write-Host "Downloading v$BaselineVersion..." -ForegroundColor Yellow
            Invoke-WebRequest -Uri $BaselineDownloadUrl -OutFile $zip -UseBasicParsing
            Expand-Archive -Path $zip -DestinationPath $extractDir -Force
        }
        $BaselinePath = "$extractDir\RuneshapePriceChecker.exe"
    }
    if (Test-Path $BaselinePath) {
        Place-Config (Split-Path $BaselinePath -Parent) "tesseract"
        foreach ($s in $Scenarios) {
            if ($s -eq "idle") { Wait-Poe2NotForeground } else { Wait-Poe2Foreground }
            Start-Sleep 1
            $allResults["baseline-$s"] = Measure-Run $BaselinePath "v$BaselineVersion (tesseract) - $($scenarioLabels[$s])"
            Start-Sleep 2
        }
    } else { Write-Host "Baseline not found: $BaselinePath" -ForegroundColor DarkGray }
}

# Latest — run once per OCR backend
$latestExe = Build-Latest
foreach ($backend in $OcrBackends) {
    Place-Config (Split-Path $latestExe -Parent) $backend
    $label = "v1.0.1 ($backend)"
    foreach ($s in $Scenarios) {
        if ($s -eq "idle") { Wait-Poe2NotForeground } else { Wait-Poe2Foreground }
        Start-Sleep 1
        $allResults["latest-$backend-$s"] = Measure-Run $latestExe "$label - $($scenarioLabels[$s])"
        Start-Sleep 2
    }
}

# ---- Report ----
Write-Host "`n========================================" -ForegroundColor Yellow
Write-Host "  v$BaselineVersion vs v1.0.1 (tesseract) vs v1.0.1 (windows) | ${DurationSeconds}s per scenario" -ForegroundColor Yellow
Write-Host "========================================" -ForegroundColor Yellow

foreach ($s in $Scenarios) {
    $base = $allResults["baseline-$s"]
    $tess = $allResults["latest-tesseract-$s"]
    $win  = $allResults["latest-windows-$s"]
    if (-not $tess -or -not $win) { continue }

    Write-Host ""
    Write-Host "### $($scenarioLabels[$s])" -ForegroundColor Green

    if ($base) {
        $dTess = [math]::Round($tess.CpuAvg - $base.CpuAvg, 2)
        $dWin  = [math]::Round($win.CpuAvg - $base.CpuAvg, 2)
        $pctTess = if ($base.CpuAvg -ne 0) { [math]::Round($dTess / $base.CpuAvg * 100, 1) } else { "N/A" }
        $pctWin  = if ($base.CpuAvg -ne 0) { [math]::Round($dWin / $base.CpuAvg * 100, 1) } else { "N/A" }
        Write-Host "  v$BaselineVersion          CPU avg=$($base.CpuAvg)% max=$($base.CpuMax)%  Mem avg=$($base.MemAvg)MB max=$($base.MemMax)MB"
        Write-Host "  Delta tess vs base        CPU ${dTess}pp (${pctTess}%)  Mem $($tess.MemAvg - $base.MemAvg)MB"
        Write-Host "  Delta win  vs base        CPU ${dWin}pp (${pctWin}%)  Mem $($win.MemAvg - $base.MemAvg)MB"
    }

    $winVsTess = if ($tess.CpuAvg -ne 0) { [math]::Round(($win.CpuAvg - $tess.CpuAvg) / $tess.CpuAvg * 100, 1) } else { "N/A" }
    Write-Host "  v1.0.1 (tesseract)        CPU avg=$($tess.CpuAvg)% max=$($tess.CpuMax)%  Mem avg=$($tess.MemAvg)MB max=$($tess.MemMax)MB"
    Write-Host "  v1.0.1 (windows)          CPU avg=$($win.CpuAvg)% max=$($win.CpuMax)%  Mem avg=$($win.MemAvg)MB max=$($win.MemMax)MB"
    Write-Host "  Delta win  vs tess        CPU ${winVsTess}%"
}
Write-Host "`nDone." -ForegroundColor Green
