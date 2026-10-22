using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using StartupTool.Core.Models;

namespace StartupTool.Core.Configuration;

/// <summary>配置 JSON 序列化约定与深拷贝工具。</summary>
internal static class ConfigJson
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create() => new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // 配置文件需要人工查看/手改，中文等非 ASCII 字符直接输出，不转义
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>JSON 往返深拷贝，避免外部拿到可变引用破坏线程安全。</summary>
    public static AppConfig Clone(AppConfig config)
    {
        var json = JsonSerializer.Serialize(config, Options);
        var clone = JsonSerializer.Deserialize<AppConfig>(json, Options);
        return clone ?? new AppConfig();
    }
}
