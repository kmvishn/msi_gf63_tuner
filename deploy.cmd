@echo off
cd /d "%~dp0"
echo Requesting administrator privileges to deploy MSI GF63 Tuner...
powershell.exe -ExecutionPolicy Bypass -Command "Start-Process powershell.exe -Verb RunAs -ArgumentList '-ExecutionPolicy Bypass -NoExit -File \"\"%~dp0deploy.ps1\"\"'"
