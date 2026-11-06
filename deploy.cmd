@echo off
setlocal
cd /d "%~dp0"

:: Check for Administrator privileges
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo [UAC] Requesting Administrator privileges to deploy MSI GF63 Tuner...
    powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Start-Process cmd.exe -ArgumentList '/c \"\"%~dp0deploy.cmd\"\"' -Verb RunAs"
    exit /b
)

title MSI GF63 Tuner - Deployment Deck
echo ==========================================================
echo     MSI GF63 TUNER - TACTICAL DEPLOYMENT DECK
echo ==========================================================
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0deploy.ps1"

echo.
echo Press any key to exit...
pause >nul
