# =============================================================================
#  pin-gpu-drivers.ps1  --  stop Windows Update replacing/downgrading drivers
#  IDEMPOTENT + self-verifying. RUN AS ADMINISTRATOR.
#  Companion to TUNING-LOG.txt  (item [19], Section 7)
#
#  WHY: Windows Update re-offers an older WHQL Intel graphics driver and
#  overwrites the newer one installed manually from intel.com -- for BOTH the
#  Iris Xe iGPU and the Arc A370M dGPU (one shared Intel package).
#
#  DEFAULT = GLOBAL exclude (recommended): WU stops delivering ANY driver, your
#  manual installs always work, and there is NO risk of stranding a device on
#  "Microsoft Basic Display Adapter" (Code 48). Security/quality/feature updates
#  are unaffected.
#
#  *** ORDER OF OPERATIONS ***
#  Install the driver you WANT from intel.com and REBOOT first, THEN run this.
#
#  Usage:
#    .\pin-gpu-drivers.ps1                  # apply global exclude (recommended)
#    .\pin-gpu-drivers.ps1 -IncludePerDevice# ALSO add a per-device deny for the
#                                           #   2 Intel GPUs (belt-and-suspenders;
#                                           #   NOTE: per-device also blocks YOUR
#                                           #   manual installs -- see -LiftPerDevice)
#    .\pin-gpu-drivers.ps1 -Revert          # undo everything this script set
#    .\pin-gpu-drivers.ps1 -LiftPerDevice   # temporarily remove ONLY the per-device
#                                           #   deny so you can manually update, then
#                                           #   re-run with -IncludePerDevice to re-pin
#    .\pin-gpu-drivers.ps1 -WhatIf          # preview, change nothing
# =============================================================================
[CmdletBinding(SupportsShouldProcess)]
param(
    [switch]$IncludePerDevice,
    [switch]$Revert,
    [switch]$LiftPerDevice
)

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole('Administrators')) {
    Write-Host "ERROR: must run as Administrator. Right-click -> Run as administrator." -ForegroundColor Red
    exit 1
}

$changed = 0; $already = 0
function Did($m)  { $script:changed++; Write-Host "  APPLIED  $m" -ForegroundColor Green }
function Same($m) { $script:already++; Write-Host "  ok       $m" -ForegroundColor DarkGray }
function Oops($m) { Write-Host "  FAILED   $m" -ForegroundColor Red }

# Broad hardware IDs (no SUBSYS/REV) so they match any revision WU offers.
$GPU_IDS = @('PCI\VEN_8086&DEV_46A6',   # Intel Iris Xe (Alder Lake-P iGPU)
             'PCI\VEN_8086&DEV_5693')   # Intel Arc A370M (dGPU)

$WU   = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate'
$DS   = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\DriverSearching'
$RES  = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\DeviceInstall\Restrictions'
$DENY = "$RES\DenyDeviceIDs"

function Ensure-Key($p) { if (-not (Test-Path $p)) { New-Item -Path $p -Force | Out-Null } }

Write-Host ""
Write-Host "=== PIN GPU DRIVERS vs WINDOWS UPDATE -- $(Get-Date -Format 'yyyy-MM-dd HH:mm') ===" -ForegroundColor White
Write-Host ""

if ($Revert) {
    # ---- REVERT everything ----
    try {
        if ((Get-ItemProperty $WU -Name ExcludeWUDriversInQualityUpdate -EA 0)) {
            if ($PSCmdlet.ShouldProcess('ExcludeWUDriversInQualityUpdate','remove')) {
                Remove-ItemProperty $WU -Name ExcludeWUDriversInQualityUpdate -EA 0; Did 'removed ExcludeWUDriversInQualityUpdate' }
        } else { Same 'ExcludeWUDriversInQualityUpdate not set' }
        if ($PSCmdlet.ShouldProcess('SearchOrderConfig','restore default 1')) {
            Ensure-Key $DS; Set-ItemProperty $DS -Name SearchOrderConfig -Value 1 -Type DWord; Did 'SearchOrderConfig -> 1 (WU drivers allowed again)' }
        if (Test-Path $RES) {
            if ($PSCmdlet.ShouldProcess('DeviceInstall\Restrictions','remove deny policy')) {
                Remove-Item $RES -Recurse -Force -EA 0; Did 'removed DeviceInstall deny policy' }
        } else { Same 'no per-device deny policy present' }
    } catch { Oops "revert: $($_.Exception.Message)" }
    Write-Host ""
    Write-Host ("RESULT:  {0} reverted, {1} already default" -f $changed, $already) -ForegroundColor Green
    Write-Host ""
    return
}

