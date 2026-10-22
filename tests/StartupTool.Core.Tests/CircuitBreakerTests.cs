using Xunit;
using StartupTool.Core.Supervision;

namespace StartupTool.Core.Tests;

public class CircuitBreakerTests
{
    [Fact]
    public void 窗口内超阈值熔断_滑出后恢复计数()
    {
        var cb = new CircuitBreaker();
        var t0 = new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc);
        var window = TimeSpan.FromSeconds(60);

        Assert.False(cb.RecordAndCheckTrip(t0, 3, window));
        Assert.False(cb.RecordAndCheckTrip(t0.AddSeconds(10), 3, window));
        Assert.False(cb.RecordAndCheckTrip(t0.AddSeconds(20), 3, window));
        Assert.True(cb.RecordAndCheckTrip(t0.AddSeconds(30), 3, window)); // 第 4 次熔断

        // 95s 后 0/10/20/30 的记录全部滑出 60s 窗口，只剩本次
        Assert.False(cb.RecordAndCheckTrip(t0.AddSeconds(95), 3, window));
        Assert.Equal(1, cb.CountInWindow(t0.AddSeconds(95), window));

        cb.Reset();
        Assert.Equal(0, cb.CountInWindow(t0.AddSeconds(95), window));
    }
}
