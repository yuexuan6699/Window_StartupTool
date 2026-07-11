using System.Diagnostics;

namespace StartupCommon;

/// <summary>
/// 进程扫描工具类 - 统一管理系统进程扫描逻辑
/// 消除 ScriptRunner 中多份重复的进程匹配代码
/// </summary>
public class ProcessScanner
{
    private readonly string _baseDir;

    public ProcessScanner(string baseDir)
    {
        _baseDir = baseDir;
    }

    /// <summary>
    /// 获取脚本的完整路径
    /// </summary>
    public string GetFullPath(ScriptItem script)
    {
        var fullPath = script.Path;
        if (!Path.IsPathRooted(fullPath))
        {
            fullPath = Path.GetFullPath(Path.Combine(_baseDir, fullPath));
        }
        return fullPath;
    }

    /// <summary>
    /// 扫描系统中查找与脚本匹配的进程
    /// </summary>
    public Process? FindScriptProcess(ScriptItem script)
    {
        try
        {
            var fullPath = GetFullPath(script);
            var scriptFileName = Path.GetFileName(fullPath).ToLowerInvariant();
            var ext = Path.GetExtension(fullPath).ToLowerInvariant();

            // .bat 或 .cmd → 匹配 cmd.exe 进程
            // .ps1 → 匹配 powershell.exe 或 pwsh.exe 进程
            // 其他 → 直接匹配进程名或完整路径
            bool useShellWrapper = ext is ".bat" or ".cmd" or ".ps1";

            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    var processPath = process.MainModule?.FileName?.ToLowerInvariant();
                    if (string.IsNullOrEmpty(processPath)) continue;

                    if (useShellWrapper)
                    {
                        // 判断是否是对应的 shell 包装器
                        string shellExe = ext switch
                        {
                            ".ps1" => "powershell.exe",
                            ".bat" or ".cmd" => "cmd.exe",
                            _ => null!
                        };
                        if (!string.IsNullOrEmpty(shellExe) && processPath.EndsWith(shellExe))
                        {
                            var cmdLine = GetCommandLine(process);
                            if (!string.IsNullOrEmpty(cmdLine) && cmdLine.Contains(scriptFileName, StringComparison.OrdinalIgnoreCase))
                            {
                                return process;
                            }
                        }
                    }
                    else
                    {
                        // 直接匹配进程名（exe 等）
                        if (processPath.EndsWith(scriptFileName) || processPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase))
                        {
                            return process;
                        }
                    }
                }
                catch
                {
                    // 某些系统进程无法访问，跳过
                }
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// 判断脚本是否在运行
    /// </summary>
    public bool IsScriptRunning(ScriptItem script)
    {
        using var process = FindScriptProcess(script);
        return process != null;
    }

    /// <summary>
    /// 扫描所有脚本的运行状态
    /// </summary>
    public Dictionary<string, bool> ScanAllScripts(List<ScriptItem> scripts)
    {
        var result = new Dictionary<string, bool>();

        // 一次性获取所有进程，避免反复调用 Process.GetProcesses()
        var allProcesses = Process.GetProcesses();

        foreach (var script in scripts)
        {
            result[script.Name] = IsScriptRunningFromList(script, allProcesses);
        }

        // 释放所有进程对象
        foreach (var p in allProcesses)
        {
            try { p.Dispose(); } catch { }
        }

        return result;
    }

    /// <summary>
    /// 从预获取的进程列表中查找脚本是否在运行（批量优化）
    /// </summary>
    private bool IsScriptRunningFromList(ScriptItem script, Process[] allProcesses)
    {
        try
        {
            var fullPath = GetFullPath(script);
            var scriptFileName = Path.GetFileName(fullPath).ToLowerInvariant();
            var ext = Path.GetExtension(fullPath).ToLowerInvariant();
            bool useShellWrapper = ext is ".bat" or ".cmd" or ".ps1";

            foreach (var process in allProcesses)
            {
                try
                {
                    var processPath = process.MainModule?.FileName?.ToLowerInvariant();
                    if (string.IsNullOrEmpty(processPath)) continue;

                    if (useShellWrapper)
                    {
                        string shellExe = ext switch
                        {
                            ".ps1" => "powershell.exe",
                            ".bat" or ".cmd" => "cmd.exe",
                            _ => null!
                        };
                        if (!string.IsNullOrEmpty(shellExe) && processPath.EndsWith(shellExe))
                        {
                            var cmdLine = GetCommandLine(process);
                            if (!string.IsNullOrEmpty(cmdLine) && cmdLine.Contains(scriptFileName, StringComparison.OrdinalIgnoreCase))
                            {
                                return true;
                            }
                        }
                    }
                    else
                    {
                        if (processPath.EndsWith(scriptFileName) || processPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
                catch { }
            }
        }
        catch { }

        return false;
    }

    /// <summary>
    /// 终止脚本对应的进程
    /// </summary>
    public bool KillScriptProcess(ScriptItem script)
    {
        try
        {
            using var process = FindScriptProcess(script);
            if (process == null) return false;

            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 通过 WMI 查询进程命令行
    /// </summary>
    private string GetCommandLine(Process process)
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {process.Id}");
            foreach (var obj in searcher.Get())
            {
                return obj["CommandLine"]?.ToString() ?? string.Empty;
            }
        }
        catch { }
        return string.Empty;
    }
}
