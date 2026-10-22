using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace StartupTool.App;

/// <summary>
/// 本程序是 WinExe（控制端不弹黑窗），但 --beat 被脚本从控制台调用时，
/// 需要附加到父进程控制台并把 CONOUT$ 设为标准输出，错误原因才可见。
/// 退出码始终有效，输出仅为辅助。
/// </summary>
internal static class ParentConsole
{
    private const int ATTACH_PARENT_PROCESS = -1;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    public static void TryAttach()
    {
        try
        {
            if (!AttachConsole(ATTACH_PARENT_PROCESS)) return;

            var handle = CreateFileW("CONOUT$",
                GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return;

            var stream = new FileStream(new SafeFileHandle(handle, ownsHandle: true), FileAccess.Write);
            var writer = new StreamWriter(stream, Console.OutputEncoding) { AutoFlush = true };
            Console.SetOut(writer);
            Console.SetError(writer);
        }
        catch
        {
            // 没有可附加的控制台（如被 wscript/资源管理器调用）：静默，仅靠退出码反馈
        }
    }
}
