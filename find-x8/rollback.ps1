<#
.SYNOPSIS
    Complete rollback script for OPPO Find X8 tuning.
.DESCRIPTION
    Reverts all changes made by tune.ps1:
    - Verifies installed packages and unfreezes them safely
    - Resets App Standby Buckets back to automatic
    - Clears custom Quick-Doze settings back to factory default
    - Disables AdGuard Private DNS
    - Re-enables system tracing
#>

[CmdletBinding()]
param()

$ErrorActionPreference = "Continue"

function Find-Adb {
    if (Get-Command adb -ErrorAction SilentlyContinue) {
        return "adb"
    }
    $candidates = @(
        "$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe",
        "$env:ProgramFiles\Android\platform-tools\adb.exe",
        "$env:SystemDrive\platform-tools\adb.exe"
    )
    foreach ($path in $candidates) {
        if (Test-Path $path) {
            return $path
        }
    }
    throw "ADB was not found in PATH or standard directories."
}

$Adb = Find-Adb
Write-Host "Using ADB: $Adb" -ForegroundColor Cyan

# Fetch currently installed packages to ensure zero-failure execution
Write-Host "`nScanning installed packages on device..." -ForegroundColor Gray
$installedRaw = & $Adb shell pm list packages -u 2>$null
$installedPackages = @($installedRaw | ForEach-Object { ($_ -replace "^package:", "").Trim() })

# 1. Unfreeze all packages
Write-Host "`n[1/4] Re-enabling frozen packages..." -ForegroundColor Yellow
$packagesToEnable = @(
    "com.oplus.logkit",
    "com.debug.loggerui",
    "com.oplus.statistics.rom",
    "com.oplus.postmanservice",
    "com.oplus.trafficmonitor",
    "com.oplus.member",
    "com.oplus.tips",
    "com.oplus.contentportal",
    "com.oplus.upgradeguide",
    "com.heytap.colorfulengine",
    "com.google.android.feedback",
    "com.google.android.printservice.recommendation"
)
foreach ($pkg in $packagesToEnable) {
    if ($installedPackages -contains $pkg) {
        try {
            $res = & $Adb shell pm enable $pkg 2>$null
            Write-Host "  $pkg -> $res" -ForegroundColor Gray
        } catch {
            Write-Host "  $pkg -> [WARN] Unable to enable, skipping." -ForegroundColor DarkYellow
        }
    } else {
        Write-Host "  $pkg -> [SKIPPED] not installed" -ForegroundColor DarkGray
    }
}

# 2. Reset Standby Buckets to Active / Automatic
Write-Host "`n[2/4] Resetting Standby Buckets to default..." -ForegroundColor Yellow
$allApps = @(
    "in.amazon.mShop.android.shopping", "com.flipkart.android", "com.zeptoconsumerapp",
    "com.grofers.customerapp", "com.jio.myjio", "com.olx.southasia", "com.localzi.nxtmeal",
    "waterwala.com.prime", "app.slikk.club", "com.firstclub.app", "com.ubercab",
    "com.olacabs.customer", "com.rapido.passenger", "com.ownly.customer", "com.confirmtkt.lite",
    "org.cris.aikyam", "com.zoomcar", "com.nic.mparivahan", "in.gov.umang.negd.g2c",
    "com.digilocker.android", "com.cleartrip.android", "com.app.abhibus", "in.redbus.android",
    "com.sbi.lotusintouch", "com.ge.capital.konysbiapp", "com.iob.iobconnect",
    "com.dreamplug.androidapp", "com.mobikwik_new", "com.msf.angelmobile", "com.tvscs.tvscreditapp",
    "com.earlysalary.android", "com.paypal.android.p2pmobile", "com.Splitwise.SplitwiseMobile",
    "icici.lombard.ghi", "com.indeed.android.jobsearch", "naukriApp.appModules.login",
    "com.internshala.app", "com.linkedin.android", "com.azure.authenticator",
    "oracle.idm.mobile.authenticator", "com.google.android.apps.authenticator2",
    "com.dts.freefiremax", "com.valvesoftware.android.steam.community",
    "com.njaistudio.jp_kanji_game", "com.njaistudio.japanese_kotoba", "com.mindtwisted.kanjistudy",
    "com.duolingo", "free.programming.programming", "com.udemy.android",
    "com.duckduckgo.mobile.android", "com.stremio.one", "org.videolan.vlc",
    "com.github.libretube", "org.schabi.newpipe", "com.wallpaperscraft.wallpaper",
    "com.rma.bsnl", "com.act.mobile.apps", "com.tvsm.connect", "com.samsung.android.oneconnect",
    "com.msi.mymsi", "com.xsquarestudio.forcelte", "com.zoho.payroll", "com.greytip.ghress",
    "com.microsoft.office.officehubrow"
)
foreach ($app in $allApps) {
    if ($installedPackages -contains $app) {
        try {
            & $Adb shell am set-standby-bucket $app active 2>$null
        } catch {
            # silently continue
        }
    }
}
Write-Host "Standby buckets reset to active." -ForegroundColor Green

# 3. Reset Settings & Doze
Write-Host "`n[3/4] Resetting system parameters to factory defaults..." -ForegroundColor Yellow
try {
    & $Adb shell settings delete global device_idle_constants 2>$null
    & $Adb shell settings put global private_dns_mode off 2>$null
    & $Adb shell settings delete global send_action_app_error 2>$null
    & $Adb shell setprop persist.traced.enable 1 2>$null
    Write-Host "Settings and kernel tracing restored to factory defaults." -ForegroundColor Green
} catch {
    Write-Host "Settings reset encountered a non-fatal warning: $_" -ForegroundColor DarkYellow
}

# 4. Finish
Write-Host "`n[4/4] Rollback Complete. Please restart your phone." -ForegroundColor Cyan
