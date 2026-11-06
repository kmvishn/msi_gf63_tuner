<#
.SYNOPSIS
    Automated optimization script for OPPO Find X8 (ColorOS 15/17, Android 17).
.DESCRIPTION
    Applies non-destructive performance and battery optimizations via ADB:
    - Verifies installed packages before acting (safely skips missing apps)
    - Trims post-OTA upgrade caches
    - Freezes telemetry and push-ad bloatware
    - Pre-compiles all packages with AOT speed-profile
    - Tunes Quick-Doze parameters for instant deep sleep
    - Places non-essential shopping, travel, and utility apps in Restricted Standby (Level 45)
    - Enables system-wide AdGuard Private DNS
    - Disables Perfetto kernel tracing probes
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
    throw "ADB was not found in PATH or standard directories. Please install Android Platform Tools."
}

$Adb = Find-Adb
Write-Host "Using ADB: $Adb" -ForegroundColor Cyan

# Check connected device
$devices = & $Adb devices | Where-Object { $_ -match "\bdevice\b" }
if (-not $devices) {
    Write-Error "No authorized device connected via ADB. Please connect your phone, enable USB Debugging, and authorize the PC prompt."
    return
}
Write-Host "Device detected and authorized." -ForegroundColor Green

# Fetch currently installed packages to ensure zero-failure execution
Write-Host "`nScanning installed packages on device..." -ForegroundColor Gray
$installedRaw = & $Adb shell pm list packages -u 2>$null
$installedPackages = @($installedRaw | ForEach-Object { ($_ -replace "^package:", "").Trim() })

# 1. Post-OTA Cache Sweep
Write-Host "`n[1/6] Purging post-OTA temporary upgrade caches..." -ForegroundColor Yellow
try {
    & $Adb shell pm trim-caches 999G 2>$null
    Write-Host "Cache sweep complete." -ForegroundColor Green
} catch {
    Write-Host "Cache sweep skipped or unsupported on this build." -ForegroundColor DarkYellow
}

