using System;
using System.Globalization;
using System.Management;
using System.Text;
using YAMDCC.Common.Configs;
using YAMDCC.Common.Logs;
using YAMDCC.IPC;

namespace YAMDCC.Service.FanControllers;

/// <summary>
/// An implementation of an MSI laptop fan controller
/// using the WMI2 methods present on modern MSI laptops.
/// </summary>
/// <remarks>
/// This implementation is most similar to how MSI Center does fan
/// control and other settings, but this implementation is still
/// experimental, and doesn't currently support older laptops.
/// </remarks>
internal sealed class Wmi2FanController : IFanController, IDisposable
{
    private readonly Logger Log;
    private ManagementObject Instance;
    private ManagementBaseObject Params;

    private Version WmiVer;

    private readonly byte[] TUpMap = [0, 3, 4, 5, 6, 7, 1];

    public Wmi2FanController(Logger log)
    {
        Log = log;
    }

    public bool Init()
    {
        try
        {
            // Run a basic WMI method to make sure we have the correct parameter
            // objects, and also determine which WMI API the computer is using.
            Instance = new ManagementObject(@"root\WMI",
                "MSI_ACPI.InstanceName='ACPI\\PNP0C14\\0_0'", null);
            Params = Instance.InvokeMethod("Get_WMI", null, null);

            // All MSI WMI methods appear to return
            // 1 as the first byte on success.
            byte[] result = (byte[])((ManagementBaseObject)Params["Data"])["Bytes"];
            if (result[0] == 1)
            {
                WmiVer = new Version(result[1], result[2]);
                Log.Debug($"WMI version: {WmiVer}");
                return true;
            }
            Log.Error($"Get_WMI reported failure!");
            return false;
        }
        catch (ManagementException ex)
        {
            Log.Error($"Failed to initialise WMI2 backend: {ex.GetType()}: {ex.Message}");
            return false;
        }
    }
    public void Deinit()
    {
        Dispose();
    }

    private bool ReadWMI2(string method, byte subVal, out byte[] result)
    {
        if (Instance is null || Params is null)
        {
            throw new InvalidOperationException("WMI2 backend is not initialised.");
        }

        ManagementBaseObject paramData = (ManagementBaseObject)Params["Data"];
        byte[] data = new byte[32];
        data[0] = subVal;

        paramData.SetPropertyValue("Bytes", data);
        Params.SetPropertyValue("Data", paramData);
        result = (byte[])((ManagementBaseObject)Instance.InvokeMethod(
            method, Params, null)["Data"])["Bytes"];

        StringBuilder sb = new($"{method} ({subVal}) returned: ");
        for (int i = 0; i < result.Length; i++)
        {
            sb.Append($"0x{result[i]:X2}, ");
        }
        Log.Debug(sb.ToString().Substring(0, sb.Length - 2));
        return result[0] == 1;
    }

    private bool WriteWMI2(string method, byte[] data)
    {
        if (Instance is null || Params is null)
        {
            throw new InvalidOperationException("WMI2 backend is not initialised.");
        }

        ManagementBaseObject paramData = (ManagementBaseObject)Params["Data"];

        paramData.SetPropertyValue("Bytes", data);
        Params.SetPropertyValue("Data", paramData);
        byte[] result = (byte[])((ManagementBaseObject)Instance.InvokeMethod(
            method, Params, null)["Data"])["Bytes"];
        return result[0] == 1;
    }

    public EcInfo GetEcFirmwareInfo()
    {
        if (ReadWMI2("Get_EC", 0, out byte[] result))
        {
            string ecVer = Encoding.UTF8.GetString(result, 2, 0xC),
            ecDate = Encoding.UTF8.GetString(result, 14, 0x10);

            if (ecVer.Length == 0xC && ecDate.Length == 0x10)
            {
                EcInfo ecInfo = new();
                try
                {
                    string temp =
                        $"{ecDate.Substring(4, 4)}-{ecDate.Substring(0, 2)}-{ecDate.Substring(2, 2)}" +
                        $"T{ecDate.Substring(8, 2).Replace(' ', '0')}:{ecDate.Substring(11, 2)}:{ecDate.Substring(14, 2)}";
                    ecInfo.Date = DateTime.ParseExact(temp, "s", CultureInfo.InvariantCulture);
                    Log.Debug($"EC firmware date: {ecInfo.Date:G}", nameof(Wmi2FanController));
                    return ecInfo;
                }
                catch (FormatException ex)
                {
                    Log.Error($"Failed to parse EC firmware date: {ex.Message}", nameof(Wmi2FanController));
                    Log.Debug($"EC firmware date (raw): {ecDate}", nameof(Wmi2FanController));
                }
            }
        }
        return null;
    }

