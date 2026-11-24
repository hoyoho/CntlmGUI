using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using CntlmGUI.Services;

namespace CntlmGUI.Models;

public sealed class GuiSettings
{
    // 认证
    public string Username { get; set; } = "";
    public string Domain { get; set; } = "";
    public string Workstation { get; set; } = "";

    [JsonIgnore]
    public string Password { get; set; } = "";

    public string PasswordProtected { get; set; } = "";
    public string PassLM { get; set; } = "";
    public string PassNT { get; set; } = "";
    public string PassNTLMv2 { get; set; } = "";
    public string Auth { get; set; } = "";
    public string Flags { get; set; } = "";

    // 代理 / 一级 + 二级
    public string ProxyList { get; set; } = "";
    public bool UseProxy { get; set; }
    public string NoProxyList { get; set; } = "";
    public string Listen { get; set; } = "127.0.0.1:8080";
    public bool Gateway { get; set; }
    public bool UsePac { get; set; }
    public string PacFile { get; set; } = "";
    public bool UseSocks5 { get; set; }
    public string Socks5Proxy { get; set; } = "";
    public string Socks5User { get; set; } = "";
    public bool UseAllow { get; set; }
    public string AllowList { get; set; } = "";
    public string DenyList { get; set; } = "";
    public bool UseTunnel { get; set; }
    public string TunnelList { get; set; } = "";
    public bool UseHeader { get; set; }
    public string HeaderList { get; set; } = "";
    public bool UseIsaScanner { get; set; }
    public int IsaScannerSize { get; set; } = 1024;
    public string IsaScannerAgentList { get; set; } = "";
    public bool UseSspi { get; set; }
    public string SspiType { get; set; } = "NTLM";

    // 运行
    public string CntlmPath { get; set; } = "";
    public string OutputConfigPath { get; set; } = "";
    public bool UseService { get; set; }
    public string ServiceName { get; set; } = "cntlm";
    public bool AutoStart { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public bool EnableTrace { get; set; }

    // 测速
    public string SpeedTestUrl { get; set; } = "https://www.google.com";
    public int SpeedTestMb { get; set; } = 10;
    public int SpeedTestTimeoutSec { get; set; } = 15;
    public List<ProxyBench> LastBench { get; set; } = new();

    [JsonIgnore]
    public static string ConfigDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CntlmGUI");

    [JsonIgnore]
    public static string SettingsPath => Path.Combine(ConfigDir, "settings.json");

    [JsonIgnore]
    public static string DefaultOutputConfigPath => Path.Combine(ConfigDir, "cntlm.ini");

    [JsonIgnore]
    public static string TraceLogPath => Path.Combine(ConfigDir, "cntlm-trace.log");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static GuiSettings Load()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);

            if (!File.Exists(SettingsPath))
            {
                var imported = TryImportInitialConfig();
                var fresh = imported ?? new GuiSettings();
                try
                {
                    fresh.Save();
                }
                catch
                {
                    // 首次保存失败不阻塞启动
                }
                return fresh;
            }

            var json = File.ReadAllText(SettingsPath, Encoding.UTF8);
            var settings = JsonSerializer.Deserialize<GuiSettings>(json, JsonOptions) ?? new GuiSettings();
            settings.Normalize();

            if (!string.IsNullOrEmpty(settings.PasswordProtected))
            {
                var plain = SecretProtector.Unprotect(settings.PasswordProtected);
                if (plain != null) settings.Password = plain;
            }

