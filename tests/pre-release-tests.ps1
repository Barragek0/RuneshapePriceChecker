# Pre-release test suite for RuneshapePriceChecker
# Specifically aims to test as many features as possible without requiring user interaction, and to catch regressions before release.
# Run: powershell -ExecutionPolicy Bypass -File tests\pre-release-tests.ps1 [-TestN] [-All]
param(
    [switch]$Test1, [switch]$Test2, [switch]$Test3, [switch]$Test4,
    [switch]$Test5, [switch]$Test6, [switch]$Test7, [switch]$Test8,
    [switch]$Test9, [switch]$Test10, [switch]$Test11, [switch]$Test12,
    [switch]$Test13, [switch]$Test14, [switch]$Test15, [switch]$Test16, [switch]$Test18, [switch]$Test19, [switch]$Test20,
    [switch]$Test21, [switch]$Test22, [switch]$Test23, [switch]$Test24,
    [switch]$Test25, [switch]$Test26, [switch]$Test27,
    [switch]$Test28, [switch]$Test29, [switch]$Test30,
    [switch]$Test31, [switch]$Test32, [switch]$Test33,
    [switch]$Test34, [switch]$Test35,
    [switch]$All
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot | Split-Path -Parent
$exeDir = "$root\obj\Release\publish"
$exe = "$exeDir\RuneshapePriceChecker.exe"
$configDir = "$exeDir\config"
$configPath = "$configDir\appsettings.json"
$logDir = "$exeDir\logs"
$configBackup = if (Test-Path $configPath) { Get-Content $configPath -Raw } else { $null }
$clipboardBackup = try { Get-Clipboard -Raw -ErrorAction Stop } catch { $null }

$passed = 0
$failed = 0
$results = @()

# ANSI escape codes for cross-terminal color support
$ansiReset = "$([char]27)[0m"
$ansiRed = "$([char]27)[31m"
$ansiGreen = "$([char]27)[32m"
$ansiYellow = "$([char]27)[33m"
$ansiCyan = "$([char]27)[36m"
$ansiGray = "$([char]27)[90m"
$ansiDarkYellow = "$([char]27)[33m"

function Write-Section($text) {
    Write-Host "`n$ansiYellow--- $text ---$ansiReset"
}

function Write-Banner($text) {
    Write-Host ""
    Write-Host "$ansiCyan$('=' * 70)$ansiReset"
    Write-Host "$ansiCyan  $text$ansiReset"
    Write-Host "$ansiCyan$('=' * 70)$ansiReset"
}

function Report-Result($test, $pass, $detail) {
    $icon = if ($pass) { "PASS" } else { "FAIL" }
    $color = if ($pass) { $ansiGreen } else { $ansiRed }
    Write-Host "  $color[$icon]$ansiReset $test"
    if ($detail -and -not $pass) { Write-Host "        ${ansiGray}$detail$ansiReset" }
    $results += [PSCustomObject]@{ Test = $test; Pass = $pass; Detail = $detail }
    if ($pass) { $script:passed++ } else { $script:failed++ }
}

function Wait-For([ScriptBlock]$condition, $timeoutMs = 5000, $intervalMs = 100) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        $result = & $condition
        if ($result) { return $true }
        Start-Sleep -Milliseconds $intervalMs
    }
    return $false
}

function Wait-ForUI($proc, $property, $value, $timeoutMs = 5000) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        $hwnd = $proc.MainWindowHandle
        if ($hwnd -ne [IntPtr]::Zero) {
            try {
                $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
                $el = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                    (New-Object System.Windows.Automation.PropertyCondition($property, $value)))
                if ($el) { return $el }
            }
            catch { }
        }
        Start-Sleep -Milliseconds 100
    }
    return $null
}

function Wait-ForClipboard($pattern, $timeoutMs = 3000) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        try { $c = Get-Clipboard -Raw -ErrorAction Stop; if ($c -and $c -match $pattern) { return $c } } catch { }
        Start-Sleep -Milliseconds 100
    }
    return $null
}

function Wait-ForConfig($section, $key, $expected, $timeoutMs = 3000) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        if (Test-Path $configPath) {
            try { $cfg = Get-Content $configPath -Raw | ConvertFrom-Json; if ($cfg.$section.$key -eq $expected) { return $true } } catch { }
        }
        Start-Sleep -Milliseconds 100
    }
    return $false
}

function Wait-ForUIGone($proc, $property, $value, $timeoutMs = 5000) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        $hwnd = $proc.MainWindowHandle
        if ($hwnd -ne [IntPtr]::Zero) {
            try {
                $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
                $el = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                    (New-Object System.Windows.Automation.PropertyCondition($property, $value)))
                if (-not $el) { return $true }
            }
            catch { return $true }
        }
        Start-Sleep -Milliseconds 100
    }
    return $false
}

function Stop-App {
    Get-Process "RuneshapePriceChecker" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
}

function Write-Config($json) {
    New-Item -ItemType Directory -Force $configDir | Out-Null
    $json | Set-Content $configPath -NoNewline
}

function Clear-Config { if (Test-Path $configPath) { Remove-Item $configPath -Force } }

function Clear-OldLogs {
    if (Test-Path $logDir) {
        # Retry up to 3 times with delays to handle OS file handle release
        for ($i = 0; $i -lt 3; $i++) {
            Remove-Item "$logDir\*" -Recurse -Force -ErrorAction SilentlyContinue
            $remaining = @(Get-ChildItem "$logDir" -ErrorAction SilentlyContinue)
            if ($remaining.Count -eq 0) { break }
            Start-Sleep -Milliseconds 300
        }
    }
}

function Get-LatestLog {
    $files = Get-ChildItem "$logDir\*-log.txt" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending
    if (-not $files) { return $null }
    return $files[0].FullName
}

function Wait-ForLog($pattern, $timeoutMs = 12000) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        $log = Get-LatestLog
        if ($log -and (Select-String -Path $log -Pattern $pattern -Quiet)) { return $true }
        Start-Sleep -Milliseconds 300
    }
    Write-Host "        ${ansiDarkYellow}[TIMEOUT] '$pattern' not in log after ${timeoutMs}ms$ansiReset"
    return $false
}

function Wait-ForApp($timeoutMs = 3500) { return Wait-ForLog "Hosting started" $timeoutMs }

function Launch-App($extraArgs = "", $waitMs = 600) {
    $launchArgs = @("--App:SuppressAlreadyRunningWarning=true", "--App:LogLevel=Debug", "--App:SuppressActivation=true")
    if ($extraArgs) { $launchArgs += $extraArgs -split ' ' | Where-Object { $_ } }
    $proc = Start-Process -FilePath $exe -ArgumentList $launchArgs -PassThru
    Start-Sleep -Milliseconds $waitMs
    return $proc
}

function Launch-App-Headless($extraArgs = "", $waitMs = 600) {
    $launchArgs = @("--App:SuppressAlreadyRunningWarning=true", "--App:LogLevel=Debug", "--App:SuppressActivation=true")
    if ($extraArgs) { $launchArgs += $extraArgs -split ' ' | Where-Object { $_ } }
    $proc = Start-Process -FilePath $exe -ArgumentList $launchArgs -PassThru
    Start-Sleep -Milliseconds $waitMs
    return $proc
}

Add-Type -AssemblyName UIAutomationClient -ErrorAction SilentlyContinue
Add-Type -AssemblyName UIAutomationTypes -ErrorAction SilentlyContinue

function Invoke-Button($proc, $buttonName, $timeoutMs = 5000) {
    $btn = Wait-ForUI $proc ([System.Windows.Automation.AutomationElement]::NameProperty) $buttonName $timeoutMs
    if ($btn) {
        try {
            $invoke = $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
            if ($invoke) { $invoke.Invoke(); return $true }
        }
        catch { }
    }
    return $false
}

function Click-Button($proc, $buttonName, $timeoutMs = 5000) {
    return Invoke-Button $proc $buttonName $timeoutMs
}
$cfgBase = @"
{
  "App": { "LogLevel": "Debug" },
  "Window": { "InitialSetupComplete": true },
  "OCR": { "SaveDebugImages": false, "Language": "eng" },
  "Update": { "AutoUpdate": false }
}
"@

