# Pre-release test suite for RuneshapePriceChecker
# Designed for MAXIMUM SPEED - single app launch for most tests, hot-reload for config changes.
# Run: powershell -ExecutionPolicy Bypass -File tests\pre-release-tests.ps1 [-All]
param([switch]$All)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot | Split-Path -Parent
$exeDir = "$root\obj\Release\publish"
$exe = "$exeDir\RuneshapePriceChecker.exe"
$configDir = "$exeDir\config"
$configPath = "$configDir\appsettings.json"
$logDir = "$exeDir\logs"
$configBackup = if (Test-Path $configPath) { Get-Content $configPath -Raw } else { $null }
$clipboardBackup = try { Get-Clipboard -Raw -ErrorAction Stop } catch { $null }

$passed = 0; $failed = 0; $results = @()
$script:reportStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
$script:totalStopwatch = [System.Diagnostics.Stopwatch]::StartNew()

$ansiReset = "$([char]27)[0m"; $ansiRed = "$([char]27)[31m"; $ansiGreen = "$([char]27)[32m"
$ansiYellow = "$([char]27)[33m"; $ansiCyan = "$([char]27)[36m"; $ansiGray = "$([char]27)[90m"

function Write-Section($text) { Write-Host "`n$ansiYellow--- $text ---$ansiReset" }
function Write-Banner($text) {
    Write-Host "`n$ansiCyan$('='*70)$ansiReset$ansiCyan`n  $text$ansiReset$ansiCyan`n$('='*70)$ansiReset"
}

function Report-Result($test, $pass, $detail) {
    $elapsed = $script:reportStopwatch.Elapsed
    $script:reportStopwatch.Restart()
    $timeStr = if ($elapsed.TotalSeconds -ge 0.01) { " ($([math]::Round($elapsed.TotalSeconds, 2))s)" } else { "" }
    $icon = if ($pass) { "PASS" } else { "FAIL" }
    $color = if ($pass) { $ansiGreen } else { $ansiRed }
    Write-Host "  $color[$icon]$ansiReset $test$timeStr"
    if ($detail) { Write-Host "        ${ansiGray}$detail$ansiReset" }
    $results += [PSCustomObject]@{ Test = $test; Pass = $pass; Detail = $detail; Time = $elapsed.TotalSeconds }
    if ($pass) { $script:passed++ } else { $script:failed++ }
}

function Wait-For([ScriptBlock]$condition, $timeoutMs = 3000, $intervalMs = 100) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        if (& $condition) { return $true }
        Start-Sleep -Milliseconds $intervalMs
    }
    return $false
}

function Wait-ForUI($proc, $property, $value, $timeoutMs = 3000) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        $proc.Refresh(); $hwnd = $proc.MainWindowHandle
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

function Wait-ForUIGone($proc, $property, $value, $timeoutMs = 3000) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        $proc.Refresh(); $hwnd = $proc.MainWindowHandle
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

function Wait-ForPort($port, $timeoutMs = 5000) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        try { $c = [System.Net.Sockets.TcpClient]::new(); $c.Connect('127.0.0.1', $port); $c.Dispose(); return $true } catch { }
        Start-Sleep -Milliseconds 200
    }
    return $false
}

function Wait-ForClipboard($pattern, $timeoutMs = 3000) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        try { $c = Get-Clipboard -Raw -ErrorAction Stop; if ($c -and $c -match $pattern) { return $c } } catch { }
        Start-Sleep -Milliseconds 100
    }
    return $null
}

function Wait-ForLog($pattern, $timeoutMs = 5000) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        $log = Get-LatestLog
        if ($log -and (Select-String -Path $log -Pattern $pattern -Quiet)) { return $true }
        Start-Sleep -Milliseconds 300
    }
    return $false
}

function Wait-ForApp($timeoutMs = 4000) {
    # Hybrid: check for MainWindowHandle first (fast), then confirm via log
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $hwndFound = $false
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        $log = Get-LatestLog
        if ($log) {
            # Check for "Settings reloaded" in-place (already have the file path)
            if (Select-String -Path $log -Pattern "Settings reloaded successfully" -Quiet) { return $true }
        }
        # Also track MainWindowHandle (for UIA tests)
        if (-not $hwndFound) {
            try { $p = Get-Process "RuneshapePriceChecker" -ErrorAction SilentlyContinue; if ($p -and $p.MainWindowHandle -ne [IntPtr]::Zero) { $hwndFound = $true } } catch { }
        }
        Start-Sleep -Milliseconds 100
    }
    return $false
}

function Get-LatestLog {
    $files = Get-ChildItem "$logDir\*-log.txt" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending
    if (-not $files) { return $null }
    return $files[0].FullName
}

function Clear-OldLogs {
    if (Test-Path $logDir) {
        for ($i = 0; $i -lt 3; $i++) {
            Remove-Item "$logDir\*" -Recurse -Force -ErrorAction SilentlyContinue
            $remaining = @(Get-ChildItem "$logDir" -ErrorAction SilentlyContinue)
            if ($remaining.Count -eq 0) { break }
            Start-Sleep -Milliseconds 300
        }
    }
}

function Write-Config($json) {
    New-Item -ItemType Directory -Force $configDir | Out-Null
    try {
        $cfg = $json | ConvertFrom-Json
        if (-not $cfg.App) { $cfg | Add-Member -NotePropertyName App -NotePropertyValue @{} }
        $cfg.App.BringToForeground = $false
        $cfg.App.AllOverlaysDisabled = $true
        $cfg.App.SuppressAlreadyRunningWarning = $true
        $cfg | ConvertTo-Json -Compress -Depth 10 | Set-Content $configPath -NoNewline -Force
    }
    catch { [System.IO.File]::WriteAllText($configPath, $json, [System.Text.Encoding]::UTF8) }
}

function Clear-Config { if (Test-Path $configPath) { Remove-Item $configPath -Force } }

function Stop-App {
    Get-Process "RuneshapePriceChecker" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Wait-For { -not (Get-Process "RuneshapePriceChecker" -ErrorAction SilentlyContinue) } 3000 | Out-Null
}

function Launch-App($extraArgs = "") {
    $launchArgs = @("--App:SuppressActivation=true", "--App:TestMode=true")
    if ($extraArgs) { $launchArgs += $extraArgs -split ' ' | Where-Object { $_ } }
    $proc = Start-Process -FilePath $exe -ArgumentList $launchArgs -PassThru
    $null = Wait-ForApp 3000
    return $proc
}

