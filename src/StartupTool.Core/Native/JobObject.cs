using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace StartupTool.Core.Native;

/// <summary>
/// Windows Job Object 封装（A4）。
/// 策略：JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE（句柄关闭即杀光 Job 内所有进程）；
/// **不**设置 BREAKAWAY_OK / SILENT_BREAKAWAY，脚本任何子孙进程都无法逃逸。
/// 存活判定单位 = Job：通过 <see cref="GetProcessIds"/> 判定 Job 内是否仍有进程。
/// </summary>
public sealed class JobObject : IDisposable
{
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    private const int ERROR_BAD_LENGTH = 24;

    private readonly SafeFileHandle _handle;
    private bool _disposed;

    public JobObject()
    {
        _handle = NativeMethods.CreateJobObjectW(IntPtr.Zero, lpName: null);
        if (_handle.IsInvalid)
            throw new InvalidOperationException($"创建 Job Object 失败，Win32 错误 {Marshal.GetLastWin32Error()}。");

        ConfigureKillOnClose();
    }

    private void ConfigureKillOnClose()
    {
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                // 注意：不设 JOB_OBJECT_LIMIT_BREAKAWAY_OK(0x1000)/SILENT_BREAKAWAY_OK(0x10000)
            }
        };

        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            if (!NativeMethods.SetInformationJobObject(_handle, JobObjectInfoType.ExtendedLimitInformation, ptr, (uint)size))
                throw new InvalidOperationException($"配置 Job Object 失败，Win32 错误 {Marshal.GetLastWin32Error()}。");
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    /// <summary>把进程加入 Job。进程必须在挂起态调用（加入后再 Resume），杜绝窗口期逃逸。</summary>
    public void Assign(SafeFileHandle processHandle)
    {
        if (!NativeMethods.AssignProcessToJobObject(_handle, processHandle))
        {
            // 嵌套 Job（进程已在别的 Job）在 Win8+ 允许；若仍失败需暴露真实错误
            throw new InvalidOperationException($"进程加入 Job 失败，Win32 错误 {Marshal.GetLastWin32Error()}。");
        }
    }

    /// <summary>终止 Job 内所有进程（跨会话同样有效）。</summary>
    public void Terminate(uint exitCode = 1)
    {
        if (!_handle.IsInvalid)
            NativeMethods.TerminateJobObject(_handle, exitCode);
    }

    /// <summary>Job 内当前全部进程 PID；Job 为空时返回空数组。</summary>
    public int[] GetProcessIds()
    {
        // 容量按 NumberOfAssignedProcesses 动态增长，避免 ERROR_BAD_LENGTH
        var capacity = 16;
        while (true)
        {
            // 头 8 字节（两个 DWORD），后接 PID（指针宽度）数组
            var size = 8 + capacity * IntPtr.Size;
            var ptr = Marshal.AllocHGlobal(size);
            try
            {
                // 关键：首字段 NumberOfAssignedProcesses 是入参，必须写入调用方提供的数组容量，
                // 否则 API 恒以 ERROR_BAD_LENGTH(24) 失败。
                Marshal.WriteInt32(ptr, 0, capacity);

                if (!NativeMethods.QueryInformationJobObject(
                        _handle, JobObjectInfoType.BasicProcessIdList, ptr, (uint)size, out _))
                {
                    var err = Marshal.GetLastWin32Error();
                    if (err == ERROR_BAD_LENGTH && capacity < 65536)
                    {
                        capacity *= 4;
                        continue;
                    }
                    throw new InvalidOperationException($"查询 Job 进程列表失败，Win32 错误 {err}。");
                }

                var assigned = Marshal.ReadInt32(ptr, 0);
                var count = Marshal.ReadInt32(ptr, 4);
                var list = new List<int>(count);
                for (var i = 0; i < count; i++)
                {
                    var pid = IntPtr.Size == 8
                        ? Marshal.ReadInt64(ptr, 8 + i * IntPtr.Size)
                        : Marshal.ReadInt32(ptr, 8 + i * IntPtr.Size);
                    // BasicProcessIdList 会短暂滞留"退出中"的 PID，逐个核实存活，
                    // 否则毫秒级脚本退出后仍被误判在运行（自愈/接力判定被拖慢）。
                    if (IsProcessAlive((int)pid))
                        list.Add((int)pid);
                }

                _ = assigned;
                return list.ToArray();
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
    }

    /// <summary>Job 内是否已无任何进程（停止/重启到位的判据）。</summary>
    public bool IsEmpty => GetProcessIds().Length == 0;

    /// <summary>决定性判定：指定 PID 当前是否在本 Job 内（测试用）。</summary>
    internal bool ContainsProcess(int pid)
    {
        using var h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h.IsInvalid) return false;
        return NativeMethods.IsProcessInJob(h, _handle.DangerousGetHandle(), out var inJob) && inJob;
    }

    private const uint STILL_ACTIVE = 259;
    private const int ERROR_INVALID_PARAMETER = 87;

    /// <summary>
    /// 判定 PID 存活（M7 收紧）。只有"进程不存在"（OpenProcess 返回 ERROR_INVALID_PARAMETER，
    /// 已退出 PID 的典型错误）才判定死亡；ACCESS_DENIED（受保护/更高权限进程）与其他任何
    /// 查询失败一律按存活处理——少报一个 PID 最坏只是延迟一拍，而误报"Job 已空"会错误触发
    /// 重启/清理（WatchLoop 双周期复查进一步降低误差）。
    /// </summary>
    private static bool IsProcessAlive(int pid)
    {
        using var h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h.IsInvalid)
            return Marshal.GetLastWin32Error() != ERROR_INVALID_PARAMETER;
        return NativeMethods.GetExitCodeProcess(h, out var code) && code == STILL_ACTIVE;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // 关闭句柄即触发 KILL_ON_JOB_CLOSE，是兜底杀光机制
        _handle.Dispose();
    }
}
