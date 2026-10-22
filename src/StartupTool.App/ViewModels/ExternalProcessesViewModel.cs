using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StartupTool.App.Services;
using StartupTool.Core.Monitoring;

namespace StartupTool.App.ViewModels;

/// <summary>外部同名进程行（A8）。</summary>
public partial class ExternalProcessRowViewModel : ObservableObject
{
    [ObservableProperty] private int _pid;
    [ObservableProperty] private string _processName = string.Empty;
    [ObservableProperty] private string _startedText = "—";
    [ObservableProperty] private string _executablePath = "—";
    public DateTime? StartedAtUtc { get; set; }
}

/// <summary>
/// 外部同名进程窗口（A8）：列出服务托管主进程之外、匹配同一可执行文件的进程；
/// 终止必须用户显式确认，由服务端三重核对后执行，客户端不直接 Kill。
/// </summary>
public partial class ExternalProcessesViewModel : ObservableObject
{
    private readonly AppClientService _client;
    private readonly LocalizationService _loc;
    private readonly string _scriptId;

    public ExternalProcessesViewModel(AppClientService client, LocalizationService loc, string scriptId, string scriptName)
    {
        _client = client;
        _loc = loc;
        _scriptId = scriptId;
        ScriptName = scriptName;
    }

    public string ScriptName { get; }
    public ObservableCollection<ExternalProcessRowViewModel> Processes { get; } = new();

    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private string? _errorText;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        ErrorText = null;
        try
        {
            var all = await _client.GetExternalProcessesAsync();
            Processes.Clear();
            foreach (var p in all.Where(x => x.ScriptId == _scriptId))
            {
                Processes.Add(new ExternalProcessRowViewModel
                {
                    Pid = p.Pid,
                    ProcessName = p.ProcessName,
                    StartedAtUtc = p.StartedAtUtc,
                    StartedText = p.StartedAtUtc is { } t
                        ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                        : "—",
                    ExecutablePath = p.ExecutablePath ?? "—"
                });
            }
            IsEmpty = Processes.Count == 0;
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
    }

    [RelayCommand]
    private async Task KillAsync(ExternalProcessRowViewModel? row)
    {
        if (row is null) return;
        try
        {
            await _client.KillExternalAsync(row.Pid, row.StartedAtUtc);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
    }
}
