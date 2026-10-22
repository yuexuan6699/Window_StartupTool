using System.Windows;

namespace StartupTool.App.Views;

/// <summary>
/// 统一模态确认/提示框（删除确认、重启询问、退出确认、托盘一次性提示）。
/// 返回 (是否勾选了复选框, 是否确认)；提示框（无取消按钮）时"确认"即 true。
/// </summary>
public partial class ConfirmWindow : Window
{
    private ConfirmWindow()
    {
        InitializeComponent();
    }

    /// <param name="owner">
    /// 模态框所有者；可为 null（N4）：调用方主窗口隐藏时不能作 owner（否则子窗不可见且不在任务栏），
    /// 此时传 null 让对话框居中弹出。
    /// </param>
    public static (bool Checked, bool Confirmed) ShowDialog(
        Window? owner,
        string title,
        string message,
        string? extraWarning,
        bool danger = false,
        string? confirmText = null,
        string? cancelText = null,
        string? checkBoxText = null)
    {
        var dlg = new ConfirmWindow
        {
            Title = title
        };
        if (owner is not null) dlg.Owner = owner;
        dlg.TitleText.Text = title;
        dlg.MessageText.Text = message;

        if (!string.IsNullOrEmpty(extraWarning))
        {
            dlg.WarningText.Text = extraWarning;
            dlg.WarningBox.Visibility = Visibility.Visible;
        }

        dlg.ConfirmButton.Content = confirmText ?? "OK";
        dlg.ConfirmButton.Style = (Style)dlg.FindResource(danger ? "DangerButton" : "AccentButton");

        if (string.IsNullOrEmpty(cancelText))
        {
            dlg.CancelButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            dlg.CancelButton.Content = cancelText;
        }

        if (!string.IsNullOrEmpty(checkBoxText))
        {
            dlg.OptionCheckBox.Content = checkBoxText;
            dlg.OptionCheckBox.Visibility = Visibility.Visible;
        }

        var confirmed = dlg.ShowDialog() == true;
        return (dlg.OptionCheckBox.IsChecked == true, confirmed);
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
