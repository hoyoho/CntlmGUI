using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CntlmGUI.Models;

namespace CntlmGUI.Services;

/// <summary>
/// 对每个候选父代理（Proxy 列表中的一行）做对比测速：
/// 1) 与代理主机直连的 TCP 延迟；2) 临时拉起独立 cntlm 进程，
/// 用真实账号/哈希经该代理实际加载“待访问地址”并计时（整链测速）；
/// 3) 可选：经同一实例下载一段固定大小的数据，测持续吞吐。
///
/// 约束：
/// - 顺序执行，避免多个代理互相干扰；
/// - 吞吐测试用 Range 限制大小，下满大小或到达时限即停止，按已下载字节计算速度；
/// - 下载数据只读入内存缓冲并丢弃，不写磁盘；临时 ini/trace 在结束时删除。
/// </summary>
public sealed class ProxySpeedTest
{
    private const int RelayTimeoutMs = 12_000;
    private const int TcpTimeoutMs = 3_000;
    private const int ReadyTimeoutMs = 300;
    private const int ReadyAttempts = 40;
    private const int ReadyPollMs = 150;

    private readonly GuiSettings _s;
    private readonly string _workDir = Path.Combine(GuiSettings.AppDataDir, "bench");
    private readonly string _exe;

    public ProxySpeedTest(GuiSettings s)
    {
        _s = s;
        Directory.CreateDirectory(_workDir);
        _exe = s.EffectiveBaseCntlmPath() ?? "";
        CleanupOldBenchFiles();
    }

    public bool Ready => File.Exists(_exe);

    // ---------------- 入口 ----------------

    /// <summary>URL 缺协议时自动补 https://，避免 HttpClient 因无 scheme 直接抛异常。</summary>
    public static string NormalizeUrl(string url)
    {
        var t = (url ?? "").Trim();
        if (t.Length == 0) return t;
        return t.IndexOf("://", StringComparison.Ordinal) >= 0 ? t : "https://" + t;
    }

    /// <summary>
    /// 顺序测速：逐个代理执行，每完成一条通过 progress 回报完整结果。
    /// probeUrl 非空时追加吞吐测试。
    /// </summary>
    public async Task<List<ProxyBench>> BenchAsync(string targetUrl, string? probeUrl,
        long maxBytes, int timeoutSec, IProgress<ProxyBench>? progress = null, CancellationToken ct = default)
    {
        targetUrl = NormalizeUrl(targetUrl);
        probeUrl = string.IsNullOrWhiteSpace(probeUrl) ? null : NormalizeUrl(probeUrl);

        var list = ParseCandidates();
        if (list.Count == 0) return new List<ProxyBench>();

        var results = new List<ProxyBench>(list.Count);
        foreach (var (host, port) in list)
        {
            ct.ThrowIfCancellationRequested();
            var r = await BenchOneAsync(host, port, targetUrl, probeUrl, maxBytes, timeoutSec, ct)
                .ConfigureAwait(false);
            results.Add(r);
            progress?.Report(r);
        }
        return results;
    }

    /// <summary>从 ProxyList（每行 ip:port）解析候选代理。</summary>
    private List<(string host, int port)> ParseCandidates() =>
        TextLines.Split(_s.ProxyList)
            .Select(ParseHostPort)
            .Where(x => x != null)
            .Select(x => x!.Value)
            .ToList();

    private static (string host, int port)? ParseHostPort(string line)
    {
        int idx = line.LastIndexOf(':');
        if (idx <= 0) return null;
        var host = line[..idx].Trim();
        var portText = line[(idx + 1)..].Trim();
        if (host.Length == 0 || !int.TryParse(portText, out var port) || port is <= 0 or > 65535) return null;
        return (host, port);
    }

