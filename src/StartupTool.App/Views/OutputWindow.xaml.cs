using System.Collections.Specialized;
using System.Windows;
using StartupTool.App.Services;
using StartupTool.App.ViewModels;

namespace StartupTool.App.Views;

/// <summary>
/// 实时输出窗口（C8）：显示与缓冲分离——数据在 <see cref="ScriptOutputBuffer"/>（5000 行环形），
/// 本窗口 200ms 批量追加；关闭只退订，不影响缓冲与服务。
/// </summary>
public partial class OutputWindow : Window
{
    private readonly OutputViewModel _vm;

    public OutputWindow(OutputViewModel vm, LocalizationService loc)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;
        Title = loc.T("Str.Output.Title", vm.ScriptName);

        _vm.Lines.CollectionChanged += OnLinesChanged;
        Closed += (_, _) => _vm.Lines.CollectionChanged -= OnLinesChanged;
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_vm.AutoScroll || OutputList.Items.Count == 0) return;
        // 批量追加后只滚一次；Dispatcher  Background 保证容器已生成
        Dispatcher.BeginInvoke(new Action(() =>
            OutputList.ScrollIntoView(OutputList.Items[OutputList.Items.Count - 1])),
            System.Windows.Threading.DispatcherPriority.Background);
    }
}
