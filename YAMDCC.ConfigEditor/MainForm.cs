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
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using YAMDCC.Common;
using YAMDCC.Common.Configs;
using YAMDCC.Common.Dialogs;
using YAMDCC.Common.Logs;
using YAMDCC.Common.Monitoring;
using YAMDCC.Common.UI;
using YAMDCC.IPC;

namespace YAMDCC.ConfigEditor;

internal sealed partial class MainForm : Form
{
    #region Fields
    private readonly Status AppStatus = new();

    /// <summary>
    /// The YAMDCC config that is currently open for editing.
    /// </summary>
    private YamdccCfg Config;

    /// <summary>
    /// The client that connects to the YAMDCC Service
    /// </summary>
    private readonly NamedPipeClient<ServiceResponse, ServiceCommand> IPCClient =
        new("YAMDCC-Server");

    private NumericUpDown[] numUpTs, numDownTs, numFanSpds;
    private DarkTrackBar[] tbFanSpds;

    private readonly ToolTip ttMain = new();

    private readonly Timer tmrPoll, tmrStatusReset, tmrSvcTimeout;

    private bool GPUFan;
    private int Debug;

    #region Monitoring
    /// <summary>
    /// Driver-free system sensors (CPU power/clock/load, per-GPU load and
    /// VRAM, memory) shown on the Monitoring tab alongside the EC's readings.
    /// </summary>
    private SystemSensors Sensors;

    private Label lblCpuLoad, lblCpuClock, lblCpuPkgW, lblCpuCoreW, lblRamUsed, lblSysPower;
    private Label lblFps, lblSensorSrc;

    /// <summary>
    /// Whether the Monitoring tab was on screen at the previous poll, so the
    /// power baseline can be reset when it comes back into view.
    /// </summary>
    private bool MonitorWasVisible;

    /// <summary>The always-on-top sensor readout, when enabled.</summary>
    private OverlayForm Overlay;

    /// <summary>Menu entry that toggles <see cref="Overlay"/>.</summary>
    private ToolStripMenuItem tsiOverlay;

    /// <summary>Publishes readings into RivaTuner Statistics Server's OSD.</summary>
    private readonly RtssOsd Osd = new();

    /// <summary>Menu entry that toggles the RTSS in-game OSD.</summary>
    private ToolStripMenuItem tsiOsd;

    /// <summary>
    /// Per-GPU value labels, keyed by adapter LUID: load, VRAM, power.
    /// </summary>
    private readonly Dictionary<string, (Label Load, Label Clock, Label Vram, Label Watts, Label Temp)> GpuLabels = [];

    /// <summary>
    /// The Monitoring tab page. The designer keeps it as a local inside
    /// InitializeComponent(), so it is resolved by name rather than by field.
    /// </summary>
    private TabPage TabMonitoring => tcMain.TabPages["tabECMon"];
    #endregion
    #endregion

    /// <summary>Requested by --overlay / --osd / --minimized, applied once the form loads.</summary>
    private readonly bool StartOverlay, StartOsd, StartMinimized;

    #region System Tray
    private NotifyIcon notifyIcon;
    private ContextMenuStrip cmsTray;
    private ToolStripMenuItem tmiTrayProfiles;
    private ToolStripMenuItem tmiTrayFullBlast;
    private bool allowExplicitExit;
    private bool hasShownTrayBalloon;
    #endregion

    public MainForm() : this(false, false, false) { }

    public MainForm(bool startOverlay, bool startOsd, bool startMinimized = false)
    {
        StartOverlay = startOverlay;
        StartOsd = startOsd;
        StartMinimized = startMinimized;

        InitializeComponent();

        // dark "MSI Dragon" theme (black + red). Must run after
        // InitializeComponent(), since it restyles the controls it creates.
        Theme.Apply(this);
        // ToolTip is a component, not a child control, so it needs theming
        // separately or it pops up as a white box on the dark form.
        Theme.Apply(ttMain);

        // rebuild the Monitoring and Extras tabs with modern cards
        BuildMonitoringTab();
        SetupModernExtrasTab();
        UpdateFanSelButton();
        UnifyProfileControls();
        AddOverlayMenuItem();

        // Set the window icon using the application icon.
        // Saves about 8-9 KB from not having to embed the same icon twice.
        Icon = Utils.GetEntryAssemblyIcon();

        // set title text to include program version
        Text = $"MSI GF63 Tuner v{Utils.GetVerString()}";

        // set literally every tooltip
        tsiLoadConf.ToolTipText = Strings.GetString("ttLoadConf");
        tsiSaveConf.ToolTipText = Strings.GetString("ttSaveConf");
        tsiApply.ToolTipText = Strings.GetString("ttApply");
        tsiRevert.ToolTipText = Strings.GetString("ttRevert");
        tsiExit.ToolTipText = Strings.GetString("ttExit");
        tsiProfAdd.ToolTipText = Strings.GetString("ttProfAdd");
        tsiProfRen.ToolTipText = Strings.GetString("ttProfRen");
        tsiProfChangeDesc.ToolTipText = Strings.GetString("ttProfChangeDesc");
        tsiECtoConf.ToolTipText = Strings.GetString("ttECtoConf");
        tsiProfDel.ToolTipText = Strings.GetString("ttProfDel");
        //tsiECMon.ToolTipText = Strings.GetString("ttECMon");
        tsiAdvanced.ToolTipText = Strings.GetString("ttAdvanced");
        tsiLogDebug.ToolTipText = Strings.GetString("ttLogLevel");
        tsiLogInfo.ToolTipText = Strings.GetString("ttLogLevel");
        tsiLogWarn.ToolTipText = Strings.GetString("ttLogLevel");
        tsiLogError.ToolTipText = Strings.GetString("ttLogLevel");
        tsiLogFatal.ToolTipText = Strings.GetString("ttLogLevel");
        tsiLogNone.ToolTipText = Strings.GetString("ttLogLevel");
        tsiStopSvc.ToolTipText = Strings.GetString("ttStopSvc");
        tsiUninstall.ToolTipText = Strings.GetString("ttUninstall");
        tsiAbout.ToolTipText = Strings.GetString("ttAbout");
        tsiSource.ToolTipText = Strings.GetString("ttSource");
        ttMain.SetToolTip(btnFanSel, Strings.GetString("ttFanSel"));
        ttMain.SetToolTip(btnProfAdd, Strings.GetString("ttProfAdd"));
        ttMain.SetToolTip(btnProfDel, Strings.GetString("ttProfDel"));
        ttMain.SetToolTip(btnApply, Strings.GetString("ttApply"));
        ttMain.SetToolTip(btnRevert, Strings.GetString("ttRevert"));

        tmrPoll = new()
        {
            Interval = 1000,
        };
        tmrPoll.Tick += new EventHandler(tmrPoll_Tick);

        tmrStatusReset = new()
        {
            Interval = 5000,
        };
        tmrStatusReset.Tick += new EventHandler(tmrStatusReset_Tick);

        tmrSvcTimeout = new()
        {
            Interval = 10000,
        };
        tmrSvcTimeout.Tick += new EventHandler(tmrSvcTimeout_Tick);

        tsiFBExit.Checked = CommonConfig.GetDisableFBOnExit();

        switch (CommonConfig.GetLogLevel())
        {
            case LogLevel.None:
                tsiLogNone.Checked = true;
                break;
            case LogLevel.Fatal:
                tsiLogFatal.Checked = true;
                break;
            case LogLevel.Error:
                tsiLogError.Checked = true;
                break;
            case LogLevel.Warn:
                tsiLogWarn.Checked = true;
                break;
            case LogLevel.Info:
                tsiLogInfo.Checked = true;
                break;
            case LogLevel.Debug:
                tsiLogDebug.Checked = true;
                break;
        }

        if (!CommonConfig.GetAutoUpdateAsked() && File.Exists("./Updater.exe"))
        {
            if (Utils.ShowInfo(Strings.GetString("dlgAutoUpdate"),
                "Check for updates?", MessageBoxButtons.YesNo) == DialogResult.Yes)
            {
                RunUpdater("--setautoupdate true");
            }
            CommonConfig.SetAutoUpdateAsked(true);
        }
        InitSystemTray();
        DisableAll();
    }

    #region Events
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        IPCClient.ServerMessage += new EventHandler<PipeMessageEventArgs<ServiceResponse, ServiceCommand>>(IPCMessage);
        IPCClient.Error += new EventHandler<PipeErrorEventArgs<ServiceResponse, ServiceCommand>>(IPCError);
        IPCClient.Start();

        ProgressDialog<bool> dlg = new()
        {
            Caption = "Connecting to MSI GF63 Tuner engine...",
            DoWork = () =>
            {
                if (!IPCClient.WaitForConnection(2000))
                {
                    Program.StartEmbeddedService();
                    return !IPCClient.WaitForConnection(4000);
                }
                return false;
            }
        };
        dlg.ShowDialog();

        if (dlg.Result)
        {
            throw new TimeoutException(Strings.GetString("exSvcTimeout"));
        }
        AppDomain.CurrentDomain.ProcessExit += new EventHandler(OnProcessExit);

        LoadConf(Paths.CurrentConfV2);

        ttMain.SetToolTip(tbKeyLight, Strings.GetString("ttNotSupported"));
        SendSvcMessage(new ServiceCommand(Command.GetKeyLightSupported));

        // honour the startup switches now the form is live
        if (StartOverlay) { tsiOverlay.Checked = true; }
        if (StartOsd) { tsiOsd.Checked = true; }
        if (StartMinimized)
        {
            BeginInvoke(new Action(() =>
            {
                HideToTray();
            }));
        }

        switch (CommonConfig.GetECtoConfState())
        {
            case ECtoConfState.Fail:
                Utils.ShowError(Strings.GetString("dlgECtoConfErr", Paths.Logs));
                CommonConfig.SetECtoConfState(ECtoConfState.None);
                break;
            case ECtoConfState.Success:
                Utils.ShowInfo(Strings.GetString("dlgECtoConfSuccess"), "Success");
                CommonConfig.SetECtoConfState(ECtoConfState.None);
                break;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        TrimMemory();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized)
        {
            HideToTray();
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!allowExplicitExit && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        base.OnFormClosing(e);

        if (notifyIcon != null)
        {
            notifyIcon.Visible = false;
            notifyIcon.Dispose();
            notifyIcon = null;
        }

        // hand the RTSS slot back and drop the overlay, so neither outlives
        // the program
        Osd.Clear();
        Overlay?.Close();
        // Disable Full Blast if it was enabled while the program
        // was running and the user wants it disabled on exit:
        if (chkFullBlast.Checked && CommonConfig.GetDisableFBOnExit())
        {
            SendSvcMessage(new ServiceCommand(Command.SetFullBlast, 0));
        }
    }