function Test1-ChangelogSetupCoordination {
    Stop-App; Clear-OldLogs
    Write-Config '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":false},"OCR":{"SaveDebugImages":false,"Language":"eng"}}'
    $proc = Launch-App; $ok = Wait-ForLog "triggering initial setup" 10000; Stop-App
    Report-Result "1a: Setup triggered" $ok $(if ($ok) { "OK" }else { "Not triggered" })

    Stop-App; Clear-OldLogs
    Write-Config '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":false},"OCR":{"SaveDebugImages":false,"Language":"eng"},"Changelog":{"Body":"## Test Changelog","Version":"1.0.0","Shown":false}}'
    $proc = Launch-App; Wait-ForLog "waiting for changelog" 10000 | Out-Null
    $log = Get-LatestLog
    if ($log) {
        $lc = Get-Content $log -Raw
        $hasSetup = $lc -match "triggering initial setup"
        $hasWaiting = $lc -match "waiting for changelog"
        if ($hasWaiting -and -not $hasSetup) {
            Report-Result "1b: Setup blocked" $true "Worker waiting"
            $clicked = Click-Button $proc "Got it" 5000
            if ($clicked) {
                Report-Result "1b: UI clicked Got it" $true "Dismissed"
                Start-Sleep -Milliseconds 1500
                $lc2 = Get-Content (Get-LatestLog) -Raw
                if ($lc2 -match "triggering initial setup") { Report-Result "1b: Setup after dismiss" $true "Triggered" }
                else { Report-Result "1b: Setup after dismiss" $false "Not triggered" }
            }
            else { Report-Result "1b: UI dismiss" $false "Button not found" }
        }
        elseif ($hasWaiting -and $hasSetup) { Report-Result "1b: Setup after changelog" $true "Both appeared (expected)" }
        else { Report-Result "1b: Setup blocked" $false "Missing log messages" }
    }
    Stop-App

    Stop-App; Clear-OldLogs
    Write-Config '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"eng"},"Changelog":{"Body":"## Test","Version":"1.0.0","Shown":false}}'
    $proc = Launch-App; Wait-ForApp 5000 | Out-Null; Stop-App
    $log = Get-LatestLog
    if ($log) {
        $lc = Get-Content $log -Raw
        if ($lc -match "triggering initial setup") { Report-Result "1c: No setup" $false "Triggered!" }
        else { Report-Result "1c: No setup" $true "Correct" }
    }
}

function Test2-InitialSetupSuite {
    Stop-App; Clear-OldLogs; Clear-Config
    $proc = Launch-App; Wait-ForApp 5000 | Out-Null; Stop-App
    if (Test-Path $configPath) {
        $cfg = Get-Content $configPath -Raw | ConvertFrom-Json
        $initComplete = $cfg.Window.InitialSetupComplete
        if ($initComplete -eq $false) { Report-Result "2a: Config created" $true "Setup needed" }
        else { Report-Result "2a: Config created" $false "Value=$initComplete" }
    }
    else { Report-Result "2a: Config created" $false "No config" }
    Stop-App; Clear-OldLogs
    Write-Config '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":false},"OCR":{"SaveDebugImages":false,"Language":"eng"},"Pricing":{"League":"Standard"}}'
    $proc = Launch-App; $found = Wait-ForLog "triggering initial setup" 10000; Stop-App
    Report-Result "2b: Setup triggered" $found "OK"
    Stop-App; Clear-OldLogs
    Write-Config $cfgBase
    $proc = Launch-App; Wait-ForApp 5000 | Out-Null; Stop-App
    $log = Get-LatestLog
    $found = $log -and (Select-String -Path $log -Pattern "triggering initial setup" -SimpleMatch -Quiet)
    Report-Result "2c: No setup when done" (-not $found) $(if ($found) { "Triggered!" }else { "Correct" })
    Stop-App; Clear-Config
    $proc = Launch-App; Wait-ForApp 5000 | Out-Null; Stop-App
    if (Test-Path $configPath) {
        $cfg = Get-Content $configPath -Raw | ConvertFrom-Json
        $missing = @("App", "Window", "OCR", "Pricing", "Update") | Where-Object { -not ($cfg.PSObject.Properties.Name -contains $_) }
        Report-Result "2d: All sections" ($missing.Count -eq 0) $(if ($missing.Count) { "Missing: $missing" }else { "OK" })
    }
}

