// This file is part of YAMDCC (Yet Another MSI Dragon Center Clone).
// Copyright © Sparronator9999 and Contributors 2023-2025.
//
// YAMDCC is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option)
// any later version.
//
// YAMDCC is distributed in the hope that it will be useful, but
// WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY
// or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License for
// more details.
//
// You should have received a copy of the GNU General Public License along with
// YAMDCC. If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Management;
using Microsoft.Win32;

namespace YAMDCC.Common.Monitoring;

/// <summary>
/// One GPU's live readings.
/// </summary>
public sealed class GpuReading
{
    /// <summary>Adapter name, e.g. "Intel(R) Arc(TM) A370M Graphics".</summary>
    public string Name { get; set; }

    /// <summary>Perf-counter LUID key, e.g. <c>luid_0x00000000_0x0000F5DF</c>.</summary>
    public string Luid { get; set; }

    /// <summary>PCI device ID, used to match Level Zero's device list.</summary>
    public uint DeviceId { get; set; }

    /// <summary>Memory clock in MHz, where the adapter has its own memory.</summary>
    public double? MemoryMHz { get; set; }

    /// <summary>Die temperature in Celsius, when the GPU reports one.</summary>
    public double? TempC { get; set; }

    /// <summary>Core voltage in volts, when the GPU reports one.</summary>
    public double? Volts { get; set; }

    /// <summary><see langword="true"/> for a discrete GPU.</summary>
    public bool Discrete { get; set; }

    /// <summary>Busy percentage, summed across the adapter's engines.</summary>
    public double LoadPercent { get; set; }

    /// <summary>Video memory in use, in MB.</summary>
    public double VramUsedMB { get; set; }

    /// <summary>Video memory the adapter reports as dedicated, in MB.</summary>
    public double VramTotalMB { get; set; }

    /// <summary>
    /// Package power in watts, or <see langword="null"/> when unavailable.
    /// </summary>
    /// <remarks>
    /// Without help, only the integrated GPU reports power, via the CPU
    /// package's RAPL PP1 domain. When MSI Afterburner is running its reading
    /// is used instead, which also covers the discrete GPU.
    /// </remarks>
    public double? Watts { get; set; }

    /// <summary>
    /// Core clock in MHz, or <see langword="null"/>. Windows exposes no GPU
    /// clock counter, so this is only available via MSI Afterburner.
    /// </summary>
    public double? CoreMHz { get; set; }

    /// <summary>
    /// <see langword="true"/> when this GPU has no power figure of its own
    /// because it shares the CPU package's power budget.
    /// </summary>
    public bool PowerOnCpuPackage { get; set; }
}

/// <summary>
/// A single set of system readings.
/// </summary>
public sealed class SensorSnapshot
{
    public double? CpuPackageWatts { get; set; }
    public double? CpuCoresWatts { get; set; }
    public double CpuLoadPercent { get; set; }
    public double CpuMHz { get; set; }
    public double RamUsedMB { get; set; }
    public double RamTotalMB { get; set; }
    public double? BatteryWatts { get; set; }

    /// <summary>CPU package temperature, from Afterburner when available.</summary>
    public double? CpuTempC { get; set; }

    /// <summary>Frames per second, when RivaTuner Statistics Server is running.</summary>
    public double? Fps { get; set; }

    /// <summary>
    /// <see langword="true"/> when the Intel Control Library supplied GPU
    /// power telemetry. Needs no third-party tool.
    /// </summary>
    public bool IgclActive { get; set; }

    /// <summary>
    /// <see langword="true"/> when Intel Level Zero supplied GPU telemetry.
    /// This needs no third-party tool.
    /// </summary>
    public bool LevelZeroActive { get; set; }

    /// <summary>
    /// <see langword="true"/> when MSI Afterburner's shared memory was read,
    /// meaning the richer sensors (GPU clock/power, CPU package temperature)
    /// are populated.
    /// </summary>
    public bool AfterburnerActive { get; set; }

    public List<GpuReading> Gpus { get; set; } = [];
}

/// <summary>
/// Reads CPU power, clocks, memory and per-GPU load without a kernel driver.
/// </summary>
/// <remarks>
/// <para>
/// Power comes from the Windows "Energy Meter" performance counters, which
/// surface the CPU's RAPL energy domains through the in-box Energy Meter
/// Interface. The <c>Power</c> counter on those instances reads zero on this
/// hardware, but <c>Energy</c> is a running total, so power is the delta over
/// time: energy is in nanojoules and time in milliseconds, making
/// <c>watts = dEnergy / dTime / 1000</c>.
/// </para>
/// <para>
/// This deliberately avoids MSR reads. Tools like HWiNFO get per-component
/// power (including discrete GPU wattage and VRM temperatures) by installing a
/// ring-0 driver; that is the same class of driver as WinRing0, which this
/// build removes because Defender flags it.
/// </para>
/// </remarks>
public sealed class SystemSensors : IDisposable
{
    private const string EnergyCat = "Energy Meter";

