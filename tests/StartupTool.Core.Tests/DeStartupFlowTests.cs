using System.Diagnostics;
using Xunit;
using StartupTool.Core.Configuration;
using StartupTool.Core.Models;
using StartupTool.Core.Processes;
using StartupTool.Core.Supervision;

namespace StartupTool.Core.Tests;

/// <summary>
/// D 类收尾（D3 启动前延迟/等网络、D4 依赖就绪与依赖校验）的真实进程集成测试。
/// 超时通过构造函数注入缩短，避免用例等待 60 秒。
/// </summary>
public class DeStartupFlowTests : IDisposable
{
    private readonly string _dir;
    private readonly ConfigService _config;
    private readonly ScriptSupervisor _supervisor;

    public DeStartupFlowTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "st de-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new ConfigService(
            Path.Combine(_dir, "scripts.json"), Path.Combine(_dir, "scripts.json.bak"), enableWatcher: false);
        // 3 秒的依赖/网络等待上限，让"未就绪放弃启动"可以用例化验证
        _supervisor = new ScriptSupervisor(
            _config, sessionContext: null,
            dependencyTimeout: TimeSpan.FromSeconds(3),
            networkWaitTimeout: TimeSpan.FromSeconds(3));
    }

    public void Dispose()
    {
        _supervisor.Dispose();
        _config.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* 文件可能仍被占用 */ }
    }

    private ScriptConfig AddScript(string fileName, Action<ScriptConfig>? setup = null)
    {
        var path = Path.Combine(_dir, fileName);
        File.WriteAllText(path, "@echo off\r\nping -n 60 127.0.0.1\r\n");
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

    // ---------- D3：启动前延迟 / 等网络 ----------

    [Fact]
    public async Task 启动前延迟_真正生效_D3()
    {
        var script = AddScript("d3-delay.bat", s => s.InitialDelaySeconds = 2);

        var sw = Stopwatch.StartNew();
        var started = await _supervisor.StartAsync(script.Id, manual: true);
        sw.Stop();

        Assert.True(started);
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(1800), $"启动未被延迟：{sw.ElapsedMilliseconds}ms");
        Assert.True(WaitFor(() => Snapshot(script.Id)?.State == ScriptState.Running));
    }

    [Fact]
    public async Task 等网络_网络可用时正常启动_D3()
    {
        if (!NetworkReadiness.IsAvailable()) return; // 离线环境不适用

        var script = AddScript("d3-net.bat", s => s.WaitForNetwork = true);
        var sw = Stopwatch.StartNew();
        var started = await _supervisor.StartAsync(script.Id, manual: true);
        sw.Stop();

        // 网络可用时不应被阻塞
        Assert.True(started);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"网络可用时被莫名阻塞：{sw.ElapsedMilliseconds}ms");
        Assert.True(WaitFor(() => Snapshot(script.Id)?.State == ScriptState.Running));
    }

    // ---------- D4：依赖就绪 ----------

    [Fact]
    public async Task 依赖已就绪_可正常启动_D4()
    {
        var dep = AddScript("d4-dep.bat");
        var main = AddScript("d4-main.bat", s => s.DependsOn = new List<string> { dep.Id });

        // 依赖先起来
        Assert.True(await _supervisor.StartAsync(dep.Id, manual: true));
        Assert.True(WaitFor(() => Snapshot(dep.Id)?.State == ScriptState.Running));

        Assert.True(await _supervisor.StartAsync(main.Id, manual: true));
        Assert.True(WaitFor(() => Snapshot(main.Id)?.State == ScriptState.Running));
    }

    [Fact]
    public async Task 依赖未就绪_等满上限后放弃启动_D4()
    {
        var dep = AddScript("d4-dep2.bat"); // 不启动它
        var main = AddScript("d4-main2.bat", s => s.DependsOn = new List<string> { dep.Id });

        var sw = Stopwatch.StartNew();
        var started = await _supervisor.StartAsync(main.Id, manual: true);
        sw.Stop();

        Assert.False(started);                                        // 放弃启动
        Assert.True(sw.Elapsed >= TimeSpan.FromSeconds(2.5));         // 确实等满了上限
        Assert.True(WaitFor(() => Snapshot(main.Id)?.State == ScriptState.Stopped, 3000));
    }

    // ---------- D4：依赖校验器 ----------

    [Fact]
    public void 依赖校验器_两节点成环可识别()
    {
        var a = new ScriptConfig { Id = "a", Name = "A" };
        var b = new ScriptConfig { Id = "b", Name = "B", DependsOn = new List<string> { "a" } };
        a.DependsOn = new List<string> { "b" };

        var cycle = ScriptDependencyValidator.FindCycle(new[] { a, b });

        Assert.NotNull(cycle);
        Assert.Contains("A", cycle);
        Assert.Contains("B", cycle);
    }

    [Fact]
    public void 依赖校验器_三节点成环可识别_无环返回null()
    {
        var a = new ScriptConfig { Id = "a", Name = "A", DependsOn = new List<string> { "b" } };
        var b = new ScriptConfig { Id = "b", Name = "B", DependsOn = new List<string> { "c" } };
        var c = new ScriptConfig { Id = "c", Name = "C", DependsOn = new List<string> { "a" } };
        Assert.NotNull(ScriptDependencyValidator.FindCycle(new[] { a, b, c }));

        var ok1 = new ScriptConfig { Id = "1", Name = "一" };
        var ok2 = new ScriptConfig { Id = "2", Name = "二", DependsOn = new List<string> { "1" } };
        var ok3 = new ScriptConfig { Id = "3", Name = "三", DependsOn = new List<string> { "1", "2" } };
        Assert.Null(ScriptDependencyValidator.FindCycle(new[] { ok1, ok2, ok3 }));
    }

    [Fact]
    public void 依赖校验器_自依赖与悬空引用被清理()
    {
        var a = new ScriptConfig { Id = "a", DependsOn = new List<string> { "a", "no-such", "b" } };
        var b = new ScriptConfig { Id = "b" };

        Assert.True(ScriptDependencyValidator.NormalizeDependencies(new[] { a, b }));
        Assert.Equal(new[] { "b" }, a.DependsOn);   // 自依赖与悬空引用被删，真实依赖保留
        Assert.Null(ScriptDependencyValidator.FindCycle(new[] { a, b }));
    }

    // ---------- D3：网络探测（注入探针，不依赖真实网卡） ----------

    [Fact]
    public async Task 网络探测_已可用时立即返回()
    {
        var ok = await NetworkReadiness.WaitAsync(TimeSpan.FromSeconds(3), probe: () => true);
        Assert.True(ok);
    }

    [Fact]
    public async Task 网络探测_超时未可用时返回false()
    {
        var sw = Stopwatch.StartNew();
        var ok = await NetworkReadiness.WaitAsync(TimeSpan.FromMilliseconds(300), probe: () => false);
        sw.Stop();

        Assert.False(ok);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10)); // 不应无限等待
    }
}