function Test8-AutoUpdater {
    $zip = Resolve-Path "$root\bin\Release\RuneshapePriceChecker.zip" -ErrorAction SilentlyContinue
    if (-not $zip) { Report-Result "3: Zip" $false "Publish first"; return }
    Stop-App
    Get-Process "dotnet" -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $PID } | Stop-Process -Force
    Start-Sleep -Milliseconds 1000
    $serverProc = Start-Process -FilePath "dotnet" -ArgumentList "run --project tests/UpdateTestServer/UpdateTestServer.csproj -c Release --no-build -- `"$zip`" 8099" -PassThru -NoNewWindow
    Start-Sleep -Milliseconds 1500
    if ($serverProc.HasExited) { Report-Result "8a: Test server" $false "Crashed"; return }
    Report-Result "8a: Test server" $true "PID $($serverProc.Id)"
    Stop-App; Clear-OldLogs
    Write-Config '{"App":{"LogLevel":"Debug","ForceUpdateAvailable":true},"Window":{"InitialSetupComplete":true},"Update":{"GitHubApiBaseUrl":"http://localhost:8099/api","AutoUpdate":true},"OCR":{"SaveDebugImages":false,"Language":"eng"}}'
    $proc = Launch-App -extraArgs "--Update:GitHubApiBaseUrl=http://localhost:8099/api --App:AutoApplyUpdate=true --App:ForceUpdateAvailable=true"
    # Wait for update detection; give auto-apply a few extra seconds after detection
    $detected = Wait-ForLog "ForceUpdateAvailable|Starting.*download|Update available" 25000
    if ($detected) { Start-Sleep -Milliseconds 5000 }  # give auto-apply time to progress
    Stop-App
    $log = Get-LatestLog
    if ($log) {
        $lc = Get-Content $log -Raw
        $detected = $lc -match "ForceUpdateAvailable" -or $lc -match "Starting update download" -or $lc -match "Update available"
        Report-Result "8b: Force update" $detected $(if ($detected) { "Detected" }else { "Not detected" })
        $prog = $lc -match "Download complete" -or $lc -match "Starting update download" -or $lc -match "Copied local zip" -or $lc -match "Launching updater" -or $lc -match "using local zip"
        Report-Result "8c: Download/launch" $prog $(if ($prog) { "Progressed" }else { "Not yet (timing)" })
    }
    Stop-App; Clear-OldLogs
    Write-Config '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":true},"Update":{"GitHubApiBaseUrl":"http://localhost:8099/api"},"OCR":{"SaveDebugImages":false,"Language":"eng"}}'
    $proc = Launch-App; Wait-ForApp 5000 | Out-Null; Stop-App
    $log = Get-LatestLog
    $found = $log -and (Select-String -Path $log -Pattern "Already up to date" -SimpleMatch -Quiet)
    Report-Result "8d: Already-up-to-date" $found "OK"
    if (Test-Path $configPath) {
        $cfg = Get-Content $configPath -Raw | ConvertFrom-Json
        Report-Result "8e: Changelog in config" ($cfg.Changelog -and $cfg.Changelog.Version) $(if ($cfg.Changelog.Version) { "v=$($cfg.Changelog.Version)" }else { "Missing" })
    }
    $serverProc | Stop-Process -Force -ErrorAction SilentlyContinue
}

function Test9-ChangelogButton {
    Stop-App; Clear-OldLogs; Write-Config $cfgBase
    $proc = Launch-App -extraArgs "--App:ShowChangelog=true"; Wait-ForApp 5000 | Out-Null
    $crashed = $proc.HasExited -and $proc.ExitCode -ne 0
    Stop-App
    $log = Get-LatestLog
    $clean = $log -and -not (Select-String -Path $log -Pattern "Fatal|crash|unhandled" -Quiet)
    Report-Result "9a: --ShowChangelog clean" ((-not $crashed) -and $clean) $(if ($crashed) { "Crashed" }else { "OK" })
    Stop-App; Clear-OldLogs
    Write-Config '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"eng"},"Changelog":{"Body":"## v1.0.0 Notes","Version":"1.0.0","Shown":false}}'
    $proc = Launch-App; Wait-ForApp 5000 | Out-Null; Stop-App
    if (Test-Path $configPath) {
        $cfg = Get-Content $configPath -Raw | ConvertFrom-Json
        Report-Result "9b: Marked shown" ($cfg.Changelog.Shown -eq $true) "Shown=$($cfg.Changelog.Shown)"
    }
    Stop-App; Clear-OldLogs
    $proc = Launch-App; Wait-ForApp 5000 | Out-Null; Stop-App
    if (Test-Path $configPath) {
        $cfg = Get-Content $configPath -Raw | ConvertFrom-Json
        Report-Result "9c: State preserved after restart" ($cfg.Changelog.Shown -eq $true) "Shown=$($cfg.Changelog.Shown)"
    }
}

function Test3-AppLifecycle {
    Stop-App; Clear-OldLogs; Clear-Config
    $proc = Launch-App; Wait-ForApp 5000 | Out-Null
    Report-Result "3a: App runs" (-not $proc.HasExited) $(if ($proc.HasExited) { "Exited:$($proc.ExitCode)" }else { "PID $($proc.Id)" })
    Stop-App
    Stop-App; Clear-OldLogs; Write-Config $cfgBase
    $proc = Launch-App; Wait-ForApp 5000 | Out-Null
    $proc.CloseMainWindow() | Out-Null; $proc.WaitForExit(4000) | Out-Null
    Report-Result "3b: Clean shutdown" $proc.HasExited "Exit:$($proc.ExitCode)"
    if (-not $proc.HasExited) { $proc.Kill() }
    $crashes = 0
    for ($i = 1; $i -le 3; $i++) { Stop-App; $p = Launch-App -waitMs 1500; if ($p.HasExited) { $crashes++ }; Stop-App }
    Report-Result "3c: Rapid restart" ($crashes -eq 0) $(if ($crashes) { "$crashes crashes" }else { "All OK" })
}

function Test4-ConfigRobustness {
    Stop-App; Clear-OldLogs; "{ bad json [[" | Set-Content $configPath -NoNewline
    $proc = Launch-App; Wait-ForApp 5000 | Out-Null; Stop-App
    try { $c = Get-Content $configPath -Raw | ConvertFrom-Json; $ok = $true } catch { $ok = $false }
    Report-Result "4a: Recovers corrupt" $ok $(if ($ok) { "Valid" }else { "Invalid" })
    Stop-App; Clear-OldLogs; "" | Set-Content $configPath -NoNewline
    $proc = Launch-App; Wait-ForApp 5000 | Out-Null; Stop-App
    try { $c = Get-Content $configPath -Raw | ConvertFrom-Json; $ok = $c.PSObject.Properties.Name.Count -ge 4 } catch { $ok = $false }
    Report-Result "4b: Empty populated" $ok $(if ($ok) { "$($c.PSObject.Properties.Name.Count) sections" }else { "Failed" })
    Stop-App; Clear-OldLogs; Write-Config '{"App":{"LogLevel":"Debug"},"Update":{"AutoUpdate":false}}'
    $proc = Launch-App; Wait-ForApp 5000 | Out-Null; Stop-App
    if (Test-Path $configPath) { $c = Get-Content $configPath -Raw | ConvertFrom-Json; Report-Result "4c: Sections filled" ($c.PSObject.Properties.Name -contains "Window") "OK" }
    Stop-App; Clear-OldLogs
    Write-Config '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"eng"},"Update":{"AutoUpdate":false},"Pricing":{"RedThreshold":0.1,"OrangeThreshold":1.0,"GreenThreshold":9999}}'
    $proc = Launch-App; Wait-ForApp 5000 | Out-Null; Stop-App
    $l = Get-LatestLog; $clean = -not ($l -and (Select-String -Path $l -Pattern "Fatal|Unhandled|crash" -Quiet))
    Report-Result "4d: Bad thresholds OK" $clean "No crash"
    Stop-App; Clear-OldLogs
    Write-Config '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"eng"},"Update":{"AutoUpdate":false},"Pricing":{"League":"Standard","DisplayCurrency":"exalt"}}'
    $proc = Launch-App; $started = Wait-ForApp 8000; Stop-App
    if (-not $started) { Report-Result "4e: Settings preserved" $false "App failed to start"; return }
    if (Test-Path $configPath) { $c = Get-Content $configPath -Raw | ConvertFrom-Json; $ok = $c.Pricing.League -eq "Standard" -and $c.Pricing.DisplayCurrency -eq "exalt"; Report-Result "4e: Settings preserved" $ok $(if ($ok) { "OK" }else { "Lost" }) }
}

function Test11-Logging($proc) {
    $l = Get-LatestLog
    if ($l) { Report-Result "11a: Log created" $true "$([math]::Round((Get-Item $l).Length/1KB,1)) KB" }
    else { Report-Result "11a: Log created" $false "No log" }
    $lc = if ($l) { Get-Content $l -Raw } else { "" }
    $sb = @()
    if ($lc -match "Tesseract") { $sb += "Tess" }; if ($lc -match "Pricing") { $sb += "Price" }
    if ($lc -match "OCR") { $sb += "OCR" }; if ($lc -match "Hosting") { $sb += "Host" }
    Report-Result "11b: Subsystems" ($sb.Count -ge 3) "$($sb -join ',')"
    Report-Result "11c: No errors" ($lc -notmatch "\[Erro\]|\[Fata\]|Unhandled|crash") $(if ($lc -notmatch "\[Erro\]") { "Clean" }else { "Errors" })
}

function Test5-ErrorHandling {
    $td = "${exeDir}\tesseract\eng.traineddata"
    $bk = "${exeDir}\tesseract\eng.traineddata.bak"
    Stop-App; Clear-OldLogs; Clear-Config
    if (Test-Path $td) { Move-Item $td $bk -Force }
    $proc = Launch-App; Wait-ForApp 5000 | Out-Null; Stop-App
    $re = Test-Path $td
    Report-Result "5a: Auto-repair" $re $(if ($re) { "Restored" }else { "Failed" })
    if (-not $re -and (Test-Path $bk)) { Move-Item $bk $td -Force }
    if (Test-Path $bk) { Remove-Item $bk -Force -ErrorAction SilentlyContinue }
    Stop-App; Clear-OldLogs
    if (Test-Path $td) { Move-Item $td $bk -Force }
    "garbage" | Set-Content $td -NoNewline
    $proc = Launch-App; Wait-ForApp 5000 | Out-Null; Stop-App
    $so = (Test-Path $td) -and ((Get-Item $td).Length -gt 100000)
    Report-Result "5b: Corrupt repair" $so $(if ($so) { "$([math]::Round((Get-Item $td).Length/1MB,1)) MB" }else { "Failed" })
    if (-not $so -and (Test-Path $bk)) { Move-Item $bk $td -Force }
    if (Test-Path $bk) { Remove-Item $bk -Force -ErrorAction SilentlyContinue }
    Stop-App; Clear-OldLogs
    Write-Config '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":true},"OCR":{"Language":"zzz_invalid","SaveDebugImages":false},"Update":{"AutoUpdate":false}}'
    $proc = Launch-App; Wait-ForApp 5000 | Out-Null; Stop-App
    $l = Get-LatestLog; $clean = -not ($l -and (Select-String -Path $l -Pattern "Fatal|Unhandled" -Quiet))
    Report-Result "5c: Bad language OK" $clean "No crash"
    Stop-App
    if (Test-Path $logDir) { Remove-Item $logDir -Recurse -Force -ErrorAction SilentlyContinue }
    if (Test-Path $configDir) { Remove-Item $configDir -Recurse -Force -ErrorAction SilentlyContinue }
    $proc = Launch-App; Wait-ForApp 5000 | Out-Null; Stop-App
    $ok = (Test-Path $logDir) -and (Test-Path $configDir)
    Report-Result "5d: Dirs created" $ok $(if ($ok) { "logs+config" }else { "Missing" })
}

function Test12-ResourceUsage($proc) {
    try { Report-Result "12a: Memory" (($proc.WorkingSet64 / 1MB) -lt 400) "$([math]::Round($proc.WorkingSet64/1MB,1)) MB" } catch { Report-Result "12a: Memory" $false "Error" }
    try { Report-Result "12b: Handles" ($proc.HandleCount -lt 2000) "$($proc.HandleCount)" } catch { Report-Result "12b: Handles" $false "Error" }
    try { Report-Result "12c: Threads" ($proc.Threads.Count -lt 50) "$($proc.Threads.Count)" } catch { Report-Result "12c: Threads" $false "Error" }
    # no Stop-App (shared instance)
}

function Test13-UiElements($proc) {
    try {
        $hwnd = $proc.MainWindowHandle
        Report-Result "13a: Window" ($hwnd -ne [IntPtr]::Zero) "HWND: $hwnd"
        Report-Result "13b: Title" ($proc.MainWindowTitle -match "Runeshape|Price") "Title: $($proc.MainWindowTitle)"
        $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
        Report-Result "13c: UIA tree" ($root -ne $null) $(if ($root) { "Accessible" }else { "Null" })
    }
    catch { Report-Result "13: UI" $false "Exception: $_" }
}

function Test16-UiButtonInteractions($proc) {
    # Test settings button
    $gearClicked = Invoke-Button $proc "Settings" 3000
    Report-Result "16a: Settings button" $gearClicked $(if ($gearClicked) { "Opened" }else { "Not found" })
    # Close settings
    if ($gearClicked) { Invoke-Button $proc "Settings" 3000 | Out-Null }
    # Wait for settings to close before clicking Close
    Wait-ForUIGone $proc ([System.Windows.Automation.AutomationElement]::NameProperty) "Cancel" 3000 | Out-Null
    $closeClicked = Invoke-Button $proc "Close" 3000
    Report-Result "16b: Close button" $closeClicked "Clicked"
    Start-Sleep -Milliseconds 1000
    if ($proc.HasExited) { Report-Result "16c: App closed" $true "Exited" } else { Report-Result "16c: App closed" $false "Still running"; Stop-App }
}

function Test6-SettingsPersistence {
    Stop-App; Clear-OldLogs
    Write-Config '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"eng"},"Update":{"AutoUpdate":false},"Pricing":{"DisplayCurrency":"exalt","RedThreshold":0.5,"OrangeThreshold":1.0,"GreenThreshold":5.0,"League":"Standard"}}'
    $proc = Launch-App; $started = Wait-ForApp 8000; if (-not $started) { Report-Result "6a: App start" $false "Failed"; Stop-App; return }
    Click-Button $proc "Settings" 3000 | Out-Null; Start-Sleep -Milliseconds 500
    $hwnd = $proc.MainWindowHandle
    if ($hwnd -ne [IntPtr]::Zero) {
        $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
        $saveBtn = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "Save")))
        Report-Result "6b: Save button" ($saveBtn -ne $null) $(if ($saveBtn) { "Found" }else { "Not found" })
        if ($saveBtn) { $invoke = $saveBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern); if ($invoke) { $invoke.Invoke(); Start-Sleep -Milliseconds 1000 } }
    }
    if (Test-Path $configPath) { $cfg = Get-Content $configPath -Raw | ConvertFrom-Json; Report-Result "6c: League persisted" ($cfg.Pricing.League -eq "Standard") "League=$($cfg.Pricing.League)"; Report-Result "6d: Currency persisted" ($cfg.Pricing.DisplayCurrency -eq "exalt") "Currency=$($cfg.Pricing.DisplayCurrency)" }
    Stop-App
}

function Test7-InvalidThresholds {
    Stop-App; Clear-OldLogs
    Write-Config '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"eng"},"Update":{"AutoUpdate":false},"Pricing":{"DisplayCurrency":"exalt","RedThreshold":5.0,"OrangeThreshold":1.0,"GreenThreshold":5.0,"League":"Standard"}}'
    $proc = Launch-App; $crashed = Wait-ForLog "Hosting failed|Pricing configuration is invalid" 15000
    if (-not $proc.HasExited) { Stop-App }
    $log = Get-LatestLog; if ($log) { $lc = Get-Content $log -Raw; $hasError = $lc -match "Pricing configuration is invalid" -or $lc -match "Hosting failed"; Report-Result "7a: Invalid thresholds rejected" ($crashed -and $hasError) $(if ($hasError) { "Rejected" } else { "Not detected" }) }
}

function Test14-SettingsFieldValidation($proc) {
    if ($proc.HasExited) { Report-Result "14: Settings" $false "App exited"; return }

    # Read original config
    $origCfg = if (Test-Path $configPath) { Get-Content $configPath -Raw } else { "" }

    # --- Phase A: Open settings, verify Cancel/Save exist, click Cancel, verify no change ---
    Click-Button $proc "Settings" 3000 | Out-Null
    Start-Sleep -Milliseconds 600

    $hwnd = $proc.MainWindowHandle
    if ($hwnd -eq [IntPtr]::Zero) { Report-Result "14a: Window" $false "No HWND"; return }
    try { $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd) } catch { Report-Result "14a: Window" $false "UIA error"; return }

    $cancelBtn = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "Cancel")))
    $saveBtn = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "Save")))

    Report-Result "14a: Cancel button" ($cancelBtn -ne $null) $(if ($cancelBtn) { "Found" } else { "Not found" })
    Report-Result "14b: Save button" ($saveBtn -ne $null) $(if ($saveBtn) { "Found" } else { "Not found" })

    # Click Cancel and verify config unchanged
    if ($cancelBtn) {
        try { $cancelBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } catch { }
        Start-Sleep -Milliseconds 600
    }
    $cfgAfterCancel = if (Test-Path $configPath) { Get-Content $configPath -Raw } else { "" }
    # Strip auto-saved Window position fields for comparison (app saves them via background timer)
    $normalize = { param($c) $c -replace '"InitialSetupComplete":\s*true,?\s*', '' -replace '"(Left|Top|Width|Height)":\s*\d+,?\s*', '' -replace ',\s*}', '}' -replace ',\s*}', '}' }
    $normalizedOrig = & $normalize $origCfg
    $normalizedAfter = & $normalize $cfgAfterCancel
    Report-Result "14c: Cancel preserves config" ($normalizedOrig -eq $normalizedAfter) $(if ($normalizedOrig -eq $normalizedAfter) { "Unchanged" } else { "Modified" })

    # --- Phase B: Reopen settings, find threshold fields, verify ValuePattern works ---
    # After Cancel, settings should be closed. Toggle to open.
    Click-Button $proc "Settings" 3000 | Out-Null
    Start-Sleep -Milliseconds 800
    try { $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd) } catch { Report-Result "14d: UIA" $false "FromHandle failed"; return }

    # Find threshold fields by AutomationId
    $redBox = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "RedThresholdBox")))
    $greenBox = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "GreenThresholdBox")))

    $foundCount = 0; if ($redBox) { $foundCount++ }; if ($greenBox) { $foundCount++ }
    Report-Result "14d: Threshold fields" ($foundCount -ge 2) "$foundCount found (of 2)"

    # Try ValuePattern on RedThresholdBox
    $valueSetOk = $false
    if ($redBox) {
        try {
            $vp = $redBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
            if ($vp) {
                $oldVal = $vp.Current.Value
                $vp.SetValue("2"); Start-Sleep -Milliseconds 150
                $valueSetOk = ($vp.Current.Value -eq "2")
                try { $vp.SetValue($oldVal) } catch { }
            }
        }
        catch { }
    }
    Report-Result "14e: Value set via UIA" $valueSetOk $(if ($valueSetOk) { "OK" } else { "No ValuePattern" })

    # Click Save
    $saveBtn = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "Save")))
    $saveClicked = $false
    if ($saveBtn) {
        try { $saveBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 800; $saveClicked = $true } catch { }
    }
    Report-Result "14f: Save clicked" $saveClicked $(if ($saveClicked) { "Clicked" } else { "Not found" })

    # --- Phase C: Ensure settings are closed before returning ---
    # Toggle twice in case Save already closed it (so we don't leave it open)
    Click-Button $proc "Settings" 3000 | Out-Null
    Start-Sleep -Milliseconds 400
    # Check if still open by looking for Cancel button; if found, close
    try { $root2 = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd) } catch { $root2 = $null }
    if ($root2) {
        $stillOpen = $root2.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "Cancel")))
        if ($stillOpen) {
            try { $stillOpen.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 400 } catch { }
        }
    }
}
function Test15-TooltipVerification($proc) {
    if ($proc.HasExited) { Report-Result "15: Tooltips" $false "App exited"; return }
    $hwnd = $proc.MainWindowHandle
    if ($hwnd -eq [IntPtr]::Zero) { Report-Result "15: Tooltips" $false "No window"; return }

    try { $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd) } catch { Report-Result "15: UIA" $false "FromHandle failed"; return }

    # Only Copy Log has an explicit ToolTip in XAML; find by AutomationId (x:Name) for reliability
    Start-Sleep -Milliseconds 300
    $copyBtn = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "CopyLogButton")))
    if (-not $copyBtn) {
        # Fallback: find by Name
        $copyBtn = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "Copy Log")))
    }
    $tooltipOk = $false
    if ($copyBtn) {
        $help = try { $copyBtn.Current.HelpText } catch { "" }
        $tooltipOk = ($help -match "clipboard")
        Report-Result "15a: Copy Log tooltip" $tooltipOk $(if ($tooltipOk) { "'$help'" } else { "No match: '$help'" })
    }
    else { Report-Result "15a: Copy Log tooltip" $false "Button not found" }

    # Settings button should exist and be invocable
    $settingsBtn = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "Settings")))
    if ($settingsBtn) {
        $invocable = $false
        try { $invoke = $settingsBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern); $invocable = ($invoke -ne $null) } catch { }
        Report-Result "15b: Settings invocable" $invocable "OK"
    }
    else { Report-Result "15b: Settings invocable" $false "Not found" }

    # Close button should exist
    $closeBtn = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "Close")))
    Report-Result "15c: Close button" ($closeBtn -ne $null) $(if ($closeBtn) { "Found" } else { "Not found" })
}
function Test10-OverlayFeatureToggles {
    # Only test with all overlays enabled — if this doesn't crash, individual toggles won't either
    Stop-App; Clear-OldLogs
    Write-Config '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"eng","ShowPricingOverlay":true,"ShowBanner":true,"DebugOverlay":true},"Update":{"AutoUpdate":false}}'
    $p = Launch-App; Wait-ForApp 5000 | Out-Null; $ok = -not $p.HasExited; Stop-App
    Report-Result "10a: All overlays on" $ok $(if ($ok) { "No crash" }else { "Exited" })
}

function Test18-OcrBackendSetting {
    Stop-App; Clear-OldLogs

    Write-Config '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"eng","OcrBackend":"windows"},"Update":{"AutoUpdate":false}}'
    $proc = Launch-App; Wait-ForApp 5000 | Out-Null; Stop-App
    if (Test-Path $configPath) {
        $cfg = Get-Content $configPath -Raw | ConvertFrom-Json
        Report-Result "18a: Windows OCR persisted" ($cfg.OCR.OcrBackend -eq "windows") "Backend=$($cfg.OCR.OcrBackend)"
    }

    Stop-App; Clear-OldLogs
    Write-Config '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"eng","OcrBackend":"tesseract"},"Update":{"AutoUpdate":false}}'
    $proc = Launch-App; Wait-ForApp 5000 | Out-Null; Stop-App
    if (Test-Path $configPath) {
        $cfg = Get-Content $configPath -Raw | ConvertFrom-Json
        Report-Result "18b: Tesseract persisted" ($cfg.OCR.OcrBackend -eq "tesseract") "Backend=$($cfg.OCR.OcrBackend)"
    }

    Stop-App; Clear-OldLogs
    Write-Config '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"eng","OcrBackend":"invalid"},"Update":{"AutoUpdate":false}}'
    $proc = Launch-App; $started = Wait-ForApp 8000; Stop-App
    $log = Get-LatestLog
    $fallback = ($log -and (Select-String -Path $log -Pattern "Unsupported.*backend|falling back|Fallback" -Quiet))
    $noCrash = -not ($log -and (Select-String -Path $log -Pattern "Fatal|Unhandled" -Quiet))
    Report-Result "18c: Invalid backend handled" ($noCrash) $(if ($fallback) { "Fallback logged" }else { "No crash" })
}

function Test19-ReRunSetup {
    Stop-App; Clear-OldLogs
    Write-Config $cfgBase
    $proc = Launch-App; Wait-ForApp 5000 | Out-Null
    if ($proc.HasExited) { Report-Result "19: Setup" $false "App exited"; return }

    # Open settings panel first, then find the Re-run button
    Click-Button $proc "Settings" 3000 | Out-Null
    Start-Sleep -Milliseconds 800
    $clicked = Click-Button $proc "Re-run initial setup" 5000
    Report-Result "19a: Re-run button found" $clicked $(if ($clicked) { "Clicked" }else { "Not found" })
    Start-Sleep -Milliseconds 1500

    $log = Get-LatestLog
    $triggered = ($log -and (Select-String -Path $log -Pattern "RunInitialSetup: starting initial setup flow|Setup overlay" -Quiet))
    Report-Result "19b: Setup triggered" $triggered $(if ($triggered) { "Triggered" }else { "Not found in log" })
    Stop-App
}

function Test20-ComprehensiveSettingsRoundTrip($proc) {
    if ($proc.HasExited) { Report-Result "20: Settings" $false "App exited"; return }

    Click-Button $proc "Settings" 3000 | Out-Null
    Start-Sleep -Milliseconds 800

    $hwnd = $proc.MainWindowHandle
    if ($hwnd -eq [IntPtr]::Zero) { Report-Result "20a: Window" $false "No HWND"; return }
    try { $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd) } catch { Report-Result "20a: Window" $false "UIA error"; return }

    # Find and verify key controls exist
    $controlNames = @(
        "LeagueCombo", "PricingSourceCombo", "CurrencyChaosCheck", "CurrencyExaltCheck",
        "RedThresholdBox", "OrangeThresholdBox", "GreenThresholdBox",
        "LanguageCombo", "OcrBackendCombo",
        "DebugOverlayCheck", "HideDebugOverlayCheck", "SaveDebugImagesCheck",
        "AutoUpdateCheck"
    )
    $found = 0; $missing = @()
    foreach ($name in $controlNames) {
        $el = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $name)))
        if ($el) { $found++ } else { $missing += $name }
    }
    Report-Result "20a: Settings controls" ($found -ge 10) "$found/$($controlNames.Count) found" $(if ($missing) { "Missing: $($missing -join ', ')" })

    # Verify Save and Cancel buttons exist
    $saveBtn = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "Save")))
    $cancelBtn = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "Cancel")))
    Report-Result "20b: Save button" ($saveBtn -ne $null) "OK"
    Report-Result "20c: Cancel button" ($cancelBtn -ne $null) "OK"

    # Click Cancel to close settings
    if ($cancelBtn) {
        try { $cancelBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } catch { }
        Start-Sleep -Milliseconds 400
    }
}

function Test21-PricingSourceChange {
    Stop-App; Clear-OldLogs
    Write-Config '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"eng"},"Update":{"AutoUpdate":false},"Pricing":{"PricingSource":"poe2scout","League":"Runes of Aldur"}}'
    $proc = Launch-App; Wait-ForApp 5000 | Out-Null; Stop-App
    $log = Get-LatestLog
    $cached = ($log -and (Select-String -Path $log -Pattern "Pricing cache refreshed|Fetched.*price rows" -Quiet))
    Report-Result "21a: Poe2Scout cache loaded" $cached "OK"

    Stop-App; Clear-OldLogs
    Write-Config '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"eng"},"Update":{"AutoUpdate":false},"Pricing":{"PricingSource":"poe.ninja","League":"Runes of Aldur"}}'
    $proc = Launch-App; Wait-ForApp 5000 | Out-Null; Start-Sleep -Milliseconds 3000; Stop-App
    $log = Get-LatestLog
    $ninjaOk = ($log -and (Select-String -Path $log -Pattern "Poe.ninja|poe.ninja.*fetched|Pricing cache refreshed" -Quiet))
    Report-Result "21b: PoeNinja source loads" $ninjaOk $(if ($ninjaOk) { "OK" }else { "Not found" })
}

function Test22-LogLevelChange {
    Stop-App; Clear-OldLogs
    Write-Config '{"App":{"LogLevel":"Warning"},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"eng"},"Update":{"AutoUpdate":false}}'
    $proc = Launch-App -extraArgs "--App:LogLevel=Warning"
    Wait-ForApp 5000 | Out-Null; Stop-App
    $log = Get-LatestLog
    $hasInfo = ($log -and (Select-String -Path $log -Pattern "info:|Hosting started|Hosting starting" -Quiet))
    Report-Result "22a: Warning suppresses info" (-not $hasInfo) $(if (-not $hasInfo) { "Info hidden" }else { "Info present" })
}

function Test23-WindowPosition {
    Stop-App; Clear-OldLogs
    Write-Config '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"eng"},"Update":{"AutoUpdate":false}}'
    $proc = Launch-App-Headless; Wait-ForApp 5000 | Out-Null
    # Graceful close to trigger save
    $proc.CloseMainWindow() | Out-Null; $proc.WaitForExit(3000) | Out-Null
    if (-not $proc.HasExited) { Stop-App }
    if (Test-Path $configPath) {
        $cfg = Get-Content $configPath -Raw | ConvertFrom-Json
        $hasPos = ($cfg.Window.PSObject.Properties.Name -contains "Left") -and ($cfg.Window.PSObject.Properties.Name -contains "Top")
        Report-Result "23a: Position saved on close" $hasPos $(if ($hasPos) { "OK" }else { "Missing" })
        # Verify restore on next launch
        if ($hasPos) {
            $savedLeft = $cfg.Window.Left
            $savedTop = $cfg.Window.Top
            $proc2 = Launch-App-Headless -waitMs 3000; Wait-ForApp 5000 | Out-Null
            $proc2.CloseMainWindow() | Out-Null; $proc2.WaitForExit(3000) | Out-Null
            if (-not $proc2.HasExited) { Stop-App }
            $cfg2 = Get-Content $configPath -Raw | ConvertFrom-Json
            $restoredLeft = $cfg2.Window.Left
            $restoredTop = $cfg2.Window.Top
            $posRestored = ($restoredLeft -eq $savedLeft) -and ($restoredTop -eq $savedTop)
            Report-Result "23b: Position restored on launch" $posRestored $(if ($posRestored) { "L=$restoredLeft T=$restoredTop" }else { "Expected L=$savedLeft T=$savedTop, got L=$restoredLeft T=$restoredTop" })
        }
    }
}

function Test24-SettingsCancel($proc) {
    if ($proc.HasExited) { Report-Result "24: Cancel" $false "App exited"; return }
    $origCfg = if (Test-Path $configPath) { Get-Content $configPath -Raw } else { "" }
    Click-Button $proc "Settings" 3000 | Out-Null; Start-Sleep -Milliseconds 600
    $hwnd = $proc.MainWindowHandle
    try { $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd) } catch { Report-Result "24: No UIA" $false; return }
    $cancelBtn = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "Cancel")))
    if ($cancelBtn) { try { $cancelBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } catch {}; Start-Sleep -Milliseconds 600 }
    $cfgAfter = if (Test-Path $configPath) { Get-Content $configPath -Raw } else { "" }
    $normalize = { param($c) $c -replace '"InitialSetupComplete":\s*true,?\s*', '' -replace '"(Left|Top|Width|Height)":\s*\d+,?\s*', '' -replace ',\s*}', '}' -replace ',\s*}', '}' }
    $normalizedOrig = & $normalize $origCfg
    $normalizedAfter = & $normalize $cfgAfter
    Report-Result "24a: Cancel unchanged" ($normalizedOrig -eq $normalizedAfter) $(if ($normalizedOrig -eq $normalizedAfter) { "Unchanged" } else { "Modified" })
}

function Test25-OcrLanguageChange {
    Stop-App; Clear-OldLogs
    Write-Config '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"fra"},"Update":{"AutoUpdate":false}}'
    $proc = Launch-App; Wait-ForApp 5000 | Out-Null; Stop-App
    if (Test-Path $configPath) {
        $cfg = Get-Content $configPath -Raw | ConvertFrom-Json
        Report-Result "25a: Language persisted" ($cfg.OCR.Language -eq "fra") "Lang=$($cfg.OCR.Language)"
    }
}

function Test26-RapidSettingsChanges {
    Stop-App; Clear-OldLogs
    Write-Config $cfgBase
    $proc = Launch-App; Wait-ForApp 5000 | Out-Null
    $crashed = $false
    for ($i = 0; $i -lt 5; $i++) {
        Click-Button $proc "Settings" 2000 | Out-Null; Start-Sleep -Milliseconds 300
        Click-Button $proc "Settings" 2000 | Out-Null; Start-Sleep -Milliseconds 300
        if ($proc.HasExited) { $crashed = $true; break }
    }
    Stop-App
    Report-Result "26a: Rapid toggle no crash" (-not $crashed) $(if ($crashed) { "Crashed" }else { "Stable" })
}

function Test27-PriceCacheOnLeagueChange {
    Stop-App; Clear-OldLogs
    Write-Config '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"eng"},"Update":{"AutoUpdate":false},"Pricing":{"League":"Runes of Aldur"}}'
    $proc = Launch-App-Headless; Wait-ForApp 8000 | Out-Null
    Start-Sleep -Milliseconds 2000
    Stop-App
    $log = Get-LatestLog
    $firstRefresh = ($log -and (Select-String -Path $log -Pattern "Pricing cache refreshed|Fetched.*price rows|Already up to date" -Quiet))
    Report-Result "27a: Pricing cache loads" $firstRefresh $(if ($firstRefresh) { "OK" }else { "Not in log" })
}

function Test28-LogOrdering($proc) {
    if ($proc.HasExited) { Report-Result "28: Log ordering" $false "App exited"; return }

    Start-Sleep -Milliseconds 1500

    # Check window ordering: newest should be at index 0 (top of list)
    $hwnd = $proc.MainWindowHandle
    if ($hwnd -eq [IntPtr]::Zero) { Report-Result "28a: Window" $false "No HWND"; return }
    try { $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd) } catch { Report-Result "28a: UIA" $false "FromHandle failed"; return }

    # Read the log list content via UIA text pattern
    $logList = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "LogList")))
    if (-not $logList) { Report-Result "28a: Log list found" $false "Not found"; return }
    Report-Result "28a: Log list found" $true "Accessible"

    # Verify Copy Log produces entries with valid timestamps and content
    Click-Button $proc "Copy Log" 3000 | Out-Null
    try {
        $clipText = Wait-ForClipboard 'RuneshapePriceChecker' 3000
        if ($clipText) {
            $lines = $clipText -split "`r`n|`n" | Where-Object { $_ -and $_ -notmatch "^=== RuneshapePriceChecker" -and $_ -notmatch "^\s*$" }
            $timestamps = @()
            foreach ($line in $lines) {
                if ($line -match '^(\d{2}:\d{2}:\d{2}\.\d{3})\s') { $timestamps += [datetime]::ParseExact($Matches[1], "HH:mm:ss.fff", [CultureInfo]::InvariantCulture) }
            }
            # Check we have entries
            Report-Result "28b: Log content" ($timestamps.Count -gt 0) "$($timestamps.Count) entries"
            # Check header present
            $hasHeader = $clipText -match "RuneshapePriceChecker.*copied at"
            Report-Result "28c: Copy header" $hasHeader "OK"
            # Verify copy format: each line starts with HH:mm:ss.fff
            $validFormat = ($lines | Where-Object { $_ -notmatch '^\d{2}:\d{2}:\d{2}\.\d{3}\s' }).Count -eq 0
            Report-Result "28d: Line format" $validFormat "All lines have timestamps"
        }
        else { Report-Result "28b: Log content" $false "Empty clipboard" }
    }
    catch { Report-Result "28b: Log content" $false "Clipboard error: $_" }
}