    private void OnProcessExit(object sender, EventArgs e)
    {
        // Close the connection to the YAMDCC
        // Service before exiting the program:
        tmrPoll.Stop();
        IPCClient.Stop();
        Program.StopEmbeddedService();
    }

    private void IPCMessage(object sender, PipeMessageEventArgs<ServiceResponse, ServiceCommand> e)
    {
        tmrSvcTimeout.Stop();
        Invoke(() =>
        {
            object[] args = e.Message.Value;
            switch (e.Message.Response)
            {
                case Response.Nothing:
                {
                    UpdateStatus(StatusCode.ServiceResponseEmpty);
                    break;
                }
                case Response.Success:
                {
                    if (args.Length != 1 || args[0] is not int cmd)
                    {
                        break;
                    }
                    switch ((Command)cmd)
                    {
                        case Command.ApplyConf:
                            ToggleSvcCmds(true);
                            UpdateStatus(StatusCode.ConfApplied);
                            SendSvcMessage(new ServiceCommand(Command.GetKeyLightSupported));
                            break;
                        case Command.SetFullBlast:
                            ToggleSvcCmds(true);
                            UpdateStatus(StatusCode.FullBlastToggled);
                            break;
                    }
                    break;
                }
                case Response.Error:
                {
                    if (args.Length == 1 && args[0] is int cmd)
                    {
                        UpdateStatus(StatusCode.ServiceCommandFail, cmd);
                    }
                    break;
                }
                case Response.Temps:
                {
                    if (args.Length == 2 && args[0] is byte cpuTemp && args[1] is byte gpuTemp)
                    {
                        lblTempC.Text = $"{cpuTemp}°C";
                        lblTempG.Text = $"{gpuTemp}°C";
                        string rpmStr = lblRPM1 != null && !string.IsNullOrEmpty(lblRPM1.Text) && lblRPM1.Text != "0 RPM"
                            ? $" • {lblRPM1.Text}"
                            : "";
                        if (lblHeaderCpu != null) { lblHeaderCpu.Text = $"CPU: {cpuTemp}°C{rpmStr}"; }
                        if (lblHeaderGpu != null) { lblHeaderGpu.Text = $"GPU: {gpuTemp}°C"; }
                        if (notifyIcon != null)
                        {
                            string tip = $"MSI GF63 Tuner: CPU {cpuTemp}°C | GPU {gpuTemp}°C";
                            if (tip.Length < 64) { notifyIcon.Text = tip; }
                        }
                    }
                    break;
                }
                case Response.FanSpeeds:
                {
                    if (args.Length == 2 && args[0] is byte cpuSpeed && args[1] is byte gpuSpeed)
                    {
                        lblFanSpdC.Text = $"{cpuSpeed}%";
                        lblFanSpdG.Text = $"{gpuSpeed}%";
                    }
                    break;
                }
                case Response.FanRPMs:
                {
                    if (args.Length == 1 && args[0] is object[] rpmObj)
                    {
                        // https://stackoverflow.com/a/14080191
                        // converts the object array returned by the service
                        // to an int array, catching non-int values that other
                        // programs may send (if such a program even exists...)
                        int[] rpms = Array.ConvertAll(rpmObj,
                            (o) => int.TryParse(o.ToString(), out int val) ? val : -1);

                        Label[] rpmLabels =
                        [
                            lblRPM1,
                            lblRPM2,
                            lblRPM3,
                            lblRPM4,
                        ];

                        for (int i = 0; i < rpms.Length || i < rpmLabels.Length; i++)
                        {
                            rpmLabels[i].Text = $"{(rpms[i] > -1 ? rpms[i] : 0)} RPM";
                        }

                        if (rpms.Length > 0 && rpms[0] > 0 && lblHeaderCpu != null)
                        {
                            lblHeaderCpu.Text = $"CPU: {lblTempC.Text} • {rpms[0]} RPM";
                        }
                    }
                    break;
                }
                case Response.KeyLightSupported:
                {
                    if (args.Length == 1 && args[0] is bool supported)
                    {
                        if (supported)
                        {
                            ttMain.SetToolTip(tbKeyLight, Strings.GetString("ttKeyLight"));
                            tbKeyLight.Enabled = true;
                            lblKeyLightHigh.Enabled = true;
                            lblKeyLightLow.Enabled = true;
                            SendSvcMessage(new ServiceCommand(Command.GetKeyLightBright));
                        }
                        else
                        {
                            ttMain.SetToolTip(tbKeyLight, Strings.GetString("ttNotSupported"));
                            tbKeyLight.Enabled = false;
                            lblKeyLightHigh.Enabled = false;
                            lblKeyLightLow.Enabled = false;
                        }
                    }
                    break;
                }
                case Response.KeyLightBright:
                {
                    if (args.Length == 1 && args[0] is byte brightness)
                    {
                        // value received from service should be valid,
                        // but let's check anyway to avoid potential crashes
                        // from non-official YAMDCC services
                        if (brightness < 0 || brightness > 4)
                        {
                            break;
                        }

                        tbKeyLight.Value = brightness;
                        ttMain.SetToolTip(tbKeyLight, Strings.GetString("ttKeyLight"));
                    }
                    break;
                }
                case Response.KeySwapSupported:
                {
                    if (args.Length == 1 && args[0] is bool supported)
                    {
                        if (supported)
                        {
                            chkWinFnSwap.Checked = Config.KeySwapEnabled;
                            ttMain.SetToolTip(chkWinFnSwap, Strings.GetString("ttKeySwap"));
                            chkWinFnSwap.Enabled = true;
                        }
                    }
                    break;
                }
                case Response.FirmVer:
                {
                    // no idea how the EcInfo class became a nested
                    // object array, but this works so keeping it
                    if (args[0] is object[] obj && obj.Length == 2 &&
                        obj[0] is string ver && obj[1] is DateTime date)
                    {
                        Config.FirmVer = ver;
                        Config.FirmDate = date;
                        txtFirmVer.Text = Config.FirmVer;
                        txtFirmDate.Text = $"{Config.FirmDate:G}";
                    }
                    break;
                }
                case Response.ConfLoaded:
                {
                    LoadConf(Paths.CurrentConfV2);
                    if (Config?.CpuFan?.FanProfs != null && Config.CpuFan.ProfSel < Config.CpuFan.FanProfs.Count)
                    {
                        string pName = Config.CpuFan.FanProfs[Config.CpuFan.ProfSel].Name;
                        if (!Visible || WindowState == FormWindowState.Minimized || !Focused)
                        {
                            ProfileOsdToast.ShowToast(pName);
                        }
                    }
                    break;
                }
            }
        });
    }

    private void IPCError(object sender, PipeErrorEventArgs<ServiceResponse, ServiceCommand> e)
    {
        new CrashDialog(e.Exception).ShowDialog();
    }

    #region Tool strip menu items

    #region File
    private void tsiLoadConf_Click(object sender, EventArgs e)
    {
        OpenFileDialog ofd = new()
        {
            AddExtension = true,
            CheckFileExists = true,
            Filter = "MSI GF63 Tuner config files|*.xml",
            Title = "Load config",
        };

        if (ofd.ShowDialog() == DialogResult.OK)
        {
            if (LoadConf(ofd.FileName))
            {
                CommonConfig.SetLastConf(ofd.FileName);
            }
            else
            {
                Utils.ShowError(Strings.GetString("dlgLoadConfFail"));
            }
        }
    }

    private void tsiSaveConf_Click(object sender, EventArgs e)
    {
        SaveFileDialog sfd = new()
        {
            AddExtension = true,
            Filter = "MSI GF63 Tuner config files|*.xml",
            FileName = Config.Model.Replace(' ', '-'),
            Title = "Save config",
        };

        if (sfd.ShowDialog() == DialogResult.OK)
        {
            Config.ChargeLim = (byte)(chkChgLim.Checked ? numChgLim.Value : 0);
            Config.Save(sfd.FileName);
            CommonConfig.SetLastConf(sfd.FileName);
        }
    }

    private void tsiExit_Click(object sender, EventArgs e)
    {
        allowExplicitExit = true;
        Close();
    }
    #endregion

    #region Options
    private void ProfRename(object sender, EventArgs e)
    {
        FanConf cfg = GPUFan ? Config.GpuFan : Config.CpuFan;
        FanProf curveCfg = cfg.FanProfs[cfg.ProfSel];

        TextInputDialog dlg = new(
            Strings.GetString("dlgProfRen"),
            "Change Profile Name", curveCfg.Name);
        if (dlg.ShowDialog() == DialogResult.OK)
        {
            curveCfg.Name = dlg.Result;
            cboProfSel.Items[cboProfSel.SelectedIndex] = dlg.Result;
        }
    }

    private void ProfChangeDesc(object sender, EventArgs e)
    {
        FanConf cfg = GPUFan ? Config.GpuFan : Config.CpuFan;
        FanProf curveCfg = cfg.FanProfs[cfg.ProfSel];

        TextInputDialog dlg = new(
            Strings.GetString("dlgProfChangeDesc"),
            "Change Profile Description", curveCfg.Desc, true);
        if (dlg.ShowDialog() == DialogResult.OK)
        {
            curveCfg.Desc = dlg.Result;
            ttMain.SetToolTip(cboProfSel, Strings.GetString(
                "ttProfSel", dlg.Result));
        }
    }

    private void ECtoConf(object sender, EventArgs e)
    {
        if (Utils.ShowInfo(Strings.GetString("dlgECtoConfStart"),
            "Default fan profile from EC?", MessageBoxButtons.YesNo) == DialogResult.Yes)
        {
            Application.Exit();
            CommonConfig.SetECtoConfState(ECtoConfState.PendingReboot);
        }
    }

