using System.IO.Compression;
using System.Text.Json;
using Xunit;
using StartupTool.Core.Configuration;
using StartupTool.Core.Diagnostics;
using StartupTool.Core.Ipc;
using StartupTool.Core.Models;
using StartupTool.Core.Native;
using StartupTool.Core.Supervision;

namespace StartupTool.Core.Tests;

/// <summary>
/// D7 配置导入/导出：导出→导入回环保留关键字段、非法/成环配置被拒且不污染现有配置；
/// D8 诊断包：令牌在服务端被真实打码、zip 可解压且含规定条目。
/// </summary>
public class ConfigTransferTests : IDisposable
{
    private readonly string _dir;
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

    public ConfigTransferTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "st xfer-" + Guid.NewGuid().ToString("N"));
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
        _client.SubscribeAsync(System.Diagnostics.Process.GetCurrentProcess().SessionId)
            .GetAwaiter().GetResult();
    }

    private async Task<ExportConfigResult> ExportAsync(bool includeTokens)
    {
        var reply = await _client.InvokeRawAsync(
            IpcTypes.ExportConfig, new ExportConfigPayload(includeTokens));
        Assert.Equal(IpcTypes.ExportConfigData, reply.Type);
        return reply.ReadPayload<ExportConfigResult>()!;
    }

    private Task<IpcMessage> ImportAsync(string json, bool replace)
        => _client.InvokeRawAsync(IpcTypes.ImportConfig, new ImportConfigPayload(json, replace));

    [Fact]
    public async Task 导出导入回环_不含令牌导出_关键字段一致()
    {
        var a = new ScriptConfig
        {
            Name = "A",
            Path = @"C:\fake\a.bat",
            Arguments = "--flag 1",
            RequireElevation = true,
            InitialDelaySeconds = 7,
            WaitForNetwork = true
        };
        var b = new ScriptConfig { Name = "B", Path = @"C:\fake\b.bat", Arguments = "--flag 2" };
        await _client.UpsertScriptAsync(a);
        b.DependsOn = new List<string> { a.Id };
        await _client.UpsertScriptAsync(b);

        var token = await _client.GetHeartbeatTokenAsync(a.Id);
        Assert.Equal(64, token.Length);

        var exported = await ExportAsync(includeTokens: false);

        // 不含令牌导出：真实令牌不得出现在文件里，heartbeatToken 为空
        Assert.DoesNotContain(token, exported.Json, StringComparison.Ordinal);
        using (var doc = JsonDocument.Parse(exported.Json))
        {
            foreach (var s in doc.RootElement.GetProperty("scripts").EnumerateArray())
            {
                if (s.TryGetProperty("heartbeatToken", out var t))
                    Assert.True(string.IsNullOrEmpty(t.GetString()));
            }
        }

        // 含令牌导出：令牌原样出现（验证 IncludeTokens=true 分支确实保留令牌）
        var withTokens = await ExportAsync(includeTokens: true);
        Assert.Contains(token, withTokens.Json, StringComparison.Ordinal);

        // 导入前故意多出一个脚本；导入回环后应恢复为导出时的 2 个
        await _client.UpsertScriptAsync(new ScriptConfig { Name = "C", Path = @"C:\fake\c.bat" });
        Assert.Equal(3, _config.Current.Scripts.Count);

        var importReply = await ImportAsync(exported.Json, replace: false);
        Assert.Equal(IpcTypes.ImportConfigData, importReply.Type);
        var imported = importReply.ReadPayload<ImportConfigResult>()!;
        Assert.Equal(2, imported.ScriptCount);

        var savedA = _config.Current.Scripts.Single(s => s.Id == a.Id);
        Assert.Equal("A", savedA.Name);
        Assert.Equal(@"C:\fake\a.bat", savedA.Path);
        Assert.Equal("--flag 1", savedA.Arguments);
        Assert.True(savedA.RequireElevation);
        Assert.Equal(7, savedA.InitialDelaySeconds);
        Assert.True(savedA.WaitForNetwork);

        var savedB = _config.Current.Scripts.Single(s => s.Id == b.Id);
        Assert.Equal("B", savedB.Name);
        Assert.Equal(@"C:\fake\b.bat", savedB.Path);
        Assert.Equal("--flag 2", savedB.Arguments);
        Assert.Equal(new[] { a.Id }, savedB.DependsOn);

        // 导入后令牌一律被清空（换机器需重新生成）
        Assert.Equal(string.Empty, savedA.HeartbeatToken);
    }

    [Fact]
    public async Task 导入成环配置_被拒且现有配置不变()
    {
        await _client.UpsertScriptAsync(new ScriptConfig { Name = "keep", Path = @"C:\fake\keep.bat" });
        var before = _config.Current.Scripts.Count;

        // A→B→A 依赖成环
        var json = """
            {
              "version": 2,
              "scripts": [
                { "id": "aaaa", "name": "A", "path": "C:\\fake\\a.bat", "dependsOn": ["bbbb"] },
                { "id": "bbbb", "name": "B", "path": "C:\\fake\\b.bat", "dependsOn": ["aaaa"] }
              ]
            }
            """;

        var reply = await ImportAsync(json, replace: false);
        var ack = reply.ReadPayload<AckPayload>()!;
        Assert.False(ack.Ok);
        Assert.Equal("invalidPayload", ack.Code);

        // 现有配置未被改动
        Assert.Equal(before, _config.Current.Scripts.Count);
        Assert.Contains(_config.Current.Scripts, s => s.Name == "keep");
    }

    [Fact]
    public async Task 导出诊断包_真实令牌被打码_zip可解压()
    {
        var script = new ScriptConfig { Name = "tok", Path = @"C:\fake\t.bat" };
        await _client.UpsertScriptAsync(script);
        var token = await _client.GetHeartbeatTokenAsync(script.Id);
        Assert.Equal(64, token.Length);

        var reply = await _client.InvokeRawAsync(IpcTypes.ExportDiagnostics, payload: null);
        Assert.Equal(IpcTypes.ExportDiagnosticsData, reply.Type);
        var result = reply.ReadPayload<ExportDiagnosticsResult>()!;
        Assert.True(File.Exists(result.ZipPath));

        using var archive = ZipFile.OpenRead(result.ZipPath);
        var entry = archive.GetEntry("config/scripts.json");
        Assert.NotNull(entry);
        using var reader = new StreamReader(entry.Open());
        var content = reader.ReadToEnd();
        Assert.DoesNotContain(token, content, StringComparison.Ordinal); // 绝不原样进 zip
        Assert.Contains("***", content, StringComparison.Ordinal);

        try { File.Delete(result.ZipPath); } catch { }
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

/// <summary>
/// D8 打包器单元测试（不需要服务端）：自定义条目名、附带最近日志、内容原样写入。
/// </summary>
public class DiagnosticsPackagerTests
{
    [Fact]
    public void 打包_含规定条目_配置内容中无真实令牌()
    {
        var dir = Path.Combine(Path.GetTempPath(), "st diag-" + Guid.NewGuid().ToString("N"));
        var logDir = Path.Combine(dir, "service");
        Directory.CreateDirectory(logDir);
        File.WriteAllText(Path.Combine(logDir, "service-20260101.log"), "line1");

        // 模拟服务端输出：心跳令牌已在 configJson 里打码为 ***
        var realToken = new string('a', 64);
        const string configJson =
            "{\"version\":2,\"scripts\":[{\"id\":\"x1\",\"name\":\"X\",\"path\":\"C:\\\\x.bat\",\"heartbeatToken\":\"***\"}]}";

        try
        {
            var zip = DiagnosticsPackager.Create(
                dir, new DiagnosticsInput(configJson, "{\"scripts\":[]}", logDir, "env-info"), DateTime.UtcNow);

            Assert.True(File.Exists(zip));

            using var archive = ZipFile.OpenRead(zip);
            var names = archive.Entries.Select(e => e.FullName).ToList();
            Assert.Contains("config/scripts.json", names);
            Assert.Contains("runtime/states.json", names);
            Assert.Contains("environment.txt", names);
            Assert.Contains(names, n => n.StartsWith("logs/service/", StringComparison.Ordinal));

            using var reader = new StreamReader(archive.GetEntry("config/scripts.json")!.Open());
            var content = reader.ReadToEnd();
            Assert.Contains("***", content, StringComparison.Ordinal);
            Assert.DoesNotContain(realToken, content, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
