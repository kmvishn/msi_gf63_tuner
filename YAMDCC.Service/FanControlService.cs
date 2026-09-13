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
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.ServiceProcess;
using System.Text;
using System.Timers;
using YAMDCC.Common;
using YAMDCC.Common.Configs;
using YAMDCC.Common.Logs;
using YAMDCC.IPC;
using YAMDCC.Service.FanControllers;

namespace YAMDCC.Service;

internal sealed class FanControlService : ServiceBase
{
    #region Fields

    /// <summary>
    /// The currently loaded YAMDCC config.
    /// </summary>
    private YamdccCfg Config;

    /// <summary>
    /// The named message pipe server that YAMDCC connects to.
    /// </summary>
    private readonly NamedPipeServer<ServiceCommand, ServiceResponse> IPCServer;

    private readonly Logger Log;

    private readonly IFanController FC;

    private readonly Timer CooldownTimer = new(1000);

    private EcInfo EcInfo;

    private bool FullBlastEnabled;
    #endregion

    /// <summary>
    /// Initialises a new instance of the <see cref="FanControlService"/> class.
    /// </summary>
    /// <param name="logger">
    /// The <see cref="Logger"/> instance to write logs to.
    /// </param>
    public FanControlService(Logger logger)
    {
        CanHandlePowerEvent = true;
        CanShutdown = true;

        Log = logger;
        FC = CommonConfig.GetUseWMI()
            ? new Wmi2FanController(logger)
            : new Ring0FanController(logger);

        PipeSecurity security = new();
        // use SDDL descriptor since not everyone uses english Windows.
        // the SDDL descriptor should be roughly equivalent to the old
        // behaviour (commented out below):
        // security.AddAccessRule(new PipeAccessRule(
        //     "Administrators", PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.SetSecurityDescriptorSddlForm("O:BAG:SYD:(A;;GA;;;SY)(A;;GRGW;;;BA)");

        CooldownTimer.Elapsed += new ElapsedEventHandler(CooldownElapsed);

        IPCServer = new NamedPipeServer<ServiceCommand, ServiceResponse>("YAMDCC-Server", security);
        IPCServer.ClientConnected += new EventHandler<PipeConnectionEventArgs<ServiceCommand, ServiceResponse>>(IPCClientConnect);
        IPCServer.ClientDisconnected += new EventHandler<PipeConnectionEventArgs<ServiceCommand, ServiceResponse>>(IPCClientDisconnect);
        IPCServer.Error += new EventHandler<PipeErrorEventArgs<ServiceCommand, ServiceResponse>>(IPCServerError);
    }

    #region Events
    protected override void OnStart(string[] args)
    {
        try
        {
            Log.Info(Strings.GetString("svcStarting"));

            // Don't try and start if MSI Center's services are running.
            // It is still possible to start MSI Center *after* YAMDCC Service,
            // but it is not recommended and will cause issues.
            if (Utils.IsMSIServiceRunning(out string[] svcs))
            {
                StringBuilder sb = new();
                foreach (string svc in svcs)
                {
                    sb.Append($"- {svc}");
                }

                ExitCode = 1;
                throw new InvalidOperationException(
                    $"The following MSI Center services are running:\n{sb}\n" +
                    "Uninstall MSI Center or disable the above services to use YAMDCC.");
            }

            try
            {
                FC.Init();
            }
            catch (Win32Exception)
            {
                ExitCode = 1;
                throw;
            }

            // Load the last applied YAMDCC config.
            bool confLoaded = LoadConf();

            // Set up IPC server
            Log.Info("Starting IPC server...");
            IPCServer.Start();

            Log.Info(Strings.GetString("svcStarted"));

            // Attempt to read default fan profile if it's pending:
            if (CommonConfig.GetECtoConfState() == ECtoConfState.PostReboot)
            {
                ECtoConf();
            }

            // Apply the fan profiles and charging threshold:
            if (confLoaded)
            {
                ApplyConf();
            }
        }
        catch (Exception ex)
        {
            Log.Fatal(Strings.GetString("svcException", ex));
            throw;
        }
    }

