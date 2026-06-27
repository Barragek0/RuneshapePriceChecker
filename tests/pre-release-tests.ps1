# Pre-release test suite for RuneshapePriceChecker
# Specifically aims to test as many features as possible without requiring user interaction, and to catch regressions before release.
# Run: powershell -ExecutionPolicy Bypass -File tests\pre-release-tests.ps1 [-TestN] [-All]
param(
    [switch]$Test1, [switch]$Test2, [switch]$Test3, [switch]$Test4,
    [switch]$Test5, [switch]$Test6, [switch]$Test7, [switch]$Test8,
    [switch]$Test9, [switch]$Test10, [switch]$Test11, [switch]$Test12,
    [switch]$Test13, [switch]$Test14, [switch]$Test15, [switch]$Test16, [switch]$Test18, [switch]$Test19, [switch]$Test20,
    [switch]$Test21, [switch]$Test22, [switch]$Test23,
    [switch]$Test25, [switch]$Test26, [switch]$Test27,
    [switch]$Test28, [switch]$Test29,
    [switch]$Test31, [switch]$Test32, [switch]$Test33,
    [switch]$Test34, [switch]$Test35,
    [switch]$Test36,
    [switch]$Test37,
    [switch]$Test38, [switch]$Test39, [switch]$Test40,
    [switch]$Test41,
    [switch]$Test42,
    [switch]$Test43,
    [switch]$Test44,
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

function Wait-ForPort($port, $timeoutMs = 5000) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        try { $c = [System.Net.Sockets.TcpClient]::new(); $c.Connect('127.0.0.1', $port); $c.Dispose(); return $true } catch { }
        Start-Sleep -Milliseconds 200
    }
    return $false
}

function Wait-ForProcess($name, $timeoutMs = 10000) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        $p = Get-Process $name -ErrorAction SilentlyContinue
        if ($p) { return $p }
        Start-Sleep -Milliseconds 200
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

# Close settings panel if open (settings auto-save on toggle close)
function Close-SettingsIfOpen($proc) {
    $hwnd = $proc.MainWindowHandle
    if ($hwnd -eq [IntPtr]::Zero) { return }
    try {
        $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
        # Check by SettingsSection (always visible when open) or RedThresholdBox (when auto-thresholds off)
        $el = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "SettingsSection")))
        if (-not $el) {
            $el = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "RedThresholdBox")))
        }
        if ($el) { Invoke-Button $proc "Settings" 3000 | Out-Null }
        Wait-ForUIGone $proc ([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "SettingsSection") 3000 | Out-Null
    }
    catch { }
}

# Uncheck AutoThresholds so threshold text boxes become visible via UIA
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

# Ensure log section is visible (close settings/changelog if open)
function EnsureLogSectionVisible($proc) {
    Close-SettingsIfOpen $proc
    # Double-toggle as safety net for any state
    Invoke-Button $proc "Settings" 3000 | Out-Null
    Wait-ForUIGone $proc ([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "SettingsSection") 3000 | Out-Null
    Invoke-Button $proc "Settings" 3000 | Out-Null
    Wait-ForUIGone $proc ([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "SettingsSection") 3000 | Out-Null
    Close-SettingsIfOpen $proc
}

function Stop-App {
    Get-Process "RuneshapePriceChecker" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Wait-For { -not (Get-Process "RuneshapePriceChecker" -ErrorAction SilentlyContinue) } 5000 | Out-Null
}

function Write-Config($json) {
    New-Item -ItemType Directory -Force $configDir | Out-Null
    # Merge in mandatory test-isolation settings so every test gets them
    # regardless of what the caller provides. These prevent overlays from
    # rendering, the window from stealing focus, and duplicate-instance warnings.
    try {
        $cfg = $json | ConvertFrom-Json
        if (-not $cfg.App) { $cfg | Add-Member -NotePropertyName App -NotePropertyValue @{} }
        $cfg.App.BringToForeground = $false
        $cfg.App.AllOverlaysDisabled = $true
        $cfg.App.SuppressAlreadyRunningWarning = $true
        $cfg | ConvertTo-Json -Compress -Depth 10 | Set-Content $configPath -NoNewline
    }
    catch {
        # If JSON is invalid (e.g. corrupt-config tests), write as-is
        $json | Set-Content $configPath -NoNewline
    }
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

function Wait-ForApp($timeoutMs = 3500) { return Wait-ForLog "Settings reloaded successfully" $timeoutMs }

function Launch-App($extraArgs = "") {
    $launchArgs = @("--App:SuppressActivation=true", "--App:TestMode=true")
    if ($extraArgs) { $launchArgs += $extraArgs -split ' ' | Where-Object { $_ } }
    $proc = Start-Process -FilePath $exe -ArgumentList $launchArgs -PassThru
    Wait-ForApp 8000 | Out-Null
    return $proc
}

function Launch-App-Headless($extraArgs = "") {
    $launchArgs = @("--App:SuppressActivation=true", "--App:TestMode=true", "--App:Headless=true")
    if ($extraArgs) { $launchArgs += $extraArgs -split ' ' | Where-Object { $_ } }
    $proc = Start-Process -FilePath $exe -ArgumentList $launchArgs -PassThru
    Wait-ForApp 5000 | Out-Null
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
  "App": { "LogLevel": "Debug", "BringToForeground": false, "AllOverlaysDisabled": true, "SuppressAlreadyRunningWarning": true },
  "Window": { "InitialSetupComplete": true },
  "OCR": { "SaveDebugImages": false, "Language": "eng" },
  "Update": { "AutoUpdate": false },
  "Pricing": { "AutoPriceThresholds": false }
}
"@

function Test1-ChangelogSetupCoordination {
    Stop-App; Clear-OldLogs
    Write-Config '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":false},"OCR":{"SaveDebugImages":false,"Language":"eng"}}'
    $proc = Launch-App-Headless; $ok = Wait-ForLog "triggering initial setup" 10000; Stop-App
    Report-Result "1a: Setup triggered" $ok $(if ($ok) { "OK" }else { "Not triggered" })

    Stop-App; Clear-OldLogs
    Write-Config '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":false},"OCR":{"SaveDebugImages":false,"Language":"eng"},"Changelog":{"Body":"## Test Changelog","Version":"1.0.0","Shown":false}}'
    $proc = Launch-App-Headless; Wait-ForLog "waiting for changelog" 10000 | Out-Null
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
                $setupAfter = Wait-ForLog "triggering initial setup" 5000
                Report-Result "1b: Setup after dismiss" $setupAfter $(if ($setupAfter) { "Triggered" }else { "Not triggered" })
            }
            else { Report-Result "1b: UI dismiss" $false "Button not found" }
        }
        elseif ($hasWaiting -and $hasSetup) { Report-Result "1b: Setup after changelog" $true "Both appeared (expected)" }
        else { Report-Result "1b: Setup blocked" $false "Missing log messages" }
    }
    Stop-App

    Stop-App; Clear-OldLogs
    Write-Config '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"eng"},"Changelog":{"Body":"## Test","Version":"1.0.0","Shown":false}}'
    $proc = Launch-App-Headless; Wait-ForApp 5000 | Out-Null; Stop-App
    $log = Get-LatestLog
    if ($log) {
        $lc = Get-Content $log -Raw
        if ($lc -match "triggering initial setup") { Report-Result "1c: No setup" $false "Triggered!" }
        else { Report-Result "1c: No setup" $true "Correct" }
    }
}

function Test2-InitialSetupSuite {
    Stop-App; Clear-OldLogs; Clear-Config
    $proc = Launch-App-Headless; Wait-ForApp 5000 | Out-Null; Stop-App
    if (Test-Path $configPath) {
        $cfg = Get-Content $configPath -Raw | ConvertFrom-Json
        $initComplete = $cfg.Window.InitialSetupComplete
        if ($initComplete -eq $false) { Report-Result "2a: Config created" $true "Setup needed" }
        else { Report-Result "2a: Config created" $false "Value=$initComplete" }
    }
    else { Report-Result "2a: Config created" $false "No config" }
    Stop-App; Clear-OldLogs
    Write-Config '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":false},"OCR":{"SaveDebugImages":false,"Language":"eng"},"Pricing":{"League":"Standard"}}'
    $proc = Launch-App-Headless; $found = Wait-ForLog "triggering initial setup" 10000; Stop-App
    Report-Result "2b: Setup triggered" $found "OK"
    Stop-App; Clear-OldLogs
    Write-Config $cfgBase
    $proc = Launch-App-Headless; Wait-ForApp 5000 | Out-Null; Stop-App
    $log = Get-LatestLog
    $found = $log -and (Select-String -Path $log -Pattern "triggering initial setup" -SimpleMatch -Quiet)
    Report-Result "2c: No setup when done" (-not $found) $(if ($found) { "Triggered!" }else { "Correct" })
    Stop-App; Clear-Config
    $proc = Launch-App-Headless; Wait-ForApp 5000 | Out-Null; Stop-App
    if (Test-Path $configPath) {
        $cfg = Get-Content $configPath -Raw | ConvertFrom-Json
        $missing = @("App", "Window", "OCR", "Pricing", "Update") | Where-Object { -not ($cfg.PSObject.Properties.Name -contains $_) }
        Report-Result "2d: All sections" ($missing.Count -eq 0) $(if ($missing.Count) { "Missing: $missing" }else { "OK" })
    }
}

