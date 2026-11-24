namespace CntlmGUI.Services;

internal static class Detect
{
    public static IEnumerable<string> KnownCntlmPaths()
    {
        var paths = new List<string>();

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        paths.Add(Path.Combine(AppContext.BaseDirectory, "cntlm.exe"));
        if (!string.IsNullOrEmpty(programFilesX86))
            paths.Add(Path.Combine(programFilesX86, "Cntlm", "cntlm.exe"));
        if (!string.IsNullOrEmpty(programFiles))
            paths.Add(Path.Combine(programFiles, "Cntlm", "cntlm.exe"));

        return paths;
    }

    public static IEnumerable<string> KnownIniPaths()
    {
        var paths = new List<string>();
        paths.Add(@"C:\Program Files (x86)\Cntlm\cntlm.ini");
        paths.Add(@"C:\Program Files\Cntlm\cntlm.ini");
        return paths;
    }

    public static string? FindCntlm()
    {
        foreach (var path in KnownCntlmPaths())
        {
            try
            {
                if (File.Exists(path)) return path;
            }
            catch
            {
                // 忽略无效路径
            }
        }

        var searchPath = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in searchPath.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), "cntlm.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // 忽略非法 PATH 项
            }
        }

        return null;
    }
}
