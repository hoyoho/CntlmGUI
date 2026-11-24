using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CntlmGUI.Models;
using CntlmGUI.Services;

namespace CntlmGUI;

/// <summary>
/// 多候选二级代理测速：顶部配置目标与吞吐参数，中部结果列表逐条回填。
/// 增删/排序通过按钮与右键菜单完成，支持中途取消；关闭窗口会先取消正在进行的测速。
/// </summary>
public class ProxySpeedForm : Form
{
    private const int StatusTitleMax = 60;

    private readonly GuiSettings _s;
    private readonly ProxySpeedTest _tester;
    private readonly System.Windows.Forms.Timer _animTimer = new() { Interval = 300 };
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly ToolTip _tip = new();

    private TextBox _targetUrl = null!;
    private NumericUpDown _probeMb = null!, _probeTimeout = null!;
    private Button _btnRun = null!, _btnSetFirst = null!, _btnUp = null!, _btnDown = null!;
    private ProxyResultList _list = null!;

    private bool _running;
    private bool _closeAfterRun;
    private Task? _runTask;
    private List<ProxyBench> _last;
    private int _animPhase;
    private CancellationTokenSource? _cts;
    private int _editRowIndex = -1;  // 允许就地编辑的行索引；-1 表示当前不允许

    public ProxySpeedForm(GuiSettings s)
    {
        _s = s;
        _tester = new ProxySpeedTest(s);
        _animTimer.Tick += (_, _) => AnimatePending();
        _last = s.LastBench.ToList();

        Text = "代理列表";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(900, 600);
        MinimumSize = new Size(900, 600);
        MaximumSize = new Size(900, 600);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        Font = AppTheme.Ui;
        BackColor = Color.White;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), RowCount = 3, ColumnCount = 1 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        root.Controls.Add(BuildButtonRow(), 0, 0);
        root.Controls.Add(BuildProbeRow(), 0, 1);