    private void CooldownElapsed(object sender, ElapsedEventArgs e)
    {
        CooldownTimer.Stop();
    }

    protected override void OnStop()
    {
        StopSvc();
    }

    protected override void OnShutdown()
    {
        if (CommonConfig.GetECtoConfState() == ECtoConfState.PendingReboot)
        {
            CommonConfig.SetECtoConfState(ECtoConfState.PostReboot);
        }
        StopSvc();
    }

    private void StopSvc()
    {
        // disable Full Blast if it was enabled while running
        SetFullBlast(0);

        Log.Info(Strings.GetString("svcStopping"));

        // Stop the IPC server:
        Log.Info("Stopping IPC server...");
        IPCServer.Stop();

        FC.Deinit();

        Log.Info(Strings.GetString("svcStopped"));
    }

    protected override bool OnPowerEvent(PowerBroadcastStatus powerStatus)
    {
        switch (powerStatus)
        {
            case PowerBroadcastStatus.ResumeCritical:
            case PowerBroadcastStatus.ResumeSuspend:
            case PowerBroadcastStatus.ResumeAutomatic:
                if (!CooldownTimer.Enabled)
                {
                    // fan settings get reset on sleep/restart
                    FullBlastEnabled = false;
                    // Re-apply the fan profiles after waking up from sleep:
                    Log.Info(Strings.GetString("svcWake"));
                    ApplyConf();
                    CooldownTimer.Start();
                }
                break;
        }
        return true;
    }

    private void IPCClientConnect(object sender, PipeConnectionEventArgs<ServiceCommand, ServiceResponse> e)
    {
        e.Connection.ReceiveMessage += new EventHandler<PipeMessageEventArgs<ServiceCommand, ServiceResponse>>(IPCClientMessage);
        Log.Info(Strings.GetString("ipcConnect", e.Connection.ID));
    }

    private void IPCClientDisconnect(object sender, PipeConnectionEventArgs<ServiceCommand, ServiceResponse> e)
    {
        e.Connection.ReceiveMessage -= new EventHandler<PipeMessageEventArgs<ServiceCommand, ServiceResponse>>(IPCClientMessage);
        Log.Info(Strings.GetString("ipcDC", e.Connection.ID));
    }

    private void IPCServerError(object sender, PipeErrorEventArgs<ServiceCommand, ServiceResponse> e)
    {
        Log.Error(Strings.GetString("ipcError", e.Connection.ID, e.Exception));
    }