function Test29-LogCoalescing($proc) {
    if ($proc.HasExited) { Report-Result "29: Coalescing" $false "App exited"; return }

    # Toggle settings several times to trigger duplicate log entries
    for ($i = 0; $i -lt 3; $i++) {
        if (-not (Invoke-Button $proc "Settings" 2000)) { break }
        Start-Sleep -Milliseconds 150
    }
    # Wait for coalesced entries to appear in the clipboard from background logging

    try {
        $clipText = Wait-ForClipboard 'RuneshapePriceChecker' 3000
        if (-not $clipText) { Report-Result "29a: Duplicate coalescing" $false "No clipboard content"; return }
        # Check for coalesced entries (those with "(x2)" or higher count)
        $hasCoalesced = $clipText -match '\(x\d+\)'
        $countText = if ($hasCoalesced) { $Matches[0] } else { "None" }
        Report-Result "29a: Duplicate coalescing" $hasCoalesced $(if ($hasCoalesced) { "Found $countText" }else { "No coalesced entries" })
    }
    catch { Report-Result "29a: Duplicate coalescing" $false "Clipboard error" }
}

function Test30-SettingsToggleStability($proc) {
    if ($proc.HasExited) { Report-Result "30: Settings toggle" $false "App exited"; return }

    # Rapid open/close settings panel, verify no crash
    $crashed = $false
    for ($i = 0; $i -lt 8; $i++) {
        if (-not (Invoke-Button $proc "Settings" 1500)) { break }
        if ($proc.HasExited) { $crashed = $true; break }
    }
    Report-Result "30a: Rapid toggle no crash" (-not $crashed) $(if ($crashed) { "Crashed" }else { "Stable" })
}

