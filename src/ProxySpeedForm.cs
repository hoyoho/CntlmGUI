using System.Drawing;
using CntlmGUI.Models;
using CntlmGUI.Services;

namespace CntlmGUI;

public sealed class ProxySpeedForm : Form
{
    private readonly GuiSettings _settings;
    private readonly ProxyResultList _list;
    private readonly TextBox _urlBox;
    private readonly TextBox _sizeBox;
    private readonly TextBox _timeoutBox;
    private readonly Button _speedButton;
    private readonly Button _upButton;
    private readonly Button _downButton;
    private readonly Button _topButton;
    private readonly System.Windows.Forms.Timer _dotsTimer = new() { Interval = 300 };
    private readonly ToolTip _toolTip = new();
    private readonly List<ProxyBench> _results = new();

    private CancellationTokenSource? _cts;
    private bool _running;
    private bool _closeAfterTest;
    private bool _closeReady;
    private int _dotFrame;

    public ProxySpeedForm(GuiSettings settings)
    {
        _settings = settings;

        Text = "代理列表";
        Font = AppTheme.Ui;
        BackColor = Color.White;
        ClientSize = new Size(900, 600);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = Size;
        MaximumSize = Size;

        var toolbar = new Panel { Location = new Point(0, 0), Size = new Size(900, 96), BackColor = Color.White };
        toolbar.Paint += (_, e) =>
        {
            using var pen = new Pen(AppTheme.Line);
            e.Graphics.DrawLine(pen, 0, toolbar.Height - 1, toolbar.Width, toolbar.Height - 1);
        };
        Controls.Add(toolbar);

        _list = new ProxyResultList(_settings)
        {
            Location = new Point(12, 104),
            Size = new Size(876, 484),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
        };
        _list.SelectionChanged += (_, _) => UpdateButtons();
        _list.OrderChanged += (_, _) => UpdateButtons();
        Controls.Add(_list);

        _speedButton = AppTheme.CreateButton("测速并重排", true);
        _speedButton.AutoSize = false;
        _speedButton.Size = new Size(112, 34);
        _speedButton.Location = new Point(12, 10);
        _speedButton.Click += OnSpeedClick;
        toolbar.Controls.Add(_speedButton);

        _upButton = CreateActionButton("上移", 132);
        _upButton.Click += (_, _) => _list.MoveUp();
        toolbar.Controls.Add(_upButton);

        _downButton = CreateActionButton("下移", 212);
        _downButton.Click += (_, _) => _list.MoveDown();
        toolbar.Controls.Add(_downButton);

        _topButton = CreateActionButton("置顶", 292);
        _topButton.Click += (_, _) => _list.MoveToTop();
        toolbar.Controls.Add(_topButton);

        var urlLabel = new Label
        {
            Text = "目标 URL",
            Font = AppTheme.Ui,
            ForeColor = AppTheme.Text,
            Location = new Point(14, 60),
            Size = new Size(68, 20),
        };
        toolbar.Controls.Add(urlLabel);

        _urlBox = new TextBox
        {
            Font = AppTheme.Ui,
            BorderStyle = BorderStyle.FixedSingle,
            Location = new Point(84, 57),
            Size = new Size(520, 23),
            Text = string.IsNullOrWhiteSpace(settings.SpeedTestUrl) ? "https://www.google.com" : settings.SpeedTestUrl,
        };
        toolbar.Controls.Add(_urlBox);

        var sizeLabel = new Label
        {
            Text = "大小(MB)",
            Font = AppTheme.Ui,
            ForeColor = AppTheme.Text,
            Location = new Point(616, 60),
            Size = new Size(62, 20),
        };
        toolbar.Controls.Add(sizeLabel);

        _sizeBox = new TextBox
        {
            Font = AppTheme.Ui,
            BorderStyle = BorderStyle.FixedSingle,
            Location = new Point(680, 57),
            Size = new Size(46, 23),
            Text = Math.Clamp(settings.SpeedTestMb, 1, 200).ToString(),
        };
        toolbar.Controls.Add(_sizeBox);

        var timeoutLabel = new Label
        {
            Text = "超时(s)",
            Font = AppTheme.Ui,
            ForeColor = AppTheme.Text,
            Location = new Point(738, 60),
            Size = new Size(54, 20),
        };
        toolbar.Controls.Add(timeoutLabel);

        _timeoutBox = new TextBox
        {
            Font = AppTheme.Ui,
            BorderStyle = BorderStyle.FixedSingle,
            Location = new Point(794, 57),
            Size = new Size(46, 23),
            Text = Math.Clamp(settings.SpeedTestTimeoutSec, 3, 300).ToString(),
        };
        toolbar.Controls.Add(_timeoutBox);

        var toolTip = _toolTip;
        toolTip.SetToolTip(_urlBox, "通过代理访问的目标地址（缺省协议时自动补 https://）");
        toolTip.SetToolTip(_sizeBox, "吞吐测试下载上限，1–200 MB");
        toolTip.SetToolTip(_timeoutBox, "吞吐测试时限，3–300 秒");

        _dotsTimer.Tick += OnDotsTick;

        UpdateButtons();
    }