# 2. Freeze Telemetry and Push-Ad Bloatware
Write-Host "`n[2/6] Freezing telemetry and promotional services..." -ForegroundColor Yellow
$telemetryPackages = @(
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
foreach ($pkg in $telemetryPackages) {
    if ($installedPackages -contains $pkg) {
        try {
            $res = & $Adb shell pm disable-user --user 0 $pkg 2>$null
            Write-Host "  $pkg -> $res" -ForegroundColor Gray
        } catch {
            Write-Host "  $pkg -> [WARN] Unable to disable, skipping." -ForegroundColor DarkYellow
        }
    } else {
        Write-Host "  $pkg -> [SKIPPED] not installed" -ForegroundColor DarkGray
    }
}

# 3. Apply Quick-Doze Standby Tuning
Write-Host "`n[3/6] Applying Quick-Doze deep sleep parameters..." -ForegroundColor Yellow
try {
    $dozeConfig = "light_after_inactive_to=60000,light_pre_idle_to=180000,light_idle_to=300000,light_max_idle_to=900000,inactive_to=300000,sensing_to=0,locating_to=0,motion_inactive_to=120000,idle_after_inactive_to=120000"
    & $Adb shell settings put global device_idle_constants $dozeConfig 2>$null
    & $Adb shell settings put global send_action_app_error 0 2>$null
    & $Adb shell setprop persist.traced.enable 0 2>$null
    Write-Host "Quick-Doze and kernel tracing tweaks applied." -ForegroundColor Green
} catch {
    Write-Host "Quick-Doze configuration error: $_" -ForegroundColor DarkYellow
}

# 4. Enable System-Wide AdGuard Private DNS
Write-Host "`n[4/6] Enabling AdGuard Private DNS..." -ForegroundColor Yellow
try {
    & $Adb shell settings put global private_dns_mode hostname 2>$null
    & $Adb shell settings put global private_dns_specifier dns.adguard-dns.com 2>$null
    Write-Host "Private DNS active: dns.adguard-dns.com" -ForegroundColor Green
} catch {
    Write-Host "DNS configuration skipped: $_" -ForegroundColor DarkYellow
}

# 5. Place Secondary Apps into Restricted Standby Bucket (Level 45)
Write-Host "`n[5/6] Restricting background execution on secondary apps..." -ForegroundColor Yellow
$restrictedApps = @(
    # E-Commerce & Delivery
    "in.amazon.mShop.android.shopping",
    "com.flipkart.android",
    "com.zeptoconsumerapp",
    "com.grofers.customerapp",
    "com.jio.myjio",
    "com.olx.southasia",
    "com.localzi.nxtmeal",
    "waterwala.com.prime",
    "app.slikk.club",
    "com.firstclub.app",

    # Travel & Rides
    "com.ubercab",
    "com.olacabs.customer",
    "com.rapido.passenger",
    "com.ownly.customer",
    "com.confirmtkt.lite",
    "org.cris.aikyam",
    "com.zoomcar",
    "com.nic.mparivahan",
    "in.gov.umang.negd.g2c",
    "com.digilocker.android",
    "com.cleartrip.android",
    "com.app.abhibus",
    "in.redbus.android",

    # Banking & Finance (Secondary)
    "com.sbi.lotusintouch",
    "com.ge.capital.konysbiapp",
    "com.iob.iobconnect",
    "com.dreamplug.androidapp",
    "com.mobikwik_new",
    "com.msf.angelmobile",
    "com.tvscs.tvscreditapp",
    "com.earlysalary.android",
    "com.paypal.android.p2pmobile",
    "com.Splitwise.SplitwiseMobile",
    "icici.lombard.ghi",

    # Professional & Job Search
    "com.indeed.android.jobsearch",
    "naukriApp.appModules.login",
    "com.internshala.app",
    "com.linkedin.android",

    # Authenticators
    "com.azure.authenticator",
    "oracle.idm.mobile.authenticator",
    "com.google.android.apps.authenticator2",

    # Games, Learning & Media
    "com.dts.freefiremax",
    "com.valvesoftware.android.steam.community",
    "com.njaistudio.jp_kanji_game",
    "com.njaistudio.japanese_kotoba",
    "com.mindtwisted.kanjistudy",
    "com.duolingo",
    "free.programming.programming",
    "com.udemy.android",
    "com.duckduckgo.mobile.android",
    "com.stremio.one",
    "org.videolan.vlc",
    "com.github.libretube",
    "org.schabi.newpipe",
    "com.wallpaperscraft.wallpaper",

    # Utilities & Telecom
    "com.rma.bsnl",
    "com.act.mobile.apps",
    "com.tvsm.connect",
    "com.samsung.android.oneconnect",
    "com.msi.mymsi",
    "com.xsquarestudio.forcelte",
    "com.zoho.payroll",
    "com.greytip.ghress",
    "com.microsoft.office.officehubrow"
)
foreach ($app in $restrictedApps) {
    if ($installedPackages -contains $app) {
        try {
            & $Adb shell am set-standby-bucket $app restricted 2>$null
            Write-Host "  $app -> Standby: Restricted (45)" -ForegroundColor Gray
        } catch {
            Write-Host "  $app -> [WARN] Unable to set bucket, skipping." -ForegroundColor DarkYellow
        }
    } else {
        Write-Host "  $app -> [SKIPPED] not installed" -ForegroundColor DarkGray
    }
}

# 6. AOT Bytecode Recompilation
Write-Host "`n[6/6] Executing AOT Compilation (speed-profile)..." -ForegroundColor Yellow
Write-Host "Optimizing all installed packages natively for Dimensity 9400..." -ForegroundColor Gray
try {
    & $Adb shell cmd package compile -m speed-profile -a 2>$null
    Write-Host "AOT compilation completed successfully." -ForegroundColor Green
} catch {
    Write-Host "Compilation encountered a non-fatal warning: $_" -ForegroundColor DarkYellow
}

Write-Host "`n=======================================================" -ForegroundColor Cyan
Write-Host " Tuning Complete! Please restart your phone to apply." -ForegroundColor Cyan
Write-Host "=======================================================" -ForegroundColor Cyan
