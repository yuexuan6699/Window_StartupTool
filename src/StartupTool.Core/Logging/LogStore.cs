using System.Text;
using StartupTool.Core.Models;

namespace StartupTool.Core.Logging;

/// <summary>
/// 日志落盘（B10 承诺：单文件大小上限 <c>logMaxSizeMb</c> + 保留天数 <c>logRetentionDays</c>）。
/// 目录约定：
/// <list type="bullet">
/// <item><c>{基目录}\logs\service\service-yyyyMMdd.log</c> —— 全局运行日志（故障/信息/启动期警告）</item>
/// <item><c>{基目录}\logs\scripts\&lt;脚本名_短哈希&gt;\output-yyyyMMdd.log</c> —— 脚本 stdout/stderr</item>
/// </list>
/// 当天文件超过上限后自动分片（<c>-2.log</c>、<c>-3.log</c>…）。
/// 线程安全；任何 IO 失败一律吞掉——日志绝不能影响守护本身。
/// </summary>
public sealed class LogStore
{
    private const int MaxShardIndex = 10_000;
    private const int MaxNameLength = 40;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly object _gate = new();
    private readonly Func<GlobalSettings> _settings;
    private readonly string _serviceDirectory;
    private readonly string _scriptsDirectory;
    private DateTime _lastCleanupDate = DateTime.MinValue;

    /// <param name="settingsAccessor">取当前全局设置（每次写入时读取，配置热重载即时生效）。</param>
    /// <param name="logsRoot">日志根目录；默认 <see cref="AppPaths.LogsDirectory"/>（测试可注入临时目录）。</param>
    public LogStore(Func<GlobalSettings> settingsAccessor, string? logsRoot = null)
    {
        _settings = settingsAccessor;
        var root = logsRoot ?? AppPaths.LogsDirectory;
        _serviceDirectory = Path.Combine(root, "service");
        _scriptsDirectory = Path.Combine(root, "scripts");
    }

    /// <summary>服务日志目录（诊断包按此取件）。</summary>
    public string ServiceDirectory => _serviceDirectory;

    /// <summary>脚本日志根目录。</summary>
    public string ScriptsDirectory => _scriptsDirectory;

    /// <summary>写一条全局运行日志。level 建议 <c>INFO</c> / <c>WARN</c> / <c>FAULT</c>。</summary>
    public void WriteService(string level, string? scriptName, string message)
        => Append(_serviceDirectory, "service", Format(level, scriptName, message));

    /// <summary>写脚本 stdout/stderr 的一行。</summary>
    public void WriteScriptOutput(string scriptId, string scriptName, string line)
        => Append(Path.Combine(_scriptsDirectory, ScriptFolderName(scriptName, scriptId)), "output", line);

    /// <summary>
    /// 清理超过保留天数的日志文件（服务启动时调一次；此后每次写入按天复核一次）。
    /// </summary>
    public void CleanupExpired()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-RetentionDays());
            foreach (var root in new[] { _serviceDirectory, _scriptsDirectory })
            {
                if (!Directory.Exists(root)) continue;
                foreach (var file in Directory.EnumerateFiles(root, "*.log", SearchOption.AllDirectories))
                {
                    try
                    {
                        if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
                    }
                    catch { /* 单个文件删除失败（占用/权限）：跳过 */ }
                }
            }
        }
        catch
        {
            // 目录不存在或枚举失败：不阻断
        }
    }

    /// <summary>脚本日志目录名：<c>&lt;净化后的脚本名&gt;_&lt;Id 前 8 位&gt;</c>。</summary>
    public static string ScriptFolderName(string? scriptName, string? scriptId)
    {
        var name = Sanitize(scriptName);
        var id = scriptId ?? string.Empty;
        var hash = id.Length >= 8 ? id[..8] : id.PadRight(8, '0');
        return $"{name}_{hash}";
    }

    private void Append(string directory, string baseName, string line)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(directory);
                var path = NextShard(directory, baseName, DateTime.Now, MaxSizeBytes());
                File.AppendAllText(path, line + Environment.NewLine, Utf8NoBom);
            }
        }
        catch
        {
            // 日志失败（磁盘满/占用/权限）绝不影响守护：静默
        }

        TryDailyCleanup();
    }

    /// <summary>取当天可用分片；当前分片已满则顺延到下一个序号。</summary>
    private static string NextShard(string directory, string baseName, DateTime now, long maxBytes)
    {
        var stamp = now.ToString("yyyyMMdd");
        var candidate = Path.Combine(directory, $"{baseName}-{stamp}.log");
        for (var i = 2; i < MaxShardIndex; i++)
        {
            if (!IsFull(candidate, maxBytes)) return candidate;
            candidate = Path.Combine(directory, $"{baseName}-{stamp}-{i}.log");
        }
        // 极端兜底（单日数万次分片）：按时刻另起文件，绝不无限循环
        return Path.Combine(directory, $"{baseName}-{stamp}-{now:HHmmss}.log");
    }

    private static bool IsFull(string path, long maxBytes)
    {
        try { return File.Exists(path) && new FileInfo(path).Length >= maxBytes; }
        catch { return false; }
    }

    private static string Format(string level, string? scriptName, string message)
    {
        var name = string.IsNullOrWhiteSpace(scriptName) ? string.Empty : $" [{scriptName}]";
        return $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}][{level}]{name} {message}";
    }

    private void TryDailyCleanup()
    {
        var today = DateTime.Now.Date;
        if (_lastCleanupDate == today) return;
        _lastCleanupDate = today;
        CleanupExpired();
    }

    private long MaxSizeBytes()
    {
        var mb = 5;
        try { mb = _settings().LogMaxSizeMb; } catch { /* 配置不可用时用默认 */ }
        return Math.Clamp(mb, 1, 1024) * 1024L * 1024L;
    }

    private int RetentionDays()
    {
        var days = 30;
        try { days = _settings().LogRetentionDays; } catch { /* 配置不可用时用默认 */ }
        return Math.Clamp(days, 1, 3650);
    }

    /// <summary>文件名净化：保留字母/数字/中文字/下划线/连字符，空格转下划线，其余丢弃；过长截断。</summary>
    private static string Sanitize(string? raw)
    {
        var source = string.IsNullOrWhiteSpace(raw) ? "script" : raw.Trim();
        var sb = new StringBuilder(source.Length);
        foreach (var ch in source)
        {
            if (char.IsLetterOrDigit(ch) || ch is '_' or '-') sb.Append(ch);
            else if (ch == ' ') sb.Append('_');
        }
        var text = sb.ToString();
        if (text.Length == 0) text = "script";
        return text.Length <= MaxNameLength ? text : text[..MaxNameLength];
    }
}
