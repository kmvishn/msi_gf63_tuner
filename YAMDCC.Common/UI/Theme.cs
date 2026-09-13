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
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace YAMDCC.Common.UI;

/// <summary>
/// A dark "MSI Dragon" theme (near-black with red accents) for Windows Forms.
/// </summary>
/// <remarks>
/// Windows Forms has no built-in dark mode, so this walks the control tree and
/// restyles each control by type. Call <see cref="Apply(Form)"/> once, after
/// <c>InitializeComponent()</c>.
/// </remarks>
public static class Theme
{
    #region Palette
    /// <summary>Form background. Deep near-black rather than pure black, so borders stay visible.</summary>
    public static readonly Color Background = Color.FromArgb(0x14, 0x14, 0x14);

    /// <summary>Panels, tab pages and other raised areas.</summary>
    public static readonly Color Surface = Color.FromArgb(0x1E, 0x1E, 0x1E);

    /// <summary>Text boxes, combo boxes and other input fields.</summary>
    public static readonly Color SurfaceAlt = Color.FromArgb(0x26, 0x26, 0x26);

    /// <summary>Hovered menu/​list rows.</summary>
    public static readonly Color SurfaceHover = Color.FromArgb(0x32, 0x32, 0x32);

    /// <summary>Hairlines and control borders.</summary>
    public static readonly Color Border = Color.FromArgb(0x3A, 0x3A, 0x3A);

    /// <summary>Primary text.</summary>
    public static readonly Color Text = Color.FromArgb(0xED, 0xED, 0xED);

    /// <summary>Secondary/dimmed text.</summary>
    public static readonly Color TextMuted = Color.FromArgb(0x9B, 0x9B, 0x9B);

    /// <summary>Text on a disabled control.</summary>
    public static readonly Color TextDisabled = Color.FromArgb(0x5A, 0x5A, 0x5A);

    /// <summary>MSI red. Used for selection, focus and emphasis.</summary>
    public static readonly Color Accent = Color.FromArgb(0xE4, 0x00, 0x2B);

    /// <summary>Lighter red for hover states.</summary>
    public static readonly Color AccentHover = Color.FromArgb(0xFF, 0x2E, 0x4D);

    /// <summary>Darker red for pressed states.</summary>
    public static readonly Color AccentPressed = Color.FromArgb(0xB3, 0x00, 0x1F);
    #endregion

    #region Dark title bar
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    // 20 on current Windows 10/11; 19 on Windows 10 builds 18985 and earlier.
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;

