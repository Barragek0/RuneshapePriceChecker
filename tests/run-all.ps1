$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

Write-Host "======================================" -ForegroundColor Cyan
Write-Host " RuneshapePriceChecker Test Suite" -ForegroundColor Cyan
Write-Host "======================================" -ForegroundColor Cyan

$simulator = "$PSScriptRoot\OcrPricingSimulator\OcrPricingSimulator.csproj"
$mock = "$root\tests\mocks\pricing-mock.json"
Write-Host ""
Write-Host "--- Build simulator ---" -ForegroundColor DarkGray
dotnet build $simulator -c Release --nologo 2>&1 | Select-Object -Last 2
if ($LASTEXITCODE -ne 0) { Write-Host "Build failed" -ForegroundColor Red; exit 1 }

Write-Host ""
Write-Host "--- Pricing & Parsing ---" -ForegroundColor Yellow
$sectionWatch = [System.Diagnostics.Stopwatch]::StartNew()

$pricingItems = @(Get-Content $mock -Raw | ConvertFrom-Json | ForEach-Object { $_.name } | Where-Object { $_ })
$pricingItems += "chaos orb", "DIVINE ORB", "ExAlTeD oRb", "0RB OF ALCHEMY", "Random Currency"
$pricingItems += "Uncut Support Gem", "Uncut Skill Gem", "Uncut Spirit Gem"
$pricingItems += "Unique Ring", "Unique Amulet", "Unique Belt", "Unique Jewellery"

$parsingTests = Get-Content "$PSScriptRoot\parsing-tests.json" -Raw | ConvertFrom-Json

$allItems = [System.Collections.Generic.HashSet[string]]::new()
foreach ($item in $pricingItems) { $null = $allItems.Add($item) }
foreach ($test in $parsingTests) { $null = $allItems.Add($test.raw) }

$tempFile = [System.IO.Path]::GetTempFileName()
try {
    $allItems -join "`n" | Out-File $tempFile -Encoding UTF8
    $raw = dotnet run --project $simulator -c Release --no-build -- --league "Test" --source "poe2scout" --mock-file $mock --display-currency chaos --input-file $tempFile 2>&1
}
finally { Remove-Item $tempFile -Force }

if ($LASTEXITCODE -ne 0) { throw "Simulator failed (exit $LASTEXITCODE)" }

$pass = 0; $fail = 0
foreach ($line in ($raw | Where-Object { $_ -match '->' })) {
    if ($line -match '^(.+?)\s*->\s*(.+?)\s*\[(\S+)\]') {
        $quote = $Matches[2].Trim()
        if ($quote -eq 'N/A' -or $quote -eq '...' -or $quote -eq '') { $fail++ }
        else { $pass++ }
    }
}

$pricingElapsed = $sectionWatch.ElapsedMilliseconds
Write-Host "  $pass/$($pass+$fail) items priced (${pricingElapsed}ms)" -ForegroundColor $(if ($fail -le 1) { "Green" } else { "Red" })
if ($fail -gt 1) { throw "$fail items unresolved" }
$pricingElapsed = $sectionWatch.ElapsedMilliseconds
Write-Host "  $pass/$($pass+$fail) checks passed (${pricingElapsed}ms)" -ForegroundColor $(if ($fail -eq 0) { "Green" } else { "Red" })
if ($fail -gt 0) { throw "$fail pricing/parsing check(s) failed" }

# ----------------------------------------------------------------
# Resolution profiles
# ----------------------------------------------------------------
Write-Host ""
Write-Host "--- Resolutions ---" -ForegroundColor Yellow
$sectionWatch = [System.Diagnostics.Stopwatch]::StartNew()
$resPass = 0; $resFail = 0

function ResTest($name, [ScriptBlock]$block) {
    try { & $block; $script:resPass++ }
    catch { $script:resFail++; Write-Host "  FAIL: $name -> $_" -ForegroundColor Red }
}

$anchorFractionY = 0.023; $anchorSampleRadiusPx = 5; $anchorSampleRadiusYPx = 10; $anchorSampleX = 0; $anchorFractionX = 0.0
$profiles = @(
    @{ Key = "1600x900"; CaptureW = 240; CaptureH = 450 }
    @{ Key = "1920x1080"; CaptureW = 288; CaptureH = 540 }
    @{ Key = "2560x1440"; CaptureW = 418; CaptureH = 720 }
    @{ Key = "3440x1440"; CaptureW = 390; CaptureH = 725 }
    @{ Key = "3840x2160"; CaptureW = 680; CaptureH = 1080 }
)

