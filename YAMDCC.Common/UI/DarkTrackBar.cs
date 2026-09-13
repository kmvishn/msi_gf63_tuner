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
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace YAMDCC.Common.UI;

/// <summary>
/// A slider drawn entirely in managed code, styled to match <see cref="Theme"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TrackBar"/> is a thin wrapper over the Win32 common control,
/// which paints itself from the system theme and ignores
/// <see cref="Control.BackColor"/> completely - on a dark form it stays a
/// bright white box. There is no way to recolour it, so this reimplements the
/// parts of its API that YAMDCC uses and does all the painting itself.
/// </para>
/// <para>
/// Like <see cref="TrackBar"/>, a vertical slider puts
/// <see cref="Maximum"/> at the top.
/// </para>
/// </remarks>
public sealed class DarkTrackBar : Control, ISupportInitialize
{
    // TrackBar implements ISupportInitialize, so the designer emits
    // BeginInit()/EndInit() around it. Implemented as no-ops so generated
    // designer code keeps compiling unchanged.
    void ISupportInitialize.BeginInit() { }
    void ISupportInitialize.EndInit() { }

    private int _min, _max = 10, _value;
    private int _tickFrequency = 1;
    private Orientation _orientation = Orientation.Horizontal;
    private TickStyle _tickStyle = TickStyle.BottomRight;
    private bool _dragging;

    /// <summary>Raised when <see cref="Value"/> changes for any reason.</summary>
    public event EventHandler ValueChanged;

    /// <summary>Raised when the user moves the slider.</summary>
    public event EventHandler Scroll;