    // RAPL domain instance names. PKG is the whole package, PP0 the cores,
    // PP1 the integrated GPU.
    private const string RaplPkg = "RAPL_Package0_PKG";
    private const string RaplPP0 = "RAPL_Package0_PP0";
    private const string RaplPP1 = "RAPL_Package0_PP1";

    private readonly Dictionary<string, (double Energy, double Time)> _lastEnergy = [];

    /// <summary>
    /// Last good wattage per RAPL domain, held while a new sample accumulates.
    /// </summary>
    private readonly Dictionary<string, double> _lastWatts = [];

    /// <summary>
    /// Shortest energy window that yields a correct figure, in counter
    /// milliseconds.
    /// </summary>
    /// <remarks>
    /// The Energy Meter counter only advances about once a second, so a
    /// shorter window catches a partial update and under-reports badly -
    /// measured 7.0 W over 250 ms against 16.8 W over 1000 ms for the same
    /// load. Sampling on a 1 s timer sat right on that boundary, so ordinary
    /// timer jitter was enough to produce the wrong answer.
    /// </remarks>
    private const double MinEnergyWindowMs = 900;

    private PerformanceCounter _cpuLoad, _cpuPerf, _ramAvail;
    private double _ramTotalMB;
    private double _baseMHz;

    private List<GpuReading> _adapters = [];
    private readonly AfterburnerSensors _ab = new();
    private readonly LevelZeroSensors _ze = new();
    private readonly IgclSensors _igcl = new();
    private bool _disposed;

    /// <summary>
    /// <see langword="true"/> once at least one prior sample exists, so power
    /// deltas are meaningful. The first <see cref="Read"/> returns no wattage.
    /// </summary>
    public bool HasPowerBaseline { get; private set; }

    public SystemSensors()
    {
        TryInit();
    }

