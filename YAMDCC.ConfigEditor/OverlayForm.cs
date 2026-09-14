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
using System.Drawing;
using System.Drawing.Text;
using System.Windows.Forms;
using YAMDCC.Common.Monitoring;

namespace YAMDCC.ConfigEditor;

/// <summary>
/// A borderless, click-through, always-on-top readout of the live sensors.
/// </summary>
/// <remarks>
/// <para>
/// This is a normal top-most window, not an injected overlay. RivaTuner
/// Statistics Server draws inside a game by injecting a DLL and hooking the
/// Direct3D present call; that is invasive and anti-cheat software treats it
/// as an attack, so it is deliberately not done here.
/// </para>
/// <para>
/// The practical consequence: this shows over the desktop and over windowed or
/// borderless-windowed games - which is how most modern titles run - but not
/// over a game in exclusive fullscreen, because nothing can draw above that
/// without hooking it.
/// </para>
/// </remarks>
internal sealed class OverlayForm : Form
{
    #region native
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;   // clicks pass through
    private const int WS_EX_TOOLWINDOW = 0x00000080;    // keep out of alt-tab
    private const int WS_EX_NOACTIVATE = 0x08000000;    // never steal focus

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    /// <summary>Never take focus when shown.</summary>
    protected override bool ShowWithoutActivation => true;
    #endregion

    // colours picked to stay readable over arbitrary game content
    private static readonly Color ColGpu = Color.FromArgb(0x6E, 0xE7, 0x7B);
    private static readonly Color ColIGpu = Color.FromArgb(0x7A, 0xC8, 0xFF);
    private static readonly Color ColCpu = Color.FromArgb(0x6E, 0xD8, 0xE7);
    private static readonly Color ColRam = Color.FromArgb(0xE7, 0xD1, 0x6E);
    private static readonly Color ColFan = Color.FromArgb(0xFF, 0x9F, 0x5A);
    private static readonly Color ColValue = Color.FromArgb(0xFF, 0xBF, 0x3F);
    private static readonly Color ColUnit = Color.FromArgb(0xB0, 0xB0, 0xB0);

    private readonly Font _labelFont = new("Consolas", 11F, FontStyle.Bold);
    private readonly Font _valueFont = new("Consolas", 11F, FontStyle.Bold);
    private readonly Font _unitFont = new("Consolas", 8F, FontStyle.Bold);

    private SensorSnapshot _snapshot;

    public OverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;

        // pure black is keyed out, leaving just the text floating
        BackColor = Color.Black;
        TransparencyKey = Color.Black;

        Size = new Size(430, 132);
        Location = new Point(12, 12);
    }

    /// <summary>
    /// Hands the overlay a fresh reading and repaints.
    /// </summary>
    public void Update(SensorSnapshot snapshot)
    {
        _snapshot = snapshot;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_snapshot is null)
        {
            return;
        }

        Graphics g = e.Graphics;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        int y = 2;
        const int rowHeight = 21;

        foreach (GpuReading gpu in _snapshot.Gpus)
        {
            List<(string Text, string Unit)> cells = [];
            if (gpu.TempC.HasValue) { cells.Add(($"{gpu.TempC.Value:F0}", "°C")); }
            cells.Add(($"{gpu.LoadPercent:F0}", "%"));
            if (gpu.VramUsedMB > 0) { cells.Add(($"{gpu.VramUsedMB:F0}", "MB")); }
            if (gpu.CoreMHz.HasValue) { cells.Add(($"{gpu.CoreMHz.Value:F0}", "MHz")); }
            if (gpu.Watts.HasValue) { cells.Add(($"{gpu.Watts.Value:F1}", "W")); }

            DrawRow(g, y, gpu.Discrete ? "GPU" : "iGPU",
                gpu.Discrete ? ColGpu : ColIGpu, cells);
            y += rowHeight;
        }

        List<(string, string)> cpu = [];
        if (_snapshot.CpuTempC.HasValue) { cpu.Add(($"{_snapshot.CpuTempC.Value:F0}", "°C")); }
        cpu.Add(($"{_snapshot.CpuLoadPercent:F0}", "%"));
        if (_snapshot.CpuMHz > 0) { cpu.Add(($"{_snapshot.CpuMHz:F0}", "MHz")); }
        if (_snapshot.CpuPackageWatts.HasValue) { cpu.Add(($"{_snapshot.CpuPackageWatts.Value:F1}", "W")); }
        DrawRow(g, y, "CPU", ColCpu, cpu);
        y += rowHeight;

        DrawRow(g, y, "FAN", ColFan, [($"{FanPercent}", "%"), ($"{FanRpm}", "RPM")]);
        y += rowHeight;

        DrawRow(g, y, "RAM", ColRam, [($"{_snapshot.RamUsedMB:F0}", "MB")]);
        y += rowHeight;

        if (_snapshot.Fps.HasValue)
        {
            DrawRow(g, y, "FPS", Color.White, [($"{_snapshot.Fps.Value:F0}", "FPS")]);
        }
    }

    /// <summary>Fan duty, supplied by the owner from the service's readings.</summary>
    public int FanPercent { get; set; }

    /// <summary>Fan speed in RPM, supplied by the owner.</summary>
    public int FanRpm { get; set; }

    private void DrawRow(Graphics g, int y, string label, Color labelColour,
        List<(string Text, string Unit)> cells)
    {
        const int labelWidth = 52;
        const int cellWidth = 74;

        TextRenderer.DrawText(g, label, _labelFont, new Point(4, y), labelColour,
            TextFormatFlags.NoPadding);

        int x = 4 + labelWidth;
        foreach ((string text, string unit) in cells)
        {
            Size size = TextRenderer.MeasureText(g, text, _valueFont, Size.Empty,
                TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, text, _valueFont, new Point(x, y), ColValue,
                TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, unit, _unitFont, new Point(x + size.Width + 1, y + 4),
                ColUnit, TextFormatFlags.NoPadding);
            x += cellWidth;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _labelFont?.Dispose();
            _valueFont?.Dispose();
            _unitFont?.Dispose();
        }
        base.Dispose(disposing);
    }
}
