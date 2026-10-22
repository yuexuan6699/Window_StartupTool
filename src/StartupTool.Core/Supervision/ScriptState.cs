namespace StartupTool.Core.Supervision;

/// <summary>每脚本生命周期状态（A5）。</summary>
public enum ScriptState
{
    /// <summary>未运行。</summary>
    Stopped,

    /// <summary>正在创建 Job / 拉起进程（中间态，被信号量保护）。</summary>
    Starting,

    /// <summary>Job 内至少有一个进程存活。</summary>
    Running,

    /// <summary>正在终止 Job（中间态）。</summary>
    Stopping,

    /// <summary>熔断：自动守护暂停，等待人工「恢复守护」。</summary>
    Faulted
}

/// <summary>重启/退出原因。</summary>
public enum FaultReason
{
    None,
    Crash,
    ResourceLimit,
    HeartbeatTimeout,
    CircuitBreaker,
    StartFailed
}

/// <summary>心跳投递结果（H3/H4）。</summary>
public enum BeatResult
{
    Ok,
    InvalidToken,
    ClockSkew,
    NotRunning
}
