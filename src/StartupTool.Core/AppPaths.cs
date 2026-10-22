namespace StartupTool.Core;

/// <summary>
/// 全程序统一的路径解析（修复旧版 B11：Logger 用 AppContext.BaseDirectory、
/// 其余模块用 Environment.ProcessPath，单文件发布下两者不一致）。
/// 统一以 <see cref="Environment.ProcessPath"/> 所在目录为基准目录。
/// </summary>
public static class AppPaths
{
    /// <summary>exe 所在目录（配置、日志都放这里）。</summary>
    public static string BaseDirectory { get; } =
        Path.GetDirectoryName(Environment.ProcessPath ?? typeof(AppPaths).Assembly.Location)
        ?? AppContext.BaseDirectory;

    /// <summary>配置目录：{基目录}\config</summary>
    public static string ConfigDirectory { get; } = Path.Combine(BaseDirectory, "config");

    /// <summary>脚本配置文件：{基目录}\config\scripts.json</summary>
    public static string ConfigFile { get; } = Path.Combine(ConfigDirectory, "scripts.json");

    /// <summary>配置备份文件：{基目录}\config\scripts.json.bak</summary>
    public static string ConfigBackupFile { get; } = ConfigFile + ".bak";

    /// <summary>日志根目录：{基目录}\logs</summary>
    public static string LogsDirectory { get; } = Path.Combine(BaseDirectory, "logs");

    /// <summary>全局运行日志目录：{基目录}\logs\service</summary>
    public static string ServiceLogDirectory { get; } = Path.Combine(LogsDirectory, "service");

    /// <summary>各脚本独立日志根目录：{基目录}\logs\scripts</summary>
    public static string ScriptsLogDirectory { get; } = Path.Combine(LogsDirectory, "scripts");

    /// <summary>诊断包输出目录：{基目录}\logs\diagnostics</summary>
    public static string DiagnosticsDirectory { get; } = Path.Combine(LogsDirectory, "diagnostics");

    /// <summary>确保关键目录存在。</summary>
    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(ServiceLogDirectory);
        Directory.CreateDirectory(ScriptsLogDirectory);
    }
}
