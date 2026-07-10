# 开机自运行工具 - 安装脚本（单文件发布）
# 请以管理员身份运行此脚本

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $scriptDir

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  开机自运行工具 - 安装程序" -ForegroundColor Green
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""

Write-Host "[1/3] 编译发布项目..." -ForegroundColor Yellow

$publishDir = Join-Path $scriptDir "publish"
if (Test-Path $publishDir) {
    Remove-Item -Recurse -Force $publishDir
}

Write-Host "  发布 StartupTool 单文件..." -ForegroundColor Gray
dotnet publish StartupTool\StartupTool.csproj -c Release -r win-x64 --self-contained true -o "$publishDir" /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:EnableCompressionInSingleFile=true 2>&1 | Out-Null

Write-Host "  编译发布完成！" -ForegroundColor Green
Write-Host ""

Write-Host "[2/3] 复制配置文件..." -ForegroundColor Yellow

$configDir = Join-Path $scriptDir "config"
if (-not (Test-Path $configDir)) {
    New-Item -ItemType Directory -Path $configDir -Force | Out-Null
}

$configFile = Join-Path $configDir "scripts.json"
if (-not (Test-Path $configFile)) {
    $defaultConfig = @{
        scripts = @()
        runMode = "normal"
        webPort = 13000
    }
    $defaultConfig | ConvertTo-Json -Depth 5 | Set-Content $configFile -Encoding UTF8
    Write-Host "  已创建默认配置文件" -ForegroundColor Gray
} else {
    Write-Host "  配置文件已存在，保留现有配置" -ForegroundColor Gray
}

# 复制配置文件到发布目录（exe 同级）
Copy-Item -Path $configDir -Destination $publishDir -Recurse -Force
Write-Host "  配置文件已复制到发布目录" -ForegroundColor Gray

Write-Host ""

Write-Host "[3/3] 安装 Windows 服务..." -ForegroundColor Yellow

$serviceExe = Join-Path $publishDir "StartupTool.exe"

$existingService = Get-Service -Name "StartupTool" -ErrorAction SilentlyContinue
if ($existingService) {
    Write-Host "  服务已存在，正在停止..." -ForegroundColor Gray
    Stop-Service -Name "StartupTool" -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    sc.exe delete StartupTool 2>&1 | Out-Null
    Start-Sleep -Seconds 1
}

Write-Host "  注册 Windows 服务..." -ForegroundColor Gray
$result = sc.exe create StartupTool binPath= "`"$serviceExe`" --service" start= auto 2>&1
Write-Host "  $result"

sc.exe description StartupTool "开机自运行工具 - 自动启动配置脚本的服务" 2>&1 | Out-Null

Write-Host "  启动服务..." -ForegroundColor Gray
Start-Service -Name "StartupTool" -ErrorAction SilentlyContinue
$svc = Get-Service -Name "StartupTool"
Write-Host "  服务状态: $($svc.Status)" -ForegroundColor Green
Write-Host ""

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  安装完成！" -ForegroundColor Green
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Windows 服务: StartupTool (开机自动启动)" -ForegroundColor White
Write-Host ""
Write-Host "你可以通过以下方式管理:" -ForegroundColor White
Write-Host "  - 直接运行 StartupTool.exe 打开管理窗口" -ForegroundColor White
Write-Host "  - 通过管理窗口添加/移除脚本" -ForegroundColor White
Write-Host "  - 通过管理窗口启用/禁用开机自启" -ForegroundColor White
Write-Host ""

Write-Host "正在启动管理程序..." -ForegroundColor Yellow
Start-Process $serviceExe
Write-Host "管理程序已启动，请查看系统托盘。" -ForegroundColor Green