if ($LiftPerDevice) {
    # ---- Temporarily remove ONLY the per-device deny (keep global exclude) ----
    if (Test-Path $RES) {
        if ($PSCmdlet.ShouldProcess('DeviceInstall\Restrictions','remove to allow a manual GPU driver install')) {
            Remove-Item $RES -Recurse -Force -EA 0
            Did 'per-device deny LIFTED -- install your driver now, reboot, then re-run with -IncludePerDevice'
        }
    } else { Same 'no per-device deny present (nothing to lift)' }
    Write-Host ""
    Write-Host ("RESULT:  {0} changed" -f $changed) -ForegroundColor Green
    Write-Host ""
    return
}

# ---- APPLY: global exclude (recommended, default) ----
try {
    Ensure-Key $WU
    if ((Get-ItemProperty $WU -Name ExcludeWUDriversInQualityUpdate -EA 0).ExcludeWUDriversInQualityUpdate -eq 1) {
        Same 'ExcludeWUDriversInQualityUpdate = 1'
    } elseif ($PSCmdlet.ShouldProcess('ExcludeWUDriversInQualityUpdate','set 1')) {
        Set-ItemProperty $WU -Name ExcludeWUDriversInQualityUpdate -Value 1 -Type DWord
        Did 'ExcludeWUDriversInQualityUpdate -> 1 (WU excludes drivers from quality updates)'
    }
} catch { Oops "global exclude: $($_.Exception.Message)" }

try {
    Ensure-Key $DS
    if ((Get-ItemProperty $DS -Name SearchOrderConfig -EA 0).SearchOrderConfig -eq 0) {
        Same 'SearchOrderConfig = 0 (never search WU for drivers)'
    } elseif ($PSCmdlet.ShouldProcess('SearchOrderConfig','set 0')) {
        Set-ItemProperty $DS -Name SearchOrderConfig -Value 0 -Type DWord
        Did 'SearchOrderConfig -> 0 (Device Installation Settings = No; never auto-fetch from WU)'
    }
} catch { Oops "SearchOrderConfig: $($_.Exception.Message)" }

# ---- APPLY: per-device deny (optional belt-and-suspenders) ----
if ($IncludePerDevice) {
    try {
        Ensure-Key $RES
        # Retroactive=0: leaves the currently-installed driver alone (no Code 48),
        # but blocks any NEW driver install attempt (WU *and* manual) for these IDs.
        Set-ItemProperty $RES -Name DenyDeviceIDs -Value 1 -Type DWord
        Set-ItemProperty $RES -Name DenyDeviceIDsRetroactive -Value 0 -Type DWord
        Ensure-Key $DENY
        # clear old numbered values, then write our two
        Get-Item $DENY | Select-Object -ExpandProperty Property | ForEach-Object { Remove-ItemProperty $DENY -Name $_ -EA 0 }
        $i = 1
        foreach ($id in $GPU_IDS) {
            if ($PSCmdlet.ShouldProcess("DenyDeviceIDs\$i", "= $id")) {
                Set-ItemProperty $DENY -Name "$i" -Value $id -Type String; Did "per-device deny [$i] $id"
            }
            $i++
        }
        Write-Host "  NOTE: per-device deny also blocks YOUR manual installs for these IDs." -ForegroundColor Yellow
        Write-Host "        To update manually later:  .\pin-gpu-drivers.ps1 -LiftPerDevice" -ForegroundColor Cyan
        Write-Host "        then install + reboot, then re-run:  .\pin-gpu-drivers.ps1 -IncludePerDevice" -ForegroundColor Cyan
    } catch { Oops "per-device deny: $($_.Exception.Message)" }
}

# ---- verify ----
Write-Host ""
Write-Host ("RESULT:  {0} applied, {1} already correct" -f $changed, $already) -ForegroundColor Green
Write-Host ""
Write-Host "--- verify ---" -ForegroundColor White
"  ExcludeWUDriversInQualityUpdate : {0}" -f (Get-ItemProperty $WU -Name ExcludeWUDriversInQualityUpdate -EA 0).ExcludeWUDriversInQualityUpdate
"  SearchOrderConfig               : {0}  (0 = never WU drivers)" -f (Get-ItemProperty $DS -Name SearchOrderConfig -EA 0).SearchOrderConfig
if (Test-Path $DENY) {
    $ids = (Get-Item $DENY).Property | ForEach-Object { (Get-ItemProperty $DENY -Name $_).$_ }
    "  Per-device deny IDs             : {0}" -f ($ids -join ', ')
} else { "  Per-device deny                 : (not set -- global exclude only)" }
Write-Host ""
Write-Host "  Reminder: install Intel drivers from intel.com whenever you want;" -ForegroundColor DarkGray
Write-Host "  they are unaffected. WU just won't push its own driver anymore." -ForegroundColor DarkGray
Write-Host ""
