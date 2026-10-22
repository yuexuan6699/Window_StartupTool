namespace StartupTool.Core.Supervision;

/// <summary>
/// 心跳巡检：独立定时任务，5 秒一次，与管道收发、资源监控完全解耦（H5/H7）。
/// 判定与重启动作分离：先收集超时脚本，再在锁外走统一重启流程。
/// </summary>
public sealed class HeartbeatMonitor : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private readonly ScriptSupervisor _supervisor;
    private readonly Timer _timer;
    private int _tickRunning;
    private bool _disposed;

    public HeartbeatMonitor(ScriptSupervisor supervisor)
    {
        _supervisor = supervisor;
        _timer = new Timer(_ => Tick(), null, Interval, Interval);
    }

    private async void Tick()
    {
        // 防止上一轮还没跑完时重入
        if (Interlocked.Exchange(ref _tickRunning, 1) == 1) return;
        try
        {
            var timedOut = _supervisor.CollectHeartbeatTimeouts(DateTime.UtcNow);
            foreach (var id in timedOut)
            {
                await _supervisor.RestartAsync(id, FaultReason.HeartbeatTimeout);
            }
        }
        catch
        {
            // 巡检异常不应影响服务
        }
        finally
        {
            Interlocked.Exchange(ref _tickRunning, 0);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
    }
}
