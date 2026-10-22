using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Xunit;
using StartupTool.Core.Configuration;
using StartupTool.Core.Models;

namespace StartupTool.Core.Tests;

public class ConfigServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;
    private readonly string _backupPath;

    public ConfigServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "st-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "scripts.json");
        _backupPath = _configPath + ".bak";
    }

    private ConfigService CreateService(bool enableWatcher = true)
        => new(_configPath, _backupPath, enableWatcher);

    /// <summary>B19/D1：旧 v1 配置自动迁移——Enabled 拆分、死字段丢弃、首次加载落盘 v2。</summary>
    [Fact]
    public void V1_配置自动迁移_拆分Enabled并丢弃死字段()
    {
        File.WriteAllText(_configPath, """
        {
          "scripts": [
            { "name": "启用脚本", "path": "C:\\a.bat", "order": 0, "enabled": true,
              "arguments": "", "maxCpuPercent": 80, "maxMemoryMb": 100,
              "maxGpuPercent": 70, "restartDelaySeconds": 5 },
            { "name": "禁用脚本", "path": "C:\\b.ps1", "order": 1, "enabled": false }
          ],
          "runMode": "web",
          "webPort": 13000
        }
        """);

        using var svc = CreateService(enableWatcher: false);

        var cfg = svc.Current;
        Assert.Equal(2, cfg.Version);
        Assert.Equal(2, cfg.Scripts.Count);

        var on = cfg.Scripts.Single(s => s.Name == "启用脚本");
        Assert.True(on.AutoStart);
        Assert.True(on.ManualEnabled);
        Assert.Equal(80, on.MaxCpuPercent);
        Assert.Equal(100, on.MaxMemoryMb);
        Assert.Equal(70, on.MaxGpuPercent);
        Assert.Equal(5, on.RestartDelaySeconds);
        Assert.False(string.IsNullOrEmpty(on.Id));

        var off = cfg.Scripts.Single(s => s.Name == "禁用脚本");
        Assert.False(off.AutoStart);   // enabled=false → 不开机自启
        Assert.True(off.ManualEnabled); // 手动启动仍允许

        // 磁盘已重写为 v2 且无死字段
        var disk = File.ReadAllText(_configPath);
        using var doc = JsonDocument.Parse(disk);
        Assert.Equal(2, doc.RootElement.GetProperty("version").GetInt32());
        Assert.False(disk.Contains("runMode", StringComparison.OrdinalIgnoreCase));
        Assert.False(disk.Contains("webPort", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>B1：热重载遇到损坏文件，保留旧内存值，只报 LoadFailed。</summary>
    [Fact]
    public void 热重载_配置损坏_保留旧内存值()
    {
        using var svc = CreateService(enableWatcher: false);
        svc.Mutate(c => c.Scripts.Add(new ScriptConfig { Name = "存活脚本", Path = "C:\\x.bat" }));

        Exception? failure = null;
        svc.LoadFailed += ex => failure = ex;

        File.WriteAllText(_configPath, "{ 这不是合法JSON ");
        svc.ReloadFromDisk();

        Assert.NotNull(failure);
        Assert.Single(svc.Current.Scripts);
        Assert.Equal("存活脚本", svc.Current.Scripts[0].Name);
    }

    /// <summary>A10：再次保存后 .bak 保存的是上一版内容。</summary>
    [Fact]
    public void 原子保存_bak为上一版()
    {
        using var svc = CreateService(enableWatcher: false);
        svc.Mutate(c => c.Scripts.Add(new ScriptConfig { Name = "第一版", Path = "C:\\1.bat" }));
        svc.Mutate(c => c.Scripts.Add(new ScriptConfig { Name = "第二版", Path = "C:\\2.bat" }));

        Assert.True(File.Exists(_backupPath));
        var bak = JsonDocument.Parse(File.ReadAllText(_backupPath));
        var bakNames = bak.RootElement.GetProperty("scripts").EnumerateArray()
            .Select(e => e.GetProperty("name").GetString()).ToList();
        Assert.Equal(new[] { "第一版" }, bakNames); // 备份保存上一版

        var now = JsonDocument.Parse(File.ReadAllText(_configPath));
        var nowNames = now.RootElement.GetProperty("scripts").EnumerateArray()
            .Select(e => e.GetProperty("name").GetString()).ToList();
        Assert.Equal(new[] { "第一版", "第二版" }, nowNames); // 当前文件为最新版
    }

    /// <summary>H2：令牌生成/反查/重置。</summary>
    [Fact]
    public void 心跳令牌_生成反查与重置()
    {
        using var svc = CreateService(enableWatcher: false);
        string id = "";
        svc.Mutate(c =>
        {
            var s = new ScriptConfig { Name = "心跳脚本", Path = "C:\\h.bat", HeartbeatEnabled = true };
            id = s.Id;
            c.Scripts.Add(s);
        });

        var token = svc.EnsureHeartbeatToken(id);
        Assert.Equal(64, token.Length);
        Assert.True(SecurityTokenGenerator.IsValidTokenFormat(token));
        Assert.Equal(id, svc.FindByHeartbeatToken(token)?.Id);

        var newToken = svc.ResetHeartbeatToken(id);
        Assert.NotEqual(token, newToken);
        Assert.Null(svc.FindByHeartbeatToken(token));        // 旧令牌立即失效
        Assert.Equal(id, svc.FindByHeartbeatToken(newToken)?.Id);

        // M4：同长度但内容不同的令牌不得命中（固定时间比较不改变判定语义）
        var sameLengthWrong = new string('0', 64);
        Assert.Null(svc.FindByHeartbeatToken(sameLengthWrong));
        Assert.Null(svc.FindByHeartbeatToken(newToken[..^1]));
    }

    /// <summary>M2：配置目录收紧为仅 SYSTEM/Administrators，且规则可被目录内新文件继承。</summary>
    [Fact]
    public void 配置目录ACL_收紧且新文件继承保护()
    {
        using var svc = CreateService(enableWatcher: false);
        Assert.True(svc.AclProtectionActive);

        var dirSec = new DirectoryInfo(_dir).GetAccessControl();
        Assert.True(dirSec.AreAccessRulesProtected); // 已断开父目录继承

        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value;
        var adminsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value;
        var dirAllows = dirSec
            .GetAccessRules(true, false, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(r => r.AccessControlType == AccessControlType.Allow)
            .Select(r => r.IdentityReference.Value)
            .ToList();
        Assert.Equal(2, dirAllows.Count);
        Assert.Contains(systemSid, dirAllows);
        Assert.Contains(adminsSid, dirAllows);

        // 目录内后建文件必须继承到这两条规则（.tmp/.bak 不再有权限空窗）
        var probe = Path.Combine(_dir, "inherit-probe.txt");
        File.WriteAllText(probe, "x");
        try
        {
            var fileSec = new FileInfo(probe).GetAccessControl();
            var inheritedAllows = fileSec
                .GetAccessRules(true, true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .Where(r => r.AccessControlType == AccessControlType.Allow && r.IsInherited)
                .Select(r => r.IdentityReference.Value)
                .ToList();
            Assert.Contains(systemSid, inheritedAllows);
            Assert.Contains(adminsSid, inheritedAllows);
        }
        finally { File.Delete(probe); }
    }

    /// <summary>A10：自己的保存不应通过 FileSystemWatcher 回环再触发一次 Changed。</summary>
    [Fact]
    public void 自我保存_不触发回环热重载()
    {
        using var svc = CreateService(enableWatcher: true);
        var changed = 0;
        svc.Changed += _ => Interlocked.Increment(ref changed);

        svc.Mutate(c => c.Scripts.Add(new ScriptConfig { Name = "回环测试", Path = "C:\\l.bat" }));
        Thread.Sleep(600); // 超过 watcher 防抖窗

        Assert.Equal(1, changed); // Mutate 本身 1 次，watcher 回环 0 次
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 忽略 */ }
    }
}