    private static Button CreateActionButton(string text, int x)
    {
        var button = AppTheme.CreateButton(text);
        button.AutoSize = false;
        button.Size = new Size(72, 34);
        button.Location = new Point(x, 10);
        return button;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_running && !_closeReady)
        {
            e.Cancel = true;
            _closeAfterTest = true;
            SetStatus("正在停止测速…");
            try
            {
                _cts?.Cancel();
            }
            catch
            {
                // 忽略
            }
        }

        base.OnFormClosing(e);
    }

    private void UpdateButtons()
    {
        _speedButton.Text = _running ? "停止" : "测速并重排";
        _speedButton.Enabled = _running || _list.Proxies.Count > 0;
        _upButton.Enabled = !_running && _list.CanMoveUp;
        _downButton.Enabled = !_running && _list.CanMoveDown;
        _topButton.Enabled = !_running && _list.CanMoveTop;
    }

    private void SetStatus(string? status)
    {
        var title = "代理列表";
        if (!string.IsNullOrEmpty(status))
        {
            var combined = title + " - " + status;
            if (combined.Length > 60) combined = combined[..59] + "…";
            Text = combined;
        }
        else
        {
            Text = title;
        }
    }

    private async void OnSpeedClick(object? sender, EventArgs e)
    {
        if (_running)
        {
            SetStatus("正在停止测速…");
            try
            {
                _cts?.Cancel();
            }
            catch
            {
                // 忽略
            }
            return;
        }

        var proxies = _list.Proxies.ToList();
        if (proxies.Count == 0)
        {
            SetStatus("没有可测速的代理。");
            return;
        }

        var url = ProxySpeedTest.NormalizeUrl(_urlBox.Text);
        if (url.Length == 0)
        {
            MessageBox.Show(this, "请填写目标 URL。", "代理列表",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var mb = ClampInt(_sizeBox.Text, 1, 200, 10);
        var timeout = ClampInt(_timeoutBox.Text, 3, 300, 15);
        _sizeBox.Text = mb.ToString();
        _timeoutBox.Text = timeout.ToString();

        _settings.SpeedTestUrl = url;
        _settings.SpeedTestMb = mb;
        _settings.SpeedTestTimeoutSec = timeout;
        try
        {
            _settings.Save();
        }
        catch
        {
            // 忽略
        }

        _running = true;
        _closeAfterTest = false;
        _closeReady = false;
        _dotFrame = 0;
        _results.Clear();
        _cts = new CancellationTokenSource();
        UpdateButtons();
        _dotsTimer.Start();
        SetStatus("正在测速…");

        foreach (var line in proxies) _list.SetDetail(line, "测速中.");

        var tester = new ProxySpeedTest(_settings, url, mb * 1024L * 1024L, timeout);
        var progress = new Progress<ProxyBench>(bench =>
        {
            _results.RemoveAll(item => item.Line.Equals(bench.Line, StringComparison.OrdinalIgnoreCase));
            _results.Add(bench);
            _list.ShowResult(bench);
        });

        try
        {
            await tester.RunAsync(progress, _cts.Token);

            var ordered = ProxySpeedTest.SortProxies(proxies, _results);
            UpdateLastBench();
            _list.SetProxies(ordered);
            SetStatus("测速完成。");
        }
        catch (OperationCanceledException)
        {
            SetStatus("测速已取消。");
        }
        catch (Exception ex)
        {
            SetStatus("测速失败：" + ex.Message);
        }
        finally
        {
            _dotsTimer.Stop();
            _running = false;
            try
            {
                _cts?.Dispose();
            }
            catch
            {
                // 忽略
            }
            _cts = null;
            UpdateButtons();

            if (_closeAfterTest)
            {
                _closeReady = true;
                Close();
            }
        }
    }

    private void UpdateLastBench()
    {
        var current = TextLines.Split(_settings.ProxyList);
        var kept = _settings.LastBench
            .Where(entry => current.Any(line => line.Equals(entry.Line, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        foreach (var result in _results)
        {
            if (!current.Any(line => line.Equals(result.Line, StringComparison.OrdinalIgnoreCase))) continue;
            kept.RemoveAll(entry => entry.Line.Equals(result.Line, StringComparison.OrdinalIgnoreCase));
            kept.Add(result);
        }

        _settings.LastBench = kept;
        try
        {
            _settings.Save();
        }
        catch
        {
            // 忽略
        }
    }

    private void OnDotsTick(object? sender, EventArgs e)
    {
        _dotFrame = (_dotFrame + 1) % 3;
        var dots = new string('.', _dotFrame + 1);
        var done = new HashSet<string>(_results.Select(item => item.Line), StringComparer.OrdinalIgnoreCase);

        foreach (var line in _list.Proxies)
        {
            if (!done.Contains(line)) _list.SetDetail(line, "测速中" + dots);
        }
    }

    private static int ClampInt(string text, int min, int max, int fallback)
    {
        if (!int.TryParse(text.Trim(), out var value)) return fallback;
        return Math.Clamp(value, min, max);
    }
}
