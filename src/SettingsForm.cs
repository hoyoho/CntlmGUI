using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Text.RegularExpressions;
using CntlmGUI.Models;
using CntlmGUI.Services;

namespace CntlmGUI;

public sealed class SettingsForm : Form
{
    private const int FieldWidth = 380;

    private readonly GuiSettings _settings;
    private readonly TabControl _tabs;
    private readonly Panel _bottomPanel;

    // 一级代理
    private readonly ComboBox _listenHost = new();
    private readonly TextBox _listenPort = NewText();
    private readonly ToggleSwitch _gatewayToggle = NewToggle("允许局域网访问（Gateway）");
    private readonly TextBox _allowBox = NewText(multiline: true, height: 70);
    private readonly TextBox _denyBox = NewText(multiline: true, height: 70);
    private readonly TextBox _noProxyBox = NewText(multiline: true, height: 70);
    private readonly TextBox _socks5Box = NewText();
    private readonly TextBox _socks5UserBox = NewText();
    private readonly TextBox _tunnelBox = NewText(multiline: true, height: 70);

    // 二级代理
    private readonly ToggleSwitch _useProxyToggle = NewToggle("启用对外互联（使用上游代理）");
    private readonly TextBox _pacBox = NewText();
    private readonly TextBox _headerBox = NewText(multiline: true, height: 60);
    private readonly ToggleSwitch _isaToggle = NewToggle("启用 ISA 扫描");
    private readonly TextBox _isaSizeBox = NewText();
    private readonly TextBox _isaAgentBox = NewText(multiline: true, height: 60);
    private readonly TextBox _usernameBox = NewText();
    private readonly TextBox _domainBox = NewText();
    private readonly TextBox _workstationBox = NewText();
    private readonly ComboBox _authCombo = NewCombo("默认(自动)", "NTLM", "NTLMv2", "LM", "NT");
    private readonly ComboBox _flagsCombo = new();
    private readonly ToggleSwitch _sspiToggle = NewToggle("启用 SSPI");
    private readonly ComboBox _sspiTypeCombo = NewCombo("NTLM", "negotiate");
    private readonly TextBox _passwordBox = NewText();
    private readonly TextBox _passLMBox = NewMonoText();
    private readonly TextBox _passNTBox = NewMonoText();
    private readonly TextBox _passNTLMv2Box = NewMonoText();
    private readonly Label _hashStatus = new();

    // 运行
    private readonly TextBox _cntlmPathBox = NewText();
    private readonly TextBox _outputPathBox = NewText();
    private readonly ComboBox _modeCombo = NewCombo("子进程", "Windows 服务");
    private readonly TextBox _serviceNameBox = NewText();
    private readonly Label _servicePathLabel = new();
    private readonly ToggleSwitch _autoStartToggle = NewToggle("开机自动启动");
    private readonly ToggleSwitch _closeToTrayToggle = NewToggle("关闭窗口时最小化到托盘");
    private readonly ToggleSwitch _traceToggle = NewToggle("记录详细日志（-T）");
    private readonly Label _importStatus = new();

    private Panel? _tier2Body;
    private Label? _tier2OffHint;

    public SettingsForm(GuiSettings settings)
    {
        _settings = settings;

        Text = "设置";
        Font = AppTheme.Ui;
        BackColor = Color.White;
        ClientSize = new Size(620, 780);
        MinimumSize = new Size(Width, 720);
        MaximumSize = new Size(Width, 980);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;

        _tabs = new TabControl { Font = AppTheme.Ui };
        _tabs.TabPages.Add(BuildTier1Page());
        _tabs.TabPages.Add(BuildTier2Page());
        _tabs.TabPages.Add(BuildRunPage());
        Controls.Add(_tabs);

        _bottomPanel = new Panel { BackColor = Color.White };
        _bottomPanel.Paint += (_, e) =>
        {
            using var pen = new Pen(AppTheme.Line);
            e.Graphics.DrawLine(pen, 0, 0, _bottomPanel.Width, 0);
        };
        Controls.Add(_bottomPanel);

        var cancel = AppTheme.CreateButton("取消");
        cancel.Click += (_, _) =>
        {
            DialogResult = DialogResult.Cancel;
            Close();
        };
        _bottomPanel.Controls.Add(cancel);

        var save = AppTheme.CreateButton("保存", true);
        save.Click += OnSaveClick;
        _bottomPanel.Controls.Add(save);

        _bottomPanel.Tag = new[] { cancel, save };

        LoadValues();
        LayoutInternal();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutInternal();
    }

