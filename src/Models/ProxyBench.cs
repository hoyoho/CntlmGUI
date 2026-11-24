using System.Text.Json.Serialization;

namespace CntlmGUI.Models;

/// <summary>
/// 单个候选代理的一次测速结果。
/// 同时作为“上次结果”的持久化结构：只保留原始字段，显示文本都由这些字段派生。
/// </summary>
public sealed record ProxyBench(string Line)
{
    /// <summary>TCP 连接耗时；-1 表示不可达。</summary>
    public int TcpMs { get; init; } = -1;

    /// <summary>经临时 cntlm 加载目标的耗时；-1 表示整链失败。</summary>
    public int RelayMs { get; init; } = -1;

    public int HttpStatus { get; init; }
    public string Detail { get; init; } = "";

    // 吞吐测试（未启用时 SpeedBps = -1）
    public long SpeedBps { get; init; } = -1;
    public long SpeedBytes { get; init; }
    public int SpeedMs { get; init; }

    /// <summary>测量是否有效：下满上限、完整响应、或到达时间上限但已有数据。</summary>
    public bool SpeedOk { get; init; }
    public string SpeedDetail { get; init; } = "";

    [JsonIgnore] public bool TcpOk => TcpMs >= 0;
    [JsonIgnore] public bool RelayOk => RelayMs >= 0;
    [JsonIgnore] public bool SpeedTested => SpeedBps >= 0;

    [JsonIgnore] public string TcpText => TcpOk ? $"{TcpMs} ms" : "不可达";
    [JsonIgnore] public string RelayText => RelayOk ? $"{RelayMs} ms" : "失败";
    [JsonIgnore] public string SpeedText => SpeedTested && SpeedOk ? FormatSpeed(SpeedBps) : "—";

    /// <summary>“说明”列文本：整链失败看 Detail，否则看吞吐说明。</summary>
    [JsonIgnore]
    public string Summary => !RelayOk ? Detail : SpeedTested ? SpeedDetail : "";

    public static string FormatSpeed(long bps) =>
        bps >= 1048576 ? $"{bps / 1048576.0:0.00} MB/s"
        : bps >= 1024 ? $"{bps / 1024.0:0.0} KB/s"
        : $"{bps} B/s";

    public static string FormatBytes(long bytes) =>
        bytes >= 1048576 ? $"{bytes / 1048576.0:0.0} MB"
        : bytes >= 1024 ? $"{bytes / 1024.0:0.0} KB"
        : $"{bytes} B";
}
