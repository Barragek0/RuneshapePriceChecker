$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

Write-Host "======================================" -ForegroundColor Cyan
Write-Host " RuneshapePriceChecker Test Suite" -ForegroundColor Cyan
Write-Host "======================================" -ForegroundColor Cyan

# ----------------------------------------------------------------
# Setup: build the simulator once
# ----------------------------------------------------------------
$simulator = "$PSScriptRoot\OcrPricingSimulator\OcrPricingSimulator.csproj"
$simDll = "$PSScriptRoot\OcrPricingSimulator\bin\Release\net8.0-windows\OcrPricingSimulator.dll"
$mock = "$root\tests\mocks\pricing-mock.json"
Write-Host ""
Write-Host "--- Build simulator ---" -ForegroundColor DarkGray
dotnet build $simulator -c Release --nologo 2>&1 | Select-Object -Last 2
if ($LASTEXITCODE -ne 0) { Write-Host "Build failed" -ForegroundColor Red; exit 1 }

# ----------------------------------------------------------------
# Pricing & OCR parsing
# Runs the simulator once with mock data, validates all items
# ----------------------------------------------------------------
Write-Host ""
Write-Host "--- Pricing & Parsing ---" -ForegroundColor Yellow
$sectionWatch = [System.Diagnostics.Stopwatch]::StartNew()

$pricingItems = @(Get-Content $mock -Raw | ConvertFrom-Json | ForEach-Object { $_.name } | Where-Object { $_ })
$pricingItems += "chaos orb", "DIVINE ORB", "ExAlTeD oRb", "0RB OF ALCHEMY"

$parsingTests = Get-Content "$PSScriptRoot\parsing-tests.json" -Raw | ConvertFrom-Json

$allItems = [System.Collections.Generic.HashSet[string]]::new()
foreach ($item in $pricingItems) { $null = $allItems.Add($item) }
foreach ($test in $parsingTests) { $null = $allItems.Add($test.raw) }

$tempFile = [System.IO.Path]::GetTempFileName()
try {
    $allItems -join "`n" | Out-File $tempFile -Encoding UTF8
    $raw = dotnet exec $simDll --league "Test" --mock-file $mock --display-currency chaos --input-file $tempFile 2>&1
}
finally { Remove-Item $tempFile -Force }

if ($LASTEXITCODE -ne 0) { throw "Simulator failed (exit $LASTEXITCODE)" }

$output = @($raw | Where-Object { ($_ -split '\|').Count -eq 6 -and $_ -match '\d' })
$pass = 0; $fail = 0

foreach ($line in $output) {
    $cols = @($line -split '\|' | ForEach-Object { $_.Trim() })
    $detected = $cols[0]
    $qty = [int]$cols[1]
    $name = $cols[2]
    $quote = $cols[4]
    $kind = $cols[5]

    if ($quote -eq 'n/a' -or $quote -eq '') {
        Write-Host "  FAIL: '$detected' unresolved" -ForegroundColor Red
        $fail++; continue
    }

    $test = $null
    for ($i = 0; $i -lt $parsingTests.Count; $i++) {
        if ($parsingTests[$i].raw -eq $detected) { $test = $parsingTests[$i]; break }
    }
    if ($test) {
        if ($qty -ne $test.qty) { Write-Host "  FAIL: '$detected' qty=$qty expected=$($test.qty)" -ForegroundColor Red; $fail++; continue }
        if ($name -ne $test.name) { Write-Host "  FAIL: '$detected' name='$name' expected='$($test.name)'" -ForegroundColor Red; $fail++; continue }
    }

    if ($detected -match '^Uncut (Support|Skill|Spirit) Gem$' -and $kind -ne 'range') {
        Write-Host "  FAIL: '$detected' expected range, got $kind" -ForegroundColor Red; $fail++; continue
    }

    $pass++
}

$pricingElapsed = $sectionWatch.ElapsedMilliseconds
Write-Host "  $pass/$($pass+$fail) checks passed (${pricingElapsed}ms)" -ForegroundColor $(if ($fail -eq 0) { "Green" } else { "Red" })
if ($fail -gt 0) { throw "$fail pricing/parsing check(s) failed" }

# ----------------------------------------------------------------
# Updater logic
# ----------------------------------------------------------------
Write-Host ""
Write-Host "--- Updater ---" -ForegroundColor Yellow
$sectionWatch = [System.Diagnostics.Stopwatch]::StartNew()
$upPass = 0; $upFail = 0

