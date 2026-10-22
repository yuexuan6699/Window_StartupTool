using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StartupTool.App.Services;
using StartupTool.Core;
using StartupTool.Core.Ipc;
using StartupTool.Core.Models;
using StartupTool.Core.Supervision;
using StartupTool.Core.Templates;

namespace StartupTool.App.ViewModels;

public enum InfoKind { Info, Success, Warning, Error }

/// <summary>
/// 主窗口视图模型：状态增量合并（C4）、搜索过滤（C5）、全部脚本操作与配置编辑入口。
/// 自身绝不启停脚本——所有动作经 <see cref="AppClientService"/> 走管道。
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly AppClientService _client;
    private readonly LocalizationService _loc;
    private readonly IUiServices _ui;
    private readonly DispatcherTimer _infoTimer;

    private AppConfig _config = new();

    public MainViewModel(AppClientService client, LocalizationService loc, IUiServices ui)
    {
        _client = client;
        _loc = loc;
        _ui = ui;

        ScriptsView = CollectionViewSource.GetDefaultView(Scripts);
        ScriptsView.Filter = obj =>
            string.IsNullOrWhiteSpace(SearchText)
            || (obj is ScriptRowViewModel row
                && row.Name.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase));

        _infoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _infoTimer.Tick += (_, _) => { _infoTimer.Stop(); InfoVisible = false; };
    }

    public ObservableCollection<ScriptRowViewModel> Scripts { get; } = new();
    public ICollectionView ScriptsView { get; }

    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _hasScripts;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private ScriptRowViewModel? _selectedScript;
    [ObservableProperty] private string _infoText = string.Empty;
    [ObservableProperty] private InfoKind _infoKind;
    [ObservableProperty] private bool _infoVisible;
    [ObservableProperty] private string _traySummary = string.Empty;

    partial void OnSearchTextChanged(string value)
    {
        ScriptsView.Refresh();
        // N8：CanReorder 依赖 SearchText，搜索状态变化必须即时刷新按钮/命令可用性
        NotifyReorderCanExecuteChanged();
    }

    private void NotifyReorderCanExecuteChanged()
    {
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }

    // ---------- 服务端事件（已由 App 层 Dispatcher 封送到 UI 线程）----------

    public void SetConnected(bool connected)
    {
        IsConnected = connected;
        StatusText = connected
            ? _loc.T("Str.Conn.Connected")
            : _loc.T("Str.Conn.Disconnected");
        if (!connected) Info(_loc.T("Str.Conn.Disconnected"), InfoKind.Warning);
        // N8：CanReorder 依赖 IsConnected，连接状态变化即时刷新
        NotifyReorderCanExecuteChanged();
    }

    public void MergeStates(IReadOnlyList<ScriptRuntimeSnapshot> snapshots)
    {
        var existing = Scripts.ToDictionary(r => r.Id);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var insertAt = 0;

        foreach (var snap in snapshots) // 快照已按 Order 排序（C2 顺序的唯一权威）
        {
            used.Add(snap.Id);
            if (existing.TryGetValue(snap.Id, out var row))
            {
                row.Loc = _loc;
                row.UpdateFrom(snap);
                var currentIndex = Scripts.IndexOf(row);
                if (currentIndex >= 0 && currentIndex != insertAt)
                    Scripts.Move(currentIndex, Math.Min(insertAt, Scripts.Count - 1));
            }
            else
            {
                var added = new ScriptRowViewModel { Loc = _loc };
                added.UpdateFrom(snap);
                Scripts.Insert(Math.Min(insertAt, Scripts.Count), added);
            }
            insertAt++;
        }

        for (var i = Scripts.Count - 1; i >= 0; i--)
            if (!used.Contains(Scripts[i].Id)) Scripts.RemoveAt(i);

        HasScripts = Scripts.Count > 0;
        UpdateTraySummary();
    }

    private void UpdateTraySummary()
    {
        var running = Scripts.Count(r => r.IsRunning);
        TraySummary = _loc.T("Str.Tray.Summary", running, Scripts.Count);
    }

    public void OnNotify(NotifyPayload notify)
    {
        var kind = notify.Kind == "fault" ? InfoKind.Error : InfoKind.Info;
        var name = string.IsNullOrEmpty(notify.ScriptName) ? string.Empty : $"[{notify.ScriptName}] ";
        Info(name + notify.Message, kind);
    }

    public void SetConfig(AppConfig config) => _config = config;

    /// <summary>每秒刷新运行时长等相对时间。</summary>
    public void Tick(DateTime nowUtc)
    {
        foreach (var row in Scripts) row.Tick(nowUtc);
    }

    /// <summary>语言切换后刷新所有派生文案。</summary>
    public void RefreshLocalization()
    {
        foreach (var row in Scripts)
        {
            row.Loc = _loc;
            row.RaiseDerivedProperties();
        }
        SetConnected(IsConnected);
        // B08：状态栏摘要由 VM 拼接，语言变化时必须一并重建
        UpdateTraySummary();
    }

    public void Info(string text, InfoKind kind = InfoKind.Info)
    {
        InfoText = text;
        InfoKind = kind;
        InfoVisible = true;
        _infoTimer.Stop();
        _infoTimer.Start();
    }

    private ScriptRowViewModel? ResolveRow(object? param)
        => param switch
        {
            ScriptRowViewModel r => r,
            string id when !string.IsNullOrEmpty(id) => Scripts.FirstOrDefault(s => s.Id == id),
            _ => SelectedScript
        };

    // ---------- 监控命令 ----------

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(() => _client.RefreshStatesAsync());

    [RelayCommand]
    private Task StopAllAsync() => RunAsync(() => _client.StopAllAsync());

    [RelayCommand]
    private Task StartAsync(object? param)
    {
        var row = ResolveRow(param);
        return row is null ? Task.CompletedTask : RunAsync(() => _client.StartScriptAsync(row.Id));
    }

    [RelayCommand]
    private Task StopAsync(object? param)
    {
        var row = ResolveRow(param);
        return row is null ? Task.CompletedTask : RunAsync(() => _client.StopScriptAsync(row.Id));
    }

    [RelayCommand]
    private Task RestartAsync(object? param)
    {
        var row = ResolveRow(param);
        return row is null ? Task.CompletedTask : RunAsync(() => _client.RestartScriptAsync(row.Id));
    }

    [RelayCommand]
    private Task ResumeGuardAsync(object? param)
    {
        var row = ResolveRow(param);
        return row is null
            ? Task.CompletedTask
            : RunAsync(() => _client.ResumeGuardAsync(row.Id, startNow: true));
    }

    [RelayCommand]
    private void ShowOutput(object? param)
    {
        var row = ResolveRow(param);
        if (row is not null) _ui.ShowOutput(row);
    }

    [RelayCommand]
    private void ShowExternal(object? param)
    {
        var row = ResolveRow(param);
        if (row is not null) _ui.ShowExternalProcesses(row);
    }

    // ---------- 配置编辑（C1/C3/C6）----------

    [RelayCommand]
    private async Task AddAsync()
    {
        var draft = new ScriptConfig
        {
            AutoStart = true,
            ManualEnabled = true,
            CrashRestart = CrashPolicy.OnFailure
        };
        var result = _ui.ShowEditDialog(draft, isNew: true, _config.Scripts);
        if (result is not null) await SaveScriptAsync(result, isNew: true, previous: null);
    }

    [RelayCommand]
    private async Task AddFromTemplateAsync()
    {
        var template = _ui.PickTemplate();
        if (template is null) return; // 用户取消

        // D9：模板只预填字段（含心跳/提权/等待网络等开关），并给出可复制的脚本文本；
        // 真正的脚本路径仍由用户填。CreateDraft 生成全新 Id 与空 Path，走「新建」分支保存。
        var draft = template.CreateDraft(_loc.T(template.NameKey));
        var result = _ui.ShowEditDialog(draft, isNew: true, _config.Scripts);
        if (result is not null) await SaveScriptAsync(result, isNew: true, previous: null);
    }

    [RelayCommand]
    private async Task EditAsync(object? param)
    {
        var row = ResolveRow(param);
        if (row is null) return;

        var current = _config.Scripts.FirstOrDefault(s => s.Id == row.Id);
        if (current is null)
        {
            // N1：配置尚未拉到/服务刚连上的瞬间，先强制拉一次；仍失败则拒绝编辑。
            // 绝不用 {Id,Name} 残缺兜底对象开窗——保存会把令牌、依赖与守护字段重置为默认值。
            await ReloadConfigAsync();
            current = _config.Scripts.FirstOrDefault(s => s.Id == row.Id);
            if (current is null)
            {
                _ui.ShowMessage(
                    _loc.T("Str.Edit.ConfigUnavailableTitle"),
                    _loc.T("Str.Edit.ConfigUnavailableMessage"),
                    error: true);
                return;
            }
        }

        var source = ScriptCloner.Clone(current);
        var result = _ui.ShowEditDialog(source, isNew: false, _config.Scripts);
        if (result is null) return;

        await SaveScriptAsync(result, isNew: false, previous: current);
    }

    [RelayCommand]
    private async Task DeleteAsync(object? param)
    {
        var row = ResolveRow(param);
        if (row is null) return;

        var extra = row.IsRunning || row.WaitingForSession
            ? _loc.T("Str.Delete.RunningWarning")
            : null;
        if (!_ui.Confirm(_loc.T("Str.Delete.Title"),
                _loc.T("Str.Delete.Message", row.Name), extra, danger: true))
            return;

        await RunAsync(async () =>
        {
            await _client.DeleteScriptAsync(row.Id);
            await ReloadConfigAsync();
        });
    }

    private async Task SaveScriptAsync(ScriptConfig script, bool isNew, ScriptConfig? previous)
    {
        var wasRunning = Scripts.FirstOrDefault(s => s.Id == script.Id)?.IsRunning ?? false;
        var startupChanged = isNew || StartupPropertiesChanged(previous, script);

        await RunAsync(async () =>
        {
            await _client.UpsertScriptAsync(script);
            await ReloadConfigAsync();
        });

        // 决策 5：启动属性改动需重启才生效，在最恰当的时机询问一次
        if (wasRunning && startupChanged && _ui.AskRestartNow(script.Name))
            await RunAsync(() => _client.RestartScriptAsync(script.Id));
    }

    private static bool StartupPropertiesChanged(ScriptConfig? a, ScriptConfig b)
    {
        if (a is null) return true;
        if (!string.Equals(a.Path?.Trim(), b.Path?.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
        if (a.Arguments != b.Arguments) return true;
        if ((a.WorkingDirectory ?? string.Empty) != (b.WorkingDirectory ?? string.Empty)) return true;
        if (a.RunInSystemSession != b.RunInSystemSession) return true;
        if (a.EnvironmentVariables.Count != b.EnvironmentVariables.Count) return true;
        foreach (var (k, v) in b.EnvironmentVariables)
            if (!a.EnvironmentVariables.TryGetValue(k, out var old) || old != v) return true;
        return false;
    }

    private async Task ReloadConfigAsync()
    {
        try { _config = await _client.GetConfigAsync(); }
        catch { /* 配置刷新失败不阻断：状态推送仍正常 */ }
    }

    // ---------- 排序（C2）----------

    private bool CanReorder() => IsConnected && string.IsNullOrWhiteSpace(SearchText);

    [RelayCommand(CanExecute = nameof(CanReorder))]
    private async Task MoveUpAsync(object? param)
    {
        var row = ResolveRow(param);
        if (row is null) return;
        var i = Scripts.IndexOf(row);
        if (i <= 0) return;
        Scripts.Move(i, i - 1);
        await SubmitReorderAsync();
    }

    [RelayCommand(CanExecute = nameof(CanReorder))]
    private async Task MoveDownAsync(object? param)
    {
        var row = ResolveRow(param);
        if (row is null) return;
        var i = Scripts.IndexOf(row);
        if (i < 0 || i >= Scripts.Count - 1) return;
        Scripts.Move(i, i + 1);
        await SubmitReorderAsync();
    }

    /// <summary>行拖拽完成：把 dragged 移到 target 前/后并落盘（C2）。</summary>
    public async Task ApplyDragOrderAsync(string draggedId, string targetId, bool after)
    {
        if (draggedId == targetId || !CanReorder()) return;
        var dragged = Scripts.FirstOrDefault(s => s.Id == draggedId);
        var target = Scripts.FirstOrDefault(s => s.Id == targetId);
        if (dragged is null || target is null) return;

        var oldIndex = Scripts.IndexOf(dragged);
        var targetIndex = Scripts.IndexOf(target);
        Scripts.RemoveAt(oldIndex);
        if (oldIndex < targetIndex) targetIndex--;
        Scripts.Insert(Math.Clamp(after ? targetIndex + 1 : targetIndex, 0, Scripts.Count), dragged);
        await SubmitReorderAsync();
    }

    private async Task SubmitReorderAsync()
    {
        var ids = Scripts.Select(s => s.Id).ToList();
        await RunAsync(async () =>
        {
            await _client.ReorderScriptsAsync(ids);
            await ReloadConfigAsync();
        });
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }

    // ---------- 杂项入口 ----------

    [RelayCommand]
    private void OpenSettings() => _ui.ShowSettings();

    [RelayCommand]
    private void OpenFileLocation(object? param)
    {
        var row = ResolveRow(param);
        var path = row is null
            ? null
            : _config.Scripts.FirstOrDefault(s => s.Id == row.Id)?.Path;
        ShellHelper.OpenFileLocation(path);
    }

    [RelayCommand]
    private void OpenConfigFile() => ShellHelper.OpenFileLocation(AppPaths.ConfigFile);

    [RelayCommand]
    private void OpenLogsFolder() => ShellHelper.OpenFolder(AppPaths.LogsDirectory);

    // ---------- 导入/导出/诊断包（D7/D8）----------

    /// <summary>
    /// 导出配置（D7）：先询问是否包含心跳令牌，再请求服务端生成 JSON，最后由用户选择保存路径落盘。
    /// </summary>
    [RelayCommand]
    private async Task ExportConfigAsync()
    {
        var (includeTokens, confirmed) = _ui.ConfirmWithOption(
            _loc.T("Str.ExportConfig.IncludeTokenTitle"),
            _loc.T("Str.ExportConfig.IncludeTokenMessage"),
            _loc.T("Str.ExportConfig.IncludeTokenCheck"));
        if (!confirmed) return;

        var result = await _client.ExportConfigAsync(includeTokens);
        if (result is null)
        {
            Info(_loc.T("Str.ExportConfig.Failed"), InfoKind.Error);
            return;
        }

        var path = _ui.AskSaveFile(result.SuggestedFileName, _loc.T("Str.ExportConfig.FileFilter"));
        if (path is null) return;

        try
        {
            File.WriteAllText(path, result.Json, new UTF8Encoding(false));
            Info(_loc.T("Str.ExportConfig.Done", path), InfoKind.Success);
        }
        catch (Exception ex)
        {
            Info($"{_loc.T("Str.ExportConfig.Failed")} {ex.Message}", InfoKind.Error);
        }
    }

    /// <summary>
    /// 导入配置（D7）：选文件 → 读文件 → 确认（并决定是否连全局设置一起替换）→ 交服务端校验落盘。
    /// 服务端返回 null 表示文件非法/依赖成环/被拒，界面给出失败提示。
    /// </summary>
    [RelayCommand]
    private async Task ImportConfigAsync()
    {
        var path = _ui.AskOpenFile(_loc.T("Str.ImportConfig.FileFilter"));
        if (path is null) return;

        string json;
        try
        {
            json = await File.ReadAllTextAsync(path);
        }
        catch (Exception ex)
        {
            Info($"{_loc.T("Str.ImportConfig.ReadFailed")} {ex.Message}", InfoKind.Error);
            return;
        }

        var (replace, confirmed) = _ui.ConfirmWithOption(
            _loc.T("Str.ImportConfig.ConfirmTitle"),
            _loc.T("Str.ImportConfig.ConfirmMessage"),
            _loc.T("Str.ImportConfig.ReplaceSettingsCheck"),
            danger: true);
        if (!confirmed) return;

        var result = await _client.ImportConfigAsync(json, replace);
        if (result is null)
        {
            Info(_loc.T("Str.ImportConfig.Failed"), InfoKind.Error);
            return;
        }

        await ReloadConfigAsync();
        Info(_loc.T("Str.ImportConfig.Done", result.ScriptCount), InfoKind.Success);
    }

    /// <summary>导出诊断包（D8）：服务端打成 zip 后，界面提示路径并在资源管理器中定位。</summary>
    [RelayCommand]
    private async Task ExportDiagnosticsAsync()
    {
        var result = await _client.ExportDiagnosticsAsync();
        if (result is null)
        {
            Info(_loc.T("Str.Diagnostics.Failed"), InfoKind.Error);
            return;
        }

        Info(_loc.T("Str.Diagnostics.Done", result.ZipPath), InfoKind.Success);
        _ui.RevealInExplorer(result.ZipPath);
    }

    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (IpcAckException ex)
        {
            Info(_loc.T("Str.Common.RejectedByService", ex.Message), InfoKind.Error);
        }
        catch (Exception ex)
        {
            Info(ex.Message, InfoKind.Error);
        }
    }
}

