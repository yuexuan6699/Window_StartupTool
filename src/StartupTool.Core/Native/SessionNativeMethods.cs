using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace StartupTool.Core.Native;

/// <summary>
/// 会话感知启动相关 Win32 API（A3）：WTS 活动会话查询、用户令牌、用户环境块、CreateProcessAsUser。
/// </summary>
internal static partial class NativeMethods
{
    private const string WtsApi32 = "wtsapi32.dll";
    private const string AdvApi32 = "advapi32.dll";
    private const string UserEnv = "userenv.dll";

    // ---- 终端服务（WTS）----

    // 注意：WTSGetActiveConsoleSessionId 由 kernel32.dll 导出（不是 wtsapi32.dll，
    // 写错 DLL 会在首次调用时抛 EntryPointNotFoundException）。
    [LibraryImport(Kernel32)]
    internal static partial int WTSGetActiveConsoleSessionId();

    internal const int INVALID_SESSION_ID = -1;

    [LibraryImport(WtsApi32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WTSQueryUserToken(int sessionId, out IntPtr phToken);

    [LibraryImport(WtsApi32, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WTSQuerySessionInformation(
        IntPtr hServer, int sessionId, WtsInfoClass wtsInfoClass,
        out IntPtr ppBuffer, out int pBytesReturned);

    [LibraryImport(WtsApi32)]
    internal static partial void WTSFreeMemory(IntPtr pMemory);

    internal enum WtsInfoClass
    {
        WtsInitialProgram = 0,
        WtsApplicationName = 1,
        WtsWorkingDirectory = 2,
        WtsUserName = 5,
        WtsWinStationName = 6,
        WtsDomainName = 7
    }

    // ---- 用户环境块 ----

    [LibraryImport(UserEnv, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CreateEnvironmentBlock(
        out IntPtr lpEnvironment, SafeFileHandle hToken, [MarshalAs(UnmanagedType.Bool)] bool bInherit);

    [LibraryImport(UserEnv, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

    // ---- 以指定用户令牌创建进程 ----

    // STARTUPINFO 含 string 字段，沿用经典 DllImport（同 CreateProcessW）
    [DllImport(AdvApi32, SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateProcessAsUserW(
        SafeFileHandle hToken,
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    // ---- 句柄 ----

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(IntPtr hObject);
}
