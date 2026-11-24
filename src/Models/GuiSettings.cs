using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CntlmGUI.Services;

namespace CntlmGUI.Models;

/// <summary>
/// CntlmGUI 自身的配置。由用户通过设置界面维护，
/// 启动时据此动态构造喂给 cntlm 的配置文件。
/// </summary>
public class GuiSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // ----- 账号与认证 -----
    public string Username { get; set; } = "";
    public string Domain { get; set; } = "";
    public string Workstation { get; set; } = "";

    /// <summary>明文密码只存在于内存；落盘的是 <see cref="PasswordProtected"/>。</summary>
    [JsonIgnore]
    public string Password { get; set; } = "";

    /// <summary>DPAPI 加密后的密码（Base64）。</summary>
    public string PasswordProtected { get; set; } = "";

    public string PassLM { get; set; } = "";
    public string PassNT { get; set; } = "";
    public string PassNTLMv2 { get; set; } = "";
    public string Auth { get; set; } = "";
    public string Flags { get; set; } = "";

    // ----- 代理与网络 -----
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

    // ----- 运行设置 -----
    public string CntlmPath { get; set; } = "";
    public string OutputConfigPath { get; set; } = "";
    public bool UseService { get; set; }
    public string ServiceName { get; set; } = "cntlm";
    public bool AutoStart { get; set; } = true;
    public bool CloseToTray { get; set; } = true;

    /// <summary>以 -T 记录 cntlm 详细日志；默认关闭，避免长时间运行时 trace 文件持续增长。</summary>
    public bool EnableTrace { get; set; }

    // ----- 吞吐测速 -----
    /// <summary>测试目标地址；整链与吞吐测试共用。</summary>
    public string SpeedTestUrl { get; set; } = "https://www.google.com";
    /// <summary>每个代理最多下载的兆字节数（Range 限制）。</summary>
    public int SpeedTestMb { get; set; } = 10;
    /// <summary>单个代理的吞吐测试时限（秒）；到达时限即停止，按已下载字节计算速度。</summary>
    public int SpeedTestTimeoutSec { get; set; } = 15;

    /// <summary>上一次测速结果，用于下次打开代理列表时直接回显。</summary>
    public List<ProxyBench> LastBench { get; set; } = new();

    // ----- 派生路径 -----
    [JsonIgnore]
    public static string AppDataDir
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "CntlmGUI");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    [JsonIgnore]
    public static string SettingsFile => Path.Combine(AppDataDir, "settings.json");

    [JsonIgnore]
    public static string DefaultOutputConfig => Path.Combine(AppDataDir, "cntlm.ini");

    [JsonIgnore]
    public static string TraceLog => Path.Combine(AppDataDir, "cntlm-trace.log");

    // ----- 序列化 -----
    public static GuiSettings Load()
    {
        if (File.Exists(SettingsFile))
        {
            try
            {
                var json = File.ReadAllText(SettingsFile, Encoding.UTF8);
                var s = JsonSerializer.Deserialize<GuiSettings>(json) ?? new GuiSettings();
                s.DecryptPassword();
                return s;
            }
            catch
            {
                // 配置损坏时保留备份，继续用默认值
                TryBackupCorrupted();
            }
        }
        return new GuiSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(AppDataDir);
        try
        {
            PasswordProtected = SecretProtector.Protect(Password);
        }
        catch
        {
            // 加密失败时保留上一次的密文，绝不把明文写回文件
        }
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(this, JsonOptions), Encoding.UTF8);
    }

    private void DecryptPassword()
    {
        try
        {
            Password = SecretProtector.Unprotect(PasswordProtected);
        }
        catch
        {
            Password = "";
        }
    }

    private static void TryBackupCorrupted()
    {
        try
        {
            var backup = SettingsFile + ".bak-" + DateTime.Now.ToString("yyyyMMddHHmmss");
            File.Copy(SettingsFile, backup, overwrite: true);
        }
        catch
        {
            // 忽略备份失败
        }
    }

    public string? EffectiveBaseCntlmPath()
    {
        if (!string.IsNullOrWhiteSpace(CntlmPath))
        {
            if (File.Exists(CntlmPath)) return CntlmPath;
            return Detect.WellKnownPaths.FirstOrDefault(File.Exists) ?? CntlmPath;
        }
        return Detect.FindCntlm();
    }

    /// <summary>解析形如 127.0.0.1:8080 的监听地址；非法值回落到默认。</summary>
    public static (string host, int port) ParseListen(string listen)
    {
        var (host, portText) = SplitListen(listen);
        if (host == "0.0.0.0" || host == "*") host = "127.0.0.1";
        return (host, int.TryParse(portText, out var port) && port is > 0 and <= 65535 ? port : 8080);
    }

    /// <summary>拆分监听地址，保留原始 host（供设置界面回显，不做通配归一化）。</summary>
    public static (string host, string port) SplitListen(string listen)
    {
        var t = listen?.Trim() ?? "";
        if (t.Length == 0) return ("127.0.0.1", "8080");
        int idx = t.LastIndexOf(':');
        if (idx <= 0) return ("127.0.0.1", t);
        var host = t[..idx].Trim();
        var port = t[(idx + 1)..].Trim();
        return (host.Length == 0 ? "127.0.0.1" : host, port.Length == 0 ? "8080" : port);
    }

    /// <summary>把当前设定渲染成可喂给 cntlm 的完整配置文件文本。</summary>
    public string RenderConfig() => CntlmConfigGenerator.Generate(this);
}