            return settings;
        }
        catch
        {
            TryBackupBrokenSettings();
            return new GuiSettings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDir);

        if (!string.IsNullOrEmpty(Password))
        {
            var encrypted = SecretProtector.Protect(Password);
            // 加密失败时保留上一次密文，绝不写明文
            if (encrypted != null) PasswordProtected = encrypted;
        }
        else
        {
            PasswordProtected = "";
        }

        var json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(SettingsPath, json, new UTF8Encoding(false));
    }

    public string EffectiveBaseCntlmPath()
    {
        if (!string.IsNullOrWhiteSpace(CntlmPath))
        {
            try
            {
                if (File.Exists(CntlmPath)) return Path.GetFullPath(CntlmPath);
            }
            catch
            {
                // 忽略非法路径
            }
        }

        foreach (var candidate in Detect.KnownCntlmPaths())
        {
            try
            {
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // 忽略非法路径
            }
        }

        return Detect.FindCntlm() ?? "";
    }

    /// <summary>供端口探测使用的 host:port；0.0.0.0 / * 归一化为 127.0.0.1。</summary>
    public static (string Host, int Port) ParseListen(string? listen)
    {
        var host = "127.0.0.1";
        var port = 8080;
        if (string.IsNullOrWhiteSpace(listen)) return (host, port);

        var text = listen.Trim();
        var idx = text.LastIndexOf(':');
        if (idx < 0)
        {
            // cntlm 允许只写端口（官方 cntlm.ini 默认就是 Listen 3128）
            if (int.TryParse(text, out var onlyPort) && onlyPort is >= 1 and <= 65535) port = onlyPort;
            return (host, port);
        }

        var rawHost = text[..idx].Trim().Trim('[', ']');
        if (rawHost.Length > 0) host = rawHost;
        if (host is "0.0.0.0" or "*" or "+") host = "127.0.0.1";

        if (int.TryParse(text[(idx + 1)..].Trim(), out var parsed) && parsed is >= 1 and <= 65535)
        {
            port = parsed;
        }

        return (host, port);
    }

    /// <summary>保留原始 host，用于设置界面回显。</summary>
    public static (string Host, string Port) SplitListen(string? listen)
    {
        if (string.IsNullOrWhiteSpace(listen)) return ("127.0.0.1", "8080");

        var text = listen.Trim();
        var idx = text.LastIndexOf(':');
        if (idx < 0)
        {
            if (int.TryParse(text, out var onlyPort) && onlyPort is >= 1 and <= 65535)
            {
                return ("127.0.0.1", onlyPort.ToString());
            }
            return ("127.0.0.1", "8080");
        }

        var host = text[..idx].Trim();
        var portText = text[(idx + 1)..].Trim();
        if (host.Length == 0) host = "127.0.0.1";
        if (portText.Length == 0) portText = "8080";
        return (host, portText);
    }

    private void Normalize()
    {
        Username ??= "";
        Domain ??= "";
        Workstation ??= "";
        PasswordProtected ??= "";
        PassLM ??= "";
        PassNT ??= "";
        PassNTLMv2 ??= "";
        Auth ??= "";
        Flags ??= "";
        ProxyList ??= "";
        NoProxyList ??= "";
        Listen ??= "127.0.0.1:8080";
        PacFile ??= "";
        Socks5Proxy ??= "";
        Socks5User ??= "";
        AllowList ??= "";
        DenyList ??= "";
        TunnelList ??= "";
        HeaderList ??= "";
        IsaScannerAgentList ??= "";
        SspiType = string.IsNullOrWhiteSpace(SspiType) ? "NTLM" : SspiType;
        CntlmPath ??= "";
        OutputConfigPath ??= "";
        ServiceName = string.IsNullOrWhiteSpace(ServiceName) ? "cntlm" : ServiceName;
        SpeedTestUrl ??= "https://www.google.com";
        LastBench ??= new List<ProxyBench>();
        LastBench.RemoveAll(item => item == null);
    }

    private static GuiSettings? TryImportInitialConfig()
    {
        foreach (var path in Detect.KnownIniPaths())
        {
            try
            {
                if (!File.Exists(path)) continue;
                return CntlmConfigGenerator.Import(path);
            }
            catch
            {
                // 导入失败静默忽略
            }
        }

        return null;
    }

    private static void TryBackupBrokenSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var backup = $"{SettingsPath}.bak-{DateTime.Now:yyyyMMddHHmmss}";
                File.Copy(SettingsPath, backup, true);
            }
        }
        catch
        {
            // 备份失败时忽略
        }
    }
}
