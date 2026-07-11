namespace StartupCommon;

public enum Language
{
    Chinese,
    English
}

public static class LocalizationManager
{
    private static Language _currentLanguage = Language.Chinese;

    private static readonly Dictionary<string, string> ChineseStrings = new()
    {
        { "AppTitle", "开机自运行工具" },
        { "ServiceStatus", "Windows 服务状态" },
        { "EnableAutoStart", "启用开机自启" },
        { "DisableAutoStart", "禁用开机自启" },
        { "InstallService", "安装并启用服务" },
        { "RefreshStatus", "刷新状态" },
        { "ServiceUnknown", "服务: 未知" },
        { "ServiceRunning", "服务: 运行中" },
        { "ServiceStopped", "服务: 已停止" },
        { "ServiceNotInstalled", "服务: 未安装" },
        { "ServiceQueryFailed", "服务: 查询失败" },
        { "ScriptList", "启动脚本列表" },
        { "ScriptLogs", "脚本日志" },
        { "BatchStart", "启动所有脚本" },
        { "StopAll", "停止所有脚本" },
        { "RemoveSelected", "移除选中脚本" },
        { "AddScript", "添加脚本" },
        { "BasicInfo", "基本信息" },
        { "ResourceLimits", "资源限制" },
        { "ScriptName", "名称" },
        { "ScriptPath", "路径" },
        { "ScriptArguments", "参数" },
        { "ScriptOrder", "顺序" },
        { "MaxCpuPercent", "CPU上限(%)" },
        { "MaxMemoryMb", "内存上限(MB)" },
        { "MaxGpuPercent", "GPU上限(%)" },
        { "RestartDelaySeconds", "重启延迟(秒)" },
        { "ScriptEnabled", "启用" },
        { "ScriptStatus", "状态" },
        { "Browse", "浏览" },
        { "SelectScriptFile", "选择脚本文件" },
        { "ShowWindow", "显示管理窗口" },
        { "StartAllScripts", "启动所有脚本" },
        { "StopAllScripts", "停止所有脚本" },
        { "Exit", "退出" },
        { "Start", "单独启动" },
        { "Stop", "停止" },
        { "Enable", "启用" },
        { "Disable", "禁用" },
        { "StatusRunning", "运行中" },
        { "StatusStopped", "已停止" },
        { "StatusDisabled", "已禁用" },
        { "StatusFileNotFound", "文件不存在" },
        { "NoScriptsEnabled", "没有启用任何脚本" },
        { "ConfirmRemove", "确定要移除脚本 \"{0}\" 吗？" },
        { "ScriptExists", "脚本 \"{0}\" 已存在。" },
        { "PleaseFillNameAndPath", "请填写脚本名称和路径。" },
        { "FileNotFound", "脚本文件不存在:\n{0}" },
        { "OperationFailed", "操作失败: {0}" },
        { "ServiceInstalledStarted", "服务安装并启动成功！" },
        { "ServiceInstallFailed", "服务安装失败，请以管理员身份运行此程序。" },
        { "ServiceStoppedMsg", "服务已停止，开机自启已禁用。" },
        { "ServiceStartMsg", "服务已启动，开机自启已启用。" },
        { "NeedAdminInstall", "需要以管理员权限安装 Windows 服务。\n\n是否现在以管理员身份重启程序？" },
        { "NeedAdminForServiceOp", "此操作需要管理员权限。\n\n是否以管理员身份重启程序？" },
        { "InstallServiceTitle", "安装服务" },
        { "ProgramAlreadyRunning", "程序已在运行中，请查看系统托盘图标。" },
        { "FileNotFoundTitle", "错误" },
        { "Hint", "提示" },
        { "Error", "错误" },
        { "Success", "成功" },
        { "Confirm", "确认" },
        { "Question", "问题" },
        { "RunningProcesses", "运行中的脚本程序" },
        { "ProcessName", "进程名" },
        { "ProcessId", "PID" },
        { "Memory", "内存" },
        { "StartTime", "启动时间" },
        { "NoRunningProcesses", "没有运行中的脚本程序" },
        { "Refresh", "刷新" },
        { "Terminate", "终止" },
        { "ClearLogs", "清空日志" },
        { "ResourceMonitor", "资源监控" },
        { "ConfirmTerminate", "确定要终止进程 \"{0}\" (PID: {1}) 吗？" },
        { "Chinese", "中文" },
        { "English", "English" },
        { "ProcessNotFound", "进程已不存在" },
        { "StartSelected", "启动选中" },
        { "StopSelected", "关闭选中" },
        { "PleaseSelectScript", "请先选中一个脚本。" },
        { "ScriptAlreadyRunning", "脚本 \"{0}\" 已在运行中。" },
        { "ServicePathChanged", "检测到程序路径已变更。\n\n旧路径: {0}\n新路径: {1}\n\n是否更新服务路径以确保开机自启正常工作？" },
        { "UpdateServicePathTitle", "更新服务路径" },
        { "ServicePathUpdated", "服务路径更新成功！" },
        { "ServicePathUpdateFailed", "服务路径更新失败: {0}" }
    };

