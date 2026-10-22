using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using StartupTool.App.ViewModels;
using StartupTool.Core.Models;

namespace StartupTool.App.Converters;

/// <summary>崩溃策略枚举 → 本地化文案。</summary>
public sealed class CrashPolicyConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value switch
        {
            CrashPolicy.Always => "Str.Crash.Always",
            CrashPolicy.OnFailure => "Str.Crash.OnFailure",
            CrashPolicy.Never => "Str.Crash.Never",
            _ => null
        };
        return key is null ? value?.ToString() ?? "" : (Application.Current.TryFindResource(key) ?? key);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>设置页语言单选：当前值与 ConverterParameter 相等时选中。</summary>
public sealed class StringEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.Equals(value as string, parameter as string, StringComparison.Ordinal);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? parameter ?? Binding.DoNothing : Binding.DoNothing;
}

/// <summary>bool 取反 → Visibility（true 折叠）。</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Collapsed;
}

/// <summary>底部信息条 InfoKind → 前景画刷。</summary>
public sealed class InfoKindToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value switch
        {
            InfoKind.Error => "DangerBrush",
            InfoKind.Warning => "WarningBrush",
            InfoKind.Success => "SuccessBrush",
            _ => "AccentBrush"
        };
        return Application.Current.TryFindResource(key) ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>底部信息条 InfoKind → 浅底色。</summary>
public sealed class InfoKindToBackgroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value switch
        {
            InfoKind.Error => "DangerSubtleBrush",
            InfoKind.Warning => "WarningBackgroundBrush",
            InfoKind.Success => "SuccessBackgroundBrush",
            _ => "InfoBackgroundBrush"
        };
        return Application.Current.TryFindResource(key) ?? Brushes.AliceBlue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>字符串非空 → Visible（空状态提示等）。</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
