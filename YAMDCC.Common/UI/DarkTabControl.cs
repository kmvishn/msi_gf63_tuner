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
using System.Windows.Forms;

namespace YAMDCC.Common.UI;

/// <summary>
/// A <see cref="TabControl"/> that paints its tab strip in the dark theme.
/// </summary>
/// <remarks>
/// Owner-drawing a plain <see cref="TabControl"/> only lets you paint the tabs
/// themselves: the strip *behind* and beside them is drawn by the Win32 common
/// control from the system theme, so it stays light grey on a dark form, as do
/// the raised borders around each tab. Taking over painting entirely is the
/// only way to get rid of both.
/// </remarks>
public sealed class DarkTabControl : TabControl
{
    private int _hoveredIndex = -1;

    public DarkTabControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw |
                 ControlStyles.UserPaint, true);

        DrawMode = TabDrawMode.OwnerDrawFixed;
        // flat buttons stops the control reserving space for 3D tab edges
        Appearance = TabAppearance.FlatButtons;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        ItemSize = new Size(160, 34);
        SizeMode = TabSizeMode.Fixed;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int newHover = -1;
        for (int i = 0; i < TabCount; i++)
        {
            if (GetTabRect(i).Contains(e.Location))
            {
                newHover = i;
                break;
            }
        }
        if (newHover != _hoveredIndex)
        {
            _hoveredIndex = newHover;
            Cursor = _hoveredIndex >= 0 ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoveredIndex != -1)
        {
            _hoveredIndex = -1;
            Cursor = Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Theme.Background);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Theme.Background);

        // the page area, so the selected tab appears joined to its page
        Rectangle display = DisplayRectangle;
        using (SolidBrush b = new(Theme.Surface))
        {
            g.FillRectangle(b, display);
        }

        // Draw top border line separating the tab strip from the page area
        using (Pen pBorder = new(Theme.Border))
        {
            g.DrawLine(pBorder, display.Left, display.Top, display.Right, display.Top);
        }

        for (int i = 0; i < TabCount; i++)
        {
            Rectangle r = GetTabRect(i);
            bool selected = SelectedIndex == i;
            bool hovered = _hoveredIndex == i;

            Color tabBg = selected ? Theme.Surface : (hovered ? Theme.SurfaceHover : Theme.Background);
            using (SolidBrush b = new(tabBg))
            {
                g.FillRectangle(b, r);
            }

            // MSI Neon Red underline marks the active tab; subtle crimson for hovered
            if (selected)
            {
                using SolidBrush accent = new(Theme.Accent);
                g.FillRectangle(accent, r.Left, r.Bottom - 3, r.Width, 3);
            }
            else if (hovered)
            {
                using SolidBrush glow = new(Theme.AccentDim);
                g.FillRectangle(glow, r.Left, r.Bottom - 2, r.Width, 2);
            }

            Color textColor = selected ? Theme.Text : (hovered ? Theme.Text : Theme.TextMuted);
            using Font f = selected ? new Font(Font, FontStyle.Bold) : new Font(Font, FontStyle.Regular);

            TextRenderer.DrawText(
                g, TabPages[i].Text, f, r,
                textColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis);
        }
    }
}