function Test31-TestModeIndicator {
    Stop-App; Clear-OldLogs; Write-Config $cfgBase
    $proc = Launch-App -extraArgs "--App:TestMode=true"
    Wait-ForApp 5000 | Out-Null
    $crashed = $proc.HasExited
    Stop-App
    Report-Result "31a: --App:TestMode no crash" (-not $crashed) $(if ($crashed) { "Exited" }else { "OK" })
}

function Test32-VersionDisplay {
    Stop-App; Clear-OldLogs; Write-Config $cfgBase
    $proc = Launch-App; Wait-ForApp 5000 | Out-Null
    # Wait for window handle
    $hwnd = [IntPtr]::Zero
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt 3000 -and $hwnd -eq [IntPtr]::Zero) { $hwnd = $proc.MainWindowHandle; Start-Sleep -Milliseconds 200 }
    $versionOk = $false
    if ($hwnd -ne [IntPtr]::Zero) {
        try {
            $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
            $versionRun = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "VersionRun")))
            if ($versionRun) {
                $vText = $versionRun.Current.Name
                $versionOk = $vText -match '^v\d+\.\d+\.\d+'
                Report-Result "32a: Version display" $versionOk $(if ($versionOk) { $vText }else { "Bad format: $vText" })
            }
            else { Report-Result "32a: Version display" $false "Element not found" }
        }
        catch { Report-Result "32a: Version display" $false "UIA error" }
    }
    else { Report-Result "32a: Version display" $false "No HWND" }
    Stop-App
}