    private void LayoutInternal()
    {
        if (_tabs == null || _bottomPanel == null) return;

        const int bottomHeight = 56;
        _tabs.SetBounds(12, 12, ClientSize.Width - 24, Math.Max(200, ClientSize.Height - bottomHeight - 20));
        _bottomPanel.SetBounds(0, ClientSize.Height - bottomHeight, ClientSize.Width, bottomHeight);

        if (_bottomPanel.Tag is Button[] buttons && buttons.Length == 2)
        {
            var save = buttons[1];
            var cancel = buttons[0];
            save.Location = new Point(_bottomPanel.Width - save.Width - 16, 11);
            cancel.Location = new Point(Math.Max(16, save.Left - cancel.Width - 10), 11);
        }
    }

    private TabPage BuildTier1Page()
    {
        var page = new TabPage("一级代理") { BackColor = Color.White, AutoScroll = true };
        var b = new PageBuilder(page);

        b.Section("监听");

        _listenHost.DropDownStyle = ComboBoxStyle.DropDownList;
        _listenHost.Font = AppTheme.Ui;
        _listenHost.Width = FieldWidth;
        b.Row("监听地址", _listenHost);
        b.Row("监听端口", _listenPort, 100);
        b.Block(_gatewayToggle);
        b.Hint("默认仅本机；选择 0.0.0.0 或允许局域网访问后，局域网可访问，请同时配置访问控制。");

        b.Section("访问控制");
        b.Row("白名单", _allowBox);
        b.Row("黑名单", _denyBox);
        b.Hint("每行一条 IP / 网段 / 主机名；为空表示不限制。");

        b.Section("本地路由");
        b.Row("免代地址", _noProxyBox);
        b.Hint("这些地址由本机直接处理，支持通配符与 CIDR。");

        b.Section("本地服务");
        b.Row("SOCKS5 监听", _socks5Box);
        b.Row("SOCKS5 认证", _socks5UserBox);
        b.Row("隧道", _tunnelBox);
        b.Hint("隧道格式：[saddr:]lport:rhost:rport，每行一条。");

        return page;
    }