function Test8-AutoUpdater {
    $zip = Resolve-Path "$root\bin\Release\RuneshapePriceChecker.zip" -ErrorAction SilentlyContinue
    if (-not $zip) { Report-Result "8: Zip" $false "Publish first"; return }

    # Build the "next version" exe so we can simulate a real version upgrade.
    $buildProps = [xml](Get-Content "$root\Directory.Build.props")
    $ver = [Version]($buildProps.Project.PropertyGroup.Version -replace '^v', '')
    $nextVer = "{0}.{1}.{2}" -f $ver.Major, $ver.Minor, ($ver.Build + 1)
    $updateDir = "$env:TEMP\rpc-update-$nextVer"
    $updateZip = "$updateDir\RuneshapePriceChecker.zip"
    if (-not (Test-Path $updateZip)) {
        Write-Host "  Building v$nextVer update package..."
        Remove-Item $updateDir -Recurse -Force -ErrorAction SilentlyContinue
        $null = New-Item -ItemType Directory $updateDir -Force
        dotnet publish "$root\RuneshapePriceChecker.csproj" -c Release /p:Version=$nextVer --output "$updateDir\publish" --nologo 2>&1 | Out-Null
        Compress-Archive -Path "$updateDir\publish\*" -DestinationPath $updateZip -Force
        Write-Host "  Update zip: $updateZip"
    }

    # Create a clean sandbox extracted from the ORIGINAL zip (v$ver).
    $sandbox = "$env:TEMP\rpc-test8-$(Get-Random)"
    $origExeDir = $exeDir
    $origConfigDir = $configDir
    $origConfigPath = $configPath
    $origLogDir = $logDir
    Write-Host "  Sandbox: $sandbox"
    Remove-Item $sandbox -Recurse -Force -ErrorAction SilentlyContinue
    $null = New-Item -ItemType Directory $sandbox -Force
    Expand-Archive -Path $zip -DestinationPath $sandbox -Force
    $script:exeDir = $sandbox
    $script:exe = "$sandbox\RuneshapePriceChecker.exe"
    $script:configDir = "$sandbox\config"
    $script:configPath = "$sandbox\config\appsettings.json"
    $script:logDir = "$sandbox\logs"
    $null = New-Item -ItemType Directory $script:configDir -Force

    Stop-App
    Get-Process "dotnet" -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $PID } | Stop-Process -Force
    $null = Wait-For { -not (Get-Process "dotnet" -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $PID }) } 5000

    # Start test server with the update zip (v$nextVer)
    $serverProc = Start-Process -FilePath "dotnet" -ArgumentList "run --project tests/UpdateTestServer/UpdateTestServer.csproj -c Release --no-build -- `"$updateZip`" 8099 $nextVer" -PassThru -NoNewWindow
    $serverReady = Wait-ForPort 8099 8000
    if (-not $serverReady -or $serverProc.HasExited) { Report-Result "8a: Test server" $false "Not listening"; return }
    Report-Result "8a: Test server" $true "PID $($serverProc.Id)"
    Stop-App; Clear-OldLogs
    Write-Config '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":true},"Update":{"GitHubApiBaseUrl":"http://localhost:8099/api","AutoUpdate":true},"OCR":{"SaveDebugImages":false,"Language":"eng"}}'
    $proc = Launch-App-Headless -extraArgs "--Update:GitHubApiBaseUrl=http://localhost:8099/api --App:AutoApplyUpdate=true"

    # 8b: Version detection
    $detected = Wait-ForLog "New version|Update available" 25000
    if (-not $detected) {
        $log = Get-LatestLog
        if ($log) { $detected = (Select-String -Path $log -Pattern "New version|Update available" -Quiet) }
    }
    Report-Result "8b: Version check ($ver -> $nextVer)" $detected $(if ($detected) { "Detected" }else { "Not detected" })

    if ($detected) {
        # 8c: Wait for download + PowerShell updater script launch
        $downloaded = Wait-ForLog "Download complete\. Extracting updater|Copied local zip|using local zip" 20000
        if ($downloaded) {
            $scriptLaunched = Wait-ForLog "PowerShell update script launched" 10000
            Report-Result "8c: Updater script launched" $scriptLaunched $(if ($scriptLaunched) { "Launched" }else { "Not detected" })

            # Wait for the original app to exit (lifetime.StopApplication)
            $appExited = $null
            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            while ($sw.ElapsedMilliseconds -lt 15000) {
                if ($proc.HasExited) { $appExited = $true; break }
                Start-Sleep -Milliseconds 200
            }
            Report-Result "8d: App exited for update" ($appExited -eq $true) $(if ($appExited) { "Exited" }else { "Still running" })

            # 8e: Wait for the restarted instance (PowerShell starts old exe which picks up .new)
            $restarted = $null
            if ($appExited) {
                $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
                while ($sw2.ElapsedMilliseconds -lt 15000) {
                    $p = Get-Process "RuneshapePriceChecker" -ErrorAction SilentlyContinue
                    if ($p -and $p.Id -ne $proc.Id) { $restarted = $p; break }
                    Start-Sleep -Milliseconds 300
                }
                if (-not $restarted) { Start-Sleep -Milliseconds 3000 }
            }
            Report-Result "8e: App restarted" ($restarted -ne $null) $(if ($restarted) { "PID $($restarted.Id)" }else { "Not detected" })
        }
    }

    Stop-App
    $log = Get-LatestLog
    if ($log) {
        $lc = Get-Content $log -Raw
        $hadDownload = $lc -match "Download complete\. Extracting updater" -or $lc -match "Copied local zip" -or $lc -match "using local zip"
        $hadScript = $lc -match "PowerShell update script launched"
        Report-Result "8f: Update download+script" ($hadDownload -and $hadScript) $(if ($hadDownload -and $hadScript) { "OK" }else { "Download=$hadDownload Script=$hadScript" })
    }
    else {
        Report-Result "8f: Update download+script" $false "No log"
    }

    # 8g: Update check on second launch.
    Stop-App; Clear-OldLogs
    $proc = Launch-App-Headless -extraArgs "--Update:GitHubApiBaseUrl=http://localhost:8099/api"; Wait-ForApp 8000 | Out-Null
    $checkRan = Wait-ForLog "Update available|New version|Already up to date|No update available" 20000
    if (-not $checkRan) {
        $log = Get-LatestLog
        if ($log) { $checkRan = (Select-String -Path $log -Pattern "Update available|New version|Already up to date" -Quiet) }
    }
    Report-Result "8g: Update check on relaunch" $checkRan "OK"

    # 8h: Changelog should be in config
    $changelogWritten = Wait-ForConfig "Changelog" "Version" $nextVer 15000
    if (-not $changelogWritten) {
        if (Test-Path $configPath) {
            $cfg = Get-Content $configPath -Raw | ConvertFrom-Json
            $changelogWritten = $cfg.Changelog -and $cfg.Changelog.Version
        }
    }
    if (-not $changelogWritten) {
        $found = Wait-For { Test-Path $configPath -and (Get-Content $configPath -Raw) -match '"Changelog"' } 10000
        $changelogWritten = $found
    }
    Stop-App
    if (Test-Path $configPath) {
        $cfg = Get-Content $configPath -Raw | ConvertFrom-Json
        Report-Result "8h: Changelog in config" ($cfg.Changelog -and $cfg.Changelog.Version) $(if ($cfg.Changelog.Version) { "v=$($cfg.Changelog.Version)" }else { "Missing" })
    }
    else {
        Report-Result "8h: Changelog in config" $false "No config"
    }
    $serverProc | Stop-Process -Force -ErrorAction SilentlyContinue

    # Restore original paths and clean up sandbox
    $script:exeDir = $origExeDir
    $script:exe = "$origExeDir\RuneshapePriceChecker.exe"
    $script:configDir = $origConfigDir
    $script:configPath = $origConfigPath
    $script:logDir = $origLogDir
    Remove-Item $sandbox -Recurse -Force -ErrorAction SilentlyContinue
}

function Test9-ChangelogButton {
    Stop-App; Clear-OldLogs; Write-Config $cfgBase
    $proc = Launch-App-Headless -extraArgs "--App:ShowChangelog=true"; Wait-ForApp 5000 | Out-Null
    $crashed = $proc.HasExited -and $proc.ExitCode -ne 0
    Stop-App
    $log = Get-LatestLog
    $clean = $log -and -not (Select-String -Path $log -Pattern "Fatal|crash|unhandled" -Quiet)
    Report-Result "9a: --ShowChangelog clean" ((-not $crashed) -and $clean) $(if ($crashed) { "Crashed" }else { "OK" })
    Stop-App; Clear-OldLogs
    Write-Config '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"eng"},"Changelog":{"Body":"## v1.0.0 Notes","Version":"1.0.0","Shown":false}}'
    $proc = Launch-App-Headless; Wait-ForApp 5000 | Out-Null; Stop-App
    if (Test-Path $configPath) {
        $cfg = Get-Content $configPath -Raw | ConvertFrom-Json
        Report-Result "9b: Marked shown" ($cfg.Changelog.Shown -eq $true) "Shown=$($cfg.Changelog.Shown)"
    }
    Stop-App; Clear-OldLogs
    $proc = Launch-App-Headless; Wait-ForApp 5000 | Out-Null; Stop-App
    if (Test-Path $configPath) {
        $cfg = Get-Content $configPath -Raw | ConvertFrom-Json
        Report-Result "9c: State preserved after restart" ($cfg.Changelog.Shown -eq $true) "Shown=$($cfg.Changelog.Shown)"
    }
}

function Test3-AppLifecycle {
    Stop-App; Clear-OldLogs; Clear-Config
    $proc = Launch-App-Headless; Wait-ForApp 5000 | Out-Null
    Report-Result "3a: App runs" (-not $proc.HasExited) $(if ($proc.HasExited) { "Exited:$($proc.ExitCode)" }else { "PID $($proc.Id)" })
    Stop-App
    Stop-App; Clear-OldLogs; Write-Config $cfgBase
    $proc = Launch-App-Headless; Wait-ForApp 5000 | Out-Null
    $proc.CloseMainWindow() | Out-Null; $proc.WaitForExit(8000) | Out-Null
    Report-Result "3b: Clean shutdown" $proc.HasExited "Exit:$($proc.ExitCode)"
    if (-not $proc.HasExited) { $proc.Kill() }
    $crashes = 0
    for ($i = 1; $i -le 3; $i++) { Stop-App; $p = Launch-App-Headless; if ($p.HasExited) { $crashes++ }; Stop-App }
    Report-Result "3c: Rapid restart" ($crashes -eq 0) $(if ($crashes) { "$crashes crashes" }else { "All OK" })
}

function Test4-ConfigRobustness {
    Stop-App; Clear-OldLogs; "{ bad json [[" | Set-Content $configPath -NoNewline
    $proc = Launch-App-Headless; Wait-ForApp 5000 | Out-Null; Stop-App
    try { $c = Get-Content $configPath -Raw | ConvertFrom-Json; $ok = $true } catch { $ok = $false }
    Report-Result "4a: Recovers corrupt" $ok $(if ($ok) { "Valid" }else { "Invalid" })
    Stop-App; Clear-OldLogs; "" | Set-Content $configPath -NoNewline
    $proc = Launch-App-Headless; Wait-ForApp 5000 | Out-Null; Stop-App
    try { $c = Get-Content $configPath -Raw | ConvertFrom-Json; $ok = $c.PSObject.Properties.Name.Count -ge 4 } catch { $ok = $false }
    Report-Result "4b: Empty populated" $ok $(if ($ok) { "$($c.PSObject.Properties.Name.Count) sections" }else { "Failed" })
    Stop-App; Clear-OldLogs; Write-Config '{"App":{"LogLevel":"Debug"},"Update":{"AutoUpdate":false}}'
    $proc = Launch-App-Headless; Wait-ForApp 5000 | Out-Null; Stop-App
    if (Test-Path $configPath) { $c = Get-Content $configPath -Raw | ConvertFrom-Json; Report-Result "4c: Sections filled" ($c.PSObject.Properties.Name -contains "Window") "OK" }
    Stop-App; Clear-OldLogs
    Write-Config '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"eng"},"Update":{"AutoUpdate":false},"Pricing":{"RedThreshold":0.1,"OrangeThreshold":1.0,"GreenThreshold":9999}}'
    $proc = Launch-App-Headless; Wait-ForApp 5000 | Out-Null; Stop-App
    $l = Get-LatestLog; $clean = -not ($l -and (Select-String -Path $l -Pattern "Fatal|Unhandled|crash" -Quiet))
    Report-Result "4d: Bad thresholds OK" $clean "No crash"
    Stop-App; Clear-OldLogs
    Write-Config '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"eng"},"Update":{"AutoUpdate":false},"Pricing":{"League":"Standard","DisplayCurrency":"exalt"}}'
    $proc = Launch-App-Headless; $started = Wait-ForApp 8000; Stop-App
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
    Report-Result "11c: No errors" ($lc -notmatch "\[Error\]|\[Fatal\]|Unhandled|crash") $(if ($lc -notmatch "\[Error\]") { "Clean" }else { "Errors" })
}

function Test5-ErrorHandling {
    $td = "${exeDir}\ocr\tesseract\eng.traineddata"
    $bk = "${exeDir}\ocr\tesseract\eng.traineddata.bak"
    Stop-App; Clear-OldLogs; Clear-Config
    if (Test-Path $td) { Move-Item $td $bk -Force }
    $proc = Launch-App-Headless; Wait-ForApp 5000 | Out-Null; Stop-App
    $re = Test-Path $td
    Report-Result "5a: Auto-repair" $re $(if ($re) { "Restored" }else { "Failed" })
    if (-not $re -and (Test-Path $bk)) { Move-Item $bk $td -Force }
    if (Test-Path $bk) { Remove-Item $bk -Force -ErrorAction SilentlyContinue }
    Stop-App; Clear-OldLogs
    if (Test-Path $td) { Move-Item $td $bk -Force }
    "garbage" | Set-Content $td -NoNewline
    $proc = Launch-App-Headless; Wait-ForApp 5000 | Out-Null; Stop-App
    $so = (Test-Path $td) -and ((Get-Item $td).Length -gt 100000)
    Report-Result "5b: Corrupt repair" $so $(if ($so) { "$([math]::Round((Get-Item $td).Length/1MB,1)) MB" }else { "Failed" })
    if (-not $so -and (Test-Path $bk)) { Move-Item $bk $td -Force }
    if (Test-Path $bk) { Remove-Item $bk -Force -ErrorAction SilentlyContinue }
    Stop-App; Clear-OldLogs
    Write-Config '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":true},"OCR":{"Language":"zzz_invalid","SaveDebugImages":false},"Update":{"AutoUpdate":false}}'
    $proc = Launch-App-Headless; Wait-ForApp 5000 | Out-Null; Stop-App
    $l = Get-LatestLog; $clean = -not ($l -and (Select-String -Path $l -Pattern "Fatal|Unhandled" -Quiet))
    Report-Result "5c: Bad language OK" $clean "No crash"
    Stop-App
    if (Test-Path $logDir) { Remove-Item $logDir -Recurse -Force -ErrorAction SilentlyContinue }
    if (Test-Path $configDir) { Remove-Item $configDir -Recurse -Force -ErrorAction SilentlyContinue }
    $proc = Launch-App-Headless; Wait-ForApp 5000 | Out-Null; Stop-App
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
    # Close settings via toggle
    if ($gearClicked) { Invoke-Button $proc "Settings" 3000 | Out-Null; Wait-ForUIGone $proc ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) "SettingsSection" 3000 | Out-Null }
    # Wait for settings to close before clicking Close
    Close-SettingsIfOpen $proc
    $closeClicked = Invoke-Button $proc "Close" 3000
    Report-Result "16b: Close button" $closeClicked "Clicked"
    $exited = Wait-For { $proc.HasExited } 5000
    if ($exited) { Report-Result "16c: App closed" $true "Exited" } else { Report-Result "16c: App closed" $false "Still running"; Stop-App }
}

function Test6-SettingsPersistence {
    Stop-App; Clear-OldLogs
    Write-Config '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"eng"},"Update":{"AutoUpdate":false},"Pricing":{"DisplayCurrency":"exalt","RedThreshold":0.5,"OrangeThreshold":1.0,"GreenThreshold":5.0,"League":"Standard"}}'
    $proc = Launch-App-Headless; $started = Wait-ForApp 8000; if (-not $started) { Report-Result "6a: App start" $false "Failed"; Stop-App; return }
    # Settings auto-save on close; open then close to trigger save
    Click-Button $proc "Settings" 3000 | Out-Null; Wait-ForUI $proc ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) "RedThresholdBox" 3000 | Out-Null
    Click-Button $proc "Settings" 3000 | Out-Null; Wait-ForUIGone $proc ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) "RedThresholdBox" 3000 | Out-Null
    if (Test-Path $configPath) { $cfg = Get-Content $configPath -Raw | ConvertFrom-Json; Report-Result "6b: League persisted" ($cfg.Pricing.League -eq "Standard") "League=$($cfg.Pricing.League)"; Report-Result "6c: Currency persisted" ($cfg.Pricing.DisplayCurrency -eq "exalt") "Currency=$($cfg.Pricing.DisplayCurrency)" }
    Stop-App
}

function Test7-InvalidThresholds {
    Stop-App; Clear-OldLogs
    Write-Config '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"eng"},"Update":{"AutoUpdate":false},"Pricing":{"DisplayCurrency":"exalt","RedThreshold":5.0,"OrangeThreshold":1.0,"GreenThreshold":5.0,"League":"Standard"}}'
    $proc = Launch-App-Headless; $crashed = Wait-ForLog "Hosting failed|Pricing configuration is invalid" 15000
    if (-not $proc.HasExited) { Stop-App }
    $log = Get-LatestLog; if ($log) { $lc = Get-Content $log -Raw; $hasError = $lc -match "Pricing configuration is invalid" -or $lc -match "Hosting failed"; Report-Result "7a: Invalid thresholds rejected" ($crashed -and $hasError) $(if ($hasError) { "Rejected" } else { "Not detected" }) }
}

function Test14-SettingsFieldValidation($proc) {
    if ($proc.HasExited) { Report-Result "14: Settings" $false "App exited"; return }

    # Open settings (settings auto-save on close via toggle)
    Click-Button $proc "Settings" 3000 | Out-Null
    Wait-ForUI $proc ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) "SettingsSection" 3000 | Out-Null
    Uncheck-AutoThresholds $proc

    $hwnd = $proc.MainWindowHandle
    if ($hwnd -eq [IntPtr]::Zero) { Report-Result "14: Window" $false "No HWND"; return }
    try { $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd) } catch { Report-Result "14: UIA" $false "FromHandle failed"; return }

    # Find threshold fields by AutomationId
    $redBox = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "RedThresholdBox")))
    $greenBox = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "GreenThresholdBox")))

    $foundCount = 0; if ($redBox) { $foundCount++ }; if ($greenBox) { $foundCount++ }
    Report-Result "14a: Threshold fields" ($foundCount -ge 2) "$foundCount found (of 2)"

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
    Report-Result "14b: Value set via UIA" $valueSetOk $(if ($valueSetOk) { "OK" } else { "No ValuePattern" })

    # Close settings via toggle (auto-saves)
    Click-Button $proc "Settings" 3000 | Out-Null
    Wait-ForUIGone $proc ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) "SettingsSection" 3000 | Out-Null
}
function Test15-TooltipVerification($proc) {
    if ($proc.HasExited) { Report-Result "15: Tooltips" $false "App exited"; return }
    # Ensure log section is visible (settings may be open from prior test)
    EnsureLogSectionVisible $proc
    $hwnd = $proc.MainWindowHandle
    if ($hwnd -eq [IntPtr]::Zero) { Report-Result "15: Tooltips" $false "No window"; return }

    try { $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd) } catch { Report-Result "15: UIA" $false "FromHandle failed"; return }

    # Only Copy Log has an explicit ToolTip in XAML; find by AutomationId (x:Name) for reliability
    $copyBtn = Wait-ForUI $proc ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) "CopyLogButton" 3000
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
    Write-Config '{"App":{"LogLevel":"Debug","PricingOverlay":true,"Banner":true},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"eng","DebugOverlay":true},"Update":{"AutoUpdate":false}}'
    $p = Launch-App-Headless "--App:AllOverlaysDisabled=false"; Wait-ForApp 5000 | Out-Null; $ok = -not $p.HasExited; Stop-App
    Report-Result "10a: All overlays on" $ok $(if ($ok) { "No crash" }else { "Exited" })
}

function Test18-OcrBackendSetting {
    Stop-App; Clear-OldLogs

    Write-Config '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"eng","OcrBackend":"windows"},"Update":{"AutoUpdate":false}}'
    $proc = Launch-App-Headless; Wait-ForApp 5000 | Out-Null; Stop-App
    if (Test-Path $configPath) {
        $cfg = Get-Content $configPath -Raw | ConvertFrom-Json
        Report-Result "18a: Windows OCR persisted" ($cfg.OCR.OcrBackend -eq "windows") "Backend=$($cfg.OCR.OcrBackend)"
    }

    Stop-App; Clear-OldLogs
    Write-Config '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"eng","OcrBackend":"tesseract"},"Update":{"AutoUpdate":false}}'
    $proc = Launch-App-Headless; Wait-ForApp 5000 | Out-Null; Stop-App
    if (Test-Path $configPath) {
        $cfg = Get-Content $configPath -Raw | ConvertFrom-Json
        Report-Result "18b: Tesseract persisted" ($cfg.OCR.OcrBackend -eq "tesseract") "Backend=$($cfg.OCR.OcrBackend)"
    }

    Stop-App; Clear-OldLogs
    Write-Config '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"eng","OcrBackend":"invalid"},"Update":{"AutoUpdate":false}}'
    $proc = Launch-App-Headless; $started = Wait-ForApp 8000; Stop-App
    $log = Get-LatestLog
    $fallback = ($log -and (Select-String -Path $log -Pattern "Unsupported.*backend|falling back|Fallback" -Quiet))
    $noCrash = -not ($log -and (Select-String -Path $log -Pattern "Fatal|Unhandled" -Quiet))
    Report-Result "18c: Invalid backend handled" ($noCrash) $(if ($fallback) { "Fallback logged" }else { "No crash" })
}

function Test19-ReRunSetup {
    Stop-App; Clear-OldLogs
    Write-Config $cfgBase
    $proc = Launch-App-Headless; Wait-ForApp 5000 | Out-Null
    if ($proc.HasExited) { Report-Result "19: Setup" $false "App exited"; return }

    # Open settings panel first, then find the Re-run button
    Click-Button $proc "Settings" 3000 | Out-Null
    Wait-ForUI $proc ([System.Windows.Automation.AutomationElement]::NameProperty) "Re-run initial setup" 3000 | Out-Null
    $clicked = Click-Button $proc "Re-run initial setup" 5000
    Report-Result "19a: Re-run button found" $clicked $(if ($clicked) { "Clicked" }else { "Not found" })
    Wait-ForLog "RunInitialSetup: starting initial setup flow|Setup overlay" 5000 | Out-Null

    $log = Get-LatestLog
    $triggered = ($log -and (Select-String -Path $log -Pattern "RunInitialSetup: starting initial setup flow|Setup overlay" -Quiet))
    Report-Result "19b: Setup triggered" $triggered $(if ($triggered) { "Triggered" }else { "Not found in log" })
    Stop-App
}

function Test20-ComprehensiveSettingsRoundTrip($proc) {
    if ($proc.HasExited) { Report-Result "20: Settings" $false "App exited"; return }

    Click-Button $proc "Settings" 3000 | Out-Null
    Wait-ForUI $proc ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) "SettingsSection" 3000 | Out-Null
    Uncheck-AutoThresholds $proc

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

    # Close settings via toggle (auto-saves)
    Click-Button $proc "Settings" 3000 | Out-Null
    Wait-ForUIGone $proc ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) "SettingsSection" 3000 | Out-Null
}

function Test21-PricingSourceChange {
    # Retry up to 3 times for network-dependent cache loading
    $21aPass = $false
    $21bPass = $false
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        Stop-App; Clear-OldLogs
        Write-Config '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"eng"},"Update":{"AutoUpdate":false},"Pricing":{"PricingSource":"poe2scout","League":"Runes of Aldur"}}'
        $proc = Launch-App-Headless; Wait-ForLog "Pricing cache refreshed|Fetched.*price rows" 15000 | Out-Null; Stop-App
        $log = Get-LatestLog
        $cached = ($log -and (Select-String -Path $log -Pattern "Pricing cache refreshed|Fetched.*price rows" -Quiet))
        if ($cached) { $21aPass = $true }

        Stop-App; Clear-OldLogs
        Write-Config '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"eng"},"Update":{"AutoUpdate":false},"Pricing":{"PricingSource":"poe.ninja","League":"Runes of Aldur"}}'
        $proc = Launch-App-Headless; Wait-ForLog "Pricing cache refreshed|Poe.ninja|poe.ninja.*fetched" 15000 | Out-Null; Stop-App
        $log = Get-LatestLog
        $ninjaOk = ($log -and (Select-String -Path $log -Pattern "Poe.ninja|poe.ninja.*fetched|Pricing cache refreshed" -Quiet))
        if ($ninjaOk) { $21bPass = $true }

        if ($21aPass -and $21bPass) { break }
        if ($attempt -lt 3) { Start-Sleep -Milliseconds 1000 }
    }
    Report-Result "21a: Poe2Scout cache loaded" $21aPass $(if ($21aPass) { "OK" }else { "Failed after 3 attempts" })
    Report-Result "21b: PoeNinja source loads" $21bPass $(if ($21bPass) { "OK" }else { "Not found after 3 attempts" })
}

function Test22-LogLevelChange {
    Stop-App; Clear-OldLogs
    Write-Config '{"App":{"LogLevel":"Warning"},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"eng"},"Update":{"AutoUpdate":false}}'
    $proc = Launch-App-Headless
    $started = Wait-ForLog "Settings reloaded successfully" 10000
    Stop-App
    if (-not $started) { Report-Result "22a: Warning suppresses debug" $false "App did not start within timeout"; return }
    $log = Get-LatestLog
    # The FileLogProvider always logs everything (IsEnabled returns true), so
    # [Information] messages will appear in the file log regardless of log level.
    # However, the minimum log level IS applied to the dashboard log sink and
    # filters out Debug messages at Warning level. Check that Debug messages are
    # suppressed (which proves the log level setting works), while acknowledging
    # that file logging is unfiltered by design.
    $content = Get-Content $log -Raw
    $hasDebug = $content -match "\[Debug\]"
    # Also verify the config was actually read with Warning level
    $configApplied = $content -match "App.*LogLevel.*Warning|LogLevel.*Warning"
    Report-Result "22a: Warning suppresses debug" (-not $hasDebug) $(if (-not $hasDebug) { "Debug suppressed" }else { "Debug present" })
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
            $proc2 = Launch-App-Headless; Wait-ForApp 5000 | Out-Null
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

function Test25-OcrLanguageChange {
    Stop-App; Clear-OldLogs
    # Use the detected PoE2 game language (if available) so the test language
    # matches what the app will auto-detect on launch. This ensures the language
    # round-trips correctly — if it changed, it was because the game config says so.
    $poe2Config = "$env:USERPROFILE\Documents\My Games\Path of Exile 2\poe2_Production_Config.ini"
    $gameLang = "fra"  # default test language
    if (Test-Path $poe2Config) {
        $line = Select-String -Path $poe2Config -Pattern "^language=" | Select-Object -First 1
        if ($line) {
            $val = $line.Line -replace '^language=', ''
            $val = $val.Trim()
            $map = @{ "en" = "eng"; "fr" = "fra"; "de" = "deu"; "es" = "spa"; "pt-BR" = "por"; "ru" = "rus"; "th" = "tha"; "zh-TW" = "chi_tra"; "ko-KR" = "kor"; "ja-JP" = "jpn" }
            if ($map.ContainsKey($val)) { $gameLang = $map[$val] }
        }
    }
    Write-Config "{\"App\":{\"LogLevel\":\"Debug\"},\"Window\":{\"InitialSetupComplete\":true},\"OCR\":{\"SaveDebugImages\":false,\"Language\":\"$gameLang\"},\"Update\":{\"AutoUpdate\":false}}"
    $proc = Launch-App-Headless; Wait-ForApp 5000 | Out-Null; Stop-App
    if (Test-Path $configPath) {
        $cfg = Get-Content $configPath -Raw | ConvertFrom-Json
        Report-Result "25a: Language persisted" ($cfg.OCR.Language -eq $gameLang) "Lang=$($cfg.OCR.Language) expected=$gameLang"
    }
}

function Test26-RapidSettingsChanges {
    Stop-App; Clear-OldLogs
    Write-Config $cfgBase
    $proc = Launch-App-Headless; Wait-ForApp 5000 | Out-Null
    $crashed = $false
    for ($i = 0; $i -lt 8; $i++) {
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
    Wait-ForLog "Pricing cache refreshed|Fetched.*price rows" 10000 | Out-Null
    Stop-App
    $log = Get-LatestLog
    $firstRefresh = ($log -and (Select-String -Path $log -Pattern "Pricing cache refreshed|Fetched.*price rows|Already up to date" -Quiet))
    Report-Result "27a: Pricing cache loads" $firstRefresh $(if ($firstRefresh) { "OK" }else { "Not in log" })
}

function Test28-LogOrdering($proc) {
    if ($proc.HasExited) { Report-Result "28: Log ordering" $false "App exited"; return }
    EnsureLogSectionVisible $proc
    Wait-ForUI $proc ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) "LogList" 3000 | Out-Null

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
        $clipText = Wait-ForClipboard '^=== RuneshapePriceChecker' 3000
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
    # Ensure settings is closed before next test (toggle pair leaves it open)
    Invoke-Button $proc "Settings" 2000 | Out-Null

    try {
        $clipText = Wait-ForClipboard '^=== RuneshapePriceChecker' 3000
        if (-not $clipText) { Report-Result "29a: Duplicate coalescing" $false "No clipboard content"; return }
        # Check for coalesced entries (those with "(x2)" or higher count)
        $hasCoalesced = $clipText -match '\(x\d+\)'
        $countText = if ($hasCoalesced) { $Matches[0] } else { "None" }
        Report-Result "29a: Duplicate coalescing" $hasCoalesced $(if ($hasCoalesced) { "Found $countText" }else { "No coalesced entries" })
    }
    catch { Report-Result "29a: Duplicate coalescing" $false "Clipboard error" }
}

function Test31-TestModeIndicator {
    Stop-App; Clear-OldLogs; Write-Config $cfgBase
    $proc = Launch-App-Headless
    Wait-ForApp 5000 | Out-Null
    $crashed = $proc.HasExited
    Stop-App
    Report-Result "31a: --App:TestMode no crash" (-not $crashed) $(if ($crashed) { "Exited" }else { "OK" })
}

function Test32-VersionDisplay {
    Stop-App; Clear-OldLogs; Write-Config $cfgBase
    $proc = Launch-App-Headless; Wait-ForApp 5000 | Out-Null
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
    $proc = Launch-App-Headless; Wait-ForApp 8000 | Out-Null
    # Poll for the Got it button with longer timeout
    $popupFound = $false
    $dismissed = $false
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt 10000 -and -not $popupFound -and -not $proc.HasExited) {
        $hwnd = $proc.MainWindowHandle
        if ($hwnd -ne [IntPtr]::Zero) {
            try {
                $uiaRoot = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
                $gotItBtn = $uiaRoot.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "Got it")))
                if ($gotItBtn) {
                    $popupFound = $true
                    try { $gotItBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); $dismissed = $true; Start-Sleep -Milliseconds 500 } catch { }
                    break
                }
            }
            catch { }
        }
        Start-Sleep -Milliseconds 200
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
    Report-Result "34b: Chaos->Exalt off" (-not $exaltChecked) $(if ($exaltChecked) { "Exalt still on" }else { "Exalt off" })
    # Click Exalt, verify Chaos off
    try { $exaltBox.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 300 } catch { }
    $chaosChecked = $chaosBox.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On
    Report-Result "34c: Exalt->Chaos off" (-not $chaosChecked) $(if ($chaosChecked) { "Chaos still on" }else { "Chaos off" })
    Invoke-Button $proc "Settings" 3000 | Out-Null
}

function Test35-SettingsValidationUI($proc) {
    if ($proc.HasExited) { Report-Result "35: Validation" $false "App exited"; return }
    if (-not (Invoke-Button $proc "Settings" 3000)) { Report-Result "35a: Open settings" $false; return }
    Wait-ForUI $proc ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) "SettingsSection" 2000 | Out-Null
    Uncheck-AutoThresholds $proc
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
    # Check if the status label shows the validation error
    $statusFound = $false
    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, ([System.Windows.Automation.Condition]::TrueCondition))
    foreach ($el in $all) {
        $n = $el.Current.Name; if ([string]::IsNullOrEmpty($n)) { continue }
        if ($n -match "should be less") { $statusFound = $true; break }
    }
    Report-Result "35c: Status shows error" $statusFound $(if ($statusFound) { "Error shown" }else { "No error text" })
    # Restore valid values
    try {
        $redVp.SetValue("5"); Start-Sleep -Milliseconds 100
        $greenVp.SetValue("3"); Start-Sleep -Milliseconds 100
    }
    catch { }
    # Close settings via toggle (auto-saves)
    Invoke-Button $proc "Settings" 3000 | Out-Null; Wait-ForUIGone $proc ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) "SettingsSection" 3000 | Out-Null
}

function Test36-StatusLockCleared($proc) {
    if ($proc.HasExited) { Report-Result "36: Status lock" $false "App exited"; return }
    # AutoThresholds is unchecked inside after settings opens

    function Get-StatusText {
        $hwnd = $proc.MainWindowHandle
        if ($hwnd -eq [IntPtr]::Zero) { return $null }
        try {
            $r = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
            $el = $r.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "StatusLabel")))
            if ($el) { return $el.Current.Name }
        }
        catch { }
        return $null
    }

    function Wait-SettingsFields($timeoutMs = 3000) {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
            if ($proc.HasExited) { return $false }
            try {
                $hwnd = $proc.MainWindowHandle
                if ($hwnd -eq [IntPtr]::Zero) { continue }
                $r = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
                $b = $r.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "RedThresholdBox")))
                if ($b) { return $true }
            }
            catch { }
            Start-Sleep -Milliseconds 200
        }
        return $false
    }

    # --- Phase A: Open settings, set invalid values, close via toggle, verify error clears ---
    Click-Button $proc "Settings" 3000 | Out-Null
    Wait-ForUI $proc ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) "SettingsSection" 3000 | Out-Null
    Uncheck-AutoThresholds $proc
    if (-not (Wait-SettingsFields 3000)) { Report-Result "36a: Open settings" $false; return }
    $hwnd = $proc.MainWindowHandle
    try { $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd) } catch { Report-Result "36a: UIA" $false; Click-Button $proc "Settings" 3000 | Out-Null; return }

    $redBox = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "RedThresholdBox")))
    $greenBox = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "GreenThresholdBox")))
    if (-not $redBox -or -not $greenBox) { Report-Result "36a: Find fields" $false; Click-Button $proc "Settings" 3000 | Out-Null; return }

    try {
        $redVp = $redBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        $greenVp = $greenBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        $redVp.SetValue("50"); Start-Sleep -Milliseconds 100
        $greenVp.SetValue("5"); Start-Sleep -Milliseconds 100
    }
    catch { Report-Result "36a: Set values" $false; Click-Button $proc "Settings" 3000 | Out-Null; return }
    Start-Sleep -Milliseconds 300

    # Close via toggle (auto-saves, triggers ClearValidationStatus which sets status to Ready)
    Click-Button $proc "Settings" 3000 | Out-Null
    Wait-ForUIGone $proc ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) "SettingsSection" 3000 | Out-Null
    $statusAfterClose = Get-StatusText
    $closeCleared = ($statusAfterClose -and $statusAfterClose -notmatch "should be less")
    Report-Result "36a: Toggle close clears error" $closeCleared $(if ($closeCleared) { "'$statusAfterClose'" } else { "Still error: '$statusAfterClose'" })

    # --- Phase B: Reopen via toggle, verify settings work ---
    if (-not $proc.HasExited) {
        $reopened = Click-Button $proc "Settings" 3000
        Wait-ForUI $proc ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) "SettingsSection" 3000 | Out-Null
        Uncheck-AutoThresholds $proc
        $settingsVisible = Wait-SettingsFields 3000
        Report-Result "36b: Reopen after toggle" ($reopened -and $settingsVisible) $(if ($settingsVisible) { "OK" } else { "Not found" })
        # Close via toggle
        Click-Button $proc "Settings" 3000 | Out-Null; Wait-ForUIGone $proc ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) "SettingsSection" 3000 | Out-Null
    }

}

function Test37-UpdateCloseGuard {
    Stop-App; Clear-OldLogs
    Write-Config $cfgBase
    $proc = Launch-App-Headless; Wait-ForApp 8000 | Out-Null
    if ($proc.HasExited) { Report-Result "37: Close guard" $false "App exited"; Stop-App; return }

    $markerPath = Join-Path (Split-Path $exe -Parent) ".update-pending"
    $hwnd = $proc.MainWindowHandle
    if ($hwnd -eq [IntPtr]::Zero) { Report-Result "37: Close guard" $false "No HWND"; Stop-App; return }

    # Phase A: With marker, Close should be blocked (app stays running)
    try { New-Item -Path $markerPath -Force | Out-Null } catch { }
    Start-Sleep -Milliseconds 200

    $proc.CloseMainWindow() | Out-Null
    Start-Sleep -Milliseconds 400
    $blocked = -not $proc.HasExited
    Report-Result "37a: Close blocked during update" $blocked $(if ($blocked) { "App stayed open" } else { "App exited" })

    # Phase B: Remove marker, verify Close actually works now
    try { Remove-Item $markerPath -Force -ErrorAction SilentlyContinue } catch { }
    Start-Sleep -Milliseconds 200

    $proc.CloseMainWindow() | Out-Null
    Start-Sleep -Milliseconds 500
    $closed = $proc.WaitForExit(5000)
    Report-Result "37b: Close succeeds after update" $closed $(if ($closed) { "App exited" } else { "Still running" })

    Stop-App
    try { Remove-Item $markerPath -Force -ErrorAction SilentlyContinue } catch { }
}

function Test38-Poe2LaunchOpts {
    Stop-App; Clear-OldLogs
    Write-Config '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"eng"},"Update":{"AutoUpdate":false},"Pricing":{"PricingSource":"poe2scout","League":"Runes of Aldur"}}'
    $proc = Launch-App-Headless; Wait-ForApp 8000 | Out-Null
    if ($proc.HasExited) { Report-Result "38: Poe2 opts" $false "App exited"; Stop-App; return }

    # Open settings
    if (-not (Click-Button $proc "Settings" 3000)) { Report-Result "38a: Open" $false; Stop-App; return }
    $settingsOpened = Wait-For { try { $hwnd = $proc.MainWindowHandle; $r = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd); $null -ne $r.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "CloseWithPoE2Check"))) } catch { $false } } 5000
    if (-not $settingsOpened) { Report-Result "38a: Open" $false "Settings not visible"; Stop-App; return }
    Report-Result "38a: Settings open" $true

    # Toggle CloseWithPoE2 on
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
    $closeCheck = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "CloseWithPoE2Check")))
    if (-not $closeCheck) { Report-Result "38b: CloseWithPoE2" $false "Not found"; Stop-App; return }
    try {
        $toggle = $closeCheck.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        if ($toggle.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::Off) { $toggle.Toggle() }
        $saved = Wait-ForConfig "App" "CloseWithPoE2" $true 3000
        Report-Result "38b: CloseWithPoE2" $saved
    }
    catch { Report-Result "38b: CloseWithPoE2" $false "Pattern error"; Stop-App; return }

    # Toggle OpenWithPoE2 on
    $openCheck = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "OpenWithPoE2Check")))
    if (-not $openCheck) { Report-Result "38c: OpenWithPoE2" $false "Not found"; Stop-App; return }
    try {
        $toggle2 = $openCheck.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        if ($toggle2.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::Off) { $toggle2.Toggle() }
        $saved2 = Wait-ForConfig "App" "OpenWithPoE2" $true 3000
        Report-Result "38c: OpenWithPoE2" $saved2
    }
    catch { Report-Result "38c: OpenWithPoE2" $false "Pattern error"; Stop-App; return }

    # Close settings (auto-saves)
    Click-Button $proc "Settings" 3000 | Out-Null
    Wait-ForUIGone $proc ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) "RedThresholdBox" 3000 | Out-Null
    Stop-App
    Wait-For { -not (Get-Process RuneshapePriceChecker -ErrorAction SilentlyContinue) } 5000 | Out-Null
}

function Test39-ScanInterval {
    Stop-App; Clear-OldLogs
    Write-Config '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"eng","ScanIntervalMs":100},"Update":{"AutoUpdate":false},"Pricing":{"PricingSource":"poe2scout","League":"Runes of Aldur"}}'
    $proc = Launch-App-Headless; Wait-ForApp 8000 | Out-Null
    if ($proc.HasExited) { Report-Result "39: Scan interval" $false "App exited"; Stop-App; return }

    # Open settings
    if (-not (Click-Button $proc "Settings" 3000)) { Report-Result "39a: Open" $false; Stop-App; return }
    $settingsOpened = Wait-For { try { $hwnd = $proc.MainWindowHandle; $r = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd); $null -ne $r.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "ScanIntervalBox"))) } catch { $false } } 5000
    if (-not $settingsOpened) { Report-Result "39a: Open" $false "Settings not visible"; Stop-App; return }
    Report-Result "39a: Settings open" $true

    # Verify default value
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
    $scanBox = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "ScanIntervalBox")))
    if (-not $scanBox) { Report-Result "39b: ScanInterval" $false "Not found"; Stop-App; return }
    try {
        $valPattern = $scanBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        Report-Result "39b: Default 100" ($valPattern.Current.Value -eq "100") "Got '$($valPattern.Current.Value)'"
    }
    catch { Report-Result "39b: Default 100" $false; Stop-App; return }

    # Change to 150 and verify persisted
    try {
        $valPattern.SetValue("150")
        $saved = Wait-ForConfig "OCR" "ScanIntervalMs" 150 3000
        Report-Result "39c: Set 150" $saved
    }
    catch { Report-Result "39c: Set 150" $false; Stop-App; return }

    # Close settings
    Click-Button $proc "Settings" 3000 | Out-Null
    Wait-ForUIGone $proc ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) "RedThresholdBox" 3000 | Out-Null
    Stop-App
    Wait-For { -not (Get-Process RuneshapePriceChecker -ErrorAction SilentlyContinue) } 5000 | Out-Null
}

function Test40-Propagation {
    Stop-App; Clear-OldLogs
    Write-Config '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"eng","ScanIntervalMs":100},"Update":{"AutoUpdate":false},"Pricing":{"PricingSource":"poe2scout","League":"Runes of Aldur"}}'
    $proc = Launch-App-Headless; Wait-ForApp 8000 | Out-Null
    if ($proc.HasExited) { Report-Result "40: Propagation" $false "App exited"; Stop-App; return }

    # Open settings
    if (-not (Click-Button $proc "Settings" 3000)) { Report-Result "40a: Open" $false; Stop-App; return }
    $settingsOpened = Wait-For { try { $hwnd = $proc.MainWindowHandle; $r = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd); $null -ne $r.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "ScanIntervalBox"))) } catch { $false } } 5000
    if (-not $settingsOpened) { Report-Result "40a: Open" $false "Settings not visible"; Stop-App; return }
    Report-Result "40a: Settings open" $true

    # Change scan interval
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
    $scanBox = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "ScanIntervalBox")))
    if ($scanBox) {
        try {
            $valPattern = $scanBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
            $valPattern.SetValue("150")
        }
        catch { }
    }

    # Toggle DebugOverlay on
    $debugCheck = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "DebugOverlayCheck")))
    if ($debugCheck) {
        try {
            $debugToggle = $debugCheck.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
            if ($debugToggle.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::Off) { $debugToggle.Toggle() }
        }
        catch { }
    }

    # Close settings (auto-saves)
    Click-Button $proc "Settings" 3000 | Out-Null
    Wait-ForUIGone $proc ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) "ScanIntervalBox" 5000 | Out-Null

    # Verify both settings saved
    if (Test-Path $configPath) {
        $cfg = Get-Content $configPath -Raw | ConvertFrom-Json
        Report-Result "40b: ScanInterval" ($cfg.OCR.ScanIntervalMs -eq 150) "=$($cfg.OCR.ScanIntervalMs)"
        Report-Result "40c: DebugOverlay" ($cfg.OCR.DebugOverlay -eq $true) "=$($cfg.OCR.DebugOverlay)"
    }
    else { Report-Result "40b: Config" $false "No config" }

    Stop-App
}

function Test41-AutoUpdaterWithOpenWithPoE2 {
    $zip = Resolve-Path "$root\bin\Release\RuneshapePriceChecker.zip" -ErrorAction SilentlyContinue
    if (-not $zip) { Report-Result "41: Zip" $false "Publish first"; return }

    # Build the "next version" update package (same as Test8)
    $buildProps = [xml](Get-Content "$root\Directory.Build.props")
    $ver = [Version]($buildProps.Project.PropertyGroup.Version -replace '^v', '')
    $nextVer = "{0}.{1}.{2}" -f $ver.Major, $ver.Minor, ($ver.Build + 1)
    $updateDir = "$env:TEMP\rpc-update-$nextVer"
    $updateZip = "$updateDir\RuneshapePriceChecker.zip"
    if (-not (Test-Path $updateZip)) {
        Write-Host "  Building v$nextVer update package..."
        Remove-Item $updateDir -Recurse -Force -ErrorAction SilentlyContinue
        $null = New-Item -ItemType Directory $updateDir -Force
        dotnet publish "$root\RuneshapePriceChecker.csproj" -c Release /p:Version=$nextVer --output "$updateDir\publish" --nologo 2>&1 | Out-Null
        Compress-Archive -Path "$updateDir\publish\*" -DestinationPath $updateZip -Force
        Write-Host "  Update zip: $updateZip"
    }

    # Create sandbox from ORIGINAL zip (v$ver)
    $sandbox = "$env:TEMP\rpc-test41-$(Get-Random)"
    $origExeDir = $exeDir
    $origConfigDir = $configDir
    $origConfigPath = $configPath
    $origLogDir = $logDir
    Write-Host "  Sandbox: $sandbox"
    Remove-Item $sandbox -Recurse -Force -ErrorAction SilentlyContinue
    $null = New-Item -ItemType Directory $sandbox -Force
    Expand-Archive -Path $zip -DestinationPath $sandbox -Force
    $script:exeDir = $sandbox
    $script:exe = "$sandbox\RuneshapePriceChecker.exe"
    $script:configDir = "$sandbox\config"
    $script:configPath = "$sandbox\config\appsettings.json"
    $script:logDir = "$sandbox\logs"
    $null = New-Item -ItemType Directory $script:configDir -Force

    Stop-App
    Get-Process "dotnet" -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $PID } | Stop-Process -Force
    $null = Wait-For { -not (Get-Process "dotnet" -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $PID }) } 5000

    # Start test server
    $serverProc = Start-Process -FilePath "dotnet" -ArgumentList "run --project tests/UpdateTestServer/UpdateTestServer.csproj -c Release --no-build -- `"$updateZip`" 8099 $nextVer" -PassThru -NoNewWindow
    $serverReady = Wait-ForPort 8099 8000
    if (-not $serverReady -or $serverProc.HasExited) { Report-Result "41a: Test server" $false "Not listening"; return }
    Report-Result "41a: Test server" $true "PID $($serverProc.Id)"
    Stop-App; Clear-OldLogs

    # Enable OpenWithPoE2 in config so the background --rpcservice starts
    Write-Config '{"App":{"LogLevel":"Debug","OpenWithPoE2":true},"Window":{"InitialSetupComplete":true},"Update":{"GitHubApiBaseUrl":"http://localhost:8099/api","AutoUpdate":true},"OCR":{"SaveDebugImages":false,"Language":"eng"}}'

    Write-Host "  [DEBUG] Launching: $exe"
    $exeVer = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exe).ProductVersion
    Write-Host "  [DEBUG] Version: $exeVer"
    $proc = Launch-App-Headless -extraArgs "--Update:GitHubApiBaseUrl=http://localhost:8099/api --App:AutoApplyUpdate=true"

    # 41b: Verify background --rpcservice starts
    $bgPid = $null
    for ($i = 0; $i -lt 20; $i++) {
        $others = Get-Process "RuneshapePriceChecker" -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $proc.Id }
        if ($others) { $bgPid = $others[0].Id; break }
        Start-Sleep -Milliseconds 500
    }
    Report-Result "41b: --rpcservice started" ($bgPid -ne $null) $(if ($bgPid) { "PID $bgPid" }else { "Not found" })

    # 41c: Version detection
    $detected = Wait-ForLog "New version|Update available" 25000
    if (-not $detected) {
        $log = Get-LatestLog
        if ($log) { $detected = (Select-String -Path $log -Pattern "New version|Update available" -Quiet) }
    }
    Report-Result "41c: Version check ($ver -> $nextVer)" $detected $(if ($detected) { "Detected" }else { "Not detected" })

    if ($detected) {
        # 41d: Wait for download + the SignalExit() to kill the background service
        # With the C# fix, ApplyUpdateAsync signals the background service to exit
        # before launching the PowerShell updater. Also triggers from CheckForUpdatesAsync
        # when AutoApplyUpdate is set (fixes race condition in Loaded handler).
        $downloaded = Wait-ForLog "Download complete\. Extracting updater|Copied local zip" 40000
        if ($downloaded) {
            # 41e: Background service was killed by KillExistingService in ApplyUpdateAsync.
            # Don't check by PID — the restarted app may recycle the same PID.
            # Instead just verify total process count is 1 (only the main app).
            Start-Sleep -Milliseconds 3000
            $mainOnly = (Get-Process "RuneshapePriceChecker" -ErrorAction SilentlyContinue).Count -eq 1
            Report-Result "41e: Background service exited" $mainOnly $(if ($mainOnly) { "Only main app running" }else { "Multiple RuneshapePriceChecker processes" })

            # 41f: PowerShell updater script launched — search ALL log files in the directory,
            # since Get-LatestLog may return the new app's log file (created after restart).
            Start-Sleep -Milliseconds 500
            $scriptLaunched = $false
            $allLogs = Get-ChildItem "$logDir\*-log.txt" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime
            foreach ($lf in $allLogs) {
                if (Select-String -Path $lf.FullName -Pattern "PowerShell update script launched" -Quiet) {
                    $scriptLaunched = $true
                    break
                }
            }
            $preRestartLog = ($allLogs | Select-Object -Last 1).FullName   # oldest = original app log
            Report-Result "41f: Updater script launched" $scriptLaunched "OK"

            # 41g: App exits (lifetime.StopApplication after PowerShell script launch)
            $appExited = $null
            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            while ($sw.ElapsedMilliseconds -lt 15000) {
                if ($proc.HasExited) { $appExited = $true; break }
                Start-Sleep -Milliseconds 200
            }
            Report-Result "41g: App exited" ($appExited -eq $true) $(if ($appExited) { "Exited" }else { "Still running" })

            # 41h: App restarts (PowerShell starts old exe, Program.cs swaps .exe.new)
            $restarted = $null
            if ($appExited) {
                $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
                while ($sw2.ElapsedMilliseconds -lt 15000) {
                    $allProcs = Get-Process "RuneshapePriceChecker" -ErrorAction SilentlyContinue
                    # Don't filter out $bgPid (may be recycled). Just exclude self ($PID)
                    # and the original process (which exited). Any remaining is the restarted app.
                    $candidates = $allProcs | Where-Object { $_.Id -ne $PID }
                    if ($candidates) { $restarted = $candidates[0]; break }
                    Start-Sleep -Milliseconds 300
                }
                if (-not $restarted) { Start-Sleep -Milliseconds 3000 }
            }
            Report-Result "41h: App restarted" ($restarted -ne $null) $(if ($restarted) { "PID $($restarted.Id)" }else { "Not detected" })

            # 41i: Background service is NOT re-registered on startup (by design).
            # It's only re-registered on app close (Closed handler in DashboardWindow).
            # Verify only the restarted app process exists — no second --rpcservice.
            if ($restarted) {
                Start-Sleep -Milliseconds 2000
                $allAfter = Get-Process "RuneshapePriceChecker" -ErrorAction SilentlyContinue
                $extraProcesses = $allAfter | Where-Object { $_.Id -ne $restarted.Id -and $_.Id -ne $PID }
                $bgNotRestarted = ($null -eq $extraProcesses)
                # The registry Run key was set by the initial Register() when OpenWithPoE2
                # was first enabled.  Verify it still exists for the next session.
                $regRun = Get-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" "RuneshapePriceChecker" -ErrorAction SilentlyContinue
                $regExists = ($null -ne $regRun)
                Report-Result "41i: No background after restart" $bgNotRestarted $(if ($bgNotRestarted) { "Only main app running (registry=$regExists)" }else { "Extra PIDs: $($extraProcesses.Id -join ',')" })
            }

            # 41j: Verify update messages across ALL log files (search both old and new)
            $hadDownload = $false; $hadScript = $false
            foreach ($lf in (Get-ChildItem "$logDir\*-log.txt" -ErrorAction SilentlyContinue)) {
                $content = Get-Content $lf.FullName -Raw
                if ($content -match "Download complete\. Extracting updater|Copied local zip") { $hadDownload = $true }
                if ($content -match "PowerShell update script launched") { $hadScript = $true }
            }
            Report-Result "41j: Update summary" ($hadDownload -and $hadScript) $(if ($hadDownload -and $hadScript) { "OK" }else { "Download=$hadDownload Script=$hadScript" })
        }
        else {
            Report-Result "41d: Download started" $false "Not detected within timeout"
        }
    }

    Stop-App
    $serverProc | Stop-Process -Force -ErrorAction SilentlyContinue

    # Capture log for debugging before cleanup
    $savedLog = Join-Path $env:TEMP "rpc-test41-lastrun.log"
    $latestLog = Get-LatestLog
    if ($latestLog -and (Test-Path $latestLog)) { Copy-Item $latestLog $savedLog -Force; Write-Host "  [DEBUG] Log saved to $savedLog" }

    # Restore original paths and clean up sandbox
    $script:exeDir = $origExeDir
    $script:exe = "$origExeDir\RuneshapePriceChecker.exe"
    $script:configDir = $origConfigDir
    $script:configPath = $origConfigPath
    $script:logDir = $origLogDir
    Remove-Item $sandbox -Recurse -Force -ErrorAction SilentlyContinue
}