/// <summary>ScriptConfig 深拷贝（Core 的 ConfigJson.Clone 为 internal，App 侧自带一份）。</summary>
internal static class ScriptCloner
{
    public static ScriptConfig Clone(ScriptConfig s) => new()
    {
        Id = s.Id,
        Name = s.Name,
        Path = s.Path,
        Arguments = s.Arguments,
        Order = s.Order,
        AutoStart = s.AutoStart,
        ManualEnabled = s.ManualEnabled,
        RunInSystemSession = s.RunInSystemSession,
        WorkingDirectory = s.WorkingDirectory,
        EnvironmentVariables = new Dictionary<string, string>(s.EnvironmentVariables),
        RequireElevation = s.RequireElevation,
        InitialDelaySeconds = s.InitialDelaySeconds,
        WaitForNetwork = s.WaitForNetwork,
        DependsOn = new List<string>(s.DependsOn),
        StartIntervalSeconds = s.StartIntervalSeconds,
        MaxCpuPercent = s.MaxCpuPercent,
        MaxMemoryMb = s.MaxMemoryMb,
        MaxGpuPercent = s.MaxGpuPercent,
        RestartDelaySeconds = s.RestartDelaySeconds,
        CrashRestart = s.CrashRestart,
        MaxRestartsPerWindow = s.MaxRestartsPerWindow,
        HeartbeatEnabled = s.HeartbeatEnabled,
        HeartbeatIntervalSeconds = s.HeartbeatIntervalSeconds,
        HeartbeatTimeoutSeconds = s.HeartbeatTimeoutSeconds,
        HeartbeatToken = s.HeartbeatToken
    };
}

/// <summary>资源管理器相关操作（C6 打开文件位置 / C9 打开配置与日志目录）。</summary>
internal static class ShellHelper
{
    public static void OpenFileLocation(string? filePath)
    {
        try
        {
            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"")
                {
                    UseShellExecute = true
                });
                return;
            }
            // 文件不存在：退化为打开所在目录
            var dir = string.IsNullOrEmpty(filePath)
                ? AppPaths.ConfigDirectory
                : Path.GetDirectoryName(filePath);
            OpenFolder(dir ?? AppPaths.ConfigDirectory);
        }
        catch (Exception ex)
        {
            MessageBox_LikeError(ex);
        }
    }

    public static void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox_LikeError(ex);
        }
    }

    private static void MessageBox_LikeError(Exception ex)
        => System.Windows.MessageBox.Show(ex.Message, "Shell",
            System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
}