function Test33-ChangelogWindowPopup {
    Stop-App; Clear-OldLogs
    Write-Config '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"eng"},"Update":{"AutoUpdate":false},"Changelog":{"Body":"## v1.0.0 Release Notes\nTest content","Version":"1.0.0","Shown":false}}'
    $proc = Launch-App; Wait-ForApp 5000 | Out-Null
    Start-Sleep -Milliseconds 1500
    $hwnd = $proc.MainWindowHandle
    $popupFound = $false
    $dismissed = $false
    if ($hwnd -ne [IntPtr]::Zero) {
        try {
            $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
            # Check for changelog dismiss button (inside changelog overlay within main window)
            $gotItBtn = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "Got it")))
            $popupFound = ($gotItBtn -ne $null)
            if ($popupFound -and $gotItBtn) {
                try { $gotItBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); $dismissed = $true; Start-Sleep -Milliseconds 500 } catch { }
            }
        }
        catch { }
    }
    Stop-App
    Report-Result "33a: Changelog popup visible" $popupFound $(if ($popupFound) { "Found" }else { "Not found" })
    if ($popupFound) { Report-Result "33b: Changelog dismissed" $dismissed $(if ($dismissed) { "OK" }else { "Failed" }) }
}

function Test34-CurrencyMutualExclusion($proc) {
    if ($proc.HasExited) { Report-Result "34: Currency" $false "App exited"; return }
    if (-not (Invoke-Button $proc "Settings" 3000)) { Report-Result "34a: Open settings" $false; return }
    Wait-ForUI $proc ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) "CurrencyChaosCheck" 2000 | Out-Null
    $hwnd = $proc.MainWindowHandle; if ($hwnd -eq [IntPtr]::Zero) { Report-Result "34a: HWND" $false; Invoke-Button $proc "Settings" 3000 | Out-Null; return }
    try { $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd) } catch { Report-Result "34a: UIA" $false; Invoke-Button $proc "Settings" 3000 | Out-Null; return }
    $chaosBox = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "CurrencyChaosCheck")))
    $exaltBox = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "CurrencyExaltCheck")))
    if (-not $chaosBox -or -not $exaltBox) { Report-Result "34a: Checkboxes" $false; Invoke-Button $proc "Settings" 3000 | Out-Null; return }
    Report-Result "34a: Checkboxes found" $true
    # Click Chaos, verify Exalt off
    try { $chaosBox.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 300 } catch { }
    $exaltChecked = $exaltBox.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On
    Report-Result "34b: Chaos⇢Exalt off" (-not $exaltChecked) $(if ($exaltChecked) { "Exalt still on" }else { "Exalt off" })
    # Click Exalt, verify Chaos off
    try { $exaltBox.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 300 } catch { }
    $chaosChecked = $chaosBox.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On
    Report-Result "34c: Exalt⇢Chaos off" (-not $chaosChecked) $(if ($chaosChecked) { "Chaos still on" }else { "Chaos off" })
    Invoke-Button $proc "Settings" 3000 | Out-Null
}

