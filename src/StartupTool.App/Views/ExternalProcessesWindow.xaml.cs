using System.Windows;
using System.Windows.Controls;
using StartupTool.App.Services;
using StartupTool.App.ViewModels;

namespace StartupTool.App.Views;

/// <summary>外部同名进程窗（A8）：仅列出；终止必须显式确认，由服务端执行。</summary>
public partial class ExternalProcessesWindow : Window
{
    private readonly ExternalProcessesViewModel _vm;
    private readonly LocalizationService _loc;

    public ExternalProcessesWindow(ExternalProcessesViewModel vm, LocalizationService loc)
    {
        _vm = vm;
        _loc = loc;
        InitializeComponent();
        DataContext = vm;
        Title = loc.T("Str.External.Title", vm.ScriptName);
        Loaded += async (_, _) => await _vm.RefreshCommand.ExecuteAsync(null);
    }

    private async void Kill_Click(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is not ExternalProcessRowViewModel row) return;

        var (_, confirmed) = ConfirmWindow.ShowDialog(
            this,
            _loc.T("Str.External.KillConfirmTitle"),
            _loc.T("Str.External.KillConfirmMessage", row.Pid, row.ProcessName),
            null,
            danger: true,
            confirmText: _loc.T("Str.Common.Yes"),
            cancelText: _loc.T("Str.Common.No"));
        if (confirmed) await _vm.KillCommand.ExecuteAsync(row);
    }
}
