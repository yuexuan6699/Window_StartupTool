using StartupTool.Core.Logging;
using StartupTool.Core.Models;
using Xunit;

namespace StartupTool.Core.Tests;

/// <summary>
/// B10 日志落盘：目录约定、单文件上限分片、保留天数清理、文件名净化。
/// </summary>
public class LogStoreTests
{
    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "st log-" + Guid.NewGuid().ToString("N"));

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }

    [Fact]
    public void 写服务日志_文件存在且含时间戳与消息()
    {
        var root = NewRoot();
        try
        {
            var store = new LogStore(() => new GlobalSettings(), root);
            store.WriteService("INFO", "demo", "hello world");

            var files = Directory.GetFiles(Path.Combine(root, "service"), "*.log");
            var text = File.ReadAllText(Assert.Single(files));
            Assert.Contains("[INFO]", text, StringComparison.Ordinal);
            Assert.Contains("[demo]", text, StringComparison.Ordinal);
            Assert.Contains("hello world", text, StringComparison.Ordinal);
            Assert.Contains(DateTime.Now.ToString("yyyy-MM-dd"), text, StringComparison.Ordinal);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void 脚本输出_落在名_短哈希目录下()
    {
        var root = NewRoot();
        try
        {
            var store = new LogStore(() => new GlobalSettings(), root);
            store.WriteScriptOutput("abcdef1234567890", "演示 脚本", "out-line");

            var dir = Path.Combine(root, "scripts", "演示_脚本_abcdef12");
            Assert.True(Directory.Exists(dir));
            var file = Assert.Single(Directory.GetFiles(dir, "*.log"));
            Assert.Equal($"output-{DateTime.Now:yyyyMMdd}.log", Path.GetFileName(file));
            Assert.Contains("out-line", File.ReadAllText(file), StringComparison.Ordinal);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void 超过单文件上限_自动分片()
    {
        var root = NewRoot();
        try
        {
            var settings = new GlobalSettings { LogMaxSizeMb = 1, LogRetentionDays = 30 };
            var store = new LogStore(() => settings, root);

            store.WriteService("INFO", null, new string('x', 1_100_000)); // 单条即超过 1MB
            store.WriteService("INFO", null, "second-shard");

            var files = Directory.GetFiles(Path.Combine(root, "service"), "*.log");
            Assert.Equal(2, files.Length);
            Assert.Contains(files, f => Path.GetFileName(f).EndsWith("-2.log", StringComparison.Ordinal));
            var shard2 = files.Single(f => Path.GetFileName(f).EndsWith("-2.log", StringComparison.Ordinal));
            Assert.Contains("second-shard", File.ReadAllText(shard2), StringComparison.Ordinal);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void 超过保留天数_旧日志被清理()
    {
        var root = NewRoot();
        try
        {
            var serviceDir = Path.Combine(root, "service");
            Directory.CreateDirectory(serviceDir);
            var old = Path.Combine(serviceDir, "service-20200101.log");
            File.WriteAllText(old, "old");
            File.SetLastWriteTime(old, DateTime.Now.AddDays(-40));

            var fresh = Path.Combine(serviceDir, "service-29990101.log");
            File.WriteAllText(fresh, "fresh");

            new LogStore(() => new GlobalSettings { LogRetentionDays = 30, LogMaxSizeMb = 5 }, root).CleanupExpired();

            Assert.False(File.Exists(old));
            Assert.True(File.Exists(fresh));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void 脚本目录名_净化非法字符并按短哈希()
    {
        Assert.Equal("my_script_12345678", LogStore.ScriptFolderName("my script", "1234567890abcdef"));
        Assert.Equal("script_50000000", LogStore.ScriptFolderName("  ", "5"));
        // 中文按字母保留，路径分隔符等非法字符丢弃（避免生成子目录）
        Assert.Equal("记事本_abcdef12", LogStore.ScriptFolderName("记事本", "abcdef1234567890"));
        Assert.Equal("abcd_abcdef12", LogStore.ScriptFolderName("ab/cd", "abcdef12"));
    }
}