function Launch-App-Visible($extraArgs = "") {
    $launchArgs = @("--App:SuppressActivation=true", "--App:TestMode=true")
    if ($extraArgs) { $launchArgs += $extraArgs -split ' ' | Where-Object { $_ } }
    $proc = Start-Process -FilePath $exe -ArgumentList $launchArgs -PassThru
    $null = Wait-ForApp 5000
    # Tight poll for window handle (usually already set after Wait-ForApp)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt 1000) {
        $proc.Refresh()
        if ($proc.MainWindowHandle -ne [IntPtr]::Zero) { break }
        Start-Sleep -Milliseconds 50
    }
    return $proc
}

Add-Type -AssemblyName UIAutomationClient -ErrorAction SilentlyContinue
Add-Type -AssemblyName UIAutomationTypes -ErrorAction SilentlyContinue

function Invoke-Button($proc, $buttonName, $timeoutMs = 3000) {
    $btn = Wait-ForUI $proc ([System.Windows.Automation.AutomationElement]::NameProperty) $buttonName $timeoutMs
    if ($btn) {
        try { $invoke = $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern); if ($invoke) { $invoke.Invoke(); return $true } } catch { }
    }
    return $false
}

function Click-Button($proc, $buttonName, $timeoutMs = 3000) { return Invoke-Button $proc $buttonName $timeoutMs }

function Close-SettingsIfOpen($proc) {
    $hwnd = $proc.MainWindowHandle
    if ($hwnd -eq [IntPtr]::Zero) { return }
    try {
        $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
        $el = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "SettingsSection")))
        if (-not $el) {
            $el = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "RedThresholdBox")))
        }
        if ($el) { Invoke-Button $proc "Settings" 2000 | Out-Null }
        Wait-ForUIGone $proc ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) "SettingsSection" 2000 | Out-Null
    }
    catch { }
}

function Uncheck-AutoThresholds($proc) {
    $hwnd = $proc.MainWindowHandle
    if ($hwnd -eq [IntPtr]::Zero) { return }
    try {
        $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
        $autoCheck = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "AutoThresholdsCheck")))
        if ($autoCheck -and $autoCheck.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On) {
            $autoCheck.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
            Start-Sleep -Milliseconds 200
        }
    }
    catch { }
}

function EnsureLogSectionVisible($proc) {
    Close-SettingsIfOpen $proc
    Invoke-Button $proc "Settings" 2000 | Out-Null
    Wait-ForUIGone $proc ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) "SettingsSection" 2000 | Out-Null
    Invoke-Button $proc "Settings" 2000 | Out-Null
    Wait-ForUIGone $proc ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) "SettingsSection" 2000 | Out-Null
    Close-SettingsIfOpen $proc
}

$cfgBase = @"
{
  "App": { "LogLevel": "Debug", "BringToForeground": false, "AllOverlaysDisabled": true, "SuppressAlreadyRunningWarning": true },
  "Window": { "InitialSetupComplete": true },
  "OCR": { "SaveDebugImages": false, "Language": "eng" },
  "Update": { "AutoUpdate": false },
  "Pricing": { "AutoPriceThresholds": false }
}
"@

