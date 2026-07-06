@echo off
REM Double-click this file to build and run DbExplorer, then open it in the browser.
REM Prefers PowerShell 7 (pwsh) and falls back to Windows PowerShell.

setlocal
cd /d "%~dp0"

where pwsh >nul 2>&1
if %ERRORLEVEL%==0 (
    pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0run.ps1" %*
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run.ps1" %*
)

REM If the script exited with an error, keep the window open so the message is readable.
if %ERRORLEVEL% neq 0 (
    echo.
    echo run.ps1 exited with code %ERRORLEVEL%.
    pause
)
endlocal
