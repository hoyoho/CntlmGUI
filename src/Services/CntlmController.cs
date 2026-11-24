using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using CntlmGUI.Models;

namespace CntlmGUI.Services;

public sealed record StatusInfo(bool Running, string Mode, string Detail);

public sealed class CntlmController
{
    private readonly object _gate = new();
    private Process? _process;
    private CancellationTokenSource? _tailCts;
    private Thread? _tailThread;

    public CntlmController(GuiSettings settings) => Settings = settings;

    public GuiSettings Settings { get; set; }

    public event Action<string>? Log;
    public event Action<string>? CntlmLog;

    public string EffectiveConfigPath
    {
        get
        {
            var settings = Settings;
            if (settings.UseService)
            {
                try
                {
                    var path = CntlmService.ConfigPath(settings.ServiceName);
                    if (!string.IsNullOrWhiteSpace(path)) return path;
                }
                catch
                {
                    // 忽略
                }
            }

            if (!string.IsNullOrWhiteSpace(settings.OutputConfigPath)) return settings.OutputConfigPath;
            return GuiSettings.DefaultOutputConfigPath;
        }
    }

    public bool Validate()
    {
        var settings = Settings;
        if (!settings.UseProxy) return true;

        if (TextLines.Split(settings.ProxyList).Length == 0)
        {
            LogNow("启用对外互联时必须至少配置一个上游代理。");
            return false;
        }

        if (settings.UseSspi) return true;

        if (string.IsNullOrWhiteSpace(settings.Username))
        {
            LogNow("请先在 设置 → 二级代理 → 上游认证 中填写用户名。");
            return false;
        }

        var hasHash = !string.IsNullOrWhiteSpace(settings.PassLM)
                      || !string.IsNullOrWhiteSpace(settings.PassNT)
                      || !string.IsNullOrWhiteSpace(settings.PassNTLMv2);

        if (!hasHash && string.IsNullOrEmpty(settings.Password))
        {
            LogNow("请填写上游认证密码，或使用「生成哈希」写入 PassLM/PassNT/PassNTLMv2。");
            return false;
        }

        if (!string.IsNullOrEmpty(settings.Password)) return true;

        var auth = (settings.Auth ?? "").Trim().ToUpperInvariant();
        switch (auth)
        {
            case "LM":
                if (string.IsNullOrWhiteSpace(settings.PassLM))
                {
                    LogNow("认证类型为 LM 时需要 PassLM。");
                    return false;
                }
                break;
            case "NT":
                if (string.IsNullOrWhiteSpace(settings.PassNT))
                {
                    LogNow("认证类型为 NT 时需要 PassNT。");
                    return false;
                }
                break;
            case "NTLMV2":
                if (string.IsNullOrWhiteSpace(settings.PassNTLMv2))
                {
                    LogNow("认证类型为 NTLMv2 时需要 PassNTLMv2。");
                    return false;
                }
                break;
            case "NTLM":
                if (string.IsNullOrWhiteSpace(settings.PassLM) && string.IsNullOrWhiteSpace(settings.PassNT))
                {
                    LogNow("认证类型为 NTLM 时需要 PassLM 或 PassNT。");
                    return false;
                }
                break;
        }

        return true;
    }

    public async Task StartAsync()
    {
        if (!Validate()) return;

        var exe = Settings.EffectiveBaseCntlmPath();
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
        {
            LogNow("未找到 cntlm.exe，请在 设置 → 运行设置 中指定路径。");
            return;
        }

        var configPath = EffectiveConfigPath;
        if (!WriteConfig(configPath)) return;

        if (Settings.UseService)
        {
            var resolved = CntlmService.ConfigPath(Settings.ServiceName);
            LogNow($"服务模式：实际使用的配置文件为 {configPath}");
            if (resolved == null)
                LogNow("未能解析到服务的实际配置路径，GUI 中的修改可能不生效。");
            LogNow("服务模式不采集 cntlm 的 trace 日志。");

            var result = await Task.Run(() => CntlmService.RunSc($"start \"{Settings.ServiceName}\"")).ConfigureAwait(false);
            foreach (var line in SplitLines(result.Output)) LogNow(line);
            if (result.ExitCode != 0) LogNow($"sc start 返回退出码 {result.ExitCode}。");
            return;
        }

        StartProcess(exe, configPath);
    }

    public async Task StopAsync()
    {
        if (Settings.UseService)
        {
            var result = await Task.Run(() => CntlmService.RunSc($"stop \"{Settings.ServiceName}\"")).ConfigureAwait(false);
            foreach (var line in SplitLines(result.Output)) LogNow(line);
            if (result.ExitCode != 0) LogNow($"sc stop 返回退出码 {result.ExitCode}。");
            return;
        }

        await Task.Run(KillProcess).ConfigureAwait(false);
    }

