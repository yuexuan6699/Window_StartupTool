namespace StartupTool.Core.Supervision;

public enum SupervisorEventKind
{
    /// <summary>脚本状态变化（IPC 推送给控制端）。</summary>
    StateChanged,

    /// <summary>需要告警/托盘通知的故障（崩溃、资源超限、心跳超时、熔断、启动失败）。</summary>
    Fault,

    /// <summary>正常运行信息（如"无登录用户，已排队待登录后启动"），不触发告警。</summary>
    Information,

    /// <summary>脚本 stdout/stderr 一行输出（M6 落盘 + 界面日志窗）。</summary>
    ScriptOutput
}

/// <summary>监管事件。M4 由 IPC 服务端接收并推送/记录；M3 可为空实现。</summary>
public sealed record SupervisorEvent(
    SupervisorEventKind Kind,
    string ScriptId,
    string ScriptName,
    string Message,
    ScriptRuntimeSnapshot? Snapshot = null);

/// <summary>事件接收器。</summary>
public interface ISupervisorEventSink
{
    void Emit(SupervisorEvent e);
}

/// <summary>空实现（测试/未接入管道时）。</summary>
public sealed class NullSupervisorEventSink : ISupervisorEventSink
{
    public void Emit(SupervisorEvent e) { }
}
