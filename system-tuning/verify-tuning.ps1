# =============================================================================
#  verify-tuning.ps1  --  checks every tuned item against its expected value
#  Safe: READ-ONLY. Changes nothing. Run normally or elevated.
#  Elevate for full detail (Defender exclusions + shadow storage need admin).
#  Companion to TUNING-LOG.txt
# =============================================================================

$elevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole('Administrators')
$pass = 0; $fail = 0; $skip = 0

function Check($id, $name, $expected, $actual, $hint) {
    $ok = "$actual" -eq "$expected"
    if ($ok) { $script:pass++; $tag = 'OK      '; $col = 'Green' }
    else     { $script:fail++; $tag = 'DRIFTED '; $col = 'Red' }
    Write-Host ("{0} [{1}] {2}" -f $tag, $id, $name) -ForegroundColor $col
    if (-not $ok) {
        Write-Host ("           expected : {0}" -f $expected) -ForegroundColor DarkGray
        Write-Host ("           actual   : {0}" -f $actual)   -ForegroundColor Yellow
        if ($hint) { Write-Host ("           fix      : {0}" -f $hint) -ForegroundColor Cyan }
    }
}
function Skip($id, $name, $why) {
    $script:skip++
    Write-Host ("SKIPPED  [{0}] {1}  ({2})" -f $id, $name, $why) -ForegroundColor DarkGray
}

Write-Host ""
Write-Host "=== SYSTEM TUNING VERIFICATION -- $(Get-Date -Format 'yyyy-MM-dd HH:mm') ===" -ForegroundColor White
Write-Host "Elevated: $elevated" -ForegroundColor DarkGray
Write-Host ""
Write-Host "--- Applied changes (Section 1) ---" -ForegroundColor White

# [1] Pagefile
$pf = Get-CimInstance Win32_PageFileSetting -EA 0
$pfVal = if ($pf) { "$($pf.InitialSize)/$($pf.MaximumSize)" } else { 'system-managed' }
Check 1 'Pagefile 4096/16384 MB' '4096/16384' $pfVal 'see TUNING-LOG.txt item [1], or run reapply-tuning.ps1'
$alloc = (Get-CimInstance Win32_PageFileUsage).AllocatedBaseSize
if ($alloc -lt 4096) { Write-Host "           NOTE: allocated is ${alloc} MB -- REBOOT PENDING to resize the file" -ForegroundColor Yellow }

# [2] Max processor state
$ac = (powercfg /query SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMAX | Select-String 'Current AC').Line -replace '.*: ',''
Check 2 'Max processor state AC = 100%' '0x00000064' $ac 'powercfg /setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMAX 100; powercfg /setactive SCHEME_CURRENT'

# [3] Fast Startup
$hb = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power' -EA 0).HiberbootEnabled
Check 3 'Fast Startup disabled' '0' $hb "Set-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power' -Name HiberbootEnabled -Value 0"

# [4] IP Helper  (most operationally important)
$ip = Get-Service iphlpsvc -EA 0
Check 4 'iphlpsvc Automatic (Tailscale dependency)' 'Automatic' $ip.StartType 'Set-Service iphlpsvc -StartupType Automatic; Start-Service iphlpsvc'
Check 4 'iphlpsvc Running' 'Running' $ip.Status 'Start-Service iphlpsvc'
$ts = Get-Service Tailscale -EA 0
if ($ts) { Check 4 'Tailscale Running' 'Running' $ts.Status 'Restart-Service Tailscale -Force' }

# [5] Shadow storage
if ($elevated) {
    $ss = (vssadmin list shadowstorage 2>&1 | Select-String 'Maximum Shadow Copy Storage space').Line
    $tenGB = $ss -match '10\.0 GB'
    Check 5 'Shadow storage 10 GB on C:' 'True' $tenGB 'vssadmin resize shadowstorage /for=C: /on=C: /maxsize=10GB'
} else { Skip 5 'Shadow storage' 'needs elevation' }

