using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CntlmGUI.Models;

namespace CntlmGUI.Services;

public enum EngineMode { Process, Service }

public sealed record CntlmStatus(bool Running, string Detail);

/// <summary>
/// 负责把 UI 配置动态写成 cntlm.ini，并以“子进程”或“Windows 服务”方式
/// 启动/停止 cntlm，同时轮询监听端口判断实际运行状态。
/// </summary>
public class CntlmController
{
    private const int ListenWaitMs = 4_000;
    private const int ListenPollMs = 200;

    private readonly GuiSettings _s;
    private Process? _process;
    private CancellationTokenSource? _tailCts;
    private Task? _tailTask;

    public CntlmController(GuiSettings s) => _s = s;

    /// <summary>GUI 自身的操作日志（配置生成、启停动作、错误提示等）。</summary>
    public event Action<string>? Log;

    /// <summary>cntlm 进程自身的输出（stdout/stderr 与 trace 日志内容）。</summary>
    public event Action<string>? CntlmLog;

    /// <summary>当前使用的 cntlm.exe 路径（启动时由 EnsureBinary 确定）。</summary>
    public string Cmd { get; set; } = "";

    public EngineMode Mode => _s.UseService ? EngineMode.Service : EngineMode.Process;

    /// <summary>服务实际读取的 ini 路径；服务不存在或无法解析时为 null。</summary>
    public string? ServiceConfigPath => CntlmService.ConfigPath(_s.ServiceName);

    /// <summary>
    /// 当前模式实际会写入/读取的 ini：
    /// 服务模式优先使用服务自己的 -c 路径（否则服务根本不会读 GUI 的配置）。
    /// </summary>
    public string EffectiveConfigPath =>
        Mode == EngineMode.Service && !string.IsNullOrWhiteSpace(ServiceConfigPath)
            ? ServiceConfigPath!
            : !string.IsNullOrWhiteSpace(_s.OutputConfigPath)
                ? _s.OutputConfigPath
                : GuiSettings.DefaultOutputConfig;

    // ---------------- 配置 ----------------