    private void IPCClientMessage(object sender, PipeMessageEventArgs<ServiceCommand, ServiceResponse> e)
    {
        bool parseSuccess = false,
            cmdSuccess = false,
            sendSuccessMsg = true;

        Command cmd = e.Message.Command;
        object[] args = e.Message.Arguments;
        int id = e.Connection.ID;

        switch (cmd)
        {
            case Command.Nothing:
                Log.Warn("Empty command received!");
                return;
            case Command.GetServiceVer:
                IPCServer.PushMessage(new ServiceResponse(
                    Response.ServiceVer, Utils.GetRevision()), id);
                return;
            case Command.GetFirmVer:
            {
                parseSuccess = true;
                sendSuccessMsg = false;
                cmdSuccess = GetFirmVer(id);
                break;
            }
            case Command.ReadECByte:
            {
                if (args.Length == 1 && args[0] is byte reg)
                {
                    parseSuccess = true;
                    sendSuccessMsg = false;
                    cmdSuccess = FC.ReadECByte(reg, out byte value);
                    if (cmdSuccess)
                    {
                        IPCServer.PushMessage(new ServiceResponse(
                            Response.ReadResult, reg, value), id);
                    }
                }
                break;
            }
            case Command.WriteECByte:
            {
                if (args.Length == 2 && args[0] is byte reg && args[1] is byte value)
                {
                    parseSuccess = true;
                    cmdSuccess = FC.WriteECByte(reg, value);
                }
                break;
            }
            case Command.ApplyConf:
                parseSuccess = true;
                cmdSuccess = LoadConf() && ApplyConf();
                break;
            case Command.SetFullBlast:
            {
                if (args.Length == 1 && args[0] is int enable)
                {
                    parseSuccess = true;
                    cmdSuccess = SetFullBlast(enable);
                }
                break;
            }
            case Command.GetFanSpeeds:
            {
                parseSuccess = true;
                sendSuccessMsg = false;
                cmdSuccess = GetFanSpeeds(id);
                break;
            }
            case Command.GetFanRPMs:
            {
                parseSuccess = true;
                sendSuccessMsg = false;
                cmdSuccess = GetFanRPMs(id);
                break;
            }
            case Command.GetTemps:
            {
                parseSuccess = true;
                sendSuccessMsg = false;
                cmdSuccess = GetTemps(id);
                break;
            }
            case Command.GetKeyLightSupported:
                parseSuccess = true;
                sendSuccessMsg = false;
                cmdSuccess = GetKeyLightSupported(id);
                break;
            case Command.GetKeyLightBright:
                parseSuccess = true;
                sendSuccessMsg = false;
                cmdSuccess = GetKeyLight(id);
                break;
            case Command.SetKeyLightBright:
            {
                if (args.Length == 1 && args[0] is byte brightness)
                {
                    parseSuccess = true;
                    cmdSuccess = SetKeyLight(brightness);
                }
                break;
            }
            case Command.GetKeySwapSupported:
                parseSuccess = true;
                sendSuccessMsg = false;
                cmdSuccess = true;
                IPCServer.PushMessage(new ServiceResponse(
                    Response.KeySwapSupported, FC.IsWinFnSwapSupported(Config.IsNewEC)));
                break;
            case Command.SetKeySwap:
            {
                if (args.Length == 1 && args[0] is int enable)
                {
                    parseSuccess = true;
                    if (enable == -1)
                    {
                        Config.KeySwapEnabled = !Config.KeySwapEnabled;
                    }
                    else if (enable == 0)
                    {
                        Config.KeySwapEnabled = false;
                    }
                    else if (enable == 1)
                    {
                        Config.KeySwapEnabled = true;
                    }
                    else
                    {
                        parseSuccess = false;
                    }
                    if (parseSuccess)
                    {
                        cmdSuccess = SetWinFnSwap();
                    }
                }
                break;
            }
            case Command.SetFanProf:
            {
                if (args.Length == 1 && args[0] is int fanProf)
                {
                    parseSuccess = true;
                    // TODO: make nicer
                    foreach (FanConf cfg in new FanConf[] { Config.CpuFan, Config.GpuFan })
                    {
                        if (fanProf < 0)
                        {
                            if (Config.CpuFan.ProfSel >= cfg.FanProfs.Count - 1)
                            {
                                cfg.ProfSel = 0;
                            }
                            else
                            {
                                cfg.ProfSel++;
                            }
                        }
                        else
                        {
                            cfg.ProfSel = fanProf;
                        }
                    }
                    cmdSuccess = ApplyConf();
                }
                break;
            }
            case Command.SetPerfMode:
            {
                if (args.Length == 1 && args[0] is int perfMode)
                {
                    parseSuccess = true;
                    if (perfMode < 0)
                    {
                        if (Config.PerfMode == PerfMode.Performance)
                        {
                            Config.PerfMode = PerfMode.MaxBattery;
                        }
                        else
                        {
                            Config.PerfMode++;
                        }
                    }
                    else
                    {
                        Config.PerfMode = (PerfMode)perfMode;
                    }
                    cmdSuccess = ApplyConf();
                }
                break;
            }
            default:    // Unknown command
                Log.Error(Strings.GetString("errBadCmd", cmd));
                break;
        }

        if (!cmdSuccess)
        {
            if (!parseSuccess)
            {
                Log.Error(Strings.GetString("errBadArgs", cmd, args));
            }
            IPCServer.PushMessage(new ServiceResponse(
                Response.Error, (int)cmd), id);
        }
        else if (sendSuccessMsg)
        {
            IPCServer.PushMessage(new ServiceResponse(
                Response.Success, (int)cmd), id);
        }
    }
    #endregion

