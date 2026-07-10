namespace StartupCommon;

public class Logger
{
    private readonly string _logDir;
    private readonly object _lock = new();
    private string _currentLogFile;
    private readonly Dictionary<string, string> _scriptLogFiles = new();

    public Logger(string baseDir)
    {
        // 日志目录始终基于 exe 所在路径，而非 baseDir（config 查找路径）
        _logDir = Path.Combine(AppContext.BaseDirectory, "logs");

        // 主程序日志目录
        var startupDir = Path.Combine(_logDir, "startup");
        Directory.CreateDirectory(startupDir);
        _currentLogFile = GetNextLogFile(startupDir);
    }

    /// <summary>当前主程序日志文件路径</summary>
    public string CurrentLogFile => _currentLogFile;

    public void Info(string message) => Log("INFO", message);
    public void Error(string message) => Log("ERROR", message);
    public void Warn(string message) => Log("WARN", message);
    public void Success(string message) => Log("SUCCESS", message);

    private void Log(string level, string message)
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var line = $"[{timestamp}] [{level}] {message}";

        lock (_lock)
        {
            Console.WriteLine(line);
            try
            {
                File.AppendAllText(_currentLogFile, line + Environment.NewLine);
            }
            catch { }
        }
    }

    /// <summary>
    /// 获取脚本对应的日志文件路径（如果不存在则创建新文件）
    /// </summary>
    public string GetScriptLogFile(string scriptName)
    {
        var pinyinName = PinyinConverter.ToPinyinDirName(scriptName);
        lock (_lock)
        {
            if (_scriptLogFiles.TryGetValue(pinyinName, out var file))
                return file;

            var scriptDir = Path.Combine(_logDir, pinyinName);
            Directory.CreateDirectory(scriptDir);
            file = GetNextLogFile(scriptDir);
            _scriptLogFiles[pinyinName] = file;
            return file;
        }
    }

    /// <summary>
    /// 将脚本输出写入对应的脚本日志文件
    /// </summary>
    public void LogScriptOutput(string scriptName, string level, string message)
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var line = $"[{timestamp}] [{level}] {message}";
        var logFile = GetScriptLogFile(scriptName);

        lock (_lock)
        {
            try
            {
                File.AppendAllText(logFile, line + Environment.NewLine);
            }
            catch { }
        }
    }

    /// <summary>
    /// 在指定目录中查找当天最大的序号，返回序号+1的新文件路径
    /// </summary>
    private static string GetNextLogFile(string dir)
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var files = Directory.GetFiles(dir, $"{today}_*.log");
        int maxSeq = 0;
        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var parts = name.Split('_');
            if (parts.Length == 2 && int.TryParse(parts[1], out var seq))
                maxSeq = Math.Max(maxSeq, seq);
        }
        return Path.Combine(dir, $"{today}_{maxSeq + 1:D3}.log");
    }
}