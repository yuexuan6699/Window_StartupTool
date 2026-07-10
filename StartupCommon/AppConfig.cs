namespace StartupCommon;

public class AppConfig
{
    public List<ScriptItem> Scripts { get; set; } = new();
    public string RunMode { get; set; } = "normal";
    public int WebPort { get; set; } = 13000;
}
