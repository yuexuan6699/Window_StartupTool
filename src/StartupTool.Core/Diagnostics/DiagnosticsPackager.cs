using System.IO.Compression;
using System.Text;

namespace StartupTool.Core.Diagnostics;

/// <summary>
/// 诊断包输入（D8）：配置 JSON（令牌已打码）、状态快照 JSON、服务日志目录与环境信息文本。
/// </summary>
public sealed record DiagnosticsInput(
    string ConfigJson,          // 已把心跳令牌打码为 *** 的配置 JSON
    string StatesJson,          // 运行中脚本状态快照 JSON
    string ServiceLogDirectory, // logs\service
    string EnvironmentText);    // 环境信息文本

/// <summary>
/// 诊断包打包（D8）：把配置/状态/环境与最近服务日志打成 zip，供用户反馈问题时附上。
/// 使用自定义条目名，故不用 <see cref="ZipFile.CreateFromDirectory"/>。
/// </summary>
public static class DiagnosticsPackager
{
    /// <summary>随包附带的日志文件上限（按最后写入时间取最新）。</summary>
    private const int MaxLogFiles = 50;

    /// <summary>把诊断包写到 outputDirectory，返回 zip 完整路径。文件名 startuptool-diagnostics-yyyyMMdd-HHmmss.zip。</summary>
    public static string Create(string outputDirectory, DiagnosticsInput input, DateTime nowUtc)
    {
        Directory.CreateDirectory(outputDirectory);
        var zipPath = Path.Combine(outputDirectory, $"startuptool-diagnostics-{nowUtc:yyyyMMdd-HHmmss}.zip");

        // 存在同名文件（同一秒内重复导出）时覆盖：ZipFile.Open 不支持 Create 模式覆盖，
        // 先删除旧文件再创建。
        try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { /* 覆盖失败则后续打开会抛，交由调用方处理 */ }

        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);

        WriteTextEntry(archive, "config/scripts.json", input.ConfigJson);
        WriteTextEntry(archive, "runtime/states.json", input.StatesJson);
        WriteTextEntry(archive, "environment.txt", input.EnvironmentText);

        foreach (var logFile in SelectRecentLogs(input.ServiceLogDirectory))
        {
            try
            {
                var entry = archive.CreateEntry($"logs/service/{Path.GetFileName(logFile)}");
                using var entryStream = entry.Open();
                using var source = File.OpenRead(logFile);
                source.CopyTo(entryStream);
            }
            catch
            {
                // 单个日志文件读取失败（被占用/权限不足/正在写入）：跳过，绝不因此让整包失败
                continue;
            }
        }

        return zipPath;
    }

    private static void WriteTextEntry(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content ?? string.Empty);
    }

    /// <summary>按最后写入时间倒序取最多 <see cref="MaxLogFiles"/> 个日志文件。</summary>
    private static List<string> SelectRecentLogs(string dir)
    {
        var candidates = new List<(string Path, DateTime LastWriteUtc)>();
        try
        {
            if (Directory.Exists(dir))
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly))
                {
                    try { candidates.Add((file, File.GetLastWriteTimeUtc(file))); }
                    catch { /* 单个文件元数据读取失败：跳过 */ }
                }
            }
        }
        catch
        {
            // 目录枚举失败（不存在/权限不足）：不附带任何日志
        }

        return candidates
            .OrderByDescending(x => x.LastWriteUtc)
            .Take(MaxLogFiles)
            .Select(x => x.Path)
            .ToList();
    }
}
