using StartupTool.Core.Models;

namespace StartupTool.Core.Templates;

/// <summary>
/// 内置脚本模板（D9）。
/// 每份模板只描述两类内容：一段可参考的脚本文本（<see cref="ScriptText"/>），以及一组「从模板新建」时
/// 预填到编辑窗口的默认字段。模板本身**不落盘**（单文件发布友好；脚本文件由用户自行保存后，把路径填回编辑窗）。
///
/// 名称/说明不在此硬编码为字符——Core 不引用 App 资源，因此这里只存资源键（<see cref="NameKey"/>/<see cref="DescriptionKey"/>），
/// 由 App 层用 <see cref="LocalizationService"/> 解析出最终文案。
/// </summary>
public sealed class ScriptTemplate
{
    /// <summary>稳定标识，用于拼接资源键前缀 <c>Str.Template.{Id}.Name/.Desc</c>。</summary>
    public required string Id { get; init; }

    /// <summary>模板名称的资源键（App 层解析）。</summary>
    public required string NameKey { get; init; }

    /// <summary>模板说明的资源键（App 层解析）。</summary>
    public required string DescriptionKey { get; init; }

    /// <summary>可参考的脚本文本（展示用、供复制；绝不写盘）。</summary>
    public required string ScriptText { get; init; }

    /// <summary>「从模板新建」时预填的命令行参数（可为空）。</summary>
    public string Arguments { get; init; } = string.Empty;

    /// <summary>预填：开机自动启动。</summary>
    public bool AutoStart { get; init; } = true;

    /// <summary>预填：允许控制端手动启动。</summary>
    public bool ManualEnabled { get; init; } = true;

    /// <summary>预填：启动前等待网络可用（D3）。</summary>
    public bool WaitForNetwork { get; init; }

    /// <summary>预填：以最高权限（SYSTEM）在登录用户桌面上运行（D6）。</summary>
    public bool RequireElevation { get; init; }

    /// <summary>预填：在服务会话（Session 0）运行。</summary>
    public bool RunInSystemSession { get; init; }

    /// <summary>预填：启动前固定延迟秒数（D3）。</summary>
    public int InitialDelaySeconds { get; init; }

    /// <summary>预填：崩溃自愈策略（D2）。</summary>
    public CrashPolicy CrashRestart { get; init; } = CrashPolicy.OnFailure;

    /// <summary>预填：启用应用层心跳（H 类）。</summary>
    public bool HeartbeatEnabled { get; init; }

    /// <summary>预填：心跳发送间隔（秒）。</summary>
    public int HeartbeatIntervalSeconds { get; init; } = 30;

    /// <summary>预填：心跳超时（秒）。</summary>
    public int HeartbeatTimeoutSeconds { get; init; } = 90;

    /// <summary>
    /// 生成一个全新的待编辑配置：Id 为随机 GUID、Path 留空（模板不落盘，由用户填实际脚本路径）、
    /// Name 取传入的本地化名称。该配置后续走「添加（isNew=true）」分支保存。
    /// </summary>
    public ScriptConfig CreateDraft(string displayName) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = displayName,
        Path = string.Empty,
        Arguments = Arguments,
        AutoStart = AutoStart,
        ManualEnabled = ManualEnabled,
        WaitForNetwork = WaitForNetwork,
        RequireElevation = RequireElevation,
        RunInSystemSession = RunInSystemSession,
        InitialDelaySeconds = InitialDelaySeconds,
        CrashRestart = CrashRestart,
        HeartbeatEnabled = HeartbeatEnabled,
        HeartbeatIntervalSeconds = HeartbeatIntervalSeconds,
        HeartbeatTimeoutSeconds = HeartbeatTimeoutSeconds
    };
}
