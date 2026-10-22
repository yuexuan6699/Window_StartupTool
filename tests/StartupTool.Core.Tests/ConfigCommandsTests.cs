using System.Diagnostics;
using Xunit;
using StartupTool.Core.Configuration;
using StartupTool.Core.Ipc;
using StartupTool.Core.Models;
using StartupTool.Core.Monitoring;
using StartupTool.Core.Native;
using StartupTool.Core.Supervision;

namespace StartupTool.Core.Tests;

/// <summary>
/// M5 配置类管道命令端到端：增删改/排序/设置读写/心跳令牌/外部进程显式终止，
/// 以及 supervisor 对配置变更的热应用（删脚本即停、名称与守护属性即时刷新）。
/// </summary>
public class ConfigCommandsTests : IDisposable
{
    private readonly string _dir;
    private readonly string _pipeName;
    private readonly ConfigService _config;
    private readonly ScriptSupervisor _supervisor;
    private readonly IpcServer _server;
    private readonly IpcClient _client;
    private readonly List<Process> _spawned = new();

    private sealed class RelaySink : ISupervisorEventSink
    {
        public ISupervisorEventSink? Target { get; set; }
        public void Emit(SupervisorEvent e) => Target?.Emit(e);
    }

    public ConfigCommandsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "st cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var configPath = Path.Combine(_dir, "scripts.json");
        _config = new ConfigService(configPath, configPath + ".bak", enableWatcher: false);

        _pipeName = "st-test-" + Guid.NewGuid().ToString("N");
        var relay = new RelaySink();
        _supervisor = new ScriptSupervisor(_config, relay, new LocalSessionContext());
        _server = new IpcServer(_supervisor, _config, _pipeName, new LocalSessionContext(),
            interactiveServer: true);
        relay.Target = _server.EventSink;
        _server.Start();

