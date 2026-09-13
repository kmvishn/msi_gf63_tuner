using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using YAMDCC.Common.Configs;
using YAMDCC.Common.Logs;
using YAMDCC.ECAccess;
using YAMDCC.IPC;

namespace YAMDCC.Service.FanControllers;

/// <summary>
/// An implementation of an MSI laptop fan controller
/// using the WinRing0 driver (via YAMDCC.ECAccess).
/// </summary>
/// <remarks>
/// The original method of applying YAMDCC's settings,
/// however the <see cref="WmiFanController"/> is
/// intended to replace it for most users.
/// </remarks>
internal sealed class Ring0FanController : IFanController
{
    private readonly Logger Log;
    private readonly EC _EC = new();

    #region EC register definitions
    private const byte CpuFanSpeedReg = 0x71;
    private const byte GpuFanSpeedReg = 0x89;
    private const byte CpuTempReg = 0x68;
    private const byte GpuTempReg = 0x80;
    private readonly byte[] RpmRegs = [0xC8, 0xCA, 0xCC, 0xCE];

    private readonly byte[] CpuTupRegs = [0x6A, 0x6B, 0x6C, 0x6D, 0x6E, 0x6F];
    private readonly byte[] CpuTdownRegs = [0x7A, 0x7B, 0x7C, 0x7D, 0x7E, 0x7F];
    private readonly byte[] CpuFanSpeedRegs = [0x72, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78];

    private readonly byte[] GpuTupRegs = [0x82, 0x83, 0x84, 0x85, 0x86, 0x87];
    private readonly byte[] GpuTdownRegs = [0x92, 0x93, 0x94, 0x95, 0x96, 0x97];
    private readonly byte[] GpuFanSpeedRegs = [0x8A, 0x8B, 0x8C, 0x8D, 0x8E, 0x8F, 0x90];

    private const byte FullBlastReg = 0x98;

    private const byte ChargeLimRegV1 = 0xEF;
    private const byte PerfModeRegV1 = 0xF2;
    private const byte FanModeRegV1 = 0xF4;
    private const byte KeyLightRegV1 = 0xF3;
    // Win/Fn swap is not supported on gen-2 ECs

    private const byte ChargeLimRegV2 = 0xD7;
    private const byte PerfModeRegV2 = 0xD2;
    private const byte FanModeRegV2 = 0xD4;
    private const byte KeyLightRegV2 = 0xD3;
    private const byte KeySwapRegV2 = 0xE8;

    #endregion

    public Ring0FanController(Logger log)
    {
        Log = log;
    }

    public bool Init()
    {
        // Install WinRing0 to get EC access
        Log.Info(Strings.GetString("drvLoad"), nameof(Ring0FanController));
        if (!_EC.LoadDriver())
        {
            int err = _EC.GetDriverError();
            Log.Fatal(Strings.GetString("drvLoadFail"), nameof(Ring0FanController));
            _EC.UnloadDriver();
            throw new Win32Exception($"{new Win32Exception(err)} ({err})");
        }
        Log.Info(Strings.GetString("drvLoadSuccess"), nameof(Ring0FanController));
        return true;
    }

    public void Deinit()
    {
        // Uninstall WinRing0 to keep things clean
        Log.Info(Strings.GetString("drvUnload"), nameof(Ring0FanController));
        _EC.UnloadDriver();
    }

    public EcInfo GetEcFirmwareInfo()
    {
        EcInfo ecInfo = new();
        if (_EC.ReadString(0xA0, 0xC, out string ecVer) && ecVer.Length == 0xC)
        {
            ecInfo.Version = ecVer;
            Log.Debug($"EC firmware version: {ecVer}", nameof(Ring0FanController));
        }
        if (_EC.ReadString(0xAC, 0x10, out string ecDate) && ecDate.Length == 0x10)
        {
            try
            {
                string temp = $"{ecDate.Substring(4, 4)}-{ecDate.Substring(0, 2)}-{ecDate.Substring(2, 2)}" +
                $"T{ecDate.Substring(8, 2).Replace(' ', '0')}:{ecDate.Substring(11, 2)}:{ecDate.Substring(14, 2)}";
                ecInfo.Date = DateTime.ParseExact(temp, "s", CultureInfo.InvariantCulture);
                Log.Debug($"EC firmware date: {ecInfo.Date:G}", nameof(Ring0FanController));
            }
            catch (FormatException ex)
            {
                Log.Error($"Failed to parse EC firmware date: {ex.Message}", nameof(Ring0FanController));
                Log.Debug($"EC firmware date (raw): {ecDate}", nameof(Ring0FanController));
            }
        }
        return ecInfo;
    }

