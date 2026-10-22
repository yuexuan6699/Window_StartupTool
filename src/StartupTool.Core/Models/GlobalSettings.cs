namespace StartupTool.Core.Models;

/// <summary>全局设置（scripts.json 顶层 settings）。</summary>
public sealed class GlobalSettings
{
    /// <summary>单个日志文件大小上限 MB。（B10）</summary>
    public int LogMaxSizeMb { get; set; } = 5;

    /// <summary>日志保留天数。（B10）</summary>
    public int LogRetentionDays { get; set; } = 30;

    /// <summary>熔断滑动窗口长度（秒）。（A7）</summary>
    public int CircuitWindowSeconds { get; set; } = 60;

    /// <summary>熔断窗口内默认最大启动次数（脚本可覆盖）。（A7）</summary>
    public int CircuitMaxStarts { get; set; } = 10;

    /// <summary>崩溃/重启/心跳超时时弹托盘气泡通知。（C11）</summary>
    public bool NotifyOnFault { get; set; } = true;

    /// <summary>界面语言：zh-CN / en / null（跟随系统）。</summary>
    public string? Language { get; set; } = "zh-CN";

    /// <summary>
    /// 服务端写入前的白名单范围校验（N2）。返回 null=通过，否则为中文错误说明。
    /// 管道对所有交互用户开放，绝不能信任客户端原值：
    /// 熔断窗口 0 会让任何启动立即熔断，语言码任意值会让界面回退逻辑失真。
    /// </summary>
    public string? Validate()
    {
        if (LogMaxSizeMb is < 1 or > 1024)
            return "单个日志文件大小上限须在 1–1024 MB 之间。";
        if (LogRetentionDays is < 1 or > 3650)
            return "日志保留天数须在 1–3650 之间。";
        if (CircuitWindowSeconds is < 1 or > 86400)
            return "熔断滑动窗口须在 1–86400 秒之间。";
        if (CircuitMaxStarts is < 1 or > 1000)
            return "熔断启动次数阈值须在 1–1000 之间。";
        if (!string.IsNullOrEmpty(Language)
            && Language != "zh-CN"
            && Language != "en"
            && Language != "system")
            return "不支持的界面语言代码。";
        return null;
    }
}
