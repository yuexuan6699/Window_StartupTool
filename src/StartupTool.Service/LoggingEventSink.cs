using StartupTool.Core.Logging;
using StartupTool.Core.Supervision;

namespace StartupTool.Service;

/// <summary>
/// 监管事件落盘（B10）：Fault/Information 写服务日志，ScriptOutput 写对应脚本日志目录。
/// StateChanged 不入盘——它是高频内部状态迁移，写盘只会淹没真正的故障记录。
/// </summary>
internal sealed class LoggingEventSink : ISupervisorEventSink
{
    private readonly LogStore _log;

    public LoggingEventSink(LogStore log) => _log = log;

    public void Emit(SupervisorEvent e)
    {
        switch (e.Kind)
        {
            case SupervisorEventKind.Fault:
                _log.WriteService("FAULT", e.ScriptName, e.Message);
                break;

            case SupervisorEventKind.Information:
                _log.WriteService("INFO", e.ScriptName, e.Message);
                break;

            case SupervisorEventKind.ScriptOutput:
                _log.WriteScriptOutput(e.ScriptId, e.ScriptName, e.Message);
                break;
        }
    }
}

/// <summary>
/// 事件多路转发：同一事件同时送往 IPC 广播入口与日志落盘入口。
/// 单个接收端异常不得影响其他接收端（IPC 广播不可因日志失败而丢事件）。
/// </summary>
internal sealed class TeeEventSink : ISupervisorEventSink
{
    private readonly ISupervisorEventSink[] _targets;

    public TeeEventSink(params ISupervisorEventSink[] targets) => _targets = targets;

    public void Emit(SupervisorEvent e)
    {
        foreach (var target in _targets)
        {
            try { target.Emit(e); }
            catch { /* 单个接收端异常隔离 */ }
        }
    }
}
