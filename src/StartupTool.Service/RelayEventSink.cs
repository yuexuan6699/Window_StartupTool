using StartupTool.Core.Supervision;

namespace StartupTool.Service;

/// <summary>
/// 事件转发器：ScriptSupervisor 先于 IpcServer 构造（IpcServer 路由需要 supervisor），
/// 故先用转发器占位，IpcServer 建好后把 <see cref="Ipc.IpcServer.EventSink"/> 接上。
/// </summary>
internal sealed class RelayEventSink : ISupervisorEventSink
{
    public ISupervisorEventSink? Target { get; set; }

    public void Emit(SupervisorEvent e) => Target?.Emit(e);
}
