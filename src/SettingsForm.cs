using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using CntlmGUI.Models;
using CntlmGUI.Services;

namespace CntlmGUI;

/// <summary>
/// 设置对话框，按“一级代理（本地互联）/ 二级代理（对外互联）/ 运行”三页组织：
/// 一级页只放监听、访问控制、本地路由与本地服务；
/// 二级页放上游选择与上游认证；
/// 运行页放 cntlm.exe、配置输出、进程/服务模式与界面行为。
/// </summary>
public class SettingsForm : Form
{
    private const int LblW = AppTheme.LabelCol;

    private readonly GuiSettings _s;

    // ---- 一级代理（本地互联）----
    private ComboBox _listenPreset = null!;
    private TextBox _listen = null!, _noProxy = null!;
    private TextBox _allow = null!, _deny = null!;
    private TextBox _socks5 = null!, _socks5User = null!, _tunnel = null!;
    private ToggleSwitch _gateway = null!;

    // ---- 二级代理（对外互联）----
    private ToggleSwitch _useProxy = null!;
    private Label _proxyWarn = null!;
    private TableLayoutPanel _upstreamBody = null!;
    private TextBox _pac = null!, _headers = null!, _isaSize = null!, _isaAgents = null!;
    private ToggleSwitch _isaEnabled = null!;
    private TextBox _username = null!, _domain = null!, _workstation = null!, _password = null!;
    private TextBox _passLm = null!, _passNt = null!, _passV2 = null!;
    private ComboBox _authCombo = null!, _flagsCombo = null!, _sspiCombo = null!;
    private ToggleSwitch _sspiEnabled = null!;
    private Label _authStatus = null!;

    // ---- 运行 ----
    private TextBox _cntlmPath = null!, _outCfg = null!, _serviceName = null!;
    private ComboBox _modeCombo = null!;
    private Label _serviceCfgLbl = null!, _runStatus = null!;
    private ToggleSwitch _autoStart = null!, _closeToTray = null!, _traceEnabled = null!;

    public SettingsForm(GuiSettings source)
    {
        _s = source;
        Text = "设置";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(620, 780);
        MinimumSize = new Size(620, 720);
        MaximumSize = new Size(620, 980);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        Font = AppTheme.Ui;
        BackColor = Color.White;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, BackColor = Color.White, Padding = new Padding(18, 14, 18, 12) };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(new TabPage("一级代理") { BackColor = Color.White, Controls = { LocalPage() } });
        tabs.TabPages.Add(new TabPage("二级代理") { BackColor = Color.White, Controls = { UpstreamPage() } });
        tabs.TabPages.Add(new TabPage("运行") { BackColor = Color.White, Controls = { RunPage() } });
        root.Controls.Add(tabs, 0, 0);

