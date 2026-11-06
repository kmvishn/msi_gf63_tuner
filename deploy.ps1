# Self-elevate script to Administrator if not already elevated
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "Requesting Administrator privileges..." -ForegroundColor Yellow
    Start-Process powershell.exe -Verb RunAs -ArgumentList "-ExecutionPolicy Bypass -File `"$PSCommandPath`""
    exit
}

$ErrorActionPreference = 'Stop'
$target = "C:\Program Files\kmvishn\msi_gf63_tuner"
$sourceBase = "$PSScriptRoot\msi_gf63_tuner"

Write-Host "=== Installing Standalone MSI GF63 Tuner ===" -ForegroundColor Red

Write-Host "Stopping running tuner instances..." -ForegroundColor Cyan
Stop-Process -Name msi_gf63_tuner, ConfigEditor, HotkeyHandler -Force -ErrorAction SilentlyContinue

Write-Host "Stopping yamdccsvc service..." -ForegroundColor Cyan
Stop-Service yamdccsvc -Force -ErrorAction SilentlyContinue

Start-Sleep -Milliseconds 1200

# Clean up obsolete legacy executables
Remove-Item -Path (Join-Path $target "HotkeyHandler.exe") -Force -ErrorAction SilentlyContinue
Remove-Item -Path (Join-Path $target "ConfigEditor.exe") -Force -ErrorAction SilentlyContinue

Write-Host "Deploying updated binaries to $target..." -ForegroundColor Cyan
$files = @(
    @("YAMDCC.ConfigEditor\bin\Release\net48\msi_gf63_tuner.exe", "msi_gf63_tuner.exe"),
    @("YAMDCC.ConfigEditor\bin\Release\net48\msi_gf63_tuner.exe.config", "msi_gf63_tuner.exe.config"),
    @("YAMDCC.Service\bin\Release\net48\yamdccsvc.exe", "yamdccsvc.exe"),
    @("YAMDCC.CLI\bin\Release\net48\yamdcc.exe", "yamdcc.exe"),
    @("YAMDCC.Common\bin\Release\net48\YAMDCC.Common.dll", "YAMDCC.Common.dll"),
    @("YAMDCC.IPC\bin\Release\net48\YAMDCC.IPC.dll", "YAMDCC.IPC.dll"),
    @("YAMDCC.ECAccess\bin\Release\net48\YAMDCC.ECAccess.dll", "YAMDCC.ECAccess.dll")
)

foreach ($f in $files) {
    $src = Join-Path $sourceBase $f[0]
    $dst = Join-Path $target $f[1]
    if (Test-Path $src) {
        Copy-Item -Path $src -Destination $dst -Force
        Write-Host "  [OK] Installed $($f[1])" -ForegroundColor Green
    } else {
        Write-Host "  [SKIP] Not found: $src" -ForegroundColor DarkGray
    }
}

Write-Host "Updating 5-profile unified fan configuration in ProgramData..." -ForegroundColor Cyan
$cfgSrc = Join-Path $sourceBase "Configs-V2\MSI-Thin-GF63-12HW.xml"
$cfgDst = "C:\ProgramData\Sparronator9999\YAMDCC\CurrentConfigV2.xml"
$cfgDir = [System.IO.Path]::GetDirectoryName($cfgDst)
if (-not (Test-Path $cfgDir)) {
    New-Item -ItemType Directory -Path $cfgDir -Force | Out-Null
}
if (Test-Path $cfgSrc) {
    Copy-Item -Path $cfgSrc -Destination $cfgDst -Force
    Write-Host "  [OK] Installed 5-profile suite (Balanced, Performance, Sustained 35W, Silent, Super Battery)" -ForegroundColor Green
}

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
    Write-Host "  [OK] Desktop shortcut created: MSI GF63 Tuner.lnk" -ForegroundColor Green
} catch {
    Write-Host "  [WARN] Could not update desktop shortcut: $_" -ForegroundColor DarkYellow
}

Write-Host "Starting yamdccsvc service..." -ForegroundColor Cyan
Start-Service yamdccsvc -ErrorAction SilentlyContinue
$svc = Get-Service yamdccsvc
Write-Host "Service status: $($svc.Status)" -ForegroundColor Yellow

Write-Host "Launching standalone MSI GF63 Tuner..." -ForegroundColor Cyan
Start-Process "$target\msi_gf63_tuner.exe"

Write-Host "Done!" -ForegroundColor Green
Start-Sleep -Seconds 2
