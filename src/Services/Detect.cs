using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace CntlmGUI.Services;

/// <summary>探测 cntlm 可执行文件与本机地址。</summary>
public static class Detect
{
    public static readonly string[] WellKnownPaths =
    {
        @"C:\Program Files (x86)\Cntlm\cntlm.exe",
        @"C:\Program Files\Cntlm\cntlm.exe",
    };

    public static string? FindCntlm()
    {
        foreach (var p in WellKnownPaths)
            if (File.Exists(p)) return p;

        try
        {
            var psi = new ProcessStartInfo("where", "cntlm.exe")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                var line = proc.StandardOutput.ReadLine();
                proc.WaitForExit(3000);
                if (!string.IsNullOrWhiteSpace(line) && File.Exists(line.Trim()))
                    return line.Trim();
            }
        }
        catch
        {
            // 忽略探测失败
        }
        return null;
    }

    /// <summary>枚举本机所有活跃网卡的 IPv4 地址（含通配与回环），用于“监听地址”下拉。</summary>
    public static List<string> GetLocalIpv4()
    {
        var result = new List<string> { "0.0.0.0", "127.0.0.1" };
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var ip = ua.Address.ToString();
                    if (result.Contains(ip)) continue;
                    if (ip.StartsWith("169.254.", StringComparison.Ordinal)) continue; // 链路本地
                    result.Add(ip);
                }
            }
        }
        catch
        {
            // 忽略枚举失败，至少保留通配与回环
        }
        return result;
    }
}
