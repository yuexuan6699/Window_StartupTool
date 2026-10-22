using System.Collections.Concurrent;
using System.Diagnostics;
using Xunit;
using StartupTool.Core.Configuration;
using StartupTool.Core.Ipc;
using StartupTool.Core.Models;
using StartupTool.Core.Native;
using StartupTool.Core.Supervision;

namespace StartupTool.Core.Tests;

/// <summary>
/// M4 命名管道端到端：真实 IpcServer + IpcClient + BeatClient 同机回环，
/// 覆盖 ping/getStates/启停/事件推送/心跳投递（含非法令牌与时钟偏差）。
/// </summary>
public class IpcEndToEndTests : IDisposable
{
    private readonly string _dir;
    private readonly string _pipeName;
    private readonly ConfigService _config;
    private readonly ScriptSupervisor _supervisor;
    private readonly IpcServer _server;
    private readonly IpcClient _client;
    private readonly ConcurrentQueue<IpcMessage> _events = new();

    private sealed class RelaySink : ISupervisorEventSink
    {
        public ISupervisorEventSink? Target { get; set; }
        public void Emit(SupervisorEvent e) => Target?.Emit(e);
    }

    public IpcEndToEndTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "st ipc-" + Guid.NewGuid().ToString("N"));
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
        _client.EventReceived += m => _events.Enqueue(m);
        _client.ConnectAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        _client.SubscribeAsync(Process.GetCurrentProcess().SessionId).GetAwaiter().GetResult();
    }

    private ScriptConfig AddLongScript(string fileName = "long.bat")
    {
        var path = Path.Combine(_dir, fileName);
        File.WriteAllText(path, "@echo off\r\nping -n 60 127.0.0.1\r\n");
        var script = new ScriptConfig
        {
            Name = Path.GetFileNameWithoutExtension(fileName),
            Path = path,
            CrashRestart = CrashPolicy.Never
        };
        _config.Mutate(c => c.Scripts.Add(script));
        return script;
    }

    private static async Task<bool> WaitForAsync(
        Func<Task<bool>> condition, int timeoutMs = 15000, int stepMs = 200)
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
    public async Task Ping_与_GetStates_正常往返()
    {
        await _client.PingAsync();

        var states = await _client.GetStatesAsync();
        Assert.Empty(states);

        var script = AddLongScript();
        states = await _client.GetStatesAsync();
        Assert.Contains(states, s => s.Id == script.Id);
    }

    [Fact]
    public async Task 经管道启动停止脚本_状态事件被推送()
    {
        var script = AddLongScript();

        await _client.StartScriptAsync(script.Id);
        Assert.True(await WaitForAsync(async () =>
            (await _client.GetStatesAsync()).First(s => s.Id == script.Id).State == ScriptState.Running));

        // 服务端状态变化应推送给订阅者
        Assert.True(WaitFor(() => _events.Any(m => m.Type == IpcTypes.ScriptStates)),
            "未收到 scriptStates 推送");

        await _client.StopScriptAsync(script.Id);
        Assert.True(await WaitForAsync(async () =>
            (await _client.GetStatesAsync()).First(s => s.Id == script.Id).State == ScriptState.Stopped));
    }

    [Fact]
    public async Task 全部停止_经服务端生效()
    {
        var s1 = AddLongScript("a.bat");
        var s2 = AddLongScript("b.bat");

        await _client.StartScriptAsync(s1.Id);
        await _client.StartScriptAsync(s2.Id);

        Assert.True(await WaitForAsync(async () =>
        {
            var states = await _client.GetStatesAsync();
            return states.First(x => x.Id == s1.Id).State == ScriptState.Running
                   && states.First(x => x.Id == s2.Id).State == ScriptState.Running;
        }));

        await _client.StopAllAsync();
        Assert.True(await WaitForAsync(async () =>
            (await _client.GetStatesAsync()).All(s => s.State == ScriptState.Stopped)));
    }

    [Fact]
    public async Task 心跳投递_成功_非法令牌_时钟偏差()
    {
        var script = AddLongScript("hb.bat");
        var token = _config.EnsureHeartbeatToken(script.Id);
        await _client.StartScriptAsync(script.Id);
        Assert.True(await WaitForAsync(async () =>
            (await _client.GetStatesAsync()).First(s => s.Id == script.Id).State == ScriptState.Running));

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        Assert.Equal(BeatTransportResult.Delivered, BeatClient.Send(token, now, _pipeName));
        Assert.Equal(BeatTransportResult.InvalidToken,
            BeatClient.Send("a".PadRight(64, 'a'), now, _pipeName));
        Assert.Equal(BeatTransportResult.ClockSkew,
            BeatClient.Send(token, now - 1000, _pipeName));
    }

    [Fact]
    public async Task 心跳_脚本未运行_返回NotRunning_M9()
    {
        var script = AddLongScript("hb-stopped.bat");
        var token = _config.EnsureHeartbeatToken(script.Id);
        // 刻意不启动：令牌有效但脚本处于 Stopped，心跳不得回 Ok
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.Equal(BeatTransportResult.NotRunning, BeatClient.Send(token, now, _pipeName));

        // 启动后应恢复 Delivered，停止后再次回到 NotRunning
        await _client.StartScriptAsync(script.Id);
        Assert.True(await WaitForAsync(async () =>
            (await _client.GetStatesAsync()).First(s => s.Id == script.Id).State == ScriptState.Running));
        Assert.Equal(BeatTransportResult.Delivered, BeatClient.Send(token, now, _pipeName));

        await _client.StopScriptAsync(script.Id);
        Assert.True(WaitFor(() =>
        {
            var st = _supervisor.GetStates().First(s => s.Id == script.Id).State;
            return st == ScriptState.Stopped;
        }));
        Assert.Equal(BeatTransportResult.NotRunning, BeatClient.Send(token, now, _pipeName));
    }

    [Fact]
    public async Task 启动不存在的脚本_收到故障通知与拒绝Ack()
    {
        var path = Path.Combine(_dir, "missing.bat");
        var script = new ScriptConfig { Name = "missing", Path = path };
        _config.Mutate(c => c.Scripts.Add(script));

        var ex = await Assert.ThrowsAsync<IpcAckException>(
            () => _client.StartScriptAsync(script.Id));
        Assert.Equal("rejected", ex.Code);

        Assert.True(WaitFor(() => _events.Any(m =>
            m.Type == IpcTypes.Notify
            && m.ReadPayload<NotifyPayload>()?.Kind == "fault")), "未收到故障通知");
    }

    [Fact]
    public void 连接不存在的管道_返回NoServer()
    {
        Assert.Equal(BeatTransportResult.NoServer,
            BeatClient.Send("a".PadRight(64, 'a'),
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                "st-test-no-such-pipe-" + Guid.NewGuid().ToString("N")));
    }

    public void Dispose()
    {
        try { _supervisor.StopAllAsync().Wait(8000); } catch { }
        _client.Dispose();
        _server.Dispose();
        _supervisor.Dispose();
        _config.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
