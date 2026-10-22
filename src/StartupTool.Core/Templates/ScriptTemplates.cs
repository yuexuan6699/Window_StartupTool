using System;
using StartupTool.Core.Models;

namespace StartupTool.Core.Templates;

/// <summary>
/// 内置脚本模板集合（D9）：纯内存内置列表，**绝不写盘**。
/// 每份模板只做两件事——预填编辑窗口字段（用户仍可改），以及提供一段可参考/可复制的脚本文本
/// （真正的脚本文件由用户保存后，把路径填回编辑窗）。
/// 心跳示例与《开发文档.md §5.3》《项目文档.md §5.3》同源，改动须保持一致。
/// </summary>
public static class ScriptTemplates
{
    /// <summary>内置模板集合（自建只读视图；调用方不要修改集合本身）。</summary>
    public static IReadOnlyList<ScriptTemplate> All { get; } = new List<ScriptTemplate>
    {
        // ---------- ① 心跳示例（bat / PowerShell / Python，与 H8 同源） ----------
        new ScriptTemplate
        {
            Id = "HeartbeatBat",
            NameKey = "Str.Template.HeartbeatBat.Name",
            DescriptionKey = "Str.Template.HeartbeatBat.Desc",
            HeartbeatEnabled = true,
            HeartbeatIntervalSeconds = 30,
            HeartbeatTimeoutSeconds = 90,
            ScriptText = """
@echo off
setlocal
rem ============================================================
rem  StartupTool 脚本心跳发送示例（批处理）
rem  把 BEAT_PATH 与 TOKEN 换成你的实际值：
rem   - BEAT_PATH：StartupTool.exe 的完整路径
rem   - TOKEN：控制端「心跳」窗口复制的 64 位十六进制令牌
rem  每 30 秒发送一次心跳；退出码非 0 即打印告警并停止发送。
rem ============================================================
set "BEAT_PATH=C:\path\to\StartupTool.exe"
set "TOKEN=REPLACE_WITH_YOUR_64_HEX_TOKEN"
set "INTERVAL_SECONDS=30"

:loop
    rem 取 Unix 秒时间戳（用 PowerShell 计算，兼容 Win10+）
    for /f %%t in ('powershell -NoProfile -Command "[DateTimeOffset]::UtcNow.ToUnixTimeSeconds()"') do set "TS=%%t"

    "%BEAT_PATH%" --beat "%TOKEN%" "%TS%"
    if errorlevel 1 (
        echo [心跳] 发送失败，退出码 %errorlevel%，停止发送。
        goto :eof
    )

    rem 用 ping 休眠，避免 timeout 在无交互会话下报错
    ping -n %INTERVAL_SECONDS% 127.0.0.1 >nul 2>&1
goto loop
"""
        },
        new ScriptTemplate
        {
            Id = "HeartbeatPowerShell",
            NameKey = "Str.Template.HeartbeatPowerShell.Name",
            DescriptionKey = "Str.Template.HeartbeatPowerShell.Desc",
            HeartbeatEnabled = true,
            HeartbeatIntervalSeconds = 30,
            HeartbeatTimeoutSeconds = 90,
            ScriptText = """
# ============================================================
#  StartupTool 脚本心跳发送示例（PowerShell）
#  把 $BeatPath 与 $Token 换成你的实际值：
#   - $BeatPath：StartupTool.exe 的完整路径
#   - $Token：控制端「心跳」窗口复制的 64 位十六进制令牌
#  每 30 秒发送一次心跳；退出码非 0 即打印告警并退出。
# ============================================================
$BeatPath = "C:\path\to\StartupTool.exe"
$Token    = "REPLACE_WITH_YOUR_64_HEX_TOKEN"
$Interval = 30

while ($true) {
    $ts = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    & $BeatPath --beat $Token $ts
    $code = $LASTEXITCODE
    if ($code -ne 0) {
        Write-Host "[心跳] 发送失败，退出码: $code，停止发送。" -ForegroundColor Yellow
        break
    }
    Start-Sleep -Seconds $Interval
}
"""
        },
        new ScriptTemplate
        {
            Id = "HeartbeatPython",
            NameKey = "Str.Template.HeartbeatPython.Name",
            DescriptionKey = "Str.Template.HeartbeatPython.Desc",
            HeartbeatEnabled = true,
            HeartbeatIntervalSeconds = 30,
            HeartbeatTimeoutSeconds = 90,
            ScriptText = """
# ============================================================
#  StartupTool 脚本心跳发送示例（Python）
#  把 BEAT_PATH 与 TOKEN 换成你的实际值：
#   - BEAT_PATH：StartupTool.exe 的完整路径
#   - TOKEN：控制端「心跳」窗口复制的 64 位十六进制令牌
#  每 30 秒发送一次心跳；退出码非 0 即打印告警并退出。
# ============================================================
import subprocess
import time

BEAT_PATH = r"C:\path\to\StartupTool.exe"
TOKEN = "REPLACE_WITH_YOUR_64_HEX_TOKEN"
INTERVAL = 30

while True:
    ts = int(time.time())
    proc = subprocess.run(
        [BEAT_PATH, "--beat", TOKEN, str(ts)],
        capture_output=True,
        text=True,
    )
    if proc.returncode != 0:
        print(f"[心跳] 发送失败，退出码: {proc.returncode}，停止发送。")
        break
    time.sleep(INTERVAL)
"""
        },

        // ---------- ② 映射网络盘 ----------
        new ScriptTemplate
        {
            Id = "MapNetworkDrive",
            NameKey = "Str.Template.MapNetworkDrive.Name",
            DescriptionKey = "Str.Template.MapNetworkDrive.Desc",
            WaitForNetwork = true,
            ScriptText = """
@echo off
setlocal
rem ============================================================
rem  StartupTool 示例：映射网络驱动器
rem  把 SHARE / DRIVE / USERNAME 换成你的实际值（密码按需填）。
rem  本模板已启用「等待网络」；否则开机时网络未就绪会失败。
rem ============================================================
set "SHARE=\\server\share"
set "DRIVE=X:"
set "USERNAME=DOMAIN\user"
rem 若需开机自动重挂，把 /persistent:no 改为 /persistent:yes
net use %DRIVE% %SHARE% /user:%USERNAME% /persistent:no
if errorlevel 1 (
    echo [映射] 失败：无法连接 %SHARE%，请检查网络与凭据。
    exit /b 1
)
echo [映射] %DRIVE% 已挂载到 %SHARE%。
exit /b 0
"""
        },

        // ---------- ③ 清理临时文件 ----------
        new ScriptTemplate
        {
            Id = "CleanTempFiles",
            NameKey = "Str.Template.CleanTempFiles.Name",
            DescriptionKey = "Str.Template.CleanTempFiles.Desc",
            ScriptText = """
@echo off
setlocal
rem ============================================================
rem  StartupTool 示例：清理临时文件（%TEMP% 与系统临时目录，超过 N 天）。
rem  删除前建议先运行一次并观察输出，确认无误再保留。
rem ============================================================
set "DAYS_OLD=7"
set "TARGETS=%TEMP%;C:\Windows\Temp"
for %%D in (%TARGETS%) do (
    if exist "%%D" (
        echo [清理] 正在清理 "%%D" 中 %DAYS_OLD% 天前的文件…
        forfiles /p "%%D" /d -%DAYS_OLD% /c "cmd /c del /f /q @path" 2>nul
        forfiles /p "%%D" /d -%DAYS_OLD% /c "cmd /c rd /s /q @path" 2>nul
    )
)
echo [清理] 完成。
exit /b 0
"""
        },

        // ---------- ④ 启动/停止 Windows 服务 ----------
        new ScriptTemplate
        {
            Id = "WindowsService",
            NameKey = "Str.Template.WindowsService.Name",
            DescriptionKey = "Str.Template.WindowsService.Desc",
            RequireElevation = true,
            ScriptText = """
@echo off
setlocal
rem ============================================================
rem  StartupTool 示例：启动一个 Windows 服务（需管理员权限）。
rem  本模板已勾选「提权运行」；把 SERVICE_NAME 换成你的实际服务名。
rem  若要改为停止服务，把下方 net start 换成 net stop 即可。
rem ============================================================
set "SERVICE_NAME=MyService"
echo [服务] 正在启动 %SERVICE_NAME% …
sc config %SERVICE_NAME% start= auto
net start %SERVICE_NAME%
if errorlevel 1 (
    echo [服务] 启动 %SERVICE_NAME% 失败，请检查服务名与权限。
    exit /b 1
)
echo [服务] %SERVICE_NAME% 已启动。
exit /b 0
"""
        },

        // ---------- ⑤ 等待网络后启动指定程序 ----------
        new ScriptTemplate
        {
            Id = "WaitNetworkStart",
            NameKey = "Str.Template.WaitNetworkStart.Name",
            DescriptionKey = "Str.Template.WaitNetworkStart.Desc",
            WaitForNetwork = true,
            ScriptText = """
@echo off
setlocal
rem ============================================================
rem  StartupTool 示例：等待网络可用后启动指定程序。
rem  本模板已启用「等待网络」；把 APP_PATH / APP_ARGS 换成你的实际值。
rem ============================================================
set "APP_PATH=C:\path\to\app.exe"
set "APP_ARGS="
if not exist "%APP_PATH%" (
    echo [启动] 找不到程序：%APP_PATH%
    exit /b 1
)
start "" "%APP_PATH%" %APP_ARGS%
echo [启动] 已启动 %APP_PATH%。
exit /b 0
"""
        }
    };

    /// <summary>按 Id 查找模板；找不到返回 null。</summary>
    public static ScriptTemplate? Find(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        foreach (var template in All)
            if (string.Equals(template.Id, id, StringComparison.Ordinal))
                return template;
        return null;
    }
}
