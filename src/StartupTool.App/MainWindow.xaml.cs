using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Hardcodet.Wpf.TaskbarNotification;
using StartupTool.App.Services;
using StartupTool.App.ViewModels;
using StartupTool.App.Views;

namespace StartupTool.App;

/// <summary>
/// 主窗口（M5 Fluent 重做）：纯视图 + 极薄事件处理；状态/命令全部在 <see cref="MainViewModel"/>。
/// 职责：行拖拽排序（C2）、托盘交互（C10/C11/B18）、× 最小化与一次性提示（C7）、窗口尺寸记忆。
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly LocalizationService _loc;
    private readonly IUiServices _ui;
    private readonly ClientSettings _settings;
    private readonly DispatcherTimer _tickTimer;
    private readonly bool _startMinimized;

    private Point _dragStart;
    private DataGridRow? _dragCandidate;
    private bool _allowClose;

    public MainWindow(
        MainViewModel vm,
        LocalizationService loc,
        IUiServices ui,
        ClientSettings settings,
        bool startMinimized = false)
    {
        _vm = vm;
        _loc = loc;
        _ui = ui;
        _settings = settings;
        _startMinimized = startMinimized;

        InitializeComponent();
        DataContext = vm;

        if (startMinimized)
        {
            // 登录自启：首帧不可见、不进任务栏；托盘图标建好后在 Window_Loaded 里 Hide
            Opacity = 0;
            ShowInTaskbar = false;
        }

        _tickTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tickTimer.Tick += (_, _) => _vm.Tick(DateTime.UtcNow);
        _tickTimer.Start();
    }

    // ---------- 本地化（B08）----------

    /// <summary>
    /// 语言切换后刷新表格列头（B08）：DataGridColumn 不在可视/逻辑树中，
    /// XAML 的 {DynamicResource} 在合并词典被替换后不会重新求值，必须在此显式重设。
    /// </summary>
    public void RefreshColumnHeaders()
    {
        ColName.Header = _loc.T("Str.Col.Name");
        ColState.Header = _loc.T("Str.Col.State");
        ColPid.Header = _loc.T("Str.Col.Pid");
        ColSession.Header = _loc.T("Str.Col.Session");
        ColUptime.Header = _loc.T("Str.Col.Uptime");
        ColRestarts.Header = _loc.T("Str.Col.Restarts");
        ColCpu.Header = _loc.T("Str.Col.Cpu");
        ColMemory.Header = _loc.T("Str.Col.Memory");
        ColGpu.Header = _loc.T("Str.Col.Gpu");
        ColLastBeat.Header = _loc.T("Str.Col.LastBeat");
        ColExternal.Header = _loc.T("Str.Col.External");
        ColExitCode.Header = _loc.T("Str.Col.ExitCode");
        ColLastFault.Header = _loc.T("Str.Col.LastFault");
        ColOps.Header = _loc.T("Str.Col.Ops");
    }

    // ---------- 窗口生命周期 ----------

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Width = _settings.WindowWidth;
        Height = _settings.WindowHeight;
        if (_settings.WindowLeft is { } left && _settings.WindowTop is { } top)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
        if (_settings.WindowMaximized) WindowState = WindowState.Maximized;

        // 托盘图标复用应用图标（app.ico）；失败时保留 XAML 里的矢量兜底图标
        ApplyTrayIconFromAppIcon();

        // 登录自启：等托盘图标建好（Loaded 结束后）再隐藏，避免开机时主窗口闪现
        if (_startMinimized)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(HideOnStartup));
    }

    /// <summary>把 XAML 内置的矢量托盘图标替换为应用图标 app.ico 的最大帧。</summary>
    private void ApplyTrayIconFromAppIcon()
    {
        try
        {
            var info = Application.GetResourceStream(new Uri("pack://application:,,,/Resources/app.ico"));
            if (info is null) return;
            using var stream = info.Stream;
            var decoder = new IconBitmapDecoder(
                stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames.OrderByDescending(f => f.PixelWidth).FirstOrDefault();
            if (frame is not null) Tray.IconSource = frame;
        }
        catch
        {
            // 读不到应用图标：XAML 兜底矢量图标已生效，静默即可
        }
    }

    /// <summary>登录自启后立即隐藏到托盘：还原透明度/任务栏标志，供之后 RestoreFromTray 正常显示。</summary>
    private void HideOnStartup()
    {
        Hide();
        Opacity = 1;
        ShowInTaskbar = true;
    }

    /// <summary>× = 最小化到托盘（决策 3）；只有托盘菜单"退出"才真正关闭。</summary>
    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        SavePlacement();
        if (_allowClose) return;

        e.Cancel = true;

        // 注意：必须先弹提示、再 Hide()。owner 隐藏后 ShowDialog 的子窗口不可见且不在任务栏，
        // 会造成“窗口消失、进程卡死在不可见模态框”的假死。
        if (!_settings.HideToTrayHintDismissed)
        {
            var (dontShow, _) = ConfirmWindow.ShowDialog(
                this,
                _loc.T("Str.Tray.HideTitle"),
                _loc.T("Str.Tray.HideMessage"),
                null,
                confirmText: _loc.T("Str.Common.OK"),
                cancelText: null,
                checkBoxText: _loc.T("Str.Tray.DontShowAgain"));
            if (dontShow)
            {
                _settings.HideToTrayHintDismissed = true;
                _settings.Save();
            }
        }

        Hide();
    }

    private void SavePlacement()
    {
        _settings.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            _settings.WindowWidth = Width;
            _settings.WindowHeight = Height;
            _settings.WindowLeft = Left;
            _settings.WindowTop = Top;
        }
        _settings.Save();
    }

    /// <summary>托盘/单例激活时从外部调用。</summary>
    public void RestoreFromTray()
    {
        // 登录自启路径可能把透明度置 0、任务栏标志置 false，显示前先恢复
        Opacity = 1;
        ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    /// <summary>
    /// 系统注销/关机时调用（N5）：放行关闭并保存窗口位置，
    /// 否则 Window_Closing 会 e.Cancel=true 阻止关机（出现"应用阻止关机"），且设置可能丢失。
    /// </summary>
    public void AllowCloseForSystemShutdown()
    {
        _allowClose = true;
        SavePlacement();
    }

    // ---------- 行拖拽排序（C2）----------

    private void ScriptsGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragCandidate = FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject);
        _dragStart = e.GetPosition(null);
    }

    private void ScriptsGrid_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragCandidate is null) return;

        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        if (_dragCandidate.DataContext is ScriptRowViewModel row
            && string.IsNullOrWhiteSpace(_vm.SearchText))
        {
            DragDrop.DoDragDrop(_dragCandidate, row, DragDropEffects.Move);
        }
        _dragCandidate = null;
    }

    private void ScriptsGrid_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(ScriptRowViewModel))
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void ScriptsGrid_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(ScriptRowViewModel)) is not ScriptRowViewModel dragged) return;
        var targetRow = FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject);
        if (targetRow?.DataContext is not ScriptRowViewModel target || ReferenceEquals(dragged, target))
            return;

        var position = e.GetPosition(targetRow);
        var after = position.Y > targetRow.ActualHeight / 2;
        await _vm.ApplyDragOrderAsync(dragged.Id, target.Id, after);
    }

    private void ScriptsGrid_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        => _dragCandidate = null;

    private void ScriptsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject)?.DataContext
            is ScriptRowViewModel row)
            _vm.EditCommand.Execute(row);
    }

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null and not System.Windows.Media.Visual) child = VisualTreeHelper.GetParent(child);
        while (child is not null)
        {
            if (child is T found) return found;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    // ---------- 右键菜单中需要开窗/确认的项 ----------

    private void HeartbeatMenu_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ScriptRowViewModel row) _ui.ShowHeartbeat(row);
    }

    private void DeleteMenu_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ScriptRowViewModel row)
            _vm.DeleteCommand.Execute(row);
    }

    // ---------- 托盘（C10/C11/B18）----------

    public void ShowTrayNotification(string title, string message)
    {
        try { Tray.ShowBalloonTip(title, message, BalloonIcon.Warning); } catch { /* 气泡失败静默 */ }
    }

    private void Tray_LeftClick(object sender, RoutedEventArgs e) => RestoreFromTray();

    private void TrayShow_Click(object sender, RoutedEventArgs e) => RestoreFromTray();

    private void TrayMenu_Opened(object sender, RoutedEventArgs e)
    {
        // 托盘菜单不在可视树，显式接上 DataContext（B18：脚本子菜单与命令都绑定 VM）
        if (TrayMenu.DataContext != _vm) TrayMenu.DataContext = _vm;
    }

    private void TrayStopAll_Click(object sender, RoutedEventArgs e)
        => _vm.StopAllCommand.Execute(null);

    private void TrayScript_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ScriptRowViewModel row) return;
        // 单击即切换：运行中→停止，未运行→启动（菜单行首的状态点标明当前状态）
        if (row.IsRunning) _vm.StopCommand.Execute(row);
        else _vm.StartCommand.Execute(row);
    }

    private void TrayExit_Click(object sender, RoutedEventArgs e)
    {
        if (!_ui.Confirm(_loc.T("Str.Tray.ExitConfirmTitle"),
                _loc.T("Str.Tray.ExitConfirmMessage")))
            return;

        _allowClose = true;
        Application.Current.Shutdown();
    }
}
