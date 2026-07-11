namespace StartupCommon;

public class ScriptItem
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public int Order { get; set; }
    public bool Enabled { get; set; } = true;
    public string Arguments { get; set; } = string.Empty;
    public int MaxCpuPercent { get; set; } = 90;
    public long MaxMemoryMb { get; set; } = 0;
    public int MaxGpuPercent { get; set; } = 90;
    public int RestartDelaySeconds { get; set; } = 10;
}