    public void StopOnExit()
    {
        if (Settings.UseService) return;
        KillProcess();
    }

    public StatusInfo GetStatus()
    {
        var settings = Settings;
        var mode = settings.UseService ? "服务" : "进程";
        var (host, port) = GuiSettings.ParseListen(settings.Listen);
        var listening = ProbePort(host, port);

        if (settings.UseService)
        {
            bool running;
            try
            {
                running = CntlmService.IsRunning(settings.ServiceName);
            }
            catch
            {
                running = false;
            }

            if (!running) return new StatusInfo(false, mode, "服务未运行");
            var prefix = listening ? "端口已监听 · " : "";
            return new StatusInfo(true, mode, $"{prefix}服务正在运行（{host}:{port}）");
        }

        Process? process;
        lock (_gate) process = _process;

        bool alive;
        int pid = 0;
        try
        {
            alive = process != null && !process.HasExited;
            if (alive) pid = process!.Id;
        }
        catch
        {
            alive = false;
        }

        if (!listening && !alive)
            return new StatusInfo(false, mode, $"未监听 {host}:{port}");

        if (!listening)
            return new StatusInfo(false, mode, "宿主进程存活但尚未检测到监听端口");

        var detail = $"监听 {host}:{port}" + (pid > 0 ? $" · PID={pid}" : "");
        return new StatusInfo(true, mode, detail);
    }

    private bool WriteConfig(string path)
    {
        try
        {
            CntlmConfigGenerator.WriteFile(path, Settings);
            LogNow($"已生成 cntlm 配置：{path}");
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            LogNow($"没有权限写入 {path}。服务模式请以管理员身份运行 CntlmGUI。");
            return false;
        }
        catch (Exception ex)
        {
            LogNow($"写入配置失败：{ex.Message}");
            return false;
        }
    }