    /// <summary>
    /// Asks DWM to render this window's title bar dark.
    /// </summary>
    /// <remarks>
    /// Silently does nothing on Windows versions that don't support it.
    /// </remarks>
    public static void UseDarkTitleBar(IWin32Window window)
    {
        if (window is null)
        {
            return;
        }

        try
        {
            int on = 1;
            if (DwmSetWindowAttribute(window.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(window.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref on, sizeof(int));
            }
        }
        catch (DllNotFoundException)
        {
            // dwmapi.dll missing (shouldn't happen on anything we support)
        }
        catch (EntryPointNotFoundException) { }
    }
    #endregion

    /// <summary>
    /// Applies the dark theme to a <see cref="Form"/> and everything in it.
    /// </summary>
    /// <param name="form">The form to restyle.</param>
    public static void Apply(Form form)
    {
        if (form is null)
        {
            return;
        }

        form.BackColor = Background;
        form.ForeColor = Text;

        // the handle must exist before DWM will accept the attribute, and it
        // needs re-applying if the handle is ever recreated.
        if (form.IsHandleCreated)
        {
            UseDarkTitleBar(form);
        }
        form.HandleCreated += (s, e) => UseDarkTitleBar((Form)s);

        ApplyToChildren(form);
    }

    /// <summary>
    /// Sets a control's background to <see cref="Color.Transparent"/> so the
    /// form or tab page shows through, falling back to a solid colour on
    /// controls that don't support it.
    /// </summary>
    /// <remarks>
    /// Only controls with <c>ControlStyles.SupportsTransparentBackColor</c>
    /// accept a transparent background; the rest throw
    /// <see cref="ArgumentException"/>. That flag is protected, so the
    /// supported way to find out is to try it. Without this, a single
    /// unsupported control anywhere in the tree crashed the whole app.
    /// </remarks>
    private static void SetTransparentBack(Control c)
    {
        // pick the colour the control would blend into, for the fallback
        Color solid = c.Parent is TabPage or Panel ? Surface : Background;

        try
        {
            c.BackColor = Color.Transparent;
        }
        catch (ArgumentException)
        {
            c.BackColor = solid;
        }
    }

    /// <summary>
    /// Applies the dark theme to a <see cref="ToolTip"/>.
    /// </summary>
    /// <remarks>
    /// A <see cref="ToolTip"/> is a component, not a control, so it never
    /// appears in a form's <see cref="Control.Controls"/> collection and the
    /// recursive walk cannot reach it - it has to be themed explicitly.
    /// Its BackColor/ForeColor are also ignored unless
    /// <see cref="ToolTip.OwnerDraw"/> is set.
    /// </remarks>
    public static void Apply(ToolTip tip)
    {
        if (tip is null)
        {
            return;
        }

        tip.OwnerDraw = true;
        tip.BackColor = Surface;
        tip.ForeColor = Text;

        tip.Draw -= DrawToolTip;
        tip.Draw += DrawToolTip;
    }

    private static void DrawToolTip(object sender, DrawToolTipEventArgs e)
    {
        using (SolidBrush b = new(Surface))
        {
            e.Graphics.FillRectangle(b, e.Bounds);
        }
        using (Pen p = new(Accent))
        {
            e.Graphics.DrawRectangle(p, 0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1);
        }

        Rectangle text = Rectangle.Inflate(e.Bounds, -5, -3);
        TextRenderer.DrawText(
            e.Graphics, e.ToolTipText, e.Font, text, Text,
            TextFormatFlags.Left | TextFormatFlags.Top |
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
    }

    /// <summary>
    /// Applies the dark theme to every child of a container.
    /// </summary>
    public static void ApplyToChildren(Control parent)
    {
        foreach (Control c in parent.Controls)
        {
            ApplyTo(c);
        }
    }

    /// <summary>
    /// Applies the dark theme to a single control, then recurses into it.
    /// </summary>
    public static void ApplyTo(Control c)
    {
        switch (c)
        {
            case MenuStrip ms:
                ms.BackColor = Surface;
                ms.ForeColor = Text;
                ms.Renderer = new DarkToolStripRenderer();
                StyleToolStripItems(ms.Items);
                break;

            case StatusStrip ss:
                ss.BackColor = Surface;
                ss.ForeColor = TextMuted;
                ss.Renderer = new DarkToolStripRenderer();
                break;

            case Button b:
                b.FlatStyle = FlatStyle.Flat;
                b.BackColor = SurfaceAlt;
                b.ForeColor = Text;
                b.UseVisualStyleBackColor = false;
                b.FlatAppearance.BorderColor = Border;
                b.FlatAppearance.BorderSize = 1;
                b.FlatAppearance.MouseOverBackColor = SurfaceHover;
                b.FlatAppearance.MouseDownBackColor = AccentPressed;
                // red edge on keyboard focus, so focus is visible on black
                b.GotFocus += (s, e) => ((Button)s).FlatAppearance.BorderColor = Accent;
                b.LostFocus += (s, e) => ((Button)s).FlatAppearance.BorderColor = Border;
                break;

            case TextBox tb:
                tb.BackColor = SurfaceAlt;
                tb.ForeColor = Text;
                // BorderStyle.FixedSingle is drawn by the system in
                // SystemColors.ControlDark (#ABADB3), a light grey line that
                // stands out badly on black. There is no way to recolour it,
                // so drop the border and let the lighter field background
                // provide the edge instead.
                tb.BorderStyle = BorderStyle.None;
                break;

            case NumericUpDown nud:
                StyleUpDown(nud);
                break;

            // DarkComboBox styles itself, including the drop-down button
            case DarkComboBox:
                break;

            case ComboBox cb:
                cb.FlatStyle = FlatStyle.Flat;
                cb.BackColor = SurfaceAlt;
                cb.ForeColor = Text;
                break;

            case CheckBox cbx:
                cbx.FlatStyle = FlatStyle.Flat;
                cbx.ForeColor = Text;
                SetTransparentBack(cbx);
                cbx.FlatAppearance.BorderColor = Border;
                cbx.FlatAppearance.CheckedBackColor = Accent;
                break;

            case RadioButton rb:
                rb.FlatStyle = FlatStyle.Flat;
                rb.ForeColor = Text;
                SetTransparentBack(rb);
                rb.FlatAppearance.CheckedBackColor = Accent;
                break;

            // LinkLabel derives from Label, so it must be matched first or
            // the Label case swallows it.
            case LinkLabel ll:
                SetTransparentBack(ll);
                ll.LinkColor = AccentHover;
                ll.ActiveLinkColor = Accent;
                ll.VisitedLinkColor = AccentPressed;
                ll.DisabledLinkColor = TextDisabled;
                break;

            case Label lbl:
                lbl.ForeColor = Text;
                SetTransparentBack(lbl);
                break;

            case TrackBar tbar:
                // TrackBar is a native common control and largely ignores
                // ForeColor; matching BackColor at least removes the light box.
                tbar.BackColor = Surface;
                break;

            case ProgressBar pb:
                pb.BackColor = SurfaceAlt;
                pb.ForeColor = Accent;
                break;

            case TabControl tc:
                StyleTabControl(tc);
                break;

            case TabPage tp:
                tp.BackColor = Surface;
                tp.ForeColor = Text;
                break;

            case ListBox lb:
                lb.BackColor = SurfaceAlt;
                lb.ForeColor = Text;
                lb.BorderStyle = BorderStyle.FixedSingle;
                break;

            case ListView lv:
                lv.BackColor = SurfaceAlt;
                lv.ForeColor = Text;
                lv.BorderStyle = BorderStyle.FixedSingle;
                break;

            case Panel p:
                p.BackColor = p.Parent is TabPage ? Surface : Background;
                p.ForeColor = Text;
                break;

            case GroupBox gb:
                SetTransparentBack(gb);
                gb.ForeColor = TextMuted;
                break;

            default:
                // TableLayoutPanel / FlowLayoutPanel / plain Control:
                // transparent so the form (or tab page) shows through.
                SetTransparentBack(c);
                c.ForeColor = Text;
                break;
        }

        foreach (Control child in c.Controls)
        {
            ApplyTo(child);
        }
    }

    /// <summary>
    /// Styles a <see cref="NumericUpDown"/>, including while it is disabled.
    /// </summary>
    /// <remarks>
    /// <see cref="UpDownBase"/> resets its inner edit box to the system
    /// colours whenever it is enabled or disabled, which left disabled spin
    /// boxes as bright white rectangles on the dark form. Re-asserting the
    /// colours on every EnabledChanged keeps them dark; the text is dimmed
    /// instead, which is what indicates "disabled" here.
    /// </remarks>
    private static void StyleUpDown(NumericUpDown nud)
    {
        // see the TextBox case: a FixedSingle border paints light grey
        nud.BorderStyle = BorderStyle.None;
        Recolour(nud, null);
        nud.EnabledChanged -= Recolour;
        nud.EnabledChanged += Recolour;

        static void Recolour(object sender, EventArgs e)
        {
            NumericUpDown n = (NumericUpDown)sender;
            n.BackColor = n.Enabled ? SurfaceAlt : Background;
            n.ForeColor = n.Enabled ? Text : TextDisabled;

            // the inner edit box is a child control and keeps its own colours
            foreach (Control inner in n.Controls)
            {
                inner.BackColor = n.BackColor;
                inner.ForeColor = n.ForeColor;
            }
        }
    }

    private static void StyleToolStripItems(ToolStripItemCollection items)
    {
        foreach (ToolStripItem item in items)
        {
            item.ForeColor = Text;
            item.BackColor = Surface;

            if (item is ToolStripMenuItem tsmi && tsmi.HasDropDownItems)
            {
                tsmi.DropDown.BackColor = Surface;
                StyleToolStripItems(tsmi.DropDownItems);
            }
        }
    }

    /// <summary>
    /// Owner-draws a <see cref="TabControl"/>'s tabs so the header strip isn't
    /// left in the light system colours.
    /// </summary>
    private static void StyleTabControl(TabControl tc)
    {
        // DarkTabControl paints itself; only its pages need touching.
        if (tc is DarkTabControl)
        {
            foreach (TabPage page in tc.TabPages)
            {
                page.BackColor = Surface;
                page.ForeColor = Text;
            }
            return;
        }

        tc.DrawMode = TabDrawMode.OwnerDrawFixed;
        tc.BackColor = Background;
        tc.ForeColor = Text;

        tc.DrawItem -= TabControlDrawItem;
        tc.DrawItem += TabControlDrawItem;

        foreach (TabPage page in tc.TabPages)
        {
            page.BackColor = Surface;
            page.ForeColor = Text;
        }
    }

    private static void TabControlDrawItem(object sender, DrawItemEventArgs e)
    {
        TabControl tc = (TabControl)sender;
        if (e.Index < 0 || e.Index >= tc.TabPages.Count)
        {
            return;
        }

        TabPage page = tc.TabPages[e.Index];
        bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
        Rectangle r = e.Bounds;

        // fill the tab itself
        using (SolidBrush bg = new(selected ? Surface : Background))
        {
            e.Graphics.FillRectangle(bg, r);
        }

        // red underline on the selected tab - the MSI accent cue
        if (selected)
        {
            using SolidBrush accent = new(Accent);
            e.Graphics.FillRectangle(accent, r.Left, r.Bottom - 3, r.Width, 3);
        }

        TextRenderer.DrawText(
            e.Graphics, page.Text, tc.Font, r,
            selected ? Text : TextMuted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.EndEllipsis);
    }
}
