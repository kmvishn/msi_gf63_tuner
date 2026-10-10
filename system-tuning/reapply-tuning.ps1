# =============================================================================
#  reapply-tuning.ps1  --  re-applies anything that got reset
#  IDEMPOTENT: safe to run repeatedly. Skips whatever is already correct.
#  RUN AS ADMINISTRATOR.
#  Companion to TUNING-LOG.txt
#
#  Usage:
#    .\reapply-tuning.ps1                     # Section 1 items only (default)
#    .\reapply-tuning.ps1 -IncludeBaseline    # also restore the 39 disabled
#                                             #   services + fsutil baseline
#    .\reapply-tuning.ps1 -WhatIf             # show what would change, do nothing
# =============================================================================
[CmdletBinding(SupportsShouldProcess)]
param(
    [switch]$IncludeBaseline
)

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole('Administrators')) {
    Write-Host "ERROR: must run as Administrator. Right-click -> Run as administrator." -ForegroundColor Red
    exit 1
}

$changed = 0; $already = 0
function Did($m)  { $script:changed++; Write-Host "  APPLIED  $m" -ForegroundColor Green }
function Same($m) { $script:already++; Write-Host "  ok       $m" -ForegroundColor DarkGray }
function Oops($m) { Write-Host "  FAILED   $m" -ForegroundColor Red }

Write-Host ""
Write-Host "=== REAPPLY TUNING -- $(Get-Date -Format 'yyyy-MM-dd HH:mm') ===" -ForegroundColor White
Write-Host ""

# --- [1] Pagefile ---------------------------------------------------------
try {
    $pf = Get-CimInstance Win32_PageFileSetting -EA 0
    if ($pf -and $pf.InitialSize -eq 4096 -and $pf.MaximumSize -eq 16384) {
        Same '[1] pagefile 4096/16384'
    } elseif ($PSCmdlet.ShouldProcess('pagefile','set 4096/16384')) {
        $cs = Get-CimInstance Win32_ComputerSystem
        if ($cs.AutomaticManagedPagefile) { Set-CimInstance -InputObject $cs -Property @{AutomaticManagedPagefile=$false} }
        Start-Sleep -Milliseconds 500
        $pf = Get-CimInstance Win32_PageFileSetting -EA 0
        if ($pf) { Set-CimInstance -InputObject $pf -Property @{InitialSize=4096; MaximumSize=16384} }
        else { New-CimInstance -ClassName Win32_PageFileSetting -Property @{Name='C:\pagefile.sys';InitialSize=4096;MaximumSize=16384} | Out-Null }
        Did '[1] pagefile 4096/16384  (REBOOT REQUIRED to resize the file)'
    }
} catch { Oops "[1] pagefile: $($_.Exception.Message)" }

# --- [2] Max processor state ---------------------------------------------
try {
    $ac = (powercfg /query SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMAX | Select-String 'Current AC').Line
    if ($ac -match '0x00000064') { Same '[2] max processor state AC = 100%' }
    elseif ($PSCmdlet.ShouldProcess('PROCTHROTTLEMAX','set 100')) {
        powercfg /setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMAX 100 | Out-Null
        powercfg /setactive SCHEME_CURRENT | Out-Null
        Did '[2] max processor state AC -> 100%'
    }
} catch { Oops "[2] $($_.Exception.Message)" }

# --- [3] Fast Startup -----------------------------------------------------
try {
    $k = 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power'
    if ((Get-ItemProperty $k -EA 0).HiberbootEnabled -eq 0) { Same '[3] Fast Startup already off' }
    elseif ($PSCmdlet.ShouldProcess('HiberbootEnabled','set 0')) {
        Set-ItemProperty $k -Name HiberbootEnabled -Value 0 -Type DWord
        powercfg /h off 2>&1 | Out-Null
        Did '[3] Fast Startup -> off'
    }
} catch { Oops "[3] $($_.Exception.Message)" }

# --- [4] IP Helper + Tailscale  (highest operational impact) --------------
try {
    $ip = Get-Service iphlpsvc -EA 0
    if ($ip.StartType -eq 'Automatic' -and $ip.Status -eq 'Running') { Same '[4] iphlpsvc Automatic/Running' }
    elseif ($PSCmdlet.ShouldProcess('iphlpsvc','enable + start')) {
        Set-Service iphlpsvc -StartupType Automatic
        Start-Service iphlpsvc -EA 0
        Did '[4] iphlpsvc -> Automatic + started'
        if (Get-Service Tailscale -EA 0) { Restart-Service Tailscale -Force -EA 0; Did '[4] Tailscale restarted' }
    }
} catch { Oops "[4] $($_.Exception.Message)" }

# --- [5] Shadow storage ---------------------------------------------------
try {
    $ss = (vssadmin list shadowstorage 2>&1 | Select-String 'Maximum Shadow Copy Storage space').Line
    if ($ss -match '10\.0 GB') { Same '[5] shadow storage 10 GB' }
    elseif ($PSCmdlet.ShouldProcess('shadowstorage','resize 10GB')) {
        Enable-ComputerRestore -Drive 'C:\' -EA 0
        vssadmin resize shadowstorage /for=C: /on=C: /maxsize=10GB | Out-Null
        Did '[5] shadow storage -> 10 GB + System Restore enabled'
    }
} catch { Oops "[5] $($_.Exception.Message)" }

