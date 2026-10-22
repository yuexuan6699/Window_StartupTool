namespace StartupTool.Core.Supervision;

/// <summary>
/// 滑动窗口熔断（A7）：窗口内启动次数超过阈值即熔断，阻止自重启死循环。
/// 手动/自动的每一次 StartUnderLock 都计入；心跳超时、资源超限导致的重启同样计入（H6）。
/// 纯逻辑、无锁（仅由所属脚本的信号量串行访问）。
/// </summary>
internal sealed class CircuitBreaker
{
    private readonly Queue<DateTime> _starts = new();

    /// <summary>
    /// 记录一次启动并判断是否应熔断。
    /// </summary>
    /// <param name="now">当前时刻。</param>
    /// <param name="maxStarts">窗口内允许的最大启动次数；第 maxStarts+1 次熔断。</param>
    /// <param name="window">滑动窗口长度。</param>
    /// <returns>true=已熔断（本次启动应放弃）；false=放行。</returns>
    public bool RecordAndCheckTrip(DateTime now, int maxStarts, TimeSpan window)
    {
        while (_starts.Count > 0 && now - _starts.Peek() > window)
            _starts.Dequeue();

        _starts.Enqueue(now);
        return _starts.Count > maxStarts;
    }

    public void Reset() => _starts.Clear();

    public int CountInWindow(DateTime now, TimeSpan window)
    {
        while (_starts.Count > 0 && now - _starts.Peek() > window)
            _starts.Dequeue();
        return _starts.Count;
    }
}
