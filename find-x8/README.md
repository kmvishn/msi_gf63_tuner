# OPPO Find X8 Optimization Guide (ColorOS 15 / ColorOS 17 / Android 17)

A battle-tested, non-destructive, zero-risk optimization guide and automation script for the **OPPO Find X8** (MediaTek Dimensity 9400, ColorOS 15/17, Android 17).

Designed specifically for **daily-driver devices upgraded over-the-air (OTA) across major Android releases without a factory reset**, maintaining 100% stability, flawless banking/UPI app compatibility, full biometric security, and zero lost data.

---

## 📋 Table of Contents
- [Target Hardware & Platform](#-target-hardware--platform)
- [Core Philosophy](#-core-philosophy)
- [Summary of Optimizations](#-summary-of-optimizations)
- [1. Ahead-Of-Time (AOT) ART Compilation](#1-ahead-of-time-aot-art-compilation)
- [2. Post-OTA Temporary Cache Purge](#2-post-ota-temporary-cache-purge)
- [3. Bloatware & Telemetry Freezing](#3-bloatware--telemetry-freezing)
- [4. Quick-Doze & Standby Battery Tuning](#4-quick-doze--standby-battery-tuning)
- [5. App Standby Buckets (Restricted Mode)](#5-app-standby-buckets-restricted-mode)
- [6. Network & Ad-Blocking (Private DNS)](#6-network--ad-blocking-private-dns)
- [7. Kernel & Profiling Overhead Suppression](#7-kernel--profiling-overhead-suppression)
- [8. Hardware & System Settings Recommendations](#8-hardware--system-settings-recommendations)
- [9. Automated Scripts (`tune.ps1` & `rollback.ps1`)](#9-automated-scripts)
- [10. Complete Rollback Reference](#10-complete-rollback-reference)

---

## 📱 Target Hardware & Platform
- **Device:** OPPO Find X8 (CPH2651 / Global & Indian variants)
- **SoC:** MediaTek Dimensity 9400 (TSMC 3nm, 1x Cortex-X925, 3x Cortex-X4, 4x Cortex-A720)
- **Memory & Storage:** 12GB / 16GB LPDDR5X RAM + UFS 4.0 Storage
- **Battery:** 5630 mAh Silicon-Carbon Glacier Battery
- **OS Baseline:** Android 17 / ColorOS 17 (Target SDK 37, Linux Kernel 6.6)
- **Upgrade History:** Maintained continuously from ColorOS 15 to Android 17 without a factory reset.

---

## 🛡️ Core Philosophy
1. **Zero `pm uninstall`:** No packages are permanently uninstalled. Everything is managed through `pm disable-user --user 0` or Android Standby Buckets.
2. **100% Reversible in Seconds:** All app data and settings remain untouched on the device storage. Every change can be rolled back immediately with a single command.
3. **Protected Core Services:** Zero modifications to:
   - Google Play Services (`com.google.android.gms`) core network tunnels
   - Banking / UPI authentication keystores
   - Biometrics (Fingerprint / Face Unlock)
   - Stock Camera processing pipeline, Gallery, Display engines
   - Cellular, SIM, VoLTE, and Wi-Fi drivers

---

## ⚡ Summary of Optimizations

| Category | Action | Benefit |
| :--- | :--- | :--- |
| **Performance** | Force AOT Compilation (`speed-profile`) | Eradicates JIT CPU overhead and micro-stutters post-OTA upgrade |
| **Storage** | Safe Cache Trim (`pm trim-caches 999G`) | Purges stale OS upgrade temporary caches and extraction blobs |
| **Standby Battery** | Quick-Doze Acceleration | Enters light sleep in 1 min, deep sleep in 5 min (vs stock 30 min) |
| **Background Churn** | Standby Bucket 45 (Restricted) | Forbids background activity on shopping, travel, and idle banking apps |
| **Telemetry & Ads** | Freezes push-ad daemons & loggers | Halts background logging, telemetry reporting, and promo notification spam |
| **Network & Privacy**| System-Wide Private DNS (AdGuard) | Blocks in-app ads and tracking beacons without VPN battery overhead |
| **Kernel Latency** | Disables Perfetto Traced Probes | Frees up continuous kernel tracepoint monitoring and memory buffers |

---

## 1. Ahead-Of-Time (AOT) ART Compilation

### The Problem
When Android devices undergo major over-the-air upgrades (e.g., ColorOS 15 to Android 17) without a clean wipe, previous pre-compiled code caches (`.odex` / `.vdex`) are invalidated. Apps default to running in **interpreted mode** or on-the-fly **JIT (Just-In-Time) compilation**, which forces the Dimensity 9400 CPU to compile code on the fly during user interaction, resulting in random frame drops, warmth, and battery drain.

### The Solution: `speed-profile`
```bash
adb shell cmd package compile -m speed-profile -a
```

### Why `speed-profile` instead of full `speed`?
- **Full `speed` (`-m speed`):** Compiles 100% of an APK's bytecode into native machine code. This wastes gigabytes of internal storage on dead/unused code (e.g. obscure debugging routines, rarely visited settings) and clutters the CPU's L2/L3 caches.
- **`speed-profile` (`-m speed-profile`):** Uses Android's Profile-Guided Optimization (PGO) baselines to compile **only the hot paths**—app launch sequences, UI feed rendering, and primary interactive functions. You get the maximum possible launch and scroll speed without ballooning storage.

---

## 2. Post-OTA Temporary Cache Purge

```bash
adb shell pm trim-caches 999G
```
Tells Android's `PackageManagerService` to safely sweep temporary installation artifacts, obsolete `.apk` extractions, and leftover OTA cache blobs from `/data/local/tmp` without touching user data, photos, messages, or login tokens.

---

## 3. Bloatware & Telemetry Freezing

Frozen using `pm disable-user --user 0 <package>`:

```bash
# ColorOS Diagnostic & ROM Telemetry
adb shell pm disable-user --user 0 com.oplus.logkit
adb shell pm disable-user --user 0 com.debug.loggerui
adb shell pm disable-user --user 0 com.oplus.statistics.rom
adb shell pm disable-user --user 0 com.oplus.postmanservice
adb shell pm disable-user --user 0 com.oplus.trafficmonitor

# Promotional Engines & Push Notification Spam
adb shell pm disable-user --user 0 com.oplus.member
adb shell pm disable-user --user 0 com.oplus.tips
adb shell pm disable-user --user 0 com.oplus.contentportal
adb shell pm disable-user --user 0 com.oplus.upgradeguide
adb shell pm disable-user --user 0 com.heytap.colorfulengine

# Google Telemetry & Background Scanners
adb shell pm disable-user --user 0 com.google.android.feedback
adb shell pm disable-user --user 0 com.google.android.printservice.recommendation
```

*(Note: Certain system-level packages like `com.heytap.htms` and `com.coloros.assistantscreen` are protected by Android 17's security framework with `only root can delete system app for a particular user`. This is a built-in platform integrity safeguard).*

---

## 4. Quick-Doze & Standby Battery Tuning

### The Problem
Stock Android takes **30+ minutes** of zero movement while stationary before initiating deep sleep. When carried in a pocket or handled periodically, the phone rarely reaches deep sleep, causing persistent 1.5%–2.5%/hour standby drain.

### The Solution: Balanced Quick-Doze
```bash
adb shell settings put global device_idle_constants "light_after_inactive_to=60000,light_pre_idle_to=180000,light_idle_to=300000,light_max_idle_to=900000,inactive_to=300000,sensing_to=0,locating_to=0,motion_inactive_to=120000,idle_after_inactive_to=120000"
```

### Key Parameters:
- `light_after_inactive_to=60000`: Light Doze engages **1 minute** after screen off (suspends non-essential background sync).
- `inactive_to=300000`: Deep Doze begins after **5 minutes** of stillness (down from 30 mins).
- `sensing_to=0, locating_to=0`: Eliminates battery-wasting GPS/sensor checks before sleeping.
- **Robustness Guarantee:** High-priority FCM push notifications (WhatsApp, Telegram, Phone calls, SMS, Banking alerts) bypass Doze automatically. Morning alarms and timers remain 100% reliable.

---

## 5. App Standby Buckets (Restricted Mode)

Instead of disabling secondary apps, we assign them to Android 17's **Restricted Standby Bucket (Level 45)**:

```bash
adb shell am set-standby-bucket <package_name> restricted
```

### What this does:
- The app is completely blocked from running background jobs, acquiring background wakelocks, or polling the network when not on screen.
- When you tap the app icon, it launches instantly with **100% functionality and full speed**.

### Apps Placed in Restricted Mode:
- **E-Commerce & Food:** Amazon (`in.amazon.mShop.android.shopping`), Flipkart (`com.flipkart.android`), Zepto (`com.zeptoconsumerapp`), Blinkit (`com.grofers.customerapp`), OLX (`com.olx.southasia`), Slikk (`app.slikk.club`), First Club (`com.firstclub.app`), NxtMeal (`com.localzi.nxtmeal`), DrinkPrime (`waterwala.com.prime`).
- **Rides & Travel:** Uber (`com.ubercab`), Ola (`com.olacabs.customer`), Rapido (`com.rapido.passenger`), Ownly (`com.ownly.customer`), ConfirmTkt (`com.confirmtkt.lite`), Aikyam (`org.cris.aikyam`), Zoomcar (`com.zoomcar`), Cleartrip (`com.cleartrip.android`), Abhibus (`com.app.abhibus`), Redbus (`in.redbus.android`), mParivahan (`com.nic.mparivahan`), Umang (`in.gov.umang.negd.g2c`), DigiLocker (`com.digilocker.android`).
- **Banking & Payments (Secondary):** SBI YONO (`com.sbi.lotusintouch`), SBI Card (`com.ge.capital.konysbiapp`), IOB (`com.iob.iobconnect`), CRED (`com.dreamplug.androidapp`), MobiKwik (`com.mobikwik_new`), Angel One (`com.msf.angelmobile`), TVS Credit (`com.tvscs.tvscreditapp`), EarlySalary (`com.earlysalary.android`), PayPal (`com.paypal.android.p2pmobile`), Splitwise (`com.Splitwise.SplitwiseMobile`), ICICI Lombard (`icici.lombard.ghi`).
- **Job Search:** Indeed (`com.indeed.android.jobsearch`), Naukri (`naukriApp.appModules.login`), Internshala (`com.internshala.app`), LinkedIn (`com.linkedin.android`).
- **Authenticators:** Microsoft / Azure (`com.azure.authenticator`), Oracle (`oracle.idm.mobile.authenticator`), Google Authenticator (`com.google.android.apps.authenticator2`).
- **Games & Learning:** Free Fire MAX (`com.dts.freefiremax`), Steam (`com.valvesoftware.android.steam.community`), Duolingo (`com.duolingo`), Kanji Study (`com.mindtwisted.kanjistudy`), Japanese Kotoba / Kanji games, Programming Hub, Udemy.
- **Utilities & Telecom:** DuckDuckGo (`com.duckduckgo.mobile.android`), BSNL (`com.rma.bsnl`), ACT Fibernet (`com.act.mobile.apps`), TVS Connect, SmartThings, MyMSI, Force LTE, Zoho Payroll, greytHR, Microsoft 365, Stremio, VLC, LibreTube, NewPipe, WallpapersCraft.

---

## 6. Network & Ad-Blocking (Private DNS)

```bash
adb shell settings put global private_dns_mode hostname
adb shell settings put global private_dns_specifier dns.adguard-dns.com
```
- Operates natively at the OS network stack.
- Blocks advertising and user telemetry domains across all apps and browsers.
- **Zero Battery Overhead:** Unlike third-party VPN ad-blockers that run a persistent local VPN daemon in RAM, Private DNS consumes 0% CPU and preserves socket battery.
- *Can be toggled anytime in:* **Settings > Connection & sharing > Private DNS**.

---

## 7. Kernel & Profiling Overhead Suppression

### Disable Developer Tracing Probes
When Developer Options is unlocked, Android automatically runs Perfetto tracing daemons (`traced`, `traced_probes`) which continuously intercept kernel tracepoints and monitor thread context switches.
```bash
adb shell setprop persist.traced.enable 0
```
Completely shuts down background Perfetto kernel probes on reboot, saving kernel context switching overhead.

### Disable Crash Stack Serialization
```bash
adb shell settings put global send_action_app_error 0
```
Stops background packaging and dispatch of diagnostic crash telemetry.

---

## 8. Hardware & System Settings Recommendations

1. **Virtual RAM / RAM Expansion -> KEEP OFF (Verified `persist.sys.oplus.nandswap = false`):**
   On a device equipped with 12GB of high-speed LPDDR5X physical RAM, Virtual RAM uses slow UFS flash storage as swap memory. It increases storage write cycles, wears out NAND flash, and introduces micro-stutters.
2. **Smart 5G -> ENABLED:**
   *Settings > Mobile network > More settings > Smart 5G*. Keeps the phone on efficient LTE during background standby and automatically engages high-speed 5G only for large downloads, video streaming, or gaming.
3. **Screen Colour Mode -> NATURAL:**
   *Settings > Display & brightness > Screen colour mode > Natural*. Calibrates the LTPO OLED panel to accurate sRGB, reducing subpixel overdrive and saving battery on bright interfaces.
4. **Developer Options -> SAFE TO TURN OFF:**
   Once tuning is complete, toggling off Developer Options in Settings does **NOT** undo any of these changes. In fact, turning it off is recommended to prevent banking and corporate apps from flagging developer mode.

---

## 9. Automated Scripts

Run these scripts from your PC via PowerShell with your phone connected and USB Debugging enabled.

- **`tune.ps1`**: Applies the full suite of optimizations automatically.
- **`rollback.ps1`**: Reverts all changes, unfreezes all packages, and resets system settings back to stock defaults.

---

## 10. Complete Rollback Reference

If you ever wish to manually revert any individual component:

### Restore a Frozen Package
```bash
adb shell pm enable <package_name>
# Example:
adb shell pm enable com.oplus.member
```

### Reset Standby Bucket to Automatic
```bash
adb shell am set-standby-bucket <package_name> active
```

### Reset Doze Parameters to Factory Stock
```bash
adb shell settings delete global device_idle_constants
```

### Reset Private DNS to Off
```bash
adb shell settings put global private_dns_mode off
```

### Re-Enable System Tracing
```bash
adb shell setprop persist.traced.enable 1
```