    public FanProf GetFanProf(bool gpu, bool offsetDT = true)
    {
        Log.Info(Strings.GetString("svcReadProfs", gpu ? "GPU" : "CPU"),
            nameof(Ring0FanController));

        FanProf prof = new()
        {
            Thresholds = new List<Threshold>(GpuFanSpeedRegs.Length),
        };

        byte[] tUpRegs = gpu ? GpuTupRegs : CpuTupRegs;
        byte[] tDownRegs = gpu ? GpuTdownRegs : CpuTdownRegs;
        byte[] speedRegs = gpu ? GpuFanSpeedRegs : CpuFanSpeedRegs;

        for (int j = 0; j < prof.Thresholds.Count; j++)
        {
            prof.Thresholds[j] = new();
            Threshold t = prof.Thresholds[j];

            if (ReadECByte(speedRegs[j], out byte value))
            {
                t.Speed = value;
            }

            if (j == 0)
            {
                t.Tup = 0;
                t.Tdown = 0;
            }
            else
            {
                if (ReadECByte(tUpRegs[j - 1], out value))
                {
                    t.Tup = value;
                }
                if (ReadECByte(tDownRegs[j - 1], out value))
                {
                    t.Tdown = offsetDT
                        ? (byte)(t.Tup - value)
                        : value;
                }
            }
        }
        return prof;
    }
    public bool SetFanProf(FanProf prof, bool gpu, bool offsetDT)
    {
        Log.Info(Strings.GetString("svcWriteFanConfs", gpu ? "GPU" : "CPU"),
            nameof(Ring0FanController));
        bool success = true;

        byte[] tUpRegs = gpu ? GpuTupRegs : CpuTupRegs;
        byte[] tDownRegs = gpu ? GpuTdownRegs : CpuTdownRegs;
        byte[] speedRegs = gpu ? GpuFanSpeedRegs : CpuFanSpeedRegs;
        for (int i = 0; i < prof.Thresholds.Count; i++)
        {
            Threshold t = prof.Thresholds[i];
            if (!WriteECByte(speedRegs[i], t.Speed))
            {
                success = false;
            }
            if (i > 0)
            {
                if (!WriteECByte(tUpRegs[i - 1], t.Tup))
                {
                    success = false;
                }
                byte downT = offsetDT
                    ? (byte)(t.Tup - t.Tdown)
                    : t.Tdown;

                if (!WriteECByte(tDownRegs[i - 1], downT))
                {
                    success = false;
                }
            }
        }
        return success;
    }

    public bool GetPerfMode(out PerfMode mode, bool gen2)
    {
        bool success = ReadECByte(gen2 ? PerfModeRegV2 : PerfModeRegV1, out byte val);
        mode = val switch
        {
            0xC2 => PerfMode.MaxBattery,
            0xC1 => PerfMode.Silent,
            0xC0 => PerfMode.Balanced,
            0xC4 => PerfMode.Performance,
            _ => throw new NotSupportedException($"0x{val:X2} cannot be converted to a performance mode."),
        };
        return success;
    }
    public bool SetPerfMode(PerfMode mode, bool gen2)
    {
        byte val = mode switch
        {
            PerfMode.MaxBattery => 0xC2,
            PerfMode.Silent => 0xC1,
            PerfMode.Balanced => 0xC0,
            PerfMode.Performance => 0xC4,
            _ => throw new NotSupportedException($"{mode} cannot be converted to an EC value."),
        };
        return WriteECByte(gen2 ? PerfModeRegV2 : PerfModeRegV1, val);
    }

    public bool GetFanMode(out FanMode mode, bool gen2)
    {
        bool success = ReadECByte(gen2 ? FanModeRegV2 : FanModeRegV1, out byte val);
        mode = val switch
        {
            0x0D => FanMode.Auto,
            0x1D => FanMode.Silent,
            0x4D => FanMode.Basic,
            0x8D => FanMode.Advanced,
            _ => throw new NotSupportedException($"0x{val:X2} cannot be converted to a fan mode."),
        };
        return success;
    }
    public bool SetFanMode(FanMode mode, bool gen2)
    {
        byte val = mode switch
        {
            FanMode.Auto => 0x0D,
            FanMode.Silent => 0x1D,
            FanMode.Basic => 0x4D,
            FanMode.Advanced => 0x8D,
            _ => throw new NotSupportedException($"{mode:X2} cannot be converted to an EC value."),
        };
        return WriteECByte(gen2 ? FanModeRegV2 : FanModeRegV1, val);
    }

