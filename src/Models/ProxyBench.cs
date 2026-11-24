using System.Text.Json.Serialization;

namespace CntlmGUI.Models;

/// <summary>
/// 单个上游代理的测速结果。
/// </summary>
public sealed record ProxyBench
{
    public string Line { get; set; } = "";
    public int TcpMs { get; set; } = -1;
    public int RelayMs { get; set; } = -1;
    public int HttpStatus { get; set; }
    public string Detail { get; set; } = "";
    public long SpeedBps { get; set; } = -1;
    public long SpeedBytes { get; set; }
    public long SpeedMs { get; set; }
    public bool SpeedOk { get; set; }
    public string SpeedDetail { get; set; } = "";

    [JsonIgnore]
    public bool TcpOk => TcpMs >= 0;

    [JsonIgnore]
    public bool RelayOk => RelayMs >= 0;

    [JsonIgnore]
    public bool SpeedTested => SpeedBps >= 0;

    [JsonIgnore]
    public string TcpText => TcpOk ? $"{TcpMs} ms" : "不可达";

    [JsonIgnore]
    public string RelayText => RelayOk ? $"{RelayMs} ms" : "-";

    [JsonIgnore]
    public string HttpText => HttpStatus > 0 ? HttpStatus.ToString() : "-";

    [JsonIgnore]
    public string SpeedText => SpeedBps < 0 ? "-" : FormatSpeed(SpeedBps);

    [JsonIgnore]
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (!TcpOk)
            {
                parts.Add(string.IsNullOrWhiteSpace(Detail) ? "TCP 不可达" : Detail);
                return string.Join(" · ", parts);
            }

            if (!RelayOk)
            {
                parts.Add(string.IsNullOrWhiteSpace(Detail) ? "整链不可用" : Detail);
                return string.Join(" · ", parts);
            }

            if (!string.IsNullOrWhiteSpace(Detail)) parts.Add(Detail);
            if (SpeedTested)
            {
                parts.Add(SpeedOk
                    ? $"吞吐 {SpeedText}"
                    : (string.IsNullOrWhiteSpace(SpeedDetail) ? "吞吐失败" : SpeedDetail));
            }

            return string.Join(" · ", parts);
        }
    }

    public static string FormatSpeed(long bytesPerSecond)
    {
        if (bytesPerSecond < 0) return "-";
        if (bytesPerSecond >= 1024L * 1024L) return $"{bytesPerSecond / (1024.0 * 1024.0):0.00} MB/s";
        if (bytesPerSecond >= 1024L) return $"{bytesPerSecond / 1024.0:0.0} KB/s";
        return $"{bytesPerSecond} B/s";
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "-";
        if (bytes >= 1024L * 1024L) return $"{bytes / (1024.0 * 1024.0):0.00} MB";
        if (bytes >= 1024L) return $"{bytes / 1024.0:0.0} KB";
        return $"{bytes} B";
    }
}
