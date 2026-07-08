@echo off
REM Double-click this file to force-delete all bin and obj folders under this directory.
REM Prefers PowerShell 7 (pwsh) and falls back to Windows PowerShell.

setlocal
cd /d "%~dp0"

where pwsh >nul 2>&1
if %ERRORLEVEL%==0 (
    pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0compress.ps1" %*
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0compress.ps1" %*
)

echo.
pause
endlocal
