# YAMDCC build notes — MSI Thin GF63 12HW (MS-16R7)

Local fork notes for building YAMDCC v2.0.0-dev on this machine and making it
work with this laptop. Pristine upstream source is kept at
`../yamdcc.ORIGINAL-BACKUP/` (this tree is not a git checkout, so that copy is
the only undo).

Hardware this was done on:

| | |
|---|---|
| Model (SMBIOS `SystemProductName`) | `Thin GF63 12HW` |
| Board | MS-16R7 |
| BIOS | E16R7IMS.10F (2025-09-02) |
| EC firmware | `16R7IMS1.104`, 2023-06-13 09:17:06 |
| MSI WMI version (`Get_WMI`) | **2.8** → WMI2 backend supported |
| Fans | **one** (`Get_Fan(0)` reports fan[0] only; fan[1..3] read 0) |

## How to build

No Visual Studio required, and no machine-wide install. The .NET SDK lives in
a user-local folder; delete that folder to undo it completely.

```bash
# one-time: SDK 9 into a user-local dir (no elevation, no PATH change)
# powershell: ./dotnet-install.ps1 -Channel 9.0 -InstallDir C:\Users\vishnu\dotnet-sdk -NoPath

cd /c/Users/vishnu/Documents/YAMDCC-main/yamdcc
/c/Users/vishnu/dotnet-sdk/dotnet.exe restore YAMDCC.sln --force-evaluate
/c/Users/vishnu/dotnet-sdk/dotnet.exe build YAMDCC.sln -c Release -p:Platform="Any CPU"
```

Output lands in `YAMDCC.ConfigEditor/bin/Release/net48/` (~1.9 MB) plus each
project's own `bin/Release/net48/`.

`--force-evaluate` is needed on restore because `packages.lock.json` files are
committed and we added package references below.

> **Gotcha:** pass `-p:Platform="Any CPU"` only when building `YAMDCC.sln`.
> On an individual `.csproj` it redirects output to `bin\Any CPU\Release\net48\`
> instead of `bin\Release\net48\`. The build reports success and you carry on
> testing a stale binary from the old path. To rebuild one project, just
> `dotnet build YAMDCC.CLI\YAMDCC.CLI.csproj -c Release` with no `Platform`.

### Why `Directory.build.props` was changed

Upstream expects Visual Studio's MSBuild. Building with the plain .NET SDK
needs two additions:

1. **`Microsoft.NETFramework.ReferenceAssemblies`** — supplies the net48
   reference assemblies, so no .NET Framework targeting pack is needed.
   `PrivateAssets="all"`; build-time only.
2. **`GenerateResourceUsePreserializedResources` + `System.Resources.Extensions`**
   — the `.resx` files hold non-string resources (icons). VS MSBuild serialises
   those with `BinaryFormatter`; `dotnet build` cannot, and fails with
   `MSB3823`/`MSB3822`. This is the supported replacement.
   `System.Resources.Extensions` ships with the app (it is the runtime reader),
   so it is deliberately *not* `PrivateAssets`.

## Source fixes

All four are in the **WMI2 backend**, which is the path this laptop uses.
None of them are machine-specific — they are upstream defects on the v2 dev
branch that happen to block an unsupported board.

### 1. `Wmi2FanController.GetFanProf()` returned `null` unconditionally

`YAMDCC.Service/FanControllers/Wmi2FanController.cs`

It built the `FanProf`, then fell through to `return null`. The caller,
`FanControlService.GetDefaultFanProf()`, immediately did `prof.Name = "Default"`
→ `NullReferenceException` → swallowed by a bare `catch` → reported as a
generic `ECtoConfState.Fail`.

Net effect: **EC-to-config never worked on the WMI2 backend** — the one feature
needed to support a laptop with no shipped config. Added the missing
`return prof;`.

### 2. `GetEcFirmwareInfo()` never stored the version string

Same file. `ecVer` was decoded from the response and then dropped, leaving
`EcInfo.Version` null. `ECtoConf()` writes `Config.FirmVer = EcInfo.Version`,
so every generated config got an empty `<FirmVer>`. Now assigned (and trimmed
of NUL/space padding).

### 3. `GetChargeLimit()` read the wrong byte

Same file. `IsChargeLimitSupported()` and `SetChargeLimit()` both use
`result[5]`; `GetChargeLimit()` read `result[4]`, which is the **keyboard
backlight brightness**. On this machine `result[4] = 0x81`, `result[5] = 0x80`,
so it reported a charge limit of 1 % instead of 0 (no limit).

Currently latent — nothing outside the controller calls `GetChargeLimit()` on
this branch — but wrong, and dangerous if wired up to the UI.

### 4. GPU fan profile was read from the CPU's profile list

`YAMDCC.Service/FanControlService.cs`, `ApplyConfig()`:

```csharp
FC.SetFanProf(Config.CpuFan.FanProfs[Config.GpuFan.ProfSel], true, ...);
//            ^^^^^^ CpuFan, indexed by GpuFan.ProfSel
```

Applied the CPU curve to the GPU fan, and threw `IndexOutOfRangeException`
whenever the GPU had more profiles than the CPU. Now indexes `GpuFan`.

### Also hardened (not a bug fix)

`GetDefaultFanProf()` now returns `null` with a specific log line instead of
dereferencing a null profile, and `ECtoConf()` refuses to save a config with a
missing default profile — previously that would have written an empty fan curve
for the service to apply on next start.

### 5. CLI crashed whenever its output was redirected

`YAMDCC.CLI/Program.cs`

`Console.BufferWidth` (used to decide whether the ASCII logo fits) throws
`IOException: The handle is invalid` when stdout is redirected, killing the
program before it did anything:

```
Unhandled Exception: System.IO.IOException: The handle is invalid.
   at System.Console.GetBufferInfo(...)
   at YAMDCC.CLI.Program.Main(String[] args)
