using System.Diagnostics;
using System.Windows;
using StartupTool.App.Services;
using StartupTool.App.ViewModels;
using StartupTool.Core.Models;
using StartupTool.Core.Templates;

namespace StartupTool.App.Views;

/// <summary>
/// IUiServices 的 WPF 实现：所有对话框/开窗集中在此，ViewModel 不引用具体窗口。
/// 非模态窗（输出/外部进程/心跳）以主窗口为 Owner，关闭时清理缓冲引用计数。
/// </summary>
internal sealed class WpfUiServices : IUiServices
{
    private readonly AppClientService _client;
    private readonly LocalizationService _loc;
    private readonly OutputBufferService _outputs;
    private readonly Func<Window> _owner;
    private readonly Action _onLanguageApplied;

    public WpfUiServices(
        AppClientService client,
        LocalizationService loc,
        OutputBufferService outputs,
        Func<Window> owner,
        Action onLanguageApplied)
    {
        _client = client;
        _loc = loc;
        _outputs = outputs;
        _owner = owner;
        _onLanguageApplied = onLanguageApplied;
    }

    /// <summary>
    /// 模态框的 owner（N4）：只有主窗口当前可见时才作 Owner。
    /// owner 隐藏时 ShowDialog 的子窗不可见且不在任务栏，会表现为"窗口消失、进程卡死"
    /// （托盘"退出控制端"路径正是从隐藏状态弹出的确认框），此时改用无 owner 居中弹出。
    /// </summary>
    private Window? OwnerForDialog()
    {
        var window = _owner();
        return window is { IsVisible: true } ? window : null;
    }

    public ScriptConfig? ShowEditDialog(ScriptConfig source, bool isNew, IReadOnlyList<ScriptConfig> allScripts)
    {
        var vm = new ScriptEditViewModel(source, isNew, _loc, allScripts);
        var win = new ScriptEditWindow(vm, _loc);
        if (OwnerForDialog() is { } owner) win.Owner = owner;
        return win.ShowDialog() == true ? vm.BuildConfig() : null;
    }

    public ScriptTemplate? PickTemplate()
        => TemplatePickerWindow.Show(OwnerForDialog(), _loc);

    public void ShowSettings()
    {
        var vm = new SettingsViewModel(_client, _loc, _onLanguageApplied);
        var win = new SettingsWindow(vm);
        if (OwnerForDialog() is { } owner) win.Owner = owner;

        // 打开前拉取最新共享设置（语言/气泡开关可能被他处修改）；模态窗仍跑消息泵，await 续体可执行
        _ = LoadAsync();
        win.ShowDialog();
        return;

        async Task LoadAsync()
        {
            try
            {
                var cfg = await _client.GetConfigAsync();
                vm.Load(cfg);
            }
            catch
            {
                /* 拉取失败时窗内仍可操作，保存时错误会显示在窗内 */
            }
        }
    }

    public void ShowOutput(ScriptRowViewModel row)
    {
        var buffer = _outputs.GetOrCreate(row.Id);

        // N6：先建 VM（此时订阅本窗）再通知 WindowOpened，订阅计数才准确、不会漏掉新行
        var vm = new OutputViewModel(buffer, row.Name);
        _outputs.WindowOpened(buffer);

        var win = new OutputWindow(vm, _loc) { Owner = _owner() };
        win.Closed += (_, _) =>
        {
            // N6：先退订（计数归零）再判定是否退订上游并释放缓冲
            vm.Dispose();
            _outputs.WindowClosed(buffer);
        };
        win.Show();
    }

    public void ShowExternalProcesses(ScriptRowViewModel row)
    {
        var vm = new ExternalProcessesViewModel(_client, _loc, row.Id, row.Name);
        var win = new ExternalProcessesWindow(vm, _loc) { Owner = _owner() };
        win.Show();
    }

    public void ShowHeartbeat(ScriptRowViewModel row)
    {
        var vm = new HeartbeatViewModel(_client, _loc, row.Id, row.Name);
        var win = new HeartbeatWindow(vm, _loc) { Owner = _owner() };
        win.Show();
    }

    public bool Confirm(string title, string message, string? extraWarning = null, bool danger = false)
        => ConfirmWindow.ShowDialog(
            OwnerForDialog(), title, message, extraWarning,
            danger: danger,
            confirmText: _loc.T("Str.Common.Confirm"),
            cancelText: _loc.T("Str.Common.Cancel")).Confirmed;

    public bool AskRestartNow(string scriptName)
        => ConfirmWindow.ShowDialog(
            OwnerForDialog(),
            _loc.T("Str.Edit.RestartAsk.Title"),
            _loc.T("Str.Edit.RestartAsk.Message", scriptName),
            null,
            confirmText: _loc.T("Str.Edit.RestartNow"),
            cancelText: _loc.T("Str.Edit.RestartLater")).Confirmed;

    public (bool Checked, bool Confirmed) ConfirmWithOption(
        string title, string message, string checkBoxText, string? extraWarning = null, bool danger = false)
        => ConfirmWindow.ShowDialog(
            OwnerForDialog(), title, message, extraWarning,
            danger: danger,
            confirmText: _loc.T("Str.Common.Confirm"),
            cancelText: _loc.T("Str.Common.Cancel"),
            checkBoxText: checkBoxText);

    public string? AskSaveFile(string suggestedFileName, string filter)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = suggestedFileName,
            Filter = filter,
            DefaultExt = ".json",
            AddExtension = true,
            OverwritePrompt = true
        };
        return dlg.ShowDialog(OwnerForDialog()) == true ? dlg.FileName : null;
    }

    public string? AskOpenFile(string filter)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = filter,
            CheckFileExists = true,
            Multiselect = false
        };
        return dlg.ShowDialog(OwnerForDialog()) == true ? dlg.FileName : null;
    }

    public void RevealInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            {
                UseShellExecute = true
            });
        }
        catch
        {
            // 打开资源管理器失败不影响导出结果提示
        }
    }

    public void ShowMessage(string title, string message, bool error = false)
        => ConfirmWindow.ShowDialog(
            OwnerForDialog(), title, message, null,
            danger: error,
            confirmText: _loc.T("Str.Common.OK"),
            cancelText: null);
}
