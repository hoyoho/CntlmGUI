using System.Drawing;
using System.Drawing.Drawing2D;

namespace CntlmGUI;

internal static class AppTheme
{
    public static readonly Font Ui = new("Microsoft YaHei UI", 9F);
    public static readonly Font UiBold = new("Microsoft YaHei UI", 9F, FontStyle.Bold);
    public static readonly Font UiTitle = new("Microsoft YaHei UI", 10F, FontStyle.Bold);
    public static readonly Font UiSmall = new("Microsoft YaHei UI", 8.5F);
    public static readonly Font Mono = new("Consolas", 9.5F);

    public static readonly Color Accent = Color.FromArgb(0, 130, 150);
    public static readonly Color AccentDark = Color.FromArgb(0, 100, 120);
    public static readonly Color Danger = Color.FromArgb(200, 100, 60);
    public static readonly Color Success = Color.FromArgb(0, 150, 136);
    public static readonly Color Text = Color.FromArgb(60, 60, 60);
    public static readonly Color Muted = Color.FromArgb(130, 130, 130);
    public static readonly Color Panel = Color.FromArgb(250, 250, 250);
    public static readonly Color Line = Color.FromArgb(221, 221, 221);

    public const int LabelCol = 165;

    public static Button CreateButton(string text, bool primary = false)
    {
        var button = new Button
        {
            Text = text,
            Font = Ui,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(0, 34),
            Padding = new Padding(16, 0, 16, 0),
            FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Accent : Color.White,
            ForeColor = primary ? Color.White : Accent,
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
            TabStop = true,
        };
        button.FlatAppearance.BorderColor = Accent;
        button.FlatAppearance.BorderSize = 1;
        return button;
    }

    public static Button CreateSmallButton(string text)
    {
        var button = new Button
        {
            Text = text,
            Font = UiSmall,
            AutoSize = false,
            Size = new Size(58, 26),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            ForeColor = Accent,
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
        };
        button.FlatAppearance.BorderColor = Accent;
        button.FlatAppearance.BorderSize = 1;
        return button;
    }

    public static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        if (d <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }
        path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
