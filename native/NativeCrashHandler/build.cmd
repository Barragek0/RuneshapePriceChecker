@echo off
call "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat" 2>nul
if errorlevel 1 exit /b 1
cl.exe /O2 /LD /GS- /Gs9999999 /Fo"%~dp0NativeCrashHandler.obj" /Fe"%~dp0NativeCrashHandler.dll" "%~dp0NativeCrashHandler.c" /link dbghelp.lib kernel32.lib
exit /b %errorlevel%
