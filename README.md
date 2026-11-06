# MSI GF63 Tuner (Thin 12HW / MS-16R7)

A high-performance, lightweight fan control, hardware tuning, and sensor monitoring suite built specifically for the **MSI Thin GF63 12HW** (MS-16R7) series, completely replacing MSI Center and MSI Afterburner.

**~950 MB MSI Center bloat → ~2.4 MB All-in-One Standalone Single Binary. 100% Driverless. Zero Kernel Drivers.**

---

## ⚡ Key Highlights

* **Single Standalone Executable (`msi_gf63_tuner.exe`):** No loose companion DLLs, no extra EXEs, and no separate Windows service installation needed. All core assemblies and the embedded fan control engine are packed into a single portable binary.
* **100% Driverless ACPI Control (WMI2):** Communicates with the embedded controller directly through MSI's native `MSI_ACPI` WMI interface (`root\WMI`). Eliminates vulnerable kernel drivers like `WinRing0.sys` or `RTCore64.sys` so Windows Defender will never flag or block it.
* **Cyberpunk Sci-Fi Tactical HUD UI:** High-tech dark aesthetic featuring chamfered sci-fi tabs, live laser telemetry pods with HUD bracket corners `[  ]`, high-voltage reactor sliders, and Arasaka Neon Red (`#FF003C`) & Electric Cyber Cyan (`#00F0FF`) accents.
* **Unified Single-Fan Architecture & 5-Profile Performance Matrix:** Specifically designed for the single-fan cooling loop of the GF63. Eliminates confusing dual-fan CPU/GPU toggles and phantom sensors while transparently writing synchronized curves to both EC thermal registers. Features five tailored profiles:
  - **Balanced:** Daily optimal acoustic and thermal balance.
  - **Performance:** Aggressive ramp-up to 100% fan speed for intensive workloads and gaming.
  - **Sustained 35W:** Custom engineered curve for PL1=35W / PL2=60W sustained thin-chassis thermals.
  - **Silent:** Ultra-quiet fan curve tailored for office, library, and media playback.
  - **Super Battery:** Minimal-power cooling curve with battery conservation.
* **Real-Time Continuous Telemetry:** Instant live CPU/GPU temperatures and fan RPM polling displayed immediately on startup and continuously across all tabs.
* **MSI Center-Style OSD Toasts:** Sleek bottom-center floating on-screen display (OSD) notifications when switching profiles via keyboard shortcuts or system tray, without stealing focus or interrupting games.
* **Hardware Module Controls:** Built-in battery charge thresholds (60% Desk Mode, 80% Balanced, 100% Full Capacity), hardware-level Win/Fn key swap, Cooler Boost (100% fan override), and keyboard backlight controls.
* **System Tray & Taskbar Integration:** Custom red-and-black cyber dragon icon embedded across window titlebars, taskbars, and the system tray with minimize-to-tray support.

---

## 🎯 Target Hardware & Verified Specifications

All curves, register addresses, and telemetry sensors were measured directly on real hardware:

| Component | Specification |
|---|---|
| **Laptop Model** | MSI Thin GF63 12HW-004IN / MS-16R7 |
| **BIOS Version** | E16R7IMS.10F |
| **EC Firmware** | `16R7IMS1.104` |
| **CPU** | Intel Core i5-12500H (12 Cores, 16 Threads) |
| **Integrated GPU** | Intel Iris Xe Graphics |
| **Discrete GPU** | Intel Arc A370M |
| **Fan Configuration** | Single high-speed blower fan shared across CPU & GPU |
| **EC Interface** | Driverless WMI2 (`root\WMI` -> `MSI_ACPI`, Version 2.8) |
| **Driver Dependency** | **None** (No WinRing0, No RTCore64, No Kernel Drivers) |

---

## 📊 Feature Overview

| Feature | Details |
|---|---|
| **Fan Curve Matrix** | 7-point custom thermal curve with programmable up/down hysteresis thresholds |
| **Cooler Boost** | Instant 100% fan blast toggle |
| **Battery Health Limiter** | Hardware EC charge capping at 60%, 80%, or 100% to preserve battery lifespan |
| **Key Mapping** | Hardware-level Win ↔ Fn key swap to prevent accidental key presses during gameplay |
| **Telemetry HUD** | Live CPU temp & RPM, GPU temp, clocks, power draw, load, and memory usage |
| **Desktop Overlay** | Click-through, borderless, always-on-top transparent telemetry HUD |
| **In-Game OSD** | Optional RivaTuner Statistics Server (RTSS) integration for full-screen games |
| **Profile OSD Toast** | Non-intrusive bottom-center toast notification for profile changes and Cooler Boost |

---

## 🚀 Quick Start & Installation

### Option 1: 1-Click Deployment (Recommended)
1. Download the latest release from the [Releases](../../releases) section.
2. Extract the package and double-click `deploy.cmd` (or run `powershell -ExecutionPolicy Bypass -File .\deploy.ps1` as Administrator).
3. The script automatically:
   - Terminates any existing tuner instances
   - Installs the standalone `msi_gf63_tuner.exe` single binary into `C:\Program Files\kmvishn\msi_gf63_tuner\`
   - Configures driverless WMI2 mode in `ProgramData`
   - Creates a Desktop shortcut with the custom cyber dragon icon
   - Flushes the Windows Explorer icon cache and launches the tuner

### Option 2: Standalone Portable Mode
Simply place `msi_gf63_tuner.exe` anywhere and run it as Administrator!

> **Note:** Do not run MSI Center simultaneously with MSI GF63 Tuner, as both programs interact with the same ACPI EC registers.

---

## 🛠️ Building from Source

This project builds cleanly with the standard .NET 8.0 / 9.0 SDK without requiring Visual Studio:

```powershell
# Clone the repository
git clone https://github.com/kmvishn/msi_gf63_tuner.git
cd msi_gf63_tuner

# Build Release binary (automatically ILRepacks into single executable)
dotnet build -c Release YAMDCC.sln

# Deploy to local system
.\deploy.cmd
```

The compiled standalone executable will be located at:
`YAMDCC.ConfigEditor\bin\Release\net48\msi_gf63_tuner.exe`

---

## 📜 Credits & License

* Originally based on [YAMDCC](https://codeberg.org/Sparronator9999/YAMDCC) by **Sparronator9999**.
* Single-binary architecture, WMI2 driverless enforcement, live telemetry polling, 5-profile performance matrix, Cyberpunk Sci-Fi UI redesign, OSD toast notifications, and MSI Thin GF63 (MS-16R7) retargeting by **kmvishn** ([jztchl](https://github.com/jztchl)).
* Licensed under **GPL-3.0-or-later**. See [LICENSE.md](LICENSE.md) for full license details.