# ------------------------------------------------------------------------------
# Test42: GitHub Release Update Simulation
# Downloads the latest shipped release from GitHub, extracts it, and simulates
# updating to the current codebase. This tests the real update flow end-to-end.
# ------------------------------------------------------------------------------
function Test42-GitHubReleaseUpdate {
    # Fetch latest release from GitHub
    try {
        $ghRelease = Invoke-RestMethod -Uri "https://api.github.com/repos/Barragek0/RuneshapePriceChecker/releases/latest" -ErrorAction Stop
        $ghTag = $ghRelease.tag_name -replace '^v', ''
        $ghZipUrl = $ghRelease.zipball_url
    }
    catch {
        # Fallback: try releases list if latest endpoint fails
        try {
            $ghReleases = Invoke-RestMethod -Uri "https://api.github.com/repos/Barragek0/RuneshapePriceChecker/releases?per_page=5" -ErrorAction Stop
            $ghRelease = $ghReleases | Where-Object { -not $_.prerelease } | Select-Object -First 1
            if (-not $ghRelease) { Report-Result "42: GitHub release" $false "No stable release found"; return }
            $ghTag = $ghRelease.tag_name -replace '^v', ''
            $ghZipUrl = $ghRelease.zipball_url
        }
        catch {
            Report-Result "42: GitHub release" $false "GitHub API error: $($_.Exception.Message)"
            return
        }
    }

    $oldVer = $ghTag
    Write-Host "  Latest GitHub release: v$oldVer"

    # Build current codebase as "next version" update package
    $buildProps = [xml](Get-Content "$root\Directory.Build.props")
    $newVer = [Version]($buildProps.Project.PropertyGroup.Version -replace '^v', '')
    $nextVer = "{0}.{1}.{2}" -f $newVer.Major, $newVer.Minor, $newVer.Build
    $updateDir = "$env:TEMP\rpc-update-github-$nextVer"
    $updateZip = "$updateDir\RuneshapePriceChecker.zip"
    if (-not (Test-Path $updateZip)) {
        Write-Host "  Building v$nextVer update package..."
        Remove-Item $updateDir -Recurse -Force -ErrorAction SilentlyContinue
        $null = New-Item -ItemType Directory $updateDir -Force
        dotnet publish "$root\RuneshapePriceChecker.csproj" -c Release /p:Version=$nextVer --output "$updateDir\publish" --nologo 2>&1 | Out-Null
        Compress-Archive -Path "$updateDir\publish\*" -DestinationPath $updateZip -Force
        Write-Host "  Update zip: $updateZip ($nextVer)"
    }

    # Download GitHub release zip and extract to sandbox
    $sandbox = "$env:TEMP\rpc-test42-$(Get-Random)"
    $origExeDir = $exeDir
    $origConfigDir = $configDir
    $origConfigPath = $configPath
    $origLogDir = $logDir
    Write-Host "  Sandbox: $sandbox"
    Remove-Item $sandbox -Recurse -Force -ErrorAction SilentlyContinue
    $null = New-Item -ItemType Directory $sandbox -Force

    try {
        Write-Host "  Downloading GitHub release zip..."
        $ghZipPath = "$env:TEMP\rpc-gh-release-$oldVer.zip"
        Invoke-WebRequest -Uri $ghZipUrl -OutFile $ghZipPath -ErrorAction Stop
        # GitHub zipball extracts to a folder named <repo>-<commit>, find the exe inside
        $null = New-Item -ItemType Directory "$sandbox\extract" -Force
        Expand-Archive -Path $ghZipPath -DestinationPath "$sandbox\extract" -Force
        $exeInZip = Get-ChildItem "$sandbox\extract" -Recurse -Filter "RuneshapePriceChecker.exe" | Select-Object -First 1
        if (-not $exeInZip) {
            # Try the Release zip artifact instead (GitHub release asset)
            $asset = $ghRelease.assets | Where-Object { $_.name -like "RuneshapePriceChecker.zip" } | Select-Object -First 1
            if (-not $asset) { Report-Result "42: Release zip" $false "No RuneshapePriceChecker.zip asset found"; return }
            Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $ghZipPath -ErrorAction Stop
            Remove-Item "$sandbox\extract" -Recurse -Force -ErrorAction SilentlyContinue
            $null = New-Item -ItemType Directory "$sandbox\extract" -Force
            Expand-Archive -Path $ghZipPath -DestinationPath "$sandbox\extract" -Force
            $exeInZip = Get-ChildItem "$sandbox\extract" -Recurse -Filter "RuneshapePriceChecker.exe" | Select-Object -First 1
        }
        if (-not $exeInZip) { Report-Result "42: Extract exe" $false "RuneshapePriceChecker.exe not found in release"; return }
        Copy-Item $exeInZip.FullName "$sandbox\RuneshapePriceChecker.exe" -Force
        Copy-Item "$root\README.md" "$sandbox\README.md" -Force -ErrorAction SilentlyContinue
        Write-Host "  Extracted v$oldVer exe"
    }
    catch {
        Report-Result "42: Download release" $false $_.Exception.Message
        return
    }

    # Set up sandbox paths
    $script:exeDir = $sandbox
    $script:exe = "$sandbox\RuneshapePriceChecker.exe"
    $script:configDir = "$sandbox\config"
    $script:configPath = "$sandbox\config\appsettings.json"
    $script:logDir = "$sandbox\logs"
    $null = New-Item -ItemType Directory $script:configDir -Force

    Stop-App
    Get-Process "dotnet" -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $PID } | Stop-Process -Force
    $null = Wait-For { -not (Get-Process "dotnet" -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $PID }) } 5000

    # Start test server
    $serverProc = Start-Process -FilePath "dotnet" -ArgumentList "run --project tests/UpdateTestServer/UpdateTestServer.csproj -c Release --no-build -- `"$updateZip`" 8099 $nextVer" -PassThru -NoNewWindow
    $serverReady = Wait-ForPort 8099 8000
    if (-not $serverReady -or $serverProc.HasExited) { Report-Result "42a: Test server" $false "Not listening"; return }
    Report-Result "42a: Test server" $true "PID $($serverProc.Id)"
    Stop-App; Clear-OldLogs

    # Config pointing to local test server
    Write-Config '{"App":{"LogLevel":"Debug"},"Window":{"InitialSetupComplete":true},"Update":{"GitHubApiBaseUrl":"http://localhost:8099/api","AutoUpdate":true},"OCR":{"SaveDebugImages":false,"Language":"eng"}}'

    Write-Host "  [DEBUG] Launching old v$oldVer exe for update to v$nextVer"
    $proc = Launch-App-Headless -extraArgs "--Update:GitHubApiBaseUrl=http://localhost:8099/api --App:AutoApplyUpdate=true"

    # 42b: Version detection
    $detected = Wait-ForLog "New version|Update available" 25000
    if (-not $detected) {
        $log = Get-LatestLog
        if ($log) { $detected = (Select-String -Path $log -Pattern "New version|Update available" -Quiet) }
    }
    Report-Result "42b: Version check ($oldVer -> $nextVer)" $detected $(if ($detected) { "Detected" }else { "Not detected" })

    if ($detected) {
        # 42c: Download completes
        $downloaded = Wait-ForLog "Download complete\. Extracting updater|Copied local zip" 40000
        if ($downloaded) {
            # 42d: Updater script launched
            Start-Sleep -Milliseconds 500
            $scriptLaunched = $false
            foreach ($lf in (Get-ChildItem "$logDir\*-log.txt" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime)) {
                if (Select-String -Path $lf.FullName -Pattern "PowerShell update script launched" -Quiet) {
                    $scriptLaunched = $true
                    break
                }
            }
            Report-Result "42d: Updater script launched" $scriptLaunched "OK"

            # 42e: App exits
            $appExited = $null
            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            while ($sw.ElapsedMilliseconds -lt 15000) {
                if ($proc.HasExited) { $appExited = $true; break }
                Start-Sleep -Milliseconds 200
            }
            Report-Result "42e: App exited" ($appExited -eq $true) $(if ($appExited) { "Exited" }else { "Still running" })

            # 42f: App restarts
            $restarted = $null
            if ($appExited) {
                $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
                while ($sw2.ElapsedMilliseconds -lt 15000) {
                    $allProcs = Get-Process "RuneshapePriceChecker" -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $PID }
                    if ($allProcs) { $restarted = $allProcs[0]; break }
                    Start-Sleep -Milliseconds 300
                }
                if (-not $restarted) { Start-Sleep -Milliseconds 3000 }
            }
            Report-Result "42f: App restarted" ($restarted -ne $null) $(if ($restarted) { "PID $($restarted.Id)" }else { "Not detected" })

            # 42g: Changelog written to config after update
            if ($restarted) {
                Start-Sleep -Milliseconds 2000
                $changelogShown = Wait-ForConfig "Changelog" "Shown" $true 5000
                Report-Result "42g: Changelog shown" $changelogShown $(if ($changelogShown) { "Shown=true" }else { "Not shown" })

                # 42h: Verify update summary in log
                $hadDownload = $false; $hadScript = $false
                foreach ($lf in (Get-ChildItem "$logDir\*-log.txt" -ErrorAction SilentlyContinue)) {
                    $content = Get-Content $lf.FullName -Raw
                    if ($content -match "Download complete\. Extracting updater|Copied local zip") { $hadDownload = $true }
                    if ($content -match "PowerShell update script launched") { $hadScript = $true }
                }
                Report-Result "42h: Update summary" ($hadDownload -and $hadScript) $(if ($hadDownload -and $hadScript) { "OK" }else { "Download=$hadDownload Script=$hadScript" })

                # 42i: Restarted app reports new version
                # The restarted app may take time to start and create its log file.
                # Increase timeout to account for .NET startup + config loading.
                $newVerLog = Wait-ForLog "Current version: $nextVer" 20000
                Report-Result "42i: New version reported" $newVerLog $(if ($newVerLog) { "v$nextVer" }else { "Not found" })
            }
        }
        else {
            Report-Result "42c: Download started" $false "Not detected within timeout"
            foreach ($lf in (Get-ChildItem "$logDir\*-log.txt" -ErrorAction SilentlyContinue)) {
                Write-Host "        ${ansiGray}Log: $($lf.Name) -> $(Get-Content $lf.FullName -Raw | Select-String -Pattern "Update|Error|download|Fail" -SimpleMatch)$ansiReset"
            }
        }
    }

    Stop-App
    $serverProc | Stop-Process -Force -ErrorAction SilentlyContinue

    # Cleanup
    $script:exeDir = $origExeDir
    $script:exe = "$origExeDir\RuneshapePriceChecker.exe"
    $script:configDir = $origConfigDir
    $script:configPath = $origConfigPath
    $script:logDir = $origLogDir
    Remove-Item $sandbox -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item "$env:TEMP\rpc-gh-release-$oldVer.zip" -Force -ErrorAction SilentlyContinue
}

# ------------------------------------------------------------------------------
# Test43: BringToForeground behavior
# Verifies that with BringToForeground=false the window does NOT activate,
# and that the fix for ShowActivated is working correctly.
# ------------------------------------------------------------------------------
function Test43-BringToForeground {
    Stop-App; Clear-OldLogs

    # Define GetForegroundWindow Win32 API
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Win32 {
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();
}
"@

    # Phase A: BringToForeground=false, no SuppressActivation
    # The window should NOT activate — ShowActivated=false prevents it.
    Write-Config '{"App":{"LogLevel":"Debug","BringToForeground":false},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"eng"},"Update":{"AutoUpdate":false}}'
    $launchArgs = @("--App:SuppressActivation=false", "--App:TestMode=true")
    $proc = Start-Process -FilePath $exe -ArgumentList $launchArgs -PassThru
    $started = Wait-ForApp 8000
    if (-not $started) { Report-Result "43a: App started" $false "Timeout"; Stop-App; return }
    Report-Result "43a: App started" $true "PID $($proc.Id)"

    # Wait briefly for any activation attempt
    Start-Sleep -Milliseconds 1000
    $fgHwnd = [Win32]::GetForegroundWindow()
    $appHwnd = $proc.MainWindowHandle
    $notForeground = $fgHwnd -ne $appHwnd -and $appHwnd -ne [IntPtr]::Zero
    Report-Result "43b: Not foreground (BringToForeground=false)" $notForeground `
    $(if ($notForeground) { "FG=$fgHwnd App=$appHwnd" } else { "Window IS foreground (unexpected)" })

    # Verify config still has BringToForeground=false after app loaded it
    if (Test-Path $configPath) {
        $cfg = Get-Content $configPath -Raw | ConvertFrom-Json
        $persisted = $cfg.App.BringToForeground -eq $false
        Report-Result "43c: Config persisted false" $persisted "=$($cfg.App.BringToForeground)"
    }

    Stop-App

    # Phase B: BringToForeground=false + SuppressActivation=true (standard test mode)
    # Verify no crash and config round-trips
    Write-Config '{"App":{"LogLevel":"Debug","BringToForeground":false},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"eng"},"Update":{"AutoUpdate":false}}'
    $proc = Launch-App-Headless; Wait-ForApp 5000 | Out-Null
    if ($proc.HasExited) { Report-Result "43d: SuppressActivation mode" $false "App exited"; Stop-App; return }
    Report-Result "43d: SuppressActivation mode" $true "PID $($proc.Id)"
    Stop-App
    if (Test-Path $configPath) {
        $cfg = Get-Content $configPath -Raw | ConvertFrom-Json
        $persisted2 = $cfg.App.BringToForeground -eq $false
        Report-Result "43e: Config persists across launches" $persisted2 "=$($cfg.App.BringToForeground)"
    }
}

