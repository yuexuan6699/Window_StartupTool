using System.IO;
using System.Text.Json;

namespace StartupTool.App.Services;

/// <summary>
/// 纯本机 UI 偏好（决策 7 的②类）：%LOCALAPPDATA%\StartupTool\client.json。
/// 共享设置（语言/故障气泡）存服务端 scripts.json，不在这里。
/// 读取失败一律回默认值，绝不阻断启动。
/// </summary>
public sealed class ClientSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>关闭主窗口最小化到托盘的一次性提示是否已关闭。（C7）</summary>
    public bool HideToTrayHintDismissed { get; set; }

    public double WindowWidth { get; set; } = 1180;
    public double WindowHeight { get; set; } = 680;
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public bool WindowMaximized { get; set; }

    private static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "StartupTool", "client.json");

    public static ClientSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<ClientSettings>(File.ReadAllText(FilePath))
                       ?? new ClientSettings();
        }
        catch { /* 损坏/无权限：回默认 */ }
        return new ClientSettings();
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch { /* 偏好写盘失败不影响使用 */ }
    }
}
