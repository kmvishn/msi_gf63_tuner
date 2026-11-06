using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace YAMDCC.Common.UI;

/// <summary>
/// A sleek, high-contrast dark theme CheckBox matching the MSI gaming aesthetic.
/// Features a vivid Neon Red filled square with crisp white checkmark when checked,
/// and a stealth recessed dark box when unchecked, making active/inactive state immediately clear.
/// </summary>
public class DarkCheckBox : CheckBox
{
    private bool IsHovered;

    public DarkCheckBox()
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.SupportsTransparentBackColor,
            true);

        Cursor = Cursors.Hand;
        BackColor = Color.Transparent;
    }

    protected override void OnMouseEnter(EventArgs eventargs)
    {
        base.OnMouseEnter(eventargs);
        IsHovered = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs eventargs)
    {
        base.OnMouseLeave(eventargs);
        IsHovered = false;
        Invalidate();
    }

    protected override void OnCheckedChanged(EventArgs e)
    {
        base.OnCheckedChanged(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        Graphics g = pevent.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        // Draw transparent or parent background
        if (Parent != null)
        {
            using Brush backBrush = new SolidBrush(Parent.BackColor);
            g.FillRectangle(backBrush, ClientRectangle);
        }
        else
        {
            g.Clear(Theme.Background);
        }

        const int boxSize = 16;
        int boxY = (Height - boxSize) / 2;
        Rectangle boxRect = new(1, boxY, boxSize, boxSize);

        Color boxBorder;
        Color boxFill;

        if (!Enabled)
        {
            boxBorder = Color.FromArgb(50, 40, 48);
            boxFill = Checked ? Color.FromArgb(80, 20, 30) : Color.FromArgb(20, 20, 26);
        }
        else if (Checked)
        {
            // Vivid Neon Red fill when enabled
            boxFill = IsHovered ? Theme.AccentHover : Theme.Accent; // #FF1A3C / #FF3D5C
            boxBorder = IsHovered ? Color.FromArgb(255, 120, 145) : Theme.Accent;
        }
        else
        {
            // Recessed dark charcoal fill when unchecked
            boxFill = IsHovered ? Color.FromArgb(28, 34, 46) : Theme.SurfaceAlt;
            boxBorder = IsHovered ? Theme.AccentCyan : Theme.Border;
        }

        // Draw box with subtle 3px rounded corners
        using (GraphicsPath path = GetRoundedRect(boxRect, 3))
        {
            using (Brush fillBrush = new SolidBrush(boxFill))
            {
                g.FillPath(fillBrush, path);
            }
            using (Pen borderPen = new(boxBorder, 1.5f))
            {
                g.DrawPath(borderPen, path);
            }
        }

        // Draw crisp checkmark when Checked
        if (Checked)
        {
            Color checkColor = Enabled ? Color.White : Color.FromArgb(170, 170, 170);
            using Pen checkPen = new(checkColor, 2.2f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };

            // Calculate checkmark points relative to boxRect
            PointF p1 = new(boxRect.Left + 3.5f, boxRect.Top + 8.5f);
            PointF p2 = new(boxRect.Left + 6.5f, boxRect.Top + 12.0f);
            PointF p3 = new(boxRect.Left + 12.5f, boxRect.Top + 4.5f);

            g.DrawLines(checkPen, new[] { p1, p2, p3 });
        }

        // Draw label text
        int textX = boxRect.Right + 8;
        Rectangle textRect = new(textX, 0, Width - textX, Height);

        Color textColor;
        if (!Enabled)
        {
            textColor = Color.FromArgb(90, 90, 105);
        }
        else if (Checked)
        {
            // Highlight Full Blast in bold accent when active, other labels in crisp white
            textColor = (Name == "chkFullBlast") ? Theme.Accent : Color.White;
        }
        else
        {
            textColor = IsHovered ? Color.White : Theme.TextMuted; // #A0A0B2
        }

        TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
        TextRenderer.DrawText(g, Text, Font, textRect, textColor, flags);
    }

    private static GraphicsPath GetRoundedRect(Rectangle bounds, int radius)
    {
        GraphicsPath path = new();
        int diameter = radius * 2;
        Rectangle arc = new(bounds.X, bounds.Y, diameter, diameter);

        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }
}
