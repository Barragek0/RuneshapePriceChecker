param(
    [string]$SourceBinaryPath,
    [string]$TestName
)

$ErrorActionPreference = "Stop"
$sandbox = "$env:TEMP\rstest-$TestName"
$zip = "C:\1.Path stuff\RuneshapePriceChecker\bin\Release\RuneshapePriceChecker.zip"

Write-Host "=== $TestName ==="
Write-Host "Source: $SourceBinaryPath"

# Clean sandbox
Get-Process -Name "RuneshapePriceChecker", "Update" -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item $sandbox -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item $SourceBinaryPath $sandbox -Recurse

# Create config
$configDir = "$sandbox\config"
New-Item -ItemType Directory $configDir -Force | Out-Null
$config = @{
    App    = @{ LogLevel = "Debug"; SuppressAlreadyRunningWarning = $true; AutoApplyUpdate = $true }
    Update = @{ AutoUpdate = $true; GitHubApiBaseUrl = "http://localhost:8099/api"; GitHubRepoOwner = "Barragek0"; GitHubRepoName = "RuneshapePriceChecker" }
    Window = @{ InitialSetupComplete = $true }
} | ConvertTo-Json -Depth 3
Set-Content -Path "$configDir\appsettings.json" -Value $config -Encoding UTF8

# Pre-check
$preExe = Get-Item "$sandbox\RuneshapePriceChecker.exe"
$preVersion = $preExe.VersionInfo.ProductVersion
$preSize = [math]::Round($preExe.Length / 1MB, 1)
Write-Host "PRE:  $preVersion | ${preSize}MB"

# Run
$proc = Start-Process -FilePath "$sandbox\RuneshapePriceChecker.exe" -PassThru
$timeout = 30
while ($timeout -gt 0 -and -not $proc.HasExited) {
    Start-Sleep -Seconds 1
    $timeout--
}
if (-not $proc.HasExited) {
    Write-Host "TIMEOUT - killing"
    $proc.Kill()
}

# Post-check
Start-Sleep -Seconds 2
$postExe = Get-Item "$sandbox\RuneshapePriceChecker.exe" -ErrorAction SilentlyContinue
if ($postExe) {
    $postVersion = $postExe.VersionInfo.ProductVersion
    $postSize = [math]::Round($postExe.Length / 1MB, 1)
    Write-Host "POST: $postVersion | ${postSize}MB"
    if ($postVersion -ne $preVersion) { Write-Host "UPDATE: SUCCESS ($preVersion -> $postVersion)" }
    else { Write-Host "UPDATE: NOT APPLIED" }
}
else {
    Write-Host "POST: exe not found!"
}

# Log analysis
Write-Host "--- LOG ANALYSIS ---"
$allLogs = @(Get-ChildItem "$sandbox" -Recurse -Filter "*.log" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime)
if ($allLogs.Count -eq 0) {
    Write-Host "  NO LOGS FOUND in sandbox"
    # Check extraction temp dirs
    $extracted = Get-ChildItem "$env:TEMP\.net" -Directory -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 3
    foreach ($d in $extracted) {
        $found = Get-ChildItem $d.FullName -Recurse -Filter "*.log" -ErrorAction SilentlyContinue
        if ($found) { Write-Host "  Found logs in extraction dir: $($d.FullName)"; $allLogs += $found }
    }
}
foreach ($log in $allLogs) {
    $content = Get-Content $log.FullName -Raw
    Write-Host "  Log: $($log.Name) ($([math]::Round($log.Length/1KB,1))KB)"
    
    # Check for the key patterns
    $noUrl = $content -match "No download URL"
    $already = $content -match "Already up to date"
    $updateAvail = $content -match "Update available"
    $launching = $content -match "Launching updater"
    $psUpdate = $content -match "PowerShell update script"
    $hideOverlay = $content -match "no update pending"
    
    Write-Host "    No-download-URL warning: $(if ($noUrl) {'FAIL'} else {'OK'})"
    Write-Host "    Already up to date: $(if ($already) {'found'} else {'not found'})"
    Write-Host "    Update available: $(if ($updateAvail) {'found'} else {'not found'})"
    Write-Host "    Launching updater: $(if ($launching) {'found'} else {'not found'})"
    Write-Host "    PowerShell update: $(if ($psUpdate) {'found'} else {'not found'})"
    Write-Host "    Fix (hide overlay): $(if ($hideOverlay) {'found'} else {'not found'})"
    
    # Show update-related lines
    $lines = $content -split "`n" | Where-Object { $_ -match "version|No download|Download complete|Launching updater|Already up to|Update available|no update pending|HideUpdate" }
    if ($lines) {
        Write-Host "    Key lines:"
        $lines | ForEach-Object { Write-Host "      $_" }
    }
}

Write-Host "--- END $TestName ---"
Write-Host ""
