using System.Drawing;
using System.Runtime.InteropServices;
using CntlmGUI.Models;

namespace CntlmGUI;

/// <summary>
/// 代理列表控件：就地编辑、排序、占位行与测速结果展示。
/// </summary>
public sealed class ProxyResultList : UserControl
{
    private const int LvmEditLabel = 0x1000 + 118;
    private const int GwlStyle = -16;
    private const int WsVscroll = 0x00200000;
    private static readonly object PlaceholderTag = new();

    private readonly GuiSettings _settings;
    private readonly ListView _list;
    private readonly Font _placeholderFont;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _menuEdit;
    private readonly ToolStripMenuItem _menuDelete;
    private readonly ToolStripMenuItem _menuUp;
    private readonly ToolStripMenuItem _menuDown;
    private readonly ToolStripMenuItem _menuTop;
    private readonly List<string> _proxies = new();

    public ProxyResultList(GuiSettings settings)
    {
        _settings = settings;

        _placeholderFont = new Font(AppTheme.Ui.FontFamily, AppTheme.Ui.Size, FontStyle.Italic);

        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            LabelEdit = true,
            HideSelection = false,
            MultiSelect = false,
            BorderStyle = BorderStyle.FixedSingle,
            Font = AppTheme.Ui,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            TabIndex = 0,
        };
        _list.Columns.Add("代理", 190);
        _list.Columns.Add("TCP 连通", 70);
        _list.Columns.Add("首字节(ms)", 90);
        _list.Columns.Add("HTTP 状态", 75);
        _list.Columns.Add("下载速度", 110);
        _list.Columns.Add("说明", 260);
        Controls.Add(_list);

        _menu = new ContextMenuStrip { Font = AppTheme.Ui };
        var add = new ToolStripMenuItem("添加代理…");
        add.Click += (_, _) => AddProxiesInteractive();
        _menuEdit = new ToolStripMenuItem("编辑选中");
        _menuEdit.Click += (_, _) => BeginEditSelected();
        _menuDelete = new ToolStripMenuItem("删除选中");
        _menuDelete.Click += (_, _) => DeleteSelected();
        _menuUp = new ToolStripMenuItem("上移");
        _menuUp.Click += (_, _) => MoveUp();
        _menuDown = new ToolStripMenuItem("下移");
        _menuDown.Click += (_, _) => MoveDown();
        _menuTop = new ToolStripMenuItem("设为第一顺位");
        _menuTop.Click += (_, _) => MoveToTop();

