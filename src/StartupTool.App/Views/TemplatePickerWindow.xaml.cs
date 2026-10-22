using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using StartupTool.App.Services;
using StartupTool.Core.Templates;

namespace StartupTool.App.Views;

/// <summary>
/// 模板选择窗口（D9）：列出内置脚本模板，展示选中模板的脚本文本（只读、可复制）。
/// 用户选中后「确定」返回对应模板；取消返回 null。调用方随后用模板预填字段走「新建」分支。
/// </summary>
public partial class TemplatePickerWindow : Window
{
    private readonly List<TemplateItem> _items;
    private readonly LocalizationService _loc;
    private ScriptTemplate? _selected;

    private TemplatePickerWindow(LocalizationService loc)
    {
        InitializeComponent();
        _loc = loc;
        _items = ScriptTemplates.All
            .Select(t => new TemplateItem(t, loc.T(t.NameKey), loc.T(t.DescriptionKey)))
            .ToList();

        TemplateList.ItemsSource = _items;
        if (_items.Count > 0) TemplateList.SelectedIndex = 0;
        PreviewScript();
    }

    /// <summary>以 owner 为父弹窗；owner 可为 null（主窗隐藏时不作 owner）。确认返回选中模板，取消返回 null。</summary>
    public static ScriptTemplate? Show(Window? owner, LocalizationService loc)
    {
        var dlg = new TemplatePickerWindow(loc);
        if (owner is not null) dlg.Owner = owner;
        return dlg.ShowDialog() == true ? dlg._selected : null;
    }

    private ScriptTemplate? SelectedTemplate =>
        (TemplateList.SelectedItem as TemplateItem)?.Template;

    private void PreviewScript()
    {
        ScriptBox.Text = SelectedTemplate?.ScriptText ?? string.Empty;
    }

    private void TemplateList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selected = SelectedTemplate;
        PreviewScript();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        // 与 HeartbeatWindow 一致：仅复制脚本文本本身，不做额外清理
        try { Clipboard.SetText(ScriptBox.Text); } catch { /* 剪贴板被占用时忽略 */ }
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null)
        {
            // 理论不可能：列表默认选中第一项。仅防御未选中时的空返回。
            DialogResult = false;
            return;
        }
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    /// <summary>列表项包装：在窗口内把资源键解析为可显示文案（模板本身只存键）。</summary>
    private sealed class TemplateItem(ScriptTemplate template, string displayName, string description)
    {
        public ScriptTemplate Template { get; } = template;
        public string DisplayName { get; } = displayName;
        public string Description { get; } = description;
    }
}
