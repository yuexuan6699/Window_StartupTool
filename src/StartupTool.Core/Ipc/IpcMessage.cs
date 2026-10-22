using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization;
using StartupTool.Core.Models;
using StartupTool.Core.Supervision;

namespace StartupTool.Core.Ipc;

/// <summary>命名管道固定名称（限本机）。</summary>
public static class PipeNames
{
    public const string Default = "StartupTool-v1";
}

/// <summary>
/// IPC 信封：Type 判别 + RequestId（请求/响应配对，事件为空）+ Payload。
/// M3 仅 Beat/Ack；M4 扩展全部命令与事件。
/// </summary>
public sealed class IpcMessage
{
    public string Type { get; set; } = string.Empty;
    public string? RequestId { get; set; }
    public JsonElement? Payload { get; set; }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    public static IpcMessage Create<T>(string type, T payload, string? requestId = null) => new()
    {
        Type = type,
        RequestId = requestId,
        Payload = JsonSerializer.SerializeToElement(payload, JsonOptions)
    };

    public T? ReadPayload<T>() =>
        Payload is null ? default : JsonSerializer.Deserialize<T>(Payload.Value, JsonOptions);
}

/// <summary>心跳请求载荷（H4：仅令牌 + 时间戳，无业务字段）。</summary>
public sealed record BeatPayload(string Token, long Timestamp);

/// <summary>应答载荷。Code 取值：ok / invalidToken / clockSkew / error。</summary>
public sealed record AckPayload(bool Ok, string? Code = null, string? Error = null);

/// <summary>
/// 协议消息类型常量（线上字面值，勿随意改名；帧格式见 <see cref="PipeFrame"/>）。
/// 约定：请求/响应的响应帧回填同一 RequestId；事件帧 RequestId 为 null。
/// </summary>
public static class IpcTypes
{
    // ---- C→S 请求 ----
    /// <summary>连通性探测（控制端启动时判断服务是否在跑）。</summary>
    public const string Ping = "ping";

    /// <summary>订阅事件推送。载荷 <see cref="SubscribePayload"/>：声明控制端会话与是否要脚本输出。</summary>
    public const string Subscribe = "subscribe";

    /// <summary>取全部脚本当前状态。响应帧类型同 <see cref="ScriptStates"/>。</summary>
    public const string GetStates = "getStates";

    public const string StartScript = "startScript";
    public const string StopScript = "stopScript";
    public const string RestartScript = "restartScript";
    public const string StartAll = "startAll";
    public const string StopAll = "stopAll";

    /// <summary>从熔断恢复（A7）。载荷 <see cref="ResumeGuardPayload"/>。</summary>
    public const string ResumeGuard = "resumeGuard";

    /// <summary>心跳（--beat 一次性进程）。载荷 <see cref="BeatPayload"/>。</summary>
    public const string Beat = "beat";

    // ---- 配置类（M5 接入；import/export 与诊断包在 M6）----
    public const string UpsertScript = "upsertScript";        // ScriptConfig
    public const string DeleteScript = "deleteScript";        // ScriptIdPayload
    public const string ReorderScripts = "reorderScripts";    // ReorderPayload
    public const string ResetHeartbeatToken = "resetHeartbeatToken"; // ScriptIdPayload → HeartbeatTokenPayload
    public const string GetHeartbeatToken = "getHeartbeatToken";     // ScriptIdPayload → HeartbeatTokenPayload
    public const string GetConfig = "getConfig";              // 响应 AppConfigPayload
    public const string UpdateSettings = "updateSettings";    // SettingsPayload
    public const string GetExternalProcesses = "getExternalProcesses"; // 响应 ExternalProcessesPayload
    public const string KillExternal = "killExternal";        // KillExternalPayload
    public const string ImportConfig = "importConfig";        // M6
    public const string ExportConfig = "exportConfig";        // M6
    public const string ExportDiagnostics = "exportDiagnostics"; // M6

    // ---- S→C 响应 / 事件 ----
    public const string Ack = "ack";

    /// <summary>脚本状态批量快照：既是 getStates 的响应（带 RequestId），也是变更推送（无 RequestId）。</summary>
    public const string ScriptStates = "scriptStates";

    /// <summary>告警/信息通知（托盘气泡）。载荷 <see cref="NotifyPayload"/>。</summary>
    public const string Notify = "notify";

    /// <summary>脚本 stdout/stderr 一行。载荷 <see cref="ScriptOutputPayload"/>。</summary>
    public const string ScriptOutput = "scriptOutput";

