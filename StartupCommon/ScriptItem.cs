namespace StartupCommon;

public class ScriptItem
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public int Order { get; set; }
    public bool Enabled { get; set; } = true;
    public string Arguments { get; set; } = string.Empty;
}
