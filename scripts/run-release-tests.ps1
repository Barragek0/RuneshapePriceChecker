param(
    [string]$Configuration = "Release",
    [string]$Filter = "Category!=NativeCrash"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$resultsDirectory = Join-Path $repoRoot "obj\$Configuration\TestResults"
$trxPath = Join-Path $resultsDirectory "ReleaseTests.trx"

New-Item -ItemType Directory -Path $resultsDirectory -Force | Out-Null
Remove-Item -LiteralPath $trxPath -Force -ErrorAction SilentlyContinue

$testArguments = @(
    "test",
    (Join-Path $repoRoot "tests\Tests.csproj"),
    "-c", $Configuration,
    "--nologo",
    "--verbosity", "minimal",
    "-m:1",
    "--filter", $Filter,
    "--logger", "trx;LogFileName=ReleaseTests.trx",
    "--results-directory", $resultsDirectory
)

& dotnet @testArguments
$testExitCode = $LASTEXITCODE

if ($testExitCode -eq 0) {
    Write-Output "Release test gate passed."
    exit 0
}

Write-Error "Release test gate failed with exit code $testExitCode."

if (Test-Path -LiteralPath $trxPath) {
    [xml]$testRun = Get-Content -LiteralPath $trxPath -Raw
    $failedTests = @($testRun.TestRun.Results.UnitTestResult | Where-Object { $_.outcome -eq "Failed" })

    if ($failedTests.Count -eq 0) {
        Write-Error "The test result file did not contain individual failed-test details: $trxPath"
    }
    else {
        foreach ($failedTest in $failedTests) {
            Write-Error ("FAILED: {0}" -f $failedTest.testName)
            $message = $failedTest.Output.ErrorInfo.Message
            if ($message) { Write-Error ("  Message: {0}" -f $message.InnerText.Trim()) }
            $stackTrace = $failedTest.Output.ErrorInfo.StackTrace
            if ($stackTrace) { Write-Error ("  Stack: {0}" -f $stackTrace.InnerText.Trim()) }
        }
    }
}
else {
    Write-Error "Test result file was not created: $trxPath"
}

exit $testExitCode