    /// <summary>心跳令牌响应。载荷 <see cref="HeartbeatTokenPayload"/>。</summary>
    public const string HeartbeatToken = "heartbeatToken";

    /// <summary>外部同名进程列表响应。载荷 <see cref="ExternalProcessesPayload"/>。</summary>
    public const string ExternalProcesses = "externalProcesses";

    /// <summary>整份配置读取响应。载荷 <see cref="AppConfigPayload"/>。</summary>
    public const string ConfigData = "configData";

    /// <summary>导出配置响应。载荷 <see cref="ExportConfigResult"/>。</summary>
    public const string ExportConfigData = "exportConfigData";

    /// <summary>导入配置响应。载荷 <see cref="ImportConfigResult"/>。</summary>
    public const string ImportConfigData = "importConfigData";

    /// <summary>导出诊断包响应。载荷 <see cref="ExportDiagnosticsResult"/>。</summary>
    public const string ExportDiagnosticsData = "exportDiagnosticsData";
}

// ---------- C→S 载荷 ----------

/// <summary>单脚本类请求（启动/停止/重启/删除/重置令牌）。</summary>
public sealed record ScriptIdPayload(string ScriptId);

/// <summary>订阅声明（A9：控制端声明自己所在会话，服务端只向活动控制台会话的订阅者推命令结果无关的弹窗类事件）。</summary>
public sealed record SubscribePayload(int? SessionId, bool IncludeOutput = false);

/// <summary>熔断恢复请求。</summary>
public sealed record ResumeGuardPayload(string ScriptId, bool StartNow);

/// <summary>排序请求：按目标顺序给出脚本 id 列表。</summary>
public sealed record ReorderPayload(IReadOnlyList<string> OrderedIds);

/// <summary>整份配置（导入/导出/读取）。</summary>
public sealed record AppConfigPayload(AppConfig Config);

/// <summary>更新全局设置（M5：语言/故障气泡开关等共享项由服务端落盘）。</summary>
public sealed record SettingsPayload(GlobalSettings Settings);

/// <summary>心跳令牌响应（确保存在/重置后返回当前令牌，供界面"复制心跳命令"）。</summary>
public sealed record HeartbeatTokenPayload(string ScriptId, string Token);

/// <summary>外部同名进程列表。</summary>
public sealed record ExternalProcessesPayload(IReadOnlyList<Monitoring.ExternalProcessInfo> Processes);

/// <summary>
/// 请求终止外部进程（A8：仅用户显式操作）。服务端按 (PID, 创建时间) 重新核对后终止，
/// 绝不按名字匹配；目标必须不在本工具托管集合内。
/// </summary>
public sealed record KillExternalPayload(int Pid, DateTime? StartedAtUtc);

/// <summary>导出配置请求（D7）：IncludeTokens=true 时随文件带走心跳令牌。</summary>
public sealed record ExportConfigPayload(bool IncludeTokens);

/// <summary>导入配置请求（D7）：Json=文件内容；Replace=true 时连全局设置一起整体替换。</summary>
public sealed record ImportConfigPayload(string Json, bool Replace);

// ---------- S→C 载荷 ----------

/// <summary>导出配置响应：建议文件名 + 文件内容（由控制端写到用户选择的路径）。</summary>
public sealed record ExportConfigResult(string SuggestedFileName, string Json);

/// <summary>导入配置响应：导入的脚本数量。</summary>
public sealed record ImportConfigResult(int ScriptCount);

/// <summary>导出诊断包响应：zip 完整路径。</summary>
public sealed record ExportDiagnosticsResult(string ZipPath);

/// <summary>全部脚本状态快照。</summary>
public sealed record ScriptStatesPayload(IReadOnlyList<ScriptRuntimeSnapshot> Scripts);

/// <summary>通知（Fault→告警气泡；Information→普通提示）。</summary>
public sealed record NotifyPayload(
    string Kind,
    string ScriptId,
    string ScriptName,
    string Message,
    DateTime AtUtc);

/// <summary>脚本输出一行（M6 同时落盘）。</summary>
public sealed record ScriptOutputPayload(
    string ScriptId,
    string ScriptName,
    string Line,
    DateTime AtUtc);

/// <summary>--beat 进程的传输结果（在服务端 BeatResult 之外再含"连不上"）。</summary>
public enum BeatTransportResult
{
    Delivered,
    NoServer,
    InvalidToken,
    ClockSkew,
    /// <summary>令牌有效但对应脚本当前未运行（M9）。</summary>
    NotRunning,
    ProtocolError
}