    /// <summary>写配置；失败时写日志并返回 false（调用方不应继续启动）。</summary>
    public bool WriteConfig()
    {
        var path = Path.GetFullPath(EffectiveConfigPath);
        var dir = Path.GetDirectoryName(path);
        try
        {
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, _s.RenderConfig(), new UTF8Encoding(false)); // cntlm 需要无 BOM 文件
            Log?.Invoke($"已生成并写入配置: {path}");
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            Log?.Invoke($"没有权限写入 {path}。服务模式请以管理员身份运行 CntlmGUI。");
            return false;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"写入配置失败: {path}：{ex.Message}");
            return false;
        }
    }

    public string PreviewConfig() => _s.RenderConfig();

    private bool EnsureBinary()
    {
        var exe = _s.EffectiveBaseCntlmPath();
        if (!string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
        {
            Cmd = exe;
            return true;
        }
        Log?.Invoke("未找到 cntlm.exe，请在 设置 → 运行设置 中指定路径。");
        return false;
    }

    // ---------------- 启动 / 停止 ----------------

    public async Task StartAsync()
    {
        var err = Validate();
        if (err != null)
        {
            Log?.Invoke(err);
            return;
        }
        if (!EnsureBinary()) return;
        if (!WriteConfig()) return;

        if (Mode == EngineMode.Service)
        {
            var cfg = ServiceConfigPath;
            Log?.Invoke(cfg == null
                ? "未找到服务实际使用的配置路径；服务将使用它自己的配置，GUI 的修改可能不会生效。"
                : $"服务模式：使用服务配置 {cfg}");
            Log?.Invoke("服务模式不采集 cntlm 的 trace 日志。");
            await Task.Run(() => RunServiceAction("start", _s.ServiceName));
        }
        else
        {
            await Task.Run(StartProcess);
        }
    }

    /// <summary>启动前置校验：按是否启用上游、是否使用 SSPI、Auth 与哈希是否匹配分别检查。</summary>
    public string? Validate()
    {
        // 未启用对外互联：生成器会写占位 Proxy/凭据，不需要用户配置上游
        if (!_s.UseProxy) return null;

        if (string.IsNullOrWhiteSpace(_s.ProxyList))
            return "已启用对外互联，但未配置上游代理：请填写代理列表，或关闭“启用对外互联”。";
        if (_s.UseSspi) return null; // SSPI 使用 Windows 当前登录身份

        if (string.IsNullOrWhiteSpace(_s.Username))
            return "未填写用户名（设置 → 二级代理 → 上游认证）。";
        if (!TextLines.AnyNonEmpty(_s.PassLM, _s.PassNT, _s.PassNTLMv2) && string.IsNullOrWhiteSpace(_s.Password))
            return "未配置认证凭据（哈希或明文密码），请设置后重试。";
        if (!string.IsNullOrWhiteSpace(_s.Password)) return null;

        bool hasLM = !string.IsNullOrWhiteSpace(_s.PassLM);
        bool hasNT = !string.IsNullOrWhiteSpace(_s.PassNT);
        bool hasV2 = !string.IsNullOrWhiteSpace(_s.PassNTLMv2);
        return _s.Auth.Trim().ToUpperInvariant() switch
        {
            "LM" when !hasLM => "认证类型为 LM，但未填写 PassLM。",
            "NT" when !hasNT => "认证类型为 NT，但未填写 PassNT。",
            "NTLMV2" when !hasV2 => "认证类型为 NTLMv2，但未填写 PassNTLMv2。",
            "NTLM" when !hasLM && !hasNT => "认证类型为 NTLM，但未填写 PassLM 或 PassNT。",
            _ => null,
        };
    }

    public async Task StopAsync()
    {
        if (Mode == EngineMode.Service)
            await Task.Run(() => RunServiceAction("stop", _s.ServiceName));
        else
            await Task.Run(KillProcess);
    }

    // ---------------- 服务模式 ----------------

    private void RunServiceAction(string op, string name)
    {
        Log?.Invoke($"执行 sc {op} {name} ...");
        var (code, output) = RunSc(op, name);
        Log?.Invoke(output.Trim());
        if (code != 0)
            Log?.Invoke("服务操作可能需要管理员权限（右键“以管理员身份运行”）。");
    }

    private static (int code, string output) RunSc(string op, string name)
    {
        var psi = new ProcessStartInfo("sc.exe", $"{op} {name}")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi);
        if (p == null) return (-1, "无法启动 sc.exe");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        Task.WaitAll(stdout, stderr);
        return (p.ExitCode, stdout.Result + stderr.Result);
    }

    private CntlmStatus ServiceStatus()
    {
        var (_, output) = RunSc("query", _s.ServiceName);
        var running = output.IndexOf("RUNNING", StringComparison.OrdinalIgnoreCase) >= 0;
        var detail = $"{_s.ServiceName} 服务 {(running ? "运行中" : "未运行")}";
        if (PortListenCheck()) detail = "端口已监听 · " + detail;
        return new CntlmStatus(running, detail);
    }

    // ---------------- 进程模式 ----------------

    /// <summary>查找监听指定端口的进程 PID；找不到返回 null。</summary>
    private static int? FindListenPid(string host, int port)
    {
        try
        {
            var psi = new ProcessStartInfo("netstat.exe", "-ano -p tcp")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);

            var needle = ":" + port + " ";
            foreach (var line in output.Split('\n'))
            {
                if (line.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (line.IndexOf("LISTENING", StringComparison.OrdinalIgnoreCase) < 0) continue;
                var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length >= 5 && int.TryParse(tokens[^1], out var pid) && pid > 0)
                    return pid;
            }
        }
        catch
        {
            // netstat 失败按“未找到”处理
        }
        return null;
    }

    private static bool IsCntlmProcess(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName.Equals("cntlm", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void StartProcess()
    {
        KillProcess();

        var (host, port) = GuiSettings.ParseListen(_s.Listen);
        var ini = EffectiveConfigPath;
        var trace = GuiSettings.TraceLog;
        var useTrace = _s.EnableTrace;

        // -T 运行时日志只写入 trace 文件（stdout 拿不到），先清掉旧文件；
        // 默认不开启，避免长时间运行时文件持续增长
        long traceStart = 0;
        if (useTrace)
        {
            try { if (File.Exists(trace)) File.Delete(trace); }
            catch
            {
                try { traceStart = new FileInfo(trace).Length; } catch { }
            }
        }

        // -T 必须是第一个参数；-f 前台运行；-c 配置文件
        var psi = new ProcessStartInfo
        {
            FileName = Cmd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(Cmd),
        };
        if (useTrace)
        {
            psi.ArgumentList.Add("-T");
            psi.ArgumentList.Add(trace);
        }
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(ini);
        Log?.Invoke(useTrace
            ? $"启动: {Cmd} -T \"{trace}\" -f -c \"{ini}\""
            : $"启动: {Cmd} -f -c \"{ini}\"（未启用详细日志）");
        try
        {
            _process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            Log?.Invoke("启动失败: " + ex.Message);
            return;
        }

        if (_process == null)
        {
            Log?.Invoke("启动失败: 无法创建进程。");
            return;
        }

        _process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) CntlmLog?.Invoke(e.Data); };
        _process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) CntlmLog?.Invoke(e.Data); };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        // 启用详细日志时才跟踪 trace 文件
        if (useTrace)
        {
            _tailCts = new CancellationTokenSource();
            var token = _tailCts.Token;
            _tailTask = Task.Run(() => TailTraceLoop(trace, traceStart, token));
        }
        else
        {
            Log?.Invoke("详细日志未开启（设置 → 运行 → 记录详细日志）；此处只显示 cntlm 的标准输出。");
        }

        // 等待监听端口就绪，判断是否成功起来
        int? pid = null;
        for (int waited = 0; waited < ListenWaitMs && !_process.HasExited; waited += ListenPollMs)
        {
            Thread.Sleep(ListenPollMs);
            pid = FindListenPid(host, port);
            if (pid != null) break;
        }

        if (_process.HasExited)
        {
            Log?.Invoke($"cntlm 提前退出，退出码 {_process.ExitCode}");
            Thread.Sleep(600); // 给 tailer 一点时间读完最后几行
            StopTraceTail();
            _process = null;
            return;
        }

        pid ??= FindListenPid(host, port);
        Log?.Invoke(pid != null
            ? $"cntlm 已启动 (监听 PID={pid})"
            : "cntlm 已启动，但暂时未发现监听端口，详情见 Cntlm 日志。");
    }

    /// <summary>持续跟踪 cntlm 的 trace 日志文件，把新增行转发到 Cntlm 日志。</summary>
    private void TailTraceLoop(string path, long start, CancellationToken ct)
    {
        long pos = start;
        var leftover = new StringBuilder();

        void ReadPass()
        {
            if (!File.Exists(path)) return;
            // cntlm 持有写句柄，必须以共享方式打开
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < pos) pos = 0; // 文件被重建/清空
            if (fs.Length <= pos) return;
            fs.Seek(pos, SeekOrigin.Begin);
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            pos += ms.Length;
            var text = leftover.ToString() + Encoding.UTF8.GetString(ms.ToArray());
            leftover.Clear();
            var lines = text.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length - 1; i++)
            {
                if (lines[i].Length > 0) CntlmLog?.Invoke(lines[i]);
            }
            if (lines[^1].Length > 0) leftover.Append(lines[^1]); // 半行留到下一轮
        }

        while (true)
        {
            try { ReadPass(); }
            catch (IOException) { /* cntlm 正在写文件，稍后重试 */ }
            catch { break; }

            if (ct.WaitHandle.WaitOne(400))
            {
                try { ReadPass(); } catch { } // 停止前补读一轮，尽量收全最后几行
                break;
            }
        }
    }

    private void StopTraceTail()
    {
        try { _tailCts?.Cancel(); } catch { }
        try { _tailTask?.Wait(1000); } catch { }
        _tailCts?.Dispose();
        _tailCts = null;
        _tailTask = null;
    }

    private void KillProcess()
    {
        StopTraceTail();
        var (host, port) = GuiSettings.ParseListen(_s.Listen);
        var listener = FindListenPid(host, port);
        if (listener != null && IsCntlmProcess(listener.Value))
        {
            Log?.Invoke($"结束监听进程 PID={listener}");
            RunCmd("taskkill", $"/PID {listener} /F /T");
        }
        else if (listener != null)
        {
            Log?.Invoke($"端口 {port} 被其它进程占用（PID={listener}），未结束该进程。");
        }

        if (_process != null)
        {
            if (!_process.HasExited)
            {
                try { _process.Kill(entireProcessTree: true); } catch { }
            }
            _process.Dispose();
            _process = null;
        }
    }

    /// <summary>程序退出时调用：进程模式强杀子进程，服务模式无需处理（服务由系统托管）。</summary>
    public void StopOnExit()
    {
        if (Mode == EngineMode.Process) KillProcess();
    }

    private void RunCmd(string file, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            p.WaitForExit();
            Task.WaitAll(stdout, stderr);
            var output = (stdout.Result + stderr.Result).Trim();
            if (output.Length > 0) Log?.Invoke(output);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"{file} 执行失败: {ex.Message}");
        }
    }

    // ---------------- 状态 ----------------

    public CntlmStatus GetStatus() =>
        Mode == EngineMode.Service ? ServiceStatus() : ProcessStatus();

    private CntlmStatus ProcessStatus()
    {
        var (host, port) = GuiSettings.ParseListen(_s.Listen);
        var listening = PortListenCheck();

        // GetStatus 可能在后台线程被调用，与 KillProcess 的 Dispose 并发，
        // HasExited 在已释放的进程对象上会抛异常，这里兜底
        Process? own = _process;
        bool ownAlive;
        try { ownAlive = own != null && !own.HasExited; }
        catch { ownAlive = false; }

        if (!listening && !ownAlive) return new CntlmStatus(false, "未运行");

        var listenShown = $"{host}:{port}";
        if (!listening) return new CntlmStatus(true, "宿主进程存活但尚未检测到监听端口");

        int? pid = null;
        try { if (ownAlive) pid = own!.Id; } catch { }
        return new CntlmStatus(true, pid != null
            ? $"运行中 · 监听 {listenShown} · PID={pid}"
            : $"运行中 · 监听 {listenShown}");
    }

    /// <summary>快速检查监听端口是否可连接（不启动外部进程）。</summary>
    private bool PortListenCheck()
    {
        var (host, port) = GuiSettings.ParseListen(_s.Listen);
        try
        {
            using var c = new TcpClient();
            c.ConnectAsync(host, port).WaitAsync(TimeSpan.FromMilliseconds(1000)).GetAwaiter().GetResult();
            return c.Connected;
        }
        catch
        {
            return false;
        }
    }
}
