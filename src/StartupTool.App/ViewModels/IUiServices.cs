using StartupTool.Core.Models;
using StartupTool.Core.Templates;

namespace StartupTool.App.ViewModels;

/// <summary>
/// 视图服务抽象：ViewModel 通过它弹窗/开窗，不直接引用 Window（保持可测试、可换实现）。
/// 所有方法都在 UI 线程调用。
/// </summary>
public interface IUiServices
{
    /// <summary>
    /// 打开添加/编辑对话框（C1/C3）。source 为待编辑配置（添加时为带默认值的新对象）；
    /// <paramref name="allScripts"/> 用于 D4 的"依赖脚本"候选（自动排除自身）。
    /// 返回用户保存后的配置；取消返回 null。
    /// </summary>
    ScriptConfig? ShowEditDialog(ScriptConfig source, bool isNew, IReadOnlyList<ScriptConfig> allScripts);

    /// <summary>
    /// 打开内置模板选择窗口（D9）：用户选中一个模板后返回它，用于预填「从模板新建」；
    /// 取消返回 null。
    /// </summary>
    ScriptTemplate? PickTemplate();

    /// <summary>打开设置对话框（语言/气泡开关）。</summary>
    void ShowSettings();

    /// <summary>打开某脚本的实时输出窗口（C8）。</summary>
    void ShowOutput(ScriptRowViewModel row);

    /// <summary>打开外部同名进程窗口（A8）。</summary>
    void ShowExternalProcesses(ScriptRowViewModel row);

    /// <summary>打开心跳命令窗口（H2）。</summary>
    void ShowHeartbeat(ScriptRowViewModel row);

    /// <summary>模态确认；danger=true 时用危险色主按钮。</summary>
    bool Confirm(string title, string message, string? extraWarning = null, bool danger = false);

    /// <summary>保存后询问是否立即重启以应用启动属性改动。true=立即重启。</summary>
    bool AskRestartNow(string scriptName);

    /// <summary>
    /// 带勾选项的模态确认（D7）：返回 (是否勾选, 是否确认)。勾选项用于导出时选择是否包含令牌、
    /// 导入时选择是否连全局设置一起替换。
    /// </summary>
    (bool Checked, bool Confirmed) ConfirmWithOption(
        string title, string message, string checkBoxText, string? extraWarning = null, bool danger = false);

    /// <summary>保存文件对话框（D7）。返回完整路径；取消返回 null。</summary>
    string? AskSaveFile(string suggestedFileName, string filter);

    /// <summary>打开文件对话框（D7）。返回完整路径；取消返回 null。</summary>
    string? AskOpenFile(string filter);

    /// <summary>在资源管理器中定位文件（D8，导出诊断包后自动打开所在目录）。</summary>
    void RevealInExplorer(string path);

    /// <summary>底部信息条轻提示（由 VM 另设 InfoBar，此方法用于弹窗式错误）。</summary>
    void ShowMessage(string title, string message, bool error = false);
}