    private TabPage BuildTier2Page()
    {
        var page = new TabPage("二级代理") { BackColor = Color.White, AutoScroll = true };
        var b = new PageBuilder(page);

        _useProxyToggle.Location = new Point(16, 12);
        _useProxyToggle.Width = 430;
        _useProxyToggle.CheckedChanged += (_, _) => UpdateTier2Visibility();
        page.Controls.Add(_useProxyToggle);

        _tier2OffHint = new Label
        {
            Text = "未启用对外互联时，只有一级代理生效。",
            Font = AppTheme.UiSmall,
            ForeColor = AppTheme.Muted,
            Location = new Point(16, 46),
            Size = new Size(520, 20),
        };
        page.Controls.Add(_tier2OffHint);

        var body = new Panel { BackColor = Color.White, Location = new Point(0, 74), Width = 570 };
        _tier2Body = body;

        var bb = new PageBuilder(body, 0);

        bb.Section("上游代理");
        bb.Button("代理列表编辑…", OnEditProxyList);
        bb.Hint("管理上游代理，测速与排序。", indent: 0);

        bb.Section("上游选择");

        _pacBox.Width = 280;
        var pacRow = InlineRow(_pacBox, 280, AppTheme.CreateSmallButton("浏览"));
        pacRow.Controls[^1].Click += OnBrowsePac;
        bb.Row("PAC 文件", pacRow);

        bb.Row("请求头替换", _headerBox);
        bb.Hint("格式：HeaderName: value，每行一条。");

        bb.Block(_isaToggle);
        bb.Row("探测包大小(KB)", _isaSizeBox, 100);
        bb.Row("代理标识", _isaAgentBox);
        bb.Hint("ISA 扫描至少需要一条代理标识才会生效。");

        bb.Section("上游认证");
        bb.Row("用户名", _usernameBox);
        bb.Row("域", _domainBox);
        bb.Row("工作站", _workstationBox);
        bb.Row("认证类型", _authCombo);

        _flagsCombo.DropDownStyle = ComboBoxStyle.DropDown;
        _flagsCombo.Font = AppTheme.Ui;
        _flagsCombo.Width = FieldWidth;
        _flagsCombo.Items.AddRange(new object[]
        {
            "默认(自动)", "0x00020000", "0x06820000", "0x06800000", "0x02088207",
        });
        _flagsCombo.SelectedIndex = 0;
        bb.Row("Flags", _flagsCombo);

        bb.Block(_sspiToggle);
        bb.Row("SSPI 类型", _sspiTypeCombo);

        _passwordBox.UseSystemPasswordChar = true;
        _passwordBox.Width = FieldWidth;
        bb.Row("密码", _passwordBox);

        _passLMBox.Font = AppTheme.Mono;
        _passNTBox.Font = AppTheme.Mono;
        _passNTLMv2Box.Font = AppTheme.Mono;
        bb.Row("PassLM", _passLMBox);
        bb.Row("PassNT", _passNTBox);
        bb.Row("PassNTLMv2", _passNTLMv2Box);

        var hashButton = AppTheme.CreateButton("生成哈希");
        hashButton.Location = new Point(16, bb.Y);
        hashButton.Click += OnGenerateHash;
        body.Controls.Add(hashButton);
        bb.Gap(hashButton.Height + 8);

        _hashStatus.Text = "";
        _hashStatus.Font = AppTheme.UiSmall;
        _hashStatus.ForeColor = AppTheme.Muted;
        _hashStatus.AutoSize = false;
        _hashStatus.Height = 32;
        bb.Block(_hashStatus, 0, 420);

        body.Height = bb.Y + 10;
        page.Controls.Add(body);

        return page;
    }

    private TabPage BuildRunPage()
    {
        var page = new TabPage("运行") { BackColor = Color.White, AutoScroll = true };
        var b = new PageBuilder(page);

        b.Section("程序与配置");
        _cntlmPathBox.Width = 220;
        var pathRow = InlineRow(_cntlmPathBox, 220,
            AppTheme.CreateSmallButton("检测"), AppTheme.CreateSmallButton("浏览"));
        pathRow.Controls[1].Click += OnDetectCntlm;
        pathRow.Controls[2].Click += OnBrowseCntlm;
        b.Row("可执行文件", pathRow);

        _outputPathBox.Width = 140;
        var outputRow = InlineRow(_outputPathBox, 140,
            AppTheme.CreateSmallButton("默认"), AppTheme.CreateSmallButton("浏览"), AppTheme.CreateSmallButton("预览…"));
        outputRow.Controls[1].Click += (_, _) => _outputPathBox.Text = GuiSettings.DefaultOutputConfigPath;
        outputRow.Controls[2].Click += OnBrowseOutput;
        outputRow.Controls[3].Click += OnPreviewClick;
        b.Row("输出路径", outputRow);

        b.Section("运行方式");
        bbRow(b, "运行方式", _modeCombo);
        b.Row("服务名", _serviceNameBox);
        _modeCombo.SelectedIndexChanged += (_, _) => UpdateServicePathLabel();
        _serviceNameBox.TextChanged += (_, _) => UpdateServicePathLabel();

        _servicePathLabel.Font = AppTheme.UiSmall;
        _servicePathLabel.ForeColor = AppTheme.Muted;
        _servicePathLabel.AutoSize = false;
        _servicePathLabel.Height = 22;
        _servicePathLabel.AutoEllipsis = true;
        b.Block(_servicePathLabel, 0, 560);
        b.Hint("服务模式通常需要管理员权限。", indent: 0);

        b.Section("界面行为");
        b.Block(_autoStartToggle);
        b.Block(_closeToTrayToggle);
        b.Block(_traceToggle);
        b.Hint("开启详细日志后会持续写入 trace 日志文件。", indent: 0);

        b.Section("配置迁移");
        b.Button("从 cntlm.ini 导入…", OnImportClick);

        _importStatus.Text = "";
        _importStatus.Font = AppTheme.UiSmall;
        _importStatus.ForeColor = AppTheme.Muted;
        _importStatus.AutoSize = false;
        _importStatus.Height = 22;
        b.Block(_importStatus, 0, 480);

        return page;
    }