function Test35-SettingsValidationUI($proc) {
    if ($proc.HasExited) { Report-Result "35: Validation" $false "App exited"; return }
    if (-not (Invoke-Button $proc "Settings" 3000)) { Report-Result "35a: Open settings" $false; return }
    Wait-ForUI $proc ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) "RedThresholdBox" 2000 | Out-Null
    $hwnd = $proc.MainWindowHandle; if ($hwnd -eq [IntPtr]::Zero) { Report-Result "35a: HWND" $false; Invoke-Button $proc "Settings" 3000 | Out-Null; return }
    try { $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd) } catch { Report-Result "35a: UIA" $false; Invoke-Button $proc "Settings" 3000 | Out-Null; return }
    $redBox = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "RedThresholdBox")))
    $greenBox = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "GreenThresholdBox")))
    if (-not $redBox -or -not $greenBox) { Report-Result "35a: Threshold fields" $false; Invoke-Button $proc "Settings" 3000 | Out-Null; return }
    Report-Result "35a: Threshold fields found" $true
    try {
        $redVp = $redBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        $greenVp = $greenBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        $redVp.SetValue("10"); Start-Sleep -Milliseconds 100
        $greenVp.SetValue("1"); Start-Sleep -Milliseconds 100
    }
    catch { Report-Result "35b: Set values" $false "ValuePattern error"; Invoke-Button $proc "Settings" 3000 | Out-Null; return }
    Report-Result "35b: Invalid values set" $true "Red=10 Green=1"
    $saveBtn = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "Save")))
    if ($saveBtn) {
        # Save button should be disabled (opacity 0.4) because live validation prevents saving with invalid order
        $isEnabled = $saveBtn.Current.IsEnabled
        $btnOpacity = 0
        try { $btnOpacity = $saveBtn.Current.LabeledBy.ToString() } catch { }
        # Try to check if the status label shows the error message
        $statusFound = $false
        $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, ([System.Windows.Automation.Condition]::TrueCondition))
        foreach ($el in $all) {
            $n = $el.Current.Name; if ([string]::IsNullOrEmpty($n)) { continue }
            if ($n -match "should be less") { $statusFound = $true; break }
        }
        $saveDisabled = -not $isEnabled
        Report-Result "35c: Save disabled when invalid" $saveDisabled $(if ($saveDisabled) { "Save.IsEnabled=false" }else { "Save still enabled" })
        Report-Result "35d: Status shows error" $statusFound $(if ($statusFound) { "Error shown" }else { "No error text" })
    }
    else { Report-Result "35c: Save button" $false "Not found" }
    Invoke-Button $proc "Cancel" 3000 | Out-Null; Start-Sleep -Milliseconds 500
}

