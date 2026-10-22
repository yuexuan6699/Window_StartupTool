﻿﻿﻿﻿# ======================================================================
# StartupTool 安装脚本
# 用法：在 PowerShell（管理员）中执行  .\tests\install.ps1（在仓库根目录下）
#
# 发布参数：Release / win-x64 / 自包含(self-contained) / 单文件 / 内嵌运行时
#   => 目标机器无需预装 .NET 10
# 每一步都校验退出码，任一步失败立即终止，不带错继续。
# 重新发布前会备份 publish\config（运行期配置），发布后原样还原。
# ======================================================================

$ErrorActionPreference = "Stop"
# 本脚本位于 tests\，仓库根目录为其上一级
$repoRoot   = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$project    = Join-Path $repoRoot "src\StartupTool.App\StartupTool.App.csproj"
$publishDir = Join-Path $repoRoot "publish"
$exe        = Join-Path $publishDir "StartupTool.exe"
$configDir  = Join-Path $publishDir "config"

function Fail($message, $code) {
    Write-Host ""
    Write-Host "[失败] $message（退出码 $code）" -ForegroundColor Red
    exit $code
}

# ---------- 0. 管理员检查 ----------
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "请以管理员身份重新运行本脚本（右键 PowerShell -> 以管理员身份运行）。" -ForegroundColor Red
    exit 5
}

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  StartupTool 安装" -ForegroundColor Green
Write-Host "============================================" -ForegroundColor Cyan

# ---------- 1. 编译发布（退出码必须为 0）----------
Write-Host "`n[1/2] 编译发布（Release / win-x64 / 自包含单文件）..." -ForegroundColor Yellow

# 重新发布前先备份运行期配置，避免连已添加的脚本一起删掉
$configBackup = $null
if (Test-Path $configDir) {
    $configBackup = Join-Path ([System.IO.Path]::GetTempPath()) ("StartupTool-config-" + [Guid]::NewGuid().ToString("N"))
    Copy-Item -Path $configDir -Destination $configBackup -Recurse -Force
    Write-Host "已备份现有配置到：$configBackup" -ForegroundColor Gray
}

if (Test-Path $publishDir) {
    Remove-Item -Recurse -Force $publishDir
}

dotnet publish $project -c Release -r win-x64 --self-contained true -o $publishDir `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true
if ($LASTEXITCODE -ne 0) { Fail "dotnet publish 失败" $LASTEXITCODE }

if (-not (Test-Path $exe)) {
    Fail "发布目录中找不到 StartupTool.exe" 20
}

if ($configBackup) {
    Copy-Item -Path (Join-Path $configBackup "*") -Destination $configDir -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force $configBackup -ErrorAction SilentlyContinue
    Write-Host "已还原原有配置到：$configDir" -ForegroundColor Gray
}

Write-Host "发布完成：$exe" -ForegroundColor Green

# ---------- 2. 安装服务（C# 安装器：sc create/描述/失败重启/登录自启/启动）----------
Write-Host "`n[2/2] 注册并启动 Windows 服务..." -ForegroundColor Yellow

& $exe --install
if ($LASTEXITCODE -ne 0) { Fail "服务安装失败" $LASTEXITCODE }

Write-Host "`n============================================" -ForegroundColor Cyan
Write-Host "  安装成功！" -ForegroundColor Green
Write-Host "============================================" -ForegroundColor Cyan
Write-Host "服务名：StartupTool（SYSTEM，开机自启，崩溃自动拉起）"
Write-Host "控制端：登录桌面后自动启动；也可手动运行 $exe"
Write-Host "卸载请执行：.\tests\uninstall.ps1（管理员）"
