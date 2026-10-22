using StartupTool.Core.Models;

namespace StartupTool.Core.Supervision;

/// <summary>
/// 脚本运行状态快照（供 IPC 推送 / 界面渲染，C12/C14）。
/// </summary>
public sealed record ScriptRuntimeSnapshot
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required ScriptState State { get; init; }

    public int? Pid { get; init; }
    public int[] AllPids { get; init; } = Array.Empty<int>();

    public DateTime? StartedAtUtc { get; init; }
    public DateTime? StoppedAtUtc { get; init; }
    public int? ExitCode { get; init; }

    /// <summary>当前运行轮次已持续毫秒。</summary>
    public long? UptimeMs { get; init; }

    /// <summary>累计（自愈/手动）重启次数。</summary>
    public int RestartCount { get; init; }

    /// <summary>最近一次故障原因（可读中文）。</summary>
    public string? LastFault { get; init; }

    // 心跳（H）
    public bool HeartbeatEnabled { get; init; }
    public DateTime? LastBeatAtUtc { get; init; }
    public bool HeartbeatTimedOut { get; init; }

    // 资源（实时采样，采集不到为 null，界面显示 N/A）
    public double? CpuPercent { get; init; }
    public long? MemoryMb { get; init; }
    public double? GpuPercent { get; init; }

    /// <summary>检测到的同名外部进程数（只读展示，A8）。</summary>
    public int ExternalProcessCount { get; init; }

    /// <summary>配置里允许手动启动（便于界面决定按钮可用性）。</summary>
    public bool ManualEnabled { get; init; }

    // 会话感知（A3/A9）
    /// <summary>true=已排队，等用户登录后自动启动（此时 State=Stopped）。</summary>
    public bool WaitingForSession { get; init; }

    /// <summary>目标会话 ID（-1=未知/无活动会话）。</summary>
    public int SessionId { get; init; }
}
