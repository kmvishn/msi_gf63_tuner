# =============================================================================
#  advanced-tuning.ps1  --  Tier 1 "next level" software tuning
#  IDEMPOTENT + self-verifying: safe to run repeatedly. Skips what's correct.
#  RUN AS ADMINISTRATOR.
#  Companion to TUNING-LOG.txt  (documents these as items [10]-[14], Section 7)
#
#  Applies the still-pending, vetted open items from TUNING-LOG Section 3:
#    [10] Wi-Fi  -> Prefer 5 GHz band        (3f/3b  ~4x throughput vs 2.4 GHz)
#    [11] NTFS   -> Last-Access OFF           (3f     DRAM-less SSD write-amp)
#    [12] PCIe   -> ASPM Off on AC            (3e     NVMe wake latency)
#    [13] Disk   -> Idle timeout 0 on AC      (3g     pointless spin-down stall)
#    [14] Wi-Fi  -> SkipOverDtim/Lprx off     (3k     first-packet latency)
#    [15] RAM    -> Memory Compression ON      (26H2   more effective RAM on 16GB)
#    [16] Tasks  -> telemetry sched tasks off  (3h     CEIP/Appraiser/DmClient/OneDC)
#    [17] LAN    -> Realtek EEE/Green/PS off    (3j     dock link renegotiation)
#
#  Usage:
#    .\advanced-tuning.ps1                 # apply all (prompts before Wi-Fi restart)
#    .\advanced-tuning.ps1 -WhatIf         # show what would change, do nothing
#    .\advanced-tuning.ps1 -SkipWiFiRestart# apply but DON'T bounce the adapter
#                                          #   ([14] then needs a manual restart/reboot)
#
#  REVERT (elevated):
#    Set-NetAdapterAdvancedProperty -Name WiFi -DisplayName 'Preferred Band' -DisplayValue '1. No Preference'
#    fsutil behavior set disablelastaccess 2
#    powercfg /setacvalueindex SCHEME_CURRENT SUB_PCIEXPRESS ASPM 1 ; powercfg /setactive SCHEME_CURRENT
#    powercfg /setacvalueindex SCHEME_CURRENT SUB_DISK DISKIDLE 1200 ; powercfg /setactive SCHEME_CURRENT
#    (set SkipOverDtimEnable / LprxEnable back to 1 under the Class key, then restart adapter)
# =============================================================================
[CmdletBinding(SupportsShouldProcess)]
param(
    [switch]$SkipWiFiRestart
)

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole('Administrators')) {
    Write-Host "ERROR: must run as Administrator. Right-click -> Run as administrator." -ForegroundColor Red
    exit 1
}

$changed = 0; $already = 0; $wifiTouched = $false
function Did($m)  { $script:changed++; Write-Host "  APPLIED  $m" -ForegroundColor Green }
function Same($m) { $script:already++; Write-Host "  ok       $m" -ForegroundColor DarkGray }
function Oops($m) { Write-Host "  FAILED   $m" -ForegroundColor Red }

Write-Host ""
Write-Host "=== ADVANCED TUNING (Tier 1) -- $(Get-Date -Format 'yyyy-MM-dd HH:mm') ===" -ForegroundColor White
Write-Host ""

# --- [10] Wi-Fi Preferred Band -> Prefer 5 GHz ----------------------------
#  NOTE: if your AP's 5 GHz signal is weak/far, this can reduce range. The
#  'Prefer' (not 'Force') setting still falls back to 2.4 GHz when 5 is unusable.
try {
    $pb = Get-NetAdapterAdvancedProperty -Name WiFi -RegistryKeyword 'RoamingPreferredBandType' -EA 0
    if ($pb.DisplayValue -eq '3. Prefer 5GHz band') { Same '[10] Wi-Fi Preferred Band = Prefer 5 GHz' }
    elseif ($pb -and $PSCmdlet.ShouldProcess('WiFi Preferred Band','set Prefer 5GHz band')) {
        Set-NetAdapterAdvancedProperty -Name WiFi -DisplayName 'Preferred Band' -DisplayValue '3. Prefer 5GHz band' -EA Stop
        $wifiTouched = $true
        Did '[10] Wi-Fi Preferred Band -> Prefer 5 GHz'
    } elseif (-not $pb) { Oops '[10] Preferred Band property not found on WiFi adapter' }
} catch { Oops "[10] $($_.Exception.Message)" }

# --- [11] NTFS Last-Access time -> disabled -------------------------------
try {
    $la = (fsutil behavior query disablelastaccess) -join ' '
    if ($la -match 'DisableLastAccess = 1') { Same '[11] NTFS Last-Access already disabled' }
    elseif ($PSCmdlet.ShouldProcess('NTFS DisableLastAccess','set 1 (User Managed, Disabled)')) {
        fsutil behavior set disablelastaccess 1 | Out-Null
        Did '[11] NTFS Last-Access -> disabled (less SSD write-amplification)'
    }
} catch { Oops "[11] $($_.Exception.Message)" }

