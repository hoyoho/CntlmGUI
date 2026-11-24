using System.Drawing;
using System.Windows.Forms;

namespace CntlmGUI;

/// <summary>全应用统一视觉规范：字体、配色、控件尺寸。</summary>
public static class AppTheme
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

    public static void StyleButton(Button b, bool primary = false)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.BackColor = primary ? Accent : Color.White;
        b.ForeColor = primary ? Color.White : AccentDark;
        b.FlatAppearance.BorderColor = Accent;
        b.FlatAppearance.BorderSize = 1;
        b.Height = 34;
        b.Padding = new Padding(16, 2, 16, 2);
        b.Font = Ui;
        b.Cursor = Cursors.Hand;
    }
}
