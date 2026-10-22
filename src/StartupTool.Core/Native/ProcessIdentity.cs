using System.Diagnostics;
using Microsoft.Win32.SafeHandles;

namespace StartupTool.Core.Native;

/// <summary>
/// 进程身份：(PID, 创建时间)（A6）。PID 会被系统复用，单凭 PID 可能误判/误杀；
/// 加上创建时间后可唯一区分一次进程运行。
/// </summary>
public readonly record struct ProcessIdentity(int Pid, DateTime StartedAtUtc)
{
    /// <summary>从进程句柄读取创建时间。</summary>
    public static ProcessIdentity FromHandle(int pid, SafeFileHandle handle)
    {
        if (!NativeMethods.GetProcessTimes(handle, out var creation, out _, out _, out _))
            throw new InvalidOperationException($"GetProcessTimes 失败，Win32 错误 {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}。");
        return new ProcessIdentity(pid, DateTime.FromFileTimeUtc(creation));
    }

    /// <summary>从 <see cref="Process"/> 读取身份。</summary>
    public static ProcessIdentity From(Process process)
        => new(process.Id, process.StartTime.ToUniversalTime());

    /// <summary>
    /// 该身份当前是否仍指向同一进程（PID 存在且创建时间一致）。
    /// 仅用于只读判断，绝不用于决定杀进程（杀一律走 Job）。
    /// </summary>
    public bool IsAlive()
    {
        try
        {
            using var p = Process.GetProcessById(Pid);
            return p.StartTime.ToUniversalTime() == StartedAtUtc;
        }
        catch (ArgumentException) { return false; }   // PID 已不存在
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; } // 访问被拒按"不可确认"处理
    }
}