# --- [12] PCIe ASPM on AC -> Off ------------------------------------------
try {
    $aspm = (powercfg /query SCHEME_CURRENT SUB_PCIEXPRESS ASPM | Select-String 'Current AC').Line
    if ($aspm -match '0x00000000') { Same '[12] PCIe ASPM AC = Off' }
    elseif ($PSCmdlet.ShouldProcess('PCIe ASPM AC','set Off (0)')) {
        powercfg /setacvalueindex SCHEME_CURRENT SUB_PCIEXPRESS ASPM 0 | Out-Null
        powercfg /setactive SCHEME_CURRENT | Out-Null
        Did '[12] PCIe ASPM AC -> Off (lower NVMe/PCIe wake latency)'
    }
} catch { Oops "[12] $($_.Exception.Message)" }

# --- [13] Disk idle timeout on AC -> 0 (never) ----------------------------
try {
    $di = (powercfg /query SCHEME_CURRENT SUB_DISK DISKIDLE | Select-String 'Current AC').Line
    if ($di -match '0x00000000') { Same '[13] Disk idle AC = never' }
    elseif ($PSCmdlet.ShouldProcess('Disk idle AC','set 0 (never)')) {
        powercfg /setacvalueindex SCHEME_CURRENT SUB_DISK DISKIDLE 0 | Out-Null
        powercfg /setactive SCHEME_CURRENT | Out-Null
        Did '[13] Disk idle AC -> never (no NVMe spin-down stall)'
    }
} catch { Oops "[13] $($_.Exception.Message)" }

# --- [14] Hidden Wi-Fi power savers -> off --------------------------------
#  Registry-only (no UI). Live in the NIC's Class key; take effect on adapter restart.
try {
    $a = Get-NetAdapter -Name WiFi -EA Stop
    $sub = (Get-PnpDeviceProperty -InstanceId $a.PnpDeviceID -KeyName 'DEVPKEY_Device_Driver' -EA Stop).Data
    $key = "HKLM:\SYSTEM\CurrentControlSet\Control\Class\$sub"
    if (-not (Test-Path $key)) { throw "Class key not found: $key" }
    foreach ($name in 'SkipOverDtimEnable','LprxEnable') {
        $cur = (Get-ItemProperty $key -Name $name -EA 0).$name
        if ($cur -eq 0) { Same "[14] $name already 0" }
        elseif ($PSCmdlet.ShouldProcess("$name","set 0")) {
            Set-ItemProperty $key -Name $name -Value 0 -Type DWord
            $wifiTouched = $true
            Did "[14] $name -> 0"
        }
    }
} catch { Oops "[14] $($_.Exception.Message)" }

# --- [15] Memory Compression -> enabled -----------------------------------
#  On a 16 GB machine running agents + WSL, compression trades a little CPU for
#  materially more effective RAM. Some debloat scripts disable it; re-enable.
try {
    $mm = Get-MMAgent
    if ($mm.MemoryCompression) { Same '[15] Memory Compression already enabled' }
    elseif ($PSCmdlet.ShouldProcess('MMAgent','Enable-MMAgent -MemoryCompression')) {
        Enable-MMAgent -MemoryCompression -EA Stop
        Did '[15] Memory Compression -> ENABLED (effective on next compressible pressure)'
    }
} catch { Oops "[15] $($_.Exception.Message)" }

# --- [16] Telemetry scheduled tasks -> disabled ---------------------------
#  DiagTrack is already disabled (baseline 4.5); these tasks still ran Ready.
try {
    $tasks = @(
        '\Microsoft\Windows\Customer Experience Improvement Program\Consolidator',
        '\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip',
        '\Microsoft\Windows\Customer Experience Improvement Program\KernelCeipTask',
        '\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser',
        '\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser Exp',
        '\Microsoft\Windows\Application Experience\ProgramDataUpdater',
        '\Microsoft\Windows\Feedback\Siuf\DmClient',
        '\Microsoft\Windows\Feedback\Siuf\DmClientOnScenarioDownload'
    )
    foreach ($t in $tasks) {
        $leaf = Split-Path $t -Leaf; $path = (Split-Path $t -Parent) + '\'
        $st = Get-ScheduledTask -TaskName $leaf -TaskPath $path -EA 0
        if (-not $st) { continue }                                   # not present on this build
        if ($st.State -eq 'Disabled') { Same "[16] task already off: $leaf" }
        elseif ($PSCmdlet.ShouldProcess($leaf,'disable scheduled task')) {
            Disable-ScheduledTask -TaskName $leaf -TaskPath $path -EA Stop | Out-Null
            Did "[16] task disabled: $leaf"
        }
    }
    # OneDC_Updater (MSI One Dragon Center updater) lives at an unknown path
    $odc = Get-ScheduledTask -TaskName 'OneDC_Updater' -EA 0
    if ($odc) {
        if ($odc.State -eq 'Disabled') { Same '[16] task already off: OneDC_Updater' }
        elseif ($PSCmdlet.ShouldProcess('OneDC_Updater','disable scheduled task')) {
            $odc | Disable-ScheduledTask -EA Stop | Out-Null
            Did '[16] task disabled: OneDC_Updater (does NOT affect MSI fan control)'
        }
    }
} catch { Oops "[16] $($_.Exception.Message)" }

