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
using System.Runtime.InteropServices;

namespace YAMDCC.Common.Monitoring;

/// <summary>
/// Telemetry for one Intel GPU, read through Level Zero.
/// </summary>
public sealed class ZeGpu
{
    /// <summary>PCI device ID, used to match this against a DXGI adapter.</summary>
    public uint DeviceId { get; set; }

    public string Name { get; set; }

    /// <summary>Core clock in MHz.</summary>
    public double? CoreMHz { get; set; }

    /// <summary>Memory clock in MHz, where the GPU has its own memory.</summary>
    public double? MemoryMHz { get; set; }

    /// <summary>Hottest reported sensor, in degrees Celsius.</summary>
    public double? TempC { get; set; }
}

/// <summary>
/// Reads Intel GPU telemetry through the Level Zero "sysman" API, with no
/// kernel driver and no third-party tool.
/// </summary>
/// <remarks>
/// <para>
/// <c>ze_loader.dll</c> ships with the Intel graphics driver and lives in
/// System32, so this needs nothing extra installed. It provides what Windows
/// itself does not expose at all: GPU core and memory clocks, and the discrete
/// GPU's temperature - the latter being more useful than the EC's GPU sensor,
/// which reads 0 whenever the card is parked.
/// </para>
/// <para>
/// Power is deliberately not read here. The Arc does advertise one power
/// domain, but <c>zesPowerGetEnergyCounter</c> returns zero energy with a
/// timestamp that never advances on this driver, so the figure would always be
/// meaningless. GPU wattage therefore still comes from MSI Afterburner when it
/// is running.
/// </para>
/// </remarks>
public sealed class LevelZeroSensors
{
    private const string Lib = "ze_loader.dll";

    // ZE_STRUCTURE_TYPE_DEVICE_PROPERTIES
    private const int DevicePropertiesStype = 1;

    #region interop
    [DllImport(Lib)] private static extern int zeInit(uint flags);
    [DllImport(Lib)] private static extern int zeDriverGet(ref uint count, IntPtr[] drivers);
    [DllImport(Lib)] private static extern int zeDeviceGet(IntPtr driver, ref uint count, IntPtr[] devices);
    [DllImport(Lib)] private static extern int zeDeviceGetProperties(IntPtr device, ref DeviceProperties props);
    [DllImport(Lib)] private static extern int zesDeviceEnumFrequencyDomains(IntPtr device, ref uint count, IntPtr[] handles);
    [DllImport(Lib)] private static extern int zesFrequencyGetState(IntPtr handle, ref FrequencyState state);
    [DllImport(Lib)] private static extern int zesDeviceEnumTemperatureSensors(IntPtr device, ref uint count, IntPtr[] handles);
    [DllImport(Lib)] private static extern int zesTemperatureGetState(IntPtr handle, ref double celsius);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct DeviceProperties
    {
        public int Stype;
        public IntPtr PNext;
        public int Type;
        public uint VendorId;
        public uint DeviceId;
        public uint Flags;
        public uint SubdeviceId;
        public uint CoreClockRate;
        public ulong MaxMemAllocSize;
        public uint MaxHardwareContexts;
        public uint MaxCommandQueuePriority;
        public uint NumThreadsPerEU;
        public uint PhysicalEUSimdWidth;
        public uint NumEUsPerSubslice;
        public uint NumSubslicesPerSlice;
        public uint NumSlices;
        public ulong TimerResolution;
        public uint TimestampValidBits;
        public uint KernelTimestampValidBits;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] Uuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Name;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FrequencyState
    {
        public int Stype;
        public IntPtr PNext;
        public double CurrentVoltage;
        public double Request;
        public double Tdp;
        public double Efficient;
        public double Actual;
        public uint ThrottleReasons;
    }
    #endregion

    private bool _initialised;
    private bool _unavailable;

    /// <summary>
    /// <see langword="true"/> if the last <see cref="Read"/> returned data.
    /// </summary>
    public bool Available { get; private set; }

