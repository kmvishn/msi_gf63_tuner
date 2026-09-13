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
using System.Windows.Forms;

namespace YAMDCC.Common.UI;

/// <summary>
/// Supplies dark colours to <see cref="ToolStripProfessionalRenderer"/>.
/// </summary>
/// <remarks>
/// Setting BackColor on a <see cref="MenuStrip"/> is not enough: menu
/// backgrounds, hover highlights, separators and the check mark gutter are all
/// painted from the renderer's colour table, so they stay light unless the
/// table is replaced too.
/// </remarks>
internal sealed class DarkColorTable : ProfessionalColorTable
{
    public override Color ToolStripDropDownBackground => Theme.Surface;
    public override Color ToolStripGradientBegin => Theme.Surface;
    public override Color ToolStripGradientMiddle => Theme.Surface;
    public override Color ToolStripGradientEnd => Theme.Surface;
    public override Color ToolStripBorder => Theme.Border;
    public override Color ToolStripContentPanelGradientBegin => Theme.Surface;
    public override Color ToolStripContentPanelGradientEnd => Theme.Surface;
    public override Color ToolStripPanelGradientBegin => Theme.Surface;
    public override Color ToolStripPanelGradientEnd => Theme.Surface;

    public override Color MenuStripGradientBegin => Theme.Surface;
    public override Color MenuStripGradientEnd => Theme.Surface;
    public override Color MenuBorder => Theme.Border;
    public override Color MenuItemBorder => Theme.Accent;

    // hovered / open menu items: MSI red
    public override Color MenuItemSelected => Theme.Accent;
    public override Color MenuItemSelectedGradientBegin => Theme.Accent;
    public override Color MenuItemSelectedGradientEnd => Theme.Accent;
    public override Color MenuItemPressedGradientBegin => Theme.Surface;
    public override Color MenuItemPressedGradientMiddle => Theme.Surface;
    public override Color MenuItemPressedGradientEnd => Theme.Surface;

    // the gutter down the left of a drop-down that holds check marks/icons
    public override Color ImageMarginGradientBegin => Theme.Surface;
    public override Color ImageMarginGradientMiddle => Theme.Surface;
    public override Color ImageMarginGradientEnd => Theme.Surface;

    public override Color SeparatorDark => Theme.Border;
    public override Color SeparatorLight => Theme.Border;

    public override Color CheckBackground => Theme.Accent;
    public override Color CheckSelectedBackground => Theme.AccentHover;
    public override Color CheckPressedBackground => Theme.AccentPressed;

    public override Color ButtonSelectedHighlight => Theme.SurfaceHover;
    public override Color ButtonSelectedHighlightBorder => Theme.Accent;
    public override Color ButtonPressedHighlight => Theme.AccentPressed;
    public override Color ButtonCheckedHighlight => Theme.SurfaceHover;
}

/// <summary>
/// Renders menus in the dark MSI theme.
/// </summary>
/// <remarks>
/// The colour table handles fills; this handles the parts that are drawn with
/// hard-coded system colours regardless of the table - item text, drop-down
/// arrows, and the outer border.
/// </remarks>
public sealed class DarkToolStripRenderer : ToolStripProfessionalRenderer
{
    public DarkToolStripRenderer() : base(new DarkColorTable())
    {
        RoundedEdges = false;
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = !e.Item.Enabled
            ? Theme.TextDisabled
            // white on red reads better than the themed text colour
            : e.Item.Selected ? Color.White : Theme.Text;

        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = e.Item.Enabled
            ? (e.Item.Selected ? Color.White : Theme.Text)
            : Theme.TextDisabled;

        base.OnRenderArrow(e);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        // the default draws a light 3D edge around drop-downs
        using Pen p = new(Theme.Border);
        Rectangle r = e.AffectedBounds;
        e.Graphics.DrawRectangle(p, 0, 0, r.Width - 1, r.Height - 1);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        Rectangle r = e.Item.ContentRectangle;
        using Pen p = new(Theme.Border);

        if (e.Vertical)
        {
            int x = r.Left + (r.Width / 2);
            e.Graphics.DrawLine(p, x, r.Top + 2, x, r.Bottom - 2);
        }
        else
        {
            int y = r.Top + (r.Height / 2);
            e.Graphics.DrawLine(p, r.Left + 4, y, r.Right - 4, y);
        }
    }
}
