using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using CntlmGUI.Models;

namespace CntlmGUI;

/// <summary>
/// 代理测速结果列表：列定义、行内容、跑马灯与“双击添加”占位行都在这里，
/// 表单只负责按钮、菜单和就地编辑事件。
/// </summary>
public sealed class ProxyResultList : ListView
{
    public const string AddRowMarker = "\u2605ADD\u2605";
    public const string AddRowHint = "双击这里可添加代理  <IP:Port>";

    private static readonly Font AddRowFont = new(AppTheme.Ui, FontStyle.Italic);

    public ProxyResultList()
    {
        Dock = DockStyle.Fill;
        FullRowSelect = true;
        View = View.Details;
        GridLines = true;
        HideSelection = false;
        BorderStyle = BorderStyle.FixedSingle;
        LabelEdit = true;

        Columns.Add("代理", 190);
        Columns.Add("TCP 连通", 70);
        Columns.Add("首字节(ms)", 90);
        Columns.Add("HTTP 状态", 75);
        Columns.Add("下载速度", 110);
        Columns.Add("说明", 260);

        // 末列锁死、自动贴满剩余宽度；前列可自由拖动
        ColumnWidthChanging += (_, e) =>
        {
            if (e.ColumnIndex == Columns.Count - 1) e.Cancel = true;
        };
        ColumnWidthChanged += (_, _) => FitLastColumn();
        Resize += (_, _) => FitLastColumn();
    }

    /// <summary>按给定顺序重建列表；有上次结果的代理直接回显，其余显示“待测”。</summary>
    public void ShowCandidates(IEnumerable<string> lines, IReadOnlyDictionary<string, ProxyBench> results)
    {
        BeginUpdate();
        try
        {
            Items.Clear();
            foreach (var line in lines)
            {
                results.TryGetValue(line, out var r);
                Items.Add(NewRow(line, r));
            }
            AppendAddRow();
        }
        finally
        {
            EndUpdate();
        }
        FitLastColumn();
    }

    /// <summary>测速开始：所有代理回到“待测”，等待逐条回填。</summary>
    public void ShowPending(IEnumerable<string> lines)
    {
        BeginUpdate();
        try
        {
            Items.Clear();
            foreach (var line in lines) Items.Add(NewRow(line, null));
            AppendAddRow();
        }
        finally
        {
            EndUpdate();
        }
        FitLastColumn();
    }

    /// <summary>把一条测速结果写进对应行。</summary>
    public void UpdateRow(ProxyBench b)
    {
        foreach (ListViewItem item in Items)
        {
            if (item.Tag as string != b.Line) continue;
            var cells = item.SubItems;
            cells[1].Text = b.TcpText;
            cells[2].Text = b.TcpOk && b.RelayOk ? b.RelayText : b.TcpOk ? "失败" : "—";
            cells[3].Text = b.HttpStatus > 0 ? b.HttpStatus.ToString() : "—";
            cells[4].Text = b.SpeedTested ? b.SpeedText : "—";
            cells[5].Text = b.Summary;
            item.ToolTipText = b.Summary;
            break;
        }
    }

    /// <summary>测速期间，未完成的行在“说明”列显示循环的“测速中...”。</summary>
    public void AnimatePending(ISet<string> pending, int phase)
    {
        if (pending.Count == 0) return;
        var dots = new string('.', phase);
        BeginUpdate();
        try
        {
            foreach (ListViewItem item in Items)
            {
                if (item.Tag is string line && pending.Contains(line))
                    item.SubItems[5].Text = "测速中" + dots;
            }
        }
        finally
        {
            EndUpdate();
        }
    }

    public bool IsAddRow(int index) =>
        index >= 0 && index < Items.Count && Items[index].Tag is string s && s == AddRowMarker;

    public void AppendAddRow()
    {
        var item = new ListViewItem(AddRowHint) { Tag = AddRowMarker, ForeColor = AppTheme.Muted, Font = AddRowFont };
        for (int i = 0; i < 5; i++) item.SubItems.Add("");
        Items.Add(item);
    }

    /// <summary>把某行恢复成“双击添加”占位提示（编辑被取消/清空时调用）。</summary>
    public void RestoreAddHint(int row)
    {
        if (row < 0 || row >= Items.Count) return;
        var item = Items[row];
        item.Text = AddRowHint;
        item.Tag = AddRowMarker;
        item.ForeColor = AppTheme.Muted;
        item.Font = AddRowFont;
        for (int i = 1; i < item.SubItems.Count; i++) item.SubItems[i].Text = "";
    }

    /// <summary>末列始终填满剩余宽度；只有真的出现垂直滚动条时才为其预留空间。</summary>
    public void FitLastColumn()
    {
        if (Columns.Count == 0) return;
        int total = ClientSize.Width - (HasVerticalScrollbar() ? SystemInformation.VerticalScrollBarWidth : 0);
        int used = 0;
        for (int i = 0; i < Columns.Count - 1; i++) used += Columns[i].Width;
        Columns[^1].Width = Math.Max(40, total - used);
    }

    private bool HasVerticalScrollbar()
    {
        if (Items.Count == 0) return false;
        try
        {
            var first = GetItemRect(0, ItemBoundsPortion.Entire);
            if (first.IsEmpty || first.Height <= 0) return false;
            return (long)Items.Count * first.Height >= ClientSize.Height;
        }
        catch
        {
            return false;
        }
    }

    private static ListViewItem NewRow(string line, ProxyBench? r)
    {
        var item = new ListViewItem(line) { Tag = line };
        if (r == null)
        {
            for (int i = 0; i < 5; i++) item.SubItems.Add("");
            item.SubItems[1].Text = "待测";
            return item;
        }

        item.SubItems.Add(r.TcpText);
        item.SubItems.Add(r.TcpOk && r.RelayOk ? r.RelayText : r.TcpOk ? "失败" : "—");
        item.SubItems.Add(r.HttpStatus > 0 ? r.HttpStatus.ToString() : "—");
        item.SubItems.Add(r.SpeedTested ? r.SpeedText : "—");
        item.SubItems.Add(r.Summary);
        item.ToolTipText = r.Summary;
        return item;
    }
}