# [6] Intel Computing Improvement Program
$esrv = @(Get-Process esrv, esrv_svc -EA 0).Count
Check 6 'Intel Computing Improvement Prog. absent' '0' $esrv 'msiexec /x {F2D45F25-BE06-4324-AC99-426FAF9B2E63} /qn /norestart'

# [7] Defender exclusions
if ($elevated) {
    $ex = @((Get-MpPreference).ExclusionPath)
    foreach ($want in @("$env:USERPROFILE\.gradle", "$env:USERPROFILE\.android")) {
        Check 7 "Defender exclusion: $(Split-Path $want -Leaf)" 'True' ($ex -contains $want) "Add-MpPreference -ExclusionPath '$want'"
    }
} else { Skip 7 'Defender exclusions' 'needs elevation' }

# [8] Cross-Device Resume  (least durable -- see TUNING-LOG.txt)
$cdr = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\CrossDeviceResume\Configuration' -EA 0
Check 8 'Cross-Device Resume off' '0' $cdr.IsResumeAllowed        'see reapply-tuning.ps1 (runs as normal user)'
Check 8 'OneDrive Resume off'     '0' $cdr.IsOneDriveResumeAllowed 'see reapply-tuning.ps1 (runs as normal user)'

# [9] Wi-Fi MIMO
$mimo = Get-NetAdapterAdvancedProperty -Name WiFi -AllProperties -EA 0 |
        Where-Object RegistryKeyword -eq 'MIMOPowerSaveMode'
$mimoVal = if ($mimo) { $mimo.RegistryValue[0] } else { 'adapter not found' }
Check 9 'Wi-Fi MIMO Power Save = No SMPS' '3' $mimoVal "Set-NetAdapterAdvancedProperty -Name WiFi -DisplayName 'MIMO Power Save Mode' -DisplayValue 'No SMPS'"

Write-Host ""
Write-Host "--- Pre-existing baseline (Section 4) ---" -ForegroundColor White

# 4.1 filesystem
$f8 = (fsutil behavior query disable8dot3) -join ' '
Check '4.1' '8dot3 creation disabled' 'True' ($f8 -match 'is: 1') 'fsutil behavior set disable8dot3 1'
$fm = (fsutil behavior query memoryusage) -join ' '
Check '4.1' 'NTFS MemoryUsage = 2' 'True' ($fm -match 'MemoryUsage = 2') 'fsutil behavior set memoryusage 2'
$ft = (fsutil behavior query disabledeletenotify) -join ' '
Check '4.1' 'TRIM enabled' 'True' ($ft -match 'NTFS DisableDeleteNotify = 0') 'fsutil behavior set disabledeletenotify 0'

# 4.2 visual fx
$vfx = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects' -EA 0).VisualFXSetting
Check '4.2' 'Visual effects = best performance' '2' $vfx "Set-ItemProperty 'HKCU:\...\VisualEffects' -Name VisualFXSetting -Value 2"

# 4.3 wireless power plan
$wl = (powercfg /query SCHEME_CURRENT 19cbb8fa-5279-450e-9fac-8a3d5fedd0c1 12bbebe6-58d6-4636-95bb-3217ef867c1a | Select-String 'Current AC').Line -replace '.*: ',''
Check '4.3' 'Wireless adapter power AC = Max Perf' '0x00000000' $wl 'powercfg /setacvalueindex SCHEME_CURRENT 19cbb8fa-5279-450e-9fac-8a3d5fedd0c1 12bbebe6-58d6-4636-95bb-3217ef867c1a 0'

