using YAMDCC.Common.Configs;
using YAMDCC.IPC;

namespace YAMDCC.Service.FanControllers;

/// <summary>
/// Base class for all YAMDCC fan controllers.
/// </summary>
internal interface IFanController
{
    bool Init();
    void Deinit();

    EcInfo GetEcFirmwareInfo();

    FanProf GetFanProf(bool gpu, bool offsetDT);
    bool SetFanProf(FanProf profile, bool gpu, bool offsetDT);

    bool GetPerfMode(out PerfMode val, bool gen2);
    bool SetPerfMode(PerfMode val, bool gen2);

    bool GetFanMode(out FanMode val, bool gen2);
    bool SetFanMode(FanMode val, bool gen2);

    bool IsChargeLimitSupported(bool gen2);
    bool GetChargeLimit(out byte val, bool gen2);
    bool SetChargeLimit(byte val, bool gen2);

    bool IsWinFnSwapSupported(bool gen2);
    bool GetWinFnSwap(out bool enabled, bool gen2);
    bool SetWinFnSwap(bool enabled, bool gen2);

    bool IsKeyLightSupported(bool gen2);
    bool GetKeyLight(out byte val, bool gen2);
    bool SetKeyLight(byte val, bool gen2);

    bool GetFanSpeed(bool gpu, out byte val);
    bool GetTemp(bool gpu, out byte val);
    int[] GetFanRPMs();

    bool GetFullBlast(out bool enabled);
    bool SetFullBlast(bool enabled);

    bool ReadECByte(byte reg, out byte val);
    bool WriteECByte(byte reg, byte val);
}
