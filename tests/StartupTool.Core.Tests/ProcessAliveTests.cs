using System.Reflection;
using Xunit;
using StartupTool.Core.Native;

namespace StartupTool.Core.Tests;

/// <summary>
/// M7：IsProcessAlive 只有明确"进程不存在"(ERROR_INVALID_PARAMETER=87) 才判死，
/// 其他打不开句柄的情况（权限不足等）一律按存活，避免误报 Job 已空触发错误重启。
/// 注：实测 QUERY_LIMITED_INFORMATION 下 PID 0/无效 PID 均返回 87，ACCESS_DENIED 难在
/// 测试环境稳定构造，其分支（错误码≠87→存活）由代码审查保证。
/// </summary>
public class ProcessAliveTests
{
    private static readonly MethodInfo IsAlive = typeof(JobObject)
        .GetMethod("IsProcessAlive", BindingFlags.Static | BindingFlags.NonPublic)!;

    private static bool Invoke(int pid) => (bool)IsAlive.Invoke(null, new object[] { pid })!;

    [Fact]
    public void 当前进程_判存活()
    {
        using var self = Process.GetCurrentProcess();
        Assert.True(Invoke(self.Id));
    }

    [Fact]
    public void 系统进程PID4_判存活()
    {
        // System 进程恒定存在且以更高完整性运行，至少证明非自身进程的存活判定有效
        Assert.True(Invoke(4));
    }

    [Fact]
    public void 不存在的PID_判已退出()
    {
        // 实测 OpenProcess 对 2147483646 返回 ERROR_INVALID_PARAMETER(87)
        Assert.False(Invoke(int.MaxValue - 1));
        Assert.False(Invoke(8)); // 启动早期 PID 通常已不存在；若环境特殊占用由前一条兜底
    }
}
