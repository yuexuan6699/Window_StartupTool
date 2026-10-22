using System.Windows;
using StartupTool.App.Services;
using StartupTool.App.ViewModels;
using StartupTool.App.Views;

namespace StartupTool.App;

/// <summary>
/// WPF 控制端（只通过命名管道与服务交互，自身绝不启停脚本，P3）。
/// M5：MVVM + Fluent 浅色界面、双语实时切换、托盘常驻、配置编辑、输出/心跳/外部进程窗口。
/// 生命周期为 OnExplicitShutdown——× 只是隐藏到托盘，只有托盘"退出控制端"才结束进程。
/// </summary>
public partial class App : Application
{
    /// <summary>由 Program 注入的每会话单例（用于响应第二个实例的"显示窗口"请求）。</summary>
    public SessionSingleton? SessionSingleton { get; set; }

    /// <summary>登录自启（<c>--minimized</c>）：启动后只驻留托盘，不显示主窗口。</summary>
    public bool StartMinimized { get; init; }

    /// <summary>服务端管道连接（界面共享同一个）。</summary>
    public AppClientService Client { get; } = new();

    private MainViewModel? _vm;
    private MainWindow? _mainWindow;
    private OutputBufferService? _outputs;
    private ClientSettings? _settings;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // 1) 语言：先用中文兜底，拿到服务端配置后按共享设置应用（决策 4/7）
        var loc = new LocalizationService(this);
        loc.Apply(LocalizationService.ZhCn);

        _settings = ClientSettings.Load();

        // 2) ViewModel + 服务
        _outputs = new OutputBufferService(Client);
        var ui = new WpfUiServices(
            Client, loc, _outputs,
            owner: () => _mainWindow!,
            onLanguageApplied: () =>
            {
                // B08：VM 刷新行内/状态栏文案；列头不在可视树，需由窗口显式重设
                _vm?.RefreshLocalization();
                _mainWindow?.RefreshColumnHeaders();
            });
        _vm = new MainViewModel(Client, loc, ui);

        // 3) 主窗口
        _mainWindow = new MainWindow(_vm, loc, ui, _settings, StartMinimized);
        MainWindow = _mainWindow;
        _mainWindow.Show();

        // 4) 管道事件（后台线程 → Dispatcher 封送 UI 线程）
        Client.ConnectionChanged += connected => Dispatcher.Invoke(() => _vm.SetConnected(connected));
        Client.StatesUpdated += states => Dispatcher.Invoke(() =>
        {
            _vm.MergeStates(states);
            _vm.MoveUpCommand.NotifyCanExecuteChanged();
            _vm.MoveDownCommand.NotifyCanExecuteChanged();
        });
        Client.NotifyReceived += notify => Dispatcher.Invoke(() =>
        {
            _vm.OnNotify(notify);
            // C11：仅故障类弹托盘气泡；普通信息只进底部信息条（服务端已按 NotifyOnFault 过滤）
            if (notify.Kind == "fault")
                _mainWindow.ShowTrayNotification(notify.ScriptName ?? loc.T("Str.App.Title"), notify.Message);
        });
        Client.OutputReceived += payload => _outputs.OnOutput(payload);
        Client.ConfigReceived += cfg => Dispatcher.Invoke(() =>
        {
            _vm.SetConfig(cfg);
            loc.ApplyFromSetting(cfg.Settings.Language);
            _vm.RefreshLocalization();
        });

        Client.Start();

        // 5) 第二实例激活
        if (SessionSingleton is not null)
        {
            SessionSingleton.ShowRequested += () => Dispatcher.Invoke(() => _mainWindow?.RestoreFromTray());
        }

        // 6) N5：注销/关机时放行关闭并保存设置，避免"应用阻止关机"与设置丢失
        SessionEnding += (_, _) => _mainWindow?.AllowCloseForSystemShutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _settings?.Save();
        _outputs?.Dispose();
        Client.Dispose();
        base.OnExit(e);
    }
}
