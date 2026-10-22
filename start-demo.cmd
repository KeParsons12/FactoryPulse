@echo off
setlocal

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0start-demo.ps1"
set "FACTORYPULSE_EXIT_CODE=%ERRORLEVEL%"

endlocal & exit /b %FACTORYPULSE_EXIT_CODE%