    private void AdvancedToggle(object sender, EventArgs e)
    {
        if (!tsiAdvanced.Checked && Utils.ShowWarning(Strings.GetString("dlgAdvanced"),
            "Show advanced settings?", MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            return;
        }
        tsiAdvanced.Checked = !tsiAdvanced.Checked;
        cboFanMode.Enabled = tsiAdvanced.Checked;
    }

    private void tsiLogNone_Click(object sender, EventArgs e)
    {
        CommonConfig.SetLogLevel(LogLevel.None);
        tsiLogNone.Checked = true;
        tsiLogDebug.Checked = false;
        tsiLogInfo.Checked = false;
        tsiLogWarn.Checked = false;
        tsiLogError.Checked = false;
        tsiLogFatal.Checked = false;
    }

    private void tsiLogDebug_Click(object sender, EventArgs e)
    {
        CommonConfig.SetLogLevel(LogLevel.Debug);
        tsiLogNone.Checked = false;
        tsiLogDebug.Checked = true;
        tsiLogInfo.Checked = false;
        tsiLogWarn.Checked = false;
        tsiLogError.Checked = false;
        tsiLogFatal.Checked = false;
    }

    private void tsiLogInfo_Click(object sender, EventArgs e)
    {
        CommonConfig.SetLogLevel(LogLevel.Info);
        tsiLogNone.Checked = false;
        tsiLogDebug.Checked = false;
        tsiLogInfo.Checked = true;
        tsiLogWarn.Checked = false;
        tsiLogError.Checked = false;
        tsiLogFatal.Checked = false;
    }

    private void tsiLogWarn_Click(object sender, EventArgs e)
    {
        CommonConfig.SetLogLevel(LogLevel.Warn);
        tsiLogNone.Checked = false;
        tsiLogDebug.Checked = false;
        tsiLogInfo.Checked = false;
        tsiLogWarn.Checked = true;
        tsiLogError.Checked = false;
        tsiLogFatal.Checked = false;
    }

    private void tsiLogError_Click(object sender, EventArgs e)
    {
        CommonConfig.SetLogLevel(LogLevel.Error);
        tsiLogNone.Checked = false;
        tsiLogDebug.Checked = false;
        tsiLogInfo.Checked = false;
        tsiLogWarn.Checked = false;
        tsiLogError.Checked = true;
        tsiLogFatal.Checked = false;
    }

    private void tsiLogFatal_Click(object sender, EventArgs e)
    {
        CommonConfig.SetLogLevel(LogLevel.Fatal);
        tsiLogNone.Checked = false;
        tsiLogDebug.Checked = false;
        tsiLogInfo.Checked = false;
        tsiLogWarn.Checked = false;
        tsiLogError.Checked = false;
        tsiLogFatal.Checked = true;
    }

    private void tsiFBExit_Click(object sender, EventArgs e)
    {
        CommonConfig.SetDisableFBOnExit(((ToolStripMenuItem)sender).Checked);
    }

    private void tsiStopSvc_Click(object sender, EventArgs e)
    {
        if (Utils.ShowWarning(Strings.GetString("dlgSvcStop"),
            "Stop Service") == DialogResult.Yes)
        {
            tmrPoll.Stop();
            IPCClient.Stop();
            Hide();

            if (Program.EmbeddedService != null)
            {
                Program.StopEmbeddedService();
                Utils.ShowInfo(Strings.GetString("dlgSvcStopped"), "Success");
            }
            else if (Utils.ServiceExists("yamdccsvc"))
            {
                ProgressDialog<bool> dlg = new()
                {
                    Caption = Strings.GetString("dlgSvcStopping"),
                    DoWork = static () =>
                    {
                        if (!Utils.StopService("yamdccsvc"))
                        {
                            Utils.ShowError(Strings.GetString("dlgSvcStopErr"));
                            return false;
                        }
                        Utils.ShowInfo(Strings.GetString("dlgSvcStopped"), "Success");
                        return true;
                    }
                };
                dlg.ShowDialog();
            }

            allowExplicitExit = true;
            Close();
        }
    }

    private void tsiUninstall_Click(object sender, EventArgs e)
    {
        if (Utils.ShowWarning(Strings.GetString("dlgUninstall"),
            "Uninstall?") == DialogResult.Yes)
        {
            bool delData = Utils.ShowWarning(
                Strings.GetString("dlgSvcDelData", Paths.Data),
                "Delete configuration data?") == DialogResult.No;

            tmrPoll.Stop();
            IPCClient.Stop();
            Hide();

            if (Program.EmbeddedService != null)
            {
                Program.StopEmbeddedService();
            }

            ProgressDialog<bool> dlg = new()
            {
                Caption = Strings.GetString("dlgSvcUninstalling"),
                DoWork = () =>
                {
                    if (Utils.ServiceExists("yamdccsvc"))
                    {
                        if (!Utils.StopService("yamdccsvc"))
                        {
                            Utils.ShowError(Strings.GetString("dlgSvcStopErr"));
                            return false;
                        }
                        if (!Utils.UninstallService("yamdccsvc"))
                        {
                            Utils.ShowError(Strings.GetString("dlgUninstallErr"));
                            return false;
                        }
                    }

                    // Delete the auto-update scheduled task
                    try
                    {
                        Process.Start(Path.GetFullPath(@".\Updater.exe"), "--setautoupdate false");
                    }
                    catch (Win32Exception ex)
                    {
                        // catch the exception that occurs if the Updater is missing
                        // no need to show the "Updater not found" window during
                        // uninstall (it might not be installed in the first place)
                        if (ex.ErrorCode != -2147467259 || ex.NativeErrorCode != 2)
                        {
                            throw;
                        }
                    }

                    // Only delete service data if the
                    // service uninstalled successfully
                    if (delData)
                    {
                        Directory.Delete(Paths.Data, true);
                    }
                    Utils.ShowInfo(Strings.GetString("dlgSvcUninstalled"), "Success");
                    return true;
                }
            };
            dlg.ShowDialog();
            allowExplicitExit = true;
            Close();
        }
    }
    #endregion

    #region Help
    private void tsiAbout_Click(object sender, EventArgs e)
    {
        new VersionDialog().ShowDialog();
    }

    private void tsiSrc_Click(object sender, EventArgs e)
    {
        Process.Start(Paths.CodebergPage);
    }

    private void tsiCheckUpdate_Click(object sender, EventArgs e)
    {
        RunUpdater("--checkupdate");
    }
    #endregion

    #endregion

    private void UpdateFanSelButton()
    {
        if (btnFanSel == null) return;
        if (!GPUFan)
        {
            btnFanSel.Text = "⚡ CPU FAN (Active)";
            btnFanSel.BackColor = Theme.Accent;
            btnFanSel.ForeColor = Color.White;
            btnFanSel.FlatAppearance.BorderColor = Theme.AccentHover;
        }
        else
        {
            btnFanSel.Text = "🎮 GPU FAN (Active)";
            btnFanSel.BackColor = Color.FromArgb(0x00, 0x7A, 0xCC);
            btnFanSel.ForeColor = Color.White;
            btnFanSel.FlatAppearance.BorderColor = Color.FromArgb(0x28, 0x98, 0xEE);
        }
    }

    private void UnifyProfileControls()
    {
        Control lblPerf = tblFanControl?.GetControlFromPosition(5, 0);
        if (lblPerf != null) lblPerf.Visible = false;
        if (cboProfPerfMode != null) cboProfPerfMode.Visible = false;
        if (cboProfSel != null)
        {
            float scale = CurrentAutoScaleDimensions.Height / 96f;
            cboProfSel.Width = (int)(250 * scale);
            cboProfSel.DropDownWidth = (int)(280 * scale);
        }
    }

    private void FanSelChange(object sender, EventArgs e)
    {
        GPUFan = !GPUFan;
        UpdateFanSelButton();
        RefreshFanProf();
    }

    private void RefreshFanProf()
    {
        FanConf cfg = GPUFan ? Config.GpuFan : Config.CpuFan;

        cboProfSel.Items.Clear();
        foreach (FanProf curve in cfg.FanProfs)
        {
            cboProfSel.Items.Add(curve.Name);
        }

        if (numFanSpds is null)
        {
            float scale = CurrentAutoScaleDimensions.Height / 96;

            tblCurve.SuspendLayout();
            tblCurve.Controls.Clear();
            numUpTs = new NumericUpDown[6];
            numDownTs = new NumericUpDown[6];
            numFanSpds = new NumericUpDown[7];
            tbFanSpds = new DarkTrackBar[7];

            tblCurve.ColumnStyles.Clear();
            tblCurve.ColumnCount = numFanSpds.Length + 1;

            tblCurve.RowCount = 5;
            tblCurve.RowStyles.Clear();
            tblCurve.RowStyles.Add(new RowStyle(SizeType.Absolute, (int)(26 * scale)));
            tblCurve.RowStyles.Add(new RowStyle(SizeType.Absolute, (int)(30 * scale)));
            tblCurve.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            tblCurve.RowStyles.Add(new RowStyle(SizeType.Absolute, (int)(30 * scale)));
            tblCurve.RowStyles.Add(new RowStyle(SizeType.Absolute, (int)(30 * scale)));

            // labels on left side
            tblCurve.ColumnStyles.Add(new ColumnStyle());
            tblCurve.Controls.Add(FanCurveLabel("CURVE", scale, 0, ContentAlignment.MiddleCenter, Theme.TextMuted), 0, 0);
            tblCurve.Controls.Add(FanCurveLabel("⚡ Speed (%)", scale, 0, ContentAlignment.MiddleRight, Theme.Accent), 0, 1);
            tblCurve.Controls.Add(FanCurveLabel("▲ Up (°C)", scale, (numFanSpds.Length + 1) * 2, ContentAlignment.MiddleRight, Color.FromArgb(0xFF, 0x8C, 0x00)), 0, 3);
            tblCurve.Controls.Add(FanCurveLabel("▼ Down (°C)", scale, (numFanSpds.Length + 1) * 3, ContentAlignment.MiddleRight, Color.FromArgb(0x40, 0xC4, 0xFF)), 0, 4);

            for (int i = 0; i < numFanSpds.Length; i++)
            {
                tblCurve.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / numFanSpds.Length));

                // Row 0: Point header card
                string ptName = i == numFanSpds.Length - 1 ? $"Pt {i + 1} (Max)" : $"Point {i + 1}";
                Color ptColor = i == numFanSpds.Length - 1 ? Theme.Accent : Theme.TextMuted;
                tblCurve.Controls.Add(FanCurveLabel(ptName, scale, 0, ContentAlignment.MiddleCenter, ptColor), i + 1, 0);

                // Row 1: Speed NUD
                numFanSpds[i] = FanCurveNUD(i, scale, i + 1);
                ttMain.SetToolTip(numFanSpds[i], Strings.GetString("ttFanSpd"));
                numFanSpds[i].ValueChanged += new EventHandler(FanSpdChange);
                tblCurve.Controls.Add(numFanSpds[i], i + 1, 1);

                // Row 2: Precision Slider
                tbFanSpds[i] = new DarkTrackBar()
                {
                    Dock = DockStyle.Fill,
                    LargeChange = 10,
                    Margin = new Padding((int)(12 * scale), 0, (int)(12 * scale), 0),
                    Orientation = Orientation.Vertical,
                    TabIndex = i + numFanSpds.Length + 2,
                    TabStop = false,
                    Tag = i,
                    TickFrequency = 5,
                    TickStyle = TickStyle.Both,
                };
                ttMain.SetToolTip(tbFanSpds[i], Strings.GetString("ttFanSpd"));
                tbFanSpds[i].ValueChanged += new EventHandler(FanSpdChange);
                tblCurve.Controls.Add(tbFanSpds[i], i + 1, 2);

                // Row 3: Up temp
                if (i != 0)
                {
                    numUpTs[i - 1] = FanCurveNUD(i - 1, scale, i + (numFanSpds.Length * 2) + 3);
                    ttMain.SetToolTip(numUpTs[i - 1], Strings.GetString("ttUpT"));
                    numUpTs[i - 1].ValueChanged += new EventHandler(UpTChange);
                    tblCurve.Controls.Add(numUpTs[i - 1], i + 1, 3);
                }
                else
                {
                    tblCurve.Controls.Add(FanCurveLabel("Default", scale, i + (numFanSpds.Length * 2) + 3, ContentAlignment.MiddleCenter, Theme.TextMuted), i + 1, 3);
                }

                // Row 4: Down temp
                if (i != numFanSpds.Length - 1)
                {
                    numDownTs[i] = FanCurveNUD(i, scale, i + (numFanSpds.Length * 3) + 4);
                    ttMain.SetToolTip(numDownTs[i], Strings.GetString("ttDownT"));
                    numDownTs[i].ValueChanged += new EventHandler(DownTChange);
                    tblCurve.Controls.Add(numDownTs[i], i + 1, 4);
                }
                else
                {
                    tblCurve.Controls.Add(FanCurveLabel("Max", scale, i + (numFanSpds.Length * 3) + 4, ContentAlignment.MiddleCenter, Theme.Accent), i + 1, 4);
                }
            }
            tblCurve.ResumeLayout(true);
        }

