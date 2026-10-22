namespace StartupTool.Core.Models;

/// <summary>顶层配置（v2）。旧版 RunMode/WebPort 死字段已删除（B19）。</summary>
public sealed class AppConfig
{
    /// <summary>配置格式版本；当前为 2。</summary>
    public int Version { get; set; } = 2;

    public List<ScriptConfig> Scripts { get; set; } = new();

    public GlobalSettings Settings { get; set; } = new();
}
