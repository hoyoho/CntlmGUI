using System;
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CntlmGUI;

/// <summary>左边文字（真实 Label 渲染，与其它 Label 完全一致）+ 右侧滑动开关。</summary>
public sealed class ToggleSwitch : Control
{
    private readonly Label _text;
    private bool _checked;

    public ToggleSwitch()
    {
        _text = new Label
        {
            AutoSize = false,
            Height = 30,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Margin = new Padding(0),
            // 注意：不能写 ForeColor = base.ForeColor，那会经过 override 的
            // ForeColor getter（访问尚未赋值的 _text），导致 NullReferenceException。
            ForeColor = DefaultForeColor,
        };
        Controls.Add(_text);
        _text.Click += (_, _) => Toggle();

        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer, true);
        Cursor = Cursors.Hand;
        Height = 30;
        base.Font = AppTheme.Ui;
        _text.Font = AppTheme.Ui;
    }

    [AllowNull]
    public override string Text
    {
        get => _text.Text;
        set { _text.Text = value; Invalidate(); }
    }

    [AllowNull]
    public override Font Font
    {
        get => _text.Font;
        set { _text.Font = value; base.Font = value; Invalidate(); }
    }

    [AllowNull]
    public override Color ForeColor
    {
        get => _text.ForeColor;
        set { _text.ForeColor = value; base.ForeColor = value; Invalidate(); }
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

    public event EventHandler? CheckedChanged;

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        Toggle();
    }

    private void Toggle() => Checked = !Checked;

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        const int trackW = 44, trackH = 22, gap = 20;
        int trackX = Width - trackW - 2;
        int textW = Math.Max(0, trackX - gap);
        _text.SetBounds(0, (Height - 30) / 2, textW, 30);

        int trackY = (Height - trackH) / 2;
        var track = new Rectangle(trackX, trackY, trackW, trackH);
        using (var path = new GraphicsPath())
        {
            path.AddArc(track.X, track.Y, trackH, trackH, 90, 180);
            path.AddArc(track.Right - trackH, track.Y, trackH, trackH, 270, 180);
            path.CloseFigure();
            using var brush = new SolidBrush(_checked ? AppTheme.Accent : Color.FromArgb(180, 180, 180));
            g.FillPath(brush, path);
        }

        int knob = trackH - 6;
        int knobX = _checked ? track.Right - knob - 3 : track.X + 3;
        using (var knobBrush = new SolidBrush(Color.White))
            g.FillEllipse(knobBrush, knobX, track.Y + 3, knob, knob);
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        MinimumSize = new Size(140, 30);
    }
}