        _menu.Items.Add(add);
        _menu.Items.Add(_menuEdit);
        _menu.Items.Add(_menuDelete);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_menuUp);
        _menu.Items.Add(_menuDown);
        _menu.Items.Add(_menuTop);
        _menu.Opening += (_, _) => UpdateMenuState();
        _list.ContextMenuStrip = _menu;

        _list.AfterLabelEdit += OnAfterLabelEdit;
        _list.DoubleClick += OnDoubleClick;
        _list.MouseDown += OnMouseDown;
        _list.KeyDown += OnKeyDown;
        _list.SelectedIndexChanged += (_, _) => SelectionChanged?.Invoke(this, EventArgs.Empty);
        _list.Resize += (_, _) => AdjustColumns();

        foreach (var line in TextLines.Split(settings.ProxyList)) _proxies.Add(line);
        Rebuild();
    }

    public event EventHandler? SelectionChanged;
    public event EventHandler? OrderChanged;

    public IReadOnlyList<string> Proxies => _proxies;

    public string? SelectedLine
    {
        get
        {
            if (_list.SelectedItems.Count == 0) return null;
            return _list.SelectedItems[0].Tag as string;
        }
    }

    public bool HasSelection => SelectedLine != null;
    public bool CanMoveUp => SelectedIndex > 0;
    public bool CanMoveDown => SelectedIndex >= 0 && SelectedIndex < _proxies.Count - 1;
    public bool CanMoveTop => SelectedIndex > 0;

    private int SelectedIndex
    {
        get
        {
            if (_list.SelectedIndices.Count == 0) return -1;
            var index = _list.SelectedIndices[0];
            return index >= 0 && index < _proxies.Count ? index : -1;
        }
    }

    public void SetProxies(IEnumerable<string> lines, bool persist = true)
    {
        _proxies.Clear();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (_proxies.Any(item => item.Equals(line, StringComparison.OrdinalIgnoreCase))) continue;
            _proxies.Add(line);
        }

        Rebuild();
        if (persist) Persist();
    }

    public void ShowResult(ProxyBench bench)
    {
        var item = FindItem(bench.Line);
        if (item == null) return;
        FillRow(item, bench);
    }

    public void SetDetail(string line, string detail)
    {
        var item = FindItem(line);
        if (item == null) return;
        if (item.SubItems.Count > 5) item.SubItems[5].Text = detail;
    }

    public void BeginEditSelected()
    {
        var index = SelectedIndex;
        if (index < 0) return;
        BeginEdit(index);
    }

    public void AddProxiesInteractive()
    {
        using var dialog = new Form
        {
            Text = "添加代理",
            ClientSize = new Size(440, 280),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            Font = AppTheme.Ui,
            BackColor = Color.White,
        };

        var box = new TextBox
        {
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Font = AppTheme.Mono,
            Location = new Point(16, 16),
            Size = new Size(408, 178),
        };
        var hint = new Label
        {
            Text = "每行一个，格式 IP:Port（可带 user:pass@ 前缀），重复行会被跳过。",
            Font = AppTheme.UiSmall,
            ForeColor = AppTheme.Muted,
            Location = new Point(16, 200),
            Size = new Size(408, 20),
        };
        var ok = AppTheme.CreateButton("添加", true);
        ok.Location = new Point(264, 230);
        ok.DialogResult = DialogResult.OK;
        var cancel = AppTheme.CreateButton("取消");
        cancel.Location = new Point(360, 230);
        cancel.DialogResult = DialogResult.Cancel;

        dialog.Controls.Add(box);
        dialog.Controls.Add(hint);
        dialog.Controls.Add(ok);
        dialog.Controls.Add(cancel);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;

        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;

        var invalid = new List<string>();
        var added = 0;
        foreach (var raw in TextLines.Split(box.Text))
        {
            if (!IsValidProxy(raw))
            {
                invalid.Add(raw);
                continue;
            }

            if (_proxies.Any(item => item.Equals(raw, StringComparison.OrdinalIgnoreCase))) continue;
            _proxies.Add(raw);
            added++;
        }

        if (added > 0)
        {
            Rebuild();
            Persist();
        }

        if (invalid.Count > 0)
        {
            MessageBox.Show(FindForm(), "以下行格式无效，已跳过：\r\n" + string.Join("\r\n", invalid),
                "添加代理", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    public void DeleteSelected()
    {
        var index = SelectedIndex;
        if (index < 0) return;

        _proxies.RemoveAt(index);
        Rebuild();
        Persist();
    }

    public void MoveUp()
    {
        var index = SelectedIndex;
        if (index <= 0) return;
        Swap(index, index - 1);
    }

    public void MoveDown()
    {
        var index = SelectedIndex;
        if (index < 0 || index >= _proxies.Count - 1) return;
        Swap(index, index + 1);
    }

    public void MoveToTop()
    {
        var index = SelectedIndex;
        if (index <= 0) return;
        var line = _proxies[index];
        _proxies.RemoveAt(index);
        _proxies.Insert(0, line);
        Rebuild();
        Persist();
    }

    private void Swap(int a, int b)
    {
        var line = _proxies[a];
        _proxies[a] = _proxies[b];
        _proxies[b] = line;
        Rebuild();
        Persist();
    }

    private void Persist()
    {
        _settings.ProxyList = string.Join("\r\n", _proxies);
        _settings.LastBench = _settings.LastBench
            .Where(item => _proxies.Any(line => line.Equals(item.Line, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        try
        {
            _settings.Save();
        }
        catch
        {
            // 保存失败不阻塞界面
        }

        OrderChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Rebuild()
    {
        var selected = SelectedLine;

        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            foreach (var line in _proxies)
            {
                var item = new ListViewItem(line);
                for (var i = 1; i < 6; i++) item.SubItems.Add("");
                item.Tag = line;

                var bench = _settings.LastBench.FirstOrDefault(
                    entry => entry.Line.Equals(line, StringComparison.OrdinalIgnoreCase));
                if (bench != null) FillRow(item, bench);

                _list.Items.Add(item);
            }

            var placeholder = new ListViewItem("双击这里可添加代理  IP:Port");
            for (var i = 1; i < 6; i++) placeholder.SubItems.Add("");
            placeholder.Tag = PlaceholderTag;
            placeholder.ForeColor = Color.Gray;
            placeholder.Font = _placeholderFont;
            _list.Items.Add(placeholder);
        }
        finally
        {
            _list.EndUpdate();
        }

        if (selected != null)
        {
            var item = FindItem(selected);
            if (item != null)
            {
                item.Selected = true;
                item.Focused = true;
                _list.EnsureVisible(item.Index);
            }
        }

        AdjustColumns();
        if (IsHandleCreated) BeginInvoke(new Action(AdjustColumns));
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        AdjustColumns();
    }

    private static void FillRow(ListViewItem item, ProxyBench bench)
    {
        item.SubItems[1].Text = bench.TcpText;
        item.SubItems[2].Text = bench.RelayText;
        item.SubItems[3].Text = bench.HttpText;
        item.SubItems[4].Text = bench.SpeedText;
        item.SubItems[5].Text = bench.Summary;
    }

    private ListViewItem? FindItem(string line)
    {
        foreach (ListViewItem item in _list.Items)
        {
            if (item.Tag is string text && text.Equals(line, StringComparison.OrdinalIgnoreCase))
                return item;
        }
        return null;
    }

    private void BeginEdit(int index)
    {
        if (index < 0 || index >= _list.Items.Count) return;
        _list.EnsureVisible(index);
        _list.Focus();
        SendMessage(_list.Handle, LvmEditLabel, (IntPtr)index, IntPtr.Zero);
    }

    private void OnAfterLabelEdit(object? sender, LabelEditEventArgs e)
    {
        var index = e.Item;
        if (index < 0 || index >= _list.Items.Count) return;

        var item = _list.Items[index];
        if (item.Tag is not string oldLine)
        {
            e.CancelEdit = true;
            return;
        }

        if (e.Label == null) return;

        var text = e.Label.Trim();
        if (text.Length == 0)
        {
            e.CancelEdit = true;
            return;
        }

        if (!IsValidProxy(text))
        {
            e.CancelEdit = true;
            MessageBox.Show(FindForm(), "代理格式无效，请使用 IP:Port。", "编辑代理",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (_proxies.Any(line => !line.Equals(oldLine, StringComparison.OrdinalIgnoreCase)
                                 && line.Equals(text, StringComparison.OrdinalIgnoreCase)))
        {
            e.CancelEdit = true;
            MessageBox.Show(FindForm(), "该代理已存在。", "编辑代理",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (text.Equals(oldLine, StringComparison.Ordinal)) return;

        var proxyIndex = _proxies.FindIndex(line => line.Equals(oldLine, StringComparison.OrdinalIgnoreCase));
        if (proxyIndex < 0) return;

        _proxies[proxyIndex] = text;

        var bench = _settings.LastBench.FirstOrDefault(
            entry => entry.Line.Equals(oldLine, StringComparison.OrdinalIgnoreCase));
        if (bench != null) bench.Line = text;

        _settings.ProxyList = string.Join("\r\n", _proxies);
        try
        {
            _settings.Save();
        }
        catch
        {
            // 忽略
        }

        OrderChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnDoubleClick(object? sender, EventArgs e)
    {
        var info = _list.HitTest(_list.PointToClient(Cursor.Position));
        if (info.Item == null) return;

        _list.SelectedItems.Clear();
        info.Item.Selected = true;
        info.Item.Focused = true;

        if (info.Item.Tag == PlaceholderTag)
        {
            AddProxiesInteractive();
            return;
        }

        var index = info.Item.Index;
        BeginInvoke(new Action(() => BeginEdit(index)));
    }

    private void OnMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right) return;
        var info = _list.HitTest(e.Location);
        if (info.Item == null) return;
        _list.SelectedItems.Clear();
        info.Item.Selected = true;
        info.Item.Focused = true;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F2)
        {
            BeginEditSelected();
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Delete)
        {
            DeleteSelected();
            e.Handled = true;
        }
    }

    private void UpdateMenuState()
    {
        var has = HasSelection;
        _menuEdit.Enabled = has;
        _menuDelete.Enabled = has;
        _menuUp.Enabled = CanMoveUp;
        _menuDown.Enabled = CanMoveDown;
        _menuTop.Enabled = CanMoveTop;
    }

    private void AdjustColumns()
    {
        if (_list.Columns.Count < 6) return;

        var fixedWidth = 190 + 70 + 90 + 75 + 110;
        var scroll = VScrollVisible() ? SystemInformation.VerticalScrollBarWidth : 0;
        var available = _list.ClientSize.Width - scroll - 2;
        _list.Columns[5].Width = Math.Max(120, available - fixedWidth);
    }

    private bool VScrollVisible()
    {
        if (!_list.IsHandleCreated) return false;
        var style = GetWindowLongPtr(_list.Handle, GwlStyle).ToInt64();
        return (style & WsVscroll) != 0;
    }

    private static bool IsValidProxy(string line)
    {
        var text = line.Trim();
        if (text.Length == 0) return false;

        var at = text.LastIndexOf('@');
        if (at >= 0) text = text[(at + 1)..];

        var idx = text.LastIndexOf(':');
        if (idx <= 0 || idx == text.Length - 1) return false;

        var host = text[..idx].Trim().Trim('[', ']');
        if (host.Length == 0) return false;

        return int.TryParse(text[(idx + 1)..].Trim(), out var port) && port is >= 1 and <= 65535;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _placeholderFont.Dispose();
            _menu.Dispose();
        }
        base.Dispose(disposing);
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
}