    public FanProf GetFanProf(bool gpu, bool offsetDT)
    {
        byte subVal = gpu ? (byte)2 : (byte)1;
        if (ReadWMI2("Get_Temperature", subVal, out byte[] tUpVals) &&
            ReadWMI2("Get_Fan", subVal, out byte[] tSpds) &&
            ReadWMI2("Get_Thermal", subVal, out byte[] tDownVals))
        {
            FanProf prof = new()
            {
                Thresholds = [],
            };

            for (int i = 0; i < 7; i++)
            {
                prof.Thresholds.Add(new Threshold
                {
                    Tup = i == 0 ? (byte)0 : tUpVals[TUpMap[i] + 1],
                    Tdown = i == 0 ? (byte)0 : offsetDT
                        ? (byte)(tUpVals[TUpMap[i] + 1] - tDownVals[i + 1])
                        : tDownVals[i + 1],
                    Speed = tSpds[i + 2],
                });
            }
        }
        return null;
    }
    public bool SetFanProf(FanProf prof, bool gpu, bool offsetDT)
    {
        byte subVal = gpu ? (byte)2 : (byte)1;
        if (ReadWMI2("Get_Temperature", subVal, out byte[] tUpVals) &&
            ReadWMI2("Get_Fan", subVal, out byte[] tSpds) &&
            ReadWMI2("Get_Thermal", subVal, out byte[] tDownVals))
        {
            tUpVals[0] = subVal;
            tDownVals[0] = subVal;
            tSpds[0] = subVal;

            for (int i = 0; i < 7; i++)
            {
                Threshold t = prof.Thresholds[i];
                if (i > 0)
                {
                    tUpVals[TUpMap[i] + 1] = t.Tup;
                    tDownVals[i + 1] = offsetDT
                        ? (byte)(t.Tup - t.Tdown)
                        : t.Tdown;
                }
                tSpds[i + 2] = t.Speed;
            }
            return WriteWMI2("Set_Temperature", tUpVals) &
                WriteWMI2("Set_Fan", tSpds) &
                WriteWMI2("Set_Thermal", tDownVals);
        }
        return false;
    }

    public bool GetPerfMode(out PerfMode val, bool gen2)
    {
        if (ReadWMI2("Get_AP", 0, out byte[] result))
        {
            val = result[3] switch
            {
                0xC2 => PerfMode.MaxBattery,
                0xC1 => PerfMode.Silent,
                0xC0 => PerfMode.Balanced,
                0xC4 => PerfMode.Performance,
                _ => throw new NotSupportedException($"0x{result[2]:X2} cannot be converted to a performance mode."),
            };
            return true;
        }
        val = 0;
        return false;
    }
    public bool SetPerfMode(PerfMode val, bool gen2)
    {
        if (ReadWMI2("Get_AP", 1, out byte[] result))
        {
            result[0] = 1;
            result[3] = val switch
            {
                PerfMode.MaxBattery => 0xC2,
                PerfMode.Silent => 0xC1,
                PerfMode.Balanced => 0xC0,
                PerfMode.Performance => 0xC4,
                _ => throw new NotSupportedException($"0x{result[0]:X2} cannot be converted to an EC value."),
            };
            return WriteWMI2("Set_AP", result);
        }
        return false;
    }

    public bool GetFanMode(out FanMode val, bool gen2)
    {
        if (ReadWMI2("Get_AP", 1, out byte[] result))
        {
            val = result[1] switch
            {
                0x0D => FanMode.Auto,
                0x1D => FanMode.Silent,
                0x4D => FanMode.Basic,
                0x8D => FanMode.Advanced,
                _ => throw new NotSupportedException($"0x{result[0]:X2} cannot be converted to a fan mode."),
            };
            return true;
        }
        val = 0;
        return false;
    }
    public bool SetFanMode(FanMode val, bool gen2)
    {
        if (ReadWMI2("Get_AP", 1, out byte[] result))
        {
            result[0] = 1;
            result[1] = val switch
            {
                FanMode.Auto => 0x0D,
                FanMode.Silent => 0x1D,
                FanMode.Basic => 0x4D,
                FanMode.Advanced => 0x8D,
                _ => throw new NotSupportedException($"0x{result[0]:X2} cannot be converted to an EC value."),
            };
            return WriteWMI2("Set_AP", result);
        }
        return false;
    }

