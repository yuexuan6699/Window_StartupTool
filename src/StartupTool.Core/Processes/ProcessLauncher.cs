using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using StartupTool.Core.Native;

namespace StartupTool.Core.Processes;

/// <summary>
/// 脚本进程启动器。
/// 关键时序：CreateProcess(CREATE_SUSPENDED) → 加入 Job → ResumeThread，
/// 保证进程从第一条指令起就在 Job 内，不存在脱离托管的窗口期（A4/A5）。
/// 两条启动路径：
/// - <see cref="Launch"/>：同会话启动（M3；--console 调试与"系统会话运行"脚本使用）；
/// - <see cref="LaunchAsUser"/>：服务端以登录用户令牌 CreateProcessAsUser（A3，默认路径）。
/// </summary>
public static class ProcessLauncher
{
    private delegate PROCESS_INFORMATION CreateProcessDelegate(
        uint flags, bool inheritHandles, ref STARTUPINFO startupInfo);

    /// <summary>
    /// 在当前会话以挂起态创建进程、加入指定 Job 后恢复执行。
    /// <paramref name="desktop"/> 非空时把进程挂到指定窗口站桌面（D6 方案 a：服务以自身
    /// SYSTEM 身份创建进程、但落到登录用户桌面，从而具备完整管理员权限且窗口可见）。
    /// </summary>
    public static LaunchedProcess Launch(LaunchOptions options, JobObject? job = null, string? desktop = null)
    {
        var envBlock = EnvironmentBlock.Build(options.EnvironmentVariables);
        try
        {
            // 同会话启动：默认不指定桌面，继承当前窗口站/桌面（--console 调试时即交互桌面；
            // 服务 SYSTEM 会话的后台脚本则继承非交互窗口站，符合 RunInSystemSession 语义）。
            return LaunchCore(options, job, envBlock, desktop, (flags, inherit, ref si) =>
            {
                var ok = NativeMethods.CreateProcessW(
                    options.FileName, BuildCommandLine(options),
                    IntPtr.Zero, IntPtr.Zero, inherit, flags, envBlock,
                    ResolveWorkingDirectory(options), ref si, out var pi);
                return ok ? pi : throw new Win32Exception(Marshal.GetLastWin32Error(),
                    $"启动进程失败：{options.FileName}");
            });
        }
        finally
        {
            EnvironmentBlock.Free(envBlock);
        }
    }

    /// <summary>
    /// 以指定用户令牌（活动登录会话）创建进程、加入指定 Job 后恢复（A3）。
    /// 环境块取自用户配置文件（USERPROFILE/APPDATA/TEMP 等正确），再叠加脚本自定义变量。
    /// </summary>
    public static LaunchedProcess LaunchAsUser(
        LaunchOptions options, SafeFileHandle userToken, JobObject? job = null)
    {
        if (!NativeMethods.CreateEnvironmentBlock(out var userEnv, userToken, false))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "创建用户环境块失败。");