# 4.5 disabled services
# Canonical baseline list (same as reapply-tuning.ps1). Services are OPTIONAL:
# if a service is installed it must be Disabled; if it's absent (uninstalled by
# a driver/app update, e.g. MSI Sendevsvc) it is simply skipped, not a failure.
$baselineServices = @(
    'CDPSvc','DiagTrack','dmwappushservice','dptftcs','DSAService',
    'DSAUpdateService','GameInputSvc','IntelGFXFWupdateTool','InventorySvc',
    'lfsvc','lmhosts','MSI Sendevsvc','NahimicService','NetTcpPortSharing',
    'O+Connect Service','OplusRemoteService','PcaSvc','QWAVE','RemoteAccess',
    'RemoteRegistry','shpamsvc','Spooler','SSDPSRV','ssh-agent','StiSvc',
    'TrkWks','tzautoupdate','WbioSrvc','wercplsupport','WerSvc',
    'whesvc','wisvc','WMIRegistrationService','WSAIFabricSvc','WSearch',
    'XblAuthManager','XblGameSave','XboxGipSvc','XboxNetApiSvc')

$present  = @($baselineServices | ForEach-Object { Get-Service $_ -EA 0 } | Where-Object { $_ })
$absent   = @($baselineServices | Where-Object { -not (Get-Service $_ -EA 0) })
$disabledPresent = @($present | Where-Object { $_.StartType -eq 'Disabled' })