    private static void bbRow(PageBuilder builder, string label, Control control) => builder.Row(label, control);

    private void UpdateTier2Visibility()
    {
        var enabled = _useProxyToggle.Checked;
        if (_tier2Body != null) _tier2Body.Visible = enabled;
        if (_tier2OffHint != null) _tier2OffHint.Visible = !enabled;
    }

    private void LoadValues()
    {
        var hosts = new List<string> { "0.0.0.0", "127.0.0.1" };
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Tunnel) continue;
                foreach (var address in nic.GetIPProperties().UnicastAddresses)
                {
                    var ip = address.Address;
                    if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                    var text = ip.ToString();
                    if (text.StartsWith("169.254.", StringComparison.Ordinal)) continue;
                    if (!hosts.Contains(text)) hosts.Add(text);
                }
            }
        }
        catch
        {
            // 忽略网卡枚举失败
        }

        var (host, portText) = GuiSettings.SplitListen(_settings.Listen);
        if (!hosts.Contains(host)) hosts.Add(host);
        _listenHost.Items.Clear();
        _listenHost.Items.AddRange(hosts.Cast<object>().ToArray());
        _listenHost.SelectedItem = host;

        _listenPort.Text = portText;
        _gatewayToggle.Checked = _settings.Gateway;
        _allowBox.Text = _settings.AllowList;
        _denyBox.Text = _settings.DenyList;
        _noProxyBox.Text = _settings.NoProxyList;
        _socks5Box.Text = _settings.Socks5Proxy;
        _socks5UserBox.Text = _settings.Socks5User;
        _tunnelBox.Text = _settings.TunnelList;

        _useProxyToggle.Checked = _settings.UseProxy;
        _pacBox.Text = _settings.PacFile;
        _headerBox.Text = _settings.HeaderList;
        _isaToggle.Checked = _settings.UseIsaScanner;
        _isaSizeBox.Text = _settings.IsaScannerSize.ToString();
        _isaAgentBox.Text = _settings.IsaScannerAgentList;

        _usernameBox.Text = _settings.Username;
        _domainBox.Text = _settings.Domain;
        _workstationBox.Text = _settings.Workstation;

        if (!string.IsNullOrWhiteSpace(_settings.Auth))
        {
            if (!_authCombo.Items.Contains(_settings.Auth)) _authCombo.Items.Add(_settings.Auth);
            _authCombo.SelectedItem = _settings.Auth;
        }
        else
        {
            _authCombo.SelectedIndex = 0;
        }

        if (string.IsNullOrWhiteSpace(_settings.Flags))
        {
            _flagsCombo.SelectedIndex = 0;
        }
        else
        {
            _flagsCombo.Text = _settings.Flags;
        }

        _sspiToggle.Checked = _settings.UseSspi;
        if (!_sspiTypeCombo.Items.Contains(_settings.SspiType)) _sspiTypeCombo.Items.Add(_settings.SspiType);
        _sspiTypeCombo.SelectedItem = string.IsNullOrWhiteSpace(_settings.SspiType) ? "NTLM" : _settings.SspiType;

        _passwordBox.Text = _settings.Password;
        _passLMBox.Text = _settings.PassLM;
        _passNTBox.Text = _settings.PassNT;
        _passNTLMv2Box.Text = _settings.PassNTLMv2;

        _cntlmPathBox.Text = _settings.CntlmPath;
        _outputPathBox.Text = _settings.OutputConfigPath;
        _modeCombo.SelectedIndex = _settings.UseService ? 1 : 0;
        _serviceNameBox.Text = _settings.ServiceName;
        _autoStartToggle.Checked = _settings.AutoStart;
        _closeToTrayToggle.Checked = _settings.CloseToTray;
        _traceToggle.Checked = _settings.EnableTrace;

        UpdateTier2Visibility();
        UpdateServicePathLabel();
    }

    private void ApplyTo(GuiSettings target)
    {
        var host = _listenHost.Text.Trim();
        if (host.Length == 0) host = "127.0.0.1";
        var portText = _listenPort.Text.Trim();
        var port = int.TryParse(portText, out var parsedPort) && parsedPort is >= 1 and <= 65535 ? parsedPort : 8080;
        target.Listen = $"{host}:{port}";
        target.Gateway = _gatewayToggle.Checked;

        target.AllowList = _allowBox.Text.Trim();
        target.DenyList = _denyBox.Text.Trim();
        target.UseAllow = TextLines.Split(target.AllowList).Length > 0 || TextLines.Split(target.DenyList).Length > 0;
        target.NoProxyList = _noProxyBox.Text.Trim();

        target.Socks5Proxy = _socks5Box.Text.Trim();
        target.Socks5User = _socks5UserBox.Text.Trim();
        target.UseSocks5 = target.Socks5Proxy.Length > 0;
        target.TunnelList = _tunnelBox.Text.Trim();
        target.UseTunnel = TextLines.Split(target.TunnelList).Length > 0;

        target.UseProxy = _useProxyToggle.Checked;
        target.PacFile = _pacBox.Text.Trim();
        target.UsePac = target.PacFile.Length > 0;
        target.HeaderList = _headerBox.Text.Trim();
        target.UseHeader = TextLines.Split(target.HeaderList).Length > 0;
        target.UseIsaScanner = _isaToggle.Checked;
        target.IsaScannerSize = int.TryParse(_isaSizeBox.Text.Trim(), out var isaSize) ? Math.Max(0, isaSize) : 1024;
        target.IsaScannerAgentList = _isaAgentBox.Text.Trim();

        target.Username = _usernameBox.Text.Trim();
        target.Domain = _domainBox.Text.Trim();
        target.Workstation = _workstationBox.Text.Trim();
        target.Auth = _authCombo.SelectedIndex <= 0 ? "" : _authCombo.SelectedItem?.ToString() ?? "";
        var flagsText = _flagsCombo.Text.Trim();
        target.Flags = flagsText == "默认(自动)" ? "" : flagsText;
        target.UseSspi = _sspiToggle.Checked;
        target.SspiType = string.IsNullOrWhiteSpace(_sspiTypeCombo.Text) ? "NTLM" : _sspiTypeCombo.Text.Trim();
        target.Password = _passwordBox.Text;
        target.PassLM = _passLMBox.Text.Trim();
        target.PassNT = _passNTBox.Text.Trim();
        target.PassNTLMv2 = _passNTLMv2Box.Text.Trim();

        target.CntlmPath = _cntlmPathBox.Text.Trim();
        target.OutputConfigPath = _outputPathBox.Text.Trim();
        target.UseService = _modeCombo.SelectedIndex == 1;
        target.ServiceName = string.IsNullOrWhiteSpace(_serviceNameBox.Text) ? "cntlm" : _serviceNameBox.Text.Trim();
        target.AutoStart = _autoStartToggle.Checked;
        target.CloseToTray = _closeToTrayToggle.Checked;
        target.EnableTrace = _traceToggle.Checked;
    }

    private void OnSaveClick(object? sender, EventArgs e)
    {
        var portText = _listenPort.Text.Trim();
        if (!int.TryParse(portText, out var port) || port is < 1 or > 65535)
        {
            MessageBox.Show(this, "监听端口必须是 1–65535 之间的数字。", "设置",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _listenPort.Focus();
            _listenPort.SelectAll();
            return;
        }

        ApplyTo(_settings);

        try
        {
            _settings.Save();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "保存设置失败：" + ex.Message, "设置",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        DialogResult = DialogResult.OK;
        Close();
    }

    private void UpdateServicePathLabel()
    {
        if (_modeCombo.SelectedIndex != 1)
        {
            _servicePathLabel.Text = "服务实际配置：仅服务模式使用";
            _servicePathLabel.ForeColor = AppTheme.Muted;
            return;
        }

        var name = string.IsNullOrWhiteSpace(_serviceNameBox.Text) ? "cntlm" : _serviceNameBox.Text.Trim();
        var path = CntlmService.ConfigPath(name);
        if (string.IsNullOrWhiteSpace(path))
        {
            _servicePathLabel.Text = "服务实际配置：未找到该服务（请先用 cntlm 安装器创建）";
            _servicePathLabel.ForeColor = AppTheme.Muted;
        }
        else
        {
            _servicePathLabel.Text = "服务实际配置：" + path;
            _servicePathLabel.ForeColor = AppTheme.Text;
        }
    }

    private void OnDetectCntlm(object? sender, EventArgs e)
    {
        var found = Detect.FindCntlm();
        if (string.IsNullOrWhiteSpace(found))
        {
            MessageBox.Show(this, "未找到 cntlm.exe，请手动浏览指定。", "检测",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _cntlmPathBox.Text = found;
    }

    private void OnBrowseCntlm(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择 cntlm.exe",
            Filter = "cntlm (cntlm.exe)|cntlm.exe|可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) _cntlmPathBox.Text = dialog.FileName;
    }

    private void OnBrowseOutput(object? sender, EventArgs e)
    {
        using var dialog = new SaveFileDialog
        {
            Title = "选择输出配置文件",
            Filter = "cntlm 配置 (*.ini)|*.ini|所有文件 (*.*)|*.*",
            FileName = "cntlm.ini",
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) _outputPathBox.Text = dialog.FileName;
    }

    private void OnBrowsePac(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择 PAC 文件",
            Filter = "PAC 文件 (*.pac)|*.pac|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) _pacBox.Text = dialog.FileName;
    }

    private void OnEditProxyList(object? sender, EventArgs e)
    {
        using var form = new ProxySpeedForm(_settings);
        form.ShowDialog(this);
    }

    private void OnPreviewClick(object? sender, EventArgs e)
    {
        var temp = new GuiSettings();
        ApplyTo(temp);
        temp.ProxyList = _settings.ProxyList;
        temp.LastBench = _settings.LastBench;

        string text;
        try
        {
            text = CntlmConfigGenerator.Generate(temp);
        }
        catch (Exception ex)
        {
            text = "生成配置失败：" + ex.Message;
        }

        var preview = new Form
        {
            Text = "配置预览",
            ClientSize = new Size(760, 560),
            FormBorderStyle = FormBorderStyle.FixedSingle,
            MaximizeBox = false,
            MinimizeBox = false,
            StartPosition = FormStartPosition.CenterParent,
            Font = AppTheme.Ui,
            BackColor = Color.White,
        };
        var box = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = AppTheme.Mono,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.White,
            Location = new Point(8, 8),
            Size = new Size(744, 544),
            Text = text,
        };
        preview.Controls.Add(box);
        preview.ShowDialog(this);
    }

    private void OnImportClick(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "从 cntlm.ini 导入",
            Filter = "cntlm 配置 (*.ini)|*.ini|所有文件 (*.*)|*.*",
        };

        var defaultPath = Detect.KnownIniPaths().FirstOrDefault(File.Exists);
        if (defaultPath != null) dialog.FileName = defaultPath;

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var imported = CntlmConfigGenerator.Import(dialog.FileName);
            _settings.ProxyList = imported.ProxyList;
            LoadValuesFrom(imported);
            _importStatus.Text = "已导入配置（尚未保存）。";
            _importStatus.ForeColor = AppTheme.Success;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "导入失败：" + ex.Message, "导入",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void LoadValuesFrom(GuiSettings source)
    {
        var (host, portText) = GuiSettings.SplitListen(source.Listen);
        if (!_listenHost.Items.Contains(host)) _listenHost.Items.Add(host);
        _listenHost.SelectedItem = host;
        _listenPort.Text = portText;
        _gatewayToggle.Checked = source.Gateway;
        _allowBox.Text = source.AllowList;
        _denyBox.Text = source.DenyList;
        _noProxyBox.Text = source.NoProxyList;
        _socks5Box.Text = source.Socks5Proxy;
        _socks5UserBox.Text = source.Socks5User;
        _tunnelBox.Text = source.TunnelList;

        _useProxyToggle.Checked = source.UseProxy;
        _pacBox.Text = source.PacFile;
        _headerBox.Text = source.HeaderList;
        _isaToggle.Checked = source.UseIsaScanner;
        _isaSizeBox.Text = source.IsaScannerSize.ToString();
        _isaAgentBox.Text = source.IsaScannerAgentList;

        _usernameBox.Text = source.Username;
        _domainBox.Text = source.Domain;
        _workstationBox.Text = source.Workstation;

        if (!string.IsNullOrWhiteSpace(source.Auth))
        {
            if (!_authCombo.Items.Contains(source.Auth)) _authCombo.Items.Add(source.Auth);
            _authCombo.SelectedItem = source.Auth;
        }
        else
        {
            _authCombo.SelectedIndex = 0;
        }

        if (string.IsNullOrWhiteSpace(source.Flags)) _flagsCombo.SelectedIndex = 0;
        else _flagsCombo.Text = source.Flags;

        _sspiToggle.Checked = source.UseSspi;
        if (!_sspiTypeCombo.Items.Contains(source.SspiType)) _sspiTypeCombo.Items.Add(source.SspiType);
        _sspiTypeCombo.SelectedItem = string.IsNullOrWhiteSpace(source.SspiType) ? "NTLM" : source.SspiType;

        _passwordBox.Text = source.Password;
        _passLMBox.Text = source.PassLM;
        _passNTBox.Text = source.PassNT;
        _passNTLMv2Box.Text = source.PassNTLMv2;

        UpdateTier2Visibility();
    }

    private async void OnGenerateHash(object? sender, EventArgs e)
    {
        var user = _usernameBox.Text.Trim();
        var password = _passwordBox.Text;
        var domain = _domainBox.Text.Trim();

        if (user.Length == 0 || password.Length == 0)
        {
            _hashStatus.ForeColor = AppTheme.Danger;
            _hashStatus.Text = "请先填写用户名和密码。";
            return;
        }

        var exe = ResolveExeFromUi();
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
        {
            _hashStatus.ForeColor = AppTheme.Danger;
            _hashStatus.Text = "未找到 cntlm.exe，请在 运行 页指定路径。";
            return;
        }

        _hashStatus.ForeColor = AppTheme.Muted;
        _hashStatus.Text = "正在生成哈希…";

        try
        {
            var arguments = new StringBuilder();
            arguments.Append("-H -u \"").Append(user).Append('"');
            if (domain.Length > 0) arguments.Append(" -d \"").Append(domain).Append('"');

            var psi = new ProcessStartInfo(exe, arguments.ToString())
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory,
            };

            using var process = new Process { StartInfo = psi };
            process.Start();

            await process.StandardInput.WriteLineAsync(password);
            process.StandardInput.Close();

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            var exited = await Task.Run(() => process.WaitForExit(30000));
            if (!exited)
            {
                try
                {
                    process.Kill(true);
                }
                catch
                {
                    // 忽略
                }

                _hashStatus.ForeColor = AppTheme.Danger;
                _hashStatus.Text = "生成哈希超时（30 秒）。";
                return;
            }

            var output = await stdoutTask + "\n" + await stderrTask;
            var lm = ParseHash(output, "PassLM");
            var nt = ParseHash(output, "PassNT");
            var ntlmv2 = ParseHash(output, "PassNTLMv2");

            if (lm == null && nt == null && ntlmv2 == null)
            {
                MessageBox.Show(this, output, "生成哈希", MessageBoxButtons.OK, MessageBoxIcon.Information);
                _hashStatus.ForeColor = AppTheme.Danger;
                _hashStatus.Text = "未能解析哈希输出。";
                return;
            }

            if (lm != null) _passLMBox.Text = lm;
            if (nt != null) _passNTBox.Text = nt;
            if (ntlmv2 != null) _passNTLMv2Box.Text = ntlmv2;

            _hashStatus.ForeColor = AppTheme.Success;
            _hashStatus.Text = "哈希已生成（未保存）。";
        }
        catch (Exception ex)
        {
            _hashStatus.ForeColor = AppTheme.Danger;
            _hashStatus.Text = "生成哈希失败：" + ex.Message;
        }
    }

    private static string? ParseHash(string output, string key)
    {
        var match = Regex.Match(output, $@"(?im)^\s*{key}\s*[:=]?\s*([0-9A-Fa-f]{{16,}})\s*$");
        return match.Success ? match.Groups[1].Value : null;
    }

    private string ResolveExeFromUi()
    {
        var path = _cntlmPathBox.Text.Trim();
        if (path.Length > 0 && File.Exists(path)) return path;
        return _settings.EffectiveBaseCntlmPath();
    }

    private static TextBox NewText(string value = "", bool multiline = false, int height = 26)
    {
        var box = new TextBox
        {
            Text = value,
            Font = AppTheme.Ui,
            BorderStyle = BorderStyle.FixedSingle,
            Multiline = multiline,
        };
        if (multiline)
        {
            box.Height = height;
            box.ScrollBars = ScrollBars.Vertical;
        }
        return box;
    }

    private static TextBox NewMonoText() => new()
    {
        Font = AppTheme.Mono,
        BorderStyle = BorderStyle.FixedSingle,
    };

    private static ToggleSwitch NewToggle(string text) => new()
    {
        Text = text,
        Width = FieldWidth,
    };

    private static ComboBox NewCombo(params string[] items)
    {
        var combo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font = AppTheme.Ui,
            FlatStyle = FlatStyle.System,
            Width = FieldWidth,
        };
        combo.Items.AddRange(items);
        if (items.Length > 0) combo.SelectedIndex = 0;
        return combo;
    }

    private static Panel InlineRow(Control main, int mainWidth, params Control[] buttons)
    {
        var panel = new Panel { Height = 26, Width = FieldWidth };
        main.Location = new Point(0, 1);
        main.Width = mainWidth;
        panel.Controls.Add(main);
        var x = mainWidth + 6;
        foreach (var button in buttons)
        {
            button.Location = new Point(x, 0);
            panel.Controls.Add(button);
            x += button.Width + 6;
        }
        return panel;
    }

    private sealed class PageBuilder
    {
        private const int Left = 16;
        private readonly Control _page;

        public PageBuilder(Control page, int startY = 12)
        {
            _page = page;
            Y = startY;
        }

        public int Y { get; private set; }

        public void Gap(int pixels) => Y += pixels;

        public void Section(string title)
        {
            _page.Controls.Add(new Label
            {
                Text = title,
                Font = AppTheme.UiTitle,
                ForeColor = AppTheme.Text,
                AutoSize = true,
                Location = new Point(Left, Y),
            });
            Y += 28;
        }

        public T Row<T>(string label, T control, int? width = null) where T : Control
        {
            _page.Controls.Add(new Label
            {
                Text = label,
                Font = AppTheme.Ui,
                ForeColor = AppTheme.Text,
                Location = new Point(Left, Y + (control.Height > 28 ? 4 : 5)),
                Size = new Size(AppTheme.LabelCol, 20),
            });
            control.Location = new Point(Left + AppTheme.LabelCol, Y);
            control.Width = width ?? FieldWidth;
            _page.Controls.Add(control);
            Y += Math.Max(control.Height, 26) + 10;
            return control;
        }

        public void Block(Control control, int indent = 0, int? width = null)
        {
            control.Location = new Point(Left + indent, Y);
            control.Width = width ?? FieldWidth;
            _page.Controls.Add(control);
            Y += control.Height + 10;
        }

        public void Hint(string text, int width = 380, int indent = AppTheme.LabelCol)
        {
            var measured = TextRenderer.MeasureText(text, AppTheme.UiSmall,
                new Size(width, int.MaxValue), TextFormatFlags.WordBreak);
            _page.Controls.Add(new Label
            {
                Text = text,
                Font = AppTheme.UiSmall,
                ForeColor = AppTheme.Muted,
                AutoSize = false,
                Location = new Point(Left + indent, Y),
                Size = new Size(width, measured.Height + 4),
            });
            Y += measured.Height + 8;
        }

        public Button Button(string text, EventHandler handler, bool primary = false)
        {
            var button = AppTheme.CreateButton(text, primary);
            button.Location = new Point(Left, Y);
            button.Click += handler;
            _page.Controls.Add(button);
            Y += button.Height + 10;
            return button;
        }
    }
}