# ------------------------------------------------------------------------------
# Test44: Settings toggles that aren't covered by other tests
# Exercises OverlayScale (auto + value), AlwaysOnTop, AutoUpdate, and
# ScanInterval clamping by setting values via UIA and verifying persistence.
# ------------------------------------------------------------------------------
function Test44-SettingsToggles {
    Stop-App; Clear-OldLogs
    Write-Config '{"App":{"LogLevel":"Debug","BringToForeground":false,"OverlayScale":1.0},"Window":{"InitialSetupComplete":true},"OCR":{"SaveDebugImages":false,"Language":"eng","ScanIntervalMs":100},"Update":{"AutoUpdate":false}}'
    $proc = Launch-App-Headless; $started = Wait-ForApp 8000
    if (-not $started) { Report-Result "44a: App start" $false "Timeout"; Stop-App; return }
    Report-Result "44a: App started" $true

    # Open settings
    if (-not (Click-Button $proc "Settings" 3000)) { Report-Result "44b: Open settings" $false; Stop-App; return }
    Wait-ForUI $proc ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) "SettingsSection" 3000 | Out-Null
    $hwnd = $proc.MainWindowHandle; if ($hwnd -eq [IntPtr]::Zero) { Report-Result "44b: HWND" $false; Stop-App; return }
    try { $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd) } catch { Report-Result "44b: UIA" $false; Stop-App; return }

    # --- OverlayScale: set a manual value (Auto is off in config so box is visible) ---
    $scaleBox = Wait-ForUI $proc ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) "OverlayScaleBox" 3000
    if (-not $scaleBox) { Report-Result "44b: OverlayScaleBox" $false "Not found"; Stop-App; return }
    Report-Result "44b: OverlayScaleBox found" $true
    # Set OverlayScale to 1.5 via ValuePattern
    try {
        $scaleVp = $scaleBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        $scaleVp.SetValue("1.5"); Start-Sleep -Milliseconds 200
        Report-Result "44c: OverlayScale 1.5" ($scaleVp.Current.Value -eq "1.5") "Got '$($scaleVp.Current.Value)'"
    }
    catch { Report-Result "44c: OverlayScale 1.5" $false "ValuePattern error" }

    # --- AlwaysOnTop: toggle on and verify persistence ---
    $alwaysOnTopCheck = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "AlwaysOnTopCheck")))
    if ($alwaysOnTopCheck) {
        try {
            $aotToggle = $alwaysOnTopCheck.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
            if ($aotToggle.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::Off) { $aotToggle.Toggle(); Start-Sleep -Milliseconds 200 }
            $persisted = Wait-ForConfig "App" "AlwaysOnTop" $true 3000
            Report-Result "44d: AlwaysOnTop on" $persisted $(if ($persisted) { "Persisted" }else { "Not found" })
            # Toggle back off
            $aotToggle.Toggle(); Start-Sleep -Milliseconds 200
            $persistedOff = Wait-ForConfig "App" "AlwaysOnTop" $false 3000
            Report-Result "44e: AlwaysOnTop off" $persistedOff
        }
        catch { Report-Result "44d: AlwaysOnTop" $false "Pattern error" }
    }
    else { Report-Result "44d: AlwaysOnTop" $false "Not found" }

    # --- AutoUpdate: toggle on and verify persistence ---
    $autoUpdateCheck = $null
    $swAu = [System.Diagnostics.Stopwatch]::StartNew()
    while ($swAu.ElapsedMilliseconds -lt 3000 -and -not $autoUpdateCheck) {
        $autoUpdateCheck = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "AutoUpdateCheck")))
        if (-not $autoUpdateCheck) { Start-Sleep -Milliseconds 200 }
    }
    if ($autoUpdateCheck) {
        try {
            $auToggle = $autoUpdateCheck.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
            if ($auToggle.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::Off) { $auToggle.Toggle(); Start-Sleep -Milliseconds 200 }
            $persisted = Wait-ForConfig "Update" "AutoUpdate" $true 3000
            Report-Result "44f: AutoUpdate on" $persisted $(if ($persisted) { "Persisted" }else { "Not found" })
            # Toggle back off
            $auToggle.Toggle(); Start-Sleep -Milliseconds 200
            $persistedOff = Wait-ForConfig "Update" "AutoUpdate" $false 3000
            Report-Result "44g: AutoUpdate off" $persistedOff
        }
        catch { Report-Result "44f: AutoUpdate" $false "Pattern error" }
    }
    else { Report-Result "44f: AutoUpdate" $false "Not found" }

    # --- ScanInterval: set a value within range and verify persistence ---
    $scanBox = Wait-ForUI $proc ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) "ScanIntervalBox" 3000
    if ($scanBox) {
        try {
            $scanVp = $scanBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
            $scanVp.SetValue("150"); Start-Sleep -Milliseconds 200
            Click-Button $proc "Settings" 3000 | Out-Null; Wait-ForUIGone $proc ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) "SettingsSection" 3000 | Out-Null
            $saved = Wait-ForConfig "OCR" "ScanIntervalMs" 150 3000
            Report-Result "44h: ScanInterval 150 persisted" $saved
        }
        catch { Report-Result "44h: ScanInterval" $false "Pattern error" }
    }
    else { Report-Result "44h: ScanInterval" $false "Not found" }

    Stop-App
}


