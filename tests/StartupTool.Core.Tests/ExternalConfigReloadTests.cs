using System.Diagnostics;
using Xunit;
using StartupTool.Core.Configuration;
using StartupTool.Core.Ipc;
using StartupTool.Core.Models;
using StartupTool.Core.Native;
using StartupTool.Core.Supervision;

namespace StartupTool.Core.Tests;

/// <summary>
/// F8：外部（人工编辑 / 其它进程）直接改写 scripts.json 后，服务端应把
/// 状态快照（行集合）与整份配置（含设置）主动广播给已订阅的控制端——
/// 此前控制端只能靠重连或重启才发现外部变更。
/// </summary>
public class ExternalConfigReloadTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;
    private readonly string _pipeName;
    private readonly ConfigService _config;
    private readonly ScriptSupervisor _supervisor;
    private readonly IpcServer _server;
    private readonly IpcClient _client;

    private sealed class RelaySink : ISupervisorEventSink
    {
        public ISupervisorEventSink? Target { get; set; }
        public void Emit(SupervisorEvent e) => Target?.Emit(e);
    }

    public ExternalConfigReloadTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "st ext-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "scripts.json");

        // 必须开启热重载监听：被测路径就是"外部改盘 → FileSystemWatcher → 广播"
        _config = new ConfigService(_configPath, _configPath + ".bak", enableWatcher: true);

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

    [Fact]
    public async Task 外部改写配置_控制端收到整份配置与状态快照()
    {
        var configReceived = new TaskCompletionSource<AppConfig>(TaskCreationOptions.RunContinuationsAsynchronously);
        var statesReceived = new TaskCompletionSource<IReadOnlyList<ScriptRuntimeSnapshot>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _client.EventReceived += m =>
        {
            if (m.Type == IpcTypes.ConfigData && m.ReadPayload<AppConfigPayload>() is { } p)
                configReceived.TrySetResult(p.Config);
            else if (m.Type == IpcTypes.ScriptStates && m.ReadPayload<ScriptStatesPayload>() is { } s)
                statesReceived.TrySetResult(s.Scripts);
        };

        // 外部直接写盘（不经过任何本进程 API）：3 个脚本，其中 C 为新行
        const string json = """
            {
              "version": 2,
              "settings": { "language": "zh-CN" },
              "scripts": [
                { "id": "aaaa1111", "name": "A", "path": "C:\\Windows\\System32\\notepad.exe", "order": 0 },
                { "id": "bbbb2222", "name": "B", "path": "C:\\Windows\\System32\\notepad.exe", "order": 1 },
                { "id": "cccc3333", "name": "C", "path": "C:\\Windows\\System32\\notepad.exe", "order": 2 }
              ]
            }
            """;
        await File.WriteAllTextAsync(_configPath, json);

        var cfg = await configReceived.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(3, cfg.Scripts.Count);

        var states = await statesReceived.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(3, states.Count);
        Assert.Contains(states, s => s.Name == "C");
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