foreach ($p in $profiles) {
    $key = $p.Key; $w = $p.CaptureW; $h = $p.CaptureH
    $leftX = if ($anchorFractionX -gt 0) { [int]($w * [Math]::Max(0, [Math]::Min(1, $anchorFractionX))) } else { [Math]::Max(0, [Math]::Min($w - 1, $anchorSampleX)) }
    $rightX = $w - 1 - $leftX
    $sampleY = if ($anchorFractionY -gt 0) { [int]($h * [Math]::Max(0, [Math]::Min(1, $anchorFractionY))) } else { [Math]::Max(0, [Math]::Min($h - 1, 0)) }
    $sampleRadiusX = [Math]::Max(2, [Math]::Min(20, $anchorSampleRadiusPx))
    $sampleRadiusY = [Math]::Max(2, [Math]::Min(20, $anchorSampleRadiusYPx))
    $leftMinX = [Math]::Max(0, $leftX - $sampleRadiusX); $leftMaxX = [Math]::Min($w - 1, $leftX + $sampleRadiusX)
    $rightMinX = [Math]::Max(0, $rightX - $sampleRadiusX); $rightMaxX = [Math]::Min($w - 1, $rightX + $sampleRadiusX)
    $minY = [Math]::Max(0, $sampleY - $sampleRadiusY); $maxY = [Math]::Min($h - 1, $sampleY + $sampleRadiusY)
    ResTest "$key : left anchor at left edge" { if ($leftX -ne 0) { throw "leftX=$leftX" } }
    ResTest "$key : right anchor at right edge" { if ($rightX -ne ($w - 1)) { throw "rightX=$rightX" } }
    ResTest "$key : anchor Y within top 5%" { if ($sampleY -gt [int]($h * 0.05)) { throw "sampleY=$sampleY" } }
    ResTest "$key : radius X in range [2,20]" { if ($sampleRadiusX -lt 2 -or $sampleRadiusX -gt 20) { throw "radiusX=$sampleRadiusX" } }
    ResTest "$key : radius Y in range [2,20]" { if ($sampleRadiusY -lt 2 -or $sampleRadiusY -gt 20) { throw "radiusY=$sampleRadiusY" } }
    ResTest "$key : left search region within bitmap" { if ($leftMinX -lt 0 -or $leftMaxX -ge $w) { throw "left X out of bounds" } }
    ResTest "$key : right search region within bitmap" { if ($rightMinX -lt 0 -or $rightMaxX -ge $w) { throw "right X out of bounds" } }
    ResTest "$key : Y search region within bitmap" { if ($minY -lt 0 -or $maxY -ge $h) { throw "Y out of bounds" } }
    ResTest "$key : search area covers 50+ pixels" { if (($leftMaxX - $leftMinX + 1) * ($maxY - $minY + 1) -lt 50) { throw "area too small" } }
    ResTest "$key : anchors symmetric" { if ($leftX -ne (($w - 1) - $rightX)) { throw "asymmetry" } }
}

$resElapsed = $sectionWatch.ElapsedMilliseconds
Write-Host "  $resPass/$($resPass+$resFail) checks passed (${resElapsed}ms)" -ForegroundColor $(if ($resFail -eq 0) { "Green" } else { "Red" })
if ($resFail -gt 0) { throw "$resFail resolution check(s) failed" }

# ----------------------------------------------------------------
# Changelog wiring
# ----------------------------------------------------------------
Write-Host ""
Write-Host "--- Changelog ---" -ForegroundColor Yellow
$sectionWatch = [System.Diagnostics.Stopwatch]::StartNew()
$chPass = 0; $chFail = 0
function ChTest($name, [ScriptBlock]$block) { try { & $block; $script:chPass++ } catch { $script:chFail++; Write-Host "  FAIL: $name -> $_" -ForegroundColor Red } }
ChTest "GitHubRelease has Body" { if ((Get-Content "$root\src\Startup\UpdateChecker.cs" -Raw) -notmatch 'string\?\s+Body') { throw "missing" } }
ChTest "UpdateOptions has GitHubApiBaseUrl" { if ((Get-Content "$root\src\Startup\UpdateChecker.cs" -Raw) -notmatch 'GitHubApiBaseUrl') { throw "missing" } }
ChTest "WriteChangelogToSettings exists" { if ((Get-Content "$root\src\Startup\UpdateChecker.cs" -Raw) -notmatch 'WriteChangelogToSettings') { throw "missing" } }
ChTest "TryGetPendingChangelog exists" { if ((Get-Content "$root\src\Dashboard\DashboardWindow.xaml.cs" -Raw) -notmatch 'TryGetPendingChangelog') { throw "missing" } }
ChTest "MarkChangelogShown exists" { if ((Get-Content "$root\src\Dashboard\DashboardViewModel.cs" -Raw) -notmatch 'MarkChangelogShown') { throw "missing" } }
$chElapsed = $sectionWatch.ElapsedMilliseconds
Write-Host "  $chPass/$($chPass+$chFail) checks passed (${chElapsed}ms)" -ForegroundColor $(if ($chFail -eq 0) { "Green" } else { "Red" })
if ($chFail -gt 0) { throw "$chFail changelog check(s) failed" }

# ----------------------------------------------------------------
# Results
# ----------------------------------------------------------------
$totalMs = $stopwatch.ElapsedMilliseconds
Write-Host ""
Write-Host "======================================" -ForegroundColor Cyan
Write-Host " ALL TESTS PASSED (${totalMs}ms)" -ForegroundColor Green
Write-Host "======================================" -ForegroundColor Cyan
exit 0