    private async Task<ProxyBench> BenchOneAsync(string host, int port, string target,
        string? probeUrl, long maxBytes, int timeoutSec, CancellationToken ct)
    {
        var line = $"{host}:{port}";
        var tcpMs = await MeasureTcpAsync(host, port, ct);
        if (tcpMs < 0)
            return new ProxyBench(line) { Detail = "TCP 不可达" };

        var listen = AllocLocalPort();
        var ini = Path.Combine(_workDir, $"bench-{listen}.ini");
        var trace = Path.Combine(_workDir, $"bench-{listen}.log");
        Process? proc = null;
        try
        {
            // 只保留该候选代理 + 本机临时监听端口的配置（认证方式与主配置保持一致）
            var tmp = new GuiSettings
            {
                Username = _s.Username,
                Domain = _s.Domain,
                Workstation = _s.Workstation,
                Password = _s.Password,
                PassLM = _s.PassLM,
                PassNT = _s.PassNT,
                PassNTLMv2 = _s.PassNTLMv2,
                Auth = _s.Auth,
                Flags = _s.Flags,
                UseSspi = _s.UseSspi,
                SspiType = _s.SspiType,
                ProxyList = $"{host}:{port}",
                UseProxy = true,
                Listen = $"127.0.0.1:{listen}",
                NoProxyList = "",
            };
            File.WriteAllText(ini, CntlmConfigGenerator.Generate(tmp), new UTF8Encoding(false));

            proc = StartCntlm(ini, trace);
            if (proc == null)
                return new ProxyBench(line) { TcpMs = tcpMs, Detail = "无法启动 cntlm 测试进程" };

            var readyErr = await WaitReadyAsync(proc, listen, ct);
            if (readyErr != null)
                return new ProxyBench(line) { TcpMs = tcpMs, Detail = readyErr };

            // 1) 整链：加载目标地址（只读到响应头）
            var (ok, ms, status, err) = await RelayOnceAsync(listen, target, ct);
            if (!ok)
                return new ProxyBench(line) { TcpMs = tcpMs, Detail = err };

            var result = new ProxyBench(line)
            {
                TcpMs = tcpMs,
                RelayMs = ms,
                HttpStatus = status,
                Detail = $"HTTP {status}",
            };

            // 2) 吞吐：同一 cntlm 实例，Range 限大小
            if (probeUrl != null && maxBytes > 0)
            {
                var (speedOk, bps, bytes, speedMs, speedErr) =
                    await ThroughputOnceAsync(listen, probeUrl, maxBytes, timeoutSec, ct);
                result = result with
                {
                    SpeedBps = bps,
                    SpeedBytes = bytes,
                    SpeedMs = speedMs,
                    SpeedOk = speedOk,
                    SpeedDetail = speedErr,
                };
            }
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ProxyBench(line) { TcpMs = tcpMs, Detail = Short((ex.InnerException ?? ex).Message) };
        }
        finally
        {
            if (proc != null)
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                proc.Dispose();
            }
            // 不留任何测试残留：ini 与 trace 都删除
            try { if (File.Exists(ini)) File.Delete(ini); } catch { }
            try { if (File.Exists(trace)) File.Delete(trace); } catch { }
        }
    }

    private Process? StartCntlm(string ini, string trace)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(_exe),
        };
        psi.ArgumentList.Add("-T");
        psi.ArgumentList.Add(trace);
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(ini);
        return Process.Start(psi);
    }

    private static async Task<string?> WaitReadyAsync(Process proc, int listen, CancellationToken ct)
    {
        for (int i = 0; i < ReadyAttempts; i++)
        {
            if (proc.HasExited)
                return $"cntlm 提前退出（码 {proc.ExitCode}）";
            if (await MeasureTcpAsync("127.0.0.1", listen, CancellationToken.None, ReadyTimeoutMs) >= 0)
                return null;
            await Task.Delay(ReadyPollMs, ct);
        }
        return "测试实例未就绪：端口未监听";
    }

    // ---------------- TCP 延迟 ----------------

    private static async Task<int> MeasureTcpAsync(string host, int port, CancellationToken ct, int timeoutMs = TcpTimeoutMs)
    {
        var sw = Stopwatch.StartNew();
        using var client = new TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try
        {
            await client.ConnectAsync(host, port, cts.Token);
            sw.Stop();
            return (int)sw.ElapsedMilliseconds;
        }
        catch
        {
            return -1;
        }
    }

    // ---------------- 整链实测（临时 cntlm）----------------

    private async Task<(bool ok, int ms, int status, string err)> RelayOnceAsync(
        int listen, string target, CancellationToken ct)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            using var handler = new HttpClientHandler
            {
                Proxy = new WebProxy("127.0.0.1", listen),
                UseProxy = true,
            };
            using var client = new HttpClient(handler);
            using var cts = new CancellationTokenSource(RelayTimeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);
            using var resp = await client.SendAsync(
                new HttpRequestMessage(HttpMethod.Get, target),
                HttpCompletionOption.ResponseHeadersRead,
                linked.Token);
            sw.Stop();
            return (true, (int)sw.ElapsedMilliseconds, (int)resp.StatusCode, "");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, -1, 0, $"超时：{RelayTimeoutMs / 1000}s 内未完成");
        }
        catch (Exception ex)
        {
            return (false, -1, 0, Short((ex.InnerException ?? ex).Message));
        }
    }

    /// <summary>
    /// 经指定 cntlm 实例下载最多 maxBytes 字节，统计平均速度。
    /// 用 Range 限制大小；服务器忽略 Range 时也只读到 maxBytes 为止。
    /// 下满 maxBytes 或到达 timeoutSec，先到哪个就停止，按已下载字节计算速度；
    /// 只有“一个字节都没收到”或 HTTP 错误才算失败。
    /// 数据只进内存缓冲，不落盘。
    /// </summary>
    private static async Task<(bool ok, long bps, long bytes, int ms, string err)> ThroughputOnceAsync(
        int listen, string url, long maxBytes, int timeoutSec, CancellationToken ct)
    {
        using var handler = new HttpClientHandler
        {
            Proxy = new WebProxy("127.0.0.1", listen),
            UseProxy = true,
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, timeoutSec)));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Range = new RangeHeaderValue(0, Math.Max(0, maxBytes - 1));
        req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

        var sw = Stopwatch.StartNew();
        long total = 0;
        try
        {
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            if (!resp.IsSuccessStatusCode)
            {
                sw.Stop();
                return (false, 0, 0, (int)sw.ElapsedMilliseconds, $"HTTP {(int)resp.StatusCode}");
            }

            using var stream = await resp.Content.ReadAsStreamAsync(linked.Token);
            var buffer = new byte[81920];
            bool eof = false;
            while (total < maxBytes)
            {
                int toRead = (int)Math.Min(buffer.Length, maxBytes - total);
                int n = await stream.ReadAsync(buffer.AsMemory(0, toRead), linked.Token);
                if (n <= 0) { eof = true; break; }
                total += n;
            }

            sw.Stop();
            var bps = sw.Elapsed.TotalSeconds > 0 ? (long)(total / sw.Elapsed.TotalSeconds) : 0;
            // 读满上限，或目标本身就这么大（完整响应）都算有效测量
            var ok = total >= maxBytes || eof;
            return (ok, bps, total, (int)sw.ElapsedMilliseconds,
                total >= maxBytes ? "" : $"完整响应：{ProxyBench.FormatBytes(total)}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            var bps = sw.Elapsed.TotalSeconds > 0 ? (long)(total / sw.Elapsed.TotalSeconds) : 0;
            // 时间到：已经有数据就是一次有效测量（按已下载字节算速度）；一个字节都没收到才算失败
            var ok = total > 0;
            return (ok, bps, total, (int)sw.ElapsedMilliseconds,
                ok
                    ? $"达到 {timeoutSec}s 上限（已下载 {ProxyBench.FormatBytes(total)}）"
                    : $"超时：{timeoutSec}s 内未收到数据");
        }
        catch (Exception ex)
        {
            sw.Stop();
            return (false, 0, total, (int)sw.ElapsedMilliseconds, Short((ex.InnerException ?? ex).Message));
        }
    }

    private static string Short(string s)
    {
        int nl = s.IndexOf('\n');
        if (nl >= 0) s = s[..nl];
        return s.Length > 160 ? s[..160] : s;
    }

    private static int AllocLocalPort()
    {
        using var t = new TcpListener(IPAddress.Loopback, 0);
        t.Start();
        var port = ((IPEndPoint)t.LocalEndpoint).Port;
        t.Stop();
        return port;
    }

    /// <summary>清理上次异常退出遗留的临时文件（正常结束时都会在 finally 删除）。</summary>
    private void CleanupOldBenchFiles()
    {
        try
        {
            foreach (var f in Directory.GetFiles(_workDir, "bench-*"))
            {
                try { if (DateTime.Now - File.GetLastWriteTime(f) > TimeSpan.FromHours(6)) File.Delete(f); }
                catch { }
            }
        }
        catch { }
    }
}
