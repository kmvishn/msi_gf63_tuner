using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using YAMDCC.Common.UI;

namespace YAMDCC.ConfigEditor;

/// <summary>
/// A sleek, non-activating, center-bottom on-screen display (OSD) toast
/// that mimics the original MSI Center profile switch notification.
/// Renders smoothly over games and desktop without stealing focus.
/// </summary>
internal sealed class ProfileOsdToast : Form
{
    private static ProfileOsdToast _instance;
    private static readonly object _syncLock = new();

    private string _profileName = "BALANCED";
    private string _detail = "FAN & POWER PROFILE ACTIVE";
    private readonly Timer _animTimer;
    private int _holdTicks;
    private const int HoldDurationTicks = 120; // ~1.8 seconds at 15ms ticks
    private enum AnimState { FadingIn, Holding, FadingOut, Hidden }
    private AnimState _state = AnimState.Hidden;

    #region Window Attributes (Never steal focus, click-through, topmost)
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;   // clicks pass through
    private const int WS_EX_TOOLWINDOW = 0x00000080;    // hide from alt-tab
    private const int WS_EX_NOACTIVATE = 0x08000000;    // never steal focus
    private const int WS_EX_TOPMOST = 0x00000008;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST;
            return cp;
        }
    }
    #endregion

    public ProfileOsdToast()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        TopMost = true;
        BackColor = Color.FromArgb(16, 16, 21);
        Opacity = 0.0;

        _animTimer = new Timer { Interval = 15 };
        _animTimer.Tick += OnAnimTick;

        UpdateDimensions();
    }

    private void UpdateDimensions()
    {
        float scale = 1.0f;
        try { scale = CurrentAutoScaleDimensions.Height / 96f; }
        catch { }
        if (scale < 1.0f) { scale = 1.0f; }

        Width = (int)(360 * scale);
        Height = (int)(76 * scale);

        // Position horizontally centered, ~80px above primary screen bottom
        Rectangle workArea = Screen.PrimaryScreen.WorkingArea;
        int x = workArea.Left + (workArea.Width - Width) / 2;
        int y = workArea.Bottom - Height - (int)(70 * scale);
        Location = new Point(x, y);
    }

    public static void ShowToast(string profileName, string detail = null)
    {
        if (string.IsNullOrWhiteSpace(profileName)) { return; }

        lock (_syncLock)
        {
            if (_instance == null || _instance.IsDisposed)
            {
                _instance = new ProfileOsdToast();
            }

            if (_instance.InvokeRequired)
            {
                _instance.BeginInvoke(new Action(() => _instance.InternalShow(profileName, detail)));
            }
            else
            {
                _instance.InternalShow(profileName, detail);
            }
        }
    }

    private void InternalShow(string profileName, string detail)
    {
        _profileName = profileName.Trim().ToUpperInvariant();
        _detail = string.IsNullOrWhiteSpace(detail)
            ? "MSI GF63 TUNER • PROFILE ACTIVE"
            : detail.Trim().ToUpperInvariant();

        UpdateDimensions();
        _holdTicks = 0;
        _state = AnimState.FadingIn;

        if (!Visible)
        {
            Opacity = 0.0;
            Show();
        }

        Invalidate();
        _animTimer.Start();
    }

    private void OnAnimTick(object sender, EventArgs e)
    {
        switch (_state)
        {
            case AnimState.FadingIn:
                if (Opacity < 0.96)
                {
                    Opacity = Math.Min(0.96, Opacity + 0.16);
                }
                else
                {
                    Opacity = 0.96;
                    _state = AnimState.Holding;
                    _holdTicks = 0;
                }
                break;

            case AnimState.Holding:
                _holdTicks++;
                if (_holdTicks >= HoldDurationTicks)
                {
                    _state = AnimState.FadingOut;
                }
                break;

            case AnimState.FadingOut:
                if (Opacity > 0.05)
                {
                    Opacity = Math.Max(0.0, Opacity - 0.08);
                }
                else
                {
                    Opacity = 0.0;
                    _state = AnimState.Hidden;
                    _animTimer.Stop();
                    Hide();
                }
                break;

            case AnimState.Hidden:
                _animTimer.Stop();
                Hide();
                break;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        Rectangle rect = new(1, 1, Width - 3, Height - 3);

        // 1. Draw rounded capsule background (Obsidian dark glass)
        using (GraphicsPath path = GetRoundedRect(rect, 14))
        {
            using (SolidBrush bgBrush = new(Color.FromArgb(16, 16, 22)))
            {
                g.FillPath(bgBrush, path);
            }

            // Top subtle carbon gradient shine
            Rectangle topShine = new(rect.X, rect.Y, rect.Width, rect.Height / 2);
            using (LinearGradientBrush shineBrush = new(
                topShine,
                Color.FromArgb(40, 255, 255, 255),
                Color.FromArgb(0, 255, 255, 255),
                LinearGradientMode.Vertical))
            {
                using GraphicsPath shinePath = GetRoundedRect(rect, 14);
                g.SetClip(shinePath);
                g.FillRectangle(shineBrush, topShine);
                g.ResetClip();
            }

            // Neon Red gaming border
            using Pen borderPen = new(Theme.Accent, 1.8f);
            g.DrawPath(borderPen, path);
        }

        // 2. Icon Badge on Left
        int badgeSize = Height - 24;
        int badgeX = 14;
        int badgeY = 12;
        Rectangle badgeRect = new(badgeX, badgeY, badgeSize, badgeSize);

        using (GraphicsPath badgePath = GetRoundedRect(badgeRect, 8))
        {
            using (SolidBrush badgeBg = new(Color.FromArgb(28, 20, 24)))
            {
                g.FillPath(badgeBg, badgePath);
            }
            using Pen badgeBorder = new(Color.FromArgb(90, Theme.Accent), 1.2f);
            g.DrawPath(badgeBorder, badgePath);
        }

        // Determine symbol and color based on active profile
        string symbol = "⚡";
        Color symColor = Theme.Accent;

        if (_profileName.Contains("COOLER BOOST"))
        {
            symbol = "🌪️";
            symColor = Color.FromArgb(0, 200, 255);
        }
        else if (_profileName.Contains("SILENT"))
        {
            symbol = "🌙";
            symColor = Color.FromArgb(100, 220, 255);
        }
        else if (_profileName.Contains("SUPER BATTERY") || _profileName.Contains("BATTERY"))
        {
            symbol = "🔋";
            symColor = Color.FromArgb(80, 240, 120);
        }
        else if (_profileName.Contains("BALANCED"))
        {
            symbol = "⚖️";
            symColor = Color.FromArgb(240, 240, 255);
        }
        else if (_profileName.Contains("SUSTAINED"))
        {
            symbol = "🔥";
            symColor = Color.FromArgb(255, 120, 40);
        }

        using (Font symFont = new("Segoe UI Emoji", 16F, FontStyle.Bold))
        using (SolidBrush symBrush = new(symColor))
        {
            StringFormat sfSym = new()
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            g.DrawString(symbol, symFont, symBrush, badgeRect, sfSym);
        }

        // 3. Text Layout (Title + Subtitle)
        int textX = badgeX + badgeSize + 14;
        int textWidth = Width - textX - 16;

        Rectangle titleRect = new(textX, 14, textWidth, 26);
        using (Font titleFont = new("Segoe UI", 12.5F, FontStyle.Bold))
        using (SolidBrush titleBrush = new(Color.White))
        {
            StringFormat sfTitle = new()
            {
                Alignment = StringAlignment.Near,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter
            };
            g.DrawString(_profileName, titleFont, titleBrush, titleRect, sfTitle);
        }

        Rectangle subRect = new(textX, 41, textWidth, 18);
        using (Font subFont = new("Segoe UI", 8.2F, FontStyle.Bold))
        using (SolidBrush subBrush = new(Theme.TextMuted))
        {
            StringFormat sfSub = new()
            {
                Alignment = StringAlignment.Near,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter
            };
            g.DrawString(_detail, subFont, subBrush, subRect, sfSub);
        }

        // 4. Subtle bottom glowing red accent stripe
        int stripeHeight = 3;
        int stripeMargin = 40;
        Rectangle stripeRect = new(stripeMargin, Height - stripeHeight - 3, Width - (stripeMargin * 2), stripeHeight);
        using (LinearGradientBrush stripeBrush = new(
            stripeRect,
            Color.FromArgb(0, Theme.Accent),
            Theme.Accent,
            LinearGradientMode.Horizontal))
        {
            stripeBrush.SetBlendTriangularShape(0.5f);
            using GraphicsPath stripePath = GetRoundedRect(stripeRect, 2);
            g.FillPath(stripeBrush, stripePath);
        }
    }

    private static GraphicsPath GetRoundedRect(Rectangle bounds, int radius)
    {
        GraphicsPath path = new();
        int diameter = radius * 2;
        Rectangle arc = new(bounds.X, bounds.Y, diameter, diameter);

        // top left
        path.AddArc(arc, 180, 90);

        // top right
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);

        // bottom right
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);

        // bottom left
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);

        path.CloseFigure();
        return path;
    }
}
