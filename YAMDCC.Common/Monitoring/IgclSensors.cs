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
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace YAMDCC.Common.Monitoring;

/// <summary>
/// Power telemetry for one Intel GPU, as reported by IGCL.
/// </summary>
public sealed class IgclGpu
{
    /// <summary>IGCL's own device index.</summary>
    public int Index { get; set; }

    /// <summary>Core clock in MHz, used to match this against a known adapter.</summary>
    public double? CoreMHz { get; set; }

    /// <summary>Graphics-tile power in watts, derived from the energy counter.</summary>
    public double? Watts { get; set; }

    /// <summary>Core voltage in volts.</summary>
    public double? Volts { get; set; }

    /// <summary>Die temperature in Celsius, where the adapter reports one.</summary>
    public double? TempC { get; set; }
}

/// <summary>
/// Reads Intel GPU power telemetry through the Intel Control Library (IGCL) -
/// the same user-mode API Intel's own Arc Control uses.
/// </summary>
/// <remarks>
/// <para>
/// <c>ControlLib.dll</c> ships inside the graphics driver store, so nothing
/// extra needs installing and no kernel driver is involved. It provides the
/// graphics tile's own energy counter, which neither Windows nor Level Zero
/// exposes: the integrated GPU has no RAPL power domain and no Level Zero
/// power domain, yet IGCL still reports its energy.
/// </para>
/// <para>
/// Power is a delta: the counter is cumulative joules alongside a timestamp in
/// seconds, so watts is the change in one divided by the change in the other.
/// </para>
/// </remarks>
public sealed class IgclSensors
{
    #region interop
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string path);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);

    [StructLayout(LayoutKind.Sequential)]
    private struct InitArgs
    {
        public uint Size;
        public byte Version;
        public uint AppVersion;
        public uint Flags;
        public uint SupportedVersion;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] AppUID;
    }

    /// <summary>
    /// ctl_oc_telemetry_item_t: a bool, two enums, then an 8-byte union at an
    /// 8-byte aligned offset. 24 bytes in total.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = ItemSize)]
    private struct TelemetryItem
    {
        [FieldOffset(0)] public byte Supported;
        [FieldOffset(4)] public int Units;
        [FieldOffset(8)] public int Type;
        [FieldOffset(16)] public double Value;
    }

    private const int ItemSize = 24;

    // ctl_power_telemetry_t begins with uint32 Size and uint8 Version, then
    // pads to the first 8-byte aligned telemetry item.
    private const int FirstItemOffset = 8;

    // item order at the head of the struct
    private const int IdxTimeStamp = 0;
    private const int IdxEnergy = 1;
    private const int IdxVoltage = 2;
    private const int IdxFrequency = 3;
    private const int IdxTemperature = 4;

    private delegate int CtlInit(ref InitArgs args, out IntPtr hApi);
    private delegate int CtlEnumerateDevices(IntPtr hApi, ref uint count, IntPtr[] devices);
    private delegate int CtlPowerTelemetryGet(IntPtr device, IntPtr telemetry);
    #endregion

    private IntPtr _lib, _hApi;
    private IntPtr[] _devices = [];
    private CtlPowerTelemetryGet _telemetry;
    private bool _tried, _unavailable;

    // previous (energy joules, timestamp seconds) per device, for the delta
    private readonly Dictionary<int, (double Energy, double Time)> _last = [];

    /// <summary><see langword="true"/> if the last read returned data.</summary>
    public bool Available { get; private set; }

    private bool Init()
    {
        if (_tried)
        {
            return !_unavailable;
        }
        _tried = true;

        try
        {
            string dll = FindControlLib();
            if (dll is null)
            {
                _unavailable = true;
                return false;
            }

            _lib = LoadLibrary(dll);
            if (_lib == IntPtr.Zero)
            {
                _unavailable = true;
                return false;
            }

            CtlInit init = (CtlInit)GetDelegate("ctlInit", typeof(CtlInit));
            CtlEnumerateDevices enumerate =
                (CtlEnumerateDevices)GetDelegate("ctlEnumerateDevices", typeof(CtlEnumerateDevices));
            _telemetry = (CtlPowerTelemetryGet)GetDelegate("ctlPowerTelemetryGet", typeof(CtlPowerTelemetryGet));

            if (init is null || enumerate is null || _telemetry is null)
            {
                _unavailable = true;
                return false;
            }

            InitArgs args = new()
            {
                Size = (uint)Marshal.SizeOf(typeof(InitArgs)),
                Version = 0,
                AppVersion = 0x00010002,   // targeting IGCL 1.2
                AppUID = new byte[16],
            };

            if (init(ref args, out _hApi) != 0 || _hApi == IntPtr.Zero)
            {
                _unavailable = true;
                return false;
            }

            uint count = 0;
            if (enumerate(_hApi, ref count, null) != 0 || count == 0)
            {
                _unavailable = true;
                return false;
            }

            _devices = new IntPtr[count];
            if (enumerate(_hApi, ref count, _devices) != 0)
            {
                _unavailable = true;
                return false;
            }
            return true;
        }
        catch
        {
            _unavailable = true;
            return false;
        }
    }

    private Delegate GetDelegate(string name, Type type)
    {
        IntPtr p = GetProcAddress(_lib, name);
        return p == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer(p, type);
    }

    /// <summary>
    /// Finds the newest 64-bit ControlLib.dll in the driver store. It is not on
    /// the DLL search path, so it has to be located and loaded explicitly.
    /// </summary>
    private static string FindControlLib()
    {
        try
        {
            string repo = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "DriverStore", "FileRepository");
            if (!Directory.Exists(repo))
            {
                return null;
            }

            return Directory.GetDirectories(repo, "iigd_*")
                .Select(d => Path.Combine(d, "ControlLib.dll"))
                .Where(File.Exists)
                .OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reads telemetry for every Intel GPU IGCL can see. Adapters that report
    /// nothing (a parked discrete GPU does) are skipped.
    /// </summary>
    public List<IgclGpu> Read()
    {
        List<IgclGpu> result = [];
        if (!Init())
        {
            Available = false;
            return result;
        }

        // generous buffer: the struct grows between IGCL versions, and the
        // leading Size field tells the library how much room it has
        const int BufSize = 4096;
        IntPtr buf = Marshal.AllocHGlobal(BufSize);

        try
        {
            for (int i = 0; i < _devices.Length; i++)
            {
                for (int z = 0; z < BufSize; z += 8)
                {
                    Marshal.WriteInt64(buf, z, 0);
                }
                Marshal.WriteInt32(buf, 0, BufSize);
                Marshal.WriteByte(buf, 4, 1);

                if (_telemetry(_devices[i], buf) != 0)
                {
                    continue;
                }

                double? time = ItemAt(buf, IdxTimeStamp);
                double? energy = ItemAt(buf, IdxEnergy);

                IgclGpu gpu = new()
                {
                    Index = i,
                    CoreMHz = ItemAt(buf, IdxFrequency),
                    Volts = ItemAt(buf, IdxVoltage),
                    TempC = ItemAt(buf, IdxTemperature),
                };

                if (energy.HasValue && time.HasValue)
                {
                    if (_last.TryGetValue(i, out (double Energy, double Time) prev))
                    {
                        double dE = energy.Value - prev.Energy;
                        double dT = time.Value - prev.Time;
                        // joules over seconds is watts; reject a counter reset
                        if (dT > 0 && dE >= 0)
                        {
                            double w = dE / dT;
                            if (w < 400)
                            {
                                gpu.Watts = w;
                            }
                        }
                    }
                    _last[i] = (energy.Value, time.Value);
                }

                // an adapter reporting nothing at all is parked; skip it rather
                // than adding a row of nulls
                if (gpu.CoreMHz.HasValue || gpu.Watts.HasValue || gpu.TempC.HasValue)
                {
                    result.Add(gpu);
                }
            }
        }
        catch { }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }

        Available = result.Count > 0;
        return result;
    }

    /// <summary>
    /// Reads one telemetry item, returning <see langword="null"/> when the
    /// adapter marks it unsupported.
    /// </summary>
    private static double? ItemAt(IntPtr buf, int index)
    {
        TelemetryItem item = (TelemetryItem)Marshal.PtrToStructure(
            buf + FirstItemOffset + (index * ItemSize), typeof(TelemetryItem));
        return item.Supported == 0 ? null : item.Value;
    }
}
