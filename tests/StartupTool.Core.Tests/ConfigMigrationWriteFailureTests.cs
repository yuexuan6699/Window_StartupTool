using Xunit;
using StartupTool.Core.Configuration;

namespace StartupTool.Core.Tests;

/// <summary>M5：迁移落盘失败不得让 ConfigService 构造抛异常（内存配置仍可用）。</summary>
public class ConfigMigrationWriteFailureTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;
    private readonly string _backupPath;

    public ConfigMigrationWriteFailureTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "st-m5-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "scripts.json");
        _backupPath = _configPath + ".bak";
    }

    [Fact]
    public void v1迁移落盘失败_构造不抛_内存配置可用_错误已记录()
    {
        File.WriteAllText(_configPath, """
        {
          "scripts": [
            { "name": "s1", "path": "C:\\a.bat", "order": 0, "enabled": true },
            { "name": "s2", "path": "C:\\b.bat", "order": 1, "enabled": false }
          ]
        }
        """);

        // 目标文件只读：临时文件可写，但 File.Replace 到只读目标会失败（UnauthorizedAccess）
        File.SetAttributes(_configPath, File.GetAttributes(_configPath) | FileAttributes.ReadOnly);

        ConfigService svc;
        try
        {
            svc = new ConfigService(_configPath, _backupPath, enableWatcher: false);
        }
        finally
        {
            // 复位只读以便 Dispose/清理
            File.SetAttributes(_configPath, FileAttributes.Normal);
        }

        using (svc)
        {
            // 服务/控制端不能因写盘失败而启动崩溃：内存中迁移结果完整可用
            Assert.Equal(2, svc.Current.Scripts.Count);
            Assert.Equal(2, svc.Current.Version);
            Assert.NotNull(svc.InitialSaveError);
        }
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_configPath))
                File.SetAttributes(_configPath, FileAttributes.Normal);
        }
        catch { }
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
