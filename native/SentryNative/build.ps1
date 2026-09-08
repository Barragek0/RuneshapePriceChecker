[CmdletBinding()]
param(
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..\.." )).Path
$metadata = Get-Content (Join-Path $PSScriptRoot "sentry-native.version.json") -Raw | ConvertFrom-Json
$objRoot = Join-Path $root "obj\SentryNative"
$archive = Join-Path $objRoot "sentry-native-$($metadata.version).zip"
$source = Join-Path $objRoot "source\$($metadata.version)"
$build = Join-Path $objRoot "build"
$runtime = Join-Path $PSScriptRoot "runtime\win-x64"
$symbols = Join-Path $objRoot "symbols"

New-Item -ItemType Directory -Force -Path $objRoot,$runtime,$symbols | Out-Null

if ($Force -or -not (Test-Path -LiteralPath $archive)) {
    Invoke-WebRequest -Uri $metadata.url -OutFile $archive
}

$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $archive).Hash
if ($hash -ne $metadata.sha256) {
    throw "Sentry Native archive hash mismatch. Expected $($metadata.sha256), got $hash."
}

$sentinel = Join-Path $source "CMakeLists.txt"
if ($Force -or -not (Test-Path -LiteralPath $sentinel)) {
    if (Test-Path -LiteralPath $source) { Remove-Item -LiteralPath $source -Recurse -Force }
    Expand-Archive -LiteralPath $archive -DestinationPath $source -Force
}

$cmake = (Get-Command cmake -ErrorAction SilentlyContinue).Source
if (-not $cmake) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    $vsPath = if (Test-Path -LiteralPath $vswhere) {
        & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    }
    if ($vsPath) {
        $cmake = Join-Path $vsPath "Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
    }
}
if (-not $cmake -or -not (Test-Path -LiteralPath $cmake)) { throw "CMake was not found." }

$configure = @(
    "-S", $source, "-B", $build, "-G", "Visual Studio 17 2022", "-A", "x64",
    "-DSENTRY_BACKEND=crashpad", "-DSENTRY_TRANSPORT=winhttp",
    "-DSENTRY_BUILD_SHARED_LIBS=ON", "-DSENTRY_BUILD_RUNTIMESTATIC=ON",
    "-DSENTRY_BUILD_TESTS=OFF", "-DSENTRY_BUILD_EXAMPLES=OFF"
)
& $cmake @configure
if ($LASTEXITCODE -ne 0) { throw "Sentry Native CMake configuration failed." }

& $cmake --build $build --config RelWithDebInfo --target sentry --parallel 1
if ($LASTEXITCODE -ne 0) { throw "Sentry Native build failed." }

$outputs = @{
    "sentry.dll" = Join-Path $build "RelWithDebInfo\sentry.dll"
    "crashpad_handler.exe" = Join-Path $build "crashpad_build\handler\RelWithDebInfo\crashpad_handler.exe"
    "crashpad_wer.dll" = Join-Path $build "crashpad_build\handler\RelWithDebInfo\crashpad_wer.dll"
}
$pdbs = @{
    "sentry.pdb" = Join-Path $build "RelWithDebInfo\sentry.pdb"
    "crashpad_handler.pdb" = Join-Path $build "crashpad_build\handler\RelWithDebInfo\crashpad_handler.pdb"
    "crashpad_wer.pdb" = Join-Path $build "crashpad_build\handler\RelWithDebInfo\crashpad_wer.pdb"
}

foreach ($name in $outputs.Keys) {
    if (-not (Test-Path -LiteralPath $outputs[$name])) { throw "Expected Sentry runtime file is missing: $name" }
    Copy-Item -LiteralPath $outputs[$name] -Destination (Join-Path $runtime $name) -Force
}
foreach ($name in $pdbs.Keys) {
    if (Test-Path -LiteralPath $pdbs[$name]) { Copy-Item -LiteralPath $pdbs[$name] -Destination (Join-Path $symbols $name) -Force }
}

$manifest = foreach ($name in $outputs.Keys | Sort-Object) {
    $file = Join-Path $runtime $name
    [ordered]@{ name = $name; sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $file).Hash }
}
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot "runtime\win-x64\manifest.json") -Encoding UTF8
