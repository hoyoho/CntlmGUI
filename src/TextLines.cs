using System;
using System.Collections.Generic;
using System.Linq;

namespace CntlmGUI;

/// <summary>
/// 配置里大量使用“每行一条”的文本格式，这里统一拆分规则：
/// 去掉行尾 \r、首尾空白，忽略空行。
/// </summary>
public static class TextLines
{
    public static List<string> Split(string? text)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return list;
        foreach (var line in text.Split('\n'))
        {
            var t = line.Trim('\r', ' ', '\t');
            if (t.Length > 0) list.Add(t);
        }
        return list;
    }

    public static bool AnyNonEmpty(params string?[] values) =>
        values.Any(v => !string.IsNullOrWhiteSpace(v));
}