```

So `yamdcc.exe -info > out.txt` — or any pipe — always failed. It only worked
if `-nologo` happened to be passed, since that skips the branch entirely. Now
guarded with `!Console.IsOutputRedirected`.

### Not fixed — `Ring0FanController.GetFanProf()`

It does `new List<Threshold>(n)`, which sets *capacity*, not count, so
`for (j = 0; j < prof.Thresholds.Count; j++)` never executes and it returns a
profile with zero thresholds. Left alone: this machine uses the WMI2 backend,
and changing an untested code path we never exercise adds risk for no gain.

## Config

`Configs-V2/MSI-Thin-GF63-12HW.xml`

The `Default` profiles were **read back from this laptop's own EC**, not copied
from another model. They decode byte-identically to the repo's
`MSI-GF63-Thin-11SC.xml` — MSI kept the same thermal table across 16R6 and
16R7 — but they were measured, not assumed. `OffsetDT` is `true`: `Get_Thermal`
returns down-threshold *offsets*, so `Tdown = Tup - offset`.

The file records the raw `Get_Temperature` / `Get_Fan` / `Get_Thermal` bytes in
a comment so the decode can be re-checked.

`ProfSel` is `0` (Default), and `PerfMode` / `ChargeLim` / `KeySwapEnabled`
all match the machine's current live state — so applying this config for the
first time reproduces existing behaviour rather than changing it.

A second profile, `Sustained 35W`, is included but **not selected**. It keeps
idle at the stock 38 %, ramps harder from 65 °C, and reaches 100 % instead of
the factory 85 % cap — intended for PL1=35 W / PL2=60 W on a single-fan thin
chassis.

## Before running any of this

`FanControlService.OnStart()` refuses to start while MSI Center is running.
On this machine all three blocked services are currently **Running**:

```
Micro Star SCM           C:\WINDOWS\SysWOW64\MSIService.exe
MSI Foundation Service   ...\MSI NBFoundation Service\MSIAPService.exe
MSI_Center_Service       ...\MSI Center\MSI_Central_Service.exe
```

It is strictly either/or. Reverting is re-enabling those three services.

MSI Center drives the **same** interface — `MSIWMIACPI2.dll` contains
`MSI_ACPI`, `PNP0C14`, `Set_Fan`, `Set_Temperature`, `Set_Thermal`, `Set_AP` —
so YAMDCC is not poking unknown registers; it reimplements MSI's own ACPI path.

## Known issue carried from upstream

`MessagePack 3.1.4` (used by `YAMDCC.IPC`) has known advisories, including a
high-severity one. It is only used for the local named pipe between the service
and the UI, and the pipe's SDDL restricts access to SYSTEM and Administrators —
but it is worth bumping if this is kept long-term.

## Current installed state (as of the first successful run)

| | |
|---|---|
| Binaries | `C:\Program Files\YAMDCC` — 3.1 MB |
| Config | `C:\ProgramData\Sparronator9999\YAMDCC\CurrentConfigV2.xml` |
| Global config | same dir, `GlobalConfig.xml` — **`UseWMI2=true`** |
| Logs | same dir, `Logs\yamdccsvc.exe.log` |
| Service | `yamdccsvc`, LocalSystem, **`start= demand` (Manual)** |
| WinRing0 | **removed** — no `.sys` on disk, no driver service |
| EC fan mode | `Advanced` (0x8D), running the stock table |
| MSI Center | all 3 services **Stopped**, StartType still **Automatic** |

`UseWMI2=true` is load-bearing. If `GlobalConfig.xml` is lost or reset, the
service falls back to the WinRing0 backend, fails to find the driver we
deleted, and will not start.

### What a reboot does right now

The service is `Manual` and MSI Center is `Automatic`, so **a reboot returns
the machine to MSI Center** and YAMDCC does not start. That is deliberate —
it is the safe resting state while testing.

To make the swap permanent:

```powershell
Set-Service yamdccsvc -StartupType Automatic
Set-Service 'Micro Star SCM' -StartupType Disabled
Set-Service 'MSI Foundation Service' -StartupType Disabled
Set-Service MSI_Center_Service -StartupType Disabled
```

To go back, reverse it (MSI Center services to `Automatic`, `yamdccsvc` to
`Manual`/`Disabled`), or run the full undo which also restores the EC fan
tables and sets fan mode back to `Auto`.

### Verified working on this machine

- `Get_WMI` → 2.8; service selected the WMI2 backend, no driver loaded
- EC firmware read back as `16R7IMS1.104` (fix #2)
- `GetFanProf` returned real profiles (fix #1)
- Writing the stock table back round-tripped **byte-identical**
- `Advanced` fan mode held 90 s: 2516–2570 RPM, duty pinned at 38 %,
  CPU 47–48 °C, GPU 51 °C
- `ApplyConfig()` on service start left every fan register unchanged
- CLI over the named pipe reports the right model, firmware and both profiles

One asymmetry worth knowing: `SetPerfMode` writes byte[3] of the `Get_AP(1)`
packet, while `GetPerfMode` reads byte[3] of `Get_AP(0)`. Starting the service
changed `Get_AP(1)[3]` from `0x03` to `0xC4`, but the read channel still
reports `0xC4` (Performance) exactly as before — the effective mode did not
change. That write channel / read channel split is how MSI's interface works.

## Dark "MSI Dragon" UI theme

Windows Forms has no dark mode, so `YAMDCC.Common/UI/` adds one. `Theme.Apply(form)`
is called once after `InitializeComponent()` in all seven forms.

| File | Purpose |
|---|---|
| `Theme.cs` | Palette, recursive restyler, dark title bar, tooltip theming |
| `DarkToolStripRenderer.cs` | Menus (colour table + text/arrow/border/separator) |
| `DarkTrackBar.cs` | Fully owner-drawn slider |
| `DarkTabControl.cs` | Owner-drawn tab strip |
| `DarkComboBox.cs` | Dark drop-down button and list rows |

Palette: background `#141414`, surface `#1E1E1E`, fields `#262626`, border
`#3A3A3A`, text `#EDEDED`, accent **MSI red `#E4002B`**.

