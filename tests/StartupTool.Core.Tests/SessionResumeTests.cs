using Microsoft.Win32.SafeHandles;
using Xunit;
using StartupTool.Core.Configuration;
using StartupTool.Core.Models;
using StartupTool.Core.Native;
using StartupTool.Core.Supervision;

namespace StartupTool.Core.Tests;

/// <summary>
/// A3 会话感知：无活动会话时排队（不占熔断），登录补发后启动/消费排队。
/// 测试用假 ISessionContext 模拟"无人登录 → 登录出现"（真实 SYSTEM 令牌路径靠 --console/服务实测）。
/// </summary>
public class SessionResumeTests : IDisposable
{
    private sealed class FakeSessionContext : ISessionContext
    {
        public int ActiveSession { get; set; } = -1;
        public SafeFileHandle? Token { get; set; }

        public int GetActiveSessionId() => ActiveSession;
        public SafeFileHandle? TryOpenUserToken(int sessionId) => Token;
    }

    private readonly string _dir;
    private readonly ConfigService _config;
    private readonly ScriptSupervisor _supervisor;
    private readonly FakeSessionContext _sessions = new();

    public SessionResumeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "st sess-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var configPath = Path.Combine(_dir, "scripts.json");
        _config = new ConfigService(configPath, configPath + ".bak", enableWatcher: false);
        _supervisor = new ScriptSupervisor(_config, sessionContext: _sessions);
    }

    private ScriptConfig AddAutoScript(string fileName, bool existingFile = true)
    {
        var path = Path.Combine(_dir, fileName);
        if (existingFile) File.WriteAllText(path, "@echo off\r\nping -n 60 127.0.0.1\r\n");
        var script = new ScriptConfig
        {
            Name = Path.GetFileNameWithoutExtension(fileName),
            Path = path,
            AutoStart = true,
            CrashRestart = CrashPolicy.Never
        };
        _config.Mutate(c => c.Scripts.Add(script));
        return script;
    }

    private ScriptRuntimeSnapshot? Snapshot(string id) =>
        _supervisor.GetStates().FirstOrDefault(s => s.Id == id);

    private static bool WaitFor(Func<bool> condition, int timeoutMs = 10000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(100);
        }
        return condition();
    }

    [Fact]
    public async Task 无人登录时排队_补发后启动()
    {
        var script = AddAutoScript("a.bat");

        _sessions.ActiveSession = -1;
        await _supervisor.StartAllAutoAsync();

        var queued = Snapshot(script.Id)!;
        Assert.True(queued.WaitingForSession);
        Assert.Equal(ScriptState.Stopped, queued.State);

        // 登录出现：非 SYSTEM 环境令牌取 null → 回退同会话启动（--console 等价路径）
        _sessions.ActiveSession = 999;
        _sessions.Token = null;
        await _supervisor.ResumePendingForSessionAsync();

        Assert.True(WaitFor(() => Snapshot(script.Id)?.State == ScriptState.Running),
            "登录补发后脚本未启动");
        Assert.False(Snapshot(script.Id)!.WaitingForSession);
        Assert.Equal(999, Snapshot(script.Id)!.SessionId);

        await _supervisor.StopAsync(script.Id);
    }

    [Fact]
    public async Task 轮询器检测到登录_自动消费排队()
    {
        var script = AddAutoScript("b.bat");

        _sessions.ActiveSession = -1;
        await _supervisor.StartAllAutoAsync();
        Assert.True(Snapshot(script.Id)!.WaitingForSession);

        // 给一个"非空但非用户令牌"的句柄（临时文件）：轮询器据此认定登录已就绪；
        // 随后启动会因 CreateEnvironmentBlock 失败而熔断——本断言只验证"排队被自动消费"。
        var handlePath = Path.Combine(_dir, "token.bin");
        File.WriteAllText(handlePath, "x");
        using var fakeToken = File.OpenHandle(handlePath);

        using var poller = new SessionResumePoller(
            _supervisor, TimeSpan.FromMilliseconds(150), _sessions);
        poller.Start();

        // 先确认无会话期间不会误补发
        Thread.Sleep(400);
        Assert.True(Snapshot(script.Id)!.WaitingForSession);

        _sessions.ActiveSession = 7;
        _sessions.Token = fakeToken;

        Assert.True(WaitFor(() => Snapshot(script.Id)!.WaitingForSession == false),
            "轮询器未在登录后消费排队");
    }

    [Fact]
    public async Task 排队后手动停止_登录不再补发()
    {
        var script = AddAutoScript("c.bat");

        _sessions.ActiveSession = -1;
        await _supervisor.StartAllAutoAsync();
        Assert.True(Snapshot(script.Id)!.WaitingForSession);

        await _supervisor.StopAsync(script.Id);

        _sessions.ActiveSession = 3;
        await _supervisor.ResumePendingForSessionAsync();

        Assert.Equal(ScriptState.Stopped, Snapshot(script.Id)!.State);
        Assert.False(Snapshot(script.Id)!.WaitingForSession);
        Assert.Empty(Snapshot(script.Id)!.AllPids);
    }

    public void Dispose()
    {
        try { _supervisor.StopAllAsync().Wait(8000); } catch { }
        _supervisor.Dispose();
        _config.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
