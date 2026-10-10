# =============================================================================
#  tune.ps1  --  MSI GF63 system tuning: verify / apply / GPU pin, one script
#  Companion to TUNING-LOG.txt (the source of truth for every item's "why").
#
#  Every item is defined ONCE as a check + a fix:
#    Verify  runs only the checks           (read-only, no admin needed)
#    Apply   runs the fix for anything that fails its check, then re-checks it
#            (idempotent -- correct items are skipped; needs admin)
#
#  Usage:
#    .\tune.ps1                          # interactive menu
#    .\tune.ps1 verify                   # check everything, change nothing
#    .\tune.ps1 apply                    # fix everything that drifted
#    .\tune.ps1 apply -Group debloat     # only one group (core, baseline,
#                                        #   advanced, debloat, gaming; comma-separate)
#    .\tune.ps1 apply -Item 15,23        # only specific item IDs
#    .\tune.ps1 apply -WhatIf            # show what would change, do nothing
#    .\tune.ps1 apply -SkipWiFiRestart   # don't bounce the Wi-Fi adapter
#    .\tune.ps1 -Gpu Global|PerDevice|Lift|Revert   # item [19] GPU driver pin
#
#  If scripts are blocked:  powershell -ExecutionPolicy Bypass -File tune.ps1
# =============================================================================
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Position = 0)]
    [ValidateSet('Menu', 'Verify', 'Apply')]
    [string]$Mode = 'Menu',
    [ValidateSet('core', 'baseline', 'advanced', 'debloat', 'gaming')]
    [string[]]$Group,
    [string[]]$Item,
    [ValidateSet('Global', 'PerDevice', 'Lift', 'Revert')]
    [string]$Gpu,
    [switch]$SkipWiFiRestart
)

$Elevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole('Administrators')
# -WhatIf: remember it, then turn the preference OFF. Otherwise it propagates
# into every cmdlet the CHECKS call (e.g. Get-PnpDeviceProperty returns nothing
# under WhatIf) and produces false "would fix" results. Dry-run is enforced by
# the engine instead: with $DryRun set, no Fix block is ever executed.
$DryRun   = [bool]$WhatIfPreference
$WhatIfPreference = $false
$script:WifiTouched = $false

# =============================================================================
#  Helpers
# =============================================================================
# Every CHECK returns one of these two shapes:
#   Res $ok $actual  -> pass/fail plus the value we actually found (shown on drift)
#   SkipRes $why     -> can't be checked here (not installed, needs admin, ...)
function Res($ok, $actual)  { [pscustomobject]@{ Ok = [bool]$ok; Actual = "$actual"; Skip = $null } }
function SkipRes($why)      { [pscustomobject]@{ Ok = $false;    Actual = '';         Skip = $why } }

# Read / write the AC (plugged-in) value of a power-plan setting on the active
# plan. Battery (DC) values are never touched by this script.
function AcValue($sub, $setting) {
    ((powercfg /query SCHEME_CURRENT $sub $setting | Select-String 'Current AC').Line -replace '.*: ', '').Trim()
}
function Set-AcValue($sub, $setting, $value) {
    powercfg /setacvalueindex SCHEME_CURRENT $sub $setting $value | Out-Null
    powercfg /setactive SCHEME_CURRENT | Out-Null
}
# Registry helpers. RegVal returns $null when the value doesn't exist.
function Ensure-Key($p) { if (-not (Test-Path $p)) { New-Item -Path $p -Force | Out-Null } }
function RegVal($key, $name) { (Get-ItemProperty $key -Name $name -EA 0).$name }
function Set-Dword($key, $name, $value) { Ensure-Key $key; Set-ItemProperty $key -Name $name -Value $value -Type DWord }

