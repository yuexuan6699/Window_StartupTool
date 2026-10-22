using System.IO;
using System.Windows;
using Microsoft.Win32;
using StartupTool.App.Services;
using StartupTool.App.ViewModels;

namespace StartupTool.App.Views;

/// <summary>添加/编辑脚本对话框（C1/C3）。返回 DialogResult=true 时从 VM 取 BuildConfig()。</summary>
public partial class ScriptEditWindow : Window
{
    private readonly ScriptEditViewModel _vm;
    private readonly LocalizationService _loc;

    public ScriptEditWindow(ScriptEditViewModel vm, LocalizationService loc)
    {
        _vm = vm;
        _loc = loc;
        InitializeComponent();
        DataContext = vm;
        Title = vm.IsNew
            ? loc.T("Str.Edit.TitleAdd")
            : loc.T("Str.Edit.TitleEdit", vm.Name);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _vm.Validate();
        if (_vm.HasErrors) return;
        DialogResult = true;
        Close();
    }

    private void BrowseFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "程序/脚本 (*.exe;*.bat;*.cmd;*.ps1;*.py;*.js)|*.exe;*.bat;*.cmd;*.ps1;*.py;*.js|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };
        TrySetInitialLocation(dlg, _vm.Path);
        if (dlg.ShowDialog(this) == true) _vm.Path = dlg.FileName;
    }

    /// <summary>D10：选择虚拟环境的 python.exe。虚拟环境目录名任意，因此不猜路径，直接让用户选文件。</summary>
    private void BrowsePython_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = _loc.T("Str.Edit.BrowsePythonTitle"),
            Filter = "Python 解释器 (python.exe)|python.exe|程序 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };
        TrySetInitialLocation(dlg, _vm.PythonExecutable);
        if (dlg.ShowDialog(this) == true) _vm.PythonExecutable = dlg.FileName;
    }

    private void BrowseDir_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog();
        if (Directory.Exists(_vm.WorkingDirectory))
            dlg.InitialDirectory = _vm.WorkingDirectory;
        if (dlg.ShowDialog(this) == true) _vm.WorkingDirectory = dlg.FolderName;
    }

    private static void TrySetInitialLocation(FileDialog dlg, string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            if (File.Exists(path))
            {
                dlg.InitialDirectory = Path.GetDirectoryName(path);
                dlg.FileName = Path.GetFileName(path);
            }
            else if (Directory.Exists(path))
            {
                dlg.InitialDirectory = path;
            }
        }
        catch { /* 路径非法时忽略，用对话框默认目录 */ }
    }
}