# Expected == number of installed baseline services; passes when all are disabled.
Check '4.5' "Baseline services disabled ($($present.Count) installed, $($absent.Count) not installed)" `
      $present.Count $disabledPresent.Count 'run reapply-tuning.ps1 -IncludeBaseline'

# Name any installed baseline service that is NOT disabled (the real drift).
foreach ($s in $present) {
    if ($s.StartType -ne 'Disabled') {
        Write-Host ("           drifted  : {0} = {1}  (should be Disabled)" -f $s.Name, $s.StartType) -ForegroundColor Yellow
    }
}
if ($absent.Count) {
    Write-Host ("           skipped  : not installed -> {0}" -f ($absent -join ', ')) -ForegroundColor DarkGray
}
# Spot-check the five most important ones individually.
foreach ($crit in @('DiagTrack','WSearch','DSAService','DSAUpdateService')) {
    $s = Get-Service $crit -EA 0
    if ($s) { Check '4.5' "  $crit disabled" 'Disabled' $s.StartType "Set-Service $crit -StartupType Disabled" }
}

Write-Host ""
Write-Host "--- Advanced tuning spot-checks (Section 7) ---" -ForegroundColor White

# [15] Memory Compression is hosted by SysMain -- SysMain must stay ON
#      (disabling it silently kills compression; see Section 8 drift event)
$sm = Get-Service SysMain -EA 0
Check 15 'SysMain Automatic (hosts Memory Compression)' 'Automatic' $sm.StartType 'Set-Service SysMain -StartupType Automatic; Start-Service SysMain'
if ($elevated) {
    $mm = Get-MMAgent
    Check 15 'Memory Compression enabled'      'True'  $mm.MemoryCompression            'Enable-MMAgent -MemoryCompression'
    Check 15 'App prelaunch off (SysMain lean)' 'False' $mm.ApplicationPreLaunch         'Disable-MMAgent -ApplicationPreLaunch'
    Check 15 'App prefetch off (SysMain lean)'  'False' $mm.ApplicationLaunchPrefetching 'Disable-MMAgent -ApplicationLaunchPrefetching'
} else { Skip 15 'Memory Compression / prefetch' 'needs elevation' }

Write-Host ""
Write-Host "--- Idle-RAM / background debloat (Section 8) ---" -ForegroundColor White

# [20] / [21] services
$mb = Get-Service MapsBroker -EA 0
if ($mb) { Check 20 'MapsBroker Disabled' 'Disabled' $mb.StartType 'Set-Service MapsBroker -StartupType Disabled' }
else     { Skip 20 'MapsBroker' 'not installed' }
$ics = Get-Service SharedAccess -EA 0
if ($ics) { Check 21 'SharedAccess (ICS) Manual  (Running is fine -- trigger-started)' 'Manual' $ics.StartType 'Set-Service SharedAccess -StartupType Manual' }
else      { Skip 21 'SharedAccess' 'not installed' }

# [22] leftover scheduled tasks (absent = skipped, not drift)
$s8tasks = @(
    '\Microsoft\Windows\Maps\MapsToastTask',
    '\Microsoft\Windows\Maps\MapsUpdateTask',
    '\Microsoft\XblGameSave\XblGameSaveTask',
    '\Microsoft\Windows\Windows Error Reporting\QueueReporting',
    '\Microsoft\Windows\Application Experience\PcaPatchDbTask',
    '\Microsoft\Windows\Shell\FamilySafetyMonitor',
    '\Microsoft\Windows\Shell\FamilySafetyRefreshTask',
    '\NahimicTask32',
    '\NahimicTask64')
$s8present = @($s8tasks | ForEach-Object {
    Get-ScheduledTask -TaskName (Split-Path $_ -Leaf) -TaskPath ((Split-Path $_ -Parent).TrimEnd('\') + '\') -EA 0 } | Where-Object { $_ })
$s8off = @($s8present | Where-Object State -eq 'Disabled')
Check 22 "Leftover scheduled tasks disabled ($($s8present.Count) present)" $s8present.Count $s8off.Count 'run advanced-tuning.ps1'
foreach ($t in $s8present) {
    if ($t.State -ne 'Disabled') { Write-Host ("           drifted  : {0}{1} = {2}" -f $t.TaskPath, $t.TaskName, $t.State) -ForegroundColor Yellow }
}

# [23]-[25] machine policies
$edge = Get-ItemProperty 'HKLM:\SOFTWARE\Policies\Microsoft\Edge' -EA 0
Check 23 'Edge Startup Boost off (policy)'  '0' $edge.StartupBoostEnabled   'run advanced-tuning.ps1'
Check 23 'Edge background mode off (policy)' '0' $edge.BackgroundModeEnabled 'run advanced-tuning.ps1'
$do = (Get-ItemProperty 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization' -EA 0).DODownloadMode
Check 24 'Delivery Optimization P2P off (DODownloadMode=0)' '0' $do 'run advanced-tuning.ps1'
$gdvr = (Get-ItemProperty 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\GameDVR' -EA 0).AllowGameDVR
Check 25 'Game DVR off (policy)' '0' $gdvr 'run advanced-tuning.ps1'

# [26] taskbar search (HKCU)
$sb = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Search' -EA 0).SearchboxTaskbarMode
Check 26 'Taskbar search hidden' '0' $sb "Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Search' -Name SearchboxTaskbarMode -Value 0"

Write-Host ""
Write-Host "--- Open items (Section 3) -- informational, not failures ---" -ForegroundColor White
$arc = Get-PnpDevice -Class Display -EA 0 | Where-Object FriendlyName -match 'Arc'
if ($arc) { Write-Host ("  Arc A370M dGPU status : {0}" -f $arc.Status) -ForegroundColor $(if($arc.Status -eq 'OK'){'Green'}else{'Yellow'}) }
$wifi = Get-NetAdapter -Name WiFi -EA 0
if ($wifi) { Write-Host ("  Wi-Fi link speed      : {0}  (~400 Mbps = 2.4 GHz, ~1200 = 5 GHz)" -f $wifi.LinkSpeed) -ForegroundColor Yellow }
$la = (fsutil behavior query disablelastaccess) -join ' '
Write-Host ("  Last Access Time      : {0}" -f $(if ($la -match '= 1') {'disabled (tuned)'} else {'ENABLED (open item 3f)'})) -ForegroundColor Yellow
$aspm = (powercfg /query SCHEME_CURRENT SUB_PCIEXPRESS ASPM | Select-String 'Current AC').Line -replace '.*: ',''
Write-Host ("  PCIe ASPM on AC       : {0}  (0x0 = Off is the tuned value, open item 3e)" -f $aspm) -ForegroundColor Yellow

Write-Host ""
Write-Host ("RESULT:  {0} OK   {1} DRIFTED   {2} SKIPPED" -f $pass, $fail, $skip) -ForegroundColor $(if($fail -eq 0){'Green'}else{'Red'})
if ($fail -gt 0) { Write-Host "Run reapply-tuning.ps1 as administrator to fix." -ForegroundColor Cyan }
Write-Host ""
