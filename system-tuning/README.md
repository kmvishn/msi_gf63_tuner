# Windows System Tuning — MSI Thin GF63 12HW

One PowerShell script that **checks, applies, and documents** a set of Windows 11
performance and debloat tweaks for an MSI Thin GF63 12HW
(i5-12500H · 16 GB · Iris Xe + Arc A370M · DRAM-less NVMe).

> **This is a machine-specific profile, not a universal optimizer.** Read
> [Caveats](#caveats) before running it on any other PC.

---

## Contents

- [Files](#files)
- [Quick start](#quick-start)
- [The menu](#the-menu)
- [Command-line usage](#command-line-usage)
- [What gets tuned](#what-gets-tuned)
  - [Core fixes `[1]`–`[9]`](#core-fixes-19)
  - [Baseline `[4.x]`](#baseline-4x)
  - [Advanced `[10]`–`[18]`](#advanced-1018)
  - [GPU driver pin `[19]`](#gpu-driver-pin-19)
  - [Idle-RAM debloat `[20]`–`[27]`](#idle-ram-debloat-2027)
- [Deliberately left alone](#deliberately-left-alone)
- [Known gotchas](#known-gotchas)
- [When to re-run](#when-to-re-run)
- [Reverting](#reverting)
- [Caveats](#caveats)

---

## Files

| File | Purpose |
|---|---|
| [`tune.ps1`](tune.ps1) | **The only script.** Each item is defined once as a *check* plus a *fix*. Verify runs the checks; Apply fixes whatever fails, then re-checks it. |
| [`TUNING-LOG.txt`](TUNING-LOG.txt) | The source of truth: why each item exists, before/after values, and the history (drift events, conflicts found). |

`tune.ps1` replaced four older scripts on 2026-10-10: `verify-tuning`,
`reapply-tuning`, `advanced-tuning` and `pin-gpu-drivers`. Having one
definition per item means the checker and the fixer can't disagree any more.

---

## Quick start

```powershell
# Interactive menu
powershell -ExecutionPolicy Bypass -File tune.ps1

# Check what has drifted. Read-only and works without admin.
powershell -ExecutionPolicy Bypass -File tune.ps1 verify

# Fix anything that drifted (run PowerShell as Administrator)
powershell -ExecutionPolicy Bypass -File tune.ps1 apply
```

If you see *"running scripts is disabled on this system"*, keep the
`-ExecutionPolicy Bypass` flag, or allow local scripts once for your user:
`Set-ExecutionPolicy -Scope CurrentUser RemoteSigned`.

**Admin rules:**
- **Verify** works as a normal user. A few checks need admin (Defender
  exclusions, shadow storage, Memory Compression); they show as `SKIPPED`
  until you run elevated.
- **Apply** and the **GPU pin** need admin. From the menu, the script offers to
  relaunch itself elevated (you'll get a UAC prompt).

---

## The menu

```
=================== MSI GF63 TUNING ===================
  1  Verify everything (read-only)
  2  Apply EVERYTHING that drifted
  3  Apply core fixes        [1]-[9]
  4  Apply baseline          NTFS, visual FX, services
  5  Apply advanced          [10]-[18]
  6  Apply idle-RAM debloat  [20]-[27]
  7  Apply specific items    (enter IDs, e.g. 15,23)
  8  GPU driver pin          [19]
  9  List all items
  Q  Quit
```

Output legend:

| Tag | Meaning |
|---|---|
| `OK` / `ok` | Already correct. Apply leaves it untouched. |
| `DRIFTED` | Not at the tuned value (Verify). The `fix` line gives the command. |
| `APPLIED` | Was fixed **and** passed its re-check. |
| `FAILED` | The fix ran, but the re-check still fails, or the fix threw an error. |
| `SKIPPED` | Can't be checked here: not installed, or needs admin. |
| `MANUAL` | No automatic fix. The hint says what to do. |

---

## Command-line usage

```powershell
.\tune.ps1 verify                        # check everything
.\tune.ps1 verify -Group debloat         # one group
.\tune.ps1 apply                         # fix everything that drifted
.\tune.ps1 apply -Group core,advanced    # groups: core, baseline, advanced, debloat
.\tune.ps1 apply -Item 15,23             # specific item IDs
.\tune.ps1 apply -WhatIf                 # dry run: shows what it would fix, changes nothing
.\tune.ps1 apply -SkipWiFiRestart        # don't restart the Wi-Fi adapter at the end
.\tune.ps1 -Gpu Global                   # [19] GPU pin (Global | PerDevice | Lift | Revert)
```

Works in Windows PowerShell 5.1 and PowerShell 7.

---

## What gets tuned

Item numbers match `TUNING-LOG.txt`, which has the full reasoning for each one.

### Core fixes `[1]`–`[9]`

| # | Setting | Why |
|---|---|---|
| 1 | Pagefile fixed at 4096 / 16384 MB | The 1 GB system-managed pagefile let WSL2 (12 GB + swap) exhaust the commit limit. Needs a reboot to resize. |
| 2 | Max processor state on AC = 100% | It was 99%, the legacy trick for capping Turbo. Battery value untouched. |
| 3 | Fast Startup off | The flag was on even though hibernation was off. Shutdown stays a real cold boot. |
| 4 | **IP Helper** Automatic + Running | **Root cause of Tailscale failing at every boot.** A debloat had disabled it. |
| 5 | Shadow Copy storage 10 GB + System Restore | Restore points were being aborted (Volsnap Event 36). |
| 6 | Intel Computing Improvement Program removed | Telemetry using about 345 MB (`esrv`). An Intel driver bundle can bring it back. |
| 7 | Defender exclusions for `.gradle`, `.android` | Real-time scanning of build caches slows builds. Source folders are still scanned. |
| 8 | Cross-Device Resume off *(per user)* | Background process and popups. The most likely item to be reset by a feature update. |
| 9 | Wi-Fi MIMO power save = No SMPS | Removes latency spikes on the first packets after idle. |

### Baseline `[4.x]`

Tuning that predates this project, kept enforced:

- **4.1** NTFS: no 8.3 short names, `MemoryUsage=2`, TRIM on
- **4.2** Visual effects = best performance *(per user)*
- **4.3** Wireless adapter power on AC = Maximum Performance
- **4.5** About 39 unused services disabled: Xbox, printing, telemetry, phone
  link, vendor updaters, and similar. Services that aren't installed are
  skipped, not counted as drift. Side effects are listed in log Section 4.6.

### Advanced `[10]`–`[18]`

| # | Setting | Why |
|---|---|---|
| 10 | Wi-Fi: prefer 5 GHz | About 3–4× the throughput of 2.4 GHz. "Prefer", not "force", so 2.4 GHz still works as a fallback. |
| 11 | NTFS last-access timestamps off | Every file read was causing a write. Less wear on the DRAM-less SSD. |
| 12 | PCIe ASPM off, **AC only** | Lower wake latency for the NVMe drive. |
| 13 | Disk idle timeout never, **AC only** | A spin-down timer only adds stalls on NVMe. |
| 14 | Hidden Intel Wi-Fi power savers off | `SkipOverDtimEnable` and `LprxEnable` let the radio sleep through beacons. |
| 15 | **Memory Compression on**, SysMain on with its prefetch/prelaunch off | See [Known gotchas](#known-gotchas). |
| 16 | Telemetry scheduled tasks off | CEIP, Compatibility Appraiser and DmClient kept running even with DiagTrack disabled. |
| 17 | Realtek Ethernet EEE / Green / Power Saving off | Caused link drops when docked. |
| 18 | WSL2 `autoMemoryReclaim=gradual`, `sparseVhd=true` | **Check only.** Edit `%USERPROFILE%\.wslconfig` by hand, then `wsl --shutdown`. |

### GPU driver pin `[19]`

Stops Windows Update from replacing the Intel graphics driver you installed
from intel.com with an older one. It covers both GPUs, Iris Xe (`DEV_46A6`) and
Arc A370M (`DEV_5693`).

| Mode | Effect |
|---|---|
| `Global` | Windows Update stops delivering **any** driver. Your manual installs still work. |
| `PerDevice` | `Global` plus an install-level block on both GPU IDs. Needed on Windows **Home**, where Windows Update got around the global setting. **This also blocks your own installs.** |
| `Lift` | Removes only the per-device block, so you can install a new driver manually. |
| `Revert` | Undoes everything `[19]` set. |

Order matters: **install the driver you want and reboot first, then pin it.**
To update later: `-Gpu Lift`, then install and reboot, then `-Gpu PerDevice`.

### Idle-RAM debloat `[20]`–`[27]`

Small trims found during an idle-RAM audit. Expect roughly 100–300 MB less
memory use and fewer background wakeups.

| # | Setting | Notes |
|---|---|---|
| 20 | `MapsBroker` disabled | Offline-maps updater. |
| 21 | `SharedAccess` (ICS) → **Manual** | Not Disabled: WSL / Hyper-V NAT starts it on demand, so seeing it *Running* is expected. |
| 22 | Leftover scheduled tasks off | Maps, Xbox, Error Reporting, PCA, Family Safety. Their services are already off. |
| 23 | Edge Startup Boost + background mode off (policy) | Edge will show "managed by your organization". That's harmless. |
| 24 | Delivery Optimization P2P off | HTTP-only downloads. The service itself stays. |
| 25 | Game DVR background capture off (policy) | |
| 26 | Taskbar search box hidden *(per user)* | SearchHost was using about 430 MB. Search from Start still works. |
| 27 | **Nahimic audio kept ON** (exception) | Nahimic's sound effects are what make this laptop's speakers sound good. Its service and session tasks are kept on, so no debloat run can switch them off. |

---

## Deliberately left alone

These came up and were rejected on purpose, so they don't get re-investigated:

- **Defender, Themes, FontCache, push notifications, Update Orchestrator,
  the Delivery Optimization service.** Turning these off breaks security,
  visuals, notifications or updates.
- **MSI Center / MSI Foundation Service.** They provide fan control, which
  matters on a thin chassis.
- **Nahimic.** Its audio effects make the speakers sound good. `[27]` keeps it
  switched on.
- **Packaged (Microsoft Store) app services.** Their startup type can't be
  changed, even by an admin. They can only be stopped.
- **The High Performance power plan.** On a laptop, Balanced already boosts
  when needed.

---

## Known gotchas

**SysMain vs Memory Compression `[15]`:** Memory Compression runs inside the
SysMain service. Turning compression on switches SysMain back on, and
disabling SysMain silently turns compression off. When both "SysMain disabled"
and "compression on" were in this tuning, each run undid the other. The fix:
keep SysMain **on**, but switch off its app prelaunch and prefetch, the part
people usually disable it for. Compression keeps a 16 GB machine running WSL
off the pagefile.

**HKCU items** (`[8]`, `4.2`, `[26]`) are per-user settings. They apply to the
account that runs the script. If you elevate with a *different* admin account,
apply them from your own account.

**Wi-Fi items** (`[9]`, `[10]`, `[14]`) take effect after an adapter restart.
Apply does that once at the end (about a 5-second drop) unless you pass
`-SkipWiFiRestart`.

---

## When to re-run

Run `.\tune.ps1 verify` after any of these:

| Event | Usually resets |
|---|---|
| Windows feature update | `[8]` Cross-Device Resume, sometimes `[7]` |
| Wi-Fi driver update / network reset | `[9]`, `[10]`, `[14]` |
| Running a debloat script | `[4]` IP Helper, which breaks Tailscale |
| Intel driver bundle install | `[6]` comes back |
| Power plan reset | `[2]`, `[12]`, `[13]` |

---

## Reverting

Revert commands for each item are in `TUNING-LOG.txt` (`REVERT` lines), and in
the comment above each item in `tune.ps1`. The GPU pin has a built-in revert:
`.\tune.ps1 -Gpu Revert`.

---

## Caveats

- **Machine-specific values are built in:** pagefile size for 16 GB of RAM,
  Intel/MSI service names, a Wi-Fi adapter named exactly `WiFi`, the Intel GPU
  hardware IDs, and Realtek Ethernet property names.
- **The baseline service list is opinionated.** It disables **`Spooler`**
  (printing), **`WSearch`** (Windows Search) and **`ssh-agent`**, which other
  machines usually need. If you use SSH keys through the agent, run
  `Set-Service ssh-agent -StartupType Manual` afterwards.
- `TUNING-LOG.txt` has been sanitized for public hosting: no personal paths,
  SSIDs, hostnames or device instance IDs.