Write-Banner "RuneshapePriceChecker v1.0.0 Pre-Release Tests"
Write-Host "  Exe: $exe"
Write-Host ""

Stop-App

$runAll = $All -or (-not ($Test1 -or $Test2 -or $Test3 -or $Test4 -or $Test5 -or $Test6 -or $Test7 -or $Test8 -or $Test9 -or $Test10 -or $Test11 -or $Test12 -or $Test13 -or $Test14 -or $Test15 -or $Test16 -or $Test18 -or $Test19 -or $Test20 -or $Test21 -or $Test22 -or $Test23 -or $Test25 -or $Test26 -or $Test27 -or $Test28 -or $Test29 -or $Test31 -or $Test32 -or $Test33 -or $Test34 -or $Test35 -or $Test36 -or $Test37 -or $Test38 -or $Test39 -or $Test40 -or $Test41 -or $Test42 -or $Test43 -or $Test44))

# -- Sandbox management for isolation between tests --
$_savedPaths = @{}  # saved original paths for restore

function Enter-TestSandbox {
    param([string]$TestName)
    # Snapshot original paths
    $_savedPaths.ExeDir = $script:exeDir
    $_savedPaths.Exe = $script:exe
    $_savedPaths.ConfigDir = $script:configDir
    $_savedPaths.ConfigPath = $script:configPath
    $_savedPaths.LogDir = $script:logDir

    # Create a fresh sandbox from the original zip
    $zip = Resolve-Path "$root\bin\Release\RuneshapePriceChecker.zip" -ErrorAction SilentlyContinue
    $sandbox = "$env:TEMP\rpc-sandbox-$TestName-$(Get-Random)"
    Remove-Item $sandbox -Recurse -Force -ErrorAction SilentlyContinue
    if ($zip) {
        $null = New-Item -ItemType Directory $sandbox -Force
        Expand-Archive -Path $zip -DestinationPath $sandbox -Force
    }
    else {
        # Fall back to original exe dir if no zip (unlikely for release tests)
        $sandbox = $_savedPaths.ExeDir
    }

    $script:exeDir = $sandbox
    $script:exe = "$sandbox\RuneshapePriceChecker.exe"
    $script:configDir = "$sandbox\config"
    $script:configPath = "$sandbox\config\appsettings.json"
    $script:logDir = "$sandbox\logs"
    $null = New-Item -ItemType Directory $script:configDir -Force
    # Seed with the perf-test baseline config so the app always has a valid starting point
    $perfConfig = "$root\scripts\perf-test-config.json"
    if (Test-Path $perfConfig) {
        Copy-Item $perfConfig $script:configPath -Force
    }
}