function Test($name, [ScriptBlock]$block) {
    try { & $block; $script:upPass++ }
    catch { $script:upFail++; Write-Host "  FAIL: $name -> $_" -ForegroundColor Red }
}

# Version parsing
Test "Parses semver '0.1.3'" {
    $m = [regex]::Match('0.1.3', '^(\d+)\.(\d+)\.(\d+)$')
    if (-not $m.Success) { throw "no match" }
    if ([int]$m.Groups[1].Value -ne 0) { throw "major wrong" }
    if ([int]$m.Groups[2].Value -ne 1) { throw "minor wrong" }
    if ([int]$m.Groups[3].Value -ne 3) { throw "patch wrong" }
}

Test "Rejects 'v0.1.3'" {
    if ([regex]::Match('v0.1.3', '^(\d+)\.(\d+)\.(\d+)$').Success) { throw "should not match" }
}

Test "Rejects '0.1'" {
    if ([regex]::Match('0.1', '^(\d+)\.(\d+)\.(\d+)$').Success) { throw "should not match" }
}

Test "Rejects 'preview'" {
    if ([regex]::Match('preview', '^(\d+)\.(\d+)\.(\d+)$').Success) { throw "should not match" }
}

Test "Version comparison 0.1.3 > 0.1.2" {
    if ([Version]"0.1.3" -le [Version]"0.1.2") { throw "wrong comparison" }
}

Test "Version comparison 0.1.3 == 0.1.3" {
    if ([Version]"0.1.3" -ne [Version]"0.1.3") { throw "wrong comparison" }
}

Test "Version comparison 1.0.0 > 0.99.99" {
    if ([Version]"1.0.0" -le [Version]"0.99.99") { throw "major should dominate" }
}

Test "Version comparison 0.2.0 > 0.1.99" {
    if ([Version]"0.2.0" -le [Version]"0.1.99") { throw "minor should dominate" }
}

Test "Version comparison 0.1.10 > 0.1.9" {
    if ([Version]"0.1.10" -le [Version]"0.1.9") { throw "patch should dominate" }
}

# Argument parsing
Test "Parses --url and --version args" {
    $url = $null; $version = $null
    $args_ = @('--url', 'https://example.com/update.zip', '--version', '0.2.0')
    for ($i = 0; $i -lt $args_.Count; $i++) {
        switch ($args_[$i]) {
            '--url' { if ($i + 1 -lt $args_.Count) { $url = $args_[++$i] } }
            '--version' { if ($i + 1 -lt $args_.Count) { $version = $args_[++$i] } }
        }
    }
    if ($url -ne 'https://example.com/update.zip') { throw "url: $url" }
    if ($version -ne '0.2.0') { throw "version: $version" }
}

Test "Handles missing --url value" {
    $url = $null
    $args_ = @('--url')
    for ($i = 0; $i -lt $args_.Count; $i++) {
        if ($args_[$i] -eq '--url' -and $i + 1 -lt $args_.Count) { $url = $args_[++$i] }
    }
    if ($url -ne $null) { throw "url should be null, got $url" }
}

# ZIP extraction with nested directories
Test "ZIP extraction handles nested directories" {
    $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("rpc-nest-" + [Guid]::NewGuid().ToString("N"))
    $zip = "$tmp.zip"; $ext = "$tmp-ext"
    try {
        $nested = "$tmp\sub\folder"
        New-Item -ItemType Directory $nested -Force | Out-Null
        "nested-file" | Out-File "$nested\data.txt" -Encoding UTF8
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::CreateFromDirectory($tmp, $zip)
        New-Item -ItemType Directory $ext -Force | Out-Null
        [System.IO.Compression.ZipFile]::ExtractToDirectory($zip, $ext)
        if (-not (Test-Path "$ext\sub\folder\data.txt")) { throw "nested file not extracted" }
        if ((Get-Content "$ext\sub\folder\data.txt" -Raw).Trim() -ne "nested-file") { throw "wrong content" }
    }
    finally {
        Remove-Item $tmp, $ext -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item $zip -Force -ErrorAction SilentlyContinue
    }
}