### Why four custom controls were needed

Setting `BackColor` is not enough for controls that are wrappers over Win32
common controls — they paint from the system theme and ignore it:

- **TrackBar** ignores `BackColor` outright and stayed a bright white box.
  `DarkTrackBar` reimplements the API YAMDCC uses (`Minimum`/`Maximum`/`Value`/
  `Orientation`/`TickFrequency`/`TickStyle`/`LargeChange`, `ValueChanged`,
  `Scroll`) and paints the track, ticks and thumb itself. It implements
  `ISupportInitialize` as no-ops so generated designer code still compiles.
- **TabControl** owner-draw only covers the tabs; the strip behind them and the
  raised tab borders are system-drawn. `DarkTabControl` takes over painting
  entirely via `ControlStyles.UserPaint`.
- **ComboBox** keeps a light drop-down button even with `FlatStyle.Flat`.
  `DarkComboBox` repaints that corner after `WM_PAINT`.
- **ToolTip** is a component, not a control, so the recursive walk can't reach
  it, and it ignores its own colours unless `OwnerDraw` is set.

### Traps hit while building it (all fixed)

- **`Color.Transparent` crashes the app.** Only controls with
  `ControlStyles.SupportsTransparentBackColor` accept it; the rest throw
  `ArgumentException`. That flag is protected, so `SetTransparentBack()` tries
  it and falls back to a solid colour. One unsupported control anywhere in the
  tree took the whole program down.
