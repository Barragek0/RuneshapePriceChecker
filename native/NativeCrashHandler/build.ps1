# Builds NativeCrashHandler.dll from the C source.
# Uses MSVC (cl.exe + link.exe) from Visual Studio 2022 Community.

$scriptDir = Split-Path -Parent $PSCommandPath
$srcFile = Join-Path $scriptDir "NativeCrashHandler.c"
$outDll = Join-Path $scriptDir "NativeCrashHandler.dll"
$vsRoot = "C:\Program Files\Microsoft Visual Studio\2022\Community"
$vcvars = "$vsRoot\VC\Auxiliary\Build\vcvars64.bat"

if (-not (Test-Path $vcvars)) {
    Write-Error "Visual Studio 2022 vcvars not found at $vcvars"
    exit 1
}

Write-Host "Building NativeCrashHandler.dll..."
Write-Host ""

# Use cmd.exe to source vcvars, then run cl.exe
$buildCmd = @"
call "$vcvars" >nul 2>&1
cl.exe /O2 /LD /GS- /Gs9999999 /Fo"$scriptDir\NativeCrashHandler.obj" /Fe"$outDll" "$srcFile" /link dbghelp.lib kernel32.lib
if %ERRORLEVEL% NEQ 0 exit %ERRORLEVEL%
"@

cmd /c $buildCmd
if ($LASTEXITCODE -ne 0) {
    Write-Error "Compilation failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

Write-Host ""
Write-Host "Success: $outDll"
Write-Host "  Size: $((Get-Item $outDll).Length) bytes"
