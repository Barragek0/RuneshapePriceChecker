param(
    [string]$League = "Runes of Aldur",
    [ValidateSet("exalt", "chaos")]
    [string]$DisplayCurrency = "exalt",
    [ValidateSet("poe2scout", "poeninja")]
    [string]$Source = "poe2scout",
    [string]$InputFile,
    [string[]]$Items,
    [string]$MockFile
)

$ErrorActionPreference = "Stop"

$runnerProject = Join-Path $PSScriptRoot "OcrPricingSimulator/OcrPricingSimulator.csproj"
if (-not (Test-Path -LiteralPath $runnerProject)) { throw "Simulator project not found." }

$arguments = @("run", "--project", $runnerProject, "--")

if ($League) { $arguments += "--league"; $arguments += $League }
if ($Source) { $arguments += "--source"; $arguments += $Source }
if ($DisplayCurrency) { $arguments += "--display-currency"; $arguments += $DisplayCurrency }

if ($InputFile -and (Test-Path -LiteralPath $InputFile)) {
    $arguments += "--input-file"; $arguments += (Resolve-Path $InputFile).Path
}
if ($MockFile -and (Test-Path -LiteralPath $MockFile)) {
    $arguments += "--mock-file"; $arguments += (Resolve-Path $MockFile).Path
}
foreach ($item in $Items) { if ($item) { $arguments += "--item"; $arguments += $item } }

& dotnet @arguments
exit $LASTEXITCODE