# Scheduled-task helpers. $list holds full task paths ('\Folder\Name').
# Tasks missing on this Windows build are silently ignored, not drift.
function TaskObjs($list) {
    foreach ($t in $list) {
        $o = Get-ScheduledTask -TaskName (Split-Path $t -Leaf) -TaskPath ((Split-Path $t -Parent).TrimEnd('\') + '\') -EA 0
        if ($o) { $o }
    }
}
function TestTasks($list) {
    $present = @(TaskObjs $list)
    if (-not $present.Count) { return SkipRes 'none present on this build' }
    $on = @($present | Where-Object State -ne 'Disabled')
    Res ($on.Count -eq 0) $(if ($on.Count) { 'still enabled: ' + (($on | ForEach-Object TaskName) -join ', ') } else { "$($present.Count)/$($present.Count) disabled" })
}
function FixTasks($list) { TaskObjs $list | Where-Object State -ne 'Disabled' | Disable-ScheduledTask | Out-Null }

# The Intel Wi-Fi driver's registry key (Control\Class\{4d36e972-...}\NNNN).
# The NNNN index differs per machine, so it's looked up from the adapter.
function WifiClassKey {
    $a = Get-NetAdapter -Name WiFi -EA Stop
    $sub = (Get-PnpDeviceProperty -InstanceId $a.PnpDeviceID -KeyName 'DEVPKEY_Device_Driver' -EA Stop).Data
    "HKLM:\SYSTEM\CurrentControlSet\Control\Class\$sub"
}
# Realtek Ethernet power-saving properties. The same property can match both
# a display name and a registry keyword, so results are de-duplicated.
function RealtekEeeProps {
    $kws = 'Energy-Efficient Ethernet', 'Green Ethernet', 'Power Saving Mode', '*EEE', 'EnableGreenEthernet', 'PowerSavingMode'
    $eth = Get-NetAdapter -Physical -EA 0 | Where-Object InterfaceDescription -match 'Realtek.*(Ethernet|GBE|Gaming|Controller)'
    foreach ($nic in $eth) {
        Get-NetAdapterAdvancedProperty -Name $nic.Name -EA 0 |
            Where-Object { $kws -contains $_.DisplayName -or $kws -contains $_.RegistryKeyword } |
            Sort-Object RegistryKeyword -Unique |
            ForEach-Object {
                $off = $_.ValidDisplayValues | Where-Object { $_ -match 'Disabl|Off' } | Select-Object -First 1
                if (-not $off) { $off = 'Disabled' }
                [pscustomobject]@{ Nic = $nic.Name; Prop = $_; Off = $off }
            }
    }
}

# =============================================================================
#  Item registry
#  Id      : TUNING-LOG item number (several entries may share one id)
#  Admin   : the CHECK needs elevation (Apply itself always runs elevated)
#  Fix     : $null = manual-only (Hint says what to do)
# =============================================================================
$Items = New-Object System.Collections.ArrayList
# Add-Item <Id> <Group> '<Name>' { check } { fix } [hint] [-Admin] [-Hkcu] [-Wifi]
#   -Admin : the check needs elevation (unelevated Verify shows it as SKIPPED)
#   -Hkcu  : per-user setting -- applies to whoever runs the script
#   -Wifi  : the fix needs a Wi-Fi adapter restart (done once, at the end)
function Add-Item {
    param($Id, $Group, $Name, [scriptblock]$Test, [scriptblock]$Fix, $Hint, [switch]$Admin, [switch]$Hkcu, [switch]$Wifi)
    [void]$Items.Add([pscustomobject]@{ Id = "$Id"; Group = $Group; Name = $Name; Test = $Test; Fix = $Fix
                                        Hint = $Hint; Admin = [bool]$Admin; Hkcu = [bool]$Hkcu; Wifi = [bool]$Wifi })
}

# ---------------------------------------------------------------- core [1]-[9]
# [1] Pagefile fixed at 4 GB min / 16 GB max.
#     WHY: system-managed pagefile was only 1 GB -> 16.7 GB commit limit, while
#          WSL2 is allowed 12 GB + 4 GB swap. Starting WSL could exhaust commit
#          (allocation failures, not just slowdown). Needs a reboot to resize.
#     REVERT: System Properties -> Advanced -> Performance -> Virtual memory ->
#             "Automatically manage paging file size".
Add-Item 1 core 'Pagefile fixed 4096/16384 MB' {
    $pf = Get-CimInstance Win32_PageFileSetting -EA 0
    $v = if ($pf) { "$($pf.InitialSize)/$($pf.MaximumSize)" } else { 'system-managed' }
    Res ($v -eq '4096/16384') $v
} {
    $cs = Get-CimInstance Win32_ComputerSystem
    if ($cs.AutomaticManagedPagefile) { Set-CimInstance -InputObject $cs -Property @{ AutomaticManagedPagefile = $false } }
    Start-Sleep -Milliseconds 500
    $pf = Get-CimInstance Win32_PageFileSetting -EA 0
    if ($pf) { Set-CimInstance -InputObject $pf -Property @{ InitialSize = 4096; MaximumSize = 16384 } }
    else { New-CimInstance -ClassName Win32_PageFileSetting -Property @{ Name = 'C:\pagefile.sys'; InitialSize = 4096; MaximumSize = 16384 } | Out-Null }
    Write-Host '           NOTE: REBOOT required to resize the file' -ForegroundColor Yellow
}

# [2] Max processor state on AC = 100%.
#     WHY: it was 99% -- the legacy trick to suppress Turbo. On Alder Lake it
#          doesn't fully kill turbo, but it's a pointless frequency ceiling.
#          PL1/PL2 are the real power governor. Battery (DC) value untouched.
#     REVERT: same powercfg call with 99.
Add-Item 2 core 'Max processor state AC = 100%' {
    $v = AcValue SUB_PROCESSOR PROCTHROTTLEMAX; Res ($v -eq '0x00000064') $v
} { Set-AcValue SUB_PROCESSOR PROCTHROTTLEMAX 100 }

# [3] Fast Startup (hiberboot) off.
#     WHY: the flag was on while hibernation itself was off (this laptop only
#          has Modern Standby) -- inert but misleading; keeps shutdown a real
#          cold boot. REVERT: HiberbootEnabled=1; powercfg /h on
Add-Item 3 core 'Fast Startup (hiberboot) off' {
    $v = RegVal 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power' HiberbootEnabled; Res ($v -eq 0) $v
} {
    Set-Dword 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power' HiberbootEnabled 0
    powercfg /h off 2>&1 | Out-Null
}

# [4] IP Helper (iphlpsvc) Automatic + Running.
#     WHY: ROOT CAUSE of Tailscale failing at boot -- a debloat had disabled it.
#          Highest operational impact of all items; a debloat re-run breaks it.
Add-Item 4 core 'IP Helper Automatic + Running (Tailscale dependency)' {
    $s = Get-Service iphlpsvc -EA 0; Res ($s.StartType -eq 'Automatic' -and $s.Status -eq 'Running') "$($s.StartType)/$($s.Status)"
} {
    Set-Service iphlpsvc -StartupType Automatic; Start-Service iphlpsvc -EA 0
    if (Get-Service Tailscale -EA 0) { Restart-Service Tailscale -Force -EA 0 }
}
# [4] Tailscale service running (only checked if Tailscale is installed).
Add-Item 4 core 'Tailscale Running' {
    $s = Get-Service Tailscale -EA 0
    if (-not $s) { return SkipRes 'not installed' }
    Res ($s.Status -eq 'Running') $s.Status
} { Restart-Service Tailscale -Force }

# [5] Shadow Copy storage 10 GB + System Restore on.
#     WHY: Volsnap Event 36 -- shadow copies aborted because storage couldn't
#          grow, so there were NO working restore points before driver work.
Add-Item 5 core 'Shadow Copy storage 10 GB + System Restore' {
    $ss = (vssadmin list shadowstorage 2>&1 | Select-String 'Maximum Shadow Copy Storage space').Line
    Res ($ss -match '10\.0 GB') (($ss -replace '.*space:\s*', '').Trim())
} {
    Enable-ComputerRestore -Drive 'C:\' -EA 0
    vssadmin resize shadowstorage /for=C: /on=C: /maxsize=10GB | Out-Null
} -Admin

# [6] Intel Computing Improvement Program removed.
#     WHY: esrv.exe + esrv_svc.exe held ~345 MB for a telemetry survey; no
#          functional role. An Intel driver bundle can bring it back.
#          (MSI Center is deliberately KEPT -- it does fan control.)
Add-Item 6 core 'Intel Computing Improvement Program absent' {
    $roots = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
    $app = @(Get-ItemProperty $roots -EA 0 | Where-Object DisplayName -like '*Computing Improvement*')
    Res ($app.Count -eq 0) $(if ($app.Count) { 'installed' } else { 'absent' })
} {
    $roots = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
    foreach ($a in (Get-ItemProperty $roots -EA 0 | Where-Object DisplayName -like '*Computing Improvement*')) {
        if ($a.PSChildName -match '^\{[0-9A-Fa-f-]+\}$') { Start-Process msiexec.exe -ArgumentList "/x $($a.PSChildName) /qn /norestart" -Wait }
    }
}

# [7] Defender real-time scan exclusions for build caches.
#     WHY: real-time scanning of Gradle/Android build caches slows builds.
#          Scope kept to caches only -- source folders are still scanned.
#     REVERT: Remove-MpPreference -ExclusionPath <path>
Add-Item 7 core 'Defender exclusions: .gradle, .android' {
    $ex = @((Get-MpPreference -EA Stop).ExclusionPath)
    $miss = @("$env:USERPROFILE\.gradle", "$env:USERPROFILE\.android" | Where-Object { $ex -notcontains $_ })
    Res ($miss.Count -eq 0) $(if ($miss.Count) { 'missing: ' + ($miss -join ', ') } else { 'both present' })
} {
    $ex = @((Get-MpPreference -EA Stop).ExclusionPath)
    foreach ($p in "$env:USERPROFILE\.gradle", "$env:USERPROFILE\.android") { if ($ex -notcontains $p) { Add-MpPreference -ExclusionPath $p } }
} -Admin

# [8] Cross-Device Resume off (HKCU = per user).
#     WHY: phone/OneDrive "resume" popups + background CrossDeviceResume process.
#          Least durable item -- feature updates tend to switch it back on.
Add-Item 8 core 'Cross-Device Resume + OneDrive Resume off (HKCU)' {
    $c = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\CrossDeviceResume\Configuration' -EA 0
    Res ($c.IsResumeAllowed -eq 0 -and $c.IsOneDriveResumeAllowed -eq 0) "$($c.IsResumeAllowed)/$($c.IsOneDriveResumeAllowed)"
} {
    $k = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\CrossDeviceResume\Configuration'
    Set-Dword $k IsResumeAllowed 0; Set-Dword $k IsOneDriveResumeAllowed 0
} -Hkcu

# [9] Wi-Fi MIMO power save = No SMPS.
#     WHY: SMPS put the radio to sleep between bursts -> latency spikes on the
#          first packets after idle. Reset by Wi-Fi driver reinstall / network reset.
Add-Item 9 core 'Wi-Fi MIMO Power Save = No SMPS' {
    $m = Get-NetAdapterAdvancedProperty -Name WiFi -AllProperties -EA 0 | Where-Object RegistryKeyword -eq 'MIMOPowerSaveMode'
    if (-not $m) { return SkipRes 'WiFi adapter / property not found' }
    Res ($m.RegistryValue[0] -eq 3) $m.RegistryValue[0]
} { Set-NetAdapterAdvancedProperty -Name WiFi -DisplayName 'MIMO Power Save Mode' -DisplayValue 'No SMPS' } -Wifi

# ------------------------------------------------------- baseline (Section 4)
# [4.1] NTFS baseline: no 8.3 short names, larger NTFS paged-pool cache
#       (MemoryUsage=2), TRIM on (SSD health). Pre-existing tuning, kept enforced.
Add-Item 4.1 baseline 'NTFS: 8dot3 off, MemoryUsage 2, TRIM on' {
    $a = ((fsutil behavior query disable8dot3) -join ' ') -match 'is: 1'
    $b = ((fsutil behavior query memoryusage) -join ' ') -match 'MemoryUsage = 2'
    $c = ((fsutil behavior query disabledeletenotify) -join ' ') -match 'NTFS DisableDeleteNotify = 0'
    Res ($a -and $b -and $c) "8dot3=$a mem=$b trim=$c"
} {
    fsutil behavior set disable8dot3 1 | Out-Null
    fsutil behavior set memoryusage 2 | Out-Null
    fsutil behavior set disabledeletenotify 0 | Out-Null
}

# [4.2] Visual effects = "best performance" (HKCU). Less GPU/compositor work.
Add-Item 4.2 baseline 'Visual effects = best performance (HKCU)' {
    $v = RegVal 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects' VisualFXSetting; Res ($v -eq 2) $v
} { Set-Dword 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects' VisualFXSetting 2 } -Hkcu

# [4.3] Wireless adapter power plan on AC = Maximum Performance (no Wi-Fi
#       power-save on mains power; battery value untouched).
Add-Item 4.3 baseline 'Wireless adapter power AC = Max Performance' {
    $v = AcValue 19cbb8fa-5279-450e-9fac-8a3d5fedd0c1 12bbebe6-58d6-4636-95bb-3217ef867c1a; Res ($v -eq '0x00000000') $v
} { Set-AcValue 19cbb8fa-5279-450e-9fac-8a3d5fedd0c1 12bbebe6-58d6-4636-95bb-3217ef867c1a 0 }

# SysMain is deliberately NOT here -- it hosts Memory Compression, see [15].
# NahimicService is deliberately NOT here -- kept ON for audio, see [27].
# [4.5] Services disabled by the original debloat, kept enforced.
#       Each one is unused on this machine (Xbox, printing, fax-era sharing,
#       telemetry, phone-link, vendor updaters...). The full per-service reason
#       and known side effects are in TUNING-LOG Section 4.5 / 4.6. Notable:
#         DSAService/DSAUpdateService off = Intel can't silently push drivers.
#         lfsvc off = 'netsh wlan show interfaces' can't read band/signal.
#         ssh-agent off = breaks SSH keys loaded via the agent (re-enable if used).
#       Services that aren't installed are skipped, not reported as drift.
$BaselineServices = @(
    'CDPSvc', 'DiagTrack', 'dmwappushservice', 'dptftcs', 'DSAService',
    'DSAUpdateService', 'GameInputSvc', 'IntelGFXFWupdateTool', 'InventorySvc',
    'lfsvc', 'lmhosts', 'MSI Sendevsvc', 'NetTcpPortSharing',
    'O+Connect Service', 'OplusRemoteService', 'PcaSvc', 'QWAVE', 'RemoteAccess',
    'RemoteRegistry', 'shpamsvc', 'Spooler', 'SSDPSRV', 'ssh-agent', 'StiSvc',
    'TrkWks', 'tzautoupdate', 'WbioSrvc', 'wercplsupport', 'WerSvc',
    'whesvc', 'wisvc', 'WMIRegistrationService', 'WSAIFabricSvc', 'WSearch',
    'XblAuthManager', 'XblGameSave', 'XboxGipSvc', 'XboxNetApiSvc')
Add-Item 4.5 baseline 'Baseline services disabled (absent ones skipped)' {
    $present = @($BaselineServices | ForEach-Object { Get-Service $_ -EA 0 } | Where-Object { $_ })
    $on = @($present | Where-Object StartType -ne 'Disabled')
    Res ($on.Count -eq 0) $(if ($on.Count) { 'not disabled: ' + (($on | ForEach-Object { "$($_.Name)=$($_.StartType)" }) -join ', ') } else { "$($present.Count)/$($present.Count) disabled" })
} {
    foreach ($s in $BaselineServices) {
        $o = Get-Service $s -EA 0
        if ($o -and $o.StartType -ne 'Disabled') { Set-Service $s -StartupType Disabled -EA Continue }
    }
    Write-Host '           NOTE: ssh-agent is in this list. If you use SSH keys via the agent:' -ForegroundColor Yellow
    Write-Host '                 Set-Service ssh-agent -StartupType Manual' -ForegroundColor Cyan
}

# ---------------------------------------------------- advanced [10]-[18] (S7)
# [10] Wi-Fi prefers 5 GHz ("prefer", not "force" -- 2.4 GHz still a fallback).
#      WHY: was sitting on 2.4 GHz at ~300-400 Mbps; 5 GHz gives ~3-4x.
#      NOTE: only works if both bands share one SSID name.
Add-Item 10 advanced 'Wi-Fi Preferred Band = Prefer 5 GHz' {
    $p = Get-NetAdapterAdvancedProperty -Name WiFi -RegistryKeyword RoamingPreferredBandType -EA 0
    if (-not $p) { return SkipRes 'Preferred Band property not found' }
    Res ($p.DisplayValue -eq '3. Prefer 5GHz band') $p.DisplayValue
} { Set-NetAdapterAdvancedProperty -Name WiFi -DisplayName 'Preferred Band' -DisplayValue '3. Prefer 5GHz band' -EA Stop } -Wifi

# [11] NTFS last-access timestamps off.
#      WHY: every file READ caused a metadata WRITE -- needless wear on the
#           DRAM-less NVMe across big git/gradle/SDK trees.
#      REVERT: fsutil behavior set disablelastaccess 2
Add-Item 11 advanced 'NTFS Last-Access updates off' {
    $v = (fsutil behavior query disablelastaccess) -join ' '; Res ($v -match 'DisableLastAccess = 1') ($v -replace '\s+\(.*', '')
} { fsutil behavior set disablelastaccess 1 | Out-Null }

# [12] PCIe ASPM off -- AC ONLY (battery value untouched).
#      WHY: link power-saving added wake latency to the NVMe and other PCIe devices.
#      REVERT: ASPM 1 (moderate) via powercfg.
Add-Item 12 advanced 'PCIe ASPM AC = Off' {
    $v = AcValue SUB_PCIEXPRESS ASPM; Res ($v -eq '0x00000000') $v
} { Set-AcValue SUB_PCIEXPRESS ASPM 0 }

# [13] Disk idle timeout never -- AC ONLY.
#      WHY: a 30 s spin-down timer is a hard-disk relic; on NVMe it only adds
#           a wake stall. REVERT: DISKIDLE 1200 via powercfg.
Add-Item 13 advanced 'Disk idle timeout AC = never' {
    $v = AcValue SUB_DISK DISKIDLE; Res ($v -eq '0x00000000') $v
} { Set-AcValue SUB_DISK DISKIDLE 0 }

# [14] Hidden Intel Wi-Fi power savers off (registry-only, no UI).
#      WHY: SkipOverDtim / Lprx let the radio sleep through beacons -> slow
#           first packet after idle. Needs an adapter restart (done at the end).
Add-Item 14 advanced 'Hidden Wi-Fi savers off (SkipOverDtim, Lprx)' {
    try { $k = WifiClassKey } catch { return SkipRes 'WiFi adapter not found' }
    $a = RegVal $k SkipOverDtimEnable; $b = RegVal $k LprxEnable
    Res ($a -eq 0 -and $b -eq 0) "SkipOverDtim=$a Lprx=$b"
} {
    $k = WifiClassKey
    Set-ItemProperty $k -Name SkipOverDtimEnable -Value 0 -Type DWord
    Set-ItemProperty $k -Name LprxEnable -Value 0 -Type DWord
} -Wifi

# [15] Compression lives in SysMain: Enable-MMAgent turns SysMain back on, and
# disabling SysMain silently kills compression. So SysMain stays ON and only
# its app preloading is cut. (TUNING-LOG Section 8 "CONFLICT FOUND")
# [15] Memory Compression ON -- and the SysMain conflict it caused.
#      WHY: on 16 GB with WSL allowed 12 GB, compressing idle pages in RAM is far
#           cheaper than paging them to the DRAM-less SSD.
#      CONFLICT (found 2026-10-10): compression is HOSTED BY SysMain.
#           Enable-MMAgent turns SysMain back on, and disabling SysMain silently
#           turns compression off -- the old baseline (SysMain disabled) and this
#           item undid each other on every run.
#      DECISION: keep SysMain ON for compression, but switch off app PRELAUNCH
#           (loading apps into RAM before you open them).
#      PREFETCH stays on: on build 26300 'Disable-MMAgent
#           -ApplicationLaunchPrefetching' fails with Windows error 50 ("The
#           request is not supported"). Harmless -- prefetch only keeps small
#           launch traces in C:\Windows\Prefetch; it doesn't hold RAM.
#      REVERT (SysMain fully off): accept compression off; remove these 3 items.
Add-Item 15 advanced 'SysMain Automatic + Running (hosts Memory Compression)' {
    $s = Get-Service SysMain -EA 0; Res ($s.StartType -eq 'Automatic' -and $s.Status -eq 'Running') "$($s.StartType)/$($s.Status)"
} { Set-Service SysMain -StartupType Automatic; Start-Service SysMain }
Add-Item 15 advanced 'Memory Compression on' {
    $v = (Get-MMAgent -EA Stop).MemoryCompression; Res $v $v
} { Enable-MMAgent -MemoryCompression } -Admin
Add-Item 15 advanced 'App prelaunch off (SysMain lean)' {
    $v = (Get-MMAgent -EA Stop).ApplicationPreLaunch; Res (-not $v) "prelaunch=$v"
} { Disable-MMAgent -ApplicationPreLaunch } -Admin

# [16] Telemetry scheduled tasks off.
#      WHY: DiagTrack (telemetry service) is disabled in 4.5, but these tasks
#           still woke up to collect data. OneDC_Updater = MSI Dragon Center
#           updater (does NOT affect fan control). Missing tasks are skipped.
$TelemetryTasks = @(
    '\Microsoft\Windows\Customer Experience Improvement Program\Consolidator',
    '\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip',
    '\Microsoft\Windows\Customer Experience Improvement Program\KernelCeipTask',
    '\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser',
    '\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser Exp',
    '\Microsoft\Windows\Application Experience\ProgramDataUpdater',
    '\Microsoft\Windows\Feedback\Siuf\DmClient',
    '\Microsoft\Windows\Feedback\Siuf\DmClientOnScenarioDownload',
    '\OneDC_Updater')
Add-Item 16 advanced 'Telemetry scheduled tasks off (CEIP/Appraiser/DmClient)' { TestTasks $TelemetryTasks } { FixTasks $TelemetryTasks }

# [17] Realtek Ethernet power saving off (EEE / Green Ethernet / Power Saving).
#      WHY: dormant on Wi-Fi, but caused link renegotiation drops when docked.
Add-Item 17 advanced 'Realtek Ethernet EEE / Green / Power Saving off' {
    $props = @(RealtekEeeProps)
    if (-not $props.Count) { return SkipRes 'no Realtek Ethernet adapter' }
    $on = @($props | Where-Object { $_.Prop.DisplayValue -ne $_.Off })
    Res ($on.Count -eq 0) $(if ($on.Count) { 'on: ' + (($on | ForEach-Object { $_.Prop.DisplayName }) -join ', ') } else { "$($props.Count)/$($props.Count) off" })
} {
    foreach ($p in RealtekEeeProps) {
        if ($p.Prop.DisplayValue -ne $p.Off) { Set-NetAdapterAdvancedProperty -Name $p.Nic -DisplayName $p.Prop.DisplayName -DisplayValue $p.Off -EA Continue }
    }
}

# [18] WSL2 memory hygiene (check only -- edit .wslconfig by hand).
#      WHY: autoMemoryReclaim hands idle WSL page cache back to Windows;
#           sparseVhd stops the ext4 virtual disk growing forever.
Add-Item 18 advanced 'WSL2 autoMemoryReclaim=gradual + sparseVhd=true' {
    $f = "$env:USERPROFILE\.wslconfig"
    if (-not (Test-Path $f)) { return Res $false '.wslconfig missing' }
    $c = Get-Content $f -Raw
    $a = $c -match '(?m)^\s*autoMemoryReclaim\s*=\s*gradual'; $b = $c -match '(?m)^\s*sparseVhd\s*=\s*true'
    Res ($a -and $b) "autoMemoryReclaim=$a sparseVhd=$b"
} $null 'add both lines under [wsl2] in %USERPROFILE%\.wslconfig, then: wsl --shutdown'

# ------------------------------------------- idle-RAM debloat [20]-[27] (S8)
# ---- Section 8: idle-RAM / background debloat (2026-10-10) ----------------
# Found during an idle-RAM audit. Each is small; together ~100-300 MB plus
# fewer background wakeups.

# [20] Downloaded Maps Manager off -- offline-maps updater, Maps app unused.
Add-Item 20 debloat 'MapsBroker disabled' {
    $s = Get-Service MapsBroker -EA 0
    if (-not $s) { return SkipRes 'not installed' }
    Res ($s.StartType -eq 'Disabled') $s.StartType
} { Set-Service MapsBroker -StartupType Disabled; Stop-Service MapsBroker -Force -EA 0 }

# [21] Internet Connection Sharing -> Manual (was Automatic at boot).
# Manual, not Disabled: RPC-trigger-started by WSL / Hyper-V NAT / hotspot.
Add-Item 21 debloat 'SharedAccess (ICS) Manual  (Running is fine)' {
    $s = Get-Service SharedAccess -EA 0
    if (-not $s) { return SkipRes 'not installed' }
    Res ($s.StartType -eq 'Manual') $s.StartType
} { Set-Service SharedAccess -StartupType Manual }

# [22] Leftover scheduled tasks whose feature is already disabled in 4.5
#      (Maps, Xbox, Error Reporting, PCA) or unused (Family Safety).
#      With the service gone they just wake up and do nothing.
#      (Nahimic tasks were here briefly -- removed, see [27].)
$LeftoverTasks = @(
    '\Microsoft\Windows\Maps\MapsToastTask',
    '\Microsoft\Windows\Maps\MapsUpdateTask',
    '\Microsoft\XblGameSave\XblGameSaveTask',
    '\Microsoft\Windows\Windows Error Reporting\QueueReporting',
    '\Microsoft\Windows\Application Experience\PcaPatchDbTask',
    '\Microsoft\Windows\Shell\FamilySafetyMonitor',
    '\Microsoft\Windows\Shell\FamilySafetyRefreshTask')
Add-Item 22 debloat 'Leftover scheduled tasks off (Maps/Xbox/WER/PCA/FamilySafety)' { TestTasks $LeftoverTasks } { FixTasks $LeftoverTasks }

# [27] Nahimic audio KEPT ON -- an explicit exception to the debloat.
#      WHY: Nahimic (MSI/A-Volute audio effects) is what makes this laptop's
#           speakers sound good. The original debloat disabled NahimicService
#           (baseline 4.5) and [22] briefly disabled its tasks; without them
#           the Nahimic app has no effect. Enforced ON so no future 'apply'
#           or debloat list can silently kill it again.
#      NOTE: NahimicTask32/64 launch NahimicSvc32/64.exe -- the per-session
#            half of Nahimic; the service is the system half. Both are needed.
$NahimicTasks = @('\NahimicTask32', '\NahimicTask64')
Add-Item 27 debloat 'Nahimic audio ON (service + tasks) -- exception' {
    $s = Get-Service NahimicService -EA 0
    if (-not $s) { return SkipRes 'Nahimic not installed' }
    $off = @(TaskObjs $NahimicTasks | Where-Object State -eq 'Disabled')
    Res ($s.StartType -eq 'Automatic' -and $s.Status -eq 'Running' -and $off.Count -eq 0) `
        "service=$($s.StartType)/$($s.Status) disabledTasks=$((($off | ForEach-Object TaskName) -join ','))"
} {
    Set-Service NahimicService -StartupType Automatic
    Start-Service NahimicService
    TaskObjs $NahimicTasks | Where-Object State -eq 'Disabled' | Enable-ScheduledTask | Out-Null
    Write-Host '           NOTE: sign out and back in (or reboot) so the Nahimic session task starts' -ForegroundColor Yellow
}

# [23] Edge Startup Boost + background mode off (machine policy).
#      WHY: Edge preloaded at every login and kept processes alive after close.
#      SIDE EFFECT: Edge shows "managed by your organization" -- harmless.
Add-Item 23 debloat 'Edge Startup Boost + background mode off (policy)' {
    $k = 'HKLM:\SOFTWARE\Policies\Microsoft\Edge'
    $a = RegVal $k StartupBoostEnabled; $b = RegVal $k BackgroundModeEnabled
    Res ($a -eq 0 -and $b -eq 0) "boost=$a background=$b"
} {
    Set-Dword 'HKLM:\SOFTWARE\Policies\Microsoft\Edge' StartupBoostEnabled 0
    Set-Dword 'HKLM:\SOFTWARE\Policies\Microsoft\Edge' BackgroundModeEnabled 0
}

# [24] Delivery Optimization peer-to-peer off (HTTP-only downloads).
#      WHY: stops seeding Windows/Store updates to other PCs (upload + disk).
#           The DoSvc service itself stays -- it is protected and needed.
Add-Item 24 debloat 'Delivery Optimization P2P off (DODownloadMode=0)' {
    $v = RegVal 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization' DODownloadMode; Res ($v -eq 0 -and $null -ne $v) $v
} { Set-Dword 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization' DODownloadMode 0 }

# [25] Game DVR background capture off -- capture hooks in games, never used.
Add-Item 25 debloat 'Game DVR off (policy)' {
    $v = RegVal 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\GameDVR' AllowGameDVR; Res ($v -eq 0 -and $null -ne $v) $v
} { Set-Dword 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\GameDVR' AllowGameDVR 0 }

# [26] Taskbar search box hidden (HKCU).
#      WHY: SearchHost + its WebView2 children held ~430 MB even with the indexer
#           off. Hidden, it loads on demand; typing in Start still searches.
Add-Item 26 debloat 'Taskbar search box hidden (HKCU)' {
    $v = RegVal 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Search' SearchboxTaskbarMode; Res ($v -eq 0 -and $null -ne $v) $v
} { Set-Dword 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Search' SearchboxTaskbarMode 0 } -Hkcu

# ---------------------------------------------------------- gaming [28]-[29]
# Added 2026-10-10 for Fortnite. Both are per-user (HKCU) settings.

# [28] Fortnite runs on the Arc A370M (high-performance GPU), not the Iris Xe.
#      WHY: with no per-app preference Windows may run the game on the iGPU --
#           far fewer FPS. Same as Settings > Display > Graphics > Fortnite >
#           "High performance". GpuPreference: 0=let Windows, 1=power saving,
#           2=high performance. Skipped if Fortnite isn't installed.
#      NOTE: Epic sometimes moves the game folder on big updates -- if this
#            shows SKIPPED/DRIFTED after one, just re-apply.
$FortniteExe = 'C:\Program Files\Epic Games\Fortnite\FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe'
$GpuPrefKey  = 'HKCU:\Software\Microsoft\DirectX\UserGpuPreferences'
Add-Item 28 gaming 'Fortnite on high-performance GPU (Arc) (HKCU)' {
    if (-not (Test-Path $FortniteExe)) { return SkipRes 'Fortnite not found at the default path' }
    $v = RegVal $GpuPrefKey $FortniteExe
    Res ($v -match 'GpuPreference=2;') $(if ($v) { $v } else { 'not set (Windows decides)' })
} {
    Ensure-Key $GpuPrefKey
    Set-ItemProperty $GpuPrefKey -Name $FortniteExe -Value 'GpuPreference=2;' -Type String
} -Hkcu

# [29] Game Mode on (HKCU). WHY: Windows prioritises the game's threads and
#      holds back Windows Update installs / notifications while it runs.
Add-Item 29 gaming 'Game Mode on (HKCU)' {
    $v = RegVal 'HKCU:\Software\Microsoft\GameBar' AutoGameModeEnabled; Res ($v -eq 1) $v
} { Set-Dword 'HKCU:\Software\Microsoft\GameBar' AutoGameModeEnabled 1 } -Hkcu

# =============================================================================
#  [19] GPU driver pin vs Windows Update  (separate action -- not part of Apply)
# =============================================================================
$GPU_IDS = @('PCI\VEN_8086&DEV_46A6',   # Intel Iris Xe (Alder Lake-P iGPU)
             'PCI\VEN_8086&DEV_5693')   # Intel Arc A370M (dGPU)
$WU   = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate'
$DS   = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\DriverSearching'
$RES  = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\DeviceInstall\Restrictions'
$DENY = "$RES\DenyDeviceIDs"

# Prints the [19] pin state; returns $true if the global exclude is in place.
function Show-GpuPin {
    $ex = RegVal $WU ExcludeWUDriversInQualityUpdate
    $so = RegVal $DS SearchOrderConfig
    $ok = ($ex -eq 1 -and $so -eq 0)
    $tag = if ($ok) { 'OK      ' } else { 'DRIFTED ' }
    Write-Host ("{0} [19] GPU pin: global exclude (Exclude=1, SearchOrder=0)" -f $tag) -ForegroundColor $(if ($ok) { 'Green' } else { 'Red' })
    if (-not $ok) { Write-Host "           actual   : Exclude=$ex SearchOrder=$so   fix: .\tune.ps1 -Gpu Global" -ForegroundColor Yellow }
    if (Test-Path $DENY) {
        $ids = (Get-Item $DENY).Property | ForEach-Object { (Get-ItemProperty $DENY -Name $_).$_ }
        Write-Host ("           per-device deny : {0}" -f ($ids -join ', ')) -ForegroundColor DarkGray
    } else { Write-Host '           per-device deny : not set (global only)' -ForegroundColor DarkGray }
    $ok
}

# Global    = WU stops delivering ANY driver (manual installs still work).
# PerDevice = Global + install-level deny for both Intel GPU hardware IDs --
#             needed on Home, where WU stepped around the global keys.
# Lift      = remove only the per-device deny, to install a driver manually.
# Revert    = undo everything [19] set.
function Invoke-GpuPin($what) {
    if (-not (Assert-Admin)) { return }
    Write-Host ""
    Write-Host "=== [19] GPU DRIVER PIN: $what ===" -ForegroundColor White
    if ($DryRun) { Write-Host '  WhatIf: no changes made.' -ForegroundColor Yellow; return }
    switch ($what) {
        'Revert' {
            Remove-ItemProperty $WU -Name ExcludeWUDriversInQualityUpdate -EA 0
            Set-Dword $DS SearchOrderConfig 1
            if (Test-Path $RES) { Remove-Item $RES -Recurse -Force }
            Write-Host '  reverted: global exclude removed, SearchOrderConfig=1, per-device deny removed' -ForegroundColor Green
        }
        'Lift' {
            if (Test-Path $RES) { Remove-Item $RES -Recurse -Force; Write-Host '  per-device deny LIFTED -- install your driver, reboot, then: .\tune.ps1 -Gpu PerDevice' -ForegroundColor Green }
            else { Write-Host '  no per-device deny present (nothing to lift)' -ForegroundColor DarkGray }
        }
        default {
            # Global exclude: WU stops delivering ANY driver; manual installs still work.
            Set-Dword $WU ExcludeWUDriversInQualityUpdate 1
            Set-Dword $DS SearchOrderConfig 0
            Write-Host '  global exclude set (ExcludeWUDriversInQualityUpdate=1, SearchOrderConfig=0)' -ForegroundColor Green
            if ($what -eq 'PerDevice') {
                # Retroactive=0: current driver untouched (no Code 48); NEW installs blocked (WU *and* manual).
                Set-Dword $RES DenyDeviceIDs 1
                Set-Dword $RES DenyDeviceIDsRetroactive 0
                Ensure-Key $DENY
                Get-Item $DENY | Select-Object -ExpandProperty Property | ForEach-Object { Remove-ItemProperty $DENY -Name $_ -EA 0 }
                $i = 1; foreach ($id in $GPU_IDS) { Set-ItemProperty $DENY -Name "$i" -Value $id -Type String; $i++ }
                Write-Host "  per-device deny set: $($GPU_IDS -join ', ')" -ForegroundColor Green
                Write-Host '  NOTE: this also blocks YOUR manual installs. To update: -Gpu Lift -> install + reboot -> -Gpu PerDevice' -ForegroundColor Yellow
            }
        }
    }
    Write-Host '  ORDER: install the wanted Intel driver + reboot FIRST, then pin.' -ForegroundColor DarkGray
    [void](Show-GpuPin)
}

# =============================================================================
#  Engine
# =============================================================================
# Filters the registry by group names and/or item IDs ('15' matches every
# entry with Id 15). No filter = everything.
function Select-Items($groups, $ids) {
    $sel = $Items
    if ($groups) { $sel = $sel | Where-Object { $groups -contains $_.Group } }
    if ($ids)    { $want = @($ids -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
                   $sel = $sel | Where-Object { $want -contains $_.Id } }
    @($sel)
}

# Runs one check safely: admin-only checks are skipped when unelevated, and a
# check that throws is reported as SKIPPED with the error -- never as a pass.
function Run-Test($it) {
    if ($it.Admin -and -not $Elevated) { return SkipRes 'needs elevation' }
    try { & $it.Test } catch { SkipRes "check failed: $($_.Exception.Message)" }
}

# Read-only report. -Full adds the GPU pin check and info lines (used when
# verifying everything rather than a filtered subset).
function Invoke-Verify($sel, [switch]$Full) {
    Write-Host ""
    Write-Host "=== VERIFY -- $(Get-Date -Format 'yyyy-MM-dd HH:mm') -- elevated: $Elevated ===" -ForegroundColor White
    $p = 0; $f = 0; $s = 0; $lastGroup = ''
    foreach ($it in $sel) {
        if ($it.Group -ne $lastGroup) { Write-Host ""; Write-Host "--- $($it.Group) ---" -ForegroundColor White; $lastGroup = $it.Group }
        $r = Run-Test $it
        if ($r.Skip)   { $s++; Write-Host ("SKIPPED  [{0}] {1}  ({2})" -f $it.Id, $it.Name, $r.Skip) -ForegroundColor DarkGray }
        elseif ($r.Ok) { $p++; Write-Host ("OK       [{0}] {1}" -f $it.Id, $it.Name) -ForegroundColor Green }
        else {
            $f++; Write-Host ("DRIFTED  [{0}] {1}" -f $it.Id, $it.Name) -ForegroundColor Red
            Write-Host ("           actual   : {0}" -f $r.Actual) -ForegroundColor Yellow
            $hint = if ($it.Fix) { ".\tune.ps1 apply -Item $($it.Id)" } else { $it.Hint }
            Write-Host ("           fix      : {0}" -f $hint) -ForegroundColor Cyan
        }
    }
    if ($Full) {
        Write-Host ""; Write-Host '--- gpu ---' -ForegroundColor White
        if (Show-GpuPin) { $p++ } else { $f++ }
        Write-Host ""; Write-Host '--- info (not checked) ---' -ForegroundColor White
        $arc = Get-PnpDevice -Class Display -EA 0 | Where-Object FriendlyName -match 'Arc'
        if ($arc) { Write-Host ("  Arc A370M dGPU   : {0}" -f $arc.Status) -ForegroundColor DarkGray }
        $wifi = Get-NetAdapter -Name WiFi -EA 0
        if ($wifi) { Write-Host ("  Wi-Fi link speed : {0}  (~400 Mbps = 2.4 GHz, ~1200 = 5 GHz)" -f $wifi.LinkSpeed) -ForegroundColor DarkGray }
    }
    Write-Host ""
    Write-Host ("RESULT:  {0} OK   {1} DRIFTED   {2} SKIPPED" -f $p, $f, $s) -ForegroundColor $(if ($f -eq 0) { 'Green' } else { 'Red' })
    if ($s -and -not $Elevated) { Write-Host 'Run elevated to check the skipped items.' -ForegroundColor DarkGray }
    Write-Host ""
}

# For each item: check -> already OK? skip -> else run fix -> RE-CHECK.
# An item only counts as APPLIED if its check passes after the fix, so a fix
# that silently didn't take shows up as FAILED instead of a false success.
function Invoke-Apply($sel) {
    if (-not (Assert-Admin)) { return }
    Write-Host ""
    Write-Host "=== APPLY -- $(Get-Date -Format 'yyyy-MM-dd HH:mm')$(if ($DryRun) { '  (WhatIf: no changes)' }) ===" -ForegroundColor White
    $done = 0; $ok = 0; $bad = 0; $lastGroup = ''
    foreach ($it in $sel) {
        if ($it.Group -ne $lastGroup) { Write-Host ""; Write-Host "--- $($it.Group) ---" -ForegroundColor White; $lastGroup = $it.Group }
        $r = Run-Test $it
        if ($r.Skip)    { Write-Host ("  skipped  [{0}] {1}  ({2})" -f $it.Id, $it.Name, $r.Skip) -ForegroundColor DarkGray; continue }
        if ($r.Ok)      { $ok++; Write-Host ("  ok       [{0}] {1}" -f $it.Id, $it.Name) -ForegroundColor DarkGray; continue }
        if (-not $it.Fix) { $bad++; Write-Host ("  MANUAL   [{0}] {1}  -> {2}" -f $it.Id, $it.Name, $it.Hint) -ForegroundColor Yellow; continue }
        if ($DryRun)    { Write-Host ("  WOULD FIX [{0}] {1}  (now: {2})" -f $it.Id, $it.Name, $r.Actual) -ForegroundColor Cyan; continue }
        try {
            & $it.Fix
            if ($it.Wifi) { $script:WifiTouched = $true }
            $r2 = Run-Test $it
            if ($r2.Ok) { $done++; Write-Host ("  APPLIED  [{0}] {1}" -f $it.Id, $it.Name) -ForegroundColor Green }
            else { $bad++; Write-Host ("  FAILED   [{0}] {1}  (still: {2})" -f $it.Id, $it.Name, $r2.Actual) -ForegroundColor Red }
        } catch { $bad++; Write-Host ("  FAILED   [{0}] {1}: {2}" -f $it.Id, $it.Name, $_.Exception.Message) -ForegroundColor Red }
    }
    if ($script:WifiTouched -and -not $SkipWiFiRestart) {
        Write-Host ""; Write-Host '  Wi-Fi settings changed -- restarting adapter (~5 s drop)...' -ForegroundColor Yellow
        try { Restart-NetAdapter -Name WiFi -EA Stop; Write-Host '  Wi-Fi adapter restarted' -ForegroundColor Green }
        catch { Write-Host "  adapter restart failed -- reboot to apply: $($_.Exception.Message)" -ForegroundColor Red }
    } elseif ($script:WifiTouched) {
        Write-Host '  NOTE: -SkipWiFiRestart set. Apply later with: Restart-NetAdapter -Name WiFi' -ForegroundColor Yellow
    }
    $script:WifiTouched = $false
    if (@($sel | Where-Object Hkcu).Count) { Write-Host '  (HKCU items apply to the account that ran this script.)' -ForegroundColor DarkGray }
    Write-Host ""
    Write-Host ("RESULT:  {0} applied   {1} already correct   {2} failed/manual" -f $done, $ok, $bad) -ForegroundColor $(if ($bad -eq 0) { 'Green' } else { 'Yellow' })
    Write-Host ""
}

# Apply / GPU actions need admin. From the menu it offers to relaunch itself
# elevated (UAC prompt); from the command line it just says so.
function Assert-Admin {
    if ($Elevated) { return $true }
    Write-Host ""
    Write-Host 'This needs Administrator.' -ForegroundColor Red
    if ($Mode -eq 'Menu') {
        $a = Read-Host 'Relaunch elevated now? (Y/n)'
        if ($a -eq '' -or $a -match '^[Yy]') {
            Start-Process powershell -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -NoExit -File `"$PSCommandPath`""
            exit
        }
    } else {
        Write-Host 'Right-click PowerShell -> Run as administrator, then re-run.' -ForegroundColor Yellow
    }
    $false
}

# =============================================================================
#  Menu
# =============================================================================
# Interactive loop -- used when the script is run with no arguments.
function Show-Menu {
    while ($true) {
        Write-Host ""
        Write-Host '=================== MSI GF63 TUNING ===================' -ForegroundColor White
        Write-Host ("  elevated: {0}" -f $Elevated) -ForegroundColor DarkGray
        Write-Host '  1  Verify everything (read-only)'
        Write-Host '  2  Apply EVERYTHING that drifted'
        Write-Host '  3  Apply core fixes        [1]-[9]'
        Write-Host '  4  Apply baseline          NTFS, visual FX, services'
        Write-Host '  5  Apply advanced          [10]-[18]'
        Write-Host '  6  Apply idle-RAM debloat  [20]-[27]'
        Write-Host '  G  Apply gaming            [28]-[29]  (Fortnite GPU, Game Mode)'
        Write-Host '  7  Apply specific items    (enter IDs, e.g. 15,23)'
        Write-Host '  8  GPU driver pin          [19]'
        Write-Host '  9  List all items'
        Write-Host '  Q  Quit'
        $c = "$(Read-Host 'Choose')".Trim().ToUpper()
        switch ($c) {
            '1' { Invoke-Verify $Items -Full }
            '2' { Invoke-Apply $Items }
            '3' { Invoke-Apply (Select-Items 'core') }
            '4' { Invoke-Apply (Select-Items 'baseline') }
            '5' { Invoke-Apply (Select-Items 'advanced') }
            '6' { Invoke-Apply (Select-Items 'debloat') }
            'G' { Invoke-Apply (Select-Items 'gaming') }
            '7' {
                $sel = Select-Items $null "$(Read-Host 'Item IDs (comma-separated)')"
                if ($sel.Count) { Invoke-Apply $sel } else { Write-Host 'No matching items.' -ForegroundColor Yellow }
            }
            '8' {
                Write-Host ""
                [void](Show-GpuPin)
                Write-Host '  a  Global exclude (recommended)'
                Write-Host '  b  Global + per-device hard-block (both Intel GPUs)'
                Write-Host '  c  Lift per-device block (to install a driver manually)'
                Write-Host '  d  Revert everything'
                switch ("$(Read-Host 'Choose (Enter = back)')".Trim().ToLower()) {
                    'a' { Invoke-GpuPin 'Global' }
                    'b' { Invoke-GpuPin 'PerDevice' }
                    'c' { Invoke-GpuPin 'Lift' }
                    'd' { Invoke-GpuPin 'Revert' }
                }
            }
            '9' {
                Write-Host ""
                foreach ($it in $Items) { Write-Host ("  [{0,-4}] {1,-9} {2}" -f $it.Id, $it.Group, $it.Name) }
            }
            'Q' { return }
            default { Write-Host 'Unknown choice.' -ForegroundColor Yellow }
        }
    }
}

# =============================================================================
#  Entry
# =============================================================================
if ($Gpu) { Invoke-GpuPin $Gpu; return }
$sel = Select-Items $Group $Item
if ($Mode -ne 'Menu' -and -not $sel.Count) { Write-Host 'No matching items. Use the menu (option 9) to list IDs.' -ForegroundColor Yellow; return }
switch ($Mode) {
    'Verify' { Invoke-Verify $sel -Full:(-not $Group -and -not $Item) }
    'Apply'  { Invoke-Apply $sel }
    default  { Show-Menu }
}
