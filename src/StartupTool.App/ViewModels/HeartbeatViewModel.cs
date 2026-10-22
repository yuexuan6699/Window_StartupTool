using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StartupTool.App.Services;

namespace StartupTool.App.ViewModels;

/// <summary>
/// 心跳命令窗口（H2）：展示脚本当前令牌与 <c>--beat</c> 命令、一键复制、重置令牌（需二次确认）。
/// 令牌只存在于服务端配置；重置后旧令牌立即作废。
/// </summary>
public partial class HeartbeatViewModel : ObservableObject
{
    private readonly AppClientService _client;
    private readonly LocalizationService _loc;

    public HeartbeatViewModel(AppClientService client, LocalizationService loc, string scriptId, string scriptName)
    {
        _client = client;
        _loc = loc;
        ScriptId = scriptId;
        ScriptName = scriptName;
    }

    public string ScriptId { get; }
    public string ScriptName { get; }

    [ObservableProperty] private string _token = string.Empty;
    [ObservableProperty] private string? _errorText;
    [ObservableProperty] private bool _isBusy;

    /// <summary>示例命令；时间戳由脚本在运行时生成，这里给占位说明。</summary>
    public string CommandSample => string.IsNullOrEmpty(Token)
        ? string.Empty
        : $"StartupTool.exe --beat {Token} <{_loc.T("Str.Heartbeat.UnixPlaceholder")}>";

    public async Task LoadAsync()
    {
        IsBusy = true;
        ErrorText = null;
        try
        {
            Token = await _client.GetHeartbeatTokenAsync(ScriptId);
            OnPropertyChanged(nameof(CommandSample));
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ResetAsync()
    {
        IsBusy = true;
        ErrorText = null;
        try
        {
            Token = await _client.ResetHeartbeatTokenAsync(ScriptId);
            OnPropertyChanged(nameof(CommandSample));
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
