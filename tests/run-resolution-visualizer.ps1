$project = Join-Path $PSScriptRoot "ResolutionVisualizer/ResolutionVisualizer.csproj"
if (-not (Test-Path -LiteralPath $project)) {
    throw "Visualizer project not found at '$project'."
}

dotnet run --project $project -c Release --nologo
