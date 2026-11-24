using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CntlmGUI.Services;

internal static class CntlmService
{
    private const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";

    /// <summary>解析服务实际使用的 cntlm.ini，解析失败返回 null。</summary>
    public static string? ConfigPath(string serviceName)
    {
        if (string.IsNullOrWhiteSpace(serviceName)) return null;

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($"{ServicesKey}\\{serviceName.Trim()}");
            if (key == null) return null;

            string? imagePath = key.GetValue("ImagePath") as string;
            string? appPath = null;
            string? appArgs = null;
            using (var parameters = key.OpenSubKey("Parameters"))
            {
                appPath = parameters?.GetValue("AppPath") as string;
                appArgs = parameters?.GetValue("AppArgs") as string;
            }

            foreach (var command in new[] { appArgs, imagePath, appPath })
            {
                var path = ExtractConfigPath(command);
                if (path != null) return path;
            }

            var executable = FirstExecutable(appPath) ?? FirstExecutable(imagePath);
            if (executable != null)
            {
                var dir = Path.GetDirectoryName(executable);
                if (!string.IsNullOrEmpty(dir)) return Path.Combine(dir, "cntlm.ini");
            }
        }
        catch
        {
            // 服务不存在或读取失败
        }

        return null;
    }

    public static bool IsRunning(string serviceName)
    {
        var result = RunSc($"query \"{serviceName?.Trim()}\"");
        return result.Output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
    }

    public static (int ExitCode, string Output) RunSc(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("sc", arguments)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process == null) return (-1, "");

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(15000))
            {
                try
                {
                    if (!process.HasExited) process.Kill(true);
                }
                catch
                {
                    // 忽略
                }
                return (-1, output + error);
            }

            return (process.ExitCode, output + error);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    private static string? ExtractConfigPath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;

        try
        {
            var match = Regex.Match(command, "(?:^|\\s)-c\\s+(?:\"([^\"]+)\"|(\\S+))");
            if (!match.Success) return null;

            var path = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            return path.Length == 0 ? null : path;
        }
        catch
        {
            return null;
        }
    }

    private static string? FirstExecutable(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;

        try
        {
            var text = Environment.ExpandEnvironmentVariables(command.Trim());
            string token;
            if (text.StartsWith('"'))
            {
                var end = text.IndexOf('"', 1);
                token = end > 1 ? text[1..end] : text.Trim('"');
            }
            else
            {
                var space = text.IndexOf(' ');
                token = space > 0 ? text[..space] : text;
            }

            token = token.Trim().Trim('"');
            return token.Length == 0 ? null : token;
        }
        catch
        {
            return null;
        }
    }
}
