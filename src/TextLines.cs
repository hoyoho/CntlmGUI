namespace CntlmGUI;

internal static class TextLines
{
    public static string[] Split(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
        return text
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();
    }

    public static string Join(IEnumerable<string> lines) => string.Join("\r\n", lines);

    public static string JoinComma(string? text) => string.Join(", ", Split(text));
}
