using System.Diagnostics;
using Xunit;
using StartupTool.Core.Models;
using StartupTool.Core.Monitoring;

namespace StartupTool.Core.Tests;

/// <summary>
/// A8 外部同名进程扫描器测试：同路径多脚本归属、托管 PID 排除。
/// </summary>
public class ExternalProcessScannerTests : IDisposable
{
    private readonly string _dir;
    private Process? _spawned;

    public ExternalProcessScannerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "st-ext-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public void 同一路径被多个脚本引用_外部进程对每个脚本都可见()
    {
        // 复制 ping.exe 为唯一名，避免扫到机器上其它 ping 进程
        var exe = Path.Combine(_dir, "probe-" + Guid.NewGuid().ToString("N") + ".exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), exe);

        _spawned = Process.Start(new ProcessStartInfo(exe, "-n 30 127.0.0.1") { UseShellExecute = false });
        Assert.NotNull(_spawned);

        var s1 = new ScriptConfig { Id = "script-a", Name = "甲", Path = exe };
        var s2 = new ScriptConfig { Id = "script-b", Name = "乙", Path = exe };

        using var scanner = new ExternalProcessScanner();

        // WMI 对新进程可见性有延迟，轮询等待
        List<ExternalProcessInfo> hits = new();
        var deadline = Environment.TickCount64 + 10000;
        while (Environment.TickCount64 < deadline)
        {
            hits = scanner.Scan(new[] { s1, s2 }, new HashSet<int>()).ToList();
            if (hits.Any(h => h.Pid == _spawned.Id && h.ScriptId == "script-a")
                && hits.Any(h => h.Pid == _spawned.Id && h.ScriptId == "script-b"))
                break;
            Thread.Sleep(300);
        }

        Assert.Contains(hits, h => h.Pid == _spawned.Id && h.ScriptId == "script-a");
        Assert.Contains(hits, h => h.Pid == _spawned.Id && h.ScriptId == "script-b");

        // 该 PID 在托管集合内时，两个脚本窗口都不应再看到它
        var managed = scanner.Scan(new[] { s1, s2 }, new HashSet<int> { _spawned.Id });
        Assert.DoesNotContain(managed, h => h.Pid == _spawned.Id);
    }

    /// <summary>
    /// M11：脚本配置指向目录内唯一 exe 时，系统目录同名解释器（ping.exe）不得误报；
    /// 只有与脚本路径完全一致的外部进程才列出。
    /// </summary>
    [Fact]
    public void 不同路径的同名exe_不误报_同路径才报_M11()
    {
        var scriptExe = Path.Combine(_dir, "probe-" + Guid.NewGuid().ToString("N") + ".exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), scriptExe);
        var script = new ScriptConfig { Id = "s1", Name = "s1", Path = scriptExe };

        // ① 只有系统 ping.exe 在跑：不同路径、命令行不含脚本路径，不应匹配
        var systemPing = Process.Start(new ProcessStartInfo(
            Path.Combine(Environment.SystemDirectory, "ping.exe"), "-n 30 127.0.0.1")
        { UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            // 给 WMI 首次枚举留时间，连续多拍确认"扫得到进程但匹配数恒为 0"
            var cleanRounds = 0;
            var deadline = Environment.TickCount64 + 10000;
            while (Environment.TickCount64 < deadline && cleanRounds < 3)
            {
                Thread.Sleep(400);
                var hits = new ExternalProcessScanner().Scan(new[] { script }, new HashSet<int>());
                if (hits.Count == 0) cleanRounds++;
            }
            Assert.Equal(3, cleanRounds);

            // ② 启动与脚本同路径的外部 exe：应被列出
            var samePath = Process.Start(new ProcessStartInfo(scriptExe, "-n 30 127.0.0.1")
            { UseShellExecute = false, CreateNoWindow = true })!;
            _spawned = samePath;

            var matched = false;
            deadline = Environment.TickCount64 + 15000;
            while (Environment.TickCount64 < deadline)
            {
                Thread.Sleep(300);
                var hits = new ExternalProcessScanner().Scan(new[] { script }, new HashSet<int>());
                if (hits.Any(x => x.Pid == samePath.Id)) { matched = true; break; }
            }
            Assert.True(matched, "同路径外部 exe 未被识别");
        }
        finally
        {
            try { if (!systemPing.HasExited) systemPing.Kill(entireProcessTree: true); } catch { }
            systemPing.Dispose();
        }
    }

    public void Dispose()
    {
        try { _spawned?.Kill(entireProcessTree: true); } catch { /* 退出竞态忽略 */ }
        try { _spawned?.Dispose(); } catch { }
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
