using System.Diagnostics;

namespace StartupCommon;

public class ScriptRunner
{
    private readonly Logger _logger;
    private readonly string _baseDir;
    private readonly Dictionary<string, Process> _runningProcesses = new();
    private readonly Dictionary<string, List<string>> _scriptLogs = new();
    private readonly object _lock = new();

    public ScriptRunner(Logger logger, string baseDir)
    {
        _logger = logger;
        _baseDir = baseDir;
    }

    public bool StartScript(ScriptItem script)
    {
        lock (_lock)
        {
            if (_runningProcesses.ContainsKey(script.Name))
            {
                _logger.Warn($"脚本已在运行: {script.Name}");
                return false;
            }
        }

        // 如果脚本路径是相对路径，则基于项目根目录补全为绝对路径
        var fullPath = script.Path;
        if (!Path.IsPathRooted(fullPath))
        {
            fullPath = Path.GetFullPath(Path.Combine(_baseDir, fullPath));
        }

        if (!File.Exists(fullPath))
        {
            _logger.Error($"脚本文件不存在: {script.Name} ({fullPath})");
            return false;
        }

        try
        {
            var workDir = Path.GetDirectoryName(fullPath) ?? "";
            var fileName = fullPath;

            string processFileName;
            string arguments;

            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            if (ext == ".ps1")
            {
                processFileName = "powershell.exe";
                arguments = $"-ExecutionPolicy Bypass -File \"{fileName}\"";
                if (!string.IsNullOrEmpty(script.Arguments))
                    arguments += " " + script.Arguments;
            }
            else if (ext == ".bat" || ext == ".cmd")
            {
                processFileName = "cmd.exe";
                arguments = $"/c \"{fileName}\"";
                if (!string.IsNullOrEmpty(script.Arguments))
                    arguments += " " + script.Arguments;
            }
            else
            {
                processFileName = fileName;
                arguments = script.Arguments;
            }

            var psi = new ProcessStartInfo
            {
                FileName = processFileName,
                Arguments = arguments,
                WorkingDirectory = workDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };

            var process = Process.Start(psi);
            if (process == null)
            {
                _logger.Error($"启动脚本失败: {script.Name}");
                return false;
            }

            process.Exited += (s, e) => OnProcessExited(script.Name, process);
            process.EnableRaisingEvents = true;

            lock (_lock)
            {
                _runningProcesses[script.Name] = process;
                _scriptLogs[script.Name] = new List<string>();
            }

            ReadProcessOutputAsync(process, script.Name);

            _logger.Success($"脚本已启动: {script.Name} (PID: {process.Id})");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error($"启动脚本异常 {script.Name}: {ex.Message}");
            return false;
        }
    }

    private async void ReadProcessOutputAsync(Process process, string scriptName)
    {
        try
        {
            await Task.WhenAll(
                ReadOutputAsync(process.StandardOutput, scriptName, "STDOUT"),
                ReadOutputAsync(process.StandardError, scriptName, "STDERR")
            );
        }
        catch { }
    }

    private async Task ReadOutputAsync(StreamReader reader, string scriptName, string type)
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            var logLine = $"[{DateTime.Now:HH:mm:ss.fff}] [{type}] {line}";
            lock (_lock)
            {
                if (_scriptLogs.TryGetValue(scriptName, out var logs))
                {
                    logs.Add(logLine);
                    if (logs.Count > 5000)
                    {
                        logs.RemoveRange(0, logs.Count - 5000);
                    }
                }
            }
            _logger.LogScriptOutput(scriptName, type, line);
        }
    }

    private void OnProcessExited(string name, Process process)
    {
        lock (_lock)
        {
            if (_runningProcesses.TryGetValue(name, out var p) && p.Id == process.Id)
            {
                _runningProcesses.Remove(name);
                _logger.Info($"脚本进程退出: {name} (PID: {process.Id}, 退出码: {process.ExitCode})");
            }
        }
        process.Dispose();
    }

    public bool StopScript(string name)
    {
        Process? process;
        lock (_lock)
        {
            if (!_runningProcesses.TryGetValue(name, out process))
            {
                _logger.Warn($"脚本未在运行: {name}");
                return false;
            }
            _runningProcesses.Remove(name);
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            _logger.Info($"脚本已停止: {name}");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error($"停止脚本异常 {name}: {ex.Message}");
            return false;
        }
        finally
        {
            process.Dispose();
        }
    }

    public async Task StartAllScriptsAsync(List<ScriptItem> scripts)
    {
        _logger.Info($"开始启动 {scripts.Count} 个脚本...");
        foreach (var script in scripts.OrderBy(s => s.Order))
        {
            StartScript(script);
            await Task.Delay(500);
        }
        _logger.Info("所有脚本启动完成");
    }

    public void StartAllScripts(List<ScriptItem> scripts)
    {
        _logger.Info($"开始启动 {scripts.Count} 个脚本...");
        foreach (var script in scripts.OrderBy(s => s.Order))
        {
            StartScript(script);
            Thread.Sleep(500);
        }
        _logger.Info("所有脚本启动完成");
    }

    public void StopAllScripts()
    {
        Process[] processes;
        lock (_lock)
        {
            processes = _runningProcesses.Values.ToArray();
            _runningProcesses.Clear();
        }

        _logger.Info($"正在停止 {processes.Length} 个运行中的脚本...");
        foreach (var process in processes)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(3000);
                }
            }
            catch { }
            finally
            {
                process.Dispose();
            }
        }
    }

    public Dictionary<string, bool> GetRunningStatus()
    {
        lock (_lock)
        {
            return _runningProcesses.ToDictionary(
                kvp => kvp.Key,
                kvp => !kvp.Value.HasExited);
        }
    }

    public bool IsRunning(string name)
    {
        lock (_lock)
        {
            return _runningProcesses.TryGetValue(name, out var p) && !p.HasExited;
        }
    }

    public int RunningCount
    {
        get
        {
            lock (_lock)
            {
                return _runningProcesses.Count;
            }
        }
    }

    public Dictionary<string, Process> GetRunningProcesses()
    {
        lock (_lock)
        {
            return _runningProcesses.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }
    }

    public List<string> GetScriptLogs(string name)
    {
        lock (_lock)
        {
            if (_scriptLogs.TryGetValue(name, out var logs))
            {
                return new List<string>(logs);
            }
            return new List<string>();
        }
    }

    public void ClearScriptLogs(string name)
    {
        lock (_lock)
        {
            if (_scriptLogs.ContainsKey(name))
            {
                _scriptLogs[name].Clear();
            }
        }
    }
}
