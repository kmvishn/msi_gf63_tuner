# Self-elevate script to Administrator if not already elevated
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "Requesting Administrator privileges..." -ForegroundColor Yellow
    Start-Process powershell.exe -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -NoExit -File `"$PSCommandPath`""
    exit
}

$ErrorActionPreference = 'Stop'
$target = "C:\Program Files\kmvishn\msi_gf63_tuner"

if (Test-Path "$PSScriptRoot\YAMDCC.ConfigEditor") {
    $sourceBase = $PSScriptRoot
} elseif (Test-Path "$PSScriptRoot\msi_gf63_tuner\YAMDCC.ConfigEditor") {
    $sourceBase = Join-Path $PSScriptRoot "msi_gf63_tuner"
} else {
    $sourceBase = $PSScriptRoot
}

Write-Host "==========================================================" -ForegroundColor Red
Write-Host "  MSI GF63 TUNER - SINGLE-BINARY DEPLOYMENT DECK" -ForegroundColor Cyan
Write-Host "  Target: $target" -ForegroundColor DarkGray
Write-Host "==========================================================" -ForegroundColor Red

# 1. Stop any running instances
Write-Host "[1/7] Stopping any running tuner instances..." -ForegroundColor Cyan
& taskkill.exe /F /IM msi_gf63_tuner.exe /T 2>$null | Out-Null
& taskkill.exe /F /IM ConfigEditor.exe /T 2>$null | Out-Null
& taskkill.exe /F /IM HotkeyHandler.exe /T 2>$null | Out-Null
& taskkill.exe /F /IM yamdccsvc.exe /T 2>$null | Out-Null
& taskkill.exe /F /IM yamdcc.exe /T 2>$null | Out-Null
Get-Process -Name msi_gf63_tuner, ConfigEditor, HotkeyHandler, yamdcc, yamdccsvc -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