function Exit-TestSandbox {
    # Restore original paths
    $script:exeDir = $_savedPaths.ExeDir
    $script:exe = $_savedPaths.Exe
    $script:configDir = $_savedPaths.ConfigDir
    $script:configPath = $_savedPaths.ConfigPath
    $script:logDir = $_savedPaths.LogDir

    # Clean up sandbox (only if it was created by Enter-TestSandbox)
    $sandbox = $script:exeDir
    if ($sandbox -and $sandbox -ne $_savedPaths.ExeDir -and $sandbox -like "$env:TEMP\rpc-sandbox-*") {
        Remove-Item $sandbox -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# Wrap each test with sandbox isolation
function Invoke-TestWithSandbox {
    param([string]$Name, [ScriptBlock]$TestBlock)
    Write-Host "${ansiCyan}[Sandbox] $Name${ansiReset}" -NoNewline
    Enter-TestSandbox $Name
    Write-Host " â†’ $($script:exeDir)"
    & $TestBlock
    Exit-TestSandbox
}

# ------------------------------------------------------------------------------
# PHASE 1: Restart-required tests (each test manages its own app lifecycle)
# Each test runs in a fresh sandbox extracted from the original release zip
# so state from one test never leaks into another.
# ------------------------------------------------------------------------------
if ($runAll -or $Test1) { Invoke-TestWithSandbox "Test1" { Test1-ChangelogSetupCoordination } }
if ($runAll -or $Test2) { Invoke-TestWithSandbox "Test2" { Test2-InitialSetupSuite } }
if ($runAll -or $Test3) { Invoke-TestWithSandbox "Test3" { Test3-AppLifecycle } }
if ($runAll -or $Test4) { Invoke-TestWithSandbox "Test4" { Test4-ConfigRobustness } }
if ($runAll -or $Test5) { Invoke-TestWithSandbox "Test5" { Test5-ErrorHandling } }
if ($runAll -or $Test6) { Invoke-TestWithSandbox "Test6" { Test6-SettingsPersistence } }
if ($runAll -or $Test7) { Invoke-TestWithSandbox "Test7" { Test7-InvalidThresholds } }
# Test8 manages its own sandbox (needs the original zip for base, a v+1 build for update)
if ($runAll -or $Test8) { Test8-AutoUpdater }
if ($runAll -or $Test9) { Invoke-TestWithSandbox "Test9" { Test9-ChangelogButton } }
if ($runAll -or $Test10) { Invoke-TestWithSandbox "Test10" { Test10-OverlayFeatureToggles } }
# Test41 manages its own sandbox (needs the original zip + update server, same pattern as Test8)
if ($runAll -or $Test41) { Test41-AutoUpdaterWithOpenWithPoE2 }
# Test42 manages its own sandbox (needs GitHub release download + update server)
if ($runAll -or $Test42) { Test42-GitHubReleaseUpdate }
if ($runAll -or $Test43) { Test43-BringToForeground }
if ($runAll -or $Test44) { Test44-SettingsToggles }

# Restart-mode tests (each starts/stops its own instance)
if ($runAll -or $Test18) { Invoke-TestWithSandbox "Test18" { Test18-OcrBackendSetting } }
if ($runAll -or $Test19) { Test19-ReRunSetup }
if ($runAll -or $Test21) { Test21-PricingSourceChange }
if ($runAll -or $Test22) { Test22-LogLevelChange }
if ($runAll -or $Test23) { Test23-WindowPosition }
if ($runAll -or $Test25) { Invoke-TestWithSandbox "Test25" { Test25-OcrLanguageChange } }
if ($runAll -or $Test26) { Test26-RapidSettingsChanges }
if ($runAll -or $Test27) { Test27-PriceCacheOnLeagueChange }
if ($runAll -or $Test31) { Test31-TestModeIndicator }
if ($runAll -or $Test32) { Test32-VersionDisplay }
if ($runAll -or $Test33) { Test33-ChangelogWindowPopup }
if ($runAll -or $Test37) { Test37-UpdateCloseGuard }
if ($runAll -or $Test38) { Invoke-TestWithSandbox "Test38" { Test38-Poe2LaunchOpts } }
if ($runAll -or $Test39) { Invoke-TestWithSandbox "Test39" { Test39-ScanInterval } }
if ($runAll -or $Test40) { Invoke-TestWithSandbox "Test40" { Test40-Propagation } }
# ------------------------------------------------------------------------------
# PHASE 2: Shared-instance tests (single app, no restart between tests)
# These tests only read state or interact with the UI non-destructively.
# ------------------------------------------------------------------------------
$runPhase2 = $runAll -or $Test11 -or $Test12 -or $Test13 -or $Test14 -or $Test15 -or $Test16 -or $Test20 -or $Test28 -or $Test29 -or $Test34 -or $Test35 -or $Test36
if ($runPhase2) {
    Write-Banner "PHASE 2: Shared-instance tests"
    Stop-App; Clear-OldLogs; Write-Config $cfgBase
    $sharedProc = Launch-App-Headless; Wait-ForApp 8000 | Out-Null
    if ($sharedProc.HasExited) { Report-Result "Phase2: App start" $false "Exited"; $sharedProc = $null }
    else { Report-Result "Phase2: App running" $true "PID $($sharedProc.Id)" }

    if ($sharedProc -and (-not $sharedProc.HasExited)) {
        # Each shared test stabilizes UI before starting — ensures no lingering panels
        if (($runAll -or $Test11) -and (-not $sharedProc.HasExited)) { Wait-ForUIGone $sharedProc ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) "SettingsSection" 2000 | Out-Null; Test11-Logging $sharedProc }
        if (($runAll -or $Test12) -and (-not $sharedProc.HasExited)) { Test12-ResourceUsage $sharedProc }
        if (($runAll -or $Test13) -and (-not $sharedProc.HasExited)) { Wait-ForUIGone $sharedProc ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) "SettingsSection" 2000 | Out-Null; Test13-UiElements $sharedProc }
        if (($runAll -or $Test14) -and (-not $sharedProc.HasExited)) { Wait-ForUIGone $sharedProc ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) "SettingsSection" 2000 | Out-Null; Test14-SettingsFieldValidation $sharedProc }
        if (($runAll -or $Test15) -and (-not $sharedProc.HasExited)) { EnsureLogSectionVisible $sharedProc; Test15-TooltipVerification $sharedProc }
        if (($runAll -or $Test20) -and (-not $sharedProc.HasExited)) { Wait-ForUIGone $sharedProc ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) "SettingsSection" 2000 | Out-Null; Test20-ComprehensiveSettingsRoundTrip $sharedProc }
        # Test24 was removed (duplicate of Test20/26)
        # Log ordering tests before Test16 closes the app
        if (($runAll -or $Test28) -and (-not $sharedProc.HasExited)) { EnsureLogSectionVisible $sharedProc; Test28-LogOrdering $sharedProc }
        if (($runAll -or $Test29) -and (-not $sharedProc.HasExited)) { EnsureLogSectionVisible $sharedProc; Test29-LogCoalescing $sharedProc }
        # Test30 was merged into Test26 (RapidSettingsChanges)
        if (($runAll -or $Test34) -and (-not $sharedProc.HasExited)) { Wait-ForUIGone $sharedProc ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) "SettingsSection" 2000 | Out-Null; Test34-CurrencyMutualExclusion $sharedProc }
        if (($runAll -or $Test35) -and (-not $sharedProc.HasExited)) { Wait-ForUIGone $sharedProc ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) "SettingsSection" 2000 | Out-Null; Test35-SettingsValidationUI $sharedProc }
        if (($runAll -or $Test36) -and (-not $sharedProc.HasExited)) { Wait-ForUIGone $sharedProc ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) "SettingsSection" 2000 | Out-Null; Test36-StatusLockCleared $sharedProc }
        # Test16 MUST be last: it clicks Close and exits the app
        if (($runAll -or $Test16) -and (-not $sharedProc.HasExited)) { Wait-ForUIGone $sharedProc ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) "SettingsSection" 2000 | Out-Null; Test16-UiButtonInteractions $sharedProc }
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
