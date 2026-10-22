namespace StartupTool.Core.Models;

/// <summary>崩溃自愈策略（D2）。</summary>
public enum CrashPolicy
{
    /// <summary>崩溃后不自动重启。</summary>
    Never = 0,

    /// <summary>仅异常退出（退出码非 0 / 被杀死）时重启；正常退出不重启。默认。</summary>
    OnFailure = 1,

    /// <summary>无论正常/异常退出都重新拉起（常驻型脚本）。</summary>
    Always = 2
}