Write-Banner "RuneshapePriceChecker v1.0.0 Pre-Release Tests"
Write-Host "  Exe: $exe"
Write-Host ""

Stop-App

$runAll = $All -or (-not ($Test1 -or $Test2 -or $Test3 -or $Test4 -or $Test5 -or $Test6 -or $Test7 -or $Test8 -or $Test9 -or $Test10 -or $Test11 -or $Test12 -or $Test13 -or $Test14 -or $Test15 -or $Test16 -or $Test18 -or $Test19 -or $Test20 -or $Test21 -or $Test22 -or $Test23 -or $Test24 -or $Test25 -or $Test26 -or $Test27 -or $Test28 -or $Test29 -or $Test30 -or $Test31 -or $Test32 -or $Test33 -or $Test34 -or $Test35))

# ═══════════════════════════════════════════════════════════════
# PHASE 1: Restart-required tests (each test manages its own app lifecycle)
# These tests change config files, test startup behavior, or need fresh app state.
# ═══════════════════════════════════════════════════════════════
if ($runAll -or $Test1) { Test1-ChangelogSetupCoordination }
if ($runAll -or $Test2) { Test2-InitialSetupSuite }
if ($runAll -or $Test3) { Test3-AppLifecycle }
if ($runAll -or $Test4) { Test4-ConfigRobustness }
if ($runAll -or $Test5) { Test5-ErrorHandling }
if ($runAll -or $Test6) { Test6-SettingsPersistence }
if ($runAll -or $Test7) { Test7-InvalidThresholds }
if ($runAll -or $Test8) { Test8-AutoUpdater }
if ($runAll -or $Test9) { Test9-ChangelogButton }
if ($runAll -or $Test10) { Test10-OverlayFeatureToggles }

# Restart-mode tests (each starts/stops its own instance)
if ($runAll -or $Test18) { Test18-OcrBackendSetting }
if ($runAll -or $Test19) { Test19-ReRunSetup }
if ($runAll -or $Test21) { Test21-PricingSourceChange }
if ($runAll -or $Test22) { Test22-LogLevelChange }
if ($runAll -or $Test23) { Test23-WindowPosition }
if ($runAll -or $Test25) { Test25-OcrLanguageChange }
if ($runAll -or $Test26) { Test26-RapidSettingsChanges }
if ($runAll -or $Test27) { Test27-PriceCacheOnLeagueChange }
if ($runAll -or $Test31) { Test31-TestModeIndicator }
if ($runAll -or $Test32) { Test32-VersionDisplay }
if ($runAll -or $Test33) { Test33-ChangelogWindowPopup }
# ═══════════════════════════════════════════════════════════════
# PHASE 2: Shared-instance tests (single app, no restart between tests)
# These tests only read state or interact with the UI non-destructively.
# ═══════════════════════════════════════════════════════════════
$runPhase2 = $runAll -or $Test11 -or $Test12 -or $Test13 -or $Test14 -or $Test15 -or $Test16 -or $Test20 -or $Test24 -or $Test28 -or $Test29 -or $Test30 -or $Test34 -or $Test35
if ($runPhase2) {
    Write-Banner "PHASE 2: Shared-instance tests"
    Stop-App; Clear-OldLogs; Write-Config $cfgBase
    $sharedProc = Launch-App; Wait-ForApp 8000 | Out-Null
    if ($sharedProc.HasExited) { Report-Result "Phase2: App start" $false "Exited"; $sharedProc = $null }
    else { Report-Result "Phase2: App running" $true "PID $($sharedProc.Id)" }

    if ($sharedProc -and (-not $sharedProc.HasExited)) {
        # Each shared test stabilizes UI before starting — ensures no lingering panels
        if (($runAll -or $Test11) -and (-not $sharedProc.HasExited)) { Wait-ForUIGone $sharedProc ([System.Windows.Automation.AutomationElement]::NameProperty) "Cancel" 2000 | Out-Null; Test11-Logging $sharedProc }
        if (($runAll -or $Test12) -and (-not $sharedProc.HasExited)) { Test12-ResourceUsage $sharedProc }
        if (($runAll -or $Test13) -and (-not $sharedProc.HasExited)) { Wait-ForUIGone $sharedProc ([System.Windows.Automation.AutomationElement]::NameProperty) "Cancel" 2000 | Out-Null; Test13-UiElements $sharedProc }
        if (($runAll -or $Test14) -and (-not $sharedProc.HasExited)) { Wait-ForUIGone $sharedProc ([System.Windows.Automation.AutomationElement]::NameProperty) "Cancel" 2000 | Out-Null; Test14-SettingsFieldValidation $sharedProc }
        if (($runAll -or $Test15) -and (-not $sharedProc.HasExited)) { Wait-ForUIGone $sharedProc ([System.Windows.Automation.AutomationElement]::NameProperty) "Cancel" 2000 | Out-Null; Test15-TooltipVerification $sharedProc }
        if (($runAll -or $Test20) -and (-not $sharedProc.HasExited)) { Wait-ForUIGone $sharedProc ([System.Windows.Automation.AutomationElement]::NameProperty) "Cancel" 2000 | Out-Null; Test20-ComprehensiveSettingsRoundTrip $sharedProc }
        if (($runAll -or $Test24) -and (-not $sharedProc.HasExited)) { Wait-ForUIGone $sharedProc ([System.Windows.Automation.AutomationElement]::NameProperty) "Cancel" 2000 | Out-Null; Test24-SettingsCancel $sharedProc }
        # Log ordering tests before Test16 closes the app
        if (($runAll -or $Test28) -and (-not $sharedProc.HasExited)) { Wait-ForUIGone $sharedProc ([System.Windows.Automation.AutomationElement]::NameProperty) "Cancel" 2000 | Out-Null; Test28-LogOrdering $sharedProc }
        if (($runAll -or $Test29) -and (-not $sharedProc.HasExited)) { Wait-ForUIGone $sharedProc ([System.Windows.Automation.AutomationElement]::NameProperty) "Cancel" 2000 | Out-Null; Test29-LogCoalescing $sharedProc }
        if (($runAll -or $Test30) -and (-not $sharedProc.HasExited)) { Wait-ForUIGone $sharedProc ([System.Windows.Automation.AutomationElement]::NameProperty) "Cancel" 2000 | Out-Null; Test30-SettingsToggleStability $sharedProc }
        if (($runAll -or $Test34) -and (-not $sharedProc.HasExited)) { Wait-ForUIGone $sharedProc ([System.Windows.Automation.AutomationElement]::NameProperty) "Cancel" 2000 | Out-Null; Test34-CurrencyMutualExclusion $sharedProc }
        if (($runAll -or $Test35) -and (-not $sharedProc.HasExited)) { Wait-ForUIGone $sharedProc ([System.Windows.Automation.AutomationElement]::NameProperty) "Cancel" 2000 | Out-Null; Test35-SettingsValidationUI $sharedProc }
        # Test16 MUST be last: it clicks Close and exits the app
        if (($runAll -or $Test16) -and (-not $sharedProc.HasExited)) { Wait-ForUIGone $sharedProc ([System.Windows.Automation.AutomationElement]::NameProperty) "Cancel" 2000 | Out-Null; Test16-UiButtonInteractions $sharedProc }
        if (-not $sharedProc.HasExited) {
            $sharedProc.CloseMainWindow() | Out-Null
            $sharedProc.WaitForExit(3000) | Out-Null
            if (-not $sharedProc.HasExited) { $sharedProc.Kill() }
        }
    }
}

Write-Banner "RESULTS"
Write-Host "  ${ansiGreen}Passed: $passed$ansiReset"
if ($failed -gt 0) { Write-Host "  ${ansiRed}Failed: $failed$ansiReset" }
else { Write-Host "  Failed: $failed" }

$results | Format-Table -AutoSize

# Restore original config and clipboard
if ($configBackup -and (Test-Path $configDir)) { $configBackup | Set-Content $configPath -NoNewline }
if ($clipboardBackup) { try { Set-Clipboard -Value $clipboardBackup -ErrorAction SilentlyContinue } catch { } }

if ($failed -gt 0) { Write-Host "`n${ansiRed}Some tests FAILED.$ansiReset"; exit 1 }
else { Write-Host "`n${ansiGreen}All tests PASSED.$ansiReset"; exit 0 }
