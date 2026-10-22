using StartupTool.Core.Models;
using StartupTool.Core.Native;
using StartupTool.Core.Processes;

namespace StartupTool.Core.Supervision;

/// <summary>
/// 单个脚本的全部运行时状态。所有字段仅允许在持有所属 <see cref="SemaphoreSlim"/> 时读写。
/// </summary>
internal sealed class RunningScript : IDisposable
{
    public required ScriptConfig Config { get; set; }
    public ScriptState State { get; set; } = ScriptState.Stopped;

    public JobObject? Job { get; set; }
    public LaunchedProcess? Process { get; set; }
    public ProcessIdentity RootIdentity { get; set; }
    public int[] CurrentPids { get; set; } = Array.Empty<int>();

    /// <summary>串行化该脚本的一切状态迁移（A5）。</summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);

    /// <summary>Job 存活轮询循环取消源。</summary>
    public CancellationTokenSource? WatchCts { get; set; }

    public DateTime? StartedAtUtc { get; set; }
    public DateTime? StoppedAtUtc { get; set; }
    public int? ExitCode { get; set; }
    public int RestartCount { get; set; }
    public FaultReason LastReason { get; set; } = FaultReason.None;
    public string? LastFault { get; set; }

    /// <summary>true=用户主动停止，Job 空了也不自愈。</summary>
    public bool UserIntendedStop { get; set; }

    public CircuitBreaker Circuit { get; } = new();

    // 心跳
    public DateTime? LastBeatAtUtc { get; set; }
    public bool HeartbeatTimedOut { get; set; }

    // 最近一次资源采样
    public double? CpuPercent { get; set; }
    public long? MemoryMb { get; set; }
    public double? GpuPercent { get; set; }

    public int ExternalProcessCount { get; set; }

    // 会话感知（A3/A9）
    /// <summary>最近一次解析到的目标会话 ID（-1=无活动会话）。</summary>
    public int SessionId { get; set; } = -1;

    /// <summary>true=AutoStart 时无人登录，已排队，登录后由补发流程启动。</summary>
    public bool WaitingForSession { get; set; }

    public bool IsAliveInstance => State is ScriptState.Starting or ScriptState.Running or ScriptState.Stopping;

    public void Dispose()
    {
        WatchCts?.Dispose();
        Process?.Dispose();
        Job?.Dispose();
        Gate.Dispose();
    }
}
