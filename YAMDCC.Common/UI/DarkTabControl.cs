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
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace YAMDCC.Common.UI;

/// <summary>
/// A sleek, futuristic Cyberpunk Sci-Fi <see cref="TabControl"/> that paints
/// chamfered tactical HUD tabs, glowing laser highlights, and clean cyber aesthetics.
/// </summary>
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
        Appearance = TabAppearance.FlatButtons;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        ItemSize = new Size(175, 36);
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

    private static GraphicsPath GetChamferedTab(Rectangle r, int cut)
    {
        GraphicsPath path = new();
        path.AddLine(r.Left, r.Bottom, r.Left, r.Top + cut);
        path.AddLine(r.Left + cut, r.Top, r.Right - cut, r.Top);
        path.AddLine(r.Right, r.Top + cut, r.Right, r.Bottom);
        path.CloseFigure();
        return path;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.Background);

        // Active page container background
        Rectangle display = DisplayRectangle;
        using (SolidBrush b = new(Theme.Surface))
        {
            g.FillRectangle(b, display);
        }

        // Cyberpunk laser rail dividing the tab strip from the page area
        using (Pen pBorder = new(Theme.Border, 1.5f))
        {
            g.DrawLine(pBorder, 0, display.Top, Width, display.Top);
        }

        for (int i = 0; i < TabCount; i++)
        {
            Rectangle r = GetTabRect(i);
            bool selected = SelectedIndex == i;
            bool hovered = _hoveredIndex == i;

            if (selected)
            {
                // Sci-fi chamfered tab card with subtle cyber gradient
                Rectangle tabCard = new(r.X + 2, r.Y + 2, r.Width - 4, r.Height);
                using (GraphicsPath path = GetChamferedTab(tabCard, 6))
                {
                    using (LinearGradientBrush lgb = new(tabCard, Color.FromArgb(34, 22, 28), Theme.Surface, LinearGradientMode.Vertical))
                    {
                        g.FillPath(lgb, path);
                    }
                    using (Pen p = new(Theme.BorderBright, 1.2f))
                    {
                        g.DrawPath(p, path);
                    }
                }

                // Dual-core glowing neon crimson laser bar at the bottom
                using (SolidBrush accent = new(Theme.Accent))
                {
                    g.FillRectangle(accent, r.Left + 2, r.Bottom - 3, r.Width - 4, 3);
                }
                using (SolidBrush core = new(Color.FromArgb(255, 160, 180)))
                {
                    g.FillRectangle(core, r.Left + 6, r.Bottom - 3, r.Width - 12, 1);
                }
            }
            else if (hovered)
            {
                // Hover highlight with cyber cyan accent
                Rectangle tabCard = new(r.X + 2, r.Y + 4, r.Width - 4, r.Height - 2);
                using (GraphicsPath path = GetChamferedTab(tabCard, 5))
                {
                    using (SolidBrush b = new(Theme.SurfaceHover))
                    {
                        g.FillPath(b, path);
                    }
                    using (Pen p = new(Color.FromArgb(90, Theme.AccentCyan), 1f))
                    {
                        g.DrawPath(p, path);
                    }
                }

                // Electric cyan indicator
                using (SolidBrush glow = new(Theme.AccentCyan))
                {
                    g.FillRectangle(glow, r.Left + 4, r.Bottom - 2, r.Width - 8, 2);
                }
            }

            Color textColor = selected ? Color.White : (hovered ? Theme.Text : Theme.TextMuted);
            using Font f = selected ? new Font(Font, FontStyle.Bold) : new Font(Font, FontStyle.Regular);

            TextRenderer.DrawText(
                g, TabPages[i].Text, f, r,
                textColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis);
        }
    }
}