        _list = new ProxyResultList();
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete) DeleteSelected();
            else if (e.KeyCode == Keys.F2) StartInlineEdit();
        };
        _list.DoubleClick += (_, _) => OnListDoubleClick();
        _list.BeforeLabelEdit += (_, e) =>
        {
            if (!_list.IsAddRow(e.Item) && e.Item != _editRowIndex) e.CancelEdit = true;
        };
        _list.AfterLabelEdit += OnAfterLabelEdit;
        _list.ContextMenuStrip = BuildMenu();
        root.Controls.Add(_list, 0, 2);

        Controls.Add(root);
        LoadCandidates();
    }

    // ---------------- 界面 ----------------

    private Control BuildButtonRow()
    {
        var row = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0, 0, 0, 8),
        };

        _btnRun = new Button { Text = "测速并重排", AutoSize = true };
        AppTheme.StyleButton(_btnRun);
        _btnRun.Click += (_, _) => { if (_running) CancelAsync(); else _runTask = RunAndSortAsync(); };
        _tip.SetToolTip(_btnRun, "顺序测试每个代理，完成后按吞吐重排代理顺序并保存。");

        _btnUp = new Button { Text = "上移", AutoSize = true };
        AppTheme.StyleButton(_btnUp);
        _btnUp.Click += (_, _) => MoveSelected(-1);

        _btnDown = new Button { Text = "下移", AutoSize = true };
        AppTheme.StyleButton(_btnDown);
        _btnDown.Click += (_, _) => MoveSelected(+1);

        _btnSetFirst = new Button { Text = "置顶", AutoSize = true };
        AppTheme.StyleButton(_btnSetFirst);
        _btnSetFirst.Click += (_, _) => SetFirst();

        _btnUp.Enabled = _btnDown.Enabled = _btnSetFirst.Enabled = false;
        row.Controls.AddRange(new Control[] { _btnRun, _btnUp, _btnDown, _btnSetFirst });
        return row;
    }

    private Control BuildProbeRow()
    {
        // 用 TableLayoutPanel 按比例分配宽度，避免 FlowLayoutPanel 的首选宽度把整窗撑宽
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 6,
            Margin = new Padding(0, 0, 0, 8),
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 4; i++) row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        row.Controls.Add(FieldLabel("目标:"), 0, 0);
        _targetUrl = new TextBox
        {
            Text = _s.SpeedTestUrl,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 3, 10, 3),
        };
        row.Controls.Add(_targetUrl, 1, 0);

        row.Controls.Add(FieldLabel("大小(MB):"), 2, 0);
        _probeMb = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 200,
            Value = Math.Clamp(_s.SpeedTestMb, 1, 200),
            Width = 60,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 3, 12, 3),
        };
        row.Controls.Add(_probeMb, 3, 0);

        row.Controls.Add(FieldLabel("超时(s):"), 4, 0);
        _probeTimeout = new NumericUpDown
        {
            Minimum = 3,
            Maximum = 300,
            Value = Math.Clamp(_s.SpeedTestTimeoutSec, 3, 300),
            Width = 60,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 3, 0, 3),
        };
        row.Controls.Add(_probeTimeout, 5, 0);

        var tip = new ToolTip();
        tip.SetToolTip(_targetUrl, "测试目标地址；整链与吞吐测试共用该地址。");
        tip.SetToolTip(_probeMb, "每个代理最多下载的兆字节数（Range 限制）。");
        tip.SetToolTip(_probeTimeout, "单个代理的吞吐测试时限；到达时限即停止，按已下载部分计算速度。");
        return row;
    }

    private static Label FieldLabel(string text) => new()
    {
        Text = text,
        AutoSize = false,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        Margin = new Padding(0, 0, 4, 0),
    };

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("添加代理…", null, (_, _) => AddProxyDialog());
        menu.Items.Add("编辑选中", null, (_, _) => StartInlineEdit());
        menu.Items.Add("删除选中", null, (_, _) => DeleteSelected());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("上移", null, (_, _) => MoveSelected(-1));
        menu.Items.Add("下移", null, (_, _) => MoveSelected(+1));
        menu.Items.Add("设为第一顺位", null, (_, _) => SetFirst());
        return menu;
    }

    // ---------------- 就地编辑 ----------------

    private void StartInlineEdit()
    {
        if (_running || _list.SelectedItems.Count != 1) return;
        var item = _list.SelectedItems[0];
        if (_list.IsAddRow(item.Index)) return;
        _editRowIndex = item.Index;
        item.BeginEdit();
    }

    private void OnAfterLabelEdit(object? sender, LabelEditEventArgs e)
    {
        int row = e.Item;
        bool addRow = _list.IsAddRow(row);
        if (!addRow && row != _editRowIndex)
        {
            e.CancelEdit = true;
            return;
        }

        var text = (e.Label ?? "").Trim();
        if (text.Length == 0)
        {
            e.CancelEdit = true;
            _editRowIndex = -1;
            if (addRow) _list.RestoreAddHint(row);
            return;
        }

        e.CancelEdit = true;
        _editRowIndex = -1;
        if (addRow) AddProxyEntry(text);
        else RenameRow(row, text);
    }

    /// <summary>把第 row 行的代理地址改为 newText，同步 Proxy 列表与既测结果。</summary>
    private void RenameRow(int row, string newText)
    {
        var oldText = _list.Items[row].Tag as string;
        if (string.IsNullOrEmpty(oldText) || oldText == newText) return;

        var lines = TextLines.Split(_s.ProxyList);
        for (int i = 0; i < lines.Count; i++)
            if (lines[i] == oldText) { lines[i] = newText; break; }
        _s.ProxyList = string.Join("\r\n", lines);

        // 同步对应的测速结果，保留已有成绩
        for (int i = 0; i < _last.Count; i++)
            if (_last[i].Line == oldText)
                _last[i] = _last[i] with { Line = newText };
        PersistBench();

        var item = _list.Items[row];
        item.Text = newText;
        item.Tag = newText;
        SetStatus($"已将 {oldText} 改为 {newText}。代理重启后生效。");
    }

    /// <summary>双击：占位行进入“添加”，已有行进入“就地编辑”（延迟到双击事件处理完，避免编辑器被第二次点击吃掉焦点）。</summary>
    private void OnListDoubleClick()
    {
        if (_running) return;
        var item = _list.FocusedItem ?? (_list.Items.Count > 0 ? _list.Items[^1] : null);
        if (item == null) return;

        if (_list.IsAddRow(item.Index))
        {
            item.SubItems[0].Text = "";
            item.BeginEdit();
            return;
        }

        _editRowIndex = item.Index;
        BeginInvoke(new Action(() =>
        {
            if (_editRowIndex >= 0 && _editRowIndex < _list.Items.Count)
                _list.Items[_editRowIndex].BeginEdit();
        }));
    }

    // ---------------- 列表内容 ----------------

    private void LoadCandidates()
    {
        var map = _last
            .GroupBy(b => b.Line, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        _list.ShowCandidates(TextLines.Split(_s.ProxyList), map);
        UpdateButtons();
    }

    private void PersistBench()
    {
        var alive = TextLines.Split(_s.ProxyList).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _last.RemoveAll(b => !alive.Contains(b.Line));
        _s.LastBench = _last.ToList();
        _s.Save();
    }

    /// <summary>状态显示在窗口标题上，不额外占用界面空间。</summary>
    private void SetStatus(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            Text = "代理列表";
            return;
        }
        if (text.Length > StatusTitleMax) text = text[..StatusTitleMax] + "…";
        Text = "代理列表 - " + text;
    }

    /// <summary>测速期间，未完成的行在“说明”列显示循环的“测速中...”。</summary>
    private void AnimatePending()
    {
        if (_pending.Count == 0) return;
        _animPhase = _animPhase % 3 + 1;
        _list.AnimatePending(_pending, _animPhase);
    }

    // ---------------- 增删 / 排序 ----------------

    private void AddProxyDialog()
    {
        using var dlg = new Form
        {
            Text = "添加代理",
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(360, 140),
            Font = AppTheme.Ui,
            BackColor = Color.White,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
        };
        var box = new TextBox { Left = 12, Top = 12, Width = 336 };
        var hint = new Label { Text = "格式: IP:端口，可一次输入多个（每行一个）", Left = 12, Top = 40, AutoSize = true, ForeColor = AppTheme.Muted };
        var ok = new Button { Text = "添加", Left = 286, Top = 100, Width = 64, Height = 30 };
        AppTheme.StyleButton(ok, primary: true);
        ok.DialogResult = DialogResult.OK;
        dlg.Controls.Add(box);
        dlg.Controls.Add(hint);
        dlg.Controls.Add(ok);
        dlg.AcceptButton = ok;
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        AddProxyEntry(box.Text);
    }

    private void AddProxyEntry(string text)
    {
        var items = TextLines.Split(text);
        if (items.Count == 0) return;

        var merged = TextLines.Split(_s.ProxyList);
        var seen = merged.ToHashSet(StringComparer.OrdinalIgnoreCase);
        int added = 0;
        foreach (var item in items)
            if (seen.Add(item)) { merged.Add(item); added++; }

        _s.ProxyList = string.Join("\r\n", merged);
        PersistBench();
        LoadCandidates();
        SetStatus(added > 0 ? $"已添加 {added} 个，点“测速”开始测试。" : "重复的代理已跳过。");
    }

    private void DeleteSelected()
    {
        if (_running || _list.SelectedItems.Count == 0) return;
        var remove = _list.SelectedItems.Cast<ListViewItem>()
            .Select(it => it.Tag as string)
            .Where(t => t != null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (remove.Count == 0) return;

        var lines = TextLines.Split(_s.ProxyList).Where(l => !remove.Contains(l)).ToList();
        _s.ProxyList = string.Join("\r\n", lines);
        _last.RemoveAll(b => remove.Contains(b.Line));
        PersistBench();
        LoadCandidates();
        SetStatus($"已删除 {remove.Count} 个代理。");
    }

    private void MoveSelected(int delta)
    {
        if (_running || _list.SelectedItems.Count == 0) return;
        int from = _list.SelectedItems[0].Index;
        var lines = TextLines.Split(_s.ProxyList);
        int to = from + delta;
        if (to < 0 || to >= lines.Count) return;

        (lines[from], lines[to]) = (lines[to], lines[from]);
        _s.ProxyList = string.Join("\r\n", lines);
        _s.Save();
        LoadCandidates();
        if (to < _list.Items.Count) _list.Items[to].Selected = true;
        SetStatus("已调整顺序（无需重新测速）。");
    }

    /// <summary>把选中行置为 Proxy 列表第一行（cntlm 将优先使用第一个可用的）。</summary>
    private void SetFirst()
    {
        if (_running || _list.SelectedItems.Count == 0) return;
        var pick = _list.SelectedItems[0].Tag as string;
        if (pick == null) return;

        var lines = TextLines.Split(_s.ProxyList);
        lines.Remove(pick);
        lines.Insert(0, pick);
        _s.ProxyList = string.Join("\r\n", lines);
        _s.Save();
        LoadCandidates();
        if (_list.Items.Count > 0) _list.Items[0].Selected = true;
        SetStatus($"已将 {pick} 设为第一顺位。代理重启后生效。");
    }

    // ---------------- 测速 ----------------

    private void CancelAsync()
    {
        try { _cts?.Cancel(); } catch { }
        SetStatus("正在停止…");
    }

    private async Task RunAndSortAsync()
    {
        if (_running) return;

        var target = _targetUrl.Text.Trim();
        if (target.Length == 0)
        {
            MessageBox.Show(this, "请填写测试 URL", "代理测速", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!_tester.Ready)
        {
            MessageBox.Show(this, "未找到 cntlm.exe，无法进行整链实测。请先在设置中指定可执行文件。",
                "代理测速", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        int probeMb = (int)_probeMb.Value;
        int probeTimeout = (int)_probeTimeout.Value;
        _s.SpeedTestUrl = ProxySpeedTest.NormalizeUrl(target);
        _s.SpeedTestMb = probeMb;
        _s.SpeedTestTimeoutSec = probeTimeout;
        _s.Save();

        var lines = TextLines.Split(_s.ProxyList);
        if (lines.Count == 0)
        {
            SetStatus("代理列表为空，右键列表添加代理。");
            return;
        }

        _running = true;
        UpdateButtons();
        _list.ShowPending(lines);
        _pending.Clear();
        foreach (var line in lines) _pending.Add(line);
        _animPhase = 0;
        _animTimer.Start();
        AnimatePending();
        SetStatus($"顺序测速中…（每个代理最多 {probeMb}MB / {probeTimeout}s）");

        var progress = new Progress<ProxyBench>(OnBenchResult);
        _cts = new CancellationTokenSource();
        try
        {
            _last = await _tester.BenchAsync(target, target,
                probeMb * 1024L * 1024L, probeTimeout, progress, _cts.Token);

            // 排序：整链可用 → 吞吐有效 → 下载速度 → 首字节 → TCP
            var ordered = _last
                .OrderByDescending(b => b.RelayOk)
                .ThenByDescending(b => b.SpeedTested && b.SpeedOk)
                .ThenByDescending(b => b.SpeedTested ? b.SpeedBps : long.MinValue)
                .ThenBy(b => b.RelayOk ? b.RelayMs : int.MaxValue)
                .ThenBy(b => b.TcpMs)
                .Select(b => b.Line)
                .ToList();
            foreach (var line in TextLines.Split(_s.ProxyList))
                if (!ordered.Contains(line)) ordered.Add(line);

            _s.ProxyList = string.Join("\r\n", ordered);
            LoadCandidates();
            PersistBench();

            var usable = _last.Count(b => b.RelayOk);
            var measured = _last.Count(b => b.SpeedTested && b.SpeedOk);
            SetStatus($"完成：{_last.Count} 个候选，{usable} 个整链可用，{measured} 个吞吐有效；已按结果重排并保存。");
        }
        catch (OperationCanceledException)
        {
            // 取消：保留已经测出的部分结果，未测的行回到“待测”
            LoadCandidates();
            PersistBench();
            SetStatus("已取消。");
        }
        catch (Exception ex)
        {
            SetStatus("测速失败: " + ex.Message);
        }
        finally
        {
            _running = false;
            _animTimer.Stop();
            _pending.Clear();
            _cts?.Dispose();
            _cts = null;
            UpdateButtons();
        }
    }

    private void OnBenchResult(ProxyBench b)
    {
        _pending.Remove(b.Line);
        if (_pending.Count == 0) _animTimer.Stop();

        var idx = _last.FindIndex(x => string.Equals(x.Line, b.Line, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) _last[idx] = b;
        else _last.Add(b);

        _list.UpdateRow(b);
    }

    private void UpdateButtons()
    {
        int count = _list.Items.Count;
        int realCount = count - (count > 0 && _list.IsAddRow(count - 1) ? 1 : 0);
        int selected = _list.SelectedIndices.Count;
        int selIdx = selected == 1 ? _list.SelectedIndices[0] : -1;
        bool selIsAdd = _list.IsAddRow(selIdx);

        _btnRun.Text = _running ? "停止" : "测速并重排";
        _btnRun.Enabled = _running || realCount > 0;
        _btnUp.Enabled = !_running && selected == 1 && !selIsAdd && selIdx > 0;
        _btnDown.Enabled = !_running && selected == 1 && !selIsAdd && selIdx < realCount - 1;
        _btnSetFirst.Enabled = !_running && selected == 1 && !selIsAdd;
    }

    // ---------------- 生命周期 ----------------

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // 测速中关窗：先取消并等测试进程退出，避免留下后台 cntlm
        if (_running && !_closeAfterRun)
        {
            e.Cancel = true;
            _closeAfterRun = true;
            CancelAsync();
            _ = CloseWhenIdleAsync();
            return;
        }
        base.OnFormClosing(e);
    }

    private async Task CloseWhenIdleAsync()
    {
        var run = _runTask;
        if (run != null)
        {
            try { await run; } catch { }
        }
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _animTimer.Stop();
            _animTimer.Dispose();
            _tip.Dispose();
            try { _cts?.Cancel(); } catch { }
        }
        base.Dispose(disposing);
    }
}