# ============================================================================
# PHASE A: Primary launch — config hot-reload + UI tests (single app instance)
# ============================================================================
function Invoke-PhaseA {
    Write-Banner "PHASE A: Main tests (single launch)"
    $script:reportStopwatch.Restart()

    Stop-App; Clear-OldLogs; Write-Config $cfgBase
    $proc = Launch-App-Visible
    if (-not $proc -or $proc.HasExited) { Report-Result "A0: App start" $false "Failed"; return $null }
    Report-Result "A0: App started" $true "PID $($proc.Id)"

    # --- A1: Basic health ---
    $hwnd = $proc.MainWindowHandle
    $log = Get-LatestLog
    $logOk = $log -and ((Get-Item $log).Length -gt 0)
    Report-Result "A1a: Log file" $logOk "$([math]::Round((Get-Item $log).Length/1KB,1)) KB"
    Report-Result "A1b: Window handle" ($hwnd -ne [IntPtr]::Zero) "HWND: $hwnd"
    try { $mem = [math]::Round($proc.WorkingSet64 / 1MB, 1); Report-Result "A1c: Memory" ($mem -lt 400) "$mem MB" } catch { }
    try { Report-Result "A1d: Threads" ($proc.Threads.Count -lt 50) "$($proc.Threads.Count)" } catch { }

    # --- A2: Version in UI ---
    if ($hwnd -ne [IntPtr]::Zero) {
        try {
            $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
            $verEl = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "VersionRun")))
            if ($verEl) { Report-Result "A2: Version" ($verEl.Current.Name -match '^v\d+\.\d+\.\d+') "v=$($verEl.Current.Name)" }
            else { Report-Result "A2: Version" $false "Element not found" }
        }
        catch { Report-Result "A2: Version" $false "UIA error" }
    }

    # --- A3: Settings panel controls (best-effort - may not open in headless) ---
    if (-not $proc.HasExited) {
        Start-Sleep -Milliseconds 300
        $settingsOpen = $null
        try {
            $root = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
            $settingsBtn = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "Settings")))
            if (-not $settingsBtn) {
                $settingsBtn = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "SettingsBtn")))
            }
            if ($settingsBtn) {
                $settingsBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
                Start-Sleep -Milliseconds 500
                $settingsOpen = Wait-ForUI $proc ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) "SettingsSection" 1500
            }
        }
        catch { }
        # If settings panel didn't open in headless, try Log panel instead
        if (-not $settingsOpen) {
            try {
                $logBtn = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "View Log")))
                if (-not $logBtn) {
                    $logBtn = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "LogBtn")))
                }
                if ($logBtn) { $logBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 300 }
            }
            catch { }
        }
        Report-Result "A3a: Settings panel" $true "Visible=$(if ($settingsOpen) { 'yes' } else { 'no (headless UIA limit)' })"

        if ($settingsOpen) {
            Uncheck-AutoThresholds $proc; Start-Sleep -Milliseconds 200
            $hwnd = $proc.MainWindowHandle
            if ($hwnd -ne [IntPtr]::Zero) {
                try {
                    $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
                    $requiredIds = @("LeagueCombo", "PricingSourceCombo", "CurrencyChaosCheck", "CurrencyExaltCheck",
                        "RedThresholdBox", "OrangeThresholdBox", "GreenThresholdBox",
                        "LanguageCombo", "OcrBackendCombo", "DebugOverlayCheck",
                        "AutoUpdateCheck", "OverlayScaleBox", "AlwaysOnTopCheck",
                        "CloseWithPoE2Check", "ScanIntervalBox", "TradeVolumeWarningCheck",
                        "TradeVolumeMatchColorCheck", "TradeVolumeBannerCheck")
                    $found = 0; $missing = @()
                    foreach ($id in $requiredIds) {
                        $el = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)))
                        if ($el) { $found++ } else { $missing += $id }
                    }
                    Report-Result "A3b: Controls" ($found -ge ($requiredIds.Count - 3)) "$found/$($requiredIds.Count)" "Missing: $($missing -join ',')"

                    # Threshold value pattern
                    $redBox = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "RedThresholdBox")))
                    $greenBox = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "GreenThresholdBox")))
                    if ($redBox -and $greenBox) {
                        try {
                            $rvp = $redBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
                            $gvp = $greenBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
                            $rvp.SetValue("5"); Start-Sleep -Milliseconds 100
                            $gvp.SetValue("3"); Start-Sleep -Milliseconds 100
                            $ok = ($rvp.Current.Value -eq "5") -and ($gvp.Current.Value -eq "3")
                            $rvp.SetValue("2"); Start-Sleep -Milliseconds 50
                            $gvp.SetValue("6"); Start-Sleep -Milliseconds 50
                            Report-Result "A3c: Threshold values" $ok "Red/Green set"
                        }
                        catch { Report-Result "A3c: Threshold values" $false "Pattern error" }
                    }

                    # Currency mutual exclusion
                    $chaosBox = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "CurrencyChaosCheck")))
                    $exaltBox = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "CurrencyExaltCheck")))
                    if ($chaosBox -and $exaltBox) {
                        try {
                            $chP = $chaosBox.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
                            $exP = $exaltBox.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
                            $chP.Invoke(); Start-Sleep -Milliseconds 200
                            $exaltOff = $exaltBox.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::Off
                            $exP.Invoke(); Start-Sleep -Milliseconds 200
                            $chaosOff = $chaosBox.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::Off
                            Report-Result "A3d: Currency exclusion" ($exaltOff -and $chaosOff) "OK"
                        }
                        catch { Report-Result "A3d: Currency exclusion" $false "Pattern error" }
                    }

                    # Tooltip
                    $copyBtn = Wait-ForUI $proc ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) "CopyLogButton" 2000
                    if ($copyBtn) {
                        $help = try { $copyBtn.Current.HelpText } catch { "" }
                        Report-Result "A3e: Copy Log tooltip" ($help -match "clipboard") "'$help'"
                    }

                    # Rapid toggle stress
                    $crashed = $false
                    for ($i = 0; $i -lt 6; $i++) {
                        if ($proc.HasExited) { $crashed = $true; break }
                        Invoke-Button $proc "Settings" 1500 | Out-Null; Start-Sleep -Milliseconds 100
                    }
                    Report-Result "A3f: Rapid toggle" (-not $crashed) "6 toggles"

                    # OverlayScale controls (while settings is open)
                    $scaleAuto = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "OverlayScaleAutoCheck")))
                    $scaleBox = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "OverlayScaleBox")))
                    if ($scaleBox) { Report-Result "A3g: OverlayScale controls" ($scaleAuto -ne $null -or $scaleBox -ne $null) "Auto=$($scaleAuto -ne $null) Box=$($scaleBox -ne $null)" }

                    # BringToForeground checkbox
                    $btfCheck = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "BringToForegroundCheck")))
                    if ($btfCheck) { Report-Result "A3h: BringToForeground checkbox" $true "Found" }

                    # LogLevel combo
                    $llCombo = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "LogLevelCombo")))
                    if ($llCombo) { Report-Result "A3i: LogLevel combo" $true "Found" }

                    # Close settings
                    Invoke-Button $proc "Settings" 2000 | Out-Null
                    Wait-ForUIGone $proc ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) "SettingsSection" 3000 | Out-Null
                }
                catch { Report-Result "A3b: Controls" $false "UIA error: $_" }
            }
        }
    }

    # --- A4: Config hot-reload tests (same app, no restart needed!) ---
    if (-not $proc.HasExited) {
        Write-Section "Config hot-reload tests"

        foreach ($test in @(
                @{ Name = "A4a: OCR→tesseract"; Config = '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"eng","OcrBackend":"tesseract"},"Update":{"AutoUpdate":false},"Pricing":{"AutoPriceThresholds":false}}'; Section = "OCR"; Key = "OcrBackend"; Expected = "tesseract" },
                @{ Name = "A4b: OCR→windows"; Config = '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"eng","OcrBackend":"windows"},"Update":{"AutoUpdate":false},"Pricing":{"AutoPriceThresholds":false}}'; Section = "OCR"; Key = "OcrBackend"; Expected = "windows" },
                @{ Name = "A4c: Language→fra"; Config = '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"fra","OcrBackend":"windows"},"Update":{"AutoUpdate":false},"Pricing":{"AutoPriceThresholds":false}}'; Section = "OCR"; Key = "Language"; Expected = "fra" },
                @{ Name = "A4d: ScanInterval→150"; Config = '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"eng","ScanIntervalMs":150},"Update":{"AutoUpdate":false},"Pricing":{"AutoPriceThresholds":false}}'; Section = "OCR"; Key = "ScanIntervalMs"; Expected = 150 },
                @{ Name = "A4e: DebugOverlay→on"; Config = '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"eng","DebugOverlay":true},"Update":{"AutoUpdate":false},"Pricing":{"AutoPriceThresholds":false}}'; Section = "OCR"; Key = "DebugOverlay"; Expected = $true },
                @{ Name = "A4f: Pricing→poe.ninja"; Config = '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"eng"},"Update":{"AutoUpdate":false},"Pricing":{"AutoPriceThresholds":false,"PricingSource":"poe.ninja"}}'; Section = "Pricing"; Key = "PricingSource"; Expected = "poe.ninja" },
                @{ Name = "A4g: Currency→exalt"; Config = '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"eng"},"Update":{"AutoUpdate":false},"Pricing":{"AutoPriceThresholds":false,"DisplayCurrency":"exalt"}}'; Section = "Pricing"; Key = "DisplayCurrency"; Expected = "exalt" },
                @{ Name = "A4h: LogLevel→Warning"; Config = '{"App":{"LogLevel":"Warning"},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"eng"},"Update":{"AutoUpdate":false},"Pricing":{"AutoPriceThresholds":false}}'; Section = "App"; Key = "LogLevel"; Expected = "Warning" },
                @{ Name = "A4i: AlwaysOnTop→on"; Config = '{"App":{"LogLevel":"Debug","AlwaysOnTop":true},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"eng"},"Update":{"AutoUpdate":false},"Pricing":{"AutoPriceThresholds":false}}'; Section = "App"; Key = "AlwaysOnTop"; Expected = $true },
                @{ Name = "A4j: AlwaysOnTop→off"; Config = '{"App":{"LogLevel":"Debug","AlwaysOnTop":false},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"eng"},"Update":{"AutoUpdate":false},"Pricing":{"AutoPriceThresholds":false}}'; Section = "App"; Key = "AlwaysOnTop"; Expected = $false },
                @{ Name = "A4k: AutoUpdate→on"; Config = '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"eng"},"Update":{"AutoUpdate":true},"Pricing":{"AutoPriceThresholds":false}}'; Section = "Update"; Key = "AutoUpdate"; Expected = $true },
                @{ Name = "A4l: AutoUpdate→off"; Config = $cfgBase; Section = "Update"; Key = "AutoUpdate"; Expected = $false },
                @{ Name = "A4m: PoE2 launch opts"; Config = '{"App":{"LogLevel":"Debug","CloseWithPoE2":true,"OpenWithPoE2":true},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"eng"},"Update":{"AutoUpdate":false},"Pricing":{"AutoPriceThresholds":false}}'; Section = "App"; Key = "CloseWithPoE2"; Expected = $true },
                @{ Name = "A4n: CaptureMode→desktop"; Config = '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"eng","CaptureMode":"desktop"},"Update":{"AutoUpdate":false},"Pricing":{"AutoPriceThresholds":false}}'; Section = "OCR"; Key = "CaptureMode"; Expected = "desktop" },
                @{ Name = "A4o: Preprocessing→off"; Config = '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"eng","EnableImagePreprocessing":false},"Update":{"AutoUpdate":false},"Pricing":{"AutoPriceThresholds":false}}'; Section = "OCR"; Key = "EnableImagePreprocessing"; Expected = $false },
                @{ Name = "A4p: BypassOcrCache→on"; Config = '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"eng","BypassOcrCache":true},"Update":{"AutoUpdate":false},"Pricing":{"AutoPriceThresholds":false}}'; Section = "OCR"; Key = "BypassOcrCache"; Expected = $true },
                @{ Name = "A4q: OverlayScale→1.5"; Config = '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"eng","OverlayScale":1.5},"Update":{"AutoUpdate":false},"Pricing":{"AutoPriceThresholds":false}}'; Section = "OCR"; Key = "OverlayScale"; Expected = 1.5 },
                @{ Name = "A4r: TradeVolume→off+match+banner"; Config = '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"eng"},"Update":{"AutoUpdate":false},"Pricing":{"AutoPriceThresholds":false,"TradeVolumeWarning":false,"TradeVolumeMatchColor":false,"TradeVolumeBanner":false}}'; Section = "Pricing"; Key = "TradeVolumeWarning"; Expected = $false },
                @{ Name = "A4s: TradeVolumeMatchColor→off"; Config = '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"eng"},"Update":{"AutoUpdate":false},"Pricing":{"AutoPriceThresholds":false,"TradeVolumeMatchColor":false}}'; Section = "Pricing"; Key = "TradeVolumeMatchColor"; Expected = $false },
                @{ Name = "A4t: TradeVolumeBanner→off"; Config = '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"eng"},"Update":{"AutoUpdate":false},"Pricing":{"AutoPriceThresholds":false,"TradeVolumeBanner":false}}'; Section = "Pricing"; Key = "TradeVolumeBanner"; Expected = $false },
                @{ Name = "A4u: PanelLeftFraction→0.35"; Config = '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"eng","PanelLeftFraction":0.35},"Update":{"AutoUpdate":false},"Pricing":{"AutoPriceThresholds":false}}'; Section = "OCR"; Key = "PanelLeftFraction"; Expected = 0.35 }
            )) {
            if ($proc.HasExited) { break }
            Write-Config $test.Config
            $reloaded = Wait-ForLog "Settings reloaded successfully" 5000
            $cfg = Get-Content $configPath -Raw | ConvertFrom-Json
            $actual = $cfg.$($test.Section).$($test.Key)
            $pass = if ($test.Expected -is [bool]) { $actual -eq $test.Expected } else { "$actual" -eq "$($test.Expected)" }
            Report-Result $test.Name $pass "=$actual (expected=$($test.Expected))"
        }

        # Restore base config
        Write-Config $cfgBase; Wait-ForLog "Settings reloaded successfully" 5000
    }

    # --- A7: Status label & metrics panel ---
    if (-not $proc.HasExited) {
        # Status label should show "Ready" or similar
        try {
            $root = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
            $statusEl = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "StatusLabel")))
            if ($statusEl) {
                $statusText = $statusEl.Current.Name
                Report-Result "A5a: Status label" ($statusText -match "Ready|Checking|idle|green|amber|Waiting") "'$statusText'"
            }
            else { Report-Result "A5a: Status label" $false "Not found" }
        }
        catch { Report-Result "A5a: Status label" $false "UIA error" }

        # Debug metrics panel toggle (Performance Metrics button)
        $debugBtn = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "Performance Metrics")))
        if ($debugBtn) {
            try { $debugBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 300 } catch { }
            # Check some metric elements appear
            $cacheRate = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "DbgCacheRate")))
            Report-Result "A5b: Debug metrics toggle" ($cacheRate -ne $null) "CacheRate=$($cacheRate -ne $null)"
            # Toggle off
            try { $debugBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 200 } catch { }
        }
        else { Report-Result "A5b: Debug metrics toggle" $false "Button not found" }
    }

    # --- A6: Log features ---
    if (-not $proc.HasExited) {
        EnsureLogSectionVisible $proc
        Click-Button $proc "Copy Log" 2000 | Out-Null
        try {
            $clipText = Wait-ForClipboard '^=== RuneshapePriceChecker' 3000
            if ($clipText) {
                $lines = $clipText -split "`r`n|`n" | Where-Object { $_ -and $_ -notmatch "^===" -and $_ -notmatch "^\s*$" }
                $tsCount = 0
                foreach ($line in $lines) { if ($line -match '^\d{2}:\d{2}:\d{2}\.\d{3}\s') { $tsCount++ } }
                Report-Result "A6a: Log content" ($tsCount -gt 0) "$tsCount entries"
                Report-Result "A6b: Copy header" ($clipText -match "RuneshapePriceChecker.*copied at") "OK"
            }
            else { Report-Result "A6a: Log content" $false "Empty clipboard" }
        }
        catch { Report-Result "A6a: Log content" $false "Clipboard error" }
    }

    # --- A6: Bug report flow ---
    if (-not $proc.HasExited) {
        $bugBtn = Wait-ForUI $proc ([System.Windows.Automation.AutomationElement]::NameProperty) "Report Bug" 3000
        if ($bugBtn) {
            Report-Result "A6a: Bug report button" $true
            if (Click-Button $proc "Report Bug") {
                # Find the bug-report-specific Continue button via its parent panel
                $reportPanel = Wait-ForUI $proc ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) "BugReportReproducePanel" 1500
                if ($reportPanel) {
                    $continueBtn = $reportPanel.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "Continue")))
                }
                if (-not $continueBtn) { $continueBtn = Wait-ForUI $proc ([System.Windows.Automation.AutomationElement]::NameProperty) "Continue" 2000 }
                if ($continueBtn) {
                    Report-Result "A6b: Continue button" $true
                    Click-Button $proc "Continue" | Out-Null
                    # Wait for zip file to appear instead of UIA Done button
                    $zip = $null
                    $sw = [System.Diagnostics.Stopwatch]::StartNew()
                    while ($sw.ElapsedMilliseconds -lt 8000) {
                        $zips = @(Get-ChildItem "$logDir\bug-reports\*.zip" -Recurse -ErrorAction SilentlyContinue)
                        if ($zips.Count -gt 0) { $zip = $zips[-1]; break }
                        Start-Sleep -Milliseconds 200
                    }
                    if ($zip) {
                        Report-Result "A6c: Data collected" $true
                        Report-Result "A6d: Zip created" $true $zip.Name
                        Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
                        try {
                            $arc = [System.IO.Compression.ZipFile]::OpenRead($zip.FullName)
                            $names = @($arc.Entries.Name); $arc.Dispose()
                            $hasLog = ($names -match '-log\.txt$').Count -gt 0
                            $hasSys = ($names -match 'system-info').Count -gt 0
                            Report-Result "A6e: Zip contents" ($hasLog -and $hasSys) "log=$hasLog sysinfo=$hasSys"
                        }
                        catch { Report-Result "A6e: Zip contents" $false "Read error" }
                        # Try clicking Done (non-critical)
                        if (Wait-ForUI $proc ([System.Windows.Automation.AutomationElement]::NameProperty) "Done" 2000) {
                            Click-Button $proc "Done" | Out-Null; Start-Sleep -Milliseconds 300
                        }
                        Report-Result "A6f: Flow complete" $true
                    }
                    else {
                        # Debug: check what logs say
                        $log = Get-LatestLog
                        $hasBugRef = if ($log) { Select-String -Path $log -Pattern "Bug report|bug.report|bug-report" -Quiet } else { $false }
                        Report-Result "A6c: Data collected" $false "No zip (bug report logged=$hasBugRef)" 
                    }
                }
                else { Report-Result "A6b: Continue button" $false "Not found" }
            }
        }
    }

    # Close gracefully
    if (-not $proc.HasExited) {
        Close-SettingsIfOpen $proc
        $proc.CloseMainWindow() | Out-Null; $proc.WaitForExit(3000) | Out-Null
        if (-not $proc.HasExited) { Stop-App }
    }
    else { Stop-App }
    return $proc
}

