using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StartupTool.App.Services;
using StartupTool.Core.Ipc;
using StartupTool.Core.Models;

namespace StartupTool.App.ViewModels;

/// <summary>
/// 设置对话框：语言（决策 4 实时切换）+ 故障气泡开关。
/// 两设置都存服务端 scripts.json（决策 7），由 <see cref="LocalizationService"/> 立即应用语言。
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly AppClientService _client;
    private readonly LocalizationService _loc;
    private readonly Action _onLanguageApplied;
    private GlobalSettings _settings = new();

    public SettingsViewModel(AppClientService client, LocalizationService loc, Action onLanguageApplied)
    {
        _client = client;
        _loc = loc;
        _onLanguageApplied = onLanguageApplied;
    }

    [ObservableProperty] private string _selectedLanguage = LocalizationService.FollowSystem;
    [ObservableProperty] private bool _notifyOnFault;

    // B08：全局数值项（此前界面上无处可改）。越界值由服务端 GlobalSettings.Validate 拒绝，
    // 失败原因显示在窗内 ErrorText；服务端配置始终权威（非法值绝不落盘）。
    [ObservableProperty] private int _logMaxSizeMb;
    [ObservableProperty] private int _logRetentionDays;
    [ObservableProperty] private int _circuitWindowSeconds;
    [ObservableProperty] private int _circuitMaxStarts;

    [ObservableProperty] private string? _errorText;
    private bool _isLoading = true;

    public void Load(AppConfig config)
    {
        _settings = config.Settings;
        SelectedLanguage = string.IsNullOrEmpty(config.Settings.Language)
            ? LocalizationService.FollowSystem
            : config.Settings.Language!;
        NotifyOnFault = config.Settings.NotifyOnFault;
        LogMaxSizeMb = config.Settings.LogMaxSizeMb;
        LogRetentionDays = config.Settings.LogRetentionDays;
        CircuitWindowSeconds = config.Settings.CircuitWindowSeconds;
        CircuitMaxStarts = config.Settings.CircuitMaxStarts;
        _isLoading = false;
    }

    /// <summary>选择即应用：语言立即切换（不重启），同时静默保存到服务端。</summary>
    partial void OnSelectedLanguageChanged(string value) { if (!_isLoading) _ = ApplyAsync(saveOnly: false); }

    partial void OnNotifyOnFaultChanged(bool value) { if (!_isLoading) _ = ApplyAsync(saveOnly: true); }

    // 数值项：TextBox 默认 LostFocus 触发，失焦即保存；越界由服务端拒绝后显示 ErrorText
    partial void OnLogMaxSizeMbChanged(int value) { if (!_isLoading) _ = ApplyAsync(saveOnly: true); }
    partial void OnLogRetentionDaysChanged(int value) { if (!_isLoading) _ = ApplyAsync(saveOnly: true); }
    partial void OnCircuitWindowSecondsChanged(int value) { if (!_isLoading) _ = ApplyAsync(saveOnly: true); }
    partial void OnCircuitMaxStartsChanged(int value) { if (!_isLoading) _ = ApplyAsync(saveOnly: true); }

    private async Task ApplyAsync(bool saveOnly)
    {
        ErrorText = null;
        var updated = CloneSettings(_settings);
        updated.Language = SelectedLanguage == LocalizationService.FollowSystem ? null : SelectedLanguage;
        updated.NotifyOnFault = NotifyOnFault;
        updated.LogMaxSizeMb = LogMaxSizeMb;
        updated.LogRetentionDays = LogRetentionDays;
        updated.CircuitWindowSeconds = CircuitWindowSeconds;
        updated.CircuitMaxStarts = CircuitMaxStarts;

        try
        {
            await _client.UpdateSettingsAsync(updated);
            _settings = updated;
            if (!saveOnly)
            {
                _loc.ApplyFromSetting(updated.Language);
                _onLanguageApplied();
            }
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
    }

    [RelayCommand]
    private Task CloseAsync() => Task.CompletedTask;

    private static GlobalSettings CloneSettings(GlobalSettings s) => new()
    {
        Language = s.Language,
        NotifyOnFault = s.NotifyOnFault,
        LogMaxSizeMb = s.LogMaxSizeMb,
        LogRetentionDays = s.LogRetentionDays,
        CircuitWindowSeconds = s.CircuitWindowSeconds,
        CircuitMaxStarts = s.CircuitMaxStarts
    };
}