        for (int i = 0; i < numFanSpds.Length; i++)
        {
            numFanSpds[i].Maximum = tbFanSpds[i].Maximum = 150;
        }

        cboProfSel.Enabled = true;
        cboProfSel.SelectedIndex = cfg.ProfSel;

        tmrPoll.Stop();
        UpdatePolling();
    }

    private void ProfSelChanged(object sender, EventArgs e)
    {
        FanConf cfg = GPUFan ? Config.GpuFan : Config.CpuFan;
        FanProf curveCfg = cfg.FanProfs[cboProfSel.SelectedIndex];

        Config.CpuFan.ProfSel = cboProfSel.SelectedIndex;
        Config.GpuFan.ProfSel = cboProfSel.SelectedIndex;

        ttMain.SetToolTip(cboProfSel, Strings.GetString(
            "ttProfSel", cfg.FanProfs[cfg.ProfSel].Desc));

        if (lblHeaderProf != null && cboProfSel.SelectedIndex >= 0 && cboProfSel.SelectedIndex < cfg.FanProfs.Count)
        {
            lblHeaderProf.Text = $"ACTIVE: {cfg.FanProfs[cboProfSel.SelectedIndex].Name.ToUpperInvariant()}";
        }

        if (!Visible || WindowState == FormWindowState.Minimized || !Focused)
        {
            ProfileOsdToast.ShowToast(curveCfg.Name);
        }

        int targetPerfIdx = (int)Config.CpuFan.FanProfs[cfg.ProfSel].PerfMode + 1;
        if (targetPerfIdx >= 0 && targetPerfIdx < cboProfPerfMode.Items.Count)
        {
            cboProfPerfMode.SelectedIndex = targetPerfIdx;
        }
        cboProfPerfMode.Enabled = true;

        if (curveCfg.PerfMode != PerfMode.Default)
        {
            Config.PerfMode = curveCfg.PerfMode;
            int modeIdx = (int)curveCfg.PerfMode;
            if (modeIdx >= 0 && modeIdx < cboPerfMode.Items.Count)
            {
                cboPerfMode.SelectedIndex = modeIdx;
            }
        }

        bool enable = true;
        for (int i = 0; i < numFanSpds.Length; i++)
        {
            // Fan profile
            Threshold t = curveCfg.Thresholds[i];
            numFanSpds[i].Value = tbFanSpds[i].Value = t.Speed;
            numFanSpds[i].Enabled = enable;
            tbFanSpds[i].Enabled = enable;

            // Temp thresholds
            if (i < numUpTs.Length)
            {
                t = curveCfg.Thresholds[i + 1];
                numUpTs[i].Value = t.Tup;
                numDownTs[i].Value = t.Tdown;
                numUpTs[i].Enabled = enable;
                numDownTs[i].Enabled = enable;
            }
        }
        btnProfDel.Enabled = cfg.FanProfs.Count > 1;
        tsiProfDel.Enabled = cfg.FanProfs.Count > 1;
    }

    private void ProfPerfModeChanged(object sender, EventArgs e)
    {
        int perfIdx = cboProfPerfMode.SelectedIndex;
        if (perfIdx >= 0 && cboProfSel.SelectedIndex >= 0 && cboProfSel.SelectedIndex < Config.CpuFan.FanProfs.Count)
        {
            PerfMode mode = (PerfMode)(perfIdx - 1);
            Config.CpuFan.FanProfs[cboProfSel.SelectedIndex].PerfMode = mode;
            if (Config.GpuFan?.FanProfs != null && cboProfSel.SelectedIndex < Config.GpuFan.FanProfs.Count)
            {
                Config.GpuFan.FanProfs[cboProfSel.SelectedIndex].PerfMode = mode;
            }
            if (mode != PerfMode.Default)
            {
                Config.PerfMode = mode;
                int modeIdx = (int)mode;
                if (modeIdx >= 0 && modeIdx < cboPerfMode.Items.Count)
                {
                    cboPerfMode.SelectedIndex = modeIdx;
                }
            }
        }

        /*if (i > 0)
        {
            ttMain.SetToolTip(cboProfPerfMode,
                Strings.GetString("ttProfPerfMode", pModeCfg.PerfModes[i - 1].Desc));
        }
        else    // use default performance mode description
        {
            ttMain.SetToolTip(cboProfPerfMode,
                Strings.GetString("ttProfPerfMode", pModeCfg.PerfModes[pModeCfg.ModeSel].Desc));
        }*/
    }

    private void ProfAdd(object sender, EventArgs e)
    {
        FanConf cfg = GPUFan ? Config.GpuFan : Config.CpuFan;

        TextInputDialog dlg = new(
            Strings.GetString("dlgProfAdd"), "New Profile",
            $"Copy of {cfg.FanProfs[cfg.ProfSel].Name}");

        if (dlg.ShowDialog() == DialogResult.OK)
        {
            CloneFanProf(Config.CpuFan, dlg.Result);
            CloneFanProf(Config.GpuFan, dlg.Result);

            // Add the new fan profile to the UI's profile list and select it:
            cboProfSel.Items.Add(dlg.Result);
            cboProfSel.SelectedIndex = cfg.ProfSel;
        }
    }

    private static void CloneFanProf(FanConf cfg, string newName)
    {
        // Create a copy of the currently selected fan profile
        // and add it to the config's list:
        FanProf oldCurveCfg = cfg.FanProfs[cfg.ProfSel];
        cfg.FanProfs.Add(oldCurveCfg.Copy());
        cfg.ProfSel = cfg.FanProfs.Count - 1;

        // Name it according to what the user specified
        cfg.FanProfs[cfg.ProfSel].Name = newName;
        cfg.FanProfs[cfg.ProfSel].Desc = $"(Copy of {oldCurveCfg.Name})\n{oldCurveCfg.Desc}";
    }

    private void btnProfAdd_KeyPress(object sender, KeyPressEventArgs e)
    {
        // hidden crash test
        switch (e.KeyChar)
        {
            case 'e':
                Debug++;
                if (Debug == 5)
                {
                    Debug = 0;
                    throw new InvalidOperationException(
                        "You pressed 'E' too many times (crash test triggered).");
                }
                break;
            default:
                Debug = 0;
                break;
        }
    }

    private void ProfDel(object sender, EventArgs e)
    {
        FanConf cfg = GPUFan ? Config.GpuFan : Config.CpuFan;
        int profIdx = cfg.ProfSel;
        FanProf curveCfg = cfg.FanProfs[profIdx];

        if (curveCfg.Name != "Default" && Utils.ShowWarning(
            Strings.GetString("dlgProfDel", curveCfg.Name),
            $"Delete fan profile?") == DialogResult.Yes)
        {
            // Remove each equivalent fan profile from the config's list
            Config.CpuFan.FanProfs.RemoveAt(profIdx);
            Config.CpuFan.ProfSel -= 1;
            Config.GpuFan.FanProfs.RemoveAt(profIdx);
            Config.GpuFan.ProfSel -= 1;

            // Remove from the list client-side, and select a different fan profile
            cboProfSel.Items.RemoveAt(cboProfSel.SelectedIndex);
            cboProfSel.SelectedIndex = cfg.ProfSel;
        }
    }

    private void FanSpdChange(object sender, EventArgs e)
    {
        Control c = (Control)sender;
        int i = (int)c.Tag;

        if (c is NumericUpDown)
        {
            tbFanSpds[i].Value = (int)numFanSpds[i].Value;
        }
        else if (c is DarkTrackBar)
        {
            numFanSpds[i].Value = tbFanSpds[i].Value;
        }

        if (cboProfSel.SelectedIndex != -1)
        {
            FanConf cfg = GPUFan ? Config.GpuFan : Config.CpuFan;

            cfg.FanProfs[cboProfSel.SelectedIndex]
                .Thresholds[i].Speed = (byte)tbFanSpds[i].Value;
        }
    }

    private void UpTChange(object sender, EventArgs e)
    {
        NumericUpDown nud = (NumericUpDown)sender;
        int i = (int)nud.Tag;

        FanConf cfg = GPUFan ? Config.GpuFan : Config.CpuFan;

        Threshold threshold = cfg
            .FanProfs[cboProfSel.SelectedIndex].Thresholds[i + 1];

        // Update associated down threshold slider
        numDownTs[i].Value += nud.Value - threshold.Tup;

        threshold.Tup = (byte)numUpTs[i].Value;
    }

    private void DownTChange(object sender, EventArgs e)
    {
        NumericUpDown nud = (NumericUpDown)sender;
        int i = (int)nud.Tag;

        FanConf cfg = GPUFan ? Config.GpuFan : Config.CpuFan;
        cfg.FanProfs[cboProfSel.SelectedIndex].Thresholds[i + 1]
            .Tdown = (byte)numDownTs[i].Value;
    }

    private void ChgLimToggle(object sender, EventArgs e)
    {
        numChgLim.Enabled = chkChgLim.Checked;
    }

    private void PerfModeChange(object sender, EventArgs e)
    {
        int i = cboPerfMode.SelectedIndex;
        Config.PerfMode = (PerfMode)i;
        //ttMain.SetToolTip(cboPerfMode, Strings.GetString("ttPerfMode", Config.PerfModeConf.PerfModes[i].Desc));

        cboProfPerfMode.Items[0] = $"Default ({Config.PerfMode})";
    }

    private void WinFnSwapToggle(object sender, EventArgs e)
    {
        Config.KeySwapEnabled = chkWinFnSwap.Checked;
    }

    private void KeyLightChange(object sender, EventArgs e)
    {
        SendSvcMessage(new ServiceCommand(Command.SetKeyLightBright, (byte)tbKeyLight.Value));
    }

    private void FanModeChange(object sender, EventArgs e)
    {
        int i = cboFanMode.SelectedIndex;
        Config.FanMode = (FanMode)i;
        //ttMain.SetToolTip(cboFanMode, Strings.GetString("ttFanMode", Config.FanMode));
    }

    private void txtAuthor_Validating(object sender, CancelEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(txtAuthor.Text))
        {
            MessageBox.Show("Author must not be empty", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            txtAuthor.Text = Config.Author;
        }
        else
        {
            Config.Author = txtAuthor.Text.Trim();
        }
    }

    private void btnGetModel_Click(object sender, EventArgs e)
    {
        string pcManufacturer = Utils.GetPCManufacturer(),
            pcModel = Utils.GetPCModel();

        if (!string.IsNullOrEmpty(pcManufacturer))
        {
            txtManufacturer.Text = pcManufacturer;
            Config.Manufacturer = pcManufacturer;
        }
        if (!string.IsNullOrEmpty(pcModel))
        {
            txtModel.Text = pcModel;
            Config.Model = pcModel;
        }

        SendSvcMessage(new ServiceCommand(Command.GetFirmVer));
    }

    private void FullBlastToggle(object sender, EventArgs e)
    {
        ToggleSvcCmds(false);
        SendSvcMessage(new ServiceCommand(Command.SetFullBlast, chkFullBlast.Checked ? 1 : 0));
        if (!Visible || WindowState == FormWindowState.Minimized || !Focused)
        {
            ProfileOsdToast.ShowToast(chkFullBlast.Checked ? "COOLER BOOST: ACTIVE" : "COOLER BOOST: DISABLED");
        }
    }

    private void RevertConf(object sender, EventArgs e)
    {
        if (Utils.ShowWarning(Strings.GetString("dlgRevert"),
            "Revert?") == DialogResult.Yes)
        {
            try
            {
                Config = YamdccCfg.Load(CommonConfig.GetLastConf());
                LoadConf(Config);
                ApplyConf(sender, e);
            }
            catch (Exception ex)
            {
                if (ex is FileNotFoundException)
                {
                    Utils.ShowError(Strings.GetString("dlgOldConfMissing"));
                }
                else if (ex is InvalidConfigException or InvalidOperationException)
                {
                    Utils.ShowError(Strings.GetString("dlgOldConfInvalid"));
                }
                else
                {
                    throw;
                }
            }
        }
    }

    private void ApplyConf(object sender, EventArgs e)
    {
        ToggleSvcCmds(false);

        // Save the updated config
        Config.ChargeLim = (byte)(chkChgLim.Checked ? numChgLim.Value : 0);
        Config.Save(Paths.CurrentConfV2);

        // Tell the service to reload and apply the updated config
        SendSvcMessage(new ServiceCommand(Command.ApplyConf));
    }

    private void tmrPoll_Tick(object sender, EventArgs e)
    {
        PollEC();
    }

    private void tmrStatusReset_Tick(object sender, EventArgs e)
    {
        UpdateStatus(StatusCode.None);
        tmrStatusReset.Stop();
    }

    private void tmrSvcTimeout_Tick(object sender, EventArgs e)
    {
        UpdateStatus(StatusCode.ServiceTimeout);
        tmrSvcTimeout.Stop();
    }

    #endregion  // Events

    #region Private methods
    private bool LoadConf(string confPath)
    {
        UpdateStatus(StatusCode.ConfLoading);

        try
        {
            Config = YamdccCfg.Load(confPath);
            LoadConf(Config);
            return true;
        }
        catch (Exception ex)
        {
            if (ex is InvalidConfigException or InvalidOperationException or FileNotFoundException)
            {
                UpdateStatus(StatusCode.NoConfig);
                return false;
            }
            else
            {
                throw;
            }
        }
    }

    private void LoadConf(YamdccCfg cfg)
    {
        DisableAll();

        tsiSaveConf.Enabled = true;

        txtAuthor.Text = cfg.Author;
        txtManufacturer.Text = cfg.Manufacturer;
        txtModel.Text = cfg.Model;
        txtFirmVer.Text = cfg.FirmVer;
        txtFirmDate.Text = $"{cfg.FirmDate:G}";

        txtAuthor.Enabled = true;
        btnGetModel.Enabled = true;

        ttMain.SetToolTip(chkFullBlast, Strings.GetString("ttFullBlast"));
        chkFullBlast.Enabled = true;

        ttMain.SetToolTip(numChgLim, Strings.GetString("ttChgLim"));
        ttMain.SetToolTip(numChgLim, Strings.GetString("ttChgLim"));
        chkChgLim.Enabled = true;
        if (Config.ChargeLim == 0)
        {
            chkChgLim.Checked = false;
            numChgLim.Enabled = false;
            numChgLim.Value = 80;
        }
        else
        {
            chkChgLim.Checked = true;
            numChgLim.Enabled = true;
            numChgLim.Value = Config.ChargeLim;
        }

        cboPerfMode.Items.Clear();
        cboProfPerfMode.Items.Clear();

        cboProfPerfMode.Items.Add($"Default ({Config.PerfMode})");
        foreach (PerfMode pMode in Enum.GetValues(typeof(PerfMode)))
        {
            if (pMode != PerfMode.Default)
            {
                cboPerfMode.Items.Add(pMode);
                cboProfPerfMode.Items.Add(pMode);
            }
        }

        int currPerf = (int)Config.PerfMode;
        if (currPerf >= 0 && currPerf < cboPerfMode.Items.Count)
        {
            cboPerfMode.SelectedIndex = currPerf;
        }
        cboPerfMode.Enabled = true;

        cboFanMode.Items.Clear();
        foreach (FanMode fMode in Enum.GetValues(typeof(FanMode)))
        {
            cboFanMode.Items.Add(fMode);
        }

        cboFanMode.SelectedIndex = (int)Config.FanMode;
        cboFanMode.Enabled = tsiAdvanced.Checked;

        IPCClient.PushMessage(new ServiceCommand(Command.GetKeySwapSupported));
        ttMain.SetToolTip(chkWinFnSwap, Strings.GetString("ttNotSupported"));
        chkWinFnSwap.Checked = false;
        chkWinFnSwap.Enabled = false;

        GPUFan = false;
        UpdateFanSelButton();
        RefreshFanProf();

        if (lblHeaderProf != null && Config?.CpuFan?.FanProfs != null && Config.CpuFan.ProfSel < Config.CpuFan.FanProfs.Count)
        {
            lblHeaderProf.Text = $"ACTIVE: {Config.CpuFan.FanProfs[Config.CpuFan.ProfSel].Name.ToUpperInvariant()}";
        }

        btnProfAdd.Enabled = true;
        tsiProfAdd.Enabled = true;
        tsiProfEdit.Enabled = true;
        tsiECtoConf.Enabled = true;
        btnFanSel.Enabled = true;

        UpdateStatus(StatusCode.None);
        ToggleSvcCmds(true);
    }

    private void PollEC()
    {
        SendSvcMessage(new ServiceCommand(Command.GetTemps));
        SendSvcMessage(new ServiceCommand(Command.GetFanSpeeds));
        SendSvcMessage(new ServiceCommand(Command.GetFanRPMs));
        RefreshSensors();
    }

    #region Monitoring tab
    /// <summary>
    /// Replaces the Monitoring tab's designer layout with one grouped by
    /// device, adding the driver-free sensors to the EC's own readings.
    /// </summary>
    /// <remarks>
    /// The existing EC labels are re-parented rather than replaced, so the
    /// service's temperature/fan-speed messages keep updating them.
    /// </remarks>
    private void BuildMonitoringTab()
    {
        try { Sensors = new SystemSensors(); }
        catch { Sensors = null; }

        TableLayoutPanel t = new()
        {
            ColumnCount = 2,
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(8, 6, 8, 6),
        };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

        int row = 0;

        void Header(string text)
        {
            Label h = new()
            {
                Text = $"//  {text.ToUpperInvariant()}",
                AutoSize = true,
                Margin = new Padding(0, row == 0 ? 0 : 12, 0, 4),
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                ForeColor = Theme.Accent,
            };
            t.Controls.Add(h, 0, row);
            t.SetColumnSpan(h, 2);
            row++;
        }

        Label Row(string caption, Label value = null)
        {
            Label c = new()
            {
                Text = caption,
                AutoSize = true,
                Margin = new Padding(12, 2, 8, 2),
                Font = new Font("Segoe UI", 9F, FontStyle.Regular),
                ForeColor = Theme.TextMuted,
            };
            value ??= new Label { Text = "--", AutoSize = true };
            value.Margin = new Padding(0, 2, 0, 2);
            value.Font = new Font("Consolas", 9.5F, FontStyle.Bold);
            value.ForeColor = Theme.Text;
            t.Controls.Add(c, 0, row);
            t.Controls.Add(value, 1, row);
            row++;
            return value;
        }

        // ---- CPU -----------------------------------------------------------
        Header("CPU");
        Row("Temperature", lblTempC);
        lblCpuLoad = Row("Load");
        lblCpuClock = Row("Clock");
        lblCpuPkgW = Row("Package power");
        lblCpuCoreW = Row("Cores power");

        // ---- GPUs ----------------------------------------------------------
        List<GpuReading> gpus = [];
        try { gpus = Sensors?.Read().Gpus ?? []; }
        catch { }

        foreach (GpuReading g in gpus)
        {
            Header(g.Name + (g.Discrete ? "  (discrete)" : "  (integrated)"));

            // the EC's GPU thermal sensor and fan channel refer to the
            // discrete GPU, so those rows only belong under it
            if (g.Discrete)
            {
                Row("Temperature", lblTempG);
            }

            Label gpuTemp = g.Discrete ? null : Row("Temperature");
            Label load = Row("Load");
            Label clock = Row("Core clock");
            Label vram = Row("VRAM");
            Label watts = Row("Power");
            GpuLabels[g.Luid] = (load, clock, vram, watts, gpuTemp ?? lblTempG);
        }

        // ---- Cooling -------------------------------------------------------
        // This laptop has a single fan shared by the CPU and GPU, so listing a
        // "fan speed" under each of them implied two fans that do not exist.
        Header("Cooling");
        Row("Fan speed", lblFanSpdC);
        Row("Fan RPM", lblRPM1);

        // ---- System --------------------------------------------------------
        Header("System");
        lblRamUsed = Row("Memory");
        lblSysPower = Row("Battery draw");
        lblFps = Row("Framerate");
        lblSensorSrc = Row("Sensor source");

        TabMonitoring.Controls.Clear();
        TabMonitoring.Controls.Add(t);
        Theme.ApplyTo(t);

        // the unused RPM slots the EC always reports are hidden by
        // RefreshSensors() once it knows how many fans actually exist
        // The EC always reports four RPM slots; only the first is a real fan
        // here. lblFanSpdG is still written to by the service's fan-speed
        // message, so it is kept alive but not shown.
        foreach (Label l in new[] { lblRPM2, lblRPM3, lblRPM4, lblFanSpdG })
        {
            l.Visible = false;
        }
    }

    /// <summary>
    /// Replaces the Extras tab with modern gaming hardware control cards.
    /// Grouped into Battery Health Master, Keyboard &amp; Hardware Input, and System Power.
    /// </summary>
    private void SetupModernExtrasTab()
    {
        TabPage page = tcMain.TabPages["tabExtra"];
        if (page == null) return;

        float scale = CurrentAutoScaleDimensions.Height / 96f;
        page.Controls.Clear();

        TableLayoutPanel mainLayout = new()
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding((int)(14 * scale), (int)(10 * scale), (int)(14 * scale), (int)(10 * scale)),
            ColumnCount = 1,
            RowCount = 2,
        };
        mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        mainLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        mainLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // =====================================================================
        // CARD 1: BATTERY HEALTH MASTER
        // =====================================================================
        GroupBox gbBattery = new()
        {
            Text = "  🔋 BATTERY HEALTH MASTER  ",
            Dock = DockStyle.Top,
            AutoSize = true,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
            ForeColor = Theme.Accent,
            Padding = new Padding((int)(14 * scale), (int)(12 * scale), (int)(14 * scale), (int)(14 * scale)),
            Margin = new Padding(0, 0, 0, (int)(12 * scale)),
        };

        TableLayoutPanel tblBat = new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 3,
        };
        tblBat.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

        Label lblBatDesc = new()
        {
            Text = "Protect lithium-ion battery lifespan and prevent wear when plugged into AC power. EC caps charging at the selected threshold.",
            Font = new Font("Segoe UI", 8.5F, FontStyle.Regular),
            ForeColor = Theme.TextMuted,
            AutoSize = true,
            Margin = new Padding((int)(2 * scale), (int)(2 * scale), (int)(2 * scale), (int)(10 * scale)),
        };
        tblBat.Controls.Add(lblBatDesc, 0, 0);

        FlowLayoutPanel flwBatControls = new()
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 0, 0, (int)(8 * scale)),
        };

        chkChgLim.Text = "Limit Peak Charge to:";
        chkChgLim.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        chkChgLim.Margin = new Padding((int)(3 * scale), (int)(4 * scale), (int)(8 * scale), (int)(4 * scale));
        chkChgLim.AutoSize = true;

        numChgLim.Margin = new Padding(0, (int)(2 * scale), (int)(4 * scale), (int)(2 * scale));
        numChgLim.Width = (int)(65 * scale);
        numChgLim.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        numChgLim.TextAlign = HorizontalAlignment.Center;

        Label lblBatPct = new()
        {
            Text = "%",
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            ForeColor = Theme.Text,
            AutoSize = true,
            Margin = new Padding(0, (int)(5 * scale), (int)(12 * scale), 0),
        };

        flwBatControls.Controls.Add(chkChgLim);
        flwBatControls.Controls.Add(numChgLim);
        flwBatControls.Controls.Add(lblBatPct);
        tblBat.Controls.Add(flwBatControls, 0, 1);

        FlowLayoutPanel flwBatPresets = new()
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, (int)(2 * scale), 0, (int)(4 * scale)),
        };

        Button btnBat60 = new()
        {
            Text = "60% (Max Lifespan - AC Desk)",
            AutoSize = true,
            Height = (int)(28 * scale),
            Font = new Font("Segoe UI", 8.5F, FontStyle.Bold),
            Margin = new Padding(0, 0, (int)(8 * scale), 0),
        };
        btnBat60.Click += (s, e) =>
        {
            chkChgLim.Checked = true;
            numChgLim.Value = 60;
        };

        Button btnBat80 = new()
        {
            Text = "80% (Balanced Health - Recommended)",
            AutoSize = true,
            Height = (int)(28 * scale),
            Font = new Font("Segoe UI", 8.5F, FontStyle.Bold),
            Margin = new Padding(0, 0, (int)(8 * scale), 0),
        };
        btnBat80.Click += (s, e) =>
        {
            chkChgLim.Checked = true;
            numChgLim.Value = 80;
        };

        Button btnBat100 = new()
        {
            Text = "100% (Full Capacity)",
            AutoSize = true,
            Height = (int)(28 * scale),
            Font = new Font("Segoe UI", 8.5F, FontStyle.Regular),
            Margin = new Padding(0, 0, (int)(8 * scale), 0),
        };
        btnBat100.Click += (s, e) =>
        {
            chkChgLim.Checked = false;
            numChgLim.Value = 100;
        };

        flwBatPresets.Controls.Add(btnBat60);
        flwBatPresets.Controls.Add(btnBat80);
        flwBatPresets.Controls.Add(btnBat100);
        tblBat.Controls.Add(flwBatPresets, 0, 2);

        gbBattery.Controls.Add(tblBat);
        mainLayout.Controls.Add(gbBattery, 0, 0);

        // =====================================================================
        // CARD 2: KEYBOARD & HARDWARE CONTROLS
        // =====================================================================
        GroupBox gbInput = new()
        {
            Text = "  ⌨️ KEYBOARD & HARDWARE INPUT  ",
            Dock = DockStyle.Top,
            AutoSize = true,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
            ForeColor = Theme.Accent,
            Padding = new Padding((int)(14 * scale), (int)(12 * scale), (int)(14 * scale), (int)(14 * scale)),
            Margin = new Padding(0, 0, 0, (int)(12 * scale)),
        };

        TableLayoutPanel tblInput = new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 2,
        };
        tblInput.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, (int)(220 * scale)));
        tblInput.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

        // Win/Fn Key Swap
        Label lblSwapDesc = new()
        {
            Text = "Hardware Key Mapping:",
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            ForeColor = Theme.Text,
            AutoSize = true,
            Margin = new Padding((int)(2 * scale), (int)(6 * scale), (int)(8 * scale), (int)(6 * scale)),
        };
        FlowLayoutPanel flwSwap = new()
        {
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            Margin = new Padding(0, (int)(2 * scale), 0, (int)(10 * scale)),
        };
        chkWinFnSwap.Text = "Swap Fn and Windows Keys (Hardware EC level)";
        chkWinFnSwap.Font = new Font("Segoe UI", 9F, FontStyle.Regular);
        chkWinFnSwap.AutoSize = true;
        Label lblSwapHint = new()
        {
            Text = "Swaps physical Fn and Win key functionality in the EC, preventing accidental Windows key presses while gaming.",
            Font = new Font("Segoe UI", 8.2F, FontStyle.Regular),
            ForeColor = Theme.TextMuted,
            AutoSize = true,
            Margin = new Padding((int)(2 * scale), (int)(2 * scale), 0, 0),
        };
        flwSwap.Controls.Add(chkWinFnSwap);
        flwSwap.Controls.Add(lblSwapHint);

        tblInput.Controls.Add(lblSwapDesc, 0, 0);
        tblInput.Controls.Add(flwSwap, 1, 0);

        // Keyboard Backlight
        Label lblKbDesc = new()
        {
            Text = "Keyboard Backlight:",
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            ForeColor = Theme.Text,
            AutoSize = true,
            Margin = new Padding((int)(2 * scale), (int)(8 * scale), (int)(8 * scale), (int)(6 * scale)),
        };

        FlowLayoutPanel flwLight = new()
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, (int)(4 * scale), 0, (int)(6 * scale)),
        };
        tbKeyLight.Width = (int)(180 * scale);
        tbKeyLight.Margin = new Padding(0, 0, (int)(8 * scale), 0);
        tbKeyLight.TickStyle = TickStyle.Both;

        lblKeyLightLow.Text = "Off";
        lblKeyLightLow.Font = new Font("Segoe UI", 8.5F, FontStyle.Bold);
        lblKeyLightLow.Margin = new Padding(0, (int)(12 * scale), (int)(6 * scale), 0);

        lblKeyLightHigh.Text = "Max";
        lblKeyLightHigh.Font = new Font("Segoe UI", 8.5F, FontStyle.Bold);
        lblKeyLightHigh.Margin = new Padding((int)(6 * scale), (int)(12 * scale), 0, 0);

        flwLight.Controls.Add(lblKeyLightLow);
        flwLight.Controls.Add(tbKeyLight);
        flwLight.Controls.Add(lblKeyLightHigh);

        tblInput.Controls.Add(lblKbDesc, 0, 1);
        tblInput.Controls.Add(flwLight, 1, 1);

        gbInput.Controls.Add(tblInput);
        mainLayout.Controls.Add(gbInput, 0, 1);

        // =====================================================================
        // CARD 3: SYSTEM POWER & ADVANCED EC
        // =====================================================================
        GroupBox gbPower = new()
        {
            Text = "  ⚡ SYSTEM POWER & ADVANCED EC  ",
            Dock = DockStyle.Top,
            AutoSize = true,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
            ForeColor = Theme.Accent,
            Padding = new Padding((int)(14 * scale), (int)(12 * scale), (int)(14 * scale), (int)(14 * scale)),
            Margin = new Padding(0, 0, 0, (int)(12 * scale)),
        };

        TableLayoutPanel tblPower = new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 2,
        };
        tblPower.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, (int)(220 * scale)));
        tblPower.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

        Label lblPwrDesc = new()
        {
            Text = "Global Power Profile:",
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            ForeColor = Theme.Text,
            AutoSize = true,
            Margin = new Padding((int)(2 * scale), (int)(6 * scale), (int)(8 * scale), (int)(6 * scale)),
        };
        cboPerfMode.Width = (int)(220 * scale);
        cboPerfMode.Margin = new Padding(0, (int)(4 * scale), 0, (int)(8 * scale));

        tblPower.Controls.Add(lblPwrDesc, 0, 0);
        tblPower.Controls.Add(cboPerfMode, 1, 0);

        Label lblFanAdvDesc = new()
        {
            Text = "EC Operating Mode (Advanced):",
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            ForeColor = Theme.Text,
            AutoSize = true,
            Margin = new Padding((int)(2 * scale), (int)(6 * scale), (int)(8 * scale), (int)(6 * scale)),
        };

        cboFanMode.Width = (int)(220 * scale);
        cboFanMode.Margin = new Padding(0, (int)(4 * scale), 0, (int)(4 * scale));

        tblPower.Controls.Add(lblFanAdvDesc, 0, 1);
        tblPower.Controls.Add(cboFanMode, 1, 1);

        page.Controls.Add(mainLayout);
        Theme.ApplyTo(page);
    }

    /// <summary>
    /// Adds the overlay toggle to the Options menu.
    /// </summary>
    private void AddOverlayMenuItem()
    {
        tsiOverlay = new ToolStripMenuItem("Show &overlay")
        {
            CheckOnClick = true,
            ToolTipText = "Show a click-through readout on top of other windows. " +
                "Works over the desktop and windowed or borderless games, but not " +
                "over a game in exclusive fullscreen.",
        };
        tsiOverlay.CheckedChanged += ToggleOverlay;

        tsiOsd = new ToolStripMenuItem("Show in-&game OSD (RTSS)")
        {
            CheckOnClick = true,
            ToolTipText = "Send these readings to RivaTuner Statistics Server, which " +
                "draws them inside the game - including in exclusive fullscreen, " +
                "where a normal window cannot appear. Requires RTSS to be running.",
        };
        tsiOsd.CheckedChanged += ToggleOsd;

        // sits with the other view options
        if (tsiOptions?.DropDownItems is not null)
        {
            tsiOptions.DropDownItems.Insert(0, tsiOverlay);
            tsiOptions.DropDownItems.Insert(1, tsiOsd);
            tsiOptions.DropDownItems.Insert(2, new ToolStripSeparator());
        }
    }

    private void ToggleOsd(object sender, EventArgs e)
    {
        if (!tsiOsd.Checked)
        {
            // hand the slot back so our text does not linger in the overlay
            Osd.Clear();
            UpdatePolling();
            return;
        }

        if (!Osd.Write("msi_gf63_tuner starting..."))
        {
            // ShowWarning here would offer Yes/No, which makes no sense for a
            // statement of fact; ShowInfo defaults to a single OK button.
            Utils.ShowInfo(
                "Could not reach RivaTuner Statistics Server." +
                Environment.NewLine + Environment.NewLine +
                "RTSS must be running for the in-game OSD. It is installed " +
                "alongside MSI Afterburner, or separately from guru3d.com.",
                "RTSS not running", MessageBoxButtons.OK);
            tsiOsd.Checked = false;
        }
        UpdatePolling();
    }

    private void ToggleOverlay(object sender, EventArgs e)
    {
        if (tsiOverlay.Checked)
        {
            Overlay ??= new OverlayForm();
            Overlay.Show();
        }
        else
        {
            Overlay?.Hide();
        }
        UpdatePolling();
    }

    /// <summary>
    /// Pulls the number off the front of a formatted label such as "43%" or
    /// "2515 RPM", for reuse in the overlay.
    /// </summary>
    private static int ParseLeadingInt(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        int end = 0;
        while (end < text.Length && char.IsDigit(text[end]))
        {
            end++;
        }
        return end == 0 ? 0 : int.Parse(text.Substring(0, end));
    }

    /// <summary>
    /// Pulls a fresh sensor snapshot into the Monitoring tab.
    /// </summary>
    private void RefreshSensors()
    {
        bool tabVisible = tcMain.SelectedTab == TabMonitoring;
        bool overlayUp = Overlay is not null && !Overlay.IsDisposed && Overlay.Visible;
        bool osdOn = tsiOsd is not null && tsiOsd.Checked;

        if (Sensors is null || (!tabVisible && !overlayUp && !osdOn))
        {
            MonitorWasVisible = false;
            return;
        }

        SensorSnapshot s;
        try
        {
            // SystemSensors enforces its own minimum energy window, so no
            // priming read is needed here - one would only restart that
            // window and delay the first real figure.
            MonitorWasVisible = true;
            s = Sensors.Read();
        }
        catch { return; }

        static string Watts(double? w) => w.HasValue ? $"{w.Value:F1} W" : "n/a";

        int fanPct = ParseLeadingInt(lblFanSpdC.Text);
        int fanRpm = ParseLeadingInt(lblRPM1.Text);

        // CpuTempC is only filled in by Afterburner, which is optional - but
        // the EC reports CPU temperature to us regardless, via the service.
        // Without this the overlay and the OSD showed no CPU temperature at
        // all unless Afterburner happened to be running.
        if (!s.CpuTempC.HasValue)
        {
            int ecTemp = ParseLeadingInt(lblTempC.Text);
            if (ecTemp > 0)
            {
                s.CpuTempC = ecTemp;
            }
        }

        // same for the discrete GPU, when Level Zero cannot supply one
        foreach (GpuReading gpu in s.Gpus)
        {
            if (gpu.Discrete && !gpu.TempC.HasValue)
            {
                int ecGpu = ParseLeadingInt(lblTempG.Text);
                if (ecGpu > 0)
                {
                    gpu.TempC = ecGpu;
                }
            }
        }

        if (overlayUp)
        {
            Overlay.FanPercent = fanPct;
            Overlay.FanRpm = fanRpm;
            Overlay.Update(s);
        }

        if (osdOn)
        {
            Osd.Write(RtssOsd.Format(s, fanPct, fanRpm));
        }

        if (!tabVisible)
        {
            return;
        }

        lblCpuLoad.Text = $"{s.CpuLoadPercent:F0}%";
        lblCpuClock.Text = s.CpuMHz > 0 ? $"{s.CpuMHz:F0} MHz" : "--";
        lblCpuPkgW.Text = Watts(s.CpuPackageWatts);
        lblCpuCoreW.Text = Watts(s.CpuCoresWatts);

        lblRamUsed.Text = s.RamTotalMB > 0
            ? $"{s.RamUsedMB / 1024:F1} / {s.RamTotalMB / 1024:F1} GB"
            : "--";
        lblSysPower.Text = s.BatteryWatts.HasValue
            ? $"{s.BatteryWatts.Value:F1} W"
            : "on AC";
        lblFps.Text = s.Fps.HasValue && s.Fps.Value > 0
            ? $"{s.Fps.Value:F0} FPS"
            : "--";
        List<string> src = ["Windows counters"];
        if (s.LevelZeroActive) { src.Add("Intel Level Zero"); }
        if (s.IgclActive) { src.Add("Intel IGCL"); }
        if (s.AfterburnerActive) { src.Add("MSI Afterburner"); }
        lblSensorSrc.Text = string.Join(" + ", src) +
            (s.AfterburnerActive ? string.Empty : "  (no kernel driver)");

        if (s.CpuTempC.HasValue)
        {
            lblTempC.Text = $"{s.CpuTempC.Value:F0}°C";
        }

        foreach (GpuReading g in s.Gpus)
        {
            if (!GpuLabels.TryGetValue(g.Luid, out (Label Load, Label Clock, Label Vram, Label Watts, Label Temp) l))
            {
                continue;
            }

            l.Load.Text = $"{g.LoadPercent:F0}%";
            l.Vram.Text = g.VramTotalMB > 0
                ? $"{g.VramUsedMB:F0} / {g.VramTotalMB:F0} MB"
                : "--";
            // Windows exposes no GPU clock at all, and no power for a discrete
            // GPU: both only arrive when Afterburner is running. Say so rather
            // than showing a misleading zero.
            l.Clock.Text = g.CoreMHz.HasValue
                ? (g.MemoryMHz.HasValue
                    ? $"{g.CoreMHz.Value:F0} MHz   (mem {g.MemoryMHz.Value:F0} MHz)"
                    : $"{g.CoreMHz.Value:F0} MHz")
                : "--";

            // Level Zero reports the die temperature even when the card is
            // parked, where the EC's GPU sensor just reads 0.
            if (g.TempC.HasValue && l.Temp is not null)
            {
                l.Temp.Text = $"{g.TempC.Value:F0}°C";
            }
            l.Watts.Text = g.Watts.HasValue
                ? $"{g.Watts.Value:F1} W"
                : g.PowerOnCpuPackage
                    ? "shared with CPU package"
                    : (s.AfterburnerActive ? "idle" : "needs Afterburner");
        }
    }
    #endregion

    private static Label FanCurveLabel(string text, float scale, int tabIdx, ContentAlignment align = ContentAlignment.MiddleRight, Color? foreColor = null)
    {
        Label lbl = new()
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding((int)(3 * scale)),
            Padding = new Padding(0, 0, 0, (int)(3 * scale)),
            TabIndex = tabIdx,
            Text = text,
            TextAlign = align,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
        };
        // created after Theme.Apply() ran, so theme it here
        Theme.ApplyTo(lbl);
        if (foreColor.HasValue)
        {
            lbl.ForeColor = foreColor.Value;
        }
        return lbl;
    }

    private NumericUpDown FanCurveNUD(int tag, float scale, int tabIdx)
    {
        NumericUpDown nud = new()
        {
            Dock = DockStyle.Fill,
            Height = (int)(24 * scale),
            Margin = new Padding((int)(3 * scale)),
            TabIndex = tabIdx,
            Tag = tag,
            TextAlign = HorizontalAlignment.Center,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
        };
        nud.KeyDown += new KeyEventHandler(NUDKeyDown);
        // the fan curve controls are built long after Theme.Apply() ran in the
        // constructor, so they have to be themed as they are created
        Theme.ApplyTo(nud);
        return nud;
    }

    private void NUDKeyDown(object sender, KeyEventArgs e)
    {
        NumericUpDown nud = (NumericUpDown)sender;
        if (nud.Focused)
        {
            switch (e.KeyCode)
            {
                case Keys.PageUp:
                    nud.Value += Math.Min(10, nud.Maximum - nud.Value);
                    break;
                case Keys.PageDown:
                    nud.Value -= Math.Min(10, nud.Value - nud.Minimum);
                    break;
            }
        }
    }

    private void DisableAll()
    {
        ToggleSvcCmds(false);
        tsiSaveConf.Enabled = false;
        tsiProfAdd.Enabled = false;
        tsiProfEdit.Enabled = false;
        tsiProfDel.Enabled = false;
        tsiECtoConf.Enabled = false;

        btnProfAdd.Enabled = false;
        btnProfDel.Enabled = false;
        btnFanSel.Enabled = false;
        cboProfSel.Enabled = false;
        cboProfPerfMode.Enabled = false;
        if (tbFanSpds is not null)
        {
            for (int i = 0; i < tbFanSpds.Length; i++)
            {
                tbFanSpds[i].Enabled = false;
                numFanSpds[i].Enabled = false;
                if (i != 0)
                {
                    numUpTs[i - 1].Enabled = false;
                    numDownTs[i - 1].Enabled = false;
                }
            }
        }

        cboPerfMode.Enabled = false;
        cboFanMode.Enabled = false;
        chkWinFnSwap.Enabled = false;
        chkChgLim.Enabled = false;
        numChgLim.Enabled = false;
        lblKeyLightLow.Enabled = false;
        lblKeyLightHigh.Enabled = false;
        tbKeyLight.Enabled = false;

        txtAuthor.Enabled = false;
        btnGetModel.Enabled = false;
    }

    private void tcMain_SelectedIndexChanged(object sender, EventArgs e)
    {
        UpdatePolling();
    }

    /// <summary>
    /// Runs the sensor poll whenever anything needs it.
    /// </summary>
    /// <remarks>
    /// This used to start the timer only on the Monitoring tab and stop it
    /// everywhere else, which was fine when that tab was the only consumer.
    /// The overlay and the RTSS OSD are used precisely when the window is NOT
    /// in front, so that gating stopped feeding them the moment you switched
    /// tabs or alt-tabbed into a game - both simply went blank.
    /// </remarks>
    private void UpdatePolling()
    {
        bool monitoring = tcMain.SelectedIndex == 3;
        bool overlayUp = Overlay is not null && !Overlay.IsDisposed && Overlay.Visible;
        bool osdOn = tsiOsd is not null && tsiOsd.Checked;

        if (monitoring || overlayUp || osdOn)
        {
            PollEC();
            tmrPoll.Start();
        }
        else
        {
            tmrPoll.Stop();
        }
    }

    private void ToggleSvcCmds(bool enable)
    {
        tsiApply.Enabled = enable;
        tsiRevert.Enabled = enable;
        chkFullBlast.Enabled = enable;
        btnApply.Enabled = enable;
        btnRevert.Enabled = enable;
    }

    private void UpdateStatus(StatusCode status, int data = 0)
    {
        if (AppStatus.Code == status)
        {
            AppStatus.Repeats++;
        }
        else
        {
            AppStatus.Code = status;
            AppStatus.Repeats = 0;
        }

        lblStatus.ForeColor = AppStatus.Code switch
        {
            StatusCode.ServiceCommandFail or StatusCode.ServiceTimeout => Theme.Accent,
            StatusCode.ConfApplied or StatusCode.FullBlastToggled => Theme.AccentHover,
            _ => Theme.TextMuted,
        };

        // set status text
        bool persist = false;
        switch (AppStatus.Code)
        {
            case StatusCode.ServiceCommandFail:
                persist = true;
                lblStatus.Text = Strings.GetString("statSvcErr", (Command)data);
                break;
            case StatusCode.ServiceResponseEmpty:
                lblStatus.Text = Strings.GetString("statResponseEmpty");
                break;
            case StatusCode.ServiceTimeout:
                persist = true;
                lblStatus.Text = Strings.GetString("statSvcTimeout");
                break;
            case StatusCode.NoConfig:
                persist = true;
                lblStatus.Text = Strings.GetString("statNoConf");
                break;
            case StatusCode.ConfLoading:
                lblStatus.Text = Strings.GetString("statConfLoading");
                break;
            case StatusCode.ConfApplied:
                lblStatus.Text = Strings.GetString("statConfApplied");
                break;
            case StatusCode.FullBlastToggled:
                lblStatus.Text = Strings.GetString("statFBToggled");
                break;
            default:
                persist = true;
                AppStatus.Repeats = 0;
                lblStatus.Text = "Ready";
                break;
        }

        if (AppStatus.Repeats > 0)
        {
            lblStatus.Text += $" (x{AppStatus.Repeats + 1})";
        }

        tmrStatusReset.Stop();
        if (!persist)
        {
            tmrStatusReset.Start();
        }
    }

    private void SendSvcMessage(ServiceCommand command)
    {
        IPCClient.PushMessage(command);
        tmrSvcTimeout.Start();
    }

    private static void RunUpdater(string args)
    {
        string path = Path.GetFullPath(@".\Updater.exe");
        try
        {
            Process.Start(path, args);
        }
        catch (Win32Exception ex)
        {
            // catch the exception that occurs if the Updater is not found
            if (ex.ErrorCode == -2147467259 && ex.NativeErrorCode == 2)
            {
                Utils.ShowError(
                    "Updater.exe not found!\n" +
                    $"MSI GF63 Tuner expected it to be located at: {path}\n\n" +
                    "This is likely a bug; please report it along with what you were doing before this message appeared.");
            }
            else
            {
                throw;
            }
        }
    }

    #region System Tray & RAM Optimization
    private void InitSystemTray()
    {
        cmsTray = new ContextMenuStrip();
        Theme.Apply(cmsTray);

        ToolStripMenuItem tmiHeader = new("MSI GF63 Tuner")
        {
            Font = new Font(Font, FontStyle.Bold),
            ForeColor = Theme.Accent,
            Enabled = false
        };
        cmsTray.Items.Add(tmiHeader);
        cmsTray.Items.Add(new ToolStripSeparator());

        tmiTrayProfiles = new ToolStripMenuItem("Fan Profile");
        cmsTray.Items.Add(tmiTrayProfiles);

        tmiTrayFullBlast = new ToolStripMenuItem("Cooler Boost (100% Fan)", null, (s, e) =>
        {
            chkFullBlast.Checked = !chkFullBlast.Checked;
            ProfileOsdToast.ShowToast(chkFullBlast.Checked ? "COOLER BOOST: ACTIVE" : "COOLER BOOST: DISABLED");
        });
        cmsTray.Items.Add(tmiTrayFullBlast);

        cmsTray.Items.Add(new ToolStripSeparator());

        ToolStripMenuItem tmiOpen = new("Open Tuner", null, (s, e) => RestoreFromTray())
        {
            Font = new Font(Font, FontStyle.Bold)
        };
        cmsTray.Items.Add(tmiOpen);

        ToolStripMenuItem tmiExit = new("Exit", null, (s, e) =>
        {
            allowExplicitExit = true;
            Close();
        });
        cmsTray.Items.Add(tmiExit);

        notifyIcon = new NotifyIcon
        {
            Icon = this.Icon,
            ContextMenuStrip = cmsTray,
            Text = "MSI GF63 Tuner",
            Visible = true
        };

        notifyIcon.DoubleClick += (s, e) => RestoreFromTray();
        cmsTray.Opening += (s, e) => UpdateTrayMenuState();
    }

    private void UpdateTrayMenuState()
    {
        if (Config?.CpuFan?.FanProfs == null) { return; }

        tmiTrayProfiles.DropDownItems.Clear();
        for (int i = 0; i < Config.CpuFan.FanProfs.Count; i++)
        {
            int profIdx = i;
            string profName = Config.CpuFan.FanProfs[i].Name;
            ToolStripMenuItem item = new(profName, null, (s, e) =>
            {
                if (cboProfSel.Items.Count > profIdx)
                {
                    cboProfSel.SelectedIndex = profIdx;
                    ApplyConf(this, EventArgs.Empty);
                    ProfileOsdToast.ShowToast(profName);
                }
            })
            {
                Checked = (cboProfSel.SelectedIndex == profIdx)
            };
            tmiTrayProfiles.DropDownItems.Add(item);
        }

        tmiTrayFullBlast.Checked = chkFullBlast.Checked;
    }

    private void HideToTray()
    {
        Hide();
        tmrPoll.Stop();
        TrimMemory();

        if (!hasShownTrayBalloon && notifyIcon != null)
        {
            hasShownTrayBalloon = true;
            notifyIcon.ShowBalloonTip(
                1500,
                "MSI GF63 Tuner",
                "Running in background. Telemetry & fan control active.",
                ToolTipIcon.Info);
        }
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
        tmrPoll.Start();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);

    private static void TrimMemory()
    {
        try
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true);
            using (Process proc = Process.GetCurrentProcess())
            {
                SetProcessWorkingSetSize(proc.Handle, (IntPtr)(-1), (IntPtr)(-1));
            }
        }
        catch
        {
        }
    }
    #endregion
    #endregion  // Private methods
}
