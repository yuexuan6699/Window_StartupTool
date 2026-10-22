using StartupTool.Core.Models;

namespace StartupTool.Core.Processes;

/// <summary>启动命令（解析后的可执行文件 + 命令行参数串）。</summary>
public readonly record struct LaunchCommand(string FileName, string Arguments);

/// <summary>
/// 按脚本类型构造进程启动命令（修复 B7）。
/// 旧版 cmd /c 只加单层引号，路径含空格时被 cmd 的引号剥离规则坑掉；
/// 统一规则：可执行文件经 lpApplicationName 直传，参数串中路径始终双引号包裹；
/// cmd /c 使用双层引号形式 <c>/c ""path" args"</c>。
/// </summary>
public static class CommandLineBuilder
{
    public static LaunchCommand Build(ScriptConfig script)
    {
        var path = script.Path;
        var userArgs = script.Arguments ?? string.Empty;
        var ext = Path.GetExtension(path).ToLowerInvariant();

        return ext switch
        {
            ".ps1" => new LaunchCommand(
                ResolveExecutable("powershell.exe"),
                JoinArgs("-NoProfile -NonInteractive -ExecutionPolicy Bypass -File", Quote(path), userArgs)),

            ".bat" or ".cmd" => new LaunchCommand(
                ResolveExecutable("cmd.exe"),
                BuildCmdArguments(path, userArgs)),

            // Python 脚本：.py / .pyw 不是可直接 CreateProcess 的可执行文件，
            // 必须经解释器执行。优先用脚本级指定的解释器（典型场景：虚拟环境 Scripts\python.exe），
            // 未指定时才回退系统 py.exe / python.exe。
            ".py" or ".pyw" => new LaunchCommand(
                ResolvePythonInterpreter(script.PythonExecutable),
                JoinArgs(Quote(path), userArgs)),

            _ => new LaunchCommand(ResolveExecutable(path), userArgs)
        };
    }

    /// <summary>
    /// 解析 Python 解释器：脚本显式指定了 <c>PythonExecutable</c> 就原样采用（虚拟环境场景，
    /// 名字任意，不做自动探测也不静默回退——回退会让"以为用了 venv"的误配更难发现）；
    /// 未指定时按 Windows 官方 py launcher（py.exe）→ python.exe → 裸文件名兜底，
    /// 后两者都缺失时由 <see cref="ProcessLauncher"/> 报错并写日志。
    /// </summary>
    internal static string ResolvePythonInterpreter(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();

        var launcher = ResolveExecutable("py.exe");
        if (Path.IsPathRooted(launcher)) return launcher;
        return ResolveExecutable("python.exe");
    }

    /// <summary>
    /// cmd.exe /c 的双层引号写法：
    /// <c>/c ""C:\dir with space\s.bat" arg1</c>；无参数时 <c>/c ""C:\dir with space\s.bat""</c>。
    /// </summary>
    internal static string BuildCmdArguments(string batchPath, string userArgs)
    {
        var inner = Quote(batchPath);
        return string.IsNullOrWhiteSpace(userArgs)
            ? $"/c \"\"{batchPath}\"\""
            : $"/c \"\"{batchPath}\" {userArgs}\"";
    }

    /// <summary>合并固定参数与用户参数（去掉多余空白）。</summary>
    private static string JoinArgs(params string[] parts)
        => string.Join(' ', parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    /// <summary>路径/参数整体加双引号；已含双引号时做反斜杠转义。</summary>
    public static string Quote(string value)
    {
        if (value.Length > 0 && value[0] == '"' && value[^1] == '"') return value;
        var escaped = value.Replace("\"", "\\\"");
        return $"\"{escaped}\"";
    }

    /// <summary>
    /// 把可能只给了文件名（cmd.exe/powershell.exe）或相对名的可执行文件解析为完整路径。
    /// 顺序：原路径存在 → System32 → PowerShell v1.0 目录 → PATH。
    /// </summary>
    public static string ResolveExecutable(string fileName)
    {
        if (Path.IsPathRooted(fileName) && File.Exists(fileName))
            return Path.GetFullPath(fileName);

        var name = Path.GetFileName(fileName);
        var searchDirs = new List<string?>
        {
            Environment.SystemDirectory,
            Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0"),
            Path.GetDirectoryName(Environment.ProcessPath)
        };
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
            searchDirs.AddRange(pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));

        foreach (var dir in searchDirs)
        {
            if (string.IsNullOrEmpty(dir)) continue;
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate)) return candidate;
        }

        return Path.IsPathRooted(fileName) ? fileName : name;
    }
}
