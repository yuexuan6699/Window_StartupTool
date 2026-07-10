namespace StartupCommon;

public static class AppHelper
{
    /// <summary>
    /// 获取 exe 所在目录（config/、logs/ 均基于此路径）。
    /// 使用 Environment.ProcessPath 以兼容单文件发布模式。
    /// </summary>
    public static string GetBaseDirectory()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(processPath))
        {
            return Path.GetDirectoryName(processPath)!;
        }

        return AppContext.BaseDirectory;
    }

    public static string GetBaseDirectory(string startPath)
    {
        return startPath;
    }

    public static string GetSelfExePath()
    {
        return Environment.ProcessPath ?? string.Empty;
    }

    public static bool IsFileExecutable(string path)
    {
        if (!File.Exists(path)) return false;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".exe" or ".bat" or ".cmd" or ".ps1";
    }

    /// <summary>
    /// 检查当前程序是否以管理员身份运行
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static bool IsRunningAsAdmin()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 以管理员身份重启当前程序
    /// </summary>
    /// <param name="arguments">重启后传递的命令行参数</param>
    /// <returns>是否成功启动了提权进程（用户拒绝 UAC 则返回 false）</returns>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static bool RestartAsAdmin(string arguments = "")
    {
        try
        {
            var exePath = GetSelfExePath();
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            {
                return false;
            }

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas"
            };

            System.Diagnostics.Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 获取指定 Windows 服务的可执行文件路径
    /// </summary>
    /// <param name="serviceName">服务名称</param>
    /// <returns>服务的 binPath，服务不存在则返回 null</returns>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static string? GetServicePath(string serviceName)
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                $"SELECT PathName FROM Win32_Service WHERE Name='{serviceName}'");
            foreach (var obj in searcher.Get())
            {
                return obj["PathName"]?.ToString();
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 更新 Windows 服务的可执行文件路径
    /// </summary>
    /// <param name="serviceName">服务名称</param>
    /// <param name="newBinPath">新的 binPath（包含命令行参数）</param>
    /// <returns>是否成功</returns>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static bool UpdateServicePath(string serviceName, string newBinPath)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = $"config {serviceName} binPath= \"{newBinPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            var p = System.Diagnostics.Process.Start(psi);
            p?.WaitForExit();
            return p?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 从服务的 binPath 中提取 exe 文件路径（去掉后面的命令行参数）
    /// </summary>
    public static string ExtractExePathFromBinPath(string? binPath)
    {
        if (string.IsNullOrEmpty(binPath))
            return string.Empty;

        if (binPath.StartsWith("\""))
        {
            var endIndex = binPath.IndexOf("\"", 1);
            if (endIndex > 1)
            {
                return binPath.Substring(1, endIndex - 1);
            }
        }

        var firstSpace = binPath.IndexOf(' ');
        if (firstSpace > 0)
        {
            return binPath.Substring(0, firstSpace);
        }

        return binPath;
    }
}