    private void StartProcess(string exe, string configPath)
    {
        KillProcess();

        var trace = Settings.EnableTrace;
        var tracePath = GuiSettings.TraceLogPath;
        long startOffset = 0;

        if (trace)
        {
            try
            {
                if (File.Exists(tracePath)) File.Delete(tracePath);
            }
            catch
            {
                try
                {
                    startOffset = new FileInfo(tracePath).Exists ? new FileInfo(tracePath).Length : 0;
                }
                catch
                {
                    startOffset = 0;
                }
            }
        }

        var args = new StringBuilder();
        if (trace) args.Append($"-T \"{tracePath}\" ");
        args.Append($"-f -c \"{configPath}\"");

        var psi = new ProcessStartInfo(exe, args.ToString())
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory,
        };

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) CntlmLog?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) CntlmLog?.Invoke(e.Data);
        };

        lock (_gate) _process = process;

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            LogNow($"启动 cntlm 失败：{ex.Message}");
            lock (_gate)
            {
                if (ReferenceEquals(_process, process)) _process = null;
            }
            try
            {
                process.Dispose();
            }
            catch
            {
                // 忽略
            }
            return;
        }

        LogNow($"已启动 cntlm 进程（PID={process.Id}）。");

        if (trace)
        {
            StartTraceTailer(tracePath, startOffset);
        }
        else
        {
            LogNow("详细日志未开启（设置 → 运行 → 记录详细日志）；此处只显示 cntlm 的标准输出。");
        }

        _ = MonitorStartupAsync(process);
    }

    private async Task MonitorStartupAsync(Process process)
    {
        var (host, port) = GuiSettings.ParseListen(Settings.Listen);
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(200).ConfigureAwait(false);

            bool exited;
            try
            {
                exited = process.HasExited;
            }
            catch
            {
                exited = true;
            }

            if (exited)
            {
                var code = -1;
                try
                {
                    code = process.ExitCode;
                }
                catch
                {
                    // 忽略
                }
                LogNow($"cntlm 进程已退出（退出码 {code}），详情见 Cntlm 日志。");
                StopTraceTailer();
                return;
            }

            if (FindListeningPid(port) is int pid)
            {
                LogNow($"cntlm 已启动并在监听 {host}:{port}（PID={pid}）。");
                return;
            }
        }

        LogNow("cntlm 已启动，但暂时未发现监听端口，详情见 Cntlm 日志。");
    }

    public void KillProcess()
    {
        StopTraceTailer();

        var (_, port) = GuiSettings.ParseListen(Settings.Listen);
        var listeningPid = FindListeningPid(port);
        if (listeningPid is int pid)
        {
            var name = GetProcessName(pid);
            if (name.Equals("cntlm", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var killer = Process.Start(new ProcessStartInfo("taskkill", $"/PID {pid} /F /T")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    });
                    killer?.WaitForExit(5000);
                    LogNow($"已结束占用端口 {port} 的 cntlm 进程（PID={pid}）。");
                }
                catch (Exception ex)
                {
                    LogNow($"结束 cntlm 进程失败：{ex.Message}");
                }
            }
            else
            {
                LogNow($"端口 {port} 被 {(name.Length == 0 ? "未知进程" : name)}（PID={pid}）占用，未结束该进程。");
            }
        }

        Process? process;
        lock (_gate)
        {
            process = _process;
            _process = null;
        }

        if (process == null) return;

        try
        {
            if (!process.HasExited) process.Kill(true);
        }
        catch
        {
            // 忽略
        }
        finally
        {
            try
            {
                process.Dispose();
            }
            catch
            {
                // 忽略
            }
        }
    }

    private void LogNow(string message) => Log?.Invoke(message);

    private static IEnumerable<string> SplitLines(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
        return text.Replace("\r\n", "\n").Split('\n')
            .Select(line => line.TrimEnd())
            .Where(line => line.Length > 0);
    }

    private static bool ProbePort(string host, int port)
    {
        try
        {
            using var cts = new CancellationTokenSource(1000);
            using var client = new TcpClient();
            client.ConnectAsync(host, port, cts.Token).GetAwaiter().GetResult();
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static int? FindListeningPid(int port)
    {
        try
        {
            var psi = new ProcessStartInfo("netstat", "-ano -p tcp")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process == null) return null;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(4000);

            foreach (var line in output.Split('\n'))
            {
                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5) continue;
                if (!parts[0].Equals("TCP", StringComparison.OrdinalIgnoreCase)) continue;
                if (!parts[3].Equals("LISTENING", StringComparison.OrdinalIgnoreCase)) continue;

                var local = parts[1];
                var idx = local.LastIndexOf(':');
                if (idx < 0) continue;
                if (!int.TryParse(local[(idx + 1)..], out var localPort) || localPort != port) continue;
                if (int.TryParse(parts[4], out var pid)) return pid;
            }
        }
        catch
        {
            // 忽略
        }

        return null;
    }

    private static string GetProcessName(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch
        {
            return "";
        }
    }

    private void StartTraceTailer(string path, long startOffset)
    {
        StopTraceTailer();
        var cts = new CancellationTokenSource();
        var thread = new Thread(() => TailLoop(path, startOffset, cts.Token)) { IsBackground = true };
        lock (_gate)
        {
            _tailCts = cts;
            _tailThread = thread;
        }
        thread.Start();
    }

    private void StopTraceTailer()
    {
        CancellationTokenSource? cts;
        Thread? thread;
        lock (_gate)
        {
            cts = _tailCts;
            thread = _tailThread;
            _tailCts = null;
            _tailThread = null;
        }

        try
        {
            cts?.Cancel();
        }
        catch
        {
            // 忽略
        }

        if (thread != null && thread != Thread.CurrentThread)
        {
            try
            {
                thread.Join(1500);
            }
            catch
            {
                // 忽略
            }
        }

        cts?.Dispose();
    }

    private void TailLoop(string path, long position, CancellationToken token)
    {
        var pending = "";
        while (!token.IsCancellationRequested)
        {
            position = ReadNewTrace(path, position, ref pending);
            Thread.Sleep(400);
        }

        // 停止前补读一次
        ReadNewTrace(path, position, ref pending, flush: true);
    }

    private long ReadNewTrace(string path, long position, ref string pending, bool flush = false)
    {
        try
        {
            if (!File.Exists(path)) return position;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < position) position = 0;
            if (stream.Length <= position)
            {
                if (flush && pending.Length > 0)
                {
                    CntlmLog?.Invoke(pending);
                    pending = "";
                }
                return position;
            }

            stream.Seek(position, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var chunk = reader.ReadToEnd();
            position = stream.Position;

            var text = pending + chunk;
            var lines = text.Replace("\r\n", "\n").Split('\n');
            pending = lines[^1];
            for (var i = 0; i < lines.Length - 1; i++)
            {
                if (lines[i].Length > 0) CntlmLog?.Invoke(lines[i]);
            }

            if (flush && pending.Length > 0)
            {
                CntlmLog?.Invoke(pending);
                pending = "";
            }
        }
        catch
        {
            // 文件被占用/删除时忽略，下一轮重试
        }

        return position;
    }
}