    private static readonly Dictionary<string, string> EnglishStrings = new()
    {
        { "AppTitle", "Startup Tool" },
        { "ServiceStatus", "Windows Service Status" },
        { "EnableAutoStart", "Enable Auto Start" },
        { "DisableAutoStart", "Disable Auto Start" },
        { "InstallService", "Install Service" },
        { "RefreshStatus", "Refresh Status" },
        { "ServiceUnknown", "Service: Unknown" },
        { "ServiceRunning", "Service: Running" },
        { "ServiceStopped", "Service: Stopped" },
        { "ServiceNotInstalled", "Service: Not Installed" },
        { "ServiceQueryFailed", "Service: Query Failed" },
        { "ScriptList", "Startup Scripts" },
        { "ScriptLogs", "Script Logs" },
        { "BatchStart", "Start All" },
        { "StopAll", "Stop All" },
        { "RemoveSelected", "Remove Selected" },
        { "AddScript", "Add Script" },
        { "BasicInfo", "Basic Info" },
        { "ResourceLimits", "Resource Limits" },
        { "ScriptName", "Name" },
        { "ScriptPath", "Path" },
        { "ScriptArguments", "Arguments" },
        { "ScriptOrder", "Order" },
        { "ScriptEnabled", "Enabled" },
        { "ScriptStatus", "Status" },
        { "Browse", "Browse" },
        { "MaxCpuPercent", "CPU Limit (%)" },
        { "MaxMemoryMb", "Memory Limit (MB)" },
        { "MaxGpuPercent", "GPU Limit (%)" },
        { "RestartDelaySeconds", "Restart Delay (s)" },
        { "SelectScriptFile", "Select Script File" },
        { "ShowWindow", "Show Management Window" },
        { "StartAllScripts", "Start All Scripts" },
        { "StopAllScripts", "Stop All Scripts" },
        { "Exit", "Exit" },
        { "Start", "Start" },
        { "Stop", "Stop" },
        { "Enable", "Enable" },
        { "Disable", "Disable" },
        { "StatusRunning", "Running" },
        { "StatusStopped", "Stopped" },
        { "StatusDisabled", "Disabled" },
        { "StatusFileNotFound", "File Not Found" },
        { "NoScriptsEnabled", "No scripts enabled." },
        { "ConfirmRemove", "Are you sure you want to remove script \"{0}\"?" },
        { "ScriptExists", "Script \"{0}\" already exists." },
        { "PleaseFillNameAndPath", "Please fill in script name and path." },
        { "FileNotFound", "Script file not found:\n{0}" },
        { "OperationFailed", "Operation failed: {0}" },
        { "ServiceInstalledStarted", "Service installed and started successfully!" },
        { "ServiceInstallFailed", "Service installation failed. Please run as administrator." },
        { "ServiceStoppedMsg", "Service stopped. Auto-start disabled." },
        { "ServiceStartMsg", "Service started. Auto-start enabled." },
        { "NeedAdminInstall", "Administrator privileges required to install Windows Service.\n\nRestart as administrator now?" },
        { "NeedAdminForServiceOp", "This operation requires administrator privileges.\n\nRestart as administrator now?" },
        { "InstallServiceTitle", "Install Service" },
        { "ProgramAlreadyRunning", "Program is already running. Please check the system tray icon." },
        { "FileNotFoundTitle", "Error" },
        { "Hint", "Hint" },
        { "Error", "Error" },
        { "Success", "Success" },
        { "Confirm", "Confirm" },
        { "Question", "Question" },
        { "RunningProcesses", "Running Script Processes" },
        { "ProcessName", "Process Name" },
        { "ProcessId", "PID" },
        { "Memory", "Memory" },
        { "StartTime", "Start Time" },
        { "NoRunningProcesses", "No running script processes" },
        { "Refresh", "Refresh" },
        { "Terminate", "Terminate" },
        { "ClearLogs", "Clear Logs" },
        { "ResourceMonitor", "Resource Monitor" },
        { "ConfirmTerminate", "Are you sure you want to terminate process \"{0}\" (PID: {1})?" },
        { "Chinese", "中文" },
        { "English", "English" },
        { "ProcessNotFound", "Process no longer exists" },
        { "StartSelected", "Start Selected" },
        { "StopSelected", "Stop Selected" },
        { "PleaseSelectScript", "Please select a script first." },
        { "ScriptAlreadyRunning", "Script \"{0}\" is already running." },
        { "ServicePathChanged", "Program path has changed.\n\nOld path: {0}\nNew path: {1}\n\nUpdate service path to ensure auto-start works correctly?" },
        { "UpdateServicePathTitle", "Update Service Path" },
        { "ServicePathUpdated", "Service path updated successfully!" },
        { "ServicePathUpdateFailed", "Failed to update service path: {0}" }
    };

    public static event Action? LanguageChanged;

    public static Language CurrentLanguage
    {
        get => _currentLanguage;
        set
        {
            if (_currentLanguage != value)
            {
                _currentLanguage = value;
                LanguageChanged?.Invoke();
            }
        }
    }

    public static string GetString(string key, params object[] args)
    {
        var dict = _currentLanguage == Language.Chinese ? ChineseStrings : EnglishStrings;
        if (dict.TryGetValue(key, out var value))
        {
            return args.Length > 0 ? string.Format(value, args) : value;
        }
        return key;
    }

    public static void ToggleLanguage()
    {
        CurrentLanguage = _currentLanguage == Language.Chinese ? Language.English : Language.Chinese;
    }
}