    /// <summary>
    /// Reads every Intel GPU Level Zero can see. Returns an empty list when the
    /// runtime is missing, rather than throwing.
    /// </summary>
    public List<ZeGpu> Read()
    {
        List<ZeGpu> result = [];
        if (_unavailable)
        {
            return result;
        }

        try
        {
            if (!_initialised)
            {
                // Older loaders gate sysman behind this; newer ones ignore it.
                // Harmless either way, and it must be set before zeInit().
                Environment.SetEnvironmentVariable("ZES_ENABLE_SYSMAN", "1");
                if (zeInit(0) != 0)
                {
                    _unavailable = true;
                    return result;
                }
                _initialised = true;
            }

            uint driverCount = 0;
            if (zeDriverGet(ref driverCount, null) != 0 || driverCount == 0)
            {
                return result;
            }

            IntPtr[] drivers = new IntPtr[driverCount];
            if (zeDriverGet(ref driverCount, drivers) != 0)
            {
                return result;
            }

            foreach (IntPtr driver in drivers)
            {
                uint deviceCount = 0;
                if (zeDeviceGet(driver, ref deviceCount, null) != 0 || deviceCount == 0)
                {
                    continue;
                }

                IntPtr[] devices = new IntPtr[deviceCount];
                if (zeDeviceGet(driver, ref deviceCount, devices) != 0)
                {
                    continue;
                }

                foreach (IntPtr device in devices)
                {
                    ZeGpu gpu = ReadDevice(device);
                    if (gpu is not null)
                    {
                        result.Add(gpu);
                    }
                }
            }
        }
        catch (DllNotFoundException)
        {
            // no Intel graphics runtime on this machine
            _unavailable = true;
        }
        catch (EntryPointNotFoundException)
        {
            // loader present but too old for the sysman entry points
            _unavailable = true;
        }
        catch { }

        Available = result.Count > 0;
        return result;
    }

    private static ZeGpu ReadDevice(IntPtr device)
    {
        DeviceProperties props = new() { Stype = DevicePropertiesStype };
        if (zeDeviceGetProperties(device, ref props) != 0)
        {
            return null;
        }

        ZeGpu gpu = new()
        {
            DeviceId = props.DeviceId,
            Name = props.Name?.Trim(),
        };

        // Frequency domains come back GPU-core first, then memory where the
        // adapter has its own. Verified on an Arc A370M, which reports a core
        // domain plus a ~14 GHz memory domain, and on an Iris Xe, which reports
        // only the core.
        try
        {
            uint count = 0;
            if (zesDeviceEnumFrequencyDomains(device, ref count, null) == 0 && count > 0)
            {
                IntPtr[] handles = new IntPtr[count];
                if (zesDeviceEnumFrequencyDomains(device, ref count, handles) == 0)
                {
                    for (uint i = 0; i < count; i++)
                    {
                        FrequencyState state = new();
                        if (zesFrequencyGetState(handles[i], ref state) != 0 || state.Actual <= 0)
                        {
                            continue;
                        }

                        if (i == 0)
                        {
                            gpu.CoreMHz = state.Actual;
                        }
                        else if (!gpu.MemoryMHz.HasValue)
                        {
                            gpu.MemoryMHz = state.Actual;
                        }
                    }
                }
            }
        }
        catch { }

        // Several sensors report for the same die; the hottest is the useful
        // one. An integrated GPU exposes none, since it sits on the CPU die.
        try
        {
            uint count = 0;
            if (zesDeviceEnumTemperatureSensors(device, ref count, null) == 0 && count > 0)
            {
                IntPtr[] handles = new IntPtr[count];
                if (zesDeviceEnumTemperatureSensors(device, ref count, handles) == 0)
                {
                    double hottest = 0;
                    for (uint i = 0; i < count; i++)
                    {
                        double t = 0;
                        if (zesTemperatureGetState(handles[i], ref t) == 0 && t > hottest)
                        {
                            hottest = t;
                        }
                    }
                    if (hottest > 0)
                    {
                        gpu.TempC = hottest;
                    }
                }
            }
        }
        catch { }

        return gpu;
    }
}