    public DarkTrackBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw |
                 ControlStyles.UserPaint |
                 ControlStyles.Selectable, true);
        BackColor = Theme.Surface;
        ForeColor = Theme.Text;
    }

    #region Properties
    [DefaultValue(0)]
    public int Minimum
    {
        get => _min;
        set
        {
            _min = value;
            if (_max < _min) { _max = _min; }
            Value = _value;
            Invalidate();
        }
    }

    [DefaultValue(10)]
    public int Maximum
    {
        get => _max;
        set
        {
            _max = value;
            if (_min > _max) { _min = _max; }
            Value = _value;
            Invalidate();
        }
    }

    [DefaultValue(0)]
    public int Value
    {
        get => _value;
        set
        {
            int clamped = value < _min ? _min : value > _max ? _max : value;
            if (clamped == _value)
            {
                return;
            }
            _value = clamped;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    [DefaultValue(Orientation.Horizontal)]
    public Orientation Orientation
    {
        get => _orientation;
        set { _orientation = value; Invalidate(); }
    }

    [DefaultValue(1)]
    public int TickFrequency
    {
        get => _tickFrequency;
        set { _tickFrequency = value < 1 ? 1 : value; Invalidate(); }
    }

    [DefaultValue(TickStyle.BottomRight)]
    public TickStyle TickStyle
    {
        get => _tickStyle;
        set { _tickStyle = value; Invalidate(); }
    }

    /// <summary>Step used by Page Up/Page Down and clicking the track.</summary>
    [DefaultValue(5)]
    public int LargeChange { get; set; } = 5;

    /// <summary>Step used by the arrow keys and the mouse wheel.</summary>
    [DefaultValue(1)]
    public int SmallChange { get; set; } = 1;
    #endregion

    #region Geometry
    private const int ThumbShort = 18;   // thumb size across the track
    private const int ThumbLong = 11;    // thumb size along the track
    private const int TrackThickness = 5;

    private bool Vertical => _orientation == Orientation.Vertical;

    /// <summary>Length of travel available to the centre of the thumb.</summary>
    private int TrackLength =>
        (Vertical ? Height : Width) - ThumbLong;

    /// <summary>Fraction of the way from <see cref="Minimum"/> to <see cref="Maximum"/>.</summary>
    private float Fraction => _max == _min ? 0f : (_value - _min) / (float)(_max - _min);

    private Rectangle TrackRect()
    {
        if (Vertical)
        {
            int x = (Width - TrackThickness) / 2;
            return new Rectangle(x, ThumbLong / 2, TrackThickness, TrackLength);
        }
        int y = (Height - TrackThickness) / 2;
        return new Rectangle(ThumbLong / 2, y, TrackLength, TrackThickness);
    }

    private Rectangle ThumbRect()
    {
        int travel = (int)Math.Round(Fraction * TrackLength);
        if (Vertical)
        {
            // Maximum at the top, matching TrackBar
            int y = ThumbLong / 2 + TrackLength - travel - (ThumbLong / 2);
            return new Rectangle((Width - ThumbShort) / 2, y, ThumbShort, ThumbLong);
        }
        int x = travel;
        return new Rectangle(x, (Height - ThumbShort) / 2, ThumbLong, ThumbShort);
    }

    private int ValueFromPoint(Point p)
    {
        int travel = Vertical
            ? TrackLength - (p.Y - (ThumbLong / 2))
            : p.X - (ThumbLong / 2);

        float frac = TrackLength <= 0 ? 0f : travel / (float)TrackLength;
        frac = frac < 0f ? 0f : frac > 1f ? 1f : frac;
        return _min + (int)Math.Round(frac * (_max - _min));
    }
    #endregion

    #region Painting
    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        Rectangle track = TrackRect();
        Rectangle thumb = ThumbRect();
        bool on = Enabled;

        // unfilled track
        using (SolidBrush b = new(on ? Theme.SurfaceAlt : Theme.Background))
        {
            g.FillRectangle(b, track);
        }
        using (Pen p = new(Theme.Border))
        {
            g.DrawRectangle(p, track);
        }

        // filled portion, in MSI red, from Minimum up to the thumb
        Rectangle fill = Vertical
            ? Rectangle.FromLTRB(track.Left, thumb.Top + (ThumbLong / 2), track.Right, track.Bottom)
            : Rectangle.FromLTRB(track.Left, track.Top, thumb.Left + (ThumbLong / 2), track.Bottom);

        if (fill.Width > 0 && fill.Height > 0)
        {
            using SolidBrush b = new(on ? Theme.Accent : Theme.TextDisabled);
            g.FillRectangle(b, fill);
        }

        DrawTicks(g, track, on);

        // thumb
        using (SolidBrush b = new(on ? Theme.Text : Theme.TextDisabled))
        {
            g.FillRectangle(b, thumb);
        }
        if (on && (_dragging || Focused))
        {
            using Pen p = new(Theme.AccentHover, 2);
            g.DrawRectangle(p, thumb.X, thumb.Y, thumb.Width - 1, thumb.Height - 1);
        }
    }

    private void DrawTicks(Graphics g, Rectangle track, bool on)
    {
        if (_tickStyle == TickStyle.None || _max <= _min)
        {
            return;
        }

        int steps = (_max - _min) / _tickFrequency;
        if (steps <= 0 || steps > 200)
        {
            return;
        }

        bool before = _tickStyle is TickStyle.TopLeft or TickStyle.Both;
        bool after = _tickStyle is TickStyle.BottomRight or TickStyle.Both;

        using Pen p = new(on ? Theme.Border : Theme.Background);
        const int len = 4, gap = 3;

        for (int i = 0; i <= steps; i++)
        {
            int travel = (int)Math.Round(i / (float)steps * TrackLength);
            if (Vertical)
            {
                int y = (ThumbLong / 2) + TrackLength - travel;
                if (before) { g.DrawLine(p, track.Left - gap - len, y, track.Left - gap, y); }
                if (after) { g.DrawLine(p, track.Right + gap, y, track.Right + gap + len, y); }
            }
            else
            {
                int x = (ThumbLong / 2) + travel;
                if (before) { g.DrawLine(p, x, track.Top - gap - len, x, track.Top - gap); }
                if (after) { g.DrawLine(p, x, track.Bottom + gap, x, track.Bottom + gap + len); }
            }
        }
    }
    #endregion

    #region Interaction
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !Enabled)
        {
            return;
        }

        Focus();
        _dragging = true;
        SetFromUser(ValueFromPoint(e.Location));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging)
        {
            SetFromUser(ValueFromPoint(e.Location));
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (Enabled && e.Delta != 0)
        {
            SetFromUser(_value + (e.Delta > 0 ? SmallChange : -SmallChange));
        }
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Up or Keys.Down or Keys.Left or Keys.Right
            or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End
        || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!Enabled)
        {
            return;
        }

        int v = _value;
        switch (e.KeyCode)
        {
            case Keys.Up or Keys.Right: v += SmallChange; break;
            case Keys.Down or Keys.Left: v -= SmallChange; break;
            case Keys.PageUp: v += LargeChange; break;
            case Keys.PageDown: v -= LargeChange; break;
            case Keys.Home: v = _max; break;
            case Keys.End: v = _min; break;
            default: return;
        }
        e.Handled = true;
        SetFromUser(v);
    }

    private void SetFromUser(int newValue)
    {
        int old = _value;
        Value = newValue;
        if (_value != old)
        {
            Scroll?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
    #endregion
}
