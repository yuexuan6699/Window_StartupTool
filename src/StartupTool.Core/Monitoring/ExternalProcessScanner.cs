using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using StartupTool.Core.Models;
using StartupTool.Core.Native;

namespace StartupTool.Core.Monitoring;

/// <summary>检测到的外部（非本工具 Job 托管）同名进程。</summary>
public sealed record ExternalProcessInfo(int Pid, string ProcessName, DateTime? StartedAtUtc,
    string ScriptId, string? ExecutablePath);

/// <summary>
/// 外部同名进程扫描（A8/B2）：
/// - 一次 WMI 批量查询（修复旧版每进程一次 WMI 的开销）；
/// - 结果仅用于只读展示与用户显式操作，**绝不参与自动杀进程**（A6）。
/// </summary>
public sealed class ExternalProcessScanner : IDisposable
{
    private ManagementObjectSearcher? _searcher;
    private bool _disposed;

    public IReadOnlyList<ExternalProcessInfo> Scan(IEnumerable<ScriptConfig> scripts, HashSet<int> managedPids)
    {
        var list = new List<ExternalProcessInfo>();
        var scriptList = scripts.ToList();
        if (scriptList.Count == 0) return list;

        _searcher?.Dispose();
        _searcher = new ManagementObjectSearcher(
            "SELECT ProcessId, Name, ExecutablePath, CommandLine FROM Win32_Process");

        using var results = _searcher.Get();
        foreach (ManagementObject obj in results)
        {
            using (obj)
            {
                try
                {
                    var pid = Convert.ToInt32(obj["ProcessId"]);
                    if (managedPids.Contains(pid)) continue; // 本工具 Job 内的不算外部

                    var exePath = obj["ExecutablePath"]?.ToString();
                    var cmdLine = obj["CommandLine"]?.ToString() ?? string.Empty;
                    var processName = obj["Name"]?.ToString() ?? string.Empty;

                    foreach (var script in scriptList)
                    {
                        if (!IsMatch(script, exePath, cmdLine)) continue;

                        DateTime? startedAt = null;
                        try
                        {
                            using var p = System.Diagnostics.Process.GetProcessById(pid);
                            startedAt = p.StartTime.ToUniversalTime();
                        }
                        catch { /* 取不到时间不影响展示 */ }

                        list.Add(new ExternalProcessInfo(pid, processName, startedAt, script.Id, exePath));
                        // 不 break：多个脚本同路径时（如两个脚本都指向 notepad.exe），
                        // 该外部进程对每个脚本都应可见、可在各自窗口中终止。
                    }
                }
                catch
                {
                    // 单条进程信息读取失败（权限/进程退出）不影响其余
                }
            }
        }

        return list;
    }

    /// <summary>
    /// 外部进程是否匹配某个脚本（M11 收紧，宁可漏报不误报）：
    /// 1) 可执行文件完整路径与脚本路径完全一致（大小写不敏感）——直接启动 exe / 同目录 python.exe；
    /// 2) cmd/powershell 包装：命令行包含脚本完整路径——手动双击 .bat/.ps1 即此场景。
    /// 旧版"仅文件名子串（长度&gt;3）"匹配已删除：node.exe / python.exe 等同名解释器会大面积误报，
    /// 且会把命令行参数里恰好提到该文件名的无关进程当成目标。
    /// </summary>
    private static bool IsMatch(ScriptConfig script, string? exePath, string? cmdLine)
    {
        var scriptPath = script.Path?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(scriptPath)) return false;

        if (!string.IsNullOrEmpty(exePath)
            && string.Equals(NormalizePath(exePath), NormalizePath(scriptPath), StringComparison.OrdinalIgnoreCase))
            return true;

        return !string.IsNullOrEmpty(cmdLine)
            && cmdLine.Contains(scriptPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>去除可选的引号与首尾空白，避免 "C:\a\b.exe" 与 C:\a\b.exe 比较失败。</summary>
    private static string NormalizePath(string path)
        => path.Trim().Trim('"').Trim();

    /// <summary>
    /// 用户显式终止外部进程（界面需二次确认，A8）。
    /// 三重保护：必须不在本工具托管集合内 + (PID,创建时间) 校验 + 直接终止该进程而非按名字匹配。
    /// </summary>
    public static bool TryKill(ExternalProcessInfo target, HashSet<int> managedPids)
    {
        if (managedPids.Contains(target.Pid)) return false;

        using var handle = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_TERMINATE | NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION,
            false, target.Pid);
        if (handle.IsInvalid) return false;

        if (target.StartedAtUtc is { } expected)
        {
            if (!NativeMethods.GetProcessTimes(handle, out var creation, out _, out _, out _))
                return false;
            if (DateTime.FromFileTimeUtc(creation) != expected)
                return false; // PID 已被复用
        }

        return NativeMethods.TerminateProcess(handle, 1);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _searcher?.Dispose();
        GC.SuppressFinalize(this);
    }
}
