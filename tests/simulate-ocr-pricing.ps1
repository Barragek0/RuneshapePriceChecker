param(
    [string]$League = "Runes of Aldur",
    [string]$BaseUrl = "https://poe.ninja",
    [string[]]$Types = @("Currency", "Expedition", "UncutGems", "Runes", "Verisium", "UniqueWeapons", "UniqueArmours", "UniqueAccessories"),
    [ValidateSet("exalt", "chaos")]
    [string]$DisplayCurrency = "exalt",
    [string]$InputFile,
    [string[]]$Items,
    [string]$MockFile
)

$ErrorActionPreference = "Stop"

function Add-ArgumentValue {
    param(
        [System.Collections.Generic.List[string]]$ArgList,
        [string]$Name,
        [string]$Value
    )

    if (-not [string]::IsNullOrWhiteSpace($Value)) {
        $ArgList.Add($Name) | Out-Null
        $ArgList.Add($Value) | Out-Null
    }
}

$runnerProject = Join-Path $PSScriptRoot "OcrPricingSimulator/OcrPricingSimulator.csproj"
if (-not (Test-Path -LiteralPath $runnerProject)) {
    throw "Simulator project not found at '$runnerProject'."
}

$resolvedInputFile = $null
if (-not [string]::IsNullOrWhiteSpace($InputFile)) {
    if (-not (Test-Path -LiteralPath $InputFile)) {
        throw "Input file not found: $InputFile"
    }

    $resolvedInputFile = (Resolve-Path -LiteralPath $InputFile).Path
}

$resolvedMockFile = $null
if (-not [string]::IsNullOrWhiteSpace($MockFile)) {
    if (-not (Test-Path -LiteralPath $MockFile)) {
        throw "Mock file not found: $MockFile"
    }

    $resolvedMockFile = (Resolve-Path -LiteralPath $MockFile).Path
}

$arguments = New-Object 'System.Collections.Generic.List[string]'
$arguments.Add("run") | Out-Null
$arguments.Add("--project") | Out-Null
$arguments.Add($runnerProject) | Out-Null
$arguments.Add("--") | Out-Null

Add-ArgumentValue -ArgList $arguments -Name "--league" -Value $League
Add-ArgumentValue -ArgList $arguments -Name "--base-url" -Value $BaseUrl

if ($Types -and $Types.Count -gt 0) {
    Add-ArgumentValue -ArgList $arguments -Name "--types" -Value ($Types -join ",")
}

Add-ArgumentValue -ArgList $arguments -Name "--display-currency" -Value $DisplayCurrency

Add-ArgumentValue -ArgList $arguments -Name "--input-file" -Value $resolvedInputFile
Add-ArgumentValue -ArgList $arguments -Name "--mock-file" -Value $resolvedMockFile

if ($Items) {
    foreach ($item in $Items) {
        Add-ArgumentValue -ArgList $arguments -Name "--item" -Value $item
    }
}

$argumentArray = $arguments.ToArray()
& dotnet @argumentArray
if ($LASTEXITCODE -ne 0) {
    throw "Simulator failed with exit code $LASTEXITCODE."
}
