# Self-elevate script to Administrator if not already elevated
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "Requesting Administrator privileges..." -ForegroundColor Yellow
    Start-Process powershell.exe -Verb RunAs -ArgumentList "-ExecutionPolicy Bypass -File `"$PSCommandPath`""
    exit
}

$ErrorActionPreference = 'Stop'
$target = "C:\Program Files\kmvishn\msi_gf63_tuner"

if (Test-Path "$PSScriptRoot\YAMDCC.ConfigEditor") {
    $sourceBase = $PSScriptRoot
} else {
    $sourceBase = Join-Path $PSScriptRoot "msi_gf63_tuner"
}

Write-Host "=== Deploying Standalone MSI GF63 Tuner (Single Binary) ===" -ForegroundColor Red

Write-Host "Stopping any running tuner instances..." -ForegroundColor Cyan
Stop-Process -Name msi_gf63_tuner, ConfigEditor, HotkeyHandler, yamdcc, yamdccsvc -Force -ErrorAction SilentlyContinue

# If the legacy yamdccsvc Windows service exists, stop and uninstall it
$svc = Get-Service -Name yamdccsvc -ErrorAction SilentlyContinue
if ($svc) {
    Write-Host "Removing legacy yamdccsvc Windows service (now embedded into single binary)..." -ForegroundColor Yellow
    Stop-Service yamdccsvc -Force -ErrorAction SilentlyContinue
    & sc.exe delete yamdccsvc | Out-Null
}

Start-Sleep -Milliseconds 800

# Ensure target directory exists
if (-not (Test-Path $target)) {
    New-Item -ItemType Directory -Path $target -Force | Out-Null
}

# Clean up ALL legacy companion executables and loose DLLs
Write-Host "Cleaning up legacy binaries and DLLs from $target..." -ForegroundColor Cyan
$legacyItems = @(
    "yamdccsvc.exe",
    "yamdccsvc.exe.config",
    "yamdcc.exe",
    "yamdcc.exe.config",
    "ConfigEditor.exe",
    "ConfigEditor.exe.config",
    "HotkeyHandler.exe",
    "HotkeyHandler.exe.config"
)
foreach ($item in $legacyItems) {
    $p = Join-Path $target $item
    if (Test-Path $p) {
        Remove-Item -Path $p -Force -ErrorAction SilentlyContinue
        Write-Host "  [-] Removed legacy $item" -ForegroundColor DarkGray
    }
}

# Clean all loose DLLs so only the standalone single binary remains
Get-ChildItem -Path $target -Filter "*.dll" -File -ErrorAction SilentlyContinue | ForEach-Object {
    Remove-Item -Path $_.FullName -Force -ErrorAction SilentlyContinue
    Write-Host "  [-] Removed loose DLL: $($_.Name)" -ForegroundColor DarkGray
}

Write-Host "Deploying standalone single binary to $target..." -ForegroundColor Cyan
$binSrc = Join-Path $sourceBase "YAMDCC.ConfigEditor\bin\Release\net48\msi_gf63_tuner.exe"
$cfgSrc = Join-Path $sourceBase "YAMDCC.ConfigEditor\bin\Release\net48\msi_gf63_tuner.exe.config"

if (Test-Path $binSrc) {
    Copy-Item -Path $binSrc -Destination (Join-Path $target "msi_gf63_tuner.exe") -Force
    Write-Host "  [OK] Installed msi_gf63_tuner.exe (All-in-one Single Binary)" -ForegroundColor Green
} else {
    Write-Host "  [FAIL] Binary not found at $binSrc! Please run dotnet build first." -ForegroundColor Red
    exit 1
}

if (Test-Path $cfgSrc) {
    Copy-Item -Path $cfgSrc -Destination (Join-Path $target "msi_gf63_tuner.exe.config") -Force
    Write-Host "  [OK] Installed msi_gf63_tuner.exe.config" -ForegroundColor Green
}

Write-Host "Updating 5-profile unified fan configuration in ProgramData..." -ForegroundColor Cyan
$profSrc = Join-Path $sourceBase "Configs-V2\MSI-Thin-GF63-12HW.xml"
$profDst = "C:\ProgramData\Sparronator9999\YAMDCC\CurrentConfigV2.xml"
$profDir = [System.IO.Path]::GetDirectoryName($profDst)
if (-not (Test-Path $profDir)) {
    New-Item -ItemType Directory -Path $profDir -Force | Out-Null
}
if (Test-Path $profSrc) {
    Copy-Item -Path $profSrc -Destination $profDst -Force
    Write-Host "  [OK] Installed 5-profile suite (Balanced, Performance, Sustained 35W, Silent, Super Battery)" -ForegroundColor Green
}

# Ensure CommonConfig.xml is set to WMI2 (driverless)
$commonCfgDst = Join-Path $profDir "CommonConfig.xml"
$commonXml = @"
<?xml version="1.0" encoding="utf-8"?>
<CommonConfig xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema" Ver="1">
	<LogLevel>Debug</LogLevel>
	<UseWMI2>true</UseWMI2>
	<DisableFBOnExit>true</DisableFBOnExit>
	<AutoUpdateAsked>true</AutoUpdateAsked>
	<PreRelease>false</PreRelease>
</CommonConfig>
"@
Set-Content -Path $commonCfgDst -Value $commonXml -Encoding UTF8 -Force
Write-Host "  [OK] Configured driverless WMI2 mode (no WinRing0 needed)" -ForegroundColor Green

# Update Desktop shortcut
try {
    $desktopPath = [System.Environment]::GetFolderPath('Desktop')
    Remove-Item -Path (Join-Path $desktopPath "ConfigEditor.lnk") -Force -ErrorAction SilentlyContinue
    $shortcutPath = Join-Path $desktopPath "MSI GF63 Tuner.lnk"
    $wshShell = New-Object -ComObject WScript.Shell
    $shortcut = $wshShell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = "$target\msi_gf63_tuner.exe"
    $shortcut.WorkingDirectory = $target
    $shortcut.Description = "MSI GF63 Tuner"
    $shortcut.Save()
    Write-Host "  [OK] Desktop shortcut updated: MSI GF63 Tuner.lnk" -ForegroundColor Green
} catch {
    Write-Host "  [WARN] Could not update desktop shortcut: $_" -ForegroundColor DarkYellow
}

Write-Host "Launching standalone MSI GF63 Tuner..." -ForegroundColor Cyan
Start-Process "$target\msi_gf63_tuner.exe"

Write-Host "All done! Single binary msi_gf63_tuner.exe deployed and active." -ForegroundColor Green
Start-Sleep -Seconds 2
