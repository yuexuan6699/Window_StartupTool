using System.Diagnostics;

namespace StartupCommon;

public class ScriptRunner
{
    private readonly Logger _logger;
    private readonly string _baseDir;
    private readonly ProcessScanner _processScanner;
    private readonly ProcessMonitor _processMonitor;
    private readonly Dictionary<string, Process> _runningProcesses = new();
    private readonly Dictionary<string, List<string>> _scriptLogs = new();
    private readonly Dictionary<string, DateTime> _lastRestartTime = new();
    private readonly object _lock = new();
    private System.Timers.Timer? _resourceMonitorTimer;

    public ScriptRunner(Logger logger, string baseDir)
    {
        _logger = logger;
        _baseDir = baseDir;
        _processScanner = new ProcessScanner(baseDir);
        _processMonitor = new ProcessMonitor(logger);
    }

    #region 资源监控

    public void StartResourceMonitor(List<ScriptItem> scripts)
    {
        if (_resourceMonitorTimer != null)
        {
            _resourceMonitorTimer.Stop();
            _resourceMonitorTimer.Dispose();
        }

        _resourceMonitorTimer = new System.Timers.Timer(5000);
        _resourceMonitorTimer.Elapsed += (s, e) => MonitorResources(scripts);
        _resourceMonitorTimer.Start();
    }

    public void StopResourceMonitor()
    {
        _resourceMonitorTimer?.Stop();
        _resourceMonitorTimer?.Dispose();
        _resourceMonitorTimer = null;
    }

    private void MonitorResources(List<ScriptItem> scripts)
    {
        try
        {
            foreach (var script in scripts)
            {
                if (!script.Enabled) continue;

                var process = _processScanner.FindScriptProcess(script);
                if (process == null) continue;

                var resourceInfo = _processMonitor.GetProcessResourceInfo(process);

                bool shouldRestart = false;
                string reason = "";

                if (script.MaxCpuPercent > 0 && resourceInfo.CpuPercent > script.MaxCpuPercent)
                {
                    shouldRestart = true;
                    reason += $"CPU占用过高({resourceInfo.CpuPercent:F1}% > {script.MaxCpuPercent}%)";
                }

                if (script.MaxMemoryMb > 0 && resourceInfo.MemoryMb > script.MaxMemoryMb)
                {
                    shouldRestart = true;
                    reason += $"内存占用过高({resourceInfo.MemoryMb}MB > {script.MaxMemoryMb}MB)";
                }

                if (script.MaxGpuPercent > 0 && resourceInfo.GpuPercent > script.MaxGpuPercent)
                {
                    shouldRestart = true;
                    reason += $"GPU占用过高({resourceInfo.GpuPercent:F1}% > {script.MaxGpuPercent}%)";
                }

                if (shouldRestart)
                {
                    // 防抖动：60秒内只重启一次
                    lock (_lock)
                    {
                        if (_lastRestartTime.TryGetValue(script.Name, out var lastTime))
                        {
                            if (DateTime.Now - lastTime < TimeSpan.FromSeconds(60))
                                continue;
                        }
                        _lastRestartTime[script.Name] = DateTime.Now;
                    }

                    _logger.Warn($"脚本资源占用过高，将重启: {script.Name} - {reason}");

                    StopScript(script.Name, script);

                    Task.Delay(script.RestartDelaySeconds * 1000).ContinueWith(_ =>
                    {
                        StartScript(script);
                    });
                }

                process.Dispose();
            }
        }
        catch { }
    }

    #endregion

    #region 脚本生命周期

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

        var fullPath = _processScanner.GetFullPath(script);

        if (!File.Exists(fullPath))
        {
            _logger.Error($"脚本文件不存在: {script.Name} ({fullPath})");
            return false;
        }

