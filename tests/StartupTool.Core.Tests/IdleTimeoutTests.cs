using System.IO.Pipes;
using Xunit;
using StartupTool.Core.Configuration;
using StartupTool.Core.Ipc;
using StartupTool.Core.Supervision;

namespace StartupTool.Core.Tests;

/// <summary>
/// S6：连接级空闲读超时——只连不发的占位连接被回收；定时 Ping 保活的控制端不被误断。
/// </summary>
public class IdleTimeoutTests : IDisposable
{
    private readonly string _dir;
    private readonly string _pipeName;
    private readonly ConfigService _config;

    public IdleTimeoutTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "st-idle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var configPath = Path.Combine(_dir, "scripts.json");
        _config = new ConfigService(configPath, configPath + ".bak", enableWatcher: false);
        _pipeName = "st-idle-" + Guid.NewGuid().ToString("N");
    }

    private (IpcServer Server, ScriptSupervisor Supervisor) StartServer(TimeSpan idleTimeout)
    {
        var sv = new ScriptSupervisor(_config);
        var server = new IpcServer(sv, _config, _pipeName,
            sessionContext: new Native.LocalSessionContext(), idleTimeout: idleTimeout,
            interactiveServer: true);
        server.Start();
        return (server, sv);
    }

    [Fact]
    public void 只连不发_超过空闲超时后连接被回收()
    {
        var pair = StartServer(TimeSpan.FromSeconds(2));
        using var supervisor = pair.Supervisor;
        using var server = pair.Server;

        using var raw = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        raw.Connect(5000);

        // 服务端在 2 秒无帧后应主动断开：客户端读返回 0（EOF）
        var buf = new byte[4];
        var deadline = Environment.TickCount64 + 10000;
        int read;
        do { read = raw.Read(buf, 0, 4); }
        while (read != 0 && Environment.TickCount64 < deadline);

        Assert.Equal(0, read);
    }

    [Fact]
    public async Task 定时Ping保活_跨越多个超时周期连接保持()
    {
        var pair = StartServer(TimeSpan.FromSeconds(2));
        using var supervisor = pair.Supervisor;
        using var server = pair.Server;

        // 保活间隔 0.7 秒 << 服务端 2 秒空闲超时
        using var client = new IpcClient(_pipeName, keepaliveInterval: TimeSpan.FromMilliseconds(700));
        await client.ConnectAsync(TimeSpan.FromSeconds(5));
        await client.SubscribeAsync(null);

        // 等待 6 秒：至少经历 2~3 个完整空闲超时窗，保活 Ping 不断刷新计时
        await Task.Delay(6000);

        Assert.True(client.IsConnected);
        await client.PingAsync(); // 连接仍可正常请求/响应
    }

    public void Dispose()
    {
        _config.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
