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

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace YAMDCC.Common.UI;

/// <summary>
/// A <see cref="ComboBox"/> whose drop-down button follows the dark theme.
/// </summary>
/// <remarks>
/// Even with <see cref="FlatStyle.Flat"/>, the drop-down button on the right
/// is painted by the system and stays a light grey square on a dark form.
/// There is no property for it, so this repaints that corner (and the border)
/// after the control has drawn itself.
/// </remarks>
public sealed class DarkComboBox : ComboBox
{
    private const int WM_PAINT = 0x000F;
    private const int ButtonWidth = 17;
    private bool _hovered;

    public DarkComboBox()
    {
        FlatStyle = FlatStyle.Flat;
        BackColor = Theme.SurfaceAlt;
        ForeColor = Theme.Text;
        DrawMode = DrawMode.OwnerDrawFixed;
    }

    protected override void OnMouseEnter(System.EventArgs e)
    {
        base.OnMouseEnter(e);
        if (Enabled)
        {
            _hovered = true;
            Cursor = Cursors.Hand;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(System.EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hovered)
        {
            _hovered = false;
            Cursor = Cursors.Default;
            Invalidate();
        }
    }

    /// <summary>
    /// Draws the list rows, which would otherwise use the system highlight.
    /// </summary>
    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        bool selected = e.Index >= 0 &&
            (e.State & DrawItemState.Selected) == DrawItemState.Selected;

        // Always paint the background, including when there is no selection
        // (e.Index < 0). Deferring to base.OnDrawItem() in that case left the
        // system's white background showing through on empty combo boxes.
        using (SolidBrush b = new(selected ? Theme.Accent : Theme.SurfaceAlt))
        {
            e.Graphics.FillRectangle(b, e.Bounds);
        }

        if (e.Index < 0)
        {
            return;
        }

        using Font f = selected ? new Font(e.Font, FontStyle.Bold) : new Font(e.Font, FontStyle.Regular);
        TextRenderer.DrawText(
            e.Graphics, GetItemText(Items[e.Index]), f, e.Bounds,
            selected ? Color.White : (Enabled ? Theme.Text : Theme.TextDisabled),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
            TextFormatFlags.NoPrefix);
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);

        if (m.Msg != WM_PAINT)
        {
            return;
        }

        using Graphics g = Graphics.FromHwnd(Handle);
        Color back = Enabled ? Theme.SurfaceAlt : Theme.Background;

        // paint over the system-drawn drop-down button
        Rectangle btn = new(Width - ButtonWidth - 1, 1, ButtonWidth, Height - 2);
        using (SolidBrush b = new(back))
        {
            g.FillRectangle(b, btn);
        }

        // chevron
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int cx = btn.Left + (btn.Width / 2);
        int cy = btn.Top + (btn.Height / 2);
        Point[] arrow =
        [
            new(cx - 4, cy - 2),
            new(cx + 4, cy - 2),
            new(cx, cy + 3),
        ];
        bool active = Focused || DroppedDown || _hovered;
        Color activeAccent = DroppedDown ? Theme.Accent : (_hovered ? Theme.AccentCyan : Theme.Accent);
        using (SolidBrush b = new(Enabled ? (active ? activeAccent : Theme.Text) : Theme.TextDisabled))
        {
            g.FillPolygon(b, arrow);
        }

        // A flat border in the theme colour, instead of the 3D one.
        using Pen p = new(active ? activeAccent : Theme.Border);
        g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        g.DrawRectangle(p, 1, 1, Width - 3, Height - 3);

        if (active && Enabled)
        {
            // Sci-Fi HUD corner bracket accents on top-left and bottom-right
            using Pen pBracket = new(Theme.Accent, 1.5f);
            g.DrawLine(pBracket, 0, 0, 5, 0);
            g.DrawLine(pBracket, 0, 0, 0, 5);
            g.DrawLine(pBracket, Width - 1, Height - 1, Width - 6, Height - 1);
            g.DrawLine(pBracket, Width - 1, Height - 1, Width - 1, Height - 6);
        }
    }
}