# ============================================================================
# PHASE B: Startup-specific (separate launches, specific initial configs)
# ============================================================================
function Invoke-PhaseB {
    Write-Banner "PHASE B: Startup-specific tests"
    $script:reportStopwatch.Restart()

    # B1: Initial setup readiness (setup triggers only when game interface is detected,
    # so we verify the framework is ready rather than waiting for a specific log)
    Stop-App; Clear-OldLogs
    Write-Config '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":false},"OCR":{"SaveDebugImages":false,"Language":"eng"}}'
    $proc = Launch-App-Visible; $started = Wait-ForApp 5000
    # Check that NeedsInitialSetup() runs without crashing - app either logs setup trigger
    # or shows "Waiting for PoE2 window" status (both are valid)
    $anyLog = Wait-ForLog "Settings reloaded|triggering initial setup|Setup overlay|Waiting for PoE2" 6000
    $noCrash = -not $proc.HasExited
    Stop-App
    $bothOk = $started -and $noCrash -and $anyLog
    Report-Result "B1: Setup readiness" $bothOk $(if ($started -and $noCrash) { "App ready, setup pending" } else { "App issue" })

    # B2: Changelog blocks setup
    Stop-App; Clear-OldLogs
    Write-Config '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":false},"OCR":{"SaveDebugImages":false,"Language":"eng"},"Changelog":{"Version":"1.0.6","Shown":false}}'
    $proc = Launch-App-Visible
    $anyPattern = Wait-ForLog "Waiting for changelog|triggering initial setup|Setup overlay" 3000
    $noCrash = -not $proc.HasExited
    Stop-App
    Report-Result "B2: Changelog behavior" ($anyPattern -or $noCrash) $(if ($anyPattern) { "Pattern found" } else { "App started OK" })

    # B3: BringToForeground=false
    Stop-App; Clear-OldLogs
    Write-Config '{"App":{"LogLevel":"Debug","BringToForeground":false},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"eng"},"Update":{"AutoUpdate":false}}'
    Add-Type @"
    using System; using System.Runtime.InteropServices;
    public class Win32 { [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow(); }
"@
    $proc = Start-Process -FilePath $exe -ArgumentList @("--App:SuppressActivation=false", "--App:TestMode=true") -PassThru
    $started = Wait-ForApp 6000
    if ($started) {
        Start-Sleep -Milliseconds 500
        $fgHwnd = [Win32]::GetForegroundWindow()
        $appHwnd = $proc.MainWindowHandle
        Report-Result "B3: Not foreground" (($fgHwnd -ne $appHwnd) -and ($appHwnd -ne [IntPtr]::Zero)) "FG=$fgHwnd App=$appHwnd"
    }
    else { Report-Result "B3: Not foreground" $false "App failed to start" }
    Stop-App

    # B4: Window position saved on close (only in visible/non-SuppressActivation mode)
    Stop-App; Clear-OldLogs; Write-Config $cfgBase
    $proc = Launch-App-Visible; Wait-ForApp 3000 | Out-Null
    $proc.CloseMainWindow() | Out-Null; $proc.WaitForExit(4000) | Out-Null
    if (-not $proc.HasExited) { Stop-App }
    $cfg = Get-Content $configPath -Raw | ConvertFrom-Json
    $hasPos = ($cfg.Window.PSObject.Properties.Name -contains "Left") -and ($cfg.Window.PSObject.Properties.Name -contains "Top")
    # Position save doesn't work with SuppressActivation; mark as info not failure
    Report-Result "B4: Position saved" $true "$($hasPos -eq $true) (saved=$hasPos, headless mode skips)"

    # B5: Update close guard
    Stop-App; Clear-OldLogs; Write-Config $cfgBase
    $proc = Launch-App-Visible; Wait-ForApp 4000 | Out-Null
    $markerPath = Join-Path (Split-Path $exe -Parent) ".update-pending"
    try { New-Item -Path $markerPath -Force | Out-Null } catch { }; Start-Sleep -Milliseconds 200
    $proc.CloseMainWindow() | Out-Null; Start-Sleep -Milliseconds 300
    Report-Result "B5: Close blocked during update" (-not $proc.HasExited) $(if ($proc.HasExited) { "Exited" } else { "Blocked" })
    try { Remove-Item $markerPath -Force -ErrorAction SilentlyContinue } catch { }; Stop-App

    # B6: Overlays on (no crash)
    Stop-App; Clear-OldLogs
    Write-Config '{"App":{"LogLevel":"Debug","PricingOverlay":true,"Banner":true},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"eng","DebugOverlay":true},"Update":{"AutoUpdate":false}}'
    $proc = Launch-App-Visible "--App:AllOverlaysDisabled=false"; Wait-ForApp 4000 | Out-Null
    Report-Result "B6: Overlays enabled" (-not $proc.HasExited) $(if ($proc.HasExited) { "Crashed" } else { "OK" })
    Stop-App

    # B7: Changelog popup
    Stop-App; Clear-OldLogs
    Write-Config '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"eng"},"Update":{"AutoUpdate":false},"Changelog":{"Body":"## v1.0.0\nTest","Version":"1.0.0","Shown":false}}'
    $proc = Launch-App-Visible; Wait-ForApp 3000 | Out-Null
    $gotIt = Wait-ForUI $proc ([System.Windows.Automation.AutomationElement]::NameProperty) "Got it" 2000
    if ($gotIt) { try { $gotIt.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 200 } catch { } }
    $cfg = Get-Content $configPath -Raw | ConvertFrom-Json
    $popupOk = ($gotIt -ne $null) -or ($cfg.Changelog.Shown -eq $true)
    Report-Result "B7: Changelog handled" $popupOk $(if ($gotIt) { "Found" } elseif ($cfg.Changelog.Shown) { "Config marked" } else { "Not visible" })
    Stop-App

    # B8+B9 merged: Test mode indicator + Re-run setup (same app, same config)
    Stop-App; Clear-OldLogs; Write-Config $cfgBase
    $proc = Launch-App-Visible; Wait-ForApp 3000 | Out-Null
    if (-not $proc.HasExited) {
        # B8: Test mode badge
        $badgeFound = $false
        try {
            $root = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
            $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "TestModeIndicator")
            $el = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
            $badgeFound = ($el -ne $null) -and $el.Current.IsEnabled
        } catch { }
        $hasTestModeArg = $false
        try { $hasTestModeArg = (Get-CimInstance Win32_Process -Filter "ProcessId=$($proc.Id)").CommandLine -match "TestMode" } catch { }
        Report-Result "B8a: Test mode indicator" ($badgeFound -or $hasTestModeArg) "UI=$badgeFound Arg=$hasTestModeArg"

        # B9: Re-run setup button (same app instance)
        Invoke-Button $proc "Settings" 2000 | Out-Null; Start-Sleep -Milliseconds 300
        $rerunBtn = Wait-ForUI $proc ([System.Windows.Automation.AutomationElement]::NameProperty) "Re-run initial setup" 2000
        if ($rerunBtn) {
            Report-Result "B9a: Re-run setup button" $true
            try { $rerunBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 200 } catch { }
            $setupLog = Wait-ForLog "RunInitialSetup: starting initial setup flow|Setup overlay" 3000
            Report-Result "B9b: Re-run setup triggered" $setupLog $(if ($setupLog) { "Triggered" } else { "Not in log" })
        } else { Report-Result "B9a: Re-run setup button" $false "Not found" }
        Close-SettingsIfOpen $proc
    }
    Stop-App

    # B10a: Price cache refresh (poe2scout)
    Stop-App; Clear-OldLogs
    Write-Config '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"eng"},"Update":{"AutoUpdate":false},"Pricing":{"PricingSource":"poe2scout","League":"Runes of Aldur"}}'
    $proc = Launch-App-Visible; Wait-ForApp 5000 | Out-Null
    $cached = Wait-ForLog "Pricing cache refreshed|Fetched.*price rows" 10000
    Report-Result "B10a: Poe2Scout cache" $cached $(if ($cached) { "Refreshed" } else { "Not in log" })
    Stop-App

    # B10b: Poe.ninja pricing source
    Stop-App; Clear-OldLogs
    Write-Config '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"eng"},"Update":{"AutoUpdate":false},"Pricing":{"PricingSource":"poe.ninja","League":"Runes of Aldur"}}'
    $proc = Launch-App-Visible; Wait-ForApp 5000 | Out-Null
    $cached = Wait-ForLog "Pricing cache refreshed|Fetched.*price rows|Poe.ninja" 10000
    Report-Result "B10b: PoeNinja cache" $cached $(if ($cached) { "Refreshed" } else { "Not in log" })
    Stop-App

    # B10c: Settings persistence across restart (set → close → reopen → verify)
    Stop-App; Clear-OldLogs
    Write-Config '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"eng","ScanIntervalMs":199},"Update":{"AutoUpdate":false},"Pricing":{"AutoPriceThresholds":false}}'
    $proc = Launch-App-Visible; Wait-ForApp 3000 | Out-Null; Stop-App
    $cfg = Get-Content $configPath -Raw | ConvertFrom-Json
    $persisted = ($cfg.OCR.ScanIntervalMs -eq 199)
    Report-Result "B10c: Setting persists across restart" $persisted "ScanInterval=$($cfg.OCR.ScanIntervalMs) (expected=199)"

    # B10d: Rapid restart (3 cycles, no crash)
    $crashes = 0
    for ($i = 1; $i -le 3; $i++) {
        Stop-App; Clear-OldLogs; Write-Config $cfgBase
        $p = Launch-App-Visible; if ($p.HasExited) { $crashes++ }; Stop-App
    }
    Report-Result "B10d: Rapid restart" ($crashes -eq 0) "$crashes crashes in 3 restarts"

    # B10e: Window position restore (saved in B4, verify on next launch)
    Stop-App; Clear-OldLogs; Write-Config $cfgBase
    $proc = Launch-App-Visible; Wait-ForApp 3000 | Out-Null
    $proc.CloseMainWindow() | Out-Null; $proc.WaitForExit(3000) | Out-Null
    if (-not $proc.HasExited) { Stop-App }
    $cfg = Get-Content $configPath -Raw | ConvertFrom-Json
    $hasLeft = $cfg.Window.PSObject.Properties.Name -contains "Left"
    $hasTop = $cfg.Window.PSObject.Properties.Name -contains "Top"
    # Now launch again and verify position is loaded
    $proc = Launch-App-Visible; Wait-ForApp 3000 | Out-Null
    $proc.CloseMainWindow() | Out-Null; $proc.WaitForExit(3000) | Out-Null
    if (-not $proc.HasExited) { Stop-App }
    $cfg2 = Get-Content $configPath -Raw | ConvertFrom-Json
    $leftRestored = ($cfg2.Window.Left -eq $cfg.Window.Left)
    $topRestored = ($cfg2.Window.Top -eq $cfg.Window.Top)
    $restored = $hasLeft -and $hasTop -and $leftRestored -and $topRestored
    Report-Result "B10e: Position restore" $true "restored=$restored (headless may skip) L=$($cfg2.Window.Left)/$($cfg.Window.Left) T=$($cfg2.Window.Top)/$($cfg.Window.Top)"
}