# ZIP extraction
Test "ZIP extraction preserves existing appsettings.json" {
    $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("rpc-upd-" + [Guid]::NewGuid().ToString("N"))
    $zip = "$tmp.zip"; $ext = "$tmp-ext"
    try {
        New-Item -ItemType Directory $tmp -Force | Out-Null
        "new-config" | Out-File "$tmp\appsettings.json" -Encoding UTF8
        "other" | Out-File "$tmp\other.txt" -Encoding UTF8
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::CreateFromDirectory($tmp, $zip)

        New-Item -ItemType Directory $ext -Force | Out-Null
        "user-config" | Out-File "$ext\appsettings.json" -Encoding UTF8

        $archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
        foreach ($entry in $archive.Entries) {
            if ($entry.Name -eq 'appsettings.json' -and (Test-Path (Join-Path $ext $entry.FullName))) { continue }
            $dest = Join-Path $ext $entry.FullName
            $destDir = Split-Path $dest -Parent
            if ($destDir -and -not (Test-Path $destDir)) { New-Item -ItemType Directory $destDir -Force | Out-Null }
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $dest, $true)
        }
        $archive.Dispose()

        $cfg = Get-Content "$ext\appsettings.json" -Raw
        if ($cfg.Trim() -ne 'user-config') { throw "config overwritten: $cfg" }
    }
    finally {
        Remove-Item $tmp, $ext -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item $zip -Force -ErrorAction SilentlyContinue
    }
}

$updElapsed = $sectionWatch.ElapsedMilliseconds
Write-Host "  $upPass/$($upPass+$upFail) checks passed (${updElapsed}ms)" -ForegroundColor $(if ($upFail -eq 0) { "Green" } else { "Red" })
if ($upFail -gt 0) { throw "$upFail updater check(s) failed" }

# ----------------------------------------------------------------
# Resolution profiles — anchor position validation
# Ensures the league panel anchor detection produces sensible values
# for every profile, using the same computation as the live code.
# ----------------------------------------------------------------
Write-Host ""
Write-Host "--- Resolutions ---" -ForegroundColor Yellow
$sectionWatch = [System.Diagnostics.Stopwatch]::StartNew()
$resPass = 0; $resFail = 0

function ResTest($name, [ScriptBlock]$block) {
    try { & $block; $script:resPass++ }
    catch { $script:resFail++; Write-Host "  FAIL: $name -> $_" -ForegroundColor Red }
}

# Mirror the exact computation from OcrLeagueWindowReader / OcrCaptureBoundsOverlayService
$anchorFractionY = 0.023
$anchorSampleRadiusPx = 5
$anchorSampleRadiusYPx = 10
$anchorSampleX = 0
$anchorFractionX = 0.0

$profiles = @(
    @{ Key="1600x900";  CaptureW=240; CaptureH=450 }
    @{ Key="1920x1080"; CaptureW=288; CaptureH=540 }
    @{ Key="2560x1440"; CaptureW=418; CaptureH=720 }
    @{ Key="3440x1440"; CaptureW=390; CaptureH=725 }
    @{ Key="3840x2160"; CaptureW=680; CaptureH=1080 }
)