        IntPtr envBlock = IntPtr.Zero;
        try
        {
            envBlock = EnvironmentBlock.RebuildWithOverrides(userEnv, options.EnvironmentVariables);

            return LaunchCore(options, job, envBlock, desktop: @"winsta0\default", (flags, inherit, ref si) =>
            {
                var ok = NativeMethods.CreateProcessAsUserW(
                    userToken,
                    options.FileName, BuildCommandLine(options),
                    IntPtr.Zero, IntPtr.Zero, inherit, flags, envBlock,
                    ResolveWorkingDirectory(options), ref si, out var pi);
                return ok ? pi : throw new Win32Exception(Marshal.GetLastWin32Error(),
                    $"以登录用户身份启动进程失败：{options.FileName}");
            });
        }
        finally
        {
            EnvironmentBlock.Free(envBlock);
            NativeMethods.DestroyEnvironmentBlock(userEnv);
        }
    }

    private static LaunchedProcess LaunchCore(
        LaunchOptions options, JobObject? job, IntPtr envBlock, string? desktop, CreateProcessDelegate create)
    {
        // S2：按 PE 子系统区分窗口策略——
        // 控制台程序（ping/cmd/powershell/批处理宿主）：CREATE_NO_WINDOW + SW_HIDE 防黑框；
        // GUI 程序（记事本/Qt/.NET WinForms/WPF…）：两者都不能给，否则首个窗口可能被隐藏。
        var isGui = PeImageInfo.IsWindowsGui(options.FileName);

        var si = new STARTUPINFO
        {
            cb = Marshal.SizeOf<STARTUPINFO>(),
            lpDesktop = desktop
        };

        var flags = NativeMethods.CREATE_SUSPENDED
                    | NativeMethods.CREATE_UNICODE_ENVIRONMENT
                    | NativeMethods.CREATE_NEW_PROCESS_GROUP;

        if (!isGui)
        {
            si.dwFlags = NativeMethods.STARTF_USESHOWWINDOW;
            si.wShowWindow = NativeMethods.SW_HIDE;
            flags |= NativeMethods.CREATE_NO_WINDOW;
        }

        var inheritHandles = false;
        SafeFileHandle? readPipe = null;
        SafeFileHandle? writePipe = null;
        SafeFileHandle? stdinRead = null;
        SafeFileHandle? stdinWrite = null;

        try
        {
            if (options.RedirectOutput)
            {
                if (!NativeMethods.CreatePipe(out readPipe, out writePipe, IntPtr.Zero, 0))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "创建输出管道失败。");

                // 注意：CreatePipe 传 NULL 安全属性时，两端句柄默认都【不可继承】，
                // 必须显式把"交给子进程"的写端标为可继承，否则子进程拿不到 stdout/stderr，
                // 父端关闭写端后读端立即 EOF（实测）。读端保持不可继承。
                if (!NativeMethods.SetHandleInformation(writePipe,
                        NativeMethods.HANDLE_FLAG_INHERIT, NativeMethods.HANDLE_FLAG_INHERIT))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "设置管道句柄继承失败。");
                if (!NativeMethods.SetHandleInformation(readPipe, NativeMethods.HANDLE_FLAG_INHERIT, 0))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "设置管道句柄继承失败。");

                // stdin 也给一根有效管道（读端可继承，父端立即关闭 => 子进程读到 EOF）。
                // 给 NULL 句柄会导致 cmd 的 start 等再派生命令时复制句柄失败。
                if (!NativeMethods.CreatePipe(out stdinRead, out stdinWrite, IntPtr.Zero, 0))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "创建输入管道失败。");
                if (!NativeMethods.SetHandleInformation(stdinRead,
                        NativeMethods.HANDLE_FLAG_INHERIT, NativeMethods.HANDLE_FLAG_INHERIT))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "设置管道句柄继承失败。");
                if (!NativeMethods.SetHandleInformation(stdinWrite, NativeMethods.HANDLE_FLAG_INHERIT, 0))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "设置管道句柄继承失败。");

                si.dwFlags |= NativeMethods.STARTF_USESTDHANDLES;
                si.hStdOutput = writePipe.DangerousGetHandle();
                si.hStdError = writePipe.DangerousGetHandle();
                si.hStdInput = stdinRead.DangerousGetHandle();
                inheritHandles = true;
            }

            var pi = create(flags, inheritHandles, ref si);

            var processHandle = new SafeFileHandle(pi.hProcess, ownsHandle: true);
            var threadHandle = new SafeFileHandle(pi.hThread, ownsHandle: true);
            var success = false;
            try
            {
                if (job is not null)
                    job.Assign(processHandle);

                if (NativeMethods.ResumeThread(threadHandle) == uint.MaxValue)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "恢复进程主线程失败。");

                var result = new LaunchedProcess { Pid = pi.dwProcessId, ProcessHandle = processHandle };

                if (readPipe is not null)
                {
                    result.SetOutputReadHandle(readPipe);
                    readPipe = null; // 所有权转交 result
                }

                success = true;
                return result;
            }
            finally
            {
                threadHandle.Dispose();
                if (!success) processHandle.Dispose();
            }
        }
        finally
        {
            writePipe?.Dispose(); // 父端写端必须关闭，否则读端永远收不到 EOF
            readPipe?.Dispose();
            stdinWrite?.Dispose();
            stdinRead?.Dispose(); // 子进程持有继承的读端副本；父端关闭后子读 stdin 即 EOF
        }
    }

    private static string BuildCommandLine(LaunchOptions options)
        => $"{CommandLineBuilder.Quote(options.FileName)} {options.Arguments}";

    /// <summary>
    /// 解析子进程工作目录（M1）：显式配置优先；否则取**脚本文件所在目录**，
    /// 与旧版及脚本作者预期一致（.bat/.ps1 内相对路径相对脚本目录）；
    /// 拿不到脚本目录（直接启动 exe 且无路径信息）才退回宿主可执行文件目录。
    /// </summary>
    internal static string? ResolveWorkingDirectory(LaunchOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.WorkingDirectory))
            return options.WorkingDirectory;

        var scriptDir = Path.GetDirectoryName(options.ScriptPath);
        if (!string.IsNullOrWhiteSpace(scriptDir))
            return scriptDir;

        return Path.GetDirectoryName(options.FileName);
    }
}
