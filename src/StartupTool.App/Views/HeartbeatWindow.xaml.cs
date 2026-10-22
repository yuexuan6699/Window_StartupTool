using System.Windows;
using StartupTool.App.Services;
using StartupTool.App.ViewModels;

namespace StartupTool.App.Views;

/// <summary>心跳命令窗（H2）：展示/复制命令，重置需二次确认。</summary>
public partial class HeartbeatWindow : Window
{
    private readonly HeartbeatViewModel _vm;
    private readonly LocalizationService _loc;

    public HeartbeatWindow(HeartbeatViewModel vm, LocalizationService loc)
    {
        _vm = vm;
        _loc = loc;
        InitializeComponent();
        DataContext = vm;
        Title = loc.T("Str.Heartbeat.Title", vm.ScriptName);
        Loaded += async (_, _) => await _vm.LoadAsync();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_vm.CommandSample)) return;
        Clipboard.SetText(_vm.CommandSample);
        Title = _loc.T("Str.Common.Copied");
    }

    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        var (_, confirmed) = ConfirmWindow.ShowDialog(
            this,
            _loc.T("Str.Heartbeat.ResetConfirmTitle"),
            _loc.T("Str.Heartbeat.ResetConfirmMessage"),
            null,
            danger: true,
            confirmText: _loc.T("Str.Heartbeat.Reset"),
            cancelText: _loc.T("Str.Common.Cancel"));
        if (confirmed) await _vm.ResetCommand.ExecuteAsync(null);
    }
}
