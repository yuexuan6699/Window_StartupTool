using Xunit;
using StartupTool.Core.Configuration;

namespace StartupTool.Core.Tests;

/// <summary>M6：手改配置产生的空 Id/重复 Id 在加载时被纠正，不拖垮资源巡检。</summary>
public class DuplicateIdNormalizeTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;
    private readonly string _backupPath;

    public DuplicateIdNormalizeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "st-m6-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "scripts.json");
        _backupPath = _configPath + ".bak";
    }

    [Fact]
    public void 重复Id与空Id_加载后全部唯一_首个Id保留()
    {
        File.WriteAllText(_configPath, """
        {
          "version": 2,
          "settings": {},
          "scripts": [
            { "id": "dup", "name": "first", "path": "C:\\a.bat", "order": 0 },
            { "id": "dup", "name": "second", "path": "C:\\b.bat", "order": 1, "dependsOn": ["dup"] },
            { "id": "", "name": "empty", "path": "C:\\c.bat", "order": 2 }
          ]
        }
        """);

        using var svc = new ConfigService(_configPath, _backupPath, enableWatcher: false);
        var scripts = svc.Current.Scripts;

        Assert.Equal(3, scripts.Count);
        var ids = scripts.Select(s => s.Id).ToList();
        Assert.Equal(3, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ids, id => Assert.False(string.IsNullOrWhiteSpace(id)));

        // 首次出现的 Id 原样保留（其他脚本对它的 DependsOn 引用因此仍然有效）
        Assert.Equal("dup", scripts[0].Id);
        Assert.Equal("dup", scripts[1].DependsOn.Single());
        Assert.NotEqual("dup", scripts[1].Id);
    }

    [Fact]
    public void 去重后_按Id建字典不再抛异常()
    {
        File.WriteAllText(_configPath, """
        {
          "version": 2,
          "settings": {},
          "scripts": [
            { "id": "x", "name": "a", "path": "C:\\a.bat", "order": 0 },
            { "id": "x", "name": "b", "path": "C:\\b.bat", "order": 1 }
          ]
        }
        """);

        using var svc = new ConfigService(_configPath, _backupPath, enableWatcher: false);
        // M6 旧根因：重复键使 ToDictionary 抛 ArgumentException，整轮资源巡检静默失效
        var dict = svc.Current.Scripts.ToDictionary(s => s.Id);
        Assert.Equal(2, dict.Count);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