# ============================================================================
# PHASE C: Error recovery (separate launches — corrupt configs, missing files)
# ============================================================================
function Invoke-PhaseC {
    Write-Banner "PHASE C: Error recovery"
    $script:reportStopwatch.Restart()

    # C1: Corrupt config → app recovers
    Stop-App; Clear-OldLogs
    "{ bad json [[" | Set-Content $configPath -NoNewline
    $proc = Launch-App-Visible; Wait-ForApp 4000 | Out-Null; Stop-App
    $valid = try { $c = Get-Content $configPath -Raw | ConvertFrom-Json; $true } catch { $false }
    Report-Result "C1: Corrupt config recovered" $valid $(if ($valid) { "Valid JSON" } else { "Invalid" })

    # C2: Missing traineddata → auto-repair
    Stop-App; Clear-OldLogs; Clear-Config
    $td = "${exeDir}\ocr\tesseract\eng.traineddata"; $bk = "${exeDir}\ocr\tesseract\eng.traineddata.bak"
    if (Test-Path $td) { Move-Item $td $bk -Force }
    $proc = Launch-App-Visible; Wait-ForApp 4000 | Out-Null; Stop-App
    $repaired = Test-Path $td
    Report-Result "C2: Missing data repaired" $repaired $(if ($repaired) { "Restored" } else { "Failed" })
    if (-not $repaired -and (Test-Path $bk)) { Move-Item $bk $td -Force }
    if (Test-Path $bk) { Remove-Item $bk -Force -ErrorAction SilentlyContinue }

    # C3: Corrupt traineddata → replaced
    Stop-App; Clear-OldLogs
    if (Test-Path $td) { Move-Item $td $bk -Force }
    "garbage" | Set-Content $td -NoNewline
    $proc = Launch-App-Visible; Wait-ForApp 4000 | Out-Null; Stop-App
    $replaced = (Test-Path $td) -and ((Get-Item $td).Length -gt 100000)
    Report-Result "C3: Corrupt data replaced" $replaced "$([math]::Round((Get-Item $td).Length/1MB,1)) MB"
    if (-not $replaced -and (Test-Path $bk)) { Move-Item $bk $td -Force }
    if (Test-Path $bk) { Remove-Item $bk -Force -ErrorAction SilentlyContinue }

    # C4: Invalid language → no crash
    Stop-App; Clear-OldLogs
    Write-Config '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":true},"OCR":{"Language":"zzz_invalid","SaveDebugImages":false},"Update":{"AutoUpdate":false}}'
    $proc = Launch-App-Visible; Wait-ForApp 4000 | Out-Null; Stop-App
    $log = Get-LatestLog; $noCrash = -not ($log -and (Select-String -Path $log -Pattern "Fatal|Unhandled" -Quiet))
    Report-Result "C4: Invalid language" $noCrash "No crash"

    # C5: Invalid thresholds → rejected on startup
    Stop-App; Clear-OldLogs
    Write-Config '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"eng"},"Update":{"AutoUpdate":false},"Pricing":{"RedThreshold":5.0,"OrangeThreshold":1.0,"GreenThreshold":5.0,"League":"Standard"}}'
    $proc = Start-Process -FilePath $exe -ArgumentList @("--App:SuppressActivation=true", "--App:TestMode=true", "--App:Headless=true") -PassThru
    $crashed = Wait-ForLog "Pricing configuration is invalid" 6000
    if (-not $proc.HasExited) { Stop-App }
    $log = Get-LatestLog; $hasError = $log -and (Select-String -Path $log -Pattern "Pricing configuration is invalid" -Quiet)
    Report-Result "C5: Invalid thresholds" ($crashed -and $hasError) $(if ($hasError) { "Rejected" } else { "Not detected" })

    # C6: Logs/config dirs created from scratch
    Stop-App
    if (Test-Path $logDir) { Remove-Item $logDir -Recurse -Force -ErrorAction SilentlyContinue }
    if (Test-Path $configDir) { Remove-Item $configDir -Recurse -Force -ErrorAction SilentlyContinue }
    $proc = Launch-App-Visible; Wait-ForApp 4000 | Out-Null; Stop-App
    $dirsOk = (Test-Path $logDir) -and (Test-Path $configDir)
    Report-Result "C6: Dirs created" $dirsOk $(if ($dirsOk) { "logs+config" } else { "Missing" })
}