        try
        {
            var workDir = Path.GetDirectoryName(fullPath) ?? "";
            var ext = Path.GetExtension(fullPath).ToLowerInvariant();

            string processFileName;
            string arguments;

            if (ext == ".ps1")
            {
                processFileName = "powershell.exe";
                arguments = $"-ExecutionPolicy Bypass -File \"{fullPath}\"";
                if (!string.IsNullOrEmpty(script.Arguments))
                    arguments += " " + script.Arguments;
            }
            else if (ext == ".bat" || ext == ".cmd")
            {
                processFileName = "cmd.exe";
                arguments = $"/c \"{fullPath}\"";
                if (!string.IsNullOrEmpty(script.Arguments))
                    arguments += " " + script.Arguments;
            }
            else
            {
                processFileName = fullPath;
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

    /// <summary>
    /// 统一停止脚本：先尝试本地跟踪的进程，再回退到系统扫描
    /// </summary>
    public bool StopScript(string name, ScriptItem? scriptItem = null)
    {
        // 第一步：尝试停止本地跟踪的进程
        Process? process;
        lock (_lock)
        {
            if (_runningProcesses.TryGetValue(name, out process))
            {
                _runningProcesses.Remove(name);
            }
            else
            {
                process = null;
            }
        }

        if (process != null)
        {
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
            }
            finally
            {
                process.Dispose();
            }
        }

        // 第二步：如果本地没有跟踪到，尝试系统扫描终止
        if (scriptItem != null)
        {
            if (_processScanner.KillScriptProcess(scriptItem))
            {
                _logger.Info($"脚本已停止(系统扫描): {name}");
                return true;
            }
        }

        _logger.Warn($"脚本未在运行: {name}");
        return false;
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

    #endregion

    #region 进程退出处理

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
                        logs.RemoveRange(0, logs.Count - 5000);
                }
            }
            _logger.LogScriptOutput(scriptName, type, line);
        }
    }

    #endregion

    #region 状态查询

    /// <summary>
    /// 获取本地跟踪的运行状态（快速，不进行系统扫描）
    /// </summary>
    public Dictionary<string, bool> GetRunningStatus()
    {
        lock (_lock)
        {
            return _runningProcesses.ToDictionary(
                kvp => kvp.Key,
                kvp => !kvp.Value.HasExited);
        }
    }

    /// <summary>
    /// 获取统一运行状态：合并本地跟踪 + 系统扫描
    /// </summary>
    public Dictionary<string, bool> GetUnifiedStatus(List<ScriptItem> allScripts)
    {
        var status = GetRunningStatus();

        // 对未在本进程跟踪到的脚本，进行系统扫描
        var unTrackedScripts = allScripts
            .Where(s => !status.ContainsKey(s.Name) || !status[s.Name])
            .ToList();

        if (unTrackedScripts.Count > 0)
        {
            var systemStatus = _processScanner.ScanAllScripts(unTrackedScripts);
            foreach (var (name, running) in systemStatus)
            {
                if (running)
                    status[name] = true;
            }
        }

        return status;
    }

    /// <summary>
    /// 获取所有运行中的脚本信息（名称 + PID），用于 UI 展示
    /// </summary>
    public (string Name, int Pid)[] GetRunningProcessInfos(List<ScriptItem> allScripts)
    {
        var results = new List<(string Name, int Pid)>();

        foreach (var script in allScripts)
        {
            using var process = _processScanner.FindScriptProcess(script);
            if (process != null && !process.HasExited)
            {
                results.Add((script.Name, process.Id));
            }
        }

        return results.ToArray();
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

    #endregion

    #region 资源信息

    public Dictionary<string, ProcessResourceInfo> GetProcessResourceInfo(List<ScriptItem> allScripts)
    {
        var infos = new Dictionary<string, ProcessResourceInfo>();

        foreach (var script in allScripts)
        {
            var process = _processScanner.FindScriptProcess(script);
            if (process != null)
            {
                try
                {
                    var info = _processMonitor.GetProcessResourceInfo(process);
                    info.ScriptName = script.Name;
                    infos[script.Name] = info;
                }
                finally
                {
                    process.Dispose();
                }
            }
            else
            {
                infos[script.Name] = new ProcessResourceInfo
                {
                    ScriptName = script.Name,
                    Running = false
                };
            }
        }

        return infos;
    }

    #endregion

    #region 日志

    public List<string> GetScriptLogs(string name)
    {
        lock (_lock)
        {
            if (_scriptLogs.TryGetValue(name, out var logs))
                return new List<string>(logs);
        }

        try
        {
            var logFile = _logger.GetLatestScriptLogFile(name);
            if (!string.IsNullOrEmpty(logFile) && File.Exists(logFile))
            {
                return File.ReadAllLines(logFile).ToList();
            }
        }
        catch { }

        return new List<string>();
    }

    public void ClearScriptLogs(string name)
    {
        lock (_lock)
        {
            if (_scriptLogs.ContainsKey(name))
                _scriptLogs[name].Clear();
        }
    }

    #endregion
}