    public bool IsChargeLimitSupported(bool gen2)
    {
        return ReadECByte(gen2 ? ChargeLimRegV2 : ChargeLimRegV1, out byte val) &&
            (val & 0x80) == 0x80;
    }
    public bool GetChargeLimit(out byte val, bool gen2)
    {
        if (ReadECByte(gen2 ? ChargeLimRegV2 : ChargeLimRegV1, out val) &&
            (val & 0x80) == 0x80)
        {
            // mask off "supported" bit before returning
            val &= 0x7F;
            return true;
        }
        return false;
    }
    public bool SetChargeLimit(byte val, bool gen2)
    {
        return WriteECByte(gen2 ? ChargeLimRegV2 : ChargeLimRegV1,
            (byte)(val | 0x80));
    }

    public bool IsWinFnSwapSupported(bool gen2)
    {
        return gen2;
    }
    public bool GetWinFnSwap(out bool enabled, bool gen2)
    {
        if (gen2 && ReadECByte(KeySwapRegV2, out byte val))
        {
            enabled = (val & 0x10) == 0x10;
            return true;
        }
        enabled = false;
        return false;
    }
    public bool SetWinFnSwap(bool enabled, bool gen2)
    {
        return gen2 && WriteECByte(KeySwapRegV2, (byte)(enabled ? 0x10 : 0));
    }

    public bool IsKeyLightSupported(bool gen2)
    {
        return ReadECByte(gen2 ? KeyLightRegV2 : KeyLightRegV1, out byte val) &&
            (val & 0x80) == 0x80;
    }
    public bool GetKeyLight(out byte val, bool gen2)
    {
        if (ReadECByte(gen2 ? KeyLightRegV2 : KeyLightRegV1, out val) &&
            (val & 0x80) == 0x80)
        {
            val &= 0x7F;
            return true;
        }
        return false;
    }
    public bool SetKeyLight(byte val, bool gen2)
    {
        if (ReadECByte(gen2 ? KeyLightRegV2 : KeyLightRegV1, out byte oldVal) &&
            (oldVal & 0x80) == 0x80)
        {
            oldVal = (byte)(val | 0x80);
            return WriteECByte(gen2 ? KeyLightRegV2 : KeyLightRegV1, oldVal);
        }
        return false;
    }

    public bool GetFanSpeed(bool gpu, out byte val)
    {
        return ReadECByte(gpu ? GpuFanSpeedReg : CpuFanSpeedReg, out val);
    }
    public bool GetTemp(bool gpu, out byte val)
    {
        return ReadECByte(gpu ? GpuTempReg : CpuTempReg, out val);
    }
    public int[] GetFanRPMs()
    {
        int[] vals = new int[RpmRegs.Length];
        for (int i = 0; i < RpmRegs.Length; i++)
        {
            vals[i] = ReadECWord(RpmRegs[i], out ushort rpm, true) && rpm > 0
                ? 478000 / rpm : -1;
        }
        return vals;
    }

    public bool GetFullBlast(out bool enabled)
    {
        if (ReadECByte(FullBlastReg, out byte val))
        {
            enabled = (val & 0x80) == 0x80;
            return true;
        }
        enabled = false;
        return false;
    }
    public bool SetFullBlast(bool enabled)
    {
        if (ReadECByte(FullBlastReg, out byte val))
        {
            if (enabled)
            {
                Log.Debug("Enabling Full Blast...", nameof(Ring0FanController));
                val |= 0x80;
            }
            else
            {
                Log.Debug("Disabling Full Blast...", nameof(Ring0FanController));
                val &= 0x7F;
            }

            if (WriteECByte(FullBlastReg, val))
            {
                return true;
            }
        }
        return false;
    }

    public bool ReadECByte(byte reg, out byte val)
    {
        bool success = _EC.ReadByte(reg, out val);
        if (success)
        {
            Log.Debug(Strings.GetString("svcECRead", reg, val),
                nameof(Ring0FanController));
        }
        else
        {
            Log.Error(Strings.GetString("errECRead", reg,
                GetWin32Error(_EC.GetDriverError())), nameof(Ring0FanController));
        }
        return success;
    }

    public bool WriteECByte(byte reg, byte val)
    {
        bool success = _EC.WriteByte(reg, val);
        if (success)
        {
            Log.Debug(Strings.GetString("svcECWrote", reg),
                nameof(Ring0FanController));
        }
        else
        {
            Log.Error(Strings.GetString("errECWrite", reg,
                GetWin32Error(_EC.GetDriverError())), nameof(Ring0FanController));
        }
        return success;
    }

    private bool ReadECWord(byte reg, out ushort val, bool bigEndian)
    {
        bool success = _EC.ReadWord(reg, out val, bigEndian);
        if (success)
        {
            Log.Debug(Strings.GetString("svcECRead", reg, val),
                nameof(Ring0FanController));
        }
        else
        {
            Log.Error(Strings.GetString("errECRead", reg,
                GetWin32Error(_EC.GetDriverError())), nameof(Ring0FanController));
        }
        return success;
    }

    private static string GetWin32Error(int error)
    {
        return new Win32Exception(error).Message;
    }
}