    private void TryInit()
    {
        // "% Processor Time", not "% Processor Utility": the latter is scaled
        // by frequency and legitimately exceeds 100% when boosting, which reads
        // as a bug in a monitoring display (it showed 184% under load).
        try
        {
            _cpuLoad = new PerformanceCounter("Processor Information", "% Processor Time", "_Total");
            _cpuLoad.NextValue();
        }
        catch
        {
            try
            {
                _cpuLoad = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                _cpuLoad.NextValue();
            }
            catch { _cpuLoad = null; }
        }

        try
        {
            _cpuPerf = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total");
            _cpuPerf.NextValue();
        }
        catch { _cpuPerf = null; }

        try
        {
            _ramAvail = new PerformanceCounter("Memory", "Available MBytes");
            _ramAvail.NextValue();
        }
        catch { _ramAvail = null; }

        try
        {
            using ManagementObjectSearcher s = new("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
            foreach (ManagementBaseObject o in s.Get())
            {
                _ramTotalMB = Convert.ToDouble(o["TotalPhysicalMemory"], CultureInfo.InvariantCulture) / 1048576.0;
                break;
            }
        }
        catch { _ramTotalMB = 0; }

        try
        {
            using RegistryKey k = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            _baseMHz = Convert.ToDouble(k?.GetValue("~MHz") ?? 0, CultureInfo.InvariantCulture);
        }
        catch { _baseMHz = 0; }

        _adapters = EnumerateAdapters();

        // prime the RAPL baseline so the first real read already has a delta
        SampleEnergy(RaplPkg);
        SampleEnergy(RaplPP0);
        SampleEnergy(RaplPP1);
    }

    /// <summary>
    /// Enumerates display adapters and their LUIDs via DXGI, skipping software
    /// adapters like the Microsoft Basic Render Driver.
    /// </summary>
    private static List<GpuReading> EnumerateAdapters()
    {
        List<GpuReading> list = [];
        try
        {
            foreach (Dxgi.AdapterInfo a in Dxgi.EnumAdapters())
            {
                // software adapter: no vendor silicon behind it
                if (a.Name.IndexOf("Basic Render", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }

                bool discrete = a.DedicatedVideoMemory > 512L * 1024 * 1024;
                list.Add(new GpuReading
                {
                    Name = a.Name,
                    Luid = a.Luid,
                    DeviceId = a.DeviceId,
                    // an integrated GPU carves a small aperture out of system
                    // RAM; a discrete card reports its own large pool
                    Discrete = discrete,
                    // For an integrated GPU the dedicated figure is a token
                    // 128 MB it never actually fills, while its real working
                    // set comes out of shared system memory - so show that as
                    // the capacity instead.
                    VramTotalMB = (discrete ? a.DedicatedVideoMemory : a.SharedSystemMemory) / 1048576.0,
                });
            }
        }
        catch { }
        return list;
    }

    /// <summary>
    /// Reads one snapshot of every available sensor.
    /// </summary>
    public SensorSnapshot Read()
    {
        SensorSnapshot s = new();

        s.CpuPackageWatts = ReadWatts(RaplPkg);
        s.CpuCoresWatts = ReadWatts(RaplPP0);
        double? igpuWatts = ReadWatts(RaplPP1);
        HasPowerBaseline = true;

        try { s.CpuLoadPercent = _cpuLoad?.NextValue() ?? 0; } catch { }
        try
        {
            // "% Processor Performance" is a percentage of the base clock, and
            // goes above 100 when boosting.
            double perf = _cpuPerf?.NextValue() ?? 0;
            s.CpuMHz = _baseMHz * perf / 100.0;
        }
        catch { }

        try
        {
            double avail = _ramAvail?.NextValue() ?? 0;
            s.RamTotalMB = _ramTotalMB;
            s.RamUsedMB = _ramTotalMB > 0 ? _ramTotalMB - avail : 0;
        }
        catch { }

        s.BatteryWatts = ReadBatteryWatts();

        Dictionary<string, (double Load, double Vram)> gpuPerf = ReadGpuCounters();
        foreach (GpuReading a in _adapters)
        {
            GpuReading g = new()
            {
                Name = a.Name,
                Luid = a.Luid,
                DeviceId = a.DeviceId,
                Discrete = a.Discrete,
                VramTotalMB = a.VramTotalMB,
                // only the iGPU has a RAPL domain
                Watts = a.Discrete ? null : igpuWatts,
            };
            if (gpuPerf.TryGetValue(a.Luid, out (double Load, double Vram) v))
            {
                g.LoadPercent = v.Load;
                g.VramUsedMB = v.Vram / 1048576.0;
            }
            s.Gpus.Add(g);
        }

        // Level Zero first: it needs no third-party tool. Afterburner then
        // fills what Level Zero cannot reach, chiefly GPU wattage.
        EnrichFromLevelZero(s);
        EnrichFromIgcl(s);
        EnrichFromAfterburner(s);
        return s;
    }

    /// <summary>
    /// Overlays Intel Level Zero's readings: GPU clocks and discrete-GPU
    /// temperature, neither of which Windows exposes, with no extra software.
    /// </summary>
    private void EnrichFromLevelZero(SensorSnapshot s)
    {
        List<ZeGpu> gpus = _ze.Read();
        s.LevelZeroActive = _ze.Available;
        if (!_ze.Available)
        {
            return;
        }

        foreach (ZeGpu z in gpus)
        {
            foreach (GpuReading g in s.Gpus)
            {
                // match on PCI device ID rather than list order, which Level
                // Zero and DXGI do not necessarily agree on
                if (g.DeviceId != z.DeviceId)
                {
                    continue;
                }

                g.CoreMHz ??= z.CoreMHz;
                g.MemoryMHz ??= z.MemoryMHz;
                g.TempC ??= z.TempC;
                break;
            }
        }
    }

    /// <summary>
    /// Overlays Intel Control Library telemetry: the graphics tile's own power
    /// and voltage, which nothing else exposes in user mode.
    /// </summary>
    /// <remarks>
    /// IGCL enumerates adapters in its own order, which is not DXGI's - on the
    /// test machine the two are reversed - and ctlGetDeviceProperties rejects
    /// any struct size but its exact internal one, so the PCI ID cannot be read
    /// back to match on. Instead each IGCL device is matched to the adapter
    /// whose Level Zero core clock it reports, and anything ambiguous is
    /// dropped rather than attributed to the wrong GPU.
    /// </remarks>
    private void EnrichFromIgcl(SensorSnapshot s)
    {
        List<IgclGpu> gpus = _igcl.Read();
        s.IgclActive = _igcl.Available;
        if (!_igcl.Available)
        {
            return;
        }

        foreach (IgclGpu ig in gpus)
        {
            if (!ig.CoreMHz.HasValue)
            {
                continue;
            }

            GpuReading match = null;
            int matches = 0;
            foreach (GpuReading g in s.Gpus)
            {
                if (g.CoreMHz.HasValue && Math.Abs(g.CoreMHz.Value - ig.CoreMHz.Value) < 1.0)
                {
                    match = g;
                    matches++;
                }
            }

            // both GPUs sitting at the same clock makes attribution a guess
            if (matches != 1 || match is null)
            {
                continue;
            }

            match.Watts ??= ig.Watts;
            match.Volts ??= ig.Volts;
            match.TempC ??= ig.TempC;
            if (ig.Watts.HasValue)
            {
                match.PowerOnCpuPackage = false;
            }
        }
    }

    /// <summary>
    /// Overlays MSI Afterburner's readings when it is running.
    /// </summary>
    /// <remarks>
    /// Afterburner's RTCore64 driver reaches sensors Windows does not expose at
    /// all: GPU core clock and power (for both adapters), CPU package
    /// temperature, and - with RivaTuner Statistics Server - the frame rate.
    /// Its values take precedence where they overlap, since they come from the
    /// hardware rather than being derived.
    /// </remarks>
    private void EnrichFromAfterburner(SensorSnapshot s)
    {
        List<AbSensor> ab = _ab.Read();
        s.AfterburnerActive = _ab.Available;
        if (!_ab.Available)
        {
            return;
        }

        s.CpuTempC = AfterburnerSensors.Find(ab, "CPU temperature");

        // RTSS parks Framerate at a huge sentinel when no application is
        // hooked, which rendered as a 20-digit number. Only accept a plausible
        // rate.
        float? fps = AfterburnerSensors.Find(ab, "Framerate");
        s.Fps = fps is > 0 and < 1000 ? fps : null;

        float? cpuW = AfterburnerSensors.Find(ab, "CPU power");
        if (cpuW.HasValue)
        {
            s.CpuPackageWatts = cpuW.Value;
        }
        float? cpuMHz = AfterburnerSensors.Find(ab, "CPU clock");
        if (cpuMHz.HasValue)
        {
            s.CpuMHz = cpuMHz.Value;
        }

        for (int i = 0; i < s.Gpus.Count; i++)
        {
            uint idx = (uint)i;
            s.Gpus[i].CoreMHz = AfterburnerSensors.FindGpu(ab, idx, "core clock");

            float? w = AfterburnerSensors.FindGpu(ab, idx, "power");
            if (w.HasValue)
            {
                // For an on-die GPU, Afterburner reports the whole CPU package
                // rather than the graphics tile, so the "GPU power" row tracked
                // CPU package watts exactly (64.1 W against 64.0 W). Showing
                // that as integrated-GPU power is misleading, so it is dropped
                // and the UI says the iGPU draws from the package instead.
                bool echoesPackage = !s.Gpus[i].Discrete &&
                    s.CpuPackageWatts.HasValue &&
                    Math.Abs(w.Value - s.CpuPackageWatts.Value) < 1.0;

                s.Gpus[i].Watts = echoesPackage ? null : w.Value;
                s.Gpus[i].PowerOnCpuPackage = echoesPackage;
            }

            float? mem = AfterburnerSensors.FindGpu(ab, idx, "memory usage");
            if (mem.HasValue && mem.Value > 0)
            {
                s.Gpus[i].VramUsedMB = mem.Value;
            }

            float? use = AfterburnerSensors.FindGpu(ab, idx, "usage");
            if (use.HasValue && s.Gpus[i].LoadPercent <= 0)
            {
                s.Gpus[i].LoadPercent = use.Value;
            }
        }
    }

    #region RAPL
    private (double Energy, double Time)? SampleEnergy(string instance)
    {
        try
        {
            PerformanceCounter e = new(EnergyCat, "Energy", instance);
            PerformanceCounter t = new(EnergyCat, "Time", instance);
            (double, double) sample = (e.RawValue, t.RawValue);
            e.Dispose();
            t.Dispose();
            _lastEnergy[instance] = sample;
            return sample;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Power for a RAPL domain, from the change in its energy counter.
    /// </summary>
    private double? ReadWatts(string instance)
    {
        if (!_lastEnergy.TryGetValue(instance, out (double Energy, double Time) prev))
        {
            SampleEnergy(instance);
            return null;
        }

        try
        {
            PerformanceCounter e = new(EnergyCat, "Energy", instance);
            PerformanceCounter t = new(EnergyCat, "Time", instance);
            double energy = e.RawValue, time = t.RawValue;
            e.Dispose();
            t.Dispose();

            double dE = energy - prev.Energy;
            double dT = time - prev.Time;

            // Not enough counter time has passed for a trustworthy figure:
            // keep the baseline where it is so the window keeps growing, and
            // report the last good value rather than a wrong one.
            if (dT < MinEnergyWindowMs)
            {
                return _lastWatts.TryGetValue(instance, out double held) ? held : null;
            }

            _lastEnergy[instance] = (energy, time);

            if (dT <= 0 || dE < 0)
            {
                return null;
            }

            // Energy is in nanojoules, time in milliseconds:
            //   (dE nJ) / (dT ms) = (dE * 1e-9 J) / (dT * 1e-3 s)
            //                     = (dE / dT) * 1e-6 W
            // so the divisor is 1e6, not 1e3. Getting this wrong made the CPU
            // package read ~16,849 W, which the sanity check below then threw
            // away as nonsense - hence "n/a" rather than an obviously wrong
            // number.
            double watts = dE / dT / 1e6;

            // a counter wrap or a stall produces nonsense; drop it rather than
            // display a spike
            if (watts is < 0 or >= 400)
            {
                return null;
            }

            _lastWatts[instance] = watts;
            return watts;
        }
        catch
        {
            return null;
        }
    }
    #endregion

    #region GPU
    /// <summary>
    /// Per-adapter load and dedicated memory, keyed by LUID.
    /// </summary>
    /// <remarks>
    /// Uses the formatted WMI perf classes rather than
    /// <see cref="PerformanceCounter"/>: the GPU Engine category has hundreds
    /// of per-process instances that churn constantly, so constructing counter
    /// objects for each of them every tick is far too slow.
    /// </remarks>
    private static Dictionary<string, (double Load, double Vram)> ReadGpuCounters()
    {
        Dictionary<string, (double Load, double Vram)> result = [];

        try
        {
            using ManagementObjectSearcher s = new(
                "SELECT Name, UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine");
            foreach (ManagementBaseObject o in s.Get())
            {
                string name = o["Name"] as string;
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                string luid = LuidOf(name);
                if (luid is null)
                {
                    continue;
                }

                double util = Convert.ToDouble(o["UtilizationPercentage"], CultureInfo.InvariantCulture);
                result.TryGetValue(luid, out (double Load, double Vram) cur);
                result[luid] = (cur.Load + util, cur.Vram);
            }
        }
        catch { }

        try
        {
            // NOTE: DedicatedUsage reads 0 for every adapter on this hardware,
            // including the discrete one - the actual working set is reported
            // under SharedUsage. Using DedicatedUsage alone left VRAM showing a
            // permanent 0 MB, so both are summed.
            using ManagementObjectSearcher s = new(
                "SELECT Name, DedicatedUsage, SharedUsage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUAdapterMemory");
            foreach (ManagementBaseObject o in s.Get())
            {
                string name = o["Name"] as string;
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                string luid = LuidOf(name);
                if (luid is null)
                {
                    continue;
                }

                double vram =
                    Convert.ToDouble(o["DedicatedUsage"], CultureInfo.InvariantCulture) +
                    Convert.ToDouble(o["SharedUsage"], CultureInfo.InvariantCulture);
                result.TryGetValue(luid, out (double Load, double Vram) cur);
                result[luid] = (cur.Load, cur.Vram + vram);
            }
        }
        catch { }

        return result;
    }

    /// <summary>
    /// Pulls the <c>luid_0xHHHHHHHH_0xLLLLLLLL</c> prefix out of a GPU
    /// performance counter instance name.
    /// </summary>
    private static string LuidOf(string instanceName)
    {
        int i = instanceName.IndexOf("luid_", StringComparison.Ordinal);
        if (i < 0)
        {
            return null;
        }

        string[] parts = instanceName.Substring(i).Split('_');
        return parts.Length >= 3
            ? string.Concat("luid_", parts[1], "_", parts[2])
            : null;
    }
    #endregion

    /// <summary>
    /// Whole-system draw while on battery, in watts. Returns
    /// <see langword="null"/> on AC, where there is nothing to measure.
    /// </summary>
    private static double? ReadBatteryWatts()
    {
        try
        {
            using ManagementObjectSearcher s = new(@"root\WMI",
                "SELECT DischargeRate, Discharging FROM BatteryStatus");
            foreach (ManagementBaseObject o in s.Get())
            {
                if (o["Discharging"] is bool d && d)
                {
                    double mW = Convert.ToDouble(o["DischargeRate"], CultureInfo.InvariantCulture);
                    if (mW > 0)
                    {
                        return mW / 1000.0;
                    }
                }
            }
        }
        catch { }
        return null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _cpuLoad?.Dispose();
        _cpuPerf?.Dispose();
        _ramAvail?.Dispose();
    }
}
