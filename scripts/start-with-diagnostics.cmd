@echo off
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0start-with-diagnostics.ps1"
set "diagnosticExitCode=%errorlevel%"
echo.
pause
exit /b %diagnosticExitCode%
