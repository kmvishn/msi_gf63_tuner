using System;
using System.Runtime.InteropServices;
using YAMDCC.Common.Logs;

namespace YAMDCC.Service;

/// <summary>
/// Provides lightweight OS-level system control actions (Airplane Mode, Sleep)
/// executed directly without external bloatware.
/// </summary>
internal static class SystemControl
{
    private static readonly Guid CLSID_AirplaneModeManager = new("581333F6-28DB-41BE-BC7A-FF201F12F3F6");
    private static readonly Guid IID_IRadioManager = new("DB3AFBFB-08E6-46C6-AA70-BF9A34C30AB7");

    [DllImport("ole32.dll")]
    private static extern int CoInitialize(IntPtr pv);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [DllImport("ole32.dll")]
    private static extern uint CoCreateInstance(Guid clsid, IntPtr pv, uint ctx, Guid iid, out IntPtr ppv);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetSystemRadioStateDelegate(IntPtr cg, out int ie, out int se, out int p3);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetSystemRadioStateDelegate(IntPtr ptr, int state);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ReleaseDelegate(IntPtr ptr);

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    /// <summary>
    /// Toggles Windows Airplane / Flight mode (all radios: Wi-Fi, Bluetooth, Cellular).
    /// </summary>
    public static bool ToggleAirplaneMode(Logger log)
    {
        try
        {
            _ = CoInitialize(IntPtr.Zero);
            try
            {
                uint hr = CoCreateInstance(CLSID_AirplaneModeManager, IntPtr.Zero, 4, IID_IRadioManager, out IntPtr irm);
                if (hr != 0 || irm == IntPtr.Zero)
                {
                    log.Warn($"[SystemControl] CoCreateInstance failed for RadioManager: 0x{hr:X8}");
                    return false;
                }

                IntPtr comPtr = Marshal.ReadIntPtr(irm);
                IntPtr[] methodPtr = new IntPtr[8];
                Marshal.Copy(comPtr, methodPtr, 0, methodPtr.Length);

                var getState = Marshal.GetDelegateForFunctionPointer<GetSystemRadioStateDelegate>(methodPtr[5]);
                var setState = Marshal.GetDelegateForFunctionPointer<SetSystemRadioStateDelegate>(methodPtr[6]);
                var release = Marshal.GetDelegateForFunctionPointer<ReleaseDelegate>(methodPtr[2]);

                getState(irm, out int currentState, out _, out _);
                int newState = (currentState == 1) ? 0 : 1;

                setState(irm, newState);
                release(irm);

                log.Info($"[SystemControl] Toggled Airplane Mode: {(currentState == 1 ? "ON -> OFF" : "OFF -> ON")}");
                return true;
            }
            finally
            {
                CoUninitialize();
            }
        }
        catch (Exception ex)
        {
            log.Error($"[SystemControl] Error toggling Airplane Mode: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Puts the machine into Sleep state immediately.
    /// </summary>
    public static bool TriggerSleep(Logger log)
    {
        try
        {
            log.Info("[SystemControl] Triggering Sleep (Suspend)...");
            return SetSuspendState(false, false, false);
        }
        catch (Exception ex)
        {
            log.Error($"[SystemControl] Error putting system to sleep: {ex.Message}");
            return false;
        }
    }
}
