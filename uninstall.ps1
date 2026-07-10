# 开机自运行工具 - 卸载脚本
# 请以管理员身份运行此脚本

$ErrorActionPreference = "Continue"

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  开机自运行工具 - 卸载程序" -ForegroundColor Yellow
Write-Host "============================================" -ForegroundColor Cyan

$svc = Get-Service -Name "StartupTool" -ErrorAction SilentlyContinue
if ($svc) {
    Write-Host "[1/2] 停止并删除 Windows 服务..." -ForegroundColor Yellow
    Stop-Service -Name "StartupTool" -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    sc.exe delete StartupTool 2>&1 | Out-Null
    Write-Host "  服务已删除" -ForegroundColor Green
} else {
    Write-Host "[1/2] 服务不存在，跳过" -ForegroundColor Gray
}

Write-Host "[2/2] 终止运行的程序..." -ForegroundColor Yellow
Get-Process -Name "StartupTool" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Write-Host "  已完成" -ForegroundColor Green

Write-Host ""
Write-Host "卸载完成！" -ForegroundColor Green
Write-Host "注意: 配置文件和日志文件保留未删除。如需完全清理，请手动删除。" -ForegroundColor Yellow