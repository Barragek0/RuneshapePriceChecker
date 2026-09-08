[CmdletBinding()]
param(
    [string]$ReleaseZipPath = "",
    [int]$Port = 8099
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$project = Join-Path $root "tests\UpdateTestServer\UpdateTestServer.csproj"
$stateDirectory = Join-Path $root "obj\UpdateTestServer"
$statePath = Join-Path $stateDirectory "server.json"
$outputLogPath = Join-Path $stateDirectory "server.out.log"
$errorLogPath = Join-Path $stateDirectory "server.err.log"

if ([string]::IsNullOrWhiteSpace($ReleaseZipPath)) {
    $ReleaseZipPath = Join-Path $root "bin\Release\RuneshapePriceChecker.zip"
}
elseif (-not [System.IO.Path]::IsPathRooted($ReleaseZipPath)) {
    $ReleaseZipPath = Join-Path $root $ReleaseZipPath
}

if (-not (Test-Path -LiteralPath $ReleaseZipPath -PathType Leaf)) {
    throw "Release ZIP was not found: $ReleaseZipPath"
}
if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
    throw "Update test server project was not found: $project"
}

if (Test-Path -LiteralPath $statePath) {
    & (Join-Path $PSScriptRoot "stop-update-test-server.ps1")
}

dotnet build $project -c Release --nologo --verbosity minimal
if ($LASTEXITCODE -ne 0) {
    throw "Update test server build failed with exit code $LASTEXITCODE."
}

New-Item -ItemType Directory -Force -Path $stateDirectory | Out-Null
Remove-Item -LiteralPath $outputLogPath,$errorLogPath -Force -ErrorAction SilentlyContinue

$arguments = @(
    "run",
    "--project",
    $project,
    "-c",
    "Release",
    "--no-build",
    "--",
    $ReleaseZipPath,
    $Port.ToString()
)
$server = Start-Process -FilePath "dotnet" -ArgumentList $arguments -WorkingDirectory $root -WindowStyle Hidden -RedirectStandardOutput $outputLogPath -RedirectStandardError $errorLogPath -PassThru
@{
    processId = $server.Id
    startedAtUtc = $server.StartTime.ToUniversalTime().ToString("O")
    port = $Port
    releaseZipPath = (Resolve-Path -LiteralPath $ReleaseZipPath).Path
    outputLogPath = $outputLogPath
    errorLogPath = $errorLogPath
} | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding UTF8

$deadline = [DateTime]::UtcNow.AddSeconds(10)
while ([DateTime]::UtcNow -lt $deadline) {
    if ($server.HasExited) {
        $output = if (Test-Path -LiteralPath $outputLogPath) { Get-Content -LiteralPath $outputLogPath -Raw } else { "" }
        $error = if (Test-Path -LiteralPath $errorLogPath) { Get-Content -LiteralPath $errorLogPath -Raw } else { "" }
        & (Join-Path $PSScriptRoot "stop-update-test-server.ps1")
        throw "Update test server exited with code $($server.ExitCode). $output $error"
    }

    try {
        $response = Invoke-WebRequest -Uri "http://localhost:$Port/api/repos/Barragek0/RuneshapePriceChecker/releases?per_page=1" -UseBasicParsing -TimeoutSec 1
        if ($response.StatusCode -eq 200) {
            Write-Host "Update test server is running on http://localhost:$Port/ (PID $($server.Id))."
            exit 0
        }
    }
    catch { }
    Start-Sleep -Milliseconds 200
}

& (Join-Path $PSScriptRoot "stop-update-test-server.ps1")
throw "Update test server did not become ready. See $outputLogPath and $errorLogPath"