# Verify file lock is released
$targetExe = Join-Path $target "msi_gf63_tuner.exe"
if (Test-Path $targetExe) {
    for ($attempt = 1; $attempt -le 10; $attempt++) {
        try {
            $stream = [System.IO.File]::Open($targetExe, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
            $stream.Close()
            $stream.Dispose()
            break
        } catch {
            Write-Host "  [WAIT] File handle locked, terminating lingering processes (attempt $attempt/10)..." -ForegroundColor Yellow
            & taskkill.exe /F /IM msi_gf63_tuner.exe /T 2>$null | Out-Null
            Start-Sleep -Milliseconds 500
        }
    }
}
Write-Host "  [OK] Process handles cleared." -ForegroundColor Green

# 2. Remove legacy Windows service if lingering
$svc = Get-Service -Name yamdccsvc -ErrorAction SilentlyContinue
if ($svc) {
    Write-Host "[2/7] Removing legacy yamdccsvc Windows service (now embedded)..." -ForegroundColor Yellow
    Stop-Service yamdccsvc -Force -ErrorAction SilentlyContinue
    & sc.exe delete yamdccsvc | Out-Null
    Write-Host "  [OK] Service removed." -ForegroundColor Green
} else {
    Write-Host "[2/7] Legacy Windows service check: Clean (none present)." -ForegroundColor DarkGray
}

Start-Sleep -Milliseconds 400

# 3. Ensure target directory exists
if (-not (Test-Path $target)) {
    New-Item -ItemType Directory -Path $target -Force | Out-Null
}

# 4. Clean up legacy binaries and loose DLLs
Write-Host "[3/7] Cleaning legacy files and loose DLLs..." -ForegroundColor Cyan
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
Get-ChildItem -Path $target -Filter "*.dll" -File -ErrorAction SilentlyContinue | ForEach-Object {
    Remove-Item -Path $_.FullName -Force -ErrorAction SilentlyContinue
    Write-Host "  [-] Removed loose DLL: $($_.Name)" -ForegroundColor DarkGray
}

# 5. Deploy standalone single binary, config, and icon
Write-Host "[4/7] Deploying standalone binary suite..." -ForegroundColor Cyan
$binSrc = Join-Path $sourceBase "YAMDCC.ConfigEditor\bin\Release\net48\msi_gf63_tuner.exe"
$cfgSrc = Join-Path $sourceBase "YAMDCC.ConfigEditor\bin\Release\net48\msi_gf63_tuner.exe.config"
$icoSrc = Join-Path $sourceBase "YAMDCC.ConfigEditor\fan.ico"

if (-not (Test-Path $binSrc)) {
    Write-Host "  [FAIL] Binary not found at $binSrc! Please run dotnet build first." -ForegroundColor Red
    exit 1
}

# Copy with retry loop
$copied = $false
for ($attempt = 1; $attempt -le 5; $attempt++) {
    try {
        Copy-Item -Path $binSrc -Destination (Join-Path $target "msi_gf63_tuner.exe") -Force -ErrorAction Stop
        $copied = $true
        break
    } catch {
        Write-Host "  [WAIT] Retrying binary install ($attempt/5)..." -ForegroundColor Yellow
        & taskkill.exe /F /IM msi_gf63_tuner.exe /T 2>$null | Out-Null
        Start-Sleep -Milliseconds 600
    }
}

if ($copied) {
    Write-Host "  [OK] Installed msi_gf63_tuner.exe (All-in-one Single Binary)" -ForegroundColor Green
} else {
    Write-Host "  [FAIL] Failed to copy msi_gf63_tuner.exe into $target" -ForegroundColor Red
    exit 1
}

if (Test-Path $cfgSrc) {
    Copy-Item -Path $cfgSrc -Destination (Join-Path $target "msi_gf63_tuner.exe.config") -Force
    Write-Host "  [OK] Installed msi_gf63_tuner.exe.config" -ForegroundColor Green
}

if (Test-Path $icoSrc) {
    Copy-Item -Path $icoSrc -Destination (Join-Path $target "fan.ico") -Force
    Write-Host "  [OK] Installed fan.ico" -ForegroundColor Green
}

# 6. Configure ProgramData (5-profile suite and driverless WMI2 mode)
Write-Host "[5/7] Configuring unified 5-profile performance matrix in ProgramData..." -ForegroundColor Cyan
$profSrc = Join-Path $sourceBase "Configs-V2\MSI-Thin-GF63-12HW.xml"
$profDst = "C:\ProgramData\Sparronator9999\YAMDCC\CurrentConfigV2.xml"
$profDir = [System.IO.Path]::GetDirectoryName($profDst)
if (-not (Test-Path $profDir)) {
    New-Item -ItemType Directory -Path $profDir -Force | Out-Null
}
if (Test-Path $profSrc) {
    Copy-Item -Path $profSrc -Destination $profDst -Force
    Write-Host "  [OK] Installed 5-profile curve matrix (Balanced, Performance, Sustained 35W, Silent, Super Battery)" -ForegroundColor Green
}

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
Write-Host "  [OK] Enforced driverless WMI2 mode (zero kernel drivers)" -ForegroundColor Green

# 7. Create/Refresh Desktop and Start Menu shortcuts
Write-Host "[6/7] Updating Desktop and Start Menu shortcuts..." -ForegroundColor Cyan
$wshShell = New-Object -ComObject WScript.Shell
$iconTarget = if (Test-Path (Join-Path $target "fan.ico")) { "$target\fan.ico,0" } else { "$target\msi_gf63_tuner.exe,0" }

# Desktop shortcut
try {
    $desktopPath = [System.Environment]::GetFolderPath('Desktop')
    Remove-Item -Path (Join-Path $desktopPath "ConfigEditor.lnk") -Force -ErrorAction SilentlyContinue
    $desktopShortcut = $wshShell.CreateShortcut((Join-Path $desktopPath "MSI GF63 Tuner.lnk"))
    $desktopShortcut.TargetPath = "$target\msi_gf63_tuner.exe"
    $desktopShortcut.WorkingDirectory = $target
    $desktopShortcut.Description = "MSI GF63 Tuner (Thin 12HW)"
    $desktopShortcut.IconLocation = $iconTarget
    $desktopShortcut.Save()
    Write-Host "  [OK] Desktop shortcut updated: MSI GF63 Tuner.lnk" -ForegroundColor Green
} catch {
    Write-Host "  [WARN] Could not update Desktop shortcut: $_" -ForegroundColor DarkYellow
}

# Start Menu shortcut
try {
    $startMenuPath = [System.Environment]::GetFolderPath('CommonPrograms')
    Remove-Item -Path (Join-Path $startMenuPath "ConfigEditor.lnk") -Force -ErrorAction SilentlyContinue
    $smShortcut = $wshShell.CreateShortcut((Join-Path $startMenuPath "MSI GF63 Tuner.lnk"))
    $smShortcut.TargetPath = "$target\msi_gf63_tuner.exe"
    $smShortcut.WorkingDirectory = $target
    $smShortcut.Description = "MSI GF63 Tuner (Thin 12HW)"
    $smShortcut.IconLocation = $iconTarget
    $smShortcut.Save()
    Write-Host "  [OK] Start Menu shortcut updated: MSI GF63 Tuner.lnk" -ForegroundColor Green
} catch {
    Write-Host "  [WARN] Could not update Start Menu shortcut: $_" -ForegroundColor DarkYellow
}

# Flush Windows Explorer icon cache so new icon appears immediately
try {
    $code = @'
    using System;
    using System.Runtime.InteropServices;
    public class ShellIconRefresh {
        [DllImport("shell32.dll")]
        public static extern void SHChangeNotify(int wEventId, int uFlags, IntPtr dwItem1, IntPtr dwItem2);
        public static void Refresh() {
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
        }
    }
'@
    Add-Type -TypeDefinition $code -ErrorAction SilentlyContinue
    [ShellIconRefresh]::Refresh()
    Write-Host "  [OK] Flushed Windows Shell icon cache" -ForegroundColor Green
} catch {}

# 8. Launch Tuner
Write-Host "[7/7] Launching MSI GF63 Tuner..." -ForegroundColor Cyan
Start-Process "$target\msi_gf63_tuner.exe"

Write-Host "==========================================================" -ForegroundColor Green
Write-Host "  SUCCESS: Standalone MSI GF63 Tuner deployed and active!" -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
Start-Sleep -Seconds 1