    public bool IsChargeLimitSupported(bool gen2)
    {
        return ReadWMI2("Get_AP", 0, out byte[] result) &&
            (result[5] & 0x80) == 0x80;
    }
    public bool GetChargeLimit(out byte val, bool gen2)
    {
        if (ReadWMI2("Get_AP", 0, out byte[] result) &&
            (result[5] & 0x80) == 0x80)
        {
            // mask off "supported" bit before returning
            val = (byte)(result[4] & 0x7F);
            return true;
        }
        val = 0;
        return false;
    }
    public bool SetChargeLimit(byte val, bool gen2)
    {
        if (ReadWMI2("Get_AP", 0, out byte[] result) &&
            (result[5] & 0x80) == 0x80)
        {
            result[0] = 0;
            result[5] &= 0x80;
            result[5] |= val;
            return WriteWMI2("Set_AP", result);
        }
        return false;
    }

    public bool IsWinFnSwapSupported(bool gen2)
    {
        return gen2;
    }
    public bool GetWinFnSwap(out bool enabled, bool gen2)
    {
        if (gen2 && ReadWMI2("Get_BIOS", 1, out byte[] result))
        {
            // mask off "supported" bit before returning
            enabled = (result[1] & 0x10) == 0x10;
            return true;
        }
        enabled = false;
        return false;
    }
    public bool SetWinFnSwap(bool enabled, bool gen2)
    {
        if (gen2 && ReadWMI2("Get_BIOS", 1, out byte[] result))
        {
            result[0] = 1;
            result[1] &= 0xEF;
            result[1] |= enabled ? (byte)0x10 : (byte)0;
            return WriteWMI2("Set_BIOS", result);
        }
        return false;
    }

    public bool IsKeyLightSupported(bool gen2)
    {
        return ReadWMI2("Get_AP", 0, out byte[] result) &&
            (result[4] & 0x80) == 0x80;
    }
    public bool GetKeyLight(out byte val, bool gen2)
    {
        if (ReadWMI2("Get_AP", 0, out byte[] result) &&
            (result[4] & 0x80) == 0x80)
        {
            val = (byte)(result[4] & 0x7F);
            return true;
        }
        val = 0;
        return false;
    }
    public bool SetKeyLight(byte val, bool gen2)
    {
        if (gen2 && ReadWMI2("Get_AP", 0, out byte[] result))
        {
            result[0] = 0;
            result[4] &= 0x80;
            result[4] |= val;
            return WriteWMI2("Set_AP", result);
        }
        return false;
    }

    public bool GetFanSpeed(bool gpu, out byte val)
    {
        if (ReadWMI2("Get_Fan", gpu ? (byte)2 : (byte)1, out byte[] result))
        {
            val = result[1];
            return true;
        }
        val = 0;
        return false;
    }
    public bool GetTemp(bool gpu, out byte val)
    {
        if (ReadWMI2("Get_Temperature", 0, out byte[] result))
        {
            val = result[gpu ? (byte)2 : (byte)1];
            return true;
        }
        val = 0;
        return false;
    }
    public int[] GetFanRPMs()
    {
        if (ReadWMI2("Get_Fan", 0, out byte[] result))
        {
            int[] rpms = new int[4];
            for (int i = 0; i < rpms.Length; i++)
            {
                int rpm = (result[2 * i + 1] << 8) | result[2 * i + 2];
                rpms[i] = rpm > 0 ? 478000 / rpm : -1;
            }
            return rpms;
        }
        return null;
    }

    public bool GetFullBlast(out bool enabled)
    {
        if (ReadWMI2("Get_Thermal", 3, out byte[] result))
        {
            enabled = (result[1] & 0x80) == 0x80;
            return true;
        }
        enabled = false;
        return false;
    }
    public bool SetFullBlast(bool enabled)
    {
        if (ReadWMI2("Get_Thermal", 3, out byte[] result))
        {
            result[0] = 3;
            result[1] = enabled
                ? (byte)(result[1] | 0x80)
                : (byte)(result[1] & 0x7F);

            return WriteWMI2("Set_Thermal", result);
        }
        return false;
    }

    public bool ReadECByte(byte reg, out byte val)
    {
        throw new NotSupportedException(
            "WMI backend does not support arbitrary EC reads/writes.");
    }
    public bool WriteECByte(byte reg, byte val)
    {
        throw new NotSupportedException(
            "WMI backend does not support arbitrary EC reads/writes.");
    }

    public void Dispose()
    {
        Instance?.Dispose();
        Params?.Dispose();
        Instance = null;
        Params = null;
    }
}
