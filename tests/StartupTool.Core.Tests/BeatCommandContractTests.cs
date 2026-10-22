using System.Reflection;
using Xunit;
using StartupTool.App;

namespace StartupTool.Core.Tests;

/// <summary>
/// M18：单 exe 分流与 --beat 退出码契约回归（之前测试不引用 App，这些路径零自动覆盖）。
/// 本地校验路径（10/13）不依赖服务；11 路径会真实尝试连接不存在的默认管道，约 3 秒返回。
/// </summary>
public class BeatCommandContractTests
{
    private static string NowSec()
        => DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

    private const string ValidToken = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void 参数个数错_返回10()
    {
        Assert.Equal(10, BeatCommand.Run(new[] { "--beat" }));
        Assert.Equal(10, BeatCommand.Run(new[] { "--beat", ValidToken }));
        Assert.Equal(10, BeatCommand.Run(new[] { "--beat", ValidToken, NowSec(), "extra" }));
    }

    [Fact]
    public void 令牌格式错_返回10()
    {
        Assert.Equal(10, BeatCommand.Run(new[] { "--beat", "xyz", NowSec() }));
        Assert.Equal(10, BeatCommand.Run(new[] { "--beat", new string('g', 64), NowSec() }));
    }

    [Fact]
    public void 时间戳非数字_返回10()
    {
        Assert.Equal(10, BeatCommand.Run(new[] { "--beat", ValidToken, "not-a-number" }));
    }

    [Fact]
    public void 时间戳超差_返回13()
    {
        var old = DateTimeOffset.UtcNow.AddSeconds(-1000).ToUnixTimeSeconds().ToString();
        Assert.Equal(13, BeatCommand.Run(new[] { "--beat", ValidToken, old }));
    }

    [Fact]
    public void 令牌合法但无服务_返回11()
    {
        // 默认管道名 StartupTool-v1 在测试环境没有服务监听；连接 3 秒超时 → 11
        Assert.Equal(11, BeatCommand.Run(new[] { "--beat", ValidToken, NowSec() }));
    }

    [Fact]
    public void Program入口_beat分流_返回码透传()
    {
        // 验证 private Main 把 --beat 正确分流到 BeatCommand（而不是误进 WPF 分支）
        var main = typeof(Program).GetMethod("Main", BindingFlags.Static | BindingFlags.NonPublic)!;
        var code = (int)main.Invoke(null, new object[] { new[] { "--beat", "bad", NowSec() } })!;
        Assert.Equal(10, code);
    }
}
