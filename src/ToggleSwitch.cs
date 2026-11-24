using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace CntlmGUI;

/// <summary>
/// 左侧文字 + 右侧 44×22 圆角开关。点击文字或控件本身都会切换状态。
/// </summary>
public sealed class ToggleSwitch : Control
{
    private const int TrackWidth = 44;
    private const int TrackHeight = 22;
    private const int TrackGap = 8;

    private readonly Label _label;
    private string _text = "";
    private bool _checked;

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        Height = 30;
        MinimumSize = new Size(140, 30);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;

        _label = new Label
        {
            AutoSize = false,
            BackColor = Color.Transparent,
            ForeColor = AppTheme.Text,
            Font = AppTheme.Ui,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Cursor = Cursors.Hand,
        };
        _label.Click += (_, _) => Checked = !Checked;
        Controls.Add(_label);
        LayoutChildren();

        Click += (_, _) => Checked = !Checked;
    }

    public event EventHandler? CheckedChanged;

    [AllowNull]
    public override string Text
    {
        get => _text;
        set
        {
            var value2 = value ?? "";
            if (_text == value2) return;
            _text = value2;
            if (_label != null) _label.Text = value2;
            Invalidate();
        }
    }

    public override Color ForeColor
    {
        get => base.ForeColor;
        set
        {
            base.ForeColor = value;
            if (_label != null) _label.ForeColor = value;
        }
    }

    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        if (_label != null) _label.Font = Font;
        LayoutChildren();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        if (_label != null) _label.ForeColor = Enabled ? ForeColor : AppTheme.Muted;
        Invalidate();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        LayoutChildren();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var track = TrackBounds;
        using var path = AppTheme.RoundedRect(track, TrackHeight / 2);
        using var brush = new SolidBrush(_checked ? AppTheme.Accent : Color.FromArgb(180, 180, 180));
        g.FillPath(brush, path);

        int knobSize = TrackHeight - 4;
        int knobX = _checked ? track.Right - knobSize - 2 : track.Left + 2;
        using var knobBrush = new SolidBrush(Color.White);
        g.FillEllipse(knobBrush, knobX, track.Top + 2, knobSize, knobSize);
    }

    private Rectangle TrackBounds =>
        new(Width - TrackWidth - 1, (Height - TrackHeight) / 2, TrackWidth, TrackHeight);

    private void LayoutChildren()
    {
        if (_label == null) return;
        int labelWidth = Math.Max(0, Width - TrackWidth - TrackGap);
        _label.SetBounds(0, 0, labelWidth, Height);
    }
}
