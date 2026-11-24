using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;
using CntlmGUI.Models;
using CntlmGUI.Services;

namespace CntlmGUI;

public sealed class MainForm : Form
{
    private const int LogLimit = 256 * 1024;
    private const int LogKeep = 32 * 1024;

    private readonly GuiSettings _settings;
    private readonly CntlmController _controller;
    private readonly Icon _appIcon;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _trayToggle;
    private readonly ToolStripMenuItem _trayExit;

    private readonly ConcurrentQueue<string> _guiQueue = new();
    private readonly ConcurrentQueue<string> _cntlmQueue = new();
    private readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 2000 };
    private readonly System.Windows.Forms.Timer _flushTimer = new() { Interval = 150 };

    private readonly Button _btnStartStop;
    private readonly Button _btnSettings;
    private readonly Button _btnRefresh;
    private readonly Label _statusLabel;
    private readonly Label _chainLabel;
    private readonly RichTextBox _guiLog;
    private readonly RichTextBox _cntlmLog;
    private readonly TextBox _configBox;

    private bool _busy;
    private bool _refreshing;
    private bool _exiting;
    private bool _lastRunning;
    private bool _shown;

    public MainForm(GuiSettings settings)
    {
        _settings = settings;
        _appIcon = LoadAppIcon();

        Text = "CntlmGUI - Cntlm 代理管理前端";
        Font = AppTheme.Ui;
        BackColor = Color.White;
        ClientSize = new Size(880, 590);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        Icon = _appIcon;
        MinimumSize = Size;
        MaximumSize = Size;

        _controller = new CntlmController(_settings);
        _controller.Log += message => _guiQueue.Enqueue($"[{DateTime.Now:HH:mm:ss}] {message}");
        _controller.CntlmLog += message => _cntlmQueue.Enqueue(message);

        // 工具栏
        var toolbar = new Panel { Location = new Point(0, 0), Size = new Size(880, 52), BackColor = Color.White };
        toolbar.Paint += (_, e) =>
        {
            using var pen = new Pen(AppTheme.Line);
            e.Graphics.DrawLine(pen, 0, toolbar.Height - 1, toolbar.Width, toolbar.Height - 1);
        };
        Controls.Add(toolbar);

        _btnStartStop = AppTheme.CreateButton("启动");
        _btnStartStop.AutoSize = false;
        _btnStartStop.Size = new Size(104, 34);
        _btnStartStop.Location = new Point(16, 9);
        _btnStartStop.Click += OnStartStopClick;
        toolbar.Controls.Add(_btnStartStop);

        _btnSettings = AppTheme.CreateButton("设置");
        _btnSettings.AutoSize = false;
        _btnSettings.Size = new Size(104, 34);
        _btnSettings.Location = new Point(132, 9);
        _btnSettings.Click += OnSettingsClick;
        toolbar.Controls.Add(_btnSettings);

        _btnRefresh = AppTheme.CreateButton("刷新");
        _btnRefresh.AutoSize = false;
        _btnRefresh.Size = new Size(104, 34);
        _btnRefresh.Location = new Point(248, 9);
        _btnRefresh.Click += OnRefreshClick;
        toolbar.Controls.Add(_btnRefresh);

        // 状态区
        var statusPanel = new Panel { Location = new Point(0, 52), Size = new Size(880, 92), BackColor = Color.White };
        statusPanel.Paint += (_, e) =>
        {
            using var pen = new Pen(AppTheme.Line);
            e.Graphics.DrawLine(pen, 0, statusPanel.Height - 1, statusPanel.Width, statusPanel.Height - 1);
        };
        Controls.Add(statusPanel);

        _statusLabel = new Label
        {
            Location = new Point(16, 10),
            Size = new Size(848, 30),
            Font = new Font("Microsoft YaHei UI", 12.5F, FontStyle.Bold),
            ForeColor = AppTheme.Danger,
            Text = "○ 未运行（进程） —— 正在读取状态…",
            AutoEllipsis = true,
        };
        statusPanel.Controls.Add(_statusLabel);

        _chainLabel = new Label
        {
            Location = new Point(16, 46),
            Size = new Size(848, 22),
            Font = AppTheme.Ui,
            ForeColor = AppTheme.Muted,
            AutoEllipsis = true,
        };
        statusPanel.Controls.Add(_chainLabel);

        // 页签
        var tabs = new TabControl
        {
            Location = new Point(12, 152),
            Size = new Size(856, 426),
            Font = AppTheme.Ui,
        };
        Controls.Add(tabs);

        var logPage = new TabPage("运行日志") { BackColor = Color.White, Padding = new Padding(8) };
        var configPage = new TabPage("生成的 cntlm 配置") { BackColor = Color.White, Padding = new Padding(8) };
        tabs.TabPages.Add(logPage);
        tabs.TabPages.Add(configPage);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterWidth = 6,
            BackColor = AppTheme.Line,
        };
        logPage.Controls.Add(split);

        _guiLog = CreateLogBox(_guiQueue);
        _cntlmLog = CreateLogBox(_cntlmQueue);
        split.Panel1.Controls.Add(BuildLogPanel("CntlmGUI 日志", _guiLog));
        split.Panel2.Controls.Add(BuildLogPanel("Cntlm 日志", _cntlmLog));

        _configBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = AppTheme.Mono,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.White,
            ForeColor = AppTheme.Text,
        };
        configPage.Controls.Add(_configBox);

        // 托盘
        _tray = new NotifyIcon
        {
            Icon = _appIcon,
            Text = "CntlmGUI - 未运行",
            Visible = true,
        };
        var trayMenu = new ContextMenuStrip { Font = AppTheme.Ui };
        _trayToggle = new ToolStripMenuItem("启动");
        _trayToggle.Click += async (_, _) => await StartStopCoreAsync();
        _trayExit = new ToolStripMenuItem("退出");
        _trayExit.Click += OnTrayExitClick;
        trayMenu.Items.Add(_trayToggle);
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add(_trayExit);
        _tray.ContextMenuStrip = trayMenu;
        _tray.DoubleClick += (_, _) =>
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        };

        _statusTimer.Tick += async (_, _) => await RefreshStatusAsync();
        _flushTimer.Tick += (_, _) => FlushLogs();
        _statusTimer.Start();
        _flushTimer.Start();

        RefreshPreview();
        UpdateChainLabel();
        UpdateButtons();
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_shown) return;
        _shown = true;

        Log("CntlmGUI 已启动。");
        await RefreshStatusAsync();

        if (!_settings.AutoStart) return;
        await Task.Delay(300);
        var status = await Task.Run(() => _controller.GetStatus());
        if (status.Running)
        {
            Log("检测到已在运行，跳过自动启动。");
            return;
        }

        await StartStopCoreAsync();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exiting && _settings.CloseToTray && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            _tray.ShowBalloonTip(1500, "CntlmGUI", "已最小化到托盘。", ToolTipIcon.Info);
            return;
        }

        if (!_exiting)
        {
            _exiting = true;
            try
            {
                _controller.StopOnExit();
            }
            catch
            {
                // 忽略
            }
        }

        _statusTimer.Stop();
        _flushTimer.Stop();
        _tray.Visible = false;
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _tray.Dispose();
        _appIcon.Dispose();
        base.OnFormClosed(e);
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_QUERYENDSESSION = 0x0011;
        const int WM_ENDSESSION = 0x0016;

        if (m.Msg == WM_QUERYENDSESSION)
        {
            _exiting = true;
            try
            {
                _controller.StopOnExit();
            }
            catch
            {
                // 忽略
            }
            m.Result = (IntPtr)1;
            return;
        }

        if (m.Msg == WM_ENDSESSION)
        {
            _exiting = true;
            try
            {
                _controller.StopOnExit();
            }
            catch
            {
                // 忽略
            }
        }

        base.WndProc(ref m);
    }

    private static Panel BuildLogPanel(string title, RichTextBox box)
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
        var label = new Label
        {
            Dock = DockStyle.Top,
            Height = 24,
            Text = title,
            Font = AppTheme.UiBold,
            ForeColor = AppTheme.Text,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(4, 0, 0, 0),
        };
        panel.Controls.Add(box);
        panel.Controls.Add(label);
        return panel;
    }

    private RichTextBox CreateLogBox(ConcurrentQueue<string> queue)
    {
        var box = new LogTextBox
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(30, 30, 30),
            ForeColor = Color.FromArgb(240, 240, 240),
            Font = AppTheme.Mono,
            BorderStyle = BorderStyle.None,
            ReadOnly = true,
            HideSelection = false,
            WordWrap = false,
            DetectUrls = false,
            ScrollBars = RichTextBoxScrollBars.Vertical,
        };

        var menu = new ContextMenuStrip { Font = AppTheme.Ui };
        var clear = new ToolStripMenuItem("清空");
        clear.Click += (_, _) =>
        {
            box.Clear();
            while (queue.TryDequeue(out _)) { }
        };
        var selectAll = new ToolStripMenuItem("全选");
        selectAll.Click += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
        menu.Items.Add(clear);
        menu.Items.Add(selectAll);
        box.ContextMenuStrip = menu;
        return box;
    }

    private void UpdateButtons()
    {
        _btnStartStop.Enabled = !_busy;
        _btnRefresh.Enabled = !_busy;
        _btnSettings.Enabled = !_busy && !_lastRunning;
    }

    private async void OnStartStopClick(object? sender, EventArgs e) => await StartStopCoreAsync();

    private void OnSettingsClick(object? sender, EventArgs e)
    {
        using var form = new SettingsForm(_settings);
        if (form.ShowDialog(this) != DialogResult.OK) return;

        _controller.Settings = _settings;
        RefreshPreview();
        UpdateChainLabel();
        Log("配置已保存。");
        _ = RefreshStatusAsync();
    }

    private void OnRefreshClick(object? sender, EventArgs e)
    {
        RefreshPreview();
        UpdateChainLabel();
        _ = RefreshStatusAsync();
    }

    private async void OnTrayExitClick(object? sender, EventArgs e)
    {
        _exiting = true;
        _tray.Visible = false;
        try
        {
            await Task.Run(() => _controller.StopOnExit());
        }
        catch
        {
            // 忽略
        }
        Close();
    }

    private async Task StartStopCoreAsync()
    {
        if (_busy) return;
        _busy = true;
        UpdateButtons();

        try
        {
            var status = await Task.Run(() => _controller.GetStatus());
            if (status.Running)
            {
                Log("正在停止 cntlm…");
                await _controller.StopAsync();
                Log("已发送停止请求。");
            }
            else
            {
                Log("正在启动 cntlm…");
                await _controller.StartAsync();
            }
        }
        catch (Exception ex)
        {
            Log("操作失败：" + ex.Message);
        }
        finally
        {
            _busy = false;
            await RefreshStatusAsync();
            await Task.Delay(1200);
            await RefreshStatusAsync();
            UpdateButtons();
        }
    }

    private async Task RefreshStatusAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var status = await Task.Run(() => _controller.GetStatus());
            ApplyStatus(status);
        }
        catch
        {
            // 状态读取失败时保持现有显示
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void ApplyStatus(StatusInfo status)
    {
        _lastRunning = status.Running;
        _statusLabel.Text = status.Running
            ? $"● 运行中（{status.Mode}） —— {status.Detail}"
            : $"○ 未运行（{status.Mode}） —— {status.Detail}";
        _statusLabel.ForeColor = status.Running ? AppTheme.Success : AppTheme.Danger;

        _btnStartStop.Text = status.Running ? "停止" : "启动";
        _trayToggle.Text = status.Running ? "停止" : "启动";
        _tray.Text = status.Running ? "CntlmGUI - 运行中" : "CntlmGUI - 未运行";
        UpdateButtons();
    }

    private void RefreshPreview()
    {
        try
        {
            _configBox.Text = CntlmConfigGenerator.Generate(_settings);
        }
        catch (Exception ex)
        {
            _configBox.Text = "生成配置失败：" + ex.Message;
        }
    }

    private void UpdateChainLabel()
    {
        var (host, port) = GuiSettings.SplitListen(_settings.Listen);
        var listen = $"{host}:{port}";
        var proxies = TextLines.Split(_settings.ProxyList);

        if (!_settings.UseProxy || proxies.Length == 0)
        {
            _chainLabel.Text = $"一级（本机 {listen}） → 直连（未启用二级）";
            return;
        }

        var extra = proxies.Length - 1;
        _chainLabel.Text = $"一级（本机 {listen}） → 二级 {proxies[0]}" + (extra > 0 ? $"（备用 {extra}）" : "");
    }

    private void Log(string message) => _guiQueue.Enqueue($"[{DateTime.Now:HH:mm:ss}] {message}");

    private void FlushLogs()
    {
        AppendQueued(_guiLog, _guiQueue);
        AppendQueued(_cntlmLog, _cntlmQueue);
    }

    private static void AppendQueued(RichTextBox box, ConcurrentQueue<string> queue)
    {
        if (queue.IsEmpty || box.IsDisposed) return;

        var sb = new StringBuilder();
        while (queue.TryDequeue(out var line))
        {
            sb.Append(line);
            sb.Append("\r\n");
        }

        if (sb.Length == 0) return;
        AppendText(box, sb.ToString());
    }

    private static void AppendText(RichTextBox box, string text)
    {
        box.AppendText(text);

        if (box.TextLength > LogLimit)
        {
            try
            {
                var content = box.Text;
                var start = Math.Max(0, content.Length - LogKeep);
                var newline = content.IndexOf('\n', start);
                if (newline >= 0) start = newline + 1;
                box.Text = content[start..];
            }
            catch
            {
                // 忽略
            }
        }

        if (box.SelectionLength > 0) return;

        box.SelectionStart = box.TextLength;
        box.ScrollToCaret();
    }

    private static Icon LoadAppIcon()
    {
        try
        {
            using var stream = typeof(MainForm).Assembly.GetManifestResourceStream("CntlmGUI.App.ico");
            if (stream != null) return new Icon(stream);
        }
        catch
        {
            // 回退到自绘图标
        }

        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(AppTheme.Accent);
            g.FillEllipse(brush, 1, 1, 30, 30);
            using var pen = new Pen(Color.White, 3.5f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round,
            };
            g.DrawLines(pen, new[]
            {
                new PointF(9, 17),
                new PointF(14, 22),
                new PointF(24, 10),
            });
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private sealed class LogTextBox : RichTextBox
    {
        private const int WM_SETFOCUS = 0x0007;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_SETFOCUS) return;
            base.WndProc(ref m);
        }
    }
}