    private bool LoadConf(int? clientID = null)
    {
        Log.Info(Strings.GetString("cfgLoading"));

        try
        {
            Config = YamdccCfg.Load(Paths.CurrentConfV2);
            Log.Info(Strings.GetString("cfgLoaded"));

            if (clientID is not null)
            {
                IPCServer?.PushMessage(new ServiceResponse(
                    Response.ConfLoaded, clientID.Value));
            }

            EcInfo = FC.GetEcFirmwareInfo();
            return true;
        }
        catch (Exception ex)
        {
            if (ex is InvalidConfigException or InvalidOperationException)
            {
                Log.Error(Strings.GetString("cfgInvalid"));
            }
            else if (ex is FileNotFoundException)
            {
                Log.Warn(Strings.GetString("cfgNotFound"));
            }
            else
            {
                throw;
            }
            Config = null;
            return false;
        }
    }

    private bool ApplyConf()
    {
        if (Config is null)
        {
            return false;
        }

        Log.Info(Strings.GetString("cfgApplying"));
        bool success = true;

        // Write the fan profile to the appropriate registers for each fan:
        FC.SetFanProf(Config.CpuFan.FanProfs[Config.CpuFan.ProfSel], false, Config.OffsetDT);
        // NOTE: this must index GpuFan, not CpuFan - the original read
        // Config.CpuFan.FanProfs[Config.GpuFan.ProfSel], which applied the
        // CPU curve to the GPU fan (and threw IndexOutOfRangeException
        // whenever the GPU had more fan profiles than the CPU).
        FC.SetFanProf(Config.GpuFan.FanProfs[Config.GpuFan.ProfSel], true, Config.OffsetDT);

        // Write the performance mode
        Log.Info(Strings.GetString("svcWritePerfMode"));
        PerfMode pMode = Config.CpuFan.FanProfs[Config.CpuFan.ProfSel].PerfMode == PerfMode.Default
            ? Config.PerfMode
            : Config.CpuFan.FanProfs[Config.CpuFan.ProfSel].PerfMode;

        if (!FC.SetPerfMode(pMode, Config.IsNewEC))
        {
            success = false;
        }

        // Write the charge threshold:
        Log.Info(Strings.GetString("svcWriteChgLim"));
        if (!FC.SetChargeLimit(Config.ChargeLim, Config.IsNewEC))
        {
            success = false;
        }

        // Write the fan mode
        Log.Info(Strings.GetString("svcWriteFanMode"));
        if (!FC.SetFanMode(Config.FanMode, Config.IsNewEC))
        {
            success = false;
        }

        // Write the Win/Fn key swap setting
        if (!SetWinFnSwap())
        {
            success = false;
        }
        return success;
    }

    private bool SetWinFnSwap()
    {
        Log.Info(Strings.GetString("svcWriteKeySwap"));
        return FC.SetWinFnSwap(Config.KeySwapEnabled, Config.IsNewEC);
    }

    private bool GetFanSpeeds(int clientId)
    {
        if (FC.GetFanSpeed(false, out byte cpuSpeed) &&
            FC.GetFanSpeed(true, out byte gpuSpeed))
        {
            IPCServer.PushMessage(new ServiceResponse(
                Response.FanSpeeds, cpuSpeed, gpuSpeed), clientId);
            return true;
        }
        return false;
    }

    private bool GetFanRPMs(int clientId)
    {
        // All known fan RPM registers on MSI laptops
        int[] rpmVals = FC.GetFanRPMs();

        if (rpmVals is null)
        {
            return false;
        }
        else
        {
            IPCServer.PushMessage(new ServiceResponse(
                Response.FanRPMs, rpmVals), clientId);
            return true;
        }
    }

    private bool GetTemps(int clientId)
    {
        if (FC.GetTemp(false, out byte cpuTemp) &&
            FC.GetTemp(true, out byte gpuTemp))
        {
            IPCServer.PushMessage(new ServiceResponse(
                Response.Temps, cpuTemp, gpuTemp), clientId);
            return true;
        }
        return false;
    }