# --- [17] Realtek Ethernet power-saving -> off ----------------------------
#  Dormant while on Wi-Fi, but causes link renegotiation drops when docked.
try {
    $eth = Get-NetAdapter -Physical -EA 0 | Where-Object InterfaceDescription -match 'Realtek.*(Ethernet|GBE|Gaming|Controller)'
    if (-not $eth) { Same '[17] no Realtek Ethernet adapter present (skipped)' }
    else {
        foreach ($nic in $eth) {
            foreach ($kw in 'Energy-Efficient Ethernet','Green Ethernet','Power Saving Mode','*EEE','EnableGreenEthernet','PowerSavingMode') {
                $prop = Get-NetAdapterAdvancedProperty -Name $nic.Name -EA 0 |
                        Where-Object { $_.DisplayName -eq $kw -or $_.RegistryKeyword -eq $kw }
                foreach ($pr in $prop) {
                    $offVal = ($pr.ValidDisplayValues | Where-Object { $_ -match 'Disabl|Off' } | Select-Object -First 1)
                    if (-not $offVal) { $offVal = 'Disabled' }
                    if ($pr.DisplayValue -eq $offVal) { Same "[17] $($nic.Name): $($pr.DisplayName) already off" }
                    elseif ($PSCmdlet.ShouldProcess("$($nic.Name) / $($pr.DisplayName)","set $offVal")) {
                        try { Set-NetAdapterAdvancedProperty -Name $nic.Name -DisplayName $pr.DisplayName -DisplayValue $offVal -EA Stop
                              Did "[17] $($nic.Name): $($pr.DisplayName) -> $offVal" }
                        catch { Oops "[17] $($pr.DisplayName): $($_.Exception.Message)" }
                    }
                }
            }
        }
    }
} catch { Oops "[17] $($_.Exception.Message)" }

# --- Apply Wi-Fi changes by bouncing the adapter --------------------------
if ($wifiTouched -and -not $SkipWiFiRestart) {
    Write-Host ""
    Write-Host "  Wi-Fi settings changed. The adapter must restart to apply [14]" -ForegroundColor Yellow
    Write-Host "  (this briefly drops your connection ~5 s)." -ForegroundColor Yellow
    if ($PSCmdlet.ShouldProcess('WiFi adapter','restart to apply changes')) {
        try { Restart-NetAdapter -Name WiFi -EA Stop; Did '[14] Wi-Fi adapter restarted (changes live)' }
        catch { Oops "adapter restart failed -- reboot to apply: $($_.Exception.Message)" }
    }
} elseif ($wifiTouched) {
    Write-Host ""
    Write-Host "  NOTE: -SkipWiFiRestart set. [14] applies after you restart the WiFi" -ForegroundColor Yellow
    Write-Host "        adapter or reboot:  Restart-NetAdapter -Name WiFi" -ForegroundColor Cyan
}

# --- Summary + quick verify -----------------------------------------------
Write-Host ""
Write-Host ("RESULT:  {0} applied, {1} already correct" -f $changed, $already) -ForegroundColor Green
Write-Host ""
Write-Host "--- verify ---" -ForegroundColor White
"  Preferred Band : {0}" -f (Get-NetAdapterAdvancedProperty -Name WiFi -RegistryKeyword 'RoamingPreferredBandType' -EA 0).DisplayValue
"  Link speed     : {0}  (~1200 Mbps once associated on 5 GHz)" -f (Get-NetAdapter -Name WiFi -EA 0).LinkSpeed
"  Last-Access    : {0}" -f ((fsutil behavior query disablelastaccess) -join ' ')
"  ASPM AC        : {0}" -f ((powercfg /query SCHEME_CURRENT SUB_PCIEXPRESS ASPM | Select-String 'Current AC').Line -replace '.*: ','')
"  DiskIdle AC    : {0}" -f ((powercfg /query SCHEME_CURRENT SUB_DISK DISKIDLE | Select-String 'Current AC').Line -replace '.*: ','')
"  MemCompression : {0}" -f (Get-MMAgent).MemoryCompression
$ceip = Get-ScheduledTask -TaskName 'Consolidator' -EA 0
"  CEIP task      : {0}" -f $(if($ceip){$ceip.State}else{'absent'})
Write-Host ""
Write-Host "  Tip: after it associates on 5 GHz, re-check link speed. If it stays on" -ForegroundColor DarkGray
Write-Host "       2.4 GHz, your AP's 5 GHz SSID may be out of range or disabled." -ForegroundColor DarkGray
Write-Host ""
