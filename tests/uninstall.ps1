﻿﻿# ======================================================================
# StartupTool 卸载脚本
# 用法：在 PowerShell（管理员）中执行  .\tests\uninstall.ps1（在仓库根目录下）
# 只停止/删除服务与登录自启项，不删除程序文件与 config\scripts.json 配置。
# 每一步校验退出码。
# ======================================================================

$ErrorActionPreference = "Stop"
# 本脚本位于 tests\，仓库根目录为其上一级
$repoRoot   = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$exe        = Join-Path $repoRoot "publish\StartupTool.exe"

# ---------- 0. 管理员检查 ----------
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "请以管理员身份重新运行本脚本（右键 PowerShell -> 以管理员身份运行）。" -ForegroundColor Red
    exit 5
}

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  StartupTool 卸载" -ForegroundColor Yellow
Write-Host "============================================" -ForegroundColor Cyan

# ---------- 1. 优先用 exe 内置卸载逻辑（含 Run 键清理与退出码反馈）----------
if (Test-Path $exe) {
    & $exe --uninstall
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[失败] 内置卸载返回退出码 $LASTEXITCODE" -ForegroundColor Red
        exit $LASTEXITCODE
    }
}
else {
    # 发布产物已不在：兜底直接用 sc.exe
    Write-Host "未找到 $exe，改用 sc.exe 直接删除服务..." -ForegroundColor Yellow
    sc.exe stop StartupTool | Out-Null
    sc.exe delete StartupTool
    if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 1060 -and $LASTEXITCODE -ne 1072) {
        Write-Host "[失败] sc.exe delete 返回退出码 $LASTEXITCODE" -ForegroundColor Red
        exit $LASTEXITCODE
    }

    try {
        $runKey = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run"
        Remove-ItemProperty -Path $runKey -Name "StartupTool" -ErrorAction Stop
    }
    catch {
        Write-Host "提示：登录自启项不存在或已移除。" -ForegroundColor Gray
    }
}

Write-Host "`n卸载完成（程序文件与配置保留，可手动删除 publish 与 config 目录）。" -ForegroundColor Green