- **`LinkLabel` derives from `Label`,** so its `case` has to come first or the
  `Label` case swallows it (`CS8120`).
- **Disabled `NumericUpDown`/`TextBox` paint light.** `UpDownBase` resets its
  inner edit box to system colours on every `EnabledChanged`, and
  `BorderStyle.FixedSingle` draws in `#ABADB3`. Fixed by re-asserting colours
  on `EnabledChanged` and using `BorderStyle.None`.
- **Controls built after `Theme.Apply()` are missed.** The fan-curve
  `NumericUpDown`s and `Label`s are created later in code, not by the designer,
  so they stayed white until `FanCurveNUD()`/`FanCurveLabel()` themed them at
  creation.
- **An empty `ComboBox` drew white.** With `OwnerDrawFixed`, `e.Index < 0` fell
  through to `base.OnDrawItem`, leaving the system background. The handler now
  always fills first.
- **`ComboBox` paints a 1px white edge one pixel inside its bounds,** so the
  border has to be drawn at inset 0 *and* inset 1.

### Not verified

The **Default** fan profile is deliberately read-only
(`bool enable = curveCfg.Name != "Default"`), so its controls are correctly
greyed out — that is not a theming bug. Screenshots were taken with the
editable profile selected.

Tab switching was confirmed with Ctrl+Tab. Clicking tabs with the **mouse** was
not confirmed: synthetic clicks from the test harness did not register on a
plain `Button` either, so the harness is at fault rather than `DarkTabControl` —
but it is worth a click to be sure, since `ControlStyles.UserPaint` is involved.

## Installer

```bash
cp LICENSE.md YAMDCC.ConfigEditor/bin/Release/net48/
Tools/InnoSetup/ISCC.exe /O+ /DBuildConfig=Release Installer/YAMDCC-Setup.iss
```

Produces `Output/YAMDCC-v2.0.0-dev-Release-setup.exe` (~2.7 MB). Two changes to
`Installer/YAMDCC-Setup.iss`:

1. `AppVer`/`AppVerFriendly` were left at **1.2.1** while the assemblies moved
   to 2.0.0-dev, so the installer registered itself in Add/Remove Programs as
   v1.2.1 while installing v2.0.0-dev binaries.
2. **EC Inspector removed from the `full` install type.** It is the only
   component that ships `WinRing0.sys` (the driver Defender flags) and it
   cannot work on a WMI2 machine at all — `Wmi2FanController.ReadECByte`/
   `WriteECByte` throw `NotSupportedException`. Still selectable under a Custom
   install for WinRing0-backend laptops.

The installer runs `InstallUtil.exe` against `yamdccsvc.exe`, which installs the
service as **Automatic** (unlike the manual `sc create` used during testing) and
starts it. Delete the hand-made `yamdccsvc` service first, or `InstallUtil` will
fail because the service already exists.