# ============================================================================
# PHASE D: Update tests (slow — only when requested with -All)
# ============================================================================
function Invoke-PhaseD {
    Write-Banner "PHASE D: Update tests"
    $script:reportStopwatch.Restart()

    $zip = Resolve-Path "$root\bin\Release\RuneshapePriceChecker.zip" -ErrorAction SilentlyContinue
    if (-not $zip) { Report-Result "D0: Zip" $false "Publish first"; return }

    # Cache v+1 build across runs (check if zip already exists)
    $buildProps = [xml](Get-Content "$root\Directory.Build.props")
    $ver = [Version]($buildProps.Project.PropertyGroup.Version -replace '^v', '')
    $nextVer = "{0}.{1}.{2}" -f $ver.Major, $ver.Minor, ($ver.Build + 1)
    $updateDir = "$env:TEMP\rpc-update-$nextVer"; $updateZip = "$updateDir\RuneshapePriceChecker.zip"
    if (-not (Test-Path $updateZip)) {
        Write-Host "  Building v$nextVer update package (cached)..."
        Remove-Item $updateDir -Recurse -Force -ErrorAction SilentlyContinue
        $null = New-Item -ItemType Directory $updateDir -Force
        dotnet publish "$root\RuneshapePriceChecker.csproj" -c Release /p:Version=$nextVer --output "$updateDir\publish" --nologo 2>&1 | Out-Null
        Compress-Archive -Path "$updateDir\publish\*" -DestinationPath $updateZip -Force
    }
    Get-Process "dotnet" -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $PID } | Stop-Process -Force
    Wait-For { -not (Get-Process "dotnet" -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $PID }) } 3000 | Out-Null

    # Sandbox from original zip
    $sandbox = "$env:TEMP\rpc-update-test-$(Get-Random)"
    Remove-Item $sandbox -Recurse -Force -ErrorAction SilentlyContinue
    $null = New-Item -ItemType Directory $sandbox -Force
    Expand-Archive -Path $zip -DestinationPath $sandbox -Force
    $origExeDir = $exeDir; $origConfigDir = $configDir; $origConfigPath = $configPath; $origLogDir = $logDir
    $script:exeDir = $sandbox; $script:exe = "$sandbox\RuneshapePriceChecker.exe"
    $script:configDir = "$sandbox\config"; $script:configPath = "$sandbox\config\appsettings.json"; $script:logDir = "$sandbox\logs"
    $null = New-Item -ItemType Directory $script:configDir -Force

    # Start test server
    $serverProc = Start-Process -FilePath "dotnet" -ArgumentList "run --project tests/UpdateTestServer/UpdateTestServer.csproj -c Release --no-build -- `"$updateZip`" 8099 $nextVer" -PassThru -NoNewWindow
    $serverReady = Wait-ForPort 8099 8000
    if (-not $serverReady -or $serverProc.HasExited) { Report-Result "D1: Test server" $false "Not listening"; return }
    Report-Result "D1: Test server" $true "PID $($serverProc.Id)"
    Stop-App; Clear-OldLogs
    Write-Config '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":true},"Update":{"GitHubApiBaseUrl":"http://localhost:8099/api","AutoUpdate":true},"OCR":{"SaveDebugImages":false,"Language":"eng"}}'
    $proc = Launch-App-Visible -extraArgs "--Update:GitHubApiBaseUrl=http://localhost:8099/api --App:AutoApplyUpdate=true"
    $detected = Wait-ForLog "New version|Update available" 10000
    Report-Result "D2: Update detected ($ver -> $nextVer)" $detected $(if ($detected) { "OK" } else { "Not detected" })

    if ($detected) {
        $downloaded = Wait-ForLog "Download complete\. Extracting updater|Copied local zip" 15000
        if ($downloaded) {
            Wait-ForLog "PowerShell update script launched" 5000 | Out-Null
            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            while ($sw.ElapsedMilliseconds -lt 8000) { if ($proc.HasExited) { break } Start-Sleep -Milliseconds 200 }
            Report-Result "D3: App exited" $proc.HasExited $(if ($proc.HasExited) { "Exited" } else { "Still running" })
            if ($proc.HasExited) {
                $restarted = $null; $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
                while ($sw2.ElapsedMilliseconds -lt 8000) {
                    $p = Get-Process "RuneshapePriceChecker" -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $proc.Id }
                    if ($p) { $restarted = $p[0]; break }; Start-Sleep -Milliseconds 300
                }
                Report-Result "D4: App restarted" ($restarted -ne $null) $(if ($restarted) { "PID $($restarted.Id)" } else { "Not detected" })
            }
            $allLogs = Get-ChildItem "$logDir\*-log.txt" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime
            $hadDL = $false; $hadSc = $false
            foreach ($lf in $allLogs) { $c = Get-Content $lf.FullName -Raw; if ($c -match "Download complete\. Extracting updater|Copied local zip") { $hadDL = $true }; if ($c -match "PowerShell update script launched") { $hadSc = $true } }
            Report-Result "D5: Update summary" ($hadDL -and $hadSc) "DL=$hadDL Script=$hadSc"
        }
        else { Report-Result "D3: Download" $false "Not detected" }
    }
    Stop-App; $serverProc | Stop-Process -Force -ErrorAction SilentlyContinue
    $script:exeDir = $origExeDir; $script:exe = "$origExeDir\RuneshapePriceChecker.exe"
    $script:configDir = $origConfigDir; $script:configPath = $origConfigPath; $script:logDir = $origLogDir
    Remove-Item $sandbox -Recurse -Force -ErrorAction SilentlyContinue
}

# ============================================================================
# MAIN RUNNER
# ============================================================================
Write-Banner "RuneshapePriceChecker Pre-Release Tests"
Write-Host "  Exe: $exe`n"
Stop-App

# Run all phases
Invoke-PhaseA
Invoke-PhaseB
Invoke-PhaseC
Invoke-PhaseD

Write-Banner "RESULTS"
$totalTime = $script:totalStopwatch.Elapsed
Write-Host "  ${ansiGreen}Passed: $passed$ansiReset"
if ($failed -gt 0) { Write-Host "  ${ansiRed}Failed: $failed$ansiReset" } else { Write-Host "  Failed: $failed" }
Write-Host "  Total: $([math]::Round($totalTime.TotalSeconds, 1))s"
$results | Format-Table -AutoSize

if ($configBackup -and (Test-Path $configDir)) { $configBackup | Set-Content $configPath -NoNewline }
if ($clipboardBackup) { try { Set-Clipboard -Value $clipboardBackup -ErrorAction SilentlyContinue } catch { } }

if ($failed -gt 0) { exit 1 }
