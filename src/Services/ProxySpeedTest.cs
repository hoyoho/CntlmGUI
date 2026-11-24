using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using CntlmGUI.Models;

namespace CntlmGUI.Services;

/// <summary>
/// 顺序测试上游代理：TCP 延迟 → 整链 HTTP → 吞吐。
/// </summary>
public sealed class ProxySpeedTest
{
    private const int RelayTimeoutSeconds = 12;
    private const int TcpTimeoutMs = 3000;

    private readonly GuiSettings _settings;
    private readonly string _url;
    private readonly long _maxBytes;
    private readonly int _speedTimeoutSec;
    private readonly string _exe;

    public ProxySpeedTest(GuiSettings settings, string url, long maxBytes, int timeoutSec)
    {
        _settings = settings;
        _url = NormalizeUrl(url);
        _maxBytes = Math.Max(0, maxBytes);
        _speedTimeoutSec = Math.Max(1, timeoutSec);
        _exe = settings.EffectiveBaseCntlmPath();
    }

    public static string NormalizeUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        var text = url.Trim();
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        return text;
    }

    public async Task RunAsync(IProgress<ProxyBench> progress, CancellationToken cancellationToken)
    {
        var benchDir = Path.Combine(GuiSettings.ConfigDir, "bench");
        CleanupOldFiles(benchDir);

        foreach (var line in TextLines.Split(_settings.ProxyList))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await TestOneAsync(line, progress, cancellationToken).ConfigureAwait(false);
            progress.Report(result);
        }
    }

    private async Task<ProxyBench> TestOneAsync(string line, IProgress<ProxyBench> progress,
        CancellationToken cancellationToken)
    {
        var bench = new ProxyBench { Line = line };

        var target = ParseProxy(line);
        if (target == null)
        {
            bench.TcpMs = -1;
            bench.Detail = "代理格式无效";
            return bench;
        }

        var (host, port) = target.Value;
        bench.TcpMs = await TcpLatencyAsync(host, port, cancellationToken).ConfigureAwait(false);
        if (bench.TcpMs < 0)
        {
            bench.Detail = "TCP 不可达";
            return bench;
        }

        if (string.IsNullOrWhiteSpace(_exe) || !File.Exists(_exe))
        {
            bench.RelayMs = -1;
            bench.Detail = "未找到 cntlm.exe";
            return bench;
        }

        var benchDir = Path.Combine(GuiSettings.ConfigDir, "bench");
        Directory.CreateDirectory(benchDir);

        var localPort = GetFreePort();
        var iniPath = Path.Combine(benchDir, $"bench-{localPort}.ini");
        var logPath = Path.Combine(benchDir, $"bench-{localPort}.log");
        Process? process = null;

        try
        {
            File.WriteAllText(iniPath, BuildBenchConfig(line, localPort), new UTF8Encoding(false));

            var psi = new ProcessStartInfo(_exe, $"-T \"{logPath}\" -f -c \"{iniPath}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(_exe) ?? AppContext.BaseDirectory,
            };

            process = new Process { StartInfo = psi };
            process.OutputDataReceived += (_, _) => { };
            process.ErrorDataReceived += (_, _) => { };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var ready = false;
            for (var i = 0; i < 40; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (process.HasExited) break;
                if (Probe(localPort))
                {
                    ready = true;
                    break;
                }
                await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            }

            if (!ready)
            {
                bench.RelayMs = -1;
                bench.Detail = process.HasExited ? "临时 cntlm 实例启动失败" : "临时 cntlm 实例未监听端口";
                return bench;
            }

            using var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{localPort}"),
                UseProxy = true,
                AllowAutoRedirect = true,
            };
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

            var relayOk = await RelayTestAsync(client, bench, cancellationToken).ConfigureAwait(false);
            progress.Report(Clone(bench));

            if (relayOk && _url.Length > 0 && _maxBytes > 0)
            {
                await SpeedTestAsync(client, bench, cancellationToken).ConfigureAwait(false);
            }

            return bench;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            bench.RelayMs = -1;
            if (string.IsNullOrWhiteSpace(bench.Detail)) bench.Detail = ex.Message;
            return bench;
        }
        finally
        {
            if (process != null)
            {
                try
                {
                    if (!process.HasExited) process.Kill(true);
                }
                catch
                {
                    // 忽略
                }
                try
                {
                    process.Dispose();
                }
                catch
                {
                    // 忽略
                }
            }

            TryDelete(iniPath);
            TryDelete(logPath);
        }
    }

    private async Task<bool> RelayTestAsync(HttpClient client, ProxyBench bench, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(RelayTimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        var sw = Stopwatch.StartNew();
        try
        {
            using var response = await client
                .GetAsync(_url, HttpCompletionOption.ResponseHeadersRead, linked.Token)
                .ConfigureAwait(false);
            sw.Stop();
            bench.HttpStatus = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                bench.RelayMs = -1;
                bench.Detail = $"HTTP {(int)response.StatusCode}";
                return false;
            }

            bench.RelayMs = (int)sw.ElapsedMilliseconds;
            bench.Detail = $"HTTP {(int)response.StatusCode}";
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            bench.RelayMs = -1;
            bench.Detail = "整链请求超时";
            return false;
        }
        catch (Exception ex)
        {
            bench.RelayMs = -1;
            bench.Detail = $"整链请求失败：{ex.Message}";
            return false;
        }
    }

    private async Task SpeedTestAsync(HttpClient client, ProxyBench bench, CancellationToken cancellationToken)
    {
        long total = 0;
        var completed = false;
        var timedOut = false;
        var httpOk = true;
        var detail = "";

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(_speedTimeoutSec));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        var sw = Stopwatch.StartNew();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _url);
            request.Headers.Range = new RangeHeaderValue(0, _maxBytes - 1);
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                httpOk = false;
                detail = $"HTTP {(int)response.StatusCode}";
            }
            else
            {
                using var stream = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
                var buffer = new byte[80 * 1024];
                while (total < _maxBytes)
                {
                    int read;
                    try
                    {
                        read = await stream.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        timedOut = true;
                        break;
                    }

                    if (read <= 0)
                    {
                        completed = true;
                        break;
                    }

                    total += read;
                    if (total >= _maxBytes)
                    {
                        completed = true;
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
        }
        catch (Exception ex)
        {
            httpOk = false;
            detail = ex.Message;
        }

        sw.Stop();
        var elapsedMs = Math.Max(1, sw.ElapsedMilliseconds);

        bench.SpeedBytes = total;
        bench.SpeedMs = elapsedMs;

        if (!httpOk)
        {
            bench.SpeedBps = -1;
            bench.SpeedOk = false;
            bench.SpeedDetail = string.IsNullOrWhiteSpace(detail) ? "吞吐测试失败" : detail;
            return;
        }

        var valid = total > 0 && (completed || timedOut);
        if (!valid)
        {
            bench.SpeedBps = -1;
            bench.SpeedOk = false;
            bench.SpeedDetail = "未收到任何数据";
            return;
        }

        bench.SpeedBps = total * 1000 / elapsedMs;
        bench.SpeedOk = true;
        bench.SpeedDetail = timedOut
            ? $"已下载 {ProxyBench.FormatBytes(total)}（超时截断）"
            : $"已下载 {ProxyBench.FormatBytes(total)}";
    }

    private string BuildBenchConfig(string proxy, int listenPort)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# CntlmGUI bench configuration (temporary)");
        sb.AppendLine();

        var hasHashes = !string.IsNullOrWhiteSpace(_settings.PassLM)
                        || !string.IsNullOrWhiteSpace(_settings.PassNT)
                        || !string.IsNullOrWhiteSpace(_settings.PassNTLMv2);
        var usable = !string.IsNullOrWhiteSpace(_settings.Username)
                     && (_settings.UseSspi || hasHashes || !string.IsNullOrEmpty(_settings.Password));

        if (usable)
        {
            if (!string.IsNullOrWhiteSpace(_settings.Username)) sb.AppendLine("Username " + _settings.Username.Trim());
            if (!string.IsNullOrWhiteSpace(_settings.Domain)) sb.AppendLine("Domain " + _settings.Domain.Trim());
            if (!string.IsNullOrWhiteSpace(_settings.Workstation)) sb.AppendLine("Workstation " + _settings.Workstation.Trim());
            if (hasHashes)
            {
                if (!string.IsNullOrWhiteSpace(_settings.PassLM)) sb.AppendLine("PassLM " + _settings.PassLM.Trim());
                if (!string.IsNullOrWhiteSpace(_settings.PassNT)) sb.AppendLine("PassNT " + _settings.PassNT.Trim());
                if (!string.IsNullOrWhiteSpace(_settings.PassNTLMv2)) sb.AppendLine("PassNTLMv2 " + _settings.PassNTLMv2.Trim());
            }
            else if (!string.IsNullOrEmpty(_settings.Password))
            {
                sb.AppendLine("Password \"" + _settings.Password.Replace("\"", "\\\"") + "\"");
            }

            if (!string.IsNullOrWhiteSpace(_settings.Auth)) sb.AppendLine("Auth " + _settings.Auth.Trim());
            if (!string.IsNullOrWhiteSpace(_settings.Flags))
            {
                var flags = _settings.Flags.Trim();
                if (!flags.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) flags = "0x" + flags;
                sb.AppendLine("Flags " + flags);
            }
            if (_settings.UseSspi)
                sb.AppendLine("SSPI " + (string.IsNullOrWhiteSpace(_settings.SspiType) ? "NTLM" : _settings.SspiType.Trim()));
        }
        else
        {
            sb.AppendLine("Username    cntlmgui");
            sb.AppendLine("PassNT      77B9081511704EE852F94227CF48A793");
            sb.AppendLine("Auth        NT");
        }

        sb.AppendLine();
        sb.AppendLine("Proxy " + proxy);
        sb.AppendLine($"Listen 127.0.0.1:{listenPort}");
        return sb.ToString();
    }

    /// <summary>稳定排序：整链可用 → 吞吐有效 → 速度降序 → 整链耗时升序 → TCP 延迟升序。</summary>
    public static List<string> SortProxies(IEnumerable<string> proxies, IEnumerable<ProxyBench> results)
    {
        var map = new Dictionary<string, ProxyBench>(StringComparer.OrdinalIgnoreCase);
        foreach (var result in results)
        {
            map[result.Line] = result;
        }

        return proxies
            .Select(line => (Line: line, Bench: map.TryGetValue(line, out var found) ? found : null))
            .OrderByDescending(item => item.Bench?.RelayOk == true)
            .ThenByDescending(item => item.Bench?.SpeedOk == true)
            .ThenByDescending(item => item.Bench?.SpeedBps ?? long.MinValue)
            .ThenBy(item => item.Bench?.RelayOk == true ? item.Bench.RelayMs : int.MaxValue)
            .ThenBy(item => item.Bench?.TcpOk == true ? item.Bench.TcpMs : int.MaxValue)
            .Select(item => item.Line)
            .ToList();
    }

    private static ProxyBench Clone(ProxyBench source) => source with { };

    private static async Task<int> TcpLatencyAsync(string host, int port, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(TcpTimeoutMs);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        var sw = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, linked.Token).ConfigureAwait(false);
            sw.Stop();
            return (int)sw.ElapsedMilliseconds;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return -1;
        }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static bool Probe(int port)
    {
        try
        {
            using var cts = new CancellationTokenSource(500);
            using var client = new TcpClient();
            client.ConnectAsync(IPAddress.Loopback, port, cts.Token).GetAwaiter().GetResult();
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static (string Host, int Port)? ParseProxy(string line)
    {
        var text = line.Trim();
        var at = text.LastIndexOf('@');
        if (at >= 0) text = text[(at + 1)..];

        var idx = text.LastIndexOf(':');
        if (idx <= 0) return null;

        var host = text[..idx].Trim().Trim('[', ']');
        if (host.Length == 0) return null;
        if (!int.TryParse(text[(idx + 1)..].Trim(), out var port)) return null;
        if (port is < 1 or > 65535) return null;
        return (host, port);
    }

    private static void CleanupOldFiles(string benchDir)
    {
        try
        {
            if (!Directory.Exists(benchDir)) return;
            foreach (var file in Directory.GetFiles(benchDir))
            {
                try
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromHours(6))
                        File.Delete(file);
                }
                catch
                {
                    // 忽略
                }
            }
        }
        catch
        {
            // 忽略
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 忽略
        }
    }
}
