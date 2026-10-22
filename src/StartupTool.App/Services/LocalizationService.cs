using System.Globalization;
using System.Windows;

namespace StartupTool.App.Services;

/// <summary>
/// 界面语言服务（C13）：通过替换 Application 级 MergedDictionary 实现实时切换、无需重启。
/// XAML 一律 <c>{DynamicResource Str.Xxx}</c>；C# 代码用 <see cref="T"/>。
/// 语言偏好来自服务端 GlobalSettings.Language（zh-CN / en / system）。
/// </summary>
public sealed class LocalizationService
{
    public const string ZhCn = "zh-CN";
    public const string En = "en";
    public const string FollowSystem = "system";

    private static readonly Dictionary<string, string> DictionaryFiles = new()
    {
        [ZhCn] = "Resources/Strings.zh-CN.xaml",
        [En] = "Resources/Strings.en-US.xaml"
    };

    private readonly Application _app;
    private ResourceDictionary? _active;

    public LocalizationService(Application app) => _app = app;

    /// <summary>当前实际生效语言（system 已解析为具体语言）。</summary>
    public string CurrentLanguage { get; private set; } = ZhCn;

    /// <summary>把"跟随系统"解析为具体语言。</summary>
    public static string ResolveSystem(string? setting)
    {
        if (string.IsNullOrWhiteSpace(setting) || setting == FollowSystem)
        {
            var two = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            return string.Equals(two, "zh", StringComparison.OrdinalIgnoreCase) ? ZhCn : En;
        }
        return setting == En ? En : ZhCn;
    }

    /// <summary>按存储值（zh-CN/en/system）应用语言。</summary>
    public void ApplyFromSetting(string? setting) => Apply(ResolveSystem(setting));

    public void Apply(string language)
    {
        language = language == En ? En : ZhCn;

        // 先加载新字典；加载失败时保留旧语言，避免界面文案整体丢失
        var next = new ResourceDictionary
        {
            // 注意：逻辑语言代码 en 对应资源文件 Strings.en-US.xaml，必须走 DictionaryFiles 映射
            Source = new Uri($"pack://application:,,,/{DictionaryFiles[language]}", UriKind.Absolute)
        };

        if (_active is not null)
            _app.Resources.MergedDictionaries.Remove(_active);
        _active = next;
        // 语言字典放最后：同名键后入者优先，且不覆盖主题字典
        _app.Resources.MergedDictionaries.Add(_active);

        // 切换 CurrentCulture 让 ToString 等本地化行为一致
        var culture = language == En ? "en-US" : "zh-CN";
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);

        CurrentLanguage = language;
    }

    /// <summary>取文案；缺失键返回键名本身，绝不抛异常（避免界面因漏译崩溃）。</summary>
    public string T(string key, params object[] args)
    {
        var value = _app.TryFindResource(key) as string;
        if (string.IsNullOrEmpty(value)) value = key;
        return args.Length == 0 ? value : string.Format(CultureInfo.CurrentCulture, value, args);
    }
}
