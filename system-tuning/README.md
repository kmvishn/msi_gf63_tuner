# Windows System Tuning — MSI GF63 (i5-12500H / Iris Xe + Arc A370M)

PowerShell scripts that **apply, verify, and document** a set of Windows 11
performance/debloat tweaks for an MSI Thin GF63 12HW. This is a personal,
machine-specific profile — read the caveats before running it on anything else.

## Files

| File | What it does | Needs admin |
|---|---|---|
| [`verify-tuning.ps1`](verify-tuning.ps1) | **Read-only.** Checks every tuned item and prints `OK` / `DRIFTED`. Changes nothing. | No (elevate for full detail — Defender exclusions + shadow storage) |
| [`reapply-tuning.ps1`](reapply-tuning.ps1) | **Idempotent.** Re-applies anything that got reset. Skips what's already correct. | Yes |
| [`advanced-tuning.ps1`](advanced-tuning.ps1) | **Idempotent.** Tier-1 "next level" items [10]–[17]: Wi-Fi 5 GHz, NTFS last-access off, PCIe ASPM / disk-idle off (AC only), memory compression on, telemetry tasks off, Realtek power-save off. | Yes |
| [`pin-gpu-drivers.ps1`](pin-gpu-drivers.ps1) | **Idempotent.** Item [19]: stop Windows Update downgrading the Intel GPU drivers (global exclude by default; `-IncludePerDevice` for a per-device hard-block; `-Revert`). | Yes |
| [`TUNING-LOG.txt`](TUNING-LOG.txt) | Full rationale, before/after, verify, and revert commands for every item. The source of truth. | — |

## Quick use

```powershell
# See what has drifted (safe, read-only)
powershell -ExecutionPolicy Bypass -File verify-tuning.ps1

# Fix drift (run elevated). Add -IncludeBaseline to also restore the disabled-service set.
powershell -ExecutionPolicy Bypass -File reapply-tuning.ps1 -IncludeBaseline

# Preview changes without applying them
powershell -ExecutionPolicy Bypass -File reapply-tuning.ps1 -WhatIf
```

If you hit *"running scripts is disabled on this system"*, that's the execution
policy — either use the `-ExecutionPolicy Bypass` flag above, or set it once for
your user: `Set-ExecutionPolicy -Scope CurrentUser RemoteSigned`.

## What it tunes (summary — see `TUNING-LOG.txt` for the why)

- **[1]** Pagefile fixed at 4096/16384 MB (commit-limit exhaustion with WSL2)
- **[2]** Max processor state → 100% on AC (removes a 99% frequency ceiling)
- **[3]** Fast Startup (hiberboot) disabled
- **[4]** IP Helper re-enabled — **root cause of Tailscale boot failures**
- **[5]** Shadow Copy storage → 10 GB, System Restore enabled
- **[6]** Intel Computing Improvement Program uninstalled (telemetry)
- **[7]** Defender exclusions for build caches (`.gradle`, `.android`)
- **[8]** Cross-Device Resume off (least durable — re-check after feature updates)
- **[9]** Wi-Fi MIMO Power Save → No SMPS (latency on first packets after idle)
- Plus a baseline: NTFS tweaks, visual-effects = performance, and ~40 disabled
  services. The service check is **optional-aware** — a service that isn't
  installed is skipped, not counted as drift.

### Advanced tuning — Section 7 (added 2026-10-06)

Balance of **efficiency + longevity + performance** (latency items are AC-only,
so battery efficiency is untouched). See `TUNING-LOG.txt` Section 7 for the why.

- **[10]** Wi-Fi → Prefer 5 GHz (keeps 2.4 GHz fallback — "prefer", not "force")
- **[11]** NTFS Last-Access **off** — less write-amplification on the DRAM-less SSD
- **[12]/[13]** PCIe ASPM off + disk-idle → never, **AC only**
- **[14]** Hidden Wi-Fi latency savers (`SkipOverDtimEnable`, `LprxEnable`) off
- **[15]** **Memory Compression on** (was disabled by a debloat — hurts a 16 GB host)
- **[16]** Telemetry scheduled tasks disabled (CEIP / Appraiser / DmClient)
- **[17]** Realtek Ethernet EEE / Green / Power-Saving off
- **[18]** WSL2 `autoMemoryReclaim=gradual` + `sparseVhd=true`
- **[19]** **Pin GPU drivers vs Windows Update** — stop WU re-installing an older
  WHQL Intel driver over a manually-installed one. On **Home** (no `gpedit`) this
  is registry-only: `ExcludeWUDriversInQualityUpdate=1` +
  `SearchOrderConfig=0`, with an optional per-device `DenyDeviceIDs` hard-block.

## ⚠️ Caveats — this is NOT a universal optimizer

- **Machine-specific values** are baked in: pagefile size (16 GB RAM), Intel/MSI
  service names, a Wi-Fi adapter literally named `WiFi`, Arc A370M, etc.
- **`reapply -IncludeBaseline` is opinionated** and disables services that are
  often wanted elsewhere — including **`Spooler`** (printing), **`WSearch`**
  (Windows Search), **`SysMain`**, and **`ssh-agent`**. Don't run it blind on
  another PC.
- `ssh-agent` is in the disable list. If you use SSH keys via the agent:
  `Set-Service ssh-agent -StartupType Manual`.
- `TUNING-LOG.txt` here has been **sanitized** of personal paths, Wi-Fi SSID,
  project names, and device IDs for public hosting.

Run `verify-tuning.ps1` after any driver update, Windows feature update, or
debloat run — those are the events most likely to undo these settings.
