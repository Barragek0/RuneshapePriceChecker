[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$statePath = Join-Path $root "obj\UpdateTestServer\server.json"

if (-not (Test-Path -LiteralPath $statePath -PathType Leaf)) {
    Write-Host "No tracked update test server is running."
    exit 0
}

$state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
try {
    $process = Get-Process -Id ([int]$state.processId) -ErrorAction SilentlyContinue
    if ($null -ne $process) {
        $expectedStart = [DateTimeOffset]::Parse($state.startedAtUtc)
        $actualStart = [DateTimeOffset]$process.StartTime.ToUniversalTime()
        if ([Math]::Abs(($actualStart - $expectedStart).TotalSeconds) -gt 5) {
            throw "Refusing to stop a process whose start time does not match the tracked update server."
        }

        Stop-Process -Id $process.Id -Force
        $process.WaitForExit()
    }
}
finally {
    Remove-Item -LiteralPath $statePath -Force -ErrorAction SilentlyContinue
}

Write-Host "Update test server stopped."