# --- [6] Intel Computing Improvement Program ------------------------------
try {
    $roots = @('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
               'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*')
    $app = Get-ItemProperty $roots -EA 0 | Where-Object DisplayName -like '*Computing Improvement*'
    if (-not $app) { Same '[6] Intel Computing Improvement Program absent' }
    elseif ($PSCmdlet.ShouldProcess('Intel Computing Improvement Program','uninstall')) {
        foreach ($a in $app) {
            if ($a.PSChildName -match '^\{[0-9A-Fa-f-]+\}$') {
                Start-Process msiexec.exe -ArgumentList "/x $($a.PSChildName) /qn /norestart" -Wait | Out-Null
            }
        }
        Did '[6] Intel Computing Improvement Program -> uninstalled'
    }
} catch { Oops "[6] $($_.Exception.Message)" }

# --- [7] Defender exclusions ----------------------------------------------
try {
    $ex = @((Get-MpPreference).ExclusionPath)
    foreach ($p in @("$env:USERPROFILE\.gradle", "$env:USERPROFILE\.android")) {
        if ($ex -contains $p) { Same "[7] exclusion $(Split-Path $p -Leaf)" }
        elseif ($PSCmdlet.ShouldProcess($p,'add Defender exclusion')) {
            Add-MpPreference -ExclusionPath $p; Did "[7] exclusion + $p"
        }
    }
} catch { Oops "[7] $($_.Exception.Message)" }

# --- [9] Wi-Fi MIMO No SMPS -----------------------------------------------
try {
    $m = Get-NetAdapterAdvancedProperty -Name WiFi -AllProperties -EA 0 | Where-Object RegistryKeyword -eq 'MIMOPowerSaveMode'
    if ($m -and $m.RegistryValue[0] -eq 3) { Same '[9] Wi-Fi MIMO = No SMPS' }
    elseif ($m -and $PSCmdlet.ShouldProcess('WiFi MIMO','set No SMPS')) {
        Set-NetAdapterAdvancedProperty -Name WiFi -DisplayName 'MIMO Power Save Mode' -DisplayValue 'No SMPS'
        Did '[9] Wi-Fi MIMO -> No SMPS'
    } elseif (-not $m) { Oops '[9] WiFi adapter / property not found' }
} catch { Oops "[9] $($_.Exception.Message)" }

# --- BASELINE (optional) --------------------------------------------------
if ($IncludeBaseline) {
    Write-Host ""
    Write-Host "--- Restoring Section 4 baseline ---" -ForegroundColor White

    foreach ($fs in @(@('disable8dot3','1'), @('memoryusage','2'), @('disabledeletenotify','0'))) {
        if ($PSCmdlet.ShouldProcess("fsutil $($fs[0])", "set $($fs[1])")) {
            fsutil behavior set $fs[0] $fs[1] | Out-Null; Did "[4.1] fsutil $($fs[0]) = $($fs[1])"
        }
    }

    $svc = @('CDPSvc','DiagTrack','dmwappushservice','dptftcs','DSAService',
      'DSAUpdateService','GameInputSvc','IntelGFXFWupdateTool','InventorySvc',
      'lfsvc','lmhosts','MSI Sendevsvc','NahimicService','NetTcpPortSharing',
      'O+Connect Service','OplusRemoteService','PcaSvc','QWAVE','RemoteAccess',
      'RemoteRegistry','shpamsvc','Spooler','SSDPSRV','ssh-agent','StiSvc',
      'TrkWks','tzautoupdate','WbioSrvc','wercplsupport','WerSvc',
      'whesvc','wisvc','WMIRegistrationService','WSAIFabricSvc','WSearch',
      'XblAuthManager','XblGameSave','XboxGipSvc','XboxNetApiSvc')
    $n = 0
    foreach ($s in $svc) {
        $o = Get-Service $s -EA 0
        if ($o -and $o.StartType -ne 'Disabled' -and $PSCmdlet.ShouldProcess($s,'disable')) {
            try { Set-Service $s -StartupType Disabled -EA Stop; $n++ } catch {}
        }
    }
    $installed = @($svc | Where-Object { Get-Service $_ -EA 0 }).Count
    if ($n) { Did "[4.5] disabled $n service(s)" } else { Same "[4.5] all $installed installed baseline service(s) already disabled" }
    Write-Host "  NOTE: ssh-agent is in this list. If you use SSH keys via the agent," -ForegroundColor Yellow
    Write-Host "        re-enable it:  Set-Service ssh-agent -StartupType Manual" -ForegroundColor Yellow
}

# --- Cross-Device Resume note ---------------------------------------------
Write-Host ""
Write-Host "[8] Cross-Device Resume is an HKCU setting and CANNOT be fixed from this" -ForegroundColor Yellow
Write-Host "    elevated session (different user hive). Run this as YOUR normal user:" -ForegroundColor Yellow
Write-Host "      `$k='HKCU:\Software\Microsoft\Windows\CurrentVersion\CrossDeviceResume\Configuration'" -ForegroundColor Cyan
Write-Host "      Set-ItemProperty `$k -Name IsResumeAllowed -Value 0 -Type DWord" -ForegroundColor Cyan
Write-Host "      Set-ItemProperty `$k -Name IsOneDriveResumeAllowed -Value 0 -Type DWord" -ForegroundColor Cyan

Write-Host ""
Write-Host ("RESULT:  {0} applied, {1} already correct" -f $changed, $already) -ForegroundColor Green
if ((Get-CimInstance Win32_PageFileUsage).AllocatedBaseSize -lt 4096) {
    Write-Host "REBOOT PENDING: pagefile still at $((Get-CimInstance Win32_PageFileUsage).AllocatedBaseSize) MB" -ForegroundColor Yellow
}
Write-Host ""