        var btns = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Height = 56, Padding = new Padding(0, 12, 0, 10), BackColor = Color.White };
        btns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        btns.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        btns.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var save = new Button { Text = "保存", Width = 116, Height = 34 };
        AppTheme.StyleButton(save, primary: true);
        save.Click += (_, _) => Save();
        var cancel = new Button { Text = "取消", Width = 116, Height = 34, DialogResult = DialogResult.Cancel };
        AppTheme.StyleButton(cancel);
        btns.Controls.Add(cancel, 2, 0);
        btns.Controls.Add(save, 1, 0);
        root.Controls.Add(btns, 0, 1);

        AcceptButton = save;
        CancelButton = cancel;
        Controls.Add(root);

        LoadValues();
    }

    // ==================== 布局基元 ====================

    private static Panel NewPage(out TableLayoutPanel grid)
    {
        var sc = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.White, Padding = new Padding(14, 6, 14, 24) };
        grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 0, Margin = new Padding(0), BackColor = Color.White };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LblW));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        sc.Controls.Add(grid);
        return sc;
    }

    private static void Section(TableLayoutPanel g, string text)
    {
        int r = g.RowCount++;
        g.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var l = new Label { Text = text, AutoSize = true, Font = AppTheme.UiBold, ForeColor = AppTheme.Text, Margin = new Padding(0, 18, 0, 6) };
        g.Controls.Add(l, 0, r);
        g.SetColumnSpan(l, 2);
    }

    private static void Field(TableLayoutPanel g, string label, Control c)
    {
        int r = g.RowCount++;
        g.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        if (!string.IsNullOrEmpty(label))
            g.Controls.Add(FieldLabel(label), 0, r);
        c.Margin = new Padding(0, 2, 0, 2);
        g.Controls.Add(c, 1, r);
    }

    private static Label FieldLabel(string text) => new()
    {
        Text = text,
        AutoSize = false,
        Width = LblW + 2,
        Height = 30,
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(0, 0, 8, 0),
        Margin = new Padding(0, 2, 0, 2),
        ForeColor = AppTheme.Text,
    };

    private static void FullWidth(TableLayoutPanel g, Control c)
    {
        int r = g.RowCount++;
        g.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        c.Margin = new Padding(0, 4, 0, 4);
        g.Controls.Add(c, 0, r);
        g.SetColumnSpan(c, 2);
    }

    private static Label Grey(string text, int maxWidth = 480) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = AppTheme.Muted,
        MaximumSize = new Size(maxWidth, 0),
        Margin = new Padding(0, 4, 0, 2),
    };

    private static void Hint(TableLayoutPanel g, string text)
    {
        int r = g.RowCount++;
        g.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var l = Grey(text, 360);
        l.Margin = new Padding(0, 2, 0, 2);
        g.Controls.Add(l, 1, r);
    }

    // ==================== 控件工厂 ====================

    private static TextBox NewBox(bool mono = false)
    {
        var t = new TextBox { Width = 340, Height = 28 };
        if (mono) t.Font = AppTheme.Mono;
        return t;
    }

    private static TextBox NewMulti(int lines) =>
        new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Width = 340, Height = Math.Max(24, lines * 22 + 6), Font = AppTheme.Mono };

    private static ComboBox NewCombo(params string[] items)
    {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 340, Height = 26 };
        c.Items.AddRange(items);
        if (items.Length > 0) c.SelectedIndex = 0;
        return c;
    }

    private static ComboBox NewComboEditable(params string[] presets)
    {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Width = 340, Height = 26 };
        c.Items.AddRange(presets);
        return c;
    }

    private static Button Btn(string text, EventHandler onClick)
    {
        var b = new Button { Text = text, AutoSize = true, Height = 30, Margin = new Padding(2) };
        AppTheme.StyleButton(b);
        b.Click += onClick;
        return b;
    }

    private static void SetComboText(ComboBox c, string value)
    {
        var v = value?.Trim() ?? "";
        if (c.DropDownStyle == ComboBoxStyle.DropDown)
        {
            c.Text = v;
            return;
        }
        int i = c.Items.IndexOf(v);
        c.SelectedIndex = i >= 0 ? i : 0;
    }

    private static bool HasText(string? s) => !string.IsNullOrWhiteSpace(s);

    // ==================== 一级代理（本地互联）====================

    private Control LocalPage()
    {
        var page = NewPage(out var g);

        Section(g, "监听");
        _listenPreset = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 340, Height = 26 };
        _listenPreset.Items.AddRange(Detect.GetLocalIpv4().ToArray());
        _listenPreset.SelectedIndexChanged += (_, _) => ApplyListenPreset();
        Field(g, "监听地址", _listenPreset);
        _listen = NewBox();
        Field(g, "监听端口", _listen);
        _gateway = new ToggleSwitch { Text = "允许局域网访问（Gateway）", Width = 340 };
        Field(g, "局域网访问", _gateway);
        Hint(g, "默认仅本机（127.0.0.1）。监听 0.0.0.0 或勾选 Gateway 后局域网可访问，请同时配置访问控制。");

        Section(g, "访问控制");
        _allow = NewMulti(2);
        _deny = NewMulti(2);
        Field(g, "白名单", _allow);
        Field(g, "黑名单", _deny);
        Hint(g, "每行一条 IP、网段或主机名；为空表示不限制。开放局域网时务必配置。");

        Section(g, "本地路由");
        _noProxy = NewMulti(4);
        Field(g, "免代地址", _noProxy);
        Hint(g, "这些地址由本机直接处理，不经过二级代理；支持通配符与 CIDR，每行一个或逗号分隔。");

        Section(g, "本地服务");
        _socks5 = NewBox();
        _socks5User = NewBox();
        Field(g, "SOCKS5 监听", _socks5);
        Field(g, "SOCKS5 认证", _socks5User);
        Hint(g, "在本机再开一个 SOCKS5 监听端口（如 1080 或 127.0.0.1:1080）；认证格式 user:password，可留空。");
        _tunnel = NewMulti(2);
        Field(g, "隧道", _tunnel);
        Hint(g, "本地端口转发，每行 [saddr:]lport:rhost:rport，如 11443:remote.com:443。");

        return page;
    }

    // ==================== 二级代理（对外互联）====================

    private Control UpstreamPage()
    {
        var page = NewPage(out var g);

        _useProxy = new ToggleSwitch { Text = "启用对外互联（使用上游代理）", Width = 340 };
        Field(g, "对外互联", _useProxy);
        _proxyWarn = new Label
        {
            Text = "未启用对外互联时，只有一级代理生效。",
            AutoSize = true,
            ForeColor = AppTheme.Muted,
            MaximumSize = new Size(360, 0),
            Margin = new Padding(0, 2, 0, 6),
            Visible = false,
        };
        FullWidth(g, _proxyWarn);

        // 关闭“对外互联”时，下面的内容整体隐藏（只保留上面的开关和提示）
        _upstreamBody = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Margin = new Padding(0), BackColor = Color.White };
        _upstreamBody.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LblW));
        _upstreamBody.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        FullWidth(g, _upstreamBody);
        _upstreamBody.Visible = _useProxy.Checked;

        var editBox = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Margin = new Padding(0) };
        editBox.Controls.Add(Btn("代理列表编辑…", (_, _) => OpenSpeedTest()), 0, 0);
        var editHint = Grey("管理上游代理，测速与排序");
        editHint.Margin = new Padding(2, 4, 0, 0);
        editBox.Controls.Add(editHint, 0, 1);
        Field(_upstreamBody, "", editBox);

        Section(_upstreamBody, "上游选择");
        _pac = NewBox();
        Field(_upstreamBody, "PAC 文件", _pac);
        Field(_upstreamBody, "", Btn("浏览…", (_, _) => PickPac()));
        Hint(_upstreamBody, "让 cntlm 按 PAC 脚本选择上游代理。");
        _headers = NewMulti(2);
        Field(_upstreamBody, "请求头替换", _headers);
        Hint(_upstreamBody, "发往上游的请求头，每行 <name: value>，如 User-Agent: Mozilla。");

        _isaEnabled = new ToggleSwitch { Text = "启用 ISA 扫描器", Width = 340 };
        Field(_upstreamBody, "ISA 扫描", _isaEnabled);
        _isaSize = NewBox(mono: true);
        _isaAgents = NewMulti(2);
        Field(_upstreamBody, "探测包大小 (KB)", _isaSize);
        Field(_upstreamBody, "代理标识", _isaAgents);
        Hint(_upstreamBody, "自动探测内网 Microsoft ISA 服务器；代理标识每行一个（至少一个才会生效）。");

        Section(_upstreamBody, "上游认证");
        _username = NewBox();
        _domain = NewBox();
        _workstation = NewBox();
        Field(_upstreamBody, "用户名", _username);
        Field(_upstreamBody, "域", _domain);
        Field(_upstreamBody, "工作站", _workstation);

        _authCombo = NewCombo("默认(自动)", "NTLM", "NTLMv2", "LM", "NT");
        _flagsCombo = NewComboEditable("默认(自动)", "0x00020000", "0x06820000", "0x06800000", "0x02088207");
        Field(_upstreamBody, "认证类型", _authCombo);
        Field(_upstreamBody, "Flags", _flagsCombo);

        _sspiEnabled = new ToggleSwitch { Text = "使用 Windows 集成认证（SSPI）", Width = 340 };
        _sspiCombo = NewCombo("NTLM", "negotiate");
        Field(_upstreamBody, "SSPI", _sspiEnabled);
        Field(_upstreamBody, "SSPI 类型", _sspiCombo);
        Hint(_upstreamBody, "启用后由 Windows 当前登录用户完成认证，通常不需要再填密码/哈希。");

        _password = NewBox();
        _password.UseSystemPasswordChar = true;
        Field(_upstreamBody, "密码", _password);
        Hint(_upstreamBody, "仅用于计算哈希；若已填 PassLM/PassNT/PassNTLMv2，可省略。");

        _passLm = NewBox(mono: true);
        _passNt = NewBox(mono: true);
        _passV2 = NewBox(mono: true);
        Field(_upstreamBody, "PassLM", _passLm);
        Field(_upstreamBody, "PassNT", _passNt);
        Field(_upstreamBody, "PassNTLMv2", _passV2);

        var ops = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        ops.Controls.Add(Btn("生成哈希", (_, _) => GenerateHash()));
        Field(_upstreamBody, "", ops);
        _authStatus = new Label { Text = "", AutoSize = true, ForeColor = AppTheme.Muted, MaximumSize = new Size(360, 0) };
        FullWidth(_upstreamBody, _authStatus);

        _useProxy.CheckedChanged += (_, _) =>
        {
            bool on = _useProxy.Checked;
            _proxyWarn.Visible = !on;
            _upstreamBody.Visible = on;
        };
        return page;
    }

    // ==================== 运行 ====================

    private Control RunPage()
    {
        var page = NewPage(out var g);

        Section(g, "程序与配置");
        _cntlmPath = NewBox();
        Field(g, "可执行文件", _cntlmPath);
        var exeOps = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        exeOps.Controls.Add(Btn("检测", (_, _) => AutoDetect()));
        exeOps.Controls.Add(Btn("浏览…", (_, _) => PickFile()));
        Field(g, "", exeOps);

        _outCfg = NewBox();
        Field(g, "输出路径", _outCfg);
        var outOps = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        outOps.Controls.Add(Btn("默认", (_, _) => _outCfg.Text = GuiSettings.DefaultOutputConfig));
        outOps.Controls.Add(Btn("浏览…", (_, _) => PickPath()));
        outOps.Controls.Add(Btn("预览…", (_, _) => PreviewConfig()));
        Field(g, "", outOps);

        Section(g, "运行方式");
        _modeCombo = NewCombo("子进程", "Windows 服务");
        Field(g, "运行模式", _modeCombo);
        _serviceName = NewBox();
        Field(g, "服务名", _serviceName);
        _serviceCfgLbl = new Label { Text = "", AutoSize = true, ForeColor = AppTheme.Muted, MaximumSize = new Size(360, 0) };
        FullWidth(g, _serviceCfgLbl);
        Hint(g, "服务模式会写入服务实际读取的 ini（通常需要管理员权限）；未安装服务时请先用 cntlm 安装器创建。");
        _modeCombo.SelectedIndexChanged += (_, _) =>
        {
            bool service = _modeCombo.SelectedIndex == 1;
            _serviceName.Enabled = service;
            UpdateServicePath();
        };
        _serviceName.TextChanged += (_, _) => UpdateServicePath();

        Section(g, "界面行为");
        _autoStart = new ToggleSwitch { Text = "启动程序时自动启动代理", Width = 340 };
        _closeToTray = new ToggleSwitch { Text = "关闭窗口时最小化到托盘", Width = 340 };
        _traceEnabled = new ToggleSwitch { Text = "记录详细日志（-T）", Width = 340 };
        Field(g, "自启动", _autoStart);
        Field(g, "最小化", _closeToTray);
        Field(g, "详细日志", _traceEnabled);
        Hint(g, "详细日志会持续写入文件，适合排查问题时临时开启。");

        Section(g, "配置迁移");
        Field(g, "", Btn("从 cntlm.ini 导入…", (_, _) => ImportConfig()));

        _runStatus = new Label { Text = "", AutoSize = true, ForeColor = AppTheme.Muted, MaximumSize = new Size(520, 0) };
        FullWidth(g, _runStatus);

        return page;
    }

    // ==================== 装载 / 保存 ====================

    private void LoadValues()
    {
        ApplyToControls(_s);

        _cntlmPath.Text = _s.CntlmPath;
        if (string.IsNullOrWhiteSpace(_cntlmPath.Text))
        {
            var found = Detect.FindCntlm();
            if (!string.IsNullOrWhiteSpace(found)) _cntlmPath.Text = found;
        }

        _outCfg.Text = string.IsNullOrWhiteSpace(_s.OutputConfigPath) ? GuiSettings.DefaultOutputConfig : _s.OutputConfigPath;
        _modeCombo.SelectedIndex = _s.UseService ? 1 : 0;
        _serviceName.Text = _s.ServiceName;
        _serviceName.Enabled = _s.UseService;
        _autoStart.Checked = _s.AutoStart;
        _closeToTray.Checked = _s.CloseToTray;
        _traceEnabled.Checked = _s.EnableTrace;
        UpdateServicePath();
    }

    /// <summary>把一份配置映射到各输入控件（装载与导入共用）。</summary>
    private void ApplyToControls(GuiSettings v)
    {
        var (ip, port) = GuiSettings.SplitListen(v.Listen);
        _listen.Text = port;
        SelectListenIp(ip);
        _gateway.Checked = v.Gateway;
        _noProxy.Text = v.NoProxyList;
        _allow.Text = v.AllowList;
        _deny.Text = v.DenyList;
        _socks5.Text = v.Socks5Proxy;
        _socks5User.Text = v.Socks5User;
        _tunnel.Text = v.TunnelList;

        _useProxy.Checked = v.UseProxy;
        _proxyWarn.Visible = !v.UseProxy;
        _upstreamBody.Visible = v.UseProxy;
        _pac.Text = v.PacFile;
        _headers.Text = v.HeaderList;
        _isaEnabled.Checked = v.UseIsaScanner;
        _isaSize.Text = v.IsaScannerSize.ToString();
        _isaAgents.Text = v.IsaScannerAgentList;

        _username.Text = v.Username;
        _domain.Text = v.Domain;
        _workstation.Text = v.Workstation;
        SetComboText(_authCombo, v.Auth);
        SetComboText(_flagsCombo, v.Flags);
        _sspiEnabled.Checked = v.UseSspi;
        SetComboText(_sspiCombo, v.SspiType);
        _password.Text = v.Password;
        _passLm.Text = v.PassLM;
        _passNt.Text = v.PassNT;
        _passV2.Text = v.PassNTLMv2;
    }

    private void Save()
    {
        if (!int.TryParse(_listen.Text.Trim(), out var port) || port is <= 0 or > 65535)
        {
            MessageBox.Show(this, "监听端口必须是 1-65535 之间的数字。", "设置",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        ApplyUiTo(_s);
        _s.Save();
        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>把界面上的值写入给定配置对象（保存与预览共用）。</summary>
    private void ApplyUiTo(GuiSettings s)
    {
        s.Listen = ListenValue();
        s.Gateway = _gateway.Checked;
        s.NoProxyList = _noProxy.Text;
        s.AllowList = _allow.Text;
        s.DenyList = _deny.Text;
        s.UseAllow = HasText(_allow.Text) || HasText(_deny.Text);
        s.Socks5Proxy = _socks5.Text.Trim();
        s.Socks5User = _socks5User.Text.Trim();
        s.UseSocks5 = HasText(s.Socks5Proxy);
        s.TunnelList = _tunnel.Text;
        s.UseTunnel = HasText(s.TunnelList);

        s.UseProxy = _useProxy.Checked;
        s.PacFile = _pac.Text.Trim();
        s.UsePac = HasText(s.PacFile);
        s.HeaderList = _headers.Text;
        s.UseHeader = HasText(s.HeaderList);
        s.IsaScannerSize = int.TryParse(_isaSize.Text.Trim(), out var size) ? Math.Max(0, size) : 0;
        s.IsaScannerAgentList = _isaAgents.Text;
        s.UseIsaScanner = _isaEnabled.Checked;

        s.Username = _username.Text.Trim();
        s.Domain = _domain.Text.Trim();
        s.Workstation = _workstation.Text.Trim();
        s.Auth = DefaultIfAutomatic(_authCombo.Text);
        s.Flags = DefaultIfAutomatic(_flagsCombo.Text);
        s.UseSspi = _sspiEnabled.Checked;
        s.SspiType = _sspiCombo.Text.Trim();
        s.Password = _password.Text;
        s.PassLM = _passLm.Text.Trim();
        s.PassNT = _passNt.Text.Trim();
        s.PassNTLMv2 = _passV2.Text.Trim();

        s.CntlmPath = _cntlmPath.Text.Trim();
        s.OutputConfigPath = _outCfg.Text.Trim();
        s.UseService = _modeCombo.SelectedIndex == 1;
        s.ServiceName = _serviceName.Text.Trim();
        s.AutoStart = _autoStart.Checked;
        s.CloseToTray = _closeToTray.Checked;
        s.EnableTrace = _traceEnabled.Checked;
    }

    private string ListenValue()
    {
        var portText = _listen.Text.Trim();
        if (!int.TryParse(portText, out var port) || port is <= 0 or > 65535)
            port = GuiSettings.ParseListen(_s.Listen).port;
        return $"{ListenIp()}:{port}";
    }

    private static string DefaultIfAutomatic(string text) =>
        text.Trim() == "默认(自动)" ? "" : text.Trim();

    // ==================== 动作 ====================

    private void ApplyListenPreset()
    {
        if (string.IsNullOrWhiteSpace(_listen.Text)) _listen.Text = "8080";
    }

    private string ListenIp() => _listenPreset.SelectedItem?.ToString() ?? "127.0.0.1";

    private void SelectListenIp(string ip)
    {
        int idx = _listenPreset.Items.IndexOf(ip);
        if (idx < 0) idx = _listenPreset.Items.IndexOf("127.0.0.1"); // 默认仅本机
        _listenPreset.SelectedIndex = idx >= 0 ? idx : 0;
    }

    private void UpdateServicePath()
    {
        if (_serviceCfgLbl == null) return;
        if (_modeCombo.SelectedIndex != 1)
        {
            _serviceCfgLbl.Text = "服务实际配置：仅服务模式使用";
            return;
        }
        var path = CntlmService.ConfigPath(_serviceName.Text.Trim());
        _serviceCfgLbl.Text = path == null
            ? "服务实际配置：未找到该服务（请先用 cntlm 安装器创建）"
            : "服务实际配置：" + path;
    }

    private void PickPac()
    {
        using var d = new OpenFileDialog { Filter = "PAC|*.pac;*.txt|所有|*.*" };
        if (d.ShowDialog() == DialogResult.OK) _pac.Text = d.FileName;
    }

    private void PickFile()
    {
        using var d = new OpenFileDialog { Filter = "cntlm.exe|cntlm.exe" };
        if (d.ShowDialog() == DialogResult.OK) _cntlmPath.Text = d.FileName;
    }

    private void PickPath()
    {
        using var d = new SaveFileDialog { Filter = "ini|*.ini", FileName = "cntlm.ini" };
        if (d.ShowDialog() == DialogResult.OK) _outCfg.Text = d.FileName;
    }

    private void AutoDetect()
    {
        var found = Detect.FindCntlm();
        _cntlmPath.Text = found ?? "";
        _runStatus.Text = found is null ? "未自动找到 cntlm，请手动指定路径。" : "已找到: " + found;
    }

    private void PreviewConfig()
    {
        var preview = new GuiSettings();
        ApplyUiTo(preview);
        preview.ProxyList = _s.ProxyList; // 代理列表在单独窗口维护
        preview.Password = _password.Text;

        using var dlg = new Form
        {
            Text = "生成的 cntlm 配置",
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(760, 560),
            Font = AppTheme.Ui,
            BackColor = Color.White,
            MinimizeBox = false,
            MaximizeBox = false,
            FormBorderStyle = FormBorderStyle.SizableToolWindow,
        };
        var box = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = AppTheme.Mono,
            Text = preview.RenderConfig(),
        };
        dlg.Controls.Add(box);
        dlg.ShowDialog(this);
    }

    private void ImportConfig()
    {
        using var d = new OpenFileDialog { Filter = "cntlm 配置|cntlm.ini;*.ini" };
        if (d.ShowDialog() != DialogResult.OK) return;
        ApplyToControls(CntlmConfigGenerator.Import(d.FileName));
        _runStatus.Text = "已导入配置（尚未保存）。";
    }

    private async void GenerateHash()
    {
        var exe = _s.EffectiveBaseCntlmPath();
        if (string.IsNullOrWhiteSpace(_username.Text) || _password.Text.Length == 0)
        {
            _authStatus.Text = "请先填写用户名与密码。";
            return;
        }
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
        {
            _authStatus.Text = "未找到 cntlm.exe，无法生成哈希（可在“运行”页指定）。";
            return;
        }

        // 密码不放到命令行（进程列表可见），改为从 stdin 交给 cntlm -H
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exe),
        };
        psi.ArgumentList.Add("-H");
        psi.ArgumentList.Add("-u");
        psi.ArgumentList.Add(_username.Text.Trim());
        if (!string.IsNullOrWhiteSpace(_domain.Text.Trim()))
        {
            psi.ArgumentList.Add("-d");
            psi.ArgumentList.Add(_domain.Text.Trim());
        }

        try
        {
            using var p = Process.Start(psi);
            if (p == null)
            {
                _authStatus.Text = "无法启动 cntlm 生成哈希。";
                return;
            }

            try
            {
                await p.StandardInput.WriteLineAsync(_password.Text);
                p.StandardInput.Close();
            }
            catch
            {
                // stdin 关闭失败时继续，交给超时逻辑处理
            }

            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await p.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch { }
                _authStatus.Text = "生成哈希超时（30 秒），已终止 cntlm 进程。";
                return;
            }

            var outText = await outTask + "\n" + await errTask;
            var lm = HashOf(outText, "PassLM");
            var nt = HashOf(outText, "PassNT");
            var v2 = HashOf(outText, "PassNTLMv2");
            if (lm == null && nt == null && v2 == null)
            {
                _authStatus.Text = "未能解析哈希输出：" + outText.Trim();
                return;
            }

            if (lm != null) _passLm.Text = lm;
            if (nt != null) _passNt.Text = nt;
            if (v2 != null) _passV2.Text = v2;
            _authStatus.Text = "哈希已生成（未保存）。";
        }
        catch (Exception ex)
        {
            _authStatus.Text = "生成哈希失败: " + ex.Message;
        }
    }

    private static string? HashOf(string text, string key)
    {
        foreach (var line in text.Split('\n'))
        {
            var t = line.Trim();
            if (!t.StartsWith(key, StringComparison.OrdinalIgnoreCase)) continue;
            int i = key.Length;
            while (i < t.Length && char.IsWhiteSpace(t[i])) i++;
            var v = (i < t.Length ? t[i..] : "").Trim();
            int sp = v.IndexOfAny(new[] { ' ', '\t' });
            return sp > 0 ? v[..sp] : v;
        }
        return null;
    }

    private void OpenSpeedTest()
    {
        using var f = new ProxySpeedForm(_s);
        f.ShowDialog(this);
    }
}
