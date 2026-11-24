using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CntlmGUI.Models;
using CntlmGUI.Services;

namespace CntlmGUI;

public class MainForm : Form
{
    private const int MaxLogChars = 256 * 1024;   // 日志上限，超过则从头部裁剪
    private const int TrimLogChars = 32 * 1024;   // 每次裁剪量
    private const int StatusPollMs = 2000;
    private const int LogFlushMs = 150;
    private const int StatusRecheckDelayMs = 1200;

    private readonly GuiSettings _settings;
    private readonly CntlmController _controller;
    private readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = StatusPollMs };
    private readonly System.Windows.Forms.Timer _logFlushTimer = new() { Interval = LogFlushMs };
    private readonly ConcurrentQueue<string> _guiLogQueue = new();
    private readonly ConcurrentQueue<string> _cntlmLogQueue = new();

    private CntlmStatus _lastStatus = new(false, "未运行");
    private bool _statusBusy;
    private bool _busy;
    private bool _forceExit;
    private bool _shuttingDown;

    private Button _btnToggle = null!;
    private Button _btnSettings = null!;
    private Label _lblStatus = null!;
    private Label _lblChain = null!;
    private RichTextBox _logGui = null!;
    private RichTextBox _logCntlm = null!;
    private TextBox _cfgPreview = null!;
    private NotifyIcon _notify = null!;
    private ToolStripMenuItem? _trayToggle;

    public MainForm()
    {
        _settings = GuiSettings.Load();
        _controller = new CntlmController(_settings);
        _controller.Log += AppendLog;
        _controller.CntlmLog += AppendCntlmLog;

        Text = "CntlmGUI - Cntlm 代理管理前端";
        Font = AppTheme.Ui;
        BackColor = Color.White;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(880, 590);
        MinimumSize = new Size(880, 590);
        MaximumSize = new Size(880, 590);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        Icon = AppIcon();

        BuildUi();
        BuildTray();

        ApplyStatus(_lastStatus);
        _statusTimer.Tick += (_, _) => _ = RefreshStatusAsync();
        _statusTimer.Start();

        _logFlushTimer.Tick += (_, _) => FlushPendingLogs();
        _logFlushTimer.Start();
    }

    // ---------------- 界面 ----------------

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 3,
            ColumnCount = 1,
            Padding = new Padding(10),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        // 工具栏
        var top = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        _btnToggle = MakeButton("启动", BtnToggle_Click);
        _btnSettings = MakeButton("设置", (_, _) => OpenSettings());
        var btnRefresh = MakeButton("刷新", (_, _) => _ = RefreshStatusAsync());
        top.Controls.AddRange(new Control[] { _btnToggle, _btnSettings, btnRefresh });
        root.Controls.Add(top, 0, 0);

        // 状态行 + 链路
        var statusRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 8, 0, 4), FlowDirection = FlowDirection.TopDown };
        _lblStatus = new Label { AutoSize = true, Font = AppTheme.UiTitle };
        _lblChain = new Label { AutoSize = true, ForeColor = AppTheme.Muted, Font = AppTheme.UiSmall };
        statusRow.Controls.Add(_lblStatus);
        statusRow.Controls.Add(_lblChain);
        root.Controls.Add(statusRow, 0, 1);

        // 日志（上下分区：CntlmGUI / cntlm）/ 配置预览
        var tabs = new TabControl { Dock = DockStyle.Fill, Margin = new Padding(0) };

        (_logGui, _logCntlm) = BuildLogSplit(out var logTab);
        tabs.TabPages.Add(logTab);

        _cfgPreview = new TextBox { Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Both, Font = AppTheme.Mono };
        _cfgPreview.Text = RenderPreview();
        var cfgTab = new TabPage("生成的 cntlm 配置");
        cfgTab.Controls.Add(_cfgPreview);
        tabs.TabPages.Add(cfgTab);

        root.Controls.Add(tabs, 0, 2);
    }

    /// <summary>构建上下分区的日志面板：上为 CntlmGUI 日志，下为 cntlm 进程日志。</summary>
    private (RichTextBox gui, RichTextBox cntlm) BuildLogSplit(out TabPage tab)
    {
        tab = new TabPage("运行日志");
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterWidth = 6,
        };

        var guiHost = MakeLogSection("CntlmGUI 日志", out var guiBox);
        var cntlmHost = MakeLogSection("Cntlm 日志", out var cntlmBox);
        split.Panel1.Controls.Add(guiHost);
        split.Panel2.Controls.Add(cntlmHost);
        tab.Controls.Add(split);

        // 布局完成后按可用高度平分上下两区
        tab.Resize += (_, _) =>
        {
            try { split.SplitterDistance = Math.Max(split.Panel1MinSize, (split.Height - split.SplitterWidth) / 2); }
            catch { /* 高度不足时忽略 */ }
        };
        return (guiBox, cntlmBox);
    }

    private static Panel MakeLogSection(string title, out RichTextBox result)
    {
        var host = new Panel { Dock = DockStyle.Fill };
        RichTextBox box = new NoCaretRichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = Color.FromArgb(30, 30, 30),
            ForeColor = Color.FromArgb(240, 240, 240),
            Font = AppTheme.Mono,
            HideSelection = false,
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add("清空", null, (_, _) => box.Clear());
        menu.Items.Add("全选", null, (_, _) => box.SelectAll());
        box.ContextMenuStrip = menu;
        var header = new Label
        {
            Dock = DockStyle.Top,
            Height = 26,
            Text = title,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(4, 4, 0, 0),
            ForeColor = AppTheme.Text,
            BackColor = AppTheme.Panel,
            Font = AppTheme.UiBold,
        };
        host.Controls.Add(box);
        host.Controls.Add(header);
        result = box;
        return host;
    }

    private Button MakeButton(string text, EventHandler onClick)
    {
        var b = new Button { Text = text, AutoSize = false, Width = 96, Margin = new Padding(0, 0, 10, 0) };
        AppTheme.StyleButton(b);
        b.Click += onClick;
        return b;
    }

    private string RenderPreview() =>
        "（生成配置预览，实际以启动时写入的文件为准）\r\n\r\n" + _controller.PreviewConfig();

    private void RefreshPreview() => _cfgPreview.Text = RenderPreview();

    // ---------------- 托盘 ----------------

    private void BuildTray()
    {
        _notify = new NotifyIcon
        {
            Icon = AppIcon(),
            Text = "CntlmGUI",
            Visible = true,
        };
        var menu = new ContextMenuStrip();
        _trayToggle = new ToolStripMenuItem("启动");
        _trayToggle.Click += BtnToggle_Click;
        menu.Items.Add(_trayToggle);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => { _forceExit = true; Close(); });
        _notify.ContextMenuStrip = menu;
        _notify.DoubleClick += (_, _) => RestoreFromTray();
        UpdateTrayToggle(false);
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    /// <summary>托盘菜单启动/停止项文本随运行状态翻转。</summary>
    private void UpdateTrayToggle(bool running)
    {
        if (_trayToggle == null) return;
        _trayToggle.Text = running ? "停止" : "启动";
    }

    // 系统会话结束（注销/关机/安装器通过 WM_QUERYENDSESSION 请求退出）时必须真正退出，
    // 不能走“最小化到托盘”逻辑，否则安装/升级时文件被占用、关机被拖延
    private const int WM_QUERYENDSESSION = 0x0011;
    private const int WM_ENDSESSION = 0x0016;

    protected override void WndProc(ref Message m)
    {
        if ((m.Msg == WM_QUERYENDSESSION || m.Msg == WM_ENDSESSION) && m.WParam != IntPtr.Zero)
        {
            _forceExit = true;
            if (m.Msg == WM_QUERYENDSESSION) m.Result = (IntPtr)1; // 允许结束会话
            Close();
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_forceExit && _settings.CloseToTray && !_shuttingDown)
        {
            e.Cancel = true;
            Hide();
            _notify.ShowBalloonTip(2000, "CntlmGUI", "已最小化到托盘。", ToolTipIcon.Info);
            return;
        }

        // 真正退出：先在后台清理（进程模式强杀子进程），避免阻塞 UI
        if (!_shuttingDown)
        {
            e.Cancel = true;
            _shuttingDown = true;
            _ = ShutdownAsync();
            return;
        }
        base.OnFormClosing(e);
    }

    private async Task ShutdownAsync()
    {
        try
        {
            _statusTimer.Stop();
            _logFlushTimer.Stop();
            await Task.Run(() => _controller.StopOnExit());
        }
        catch (Exception ex)
        {
            AppendLog("退出清理失败: " + ex.Message);
        }
        finally
        {
            _notify?.Dispose();
            _statusTimer.Dispose();
            _logFlushTimer.Dispose();
            Close();
        }
    }

    private static Icon? _appIcon;

    private static Icon AppIcon() => _appIcon ??= LoadAppIcon() ?? BuildTrayIcon();

    private static Icon? LoadAppIcon()
    {
        try
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("App.ico", StringComparison.OrdinalIgnoreCase));
            if (name != null)
                using (var s = asm.GetManifestResourceStream(name))
                    return s != null ? new Icon(s) : null;
        }
        catch
        {
            // 回退到绘制图标
        }
        return null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    private static Icon BuildTrayIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(Color.FromArgb(0, 150, 136));
        g.FillEllipse(brush, 1, 1, 30, 30);
        using var pen = new Pen(Color.White, 4f);
        g.DrawLine(pen, 9, 16, 15, 22);
        g.DrawLine(pen, 15, 22, 23, 10);

        // GetHicon 返回的句柄需要手动释放；Clone 后图标自己持有副本
        var handle = bmp.GetHicon();
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

    // ---------------- 状态 ----------------

    /// <summary>
    /// 轮询运行状态。GetStatus 会做端口探测，放到后台线程避免阻塞 UI。
    /// </summary>
    private async Task RefreshStatusAsync()
    {
        if (_statusBusy) return;
        _statusBusy = true;
        try
        {
            var st = await Task.Run(() => _controller.GetStatus());
            _lastStatus = st;
            ApplyStatus(st);
        }
        catch
        {
            // 轮询失败，等下一轮
        }
        finally
        {
            _statusBusy = false;
        }
    }

    private void ApplyStatus(CntlmStatus st)
    {
        var detail = CleanStatus(st.Detail);
        var mode = _settings.UseService ? "服务" : "进程";
        _lblStatus.Text = st.Running
            ? detail.Length > 0 ? $"● 运行中（{mode}） —— {detail}" : $"● 运行中（{mode}）"
            : detail.Length > 0 ? $"○ 未运行（{mode}） —— {detail}" : $"○ 未运行（{mode}）";
        _lblStatus.ForeColor = st.Running ? AppTheme.Success : AppTheme.Danger;
        _btnToggle.Text = st.Running ? "停止" : "启动";
        _btnToggle.Enabled = !_busy;
        _btnSettings.Enabled = !_busy && !st.Running; // 改配置前必须先停止 cntlm
        _notify.Text = "CntlmGUI - " + (st.Running ? "运行中" : "未运行");
        UpdateTrayToggle(st.Running);

        // 代理链路（展示的是“配置意图”，与实际运行状态无关）
        if (!_settings.UseProxy)
        {
            _lblChain.Text = $"一级（本机 {_settings.Listen}） → 直连（未启用二级）";
            return;
        }

        var proxyLines = TextLines.Split(_settings.ProxyList);
        var firstProxy = proxyLines.FirstOrDefault();
        _lblChain.Text = firstProxy != null
            ? $"一级（本机 {_settings.Listen}） → 二级 {firstProxy}{(proxyLines.Count > 1 ? $"（备用 {proxyLines.Count - 1}）" : "")}"
            : $"一级（本机 {_settings.Listen}） → 直连（未配置二级）";
    }

    private static string CleanStatus(string detail)
    {
        var s = (detail ?? "").Trim();
        // 去掉与状态词重复的前缀："运行中 · 监听 …" → "监听 …"
        foreach (var prefix in new[] { "运行中 · ", "运行中", "未运行" })
            if (s.StartsWith(prefix, StringComparison.Ordinal))
                return s[prefix.Length..].Trim();
        return s;
    }

    private async void BtnToggle_Click(object? sender, EventArgs e)
    {
        if (_busy) return;
        _busy = true;
        ApplyStatus(_lastStatus); // 立即反馈按钮禁用
        try
        {
            var running = await Task.Run(() => _controller.GetStatus().Running);
            if (running)
            {
                await _controller.StopAsync();
            }
            else
            {
                await _controller.StartAsync();
                RefreshPreview();
            }
        }
        catch (Exception ex)
        {
            AppendLog("操作发生异常: " + ex.Message);
        }
        finally
        {
            _busy = false;
            await RefreshStatusAsync();
            await Task.Delay(StatusRecheckDelayMs); // 延迟一次状态校验
            await RefreshStatusAsync();
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_settings.AutoStart) _ = EnsureStartedAsync();
    }

    private async Task EnsureStartedAsync()
    {
        if (_busy) return;
        var st = await Task.Run(() => _controller.GetStatus());
        if (st.Running)
        {
            AppendLog("检测到已在运行，跳过自动启动。");
            ApplyStatus(st);
            return;
        }

        _busy = true;
        ApplyStatus(st);
        try
        {
            AppendLog("自动启动...");
            await _controller.StartAsync();
            await Task.Delay(800);
        }
        catch (Exception ex)
        {
            AppendLog("自动启动失败: " + ex.Message);
        }
        finally
        {
            _busy = false;
            await RefreshStatusAsync();
        }
    }

    private void OpenSettings()
    {
        using var f = new SettingsForm(_settings);
        if (f.ShowDialog(this) != DialogResult.OK) return;

        _controller.Cmd = _settings.EffectiveBaseCntlmPath() ?? "";
        RefreshPreview();
        ApplyStatus(_lastStatus);
        AppendLog("配置已保存。");
    }

    // ---------------- 日志 ----------------
    //
    // 日志洪峰时逐行 BeginInvoke 会塞满 UI 消息队列，导致整个界面假死
    // （输入事件被饿死，只有日志区还在刷新）。改为：后台线程只入队，
    // UI 定时器每 150ms 批量刷入一次，每次只触发一次重绘与滚动。

    private void AppendLog(string line) => EnqueueLog(_guiLogQueue, line);

    private void AppendCntlmLog(string line) => EnqueueLog(_cntlmLogQueue, line);

    private static void EnqueueLog(ConcurrentQueue<string> q, string line)
    {
        if (string.IsNullOrEmpty(line)) return;
        q.Enqueue($"[{DateTime.Now:HH:mm:ss}] {line}\r\n");
    }

    private void FlushPendingLogs()
    {
        FlushTo(_logGui, _guiLogQueue);
        FlushTo(_logCntlm, _cntlmLogQueue);
    }

    private static void FlushTo(RichTextBox box, ConcurrentQueue<string> q)
    {
        if (q.IsEmpty) return;
        var sb = new StringBuilder(4096);
        while (q.TryDequeue(out var line)) sb.Append(line);
        box.AppendText(sb.ToString());

        // 用户正在框内选择文本（复制日志）时不强制滚到底
        if (!(box.Focused && box.SelectionLength > 0))
        {
            box.SelectionStart = box.TextLength;
            box.ScrollToCaret();
        }

        TrimExcessLog(box);
    }

    /// <summary>日志超过上限时从头部裁到上限以下（按整行），防止长期运行文本过大导致追加变慢。</summary>
    private static void TrimExcessLog(RichTextBox box)
    {
        if (box.TextLength <= MaxLogChars) return;
        var text = box.Text;
        // 必须一次裁到上限以下并留出余量：若只裁固定小段，洪峰时每批新增量
        // 大于裁剪量，文本仍会无界增长
        int cut = box.TextLength - MaxLogChars + TrimLogChars;
        int nl = text.IndexOf('\n', Math.Min(cut, text.Length - 1)); // 裁到行尾，避免留半行
        if (nl >= 0) cut = nl + 1;
        if (cut <= 0) return;

        // 只读状态下 SelectedText 赋值无效，需临时解除
        box.ReadOnly = false;
        try
        {
            if (cut >= text.Length)
            {
                box.Clear();
            }
            else
            {
                box.Select(0, cut);
                box.SelectedText = string.Empty;
            }
        }
        finally
        {
            box.ReadOnly = true;
        }
        box.SelectionStart = box.TextLength;
    }
}

/// <summary>只读日志框：吞掉 WM_SETFOCUS，不显示闪烁的文本插入光标。</summary>
public class NoCaretRichTextBox : RichTextBox
{
    private const int WM_SETFOCUS = 0x0007;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_SETFOCUS) return;
        base.WndProc(ref m);
    }
}
