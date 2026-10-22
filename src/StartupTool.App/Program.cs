using System.Diagnostics;
using System.IO;
using System.Linq;
using StartupTool.Installation;
using StartupTool.Service;

namespace StartupTool.App;

/// <summary>
/// 程序唯一入口：单 exe 多模式（P2）。
/// <list type="bullet">
/// <item><c>StartupTool.exe --service</c>：Windows 服务模式（SYSTEM，开机即启，唯一权威所有者）。</item>
/// <item><c>StartupTool.exe --console</c>：控制台调试模式（免安装跑同一套宿主，脚本同会话启动）。</item>
/// <item><c>StartupTool.exe --install / --uninstall</c>：注册/删除 Windows 服务（需管理员）。</item>
/// <item><c>StartupTool.exe --beat &lt;令牌&gt; &lt;时间戳&gt;</c>：脚本心跳一次性投递进程（M3 实现），投递后退出。</item>
/// <item>无参：WPF 控制端（托盘 + 主窗口，只通过命名管道下发命令，自身绝不启停脚本）。</item>
/// <item><c>--minimized</c>（等同 <c>--tray</c>）：控制端启动后只驻留托盘，不显示主窗口（登录自启用）。</item>
/// </list>
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 &&
            args[0].Equals("--service", StringComparison.OrdinalIgnoreCase))
        {
            ServiceHost.RunAsync(args, consoleMode: false).GetAwaiter().GetResult();
            return 0;
        }

        if (args.Length > 0 &&
            args[0].Equals("--console", StringComparison.OrdinalIgnoreCase))
        {
            ServiceHost.RunAsync(args, consoleMode: true).GetAwaiter().GetResult();
            return 0;
        }

        if (args.Length > 0 &&
            args[0].Equals("--install", StringComparison.OrdinalIgnoreCase))
        {
            ParentConsole.TryAttach();
            var exePath = Environment.ProcessPath
                ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
            Console.WriteLine("=== StartupTool 服务安装（需管理员）===");
            return ServiceInstaller.Install(exePath, Console.Out);
        }

        if (args.Length > 0 &&
            args[0].Equals("--uninstall", StringComparison.OrdinalIgnoreCase))
        {
            ParentConsole.TryAttach();

            // 卸载即删（MSI U11）：若直接运行安装目录下的 exe，会锁住自身，导致 MSI 的
            // RemoveFiles 只能把该 exe 标记为"重启后删除"。此处先把自身复制到临时目录，
            // 再从临时副本执行清理以释放安装目录 exe 的锁。内部标记 --from-temp-copy
            // 防止临时副本再次复制（递归）。
            if (!args.Contains("--from-temp-copy", StringComparer.OrdinalIgnoreCase)
                && TrySelfRelocateToTemp(out var relocExit))
            {
                return relocExit;
            }

            Console.WriteLine("=== StartupTool 服务卸载（需管理员）===");
            return ServiceInstaller.Uninstall(Console.Out);
        }

        if (args.Length > 0 &&
            args[0].Equals("--beat", StringComparison.OrdinalIgnoreCase))
        {
            return BeatCommand.Run(args);
        }

        // B8：每会话单例——第二个实例只通知已有窗口显示，然后退出（不依赖窗口标题/语言）
        using var singleton = new SessionSingleton();
        if (!singleton.IsFirstInstance)
        {
            SessionSingleton.SignalExistingInstance();
            return 0;
        }

        // 登录自启：只驻留托盘、不弹出主窗口（HKLM Run 写入的本参数）
        var startMinimized = args.Any(a =>
            a.Equals("--minimized", StringComparison.OrdinalIgnoreCase)
            || a.Equals("--tray", StringComparison.OrdinalIgnoreCase));

        var app = new App
        {
            SessionSingleton = singleton,
            StartMinimized = startMinimized
        };
        app.InitializeComponent();
        app.Run();
        return 0;
    }

    /// <summary>
    /// 卸载即删（MSI U11）：把自身复制到临时目录，再从临时副本重新执行 <c>--uninstall --from-temp-copy</c>，
    /// 从而不锁定安装目录下的 exe。返回是否已走"副本清理"路径（true=已处理，out 为副本清理的退出码；
    /// false=复制失败，调用方应回退为直接卸载）。
    /// </summary>
    private static bool TrySelfRelocateToTemp(out int exitCode)
    {
        exitCode = 0;
        var selfPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(selfPath))
        {
            Console.WriteLine("警告：无法确定自身路径，回退为直接卸载。");
            return false;
        }

        var tempExe = Path.Combine(Path.GetTempPath(), $"StartupTool-{Guid.NewGuid():N}.exe");
        try
        {
            File.Copy(selfPath, tempExe, overwrite: true);

            var psi = new ProcessStartInfo
            {
                FileName = tempExe,
                Arguments = "--uninstall --from-temp-copy",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process is null)
            {
                exitCode = 1;
                return true;
            }

            if (!process.WaitForExit(120_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* 已退出竞态 */ }
                exitCode = 1;
                return true;
            }

            exitCode = process.ExitCode;
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"警告：无法复制到临时目录执行卸载（{ex.Message}），回退为直接卸载。");
            return false;
        }
        finally
        {
            try { File.Delete(tempExe); } catch { /* 临时副本可能仍被占用，忽略 */ }
        }
    }
}
