using System;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CntlmGUI.Services;

/// <summary>
/// 读取 Windows 服务实际使用的 cntlm 配置路径。
/// 支持 cygrunsrv（Parameters\AppPath/AppArgs）和普通 ImagePath 两种注册方式。
/// </summary>
public static class CntlmService
{
    public static bool Exists(string serviceName)
    {
        if (string.IsNullOrWhiteSpace(serviceName)) return false;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(ServiceKey(serviceName));
            return key != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>解析服务实际读取的 ini 路径；找不到服务或解析失败返回 null。</summary>
    public static string? ConfigPath(string serviceName)
    {
        if (string.IsNullOrWhiteSpace(serviceName)) return null;
        try
        {
            var root = ServiceKey(serviceName);
            using var key = Registry.LocalMachine.OpenSubKey(root);
            if (key == null) return null;

            using var argsKey = Registry.LocalMachine.OpenSubKey(root + @"\Parameters");
            var appPath = argsKey?.GetValue("AppPath") as string;
            var appArgs = argsKey?.GetValue("AppArgs") as string ?? "";
            var imagePath = key.GetValue("ImagePath") as string ?? "";

            var fromArgs = FindConfigArg(appArgs) ?? FindConfigArg(imagePath);
            if (!string.IsNullOrWhiteSpace(fromArgs))
                return Environment.ExpandEnvironmentVariables(fromArgs.Trim('"'));

            // 没有 -c：cntlm 使用可执行文件同目录下的 cntlm.ini
            var exe = !string.IsNullOrWhiteSpace(appPath) ? appPath : FirstExecutable(imagePath);
            if (!string.IsNullOrWhiteSpace(exe))
                return Path.Combine(Path.GetDirectoryName(exe)!, "cntlm.ini");
        }
        catch
        {
            // 读注册表失败按“未知”处理
        }
        return null;
    }

    private static string ServiceKey(string name) =>
        $@"SYSTEM\CurrentControlSet\Services\{name}";

    private static string? FindConfigArg(string commandLine)
    {
        var m = Regex.Match(commandLine ?? "", @"(?:^|\s)-c\s+(?:""([^""]+)""|(\S+))");
        if (!m.Success) return null;
        return m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
    }

    private static string? FirstExecutable(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) return null;
        var t = imagePath.Trim();
        if (t.StartsWith("\"", StringComparison.Ordinal))
        {
            int end = t.IndexOf('"', 1);
            return end > 0 ? t[1..end] : t.Trim('"');
        }
        int space = t.IndexOf(' ');
        return space > 0 ? t[..space] : t;
    }
}