        _client = new IpcClient(_pipeName);
        _client.ConnectAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        _client.SubscribeAsync(Process.GetCurrentProcess().SessionId).GetAwaiter().GetResult();
    }

    private string WriteLongBat(string fileName)
    {
        var path = Path.Combine(_dir, fileName);
        File.WriteAllText(path, "@echo off\r\nping -n 120 127.0.0.1\r\n");
        return path;
    }

    private static async Task<bool> WaitForAsync(
        Func<Task<bool>> condition, int timeoutMs = 20000, int stepMs = 200)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (await condition()) return true;
            await Task.Delay(stepMs);
        }
        return await condition();
    }

    private static bool WaitFor(Func<bool> condition, int timeoutMs = 15000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(200);
        }
        return condition();
    }

    [Fact]
    public async Task Upsert_新增脚本_出现在状态列表()
    {
        var path = WriteLongBat("new.bat");
        var script = new ScriptConfig { Name = "新脚本", Path = path };

        await _client.UpsertScriptAsync(script);

        var states = await _client.GetStatesAsync();
        Assert.Contains(states, s => s.Id == script.Id && s.Name == "新脚本");
        // 落盘确认（服务端权威文件）
        Assert.Contains(_config.Current.Scripts, s => s.Id == script.Id);
    }

    [Fact]
    public async Task Upsert_空名称或空路径_拒绝()
    {
        var path = WriteLongBat("x.bat");

        var badName = new ScriptConfig { Name = "  ", Path = path };
        var ex1 = await Assert.ThrowsAsync<IpcAckException>(() => _client.UpsertScriptAsync(badName));
        Assert.Equal("invalidPayload", ex1.Code);

        var badPath = new ScriptConfig { Name = "n", Path = "   " };
        var ex2 = await Assert.ThrowsAsync<IpcAckException>(() => _client.UpsertScriptAsync(badPath));
        Assert.Equal("invalidPayload", ex2.Code);
    }

    [Fact]
    public async Task Upsert_更新名称_快照即时刷新()
    {
        var path = WriteLongBat("rename.bat");
        var script = new ScriptConfig { Name = "旧名", Path = path };
        await _client.UpsertScriptAsync(script);

        script.Name = "新名";
        await _client.UpsertScriptAsync(script);

        var states = await _client.GetStatesAsync();
        Assert.Equal("新名", states.Single(s => s.Id == script.Id).Name);
    }

    [Fact]
    public async Task Delete_运行中的脚本_被停止并从列表摘除()
    {
        var path = WriteLongBat("running.bat");
        var script = new ScriptConfig { Name = "run", Path = path, CrashRestart = CrashPolicy.Never };
        await _client.UpsertScriptAsync(script);
        await _client.StartScriptAsync(script.Id);

        Assert.True(await WaitForAsync(async () =>
            (await _client.GetStatesAsync()).First(s => s.Id == script.Id).State == ScriptState.Running));

        await _client.DeleteScriptAsync(script.Id);

        // 配置变更热应用：实例停止
        Assert.True(await WaitForAsync(async () =>
            (await _client.GetStatesAsync()).All(s => s.Id != script.Id)));
        Assert.DoesNotContain(_config.Current.Scripts, s => s.Id == script.Id);
    }

    [Fact]
    public async Task Delete_不存在的Id_返回notFound()
    {
        var ex = await Assert.ThrowsAsync<IpcAckException>(
            () => _client.DeleteScriptAsync("nope"));
        Assert.Equal("notFound", ex.Code);
    }

    [Fact]
    public async Task Reorder_按拖拽顺序落盘()
    {
        var path = WriteLongBat("p.bat");
        var a = new ScriptConfig { Name = "A", Path = path };
        var b = new ScriptConfig { Name = "B", Path = path };
        var c = new ScriptConfig { Name = "C", Path = path };
        foreach (var s in new[] { a, b, c }) await _client.UpsertScriptAsync(s);

        await _client.ReorderScriptsAsync(new[] { c.Id, a.Id, b.Id });

        var states = await _client.GetStatesAsync();
        Assert.Equal(new[] { "C", "A", "B" }, states.Select(s => s.Name).ToArray());
    }

    [Fact]
    public async Task GetConfig_与_UpdateSettings_往返()
    {
        var cfg = await _client.GetConfigAsync();
        Assert.True(cfg.Settings.NotifyOnFault); // 默认开

        cfg.Settings.NotifyOnFault = false;
        cfg.Settings.Language = "en";
        await _client.UpdateSettingsAsync(cfg.Settings);

        var again = await _client.GetConfigAsync();
        Assert.False(again.Settings.NotifyOnFault);
        Assert.Equal("en", again.Settings.Language);
    }

    [Fact]
    public async Task 心跳令牌_获取即生成_重置后变化_不存在拒绝()
    {
        var path = WriteLongBat("tok.bat");
        var script = new ScriptConfig { Name = "tok", Path = path };
        await _client.UpsertScriptAsync(script);

        var t1 = await _client.GetHeartbeatTokenAsync(script.Id);
        Assert.Equal(64, t1.Length);
        // 再次获取保持不变
        Assert.Equal(t1, await _client.GetHeartbeatTokenAsync(script.Id));

        var t2 = await _client.ResetHeartbeatTokenAsync(script.Id);
        Assert.Equal(64, t2.Length);
        Assert.NotEqual(t1, t2);

        var ex = await Assert.ThrowsAsync<IpcAckException>(
            () => _client.GetHeartbeatTokenAsync("ghost"));
        Assert.Equal("notFound", ex.Code);
    }

    [Fact]
    public async Task 外部同名进程_只读列出_显式终止后消失()
    {
        // 不经 supervisor，直接在外部启动同名 bat（cmd 包装，命令行含脚本路径）
        var path = WriteLongBat("external.bat");
        var script = new ScriptConfig { Name = "ext", Path = path };
        await _client.UpsertScriptAsync(script);

        var psi = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true };
        var p = Process.Start(psi)!;
        _spawned.Add(p);

        // WMI 有采集延迟，轮询直到列出
        IReadOnlyList<ExternalProcessInfo>? found = null;
        var seen = await WaitForAsync(async () =>
        {
            found = await _client.GetExternalProcessesAsync();
            return found.Any(x => x.ScriptId == script.Id);
        }, timeoutMs: 30000);
        Assert.True(seen, "未扫描到外部同名进程");

        var target = found!.First(x => x.ScriptId == script.Id);
        await _client.KillExternalAsync(target.Pid, target.StartedAtUtc);

        Assert.True(WaitFor(() =>
        {
            try { p.Refresh(); return p.HasExited; }
            catch { return true; }
        }, 10000), "外部进程未被终止");
    }

    [Fact]
    public async Task Upsert_更新脚本_令牌与Order保留_依赖等字段可编辑_N1_N3_D3_D4_D6()
    {
        var path = WriteLongBat("n1.bat");
        var script = new ScriptConfig { Name = "orig", Path = path };
        await _client.UpsertScriptAsync(script);
        var token = await _client.GetHeartbeatTokenAsync(script.Id);

        // 真实存在的依赖目标：D3/D4/D6 起 依赖/提权/延迟/等网络 已是编辑窗可编辑字段，
        // 服务端不再用旧值覆盖（保留清单只剩 Order 与 HeartbeatToken）
        var dep = new ScriptConfig { Name = "dep", Path = path };
        await _client.UpsertScriptAsync(dep);

        // 模拟 N1 场景：客户端只拿到残缺对象（类似 UI 兜底 {Id,Name}），且伪造了心跳令牌（N3）
        var tampered = new ScriptConfig
        {
            Id = script.Id,
            Name = "renamed",
            Path = path,
            HeartbeatToken = new string('a', 64), // 伪造令牌，必须被忽略
            DependsOn = new List<string> { dep.Id, "no-such-script" }, // 悬空引用应被清理
            RequireElevation = true,
            InitialDelaySeconds = 7,
            WaitForNetwork = true,
            MaxCpuPercent = 42
        };
        await _client.UpsertScriptAsync(tampered);

        var saved = _config.Current.Scripts.Single(s => s.Id == script.Id);
        Assert.Equal("renamed", saved.Name);            // 可编辑字段生效
        Assert.Equal(42, saved.MaxCpuPercent);
        Assert.Equal(token, saved.HeartbeatToken);       // N3：令牌不被覆盖
        Assert.Equal(0, saved.Order);                   // Order 仍只能经 reorder 改
        // D3/D4/D6：这些字段现在接受客户端新值；悬空依赖引用被清理
        Assert.Equal(new[] { dep.Id }, saved.DependsOn);
        Assert.True(saved.RequireElevation);
        Assert.Equal(7, saved.InitialDelaySeconds);
        Assert.True(saved.WaitForNetwork);
    }

    [Fact]
    public async Task Upsert_依赖成环_被拒绝且不落盘_D4()
    {
        var path = WriteLongBat("cyc.bat");
        var a = new ScriptConfig { Name = "a", Path = path };
        var b = new ScriptConfig { Name = "b", Path = path };
        await _client.UpsertScriptAsync(a);
        await _client.UpsertScriptAsync(b);

        // b 依赖 a（合法）
        await _client.UpsertScriptAsync(new ScriptConfig
        {
            Id = b.Id, Name = "b", Path = path, DependsOn = new List<string> { a.Id }
        });

        // a 依赖 b ⇒ 成环，必须被拒且不落盘（否则每次启动都会等满依赖上限后放弃）
        var reply = await _client.InvokeRawAsync(IpcTypes.UpsertScript, new ScriptConfig
        {
            Id = a.Id, Name = "a", Path = path, DependsOn = new List<string> { b.Id }
        });
        var ack = reply.ReadPayload<AckPayload>();
        Assert.NotNull(ack);
        Assert.False(ack!.Ok);
        Assert.Equal("invalidPayload", ack.Code);

        // 磁盘与内存配置都未被污染
        Assert.Empty(_config.Current.Scripts.Single(s => s.Id == a.Id).DependsOn);
        Assert.Equal(new[] { a.Id }, _config.Current.Scripts.Single(s => s.Id == b.Id).DependsOn);
    }

    [Fact]
    public async Task Upsert_新建脚本_自带令牌被忽略_N3()
    {
        var path = WriteLongBat("n3.bat");
        var forged = new string('f', 64);
        var script = new ScriptConfig { Name = "n3", Path = path, HeartbeatToken = forged };
        await _client.UpsertScriptAsync(script);

        var saved = _config.Current.Scripts.Single(s => s.Id == script.Id);
        Assert.Equal(string.Empty, saved.HeartbeatToken); // 不接受客户端自带令牌

        var generated = await _client.GetHeartbeatTokenAsync(script.Id);
        Assert.Equal(64, generated.Length);
        Assert.NotEqual(forged, generated);
    }

    [Fact]
    public async Task UpdateSettings_非法值_拒绝且旧值保留_N2()
    {
        var original = (await _client.GetConfigAsync()).Settings;

        var badWindow = new GlobalSettings
        {
            NotifyOnFault = original.NotifyOnFault,
            Language = original.Language,
            LogMaxSizeMb = original.LogMaxSizeMb,
            LogRetentionDays = original.LogRetentionDays,
            CircuitWindowSeconds = 0, // 会导致任何启动立即熔断
            CircuitMaxStarts = original.CircuitMaxStarts
        };
        var ex1 = await Assert.ThrowsAsync<IpcAckException>(
            () => _client.UpdateSettingsAsync(badWindow));
        Assert.Equal("invalidPayload", ex1.Code);

        var badLang = new GlobalSettings
        {
            NotifyOnFault = original.NotifyOnFault,
            Language = "fr-FR",
            LogMaxSizeMb = original.LogMaxSizeMb,
            LogRetentionDays = original.LogRetentionDays,
            CircuitWindowSeconds = original.CircuitWindowSeconds,
            CircuitMaxStarts = original.CircuitMaxStarts
        };
        var ex2 = await Assert.ThrowsAsync<IpcAckException>(
            () => _client.UpdateSettingsAsync(badLang));
        Assert.Equal("invalidPayload", ex2.Code);

        // 合法值照常通过
        var ok = new GlobalSettings
        {
            NotifyOnFault = false,
            Language = "en",
            LogMaxSizeMb = 10,
            LogRetentionDays = 14,
            CircuitWindowSeconds = 30,
            CircuitMaxStarts = 5
        };
        await _client.UpdateSettingsAsync(ok);
        var after = (await _client.GetConfigAsync()).Settings;
        Assert.Equal(30, after.CircuitWindowSeconds);
        Assert.Equal("en", after.Language);
        Assert.False(after.NotifyOnFault);

        // null 语言（跟随系统）也必须被接受不报错（其持久化往返的已知行为另见检查记录）
        var follow = new GlobalSettings
        {
            NotifyOnFault = false,
            Language = null,
            LogMaxSizeMb = 10,
            LogRetentionDays = 14,
            CircuitWindowSeconds = 30,
            CircuitMaxStarts = 5
        };
        await _client.UpdateSettingsAsync(follow);

        // 被拒的两次没有污染已保存配置
        Assert.Equal(30, _config.Current.Settings.CircuitWindowSeconds);
    }

    public void Dispose()
    {
        foreach (var p in _spawned)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            p.Dispose();
        }
        try { _supervisor.StopAllAsync().Wait(8000); } catch { }
        _client.Dispose();
        _server.Dispose();
        _supervisor.Dispose();
        _config.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
