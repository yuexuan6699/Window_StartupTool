namespace StartupTool.Core.Configuration;

/// <summary>旧版（v1）配置 DTO，仅用于读取与迁移。文件：config/scripts.json。</summary>
internal sealed class LegacyAppConfig
{
    public List<LegacyScriptItem> Scripts { get; set; } = new();

    /// <summary>死字段：迁移时丢弃（B19）。</summary>
    public string? RunMode { get; set; }

    /// <summary>死字段：迁移时丢弃（B19）。</summary>
    public int? WebPort { get; set; }
}

internal sealed class LegacyScriptItem
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public int Order { get; set; }

    /// <summary>旧版语义混合的开关：同时表示"开机自启"与"启用"。迁移时拆为 AutoStart / ManualEnabled（D1）。</summary>
    public bool Enabled { get; set; } = true;

    public string Arguments { get; set; } = string.Empty;
    public int MaxCpuPercent { get; set; } = 90;
    public long MaxMemoryMb { get; set; }
    public int MaxGpuPercent { get; set; } = 90;
    public int RestartDelaySeconds { get; set; } = 10;
}