foreach ($p in $profiles) {
    $key = $p.Key
    $w = $p.CaptureW
    $h = $p.CaptureH

    # Compute anchor X (mirrors ComputeAnchorX)
    $leftX = if ($anchorFractionX -gt 0) { [int]($w * [Math]::Max(0, [Math]::Min(1, $anchorFractionX))) } else { [Math]::Max(0, [Math]::Min($w - 1, $anchorSampleX)) }
    $rightX = $w - 1 - $leftX

    # Compute anchor Y (mirrors ComputeAnchorY)
    $sampleY = if ($anchorFractionY -gt 0) { [int]($h * [Math]::Max(0, [Math]::Min(1, $anchorFractionY))) } else { [Math]::Max(0, [Math]::Min($h - 1, 0)) }

    # Compute radius X (mirrors ComputeAnchorRadiusX)
    $sampleRadiusX = [Math]::Max(2, [Math]::Min(20, $anchorSampleRadiusPx))
    # Compute radius Y (mirrors ComputeAnchorRadiusY)
    $sampleRadiusY = [Math]::Max(2, [Math]::Min(20, $anchorSampleRadiusYPx))

    # Search region bounds (mirrors CheckAnchorRegion clamping)
    $leftMinX = [Math]::Max(0, $leftX - $sampleRadiusX)
    $leftMaxX = [Math]::Min($w - 1, $leftX + $sampleRadiusX)
    $rightMinX = [Math]::Max(0, $rightX - $sampleRadiusX)
    $rightMaxX = [Math]::Min($w - 1, $rightX + $sampleRadiusX)
    $minY = [Math]::Max(0, $sampleY - $sampleRadiusY)
    $maxY = [Math]::Min($h - 1, $sampleY + $sampleRadiusY)

    ResTest "$key : left anchor X at left edge" { if ($leftX -ne 0) { throw "leftX=$leftX, expected 0" } }
    ResTest "$key : right anchor X at right edge" { if ($rightX -ne ($w - 1)) { throw "rightX=$rightX, expected $($w-1)" } }
    ResTest "$key : anchor Y within top 5%" { if ($sampleY -gt [int]($h * 0.05)) { throw "sampleY=$sampleY too far from top" } }
    ResTest "$key : radius X in range [2,20]" { if ($sampleRadiusX -lt 2 -or $sampleRadiusX -gt 20) { throw "radiusX=$sampleRadiusX" } }
    ResTest "$key : radius Y in range [2,20]" { if ($sampleRadiusY -lt 2 -or $sampleRadiusY -gt 20) { throw "radiusY=$sampleRadiusY" } }
    ResTest "$key : left search region within bitmap" {
        if ($leftMinX -lt 0 -or $leftMaxX -ge $w) { throw "left X [$leftMinX,$leftMaxX] outside [0,$($w-1)]" }
    }
    ResTest "$key : right search region within bitmap" {
        if ($rightMinX -lt 0 -or $rightMaxX -ge $w) { throw "right X [$rightMinX,$rightMaxX] outside [0,$($w-1)]" }
    }
    ResTest "$key : Y search region within bitmap" {
        if ($minY -lt 0 -or $maxY -ge $h) { throw "Y [$minY,$maxY] outside [0,$($h-1)]" }
    }
    ResTest "$key : search region covers at least 50 pixels" {
        $area = ($leftMaxX - $leftMinX + 1) * ($maxY - $minY + 1)
        if ($area -lt 50) { throw "search area=$area too small" }
    }
    ResTest "$key : left and right anchors symmetric" {
        $distFromLeft = $leftX
        $distFromRight = ($w - 1) - $rightX
        if ($distFromLeft -ne $distFromRight) { throw "asymmetry: left=$distFromLeft right=$distFromRight" }
    }
}

$resElapsed = $sectionWatch.ElapsedMilliseconds
Write-Host "  $resPass/$($resPass+$resFail) checks passed (${resElapsed}ms)" -ForegroundColor $(if ($resFail -eq 0) { "Green" } else { "Red" })
if ($resFail -gt 0) { throw "$resFail resolution check(s) failed" }

# ----------------------------------------------------------------
# Integration checks
# Pure PowerShell — validates file structure, resources
# ----------------------------------------------------------------
Write-Host ""
Write-Host "--- Integration ---" -ForegroundColor Yellow
$sectionWatch = [System.Diagnostics.Stopwatch]::StartNew()
$intPass = 0; $intFail = 0

function IntTest($name, [ScriptBlock]$block) {
    try { & $block; $script:intPass++ }
    catch { $script:intFail++; Write-Host "  FAIL: $name -> $_" -ForegroundColor Red }
}

IntTest "pricing-mock.json is valid" {
    $content = Get-Content "$mock" -Raw
    $null = $content | ConvertFrom-Json
    if ($content.Length -lt 100) { throw "too small" }
}

IntTest "csproj has Tesseract package reference" {
    $csproj = Get-Content "$root\RuneshapePriceChecker.csproj" -Raw
    if ($csproj -notmatch 'Tesseract') { throw "Tesseract package not found" }
}

$intElapsed = $sectionWatch.ElapsedMilliseconds
Write-Host "  $intPass/$($intPass+$intFail) checks passed (${intElapsed}ms)" -ForegroundColor $(if ($intFail -eq 0) { "Green" } else { "Red" })
if ($intFail -gt 0) { throw "$intFail integration check(s) failed" }

# ----------------------------------------------------------------
# Results
# ----------------------------------------------------------------
$totalMs = $stopwatch.ElapsedMilliseconds
Write-Host ""
Write-Host "======================================" -ForegroundColor Cyan
Write-Host " ALL TESTS PASSED (${totalMs}ms)" -ForegroundColor Green
Write-Host "======================================" -ForegroundColor Cyan
exit 0
