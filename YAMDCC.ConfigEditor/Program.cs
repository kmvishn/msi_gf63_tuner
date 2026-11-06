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
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using YAMDCC.Common;
using YAMDCC.Common.Configs;
using YAMDCC.Common.Dialogs;
using YAMDCC.Common.Logs;

#nullable enable

namespace YAMDCC.ConfigEditor;

internal static class Program
{
    public static YAMDCC.Service.FanControlService? EmbeddedService { get; private set; }

    private static Logger CreateLogger()
    {
        return new Logger
        {
            LogDir = Paths.Logs,
            ConsoleLevel = LogLevel.None,
            FileLevel = CommonConfig.GetLogLevel(),
        };
    }

    public static void StartEmbeddedService()
    {
        if (EmbeddedService == null)
        {
            try
            {
                Logger svcLog = CreateLogger();
                EmbeddedService = new YAMDCC.Service.FanControlService(svcLog);
                EmbeddedService.StartInProcess();
            }
            catch (Exception ex)
            {
                Utils.ShowError("Failed to initialize embedded fan control engine:\n\n" + ex.Message);
            }
        }
    }

    public static void StopEmbeddedService()
    {
        try
        {
            EmbeddedService?.StopInProcess();
            EmbeddedService = null;
        }
        catch { }
    }

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string AppID);

    /// <summary>
    /// The main entry point for the application.
    /// </summary>
    [STAThread]
    private static void Main(string[] args)
    {
        try
        {
            SetCurrentProcessExplicitAppUserModelID("MSI.GF63Tuner.Suite");
        }
        catch { }

        // Support running as standalone Windows Service if requested via flag
        if (args != null && args.Length > 0 &&
            (args[0].Equals("--service", StringComparison.OrdinalIgnoreCase) ||
             args[0].Equals("-s", StringComparison.OrdinalIgnoreCase) ||
             args[0].Equals("/service", StringComparison.OrdinalIgnoreCase)))
        {
            Logger svcLog = CreateLogger();
            ServiceBase.Run(new YAMDCC.Service.FanControlService(svcLog));
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Application.ThreadException += new ThreadExceptionEventHandler(ThreadException);
        AppDomain.CurrentDomain.UnhandledException += new UnhandledExceptionEventHandler(UnhandledException);

        if (!Utils.IsAdmin())
        {
            Utils.ShowError(Strings.GetString("dlgNoAdmin"));
            return;
        }

        // multi-instance detection
        // NOTE: GUID is used to prevent conflicts with potential
        // identically named but different program
        // based on: https://stackoverflow.com/a/184143
        using (Mutex mutex = new(true, "{10572c4f-894e-4837-b31c-356d70c44e19}", out bool createdNew))
        {
            // this instance is the first to open; proceed as normal:
            if (createdNew)
            {
                // Make sure the application data directory structure is set up
                // because apparently windows services don't know how to create
                // directories:
                Directory.CreateDirectory(Paths.Logs);

                // Set working directory to same location as executable.
                // fixes bugs related to calling other YAMDCC programs
                // when launched with a different working directory.
                Directory.SetCurrentDirectory(Path.GetDirectoryName(
                    Assembly.GetEntryAssembly().Location));

                // Check for existence of MSI Center's various
                // services, and warn the user if they're installed.

                if (Utils.IsMSIServiceRunning(out string[] services))
                {
                    StringBuilder sb = new();
                    foreach (string svc in services)
                    {
                        sb.Append($"- {svc}\n");
                    }

                    if (Utils.ShowWarning(
                        "MSI GF63 Tuner has detected the following MSI Center services are running:\n\n" +
                        $"{sb}\n\n" +
                        "It is highly recommended that you uninstall MSI Center before using " +
                        "MSI GF63 Tuner, otherwise the two *WILL* conflict with each other.\n\n" +
                        "Proceed anyways?", "MSI Center detected!",
                        MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                    {
                        if (Utils.StopService("yamdccsvc"))
                        {
                            Utils.UninstallService("yamdccsvc");
                        }
                        return;
                    }
                }

                // If an external service is not already running, start the embedded fan control service directly in-process
                if (!Utils.ServiceRunning("yamdccsvc") && !Utils.ServiceRunning("msi_gf63_tuner"))
                {
                    StartEmbeddedService();
                }

                // Start the program:
                Start(args);
            }
            else
            {
                // YAMDCC is already running, focus
                // (and restore, if minimised) its window:
                Process current = Process.GetCurrentProcess();
                foreach (Process process in Process.GetProcessesByName(current.ProcessName))
                {
                    if (process.Id != current.Id)
                    {
                        if (process.MainWindowHandle != IntPtr.Zero)
                        {
                            ShowWindow(process.MainWindowHandle, 9);    // SW_RESTORE
                            SetForegroundWindow(process.MainWindowHandle);
                        }
                        break;
                    }
                }
            }
        }
    }

    private static void Start(string[] args)
    {
        if (CommonConfig.GetECtoConfState() == ECtoConfState.PendingReboot)
        {
            if (Utils.ShowWarning(Strings.GetString("dlgECtoConfReboot"),
                "Reboot pending", MessageBoxDefaultButton.Button2) == DialogResult.Yes)
            {
                CommonConfig.SetECtoConfState(ECtoConfState.None);
            }
            else
            {
                return;
            }
        }

        // --overlay / --osd / --minimized startup options
        bool overlay = false, osd = false, minimized = false;
        foreach (string a in args ?? [])
        {
            string arg = a.TrimStart('-', '/').ToLowerInvariant();
            if (arg == "overlay") { overlay = true; }
            else if (arg == "osd") { osd = true; }
            else if (arg is "minimized" or "tray" or "min" or "m") { minimized = true; }
        }
        Application.Run(new MainForm(overlay, osd, minimized));
    }

    [DllImport("User32")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("User32")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private static void ThreadException(object sender, ThreadExceptionEventArgs e)
    {
        new CrashDialog(e.Exception).ShowDialog();
    }

    private static void UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        new CrashDialog((Exception)e.ExceptionObject).ShowDialog();
    }
}
