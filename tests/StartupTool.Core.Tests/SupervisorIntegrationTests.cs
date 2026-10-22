using System.Collections.Concurrent;
using System.Diagnostics;
using Xunit;
using StartupTool.Core.Configuration;
using StartupTool.Core.Models;
using StartupTool.Core.Supervision;

namespace StartupTool.Core.Tests;

/// <summary>
/// M3 进程托管真实进程集成测试：Job 杀整树、自重启接续、自愈、熔断、心跳。
/// </summary>
public class SupervisorIntegrationTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;
    private readonly string _backupPath;
    private readonly ConfigService _config;
    private readonly CollectingSink _sink = new();
    private readonly ScriptSupervisor _supervisor;

    private sealed class CollectingSink : ISupervisorEventSink
    {
        public ConcurrentQueue<SupervisorEvent> Events { get; } = new();
        public void Emit(SupervisorEvent e) => Events.Enqueue(e);
    }

    public SupervisorIntegrationTests()
    {
        // 目录名故意带空格，顺带回归 B7
        _dir = Path.Combine(Path.GetTempPath(), "st itest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "scripts.json");
        _backupPath = _configPath + ".bak";
        _config = new ConfigService(_configPath, _backupPath, enableWatcher: false);
        _supervisor = new ScriptSupervisor(_config, _sink);
    }

    private ScriptConfig AddScript(string fileName, string content, Action<ScriptConfig>? setup = null)
    {
        var path = Path.Combine(_dir, fileName);
        File.WriteAllText(path, content);
        var script = new ScriptConfig
        {
            Name = Path.GetFileNameWithoutExtension(fileName),
            Path = path,
            CrashRestart = CrashPolicy.Never,
            RestartDelaySeconds = 1
        };
        setup?.Invoke(script);

        _config.Mutate(c => c.Scripts.Add(script));
        return script;
    }

    private ScriptRuntimeSnapshot? Snapshot(string id) =>
        _supervisor.GetStates().FirstOrDefault(s => s.Id == id);

    private static bool WaitFor(Func<bool> condition, int timeoutMs = 15000, int stepMs = 200)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(stepMs);
        }
        return condition();
    }

    private static bool IsProcessDead(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.HasExited;
        }
        catch (ArgumentException) { return true; }
        catch (InvalidOperationException) { return true; }
    }

    /// <summary>M8 残余：监控/心跳在锁外收集到重启 id 后，用户抢先停止——自动重启必须放弃，不得复活。</summary>
    [Fact]
    public async Task 用户已停止_自动重启请求不复活脚本_M8()
    {
        var script = AddScript("m8.bat",
            "@echo off\r\nping -n 60 127.0.0.1\r\n",
            s => s.RestartDelaySeconds = 0);
        await _supervisor.StartAsync(script.Id, manual: true);
        Assert.True(WaitFor(() => Snapshot(script.Id)?.State == ScriptState.Running));
        var oldPid = Snapshot(script.Id)!.Pid!.Value;

        // 用户先停止
        await _supervisor.StopAsync(script.Id);
        Assert.True(WaitFor(() => Snapshot(script.Id)?.State == ScriptState.Stopped, 5000));
        Assert.True(IsProcessDead(oldPid));

        // 模拟巡检锁外拿到超时/超限 id 后才调自动重启（HeartbeatTimeout/ResourceLimit 同理）
        await _supervisor.RestartAsync(script.Id, FaultReason.HeartbeatTimeout);

        // 等待一个监控拍，确认没有被拉起
        Thread.Sleep(1500);
        Assert.Equal(ScriptState.Stopped, Snapshot(script.Id)!.State);
        Assert.True(IsProcessDead(oldPid));

        // 对照：手动重启（FaultReason.None）不受影响，仍可重新启动
        await _supervisor.RestartAsync(script.Id, FaultReason.None);
        Assert.True(WaitFor(() => Snapshot(script.Id)?.State == ScriptState.Running));
    }

    /// <summary>
    /// M21：全部停止并行执行——多脚本关停总耗时不得随脚本数线性增长
    /// （旧串行实现对每个脚本最多等 StopTimeout=10s，N 个脚本会超过宿主 ShutdownTimeout）。
    /// </summary>
    [Fact]
    public async Task 全部停止_多脚本并行_均停到位()
    {
        var scripts = new List<ScriptConfig>
        {
            AddScript("all1.bat", "@echo off\r\nping -n 60 127.0.0.1\r\n"),
            AddScript("all2.bat", "@echo off\r\nping -n 60 127.0.0.1\r\n"),
            AddScript("all3.bat", "@echo off\r\nping -n 60 127.0.0.1\r\n")
        };
        foreach (var s in scripts)
            await _supervisor.StartAsync(s.Id, manual: true);
        Assert.True(WaitFor(() => scripts.All(s => Snapshot(s.Id)?.State == ScriptState.Running)));

        var sw = Stopwatch.StartNew();
        await _supervisor.StopAllAsync();
        sw.Stop();

        Assert.All(scripts, s => Assert.Equal(ScriptState.Stopped, Snapshot(s.Id)!.State));
        // 宽松上限：只拦"退化为串行/卡死"（串行最坏 3×10s=30s）
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15),
            $"StopAllAsync 耗时 {sw.Elapsed}，疑似退化为串行关停");
    }

    /// <summary>A4：停止 = 终止整个 Job，cmd 及其子进程 ping 一次清光。</summary>
    [Fact]
    public async Task 停止脚本_整棵进程树被杀光()
    {
        var script = AddScript("tree.bat",
            "@echo off\r\nping -n 30 127.0.0.1\r\n");
        await _supervisor.StartAsync(script.Id, manual: true);

        Assert.True(WaitFor(() => Snapshot(script.Id)?.State == ScriptState.Running));
        var pids = Snapshot(script.Id)!.AllPids;
        Assert.NotEmpty(pids);

        await _supervisor.StopAsync(script.Id);
        Assert.True(WaitFor(() => Snapshot(script.Id)?.State == ScriptState.Stopped, 5000));

        // Job 内所有 PID（cmd + 派生的 ping）都应消失
        Assert.True(WaitFor(() => pids.All(IsProcessDead), 8000),
            "仍有进程残留：" + string.Join(",", pids.Where(p => !IsProcessDead(p))));
    }

    /// <summary>A4 验收：脚本 start 自重启（根退出、新进程接续），状态保持运行中。</summary>
    [Fact]
    public async Task 脚本自行start重启_Job存活则保持运行中()
    {
        var script = AddScript("selfrestart.bat",
            "@echo off\r\nstart \"\" /b ping -n 20 127.0.0.1\r\nexit /b 0\r\n");
        await _supervisor.StartAsync(script.Id, manual: true);
        Assert.True(WaitFor(() => Snapshot(script.Id)?.State == ScriptState.Running));

        // 等 WatchLoop 至少完成一次"根已退出但 Job 非空"的轮询刷新
        Assert.True(WaitFor(() =>
        {
            var s = Snapshot(script.Id);
            return s is { State: ScriptState.Running } && s.AllPids.Length > 0;
        }, 6000));

        // 超过"2s 轮询 + 2s 复查"宽限后仍应 Running（Job 内有 ping 接续），且接力进程确实在 Job 内
        Thread.Sleep(5000);
        var snap = Snapshot(script.Id)!;
        Assert.Equal(ScriptState.Running, snap.State);
        Assert.NotEmpty(snap.AllPids);
        var job = _supervisor.GetJobForTest(script.Id);
        Assert.All(snap.AllPids, pid => Assert.True(job!.ContainsProcess(pid)));

        await _supervisor.StopAsync(script.Id);
        Assert.True(WaitFor(() => Snapshot(script.Id)?.State == ScriptState.Stopped, 8000));
        Assert.True(WaitFor(() => snap.AllPids.All(IsProcessDead), 8000));
    }

    /// <summary>D2：异常退出（退出码 1）+ OnFailure 策略 → 自动重启。</summary>
    [Fact]
    public async Task 异常退出_按策略自动重启()
    {
        var script = AddScript("crash.bat",
            "@echo off\r\nexit /b 1\r\n",
            s =>
            {
                s.CrashRestart = CrashPolicy.OnFailure;
                s.RestartDelaySeconds = 1;
                s.MaxRestartsPerWindow = 50; // 本测试不触发熔断
            });

        await _supervisor.StartAsync(script.Id, manual: true);
        Assert.True(WaitFor(() =>
        {
            var s = Snapshot(script.Id);
            return s is { RestartCount: >= 1 };
        }, 20000), "未观察到自动重启");

        Assert.Equal(ScriptState.Running, Snapshot(script.Id)!.State);
    }

    /// <summary>A7：窗口内重启过频 → Faulted，停止产进程。</summary>
    [Fact]
    public async Task 自重启死循环_熔断后停止守护()
    {
        var script = AddScript("loopcrash.bat",
            "@echo off\r\nexit /b 1\r\n",
            s =>
            {
                s.CrashRestart = CrashPolicy.Always;
                s.RestartDelaySeconds = 1;
                s.MaxRestartsPerWindow = 3;
            });

        await _supervisor.StartAsync(script.Id, manual: true);

        Assert.True(WaitFor(() => Snapshot(script.Id)?.State == ScriptState.Faulted, 45000),
            "未触发熔断");
        var fault = Snapshot(script.Id)!;
        Assert.NotNull(fault.LastFault);

        // 熔断后再等 6 秒，重启次数不应继续增长
        var countAtTrip = fault.RestartCount;
        Thread.Sleep(6000);
        Assert.Equal(countAtTrip, Snapshot(script.Id)!.RestartCount);
        Assert.Equal(ScriptState.Faulted, Snapshot(script.Id)!.State);
    }

    /// <summary>H5：按时心跳不判超时；停发则在宽限期后判定超时。</summary>
    [Fact]
    public async Task 心跳_正常不超时_停发则超时()
    {
        var script = AddScript("hb.bat",
            "@echo off\r\nping -n 60 127.0.0.1\r\n",
            s =>
            {
                s.HeartbeatEnabled = true;
                s.HeartbeatTimeoutSeconds = 2;
            });
        var token = _config.EnsureHeartbeatToken(script.Id);

        await _supervisor.StartAsync(script.Id, manual: true);
        Assert.True(WaitFor(() => Snapshot(script.Id)?.State == ScriptState.Running));

        // 持续心跳 6 秒（宽限期 = 2*2 = 4 秒），不应超时
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(BeatResult.Ok,
                _supervisor.Beat(token, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            var timeouts = _supervisor.CollectHeartbeatTimeouts(DateTime.UtcNow);
            Assert.DoesNotContain(script.Id, timeouts);
            Thread.Sleep(1500);
        }

        // 令牌与时间戳校验
        Assert.Equal(BeatResult.InvalidToken, _supervisor.Beat("x".PadRight(64, '0'),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        Assert.Equal(BeatResult.ClockSkew,
            _supervisor.Beat(token, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1000));

        // 停发：超过宽限期（4s）后必判超时
        Assert.True(WaitFor(() =>
            _supervisor.CollectHeartbeatTimeouts(DateTime.UtcNow).Contains(script.Id), 9000));
    }

    /// <summary>A7：可执行文件不存在 → 立即 Faulted，LastFault 提示路径，不残留进程。</summary>
    [Fact]
    public async Task 启动_文件不存在_状态为故障()
    {
        var missing = Path.Combine(_dir, "no-such-file-" + Guid.NewGuid().ToString("N") + ".exe");
        var script = new ScriptConfig
        {
            Name = "missing-file-demo",
            Path = missing,
            CrashRestart = CrashPolicy.Never,
            ManualEnabled = true
        };
        _config.Mutate(c => c.Scripts.Add(script));

        var ok = await _supervisor.StartAsync(script.Id, manual: true);
        Assert.False(ok);
        Assert.True(WaitFor(() => Snapshot(script.Id)?.State == ScriptState.Faulted, 5000));
        var snap = Snapshot(script.Id)!;
        Assert.NotNull(snap.LastFault);
        Assert.Contains(missing, snap.LastFault);
    }

    /// <summary>手动重启（FaultReason.None）：旧实例停止后，必须在重启延迟后自动拉起新实例。</summary>
    [Fact]
    public async Task 手动重启_延迟后脚本必须恢复运行()
    {
        var script = AddScript("manualrestart.bat",
            "@echo off\r\nping -n 60 127.0.0.1\r\n",
            s => s.RestartDelaySeconds = 1);
        await _supervisor.StartAsync(script.Id, manual: true);
        Assert.True(WaitFor(() => Snapshot(script.Id)?.State == ScriptState.Running));
        var oldPids = Snapshot(script.Id)!.AllPids;
        Assert.NotEmpty(oldPids);

        // 不 await 到完成也没关系；RestartAsync 内部先停旧实例，再在锁外等延迟后启动
        await _supervisor.RestartAsync(script.Id, FaultReason.None);

        // 关键回归：重启延迟后必须回到 Running，且是全新进程
        Assert.True(WaitFor(() =>
        {
            var s = Snapshot(script.Id);
            return s is { State: ScriptState.Running }
                   && s.AllPids.Length > 0
                   && !s.AllPids.Any(oldPids.Contains);
        }, 15000), "重启后脚本未恢复运行（旧 UserIntendedStop 误取消待定重启回归）");

        Assert.True(WaitFor(() => oldPids.All(IsProcessDead), 8000), "旧进程未退出");
    }

    /// <summary>A5：手动重启与停止并发，最终只有一个结果且 Job 内无残留进程。</summary>
    [Fact]
    public async Task 重启与停止并发_无重复实例无残留()
    {
        var script = AddScript("concurrent.bat",
            "@echo off\r\nping -n 30 127.0.0.1\r\n");
        await _supervisor.StartAsync(script.Id, manual: true);
        Assert.True(WaitFor(() => Snapshot(script.Id)?.State == ScriptState.Running));

        // 同时下发重启与停止（信号量串行化，二者皆为合法终态）
        var restart = _supervisor.RestartAsync(script.Id, FaultReason.Crash);
        await _supervisor.StopAsync(script.Id);
        await restart;

        Assert.True(WaitFor(() => Snapshot(script.Id)?.State == ScriptState.Stopped, 8000));
        var allPids = _supervisor.GetStates().SelectMany(s => s.AllPids).ToArray();
        Assert.Empty(allPids);
    }

    public void Dispose()
    {
        try { _supervisor.StopAllAsync().Wait(8000); } catch { }
        _supervisor.Dispose();
        _config.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
