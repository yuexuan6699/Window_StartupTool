using Xunit;
using StartupTool.Core.Models;

namespace StartupTool.Core.Tests;

/// <summary>N2：全局设置服务端白名单范围校验。</summary>
public class SettingsValidationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("zh-CN")]
    [InlineData("en")]
    [InlineData("system")]
    public void 合法语言与边界值_通过(string? language)
    {
        var s = new GlobalSettings
        {
            Language = language,
            LogMaxSizeMb = 1,
            LogRetentionDays = 3650,
            CircuitWindowSeconds = 86400,
            CircuitMaxStarts = 1000
        };
        Assert.Null(s.Validate());

        // 另一侧边界
        var s2 = new GlobalSettings
        {
            Language = language,
            LogMaxSizeMb = 1024,
            LogRetentionDays = 1,
            CircuitWindowSeconds = 1,
            CircuitMaxStarts = 1
        };
        Assert.Null(s2.Validate());
    }

    [Fact]
    public void 默认设置_通过() => Assert.Null(new GlobalSettings().Validate());

    [Theory]
    [InlineData(0, 5, 30, 10)]    // 日志上限 0
    [InlineData(1025, 5, 30, 10)] // 日志上限超界
    [InlineData(5, 0, 30, 10)]    // 保留天数 0
    [InlineData(5, 3651, 30, 10)] // 保留天数超界
    [InlineData(5, 30, 0, 10)]    // 熔断窗口 0
    [InlineData(5, 30, 86401, 10)]// 熔断窗口超界
    [InlineData(5, 30, 30, 0)]    // 熔断阈值 0
    [InlineData(5, 30, 30, 1001)] // 熔断阈值超界
    public void 非法数值_返回错误说明(int logMb, int days, int window, int maxStarts)
    {
        var s = new GlobalSettings
        {
            LogMaxSizeMb = logMb,
            LogRetentionDays = days,
            CircuitWindowSeconds = window,
            CircuitMaxStarts = maxStarts
        };
        Assert.NotNull(s.Validate());
    }

    [Fact]
    public void 非法语言码_返回错误说明()
    {
        Assert.NotNull(new GlobalSettings { Language = "fr-FR" }.Validate());
        Assert.NotNull(new GlobalSettings { Language = "xx" }.Validate());
    }
}