    private bool SetFullBlast(int enable)
    {
        bool oldFbEnable = FullBlastEnabled;

        if (enable == -1)
        {
            FullBlastEnabled = !FullBlastEnabled;
        }
        else if (enable == 0)
        {
            FullBlastEnabled = false;
        }
        else if (enable == 1)
        {
            FullBlastEnabled = true;
        }
        else
        {
            // invalid Full Blast value
            return false;
        }

        if (FC.SetFullBlast(FullBlastEnabled))
        {
            return true;
        }
        else
        {
            // failed to change full blast state; revert to old full blast enabled
            FullBlastEnabled = oldFbEnable;
            return false;
        }
    }

    private bool GetKeyLightSupported(int clientId)
    {
        if (Config is null)
        {
            return false;
        }

        IPCServer.PushMessage(new ServiceResponse(Response.KeyLightSupported,
            FC.IsKeyLightSupported(Config.IsNewEC)), clientId);
        return true;
    }

    private bool GetKeyLight(int clientId)
    {
        if (Config is null)
        {
            return false;
        }

        Log.Debug(Strings.GetString("svcGetKeyLight"));

        if (FC.GetKeyLight(out byte brightness, Config.IsNewEC))
        {
            brightness &= 0x7F;

            IPCServer.PushMessage(new ServiceResponse(
                Response.KeyLightBright, brightness), clientId);
            return true;
        }
        return false;
    }

    private bool SetKeyLight(byte brightness)
    {
        if (Config is null)
        {
            return false;
        }

        Log.Debug(Strings.GetString("svcSetKeyLight", brightness));
        return brightness >= 0 && brightness <= 3 &&
            FC.SetKeyLight(brightness, Config.IsNewEC);
    }

    private bool GetFirmVer(int clientId)
    {
        Log.Debug(Strings.GetString("svcGerFirmVer", clientId));
        IPCServer.PushMessage(new ServiceResponse(Response.FirmVer, EcInfo), clientId);
        return true;
    }

    private bool ECtoConf()
    {
        if (Config is null)
        {
            return false;
        }

        try
        {
            Log.Info(Strings.GetString("svcReadModel"));

            string pcManufacturer = Utils.GetPCManufacturer(),
                pcModel = Utils.GetPCModel();

            if (string.IsNullOrEmpty(pcManufacturer))
            {
                Log.Error(Strings.GetString("errReadManufacturer"));
            }
            else
            {
                Config.Manufacturer = pcManufacturer;
            }

            if (string.IsNullOrEmpty(pcModel))
            {
                Log.Error(Strings.GetString("errReadModel"));
            }
            else
            {
                Config.Model = pcModel;
            }

            Config.FirmVer = EcInfo.Version;
            Config.FirmDate = EcInfo.Date;

            FanProf cpuProf = GetDefaultFanProf(false),
                gpuProf = GetDefaultFanProf(true);

            if (cpuProf is null || gpuProf is null)
            {
                // never save a config with a missing default profile:
                // the service would apply an empty fan curve on next start.
                Log.Error("EC-to-config aborted: could not read the default fan profiles.");
                CommonConfig.SetECtoConfState(ECtoConfState.Fail);
                return false;
            }

            Config.CpuFan.FanProfs[0] = cpuProf;
            Config.GpuFan.FanProfs[0] = gpuProf;

            Log.Info("Saving config...");
            Config.Save(Paths.CurrentConfV2);

            CommonConfig.SetECtoConfState(ECtoConfState.Success);
            return true;
        }
        catch
        {
            CommonConfig.SetECtoConfState(ECtoConfState.Fail);
            return false;
        }
    }

    private FanProf GetDefaultFanProf(bool gpu)
    {
        if (Config is null)
        {
            return null;
        }

        Log.Info(Strings.GetString("svcReadProfs", gpu ? "GPU" : "CPU"));
        FanProf prof = FC.GetFanProf(gpu, Config.OffsetDT);

        if (prof is null)
        {
            // don't NullReferenceException into the caller's catch-all,
            // which would report a generic EC-to-config failure and hide
            // the fact that the EC read itself was what failed.
            Log.Error($"Failed to read the default {(gpu ? "GPU" : "CPU")} fan profile from the EC.");
            return null;
        }

        prof.Name = "Default";
        prof.Desc = Strings.GetString("DefaultDesc", gpu ? "GPU" : "CPU");

        return prof;
    }
}
