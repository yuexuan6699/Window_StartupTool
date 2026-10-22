using System.Windows;
using StartupTool.App.ViewModels;

namespace StartupTool.App.Views;

/// <summary>设置窗口：语言实时切换（不重启）+ 故障气泡开关，均写服务端 scripts.json。</summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
