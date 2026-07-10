# 开机自运行工具

一个基于 .NET 10.0 的 Windows 开机自运行脚本管理工具，采用 **Windows Service + 系统托盘应用** 的组合架构。

## 功能特点

- ✅ 按指定顺序启动多个脚本（支持 .bat、.cmd、.ps1）
- ✅ 支持随时添加和删除自启动脚本
- ✅ 通过 Windows Service 实现开机自动运行
- ✅ 提供系统托盘管理界面
- ✅ 支持脚本单独启动/停止、批量启动/停止
- ✅ 详细的日志记录
- ✅ 配置文件热重载

## 系统要求

- Windows 操作系统（Windows 10 或更高版本）
- .NET 10.0 Runtime 或更高版本

## 安装步骤

### 方法一：一键安装（推荐）

1. **以管理员身份运行 PowerShell**
2. **执行安装脚本**：

```powershell
.\install.ps1
```

安装脚本会自动完成以下操作：
- 编译发布项目
- 创建配置文件（如不存在）
- 安装并启动 Windows Service
- 配置托盘程序开机自启（通过计划任务）
- 启动托盘程序

### 方法二：手动安装

> **构建输出说明**：
> - `build.bat`：快速构建脚本，输出到 `build\` 目录，适用于开发测试
> - `install.ps1`：正式部署脚本，输出到 `publish\` 目录，适用于生产环境

1. **构建项目**（开发测试用）：

```powershell
# 使用构建脚本，输出到 build\ 目录
.\build.bat

# 或手动构建
dotnet build StartupTool.slnx -c Release
```

2. **发布项目**（正式部署用）：

```powershell
dotnet publish StartupService\StartupService\StartupService.csproj -c Release -o publish\Service -r win-x64 --self-contained false
dotnet publish StartupTray\StartupTray\StartupTray.csproj -c Release -o publish\Tray -r win-x64 --self-contained false
```

3. **安装 Windows Service**：

```powershell
sc create StartupService binPath= "E:\path\to\publish\Service\StartupService.exe" start= auto
sc description StartupService "开机自运行工具 - 自动启动配置脚本的服务"
sc start StartupService
```

4. **启动托盘程序**：

```powershell
.\publish\Tray\StartupTray.exe
```

## 使用方法

### 1. 系统托盘管理

安装完成后，托盘程序会自动启动并出现在系统托盘中。

**右键菜单操作**：
- **显示管理窗口**：打开可视化管理界面
- **启动所有脚本**：批量启动配置的所有脚本
- **停止所有脚本**：停止所有运行中的脚本
- **退出**：关闭托盘程序

### 2. 管理窗口

点击系统托盘图标打开管理窗口，可以进行以下操作：

**服务管理**：
- **启用/禁用开机自启**：控制 Windows Service 的运行状态
- **刷新状态**：更新服务状态显示

**脚本管理**：
- **添加脚本**：输入脚本名称、路径和启动顺序
- **移除脚本**：删除选中的脚本配置
- **批量启动脚本**：按顺序启动所有脚本
- **停止所有**：停止所有运行中的脚本

**脚本右键菜单**：
- **单独启动**：启动选中的脚本
- **停止**：停止选中的脚本

### 3. 配置文件

配置文件位于 `config/scripts.json`，当前实际格式如下：

```json
{
  "Scripts": [
    {
      "Name": "示例脚本",
      "Path": "C:\\example\\script.bat",
      "Enabled": false,
      "Arguments": ""
    }
  ]
}
```

| 字段 | 类型 | 说明 |
|------|------|------|
| `Name` | string | 脚本名称（唯一） |
| `Path` | string | 脚本文件路径 |
| `Enabled` | bool | 是否启用（JSON 中存在，但代码模型未定义，当前未生效） |
| `Arguments` | string | 命令行参数（JSON 中存在，但代码模型未定义，当前未生效） |
| `Order` | int | 启动顺序（代码模型中定义，但 JSON 中未包含，需手动添加） |
| `RunMode` | string | 运行模式（`normal` 或 `min`，代码模型中定义，JSON 中未包含） |
| `WebPort` | int | Web 服务端口（代码模型中定义，JSON 中未包含） |

> **注意**：当前存在数据模型与配置文件不一致的问题，部分字段在代码中定义但 JSON 中未使用，或 JSON 中存在但代码未读取。如需使用 `Order` 排序功能，需要手动在 JSON 中添加该字段。

## 项目结构

```
开机自运行工具/
├── StartupCommon/              # 共享类库（核心逻辑）
│   ├── AppConfig.cs              # 应用配置模型
│   ├── AppHelper.cs              # 应用辅助工具类
│   ├── ConfigManager.cs          # 配置管理（加载/保存/热重载）
│   ├── Logger.cs                 # 日志记录器
│   ├── ScriptItem.cs             # 脚本项模型
│   ├── ScriptRunner.cs           # 脚本运行器（启动/停止进程）
│   └── StartupCommon.csproj
│
├── StartupService/             # Windows Service（开机自启）
│   ├── Program.cs                # 服务入口
│   ├── StartupWorker.cs          # 后台工作者
│   ├── Properties/
│   │   └── launchSettings.json   # 启动设置
│   ├── appsettings.json          # 应用配置
│   ├── appsettings.Development.json  # 开发环境配置
│   └── StartupService.csproj
│
├── StartupTray/                # 系统托盘应用（可视化管理）
│   ├── MainForm.cs               # 主窗体
│   ├── Program.cs                # 应用入口
│   └── StartupTray.csproj
│
├── config/                     # 配置目录
│   └── scripts.json              # 脚本配置文件
│
├── logs/                       # 日志目录（运行时自动创建）
│   └── startup.log               # 日志文件
│
├── build.bat                   # 快速构建脚本（输出到 build\，用于测试）
├── install.ps1                 # 一键安装脚本（管理员，输出到 publish\，用于部署）
├── uninstall.ps1               # 一键卸载脚本（管理员）
└── README.md                   # 项目说明文档
```

## 日志管理

所有操作日志会记录在 `logs/startup.log` 文件中，包括：
- 服务启动/停止过程
- 脚本启动/停止状态
- 错误信息
- 操作记录

日志格式：
```
[2026-07-10 10:00:00.123] [INFO] 服务启动，工作目录: E:\test\Multi-script_self-starting_tool
[2026-07-10 10:00:01.456] [SUCCESS] 脚本已启动: 示例脚本 (PID: 1234)
[2026-07-10 10:00:02.789] [ERROR] 脚本文件不存在: 测试脚本 (C:\test\missing.bat)
```

## 卸载

以管理员身份运行卸载脚本：

```powershell
.\uninstall.ps1
```

卸载脚本会自动完成以下操作：
- 停止并删除 Windows Service
- 删除计划任务（托盘程序自启）
- 终止运行的托盘程序

> **注意**：配置文件和日志文件保留未删除。如需完全清理，请手动删除项目目录。

## 故障排除

### 1. 服务安装失败
- 确保以管理员身份运行 `install.ps1`
- 检查 .NET 10.0 Runtime 是否已安装
- 查看 `logs/startup.log` 中的错误信息

### 2. 脚本无法启动
- 检查脚本路径是否正确
- 确保脚本文件存在且可执行
- 查看 `logs/startup.log` 中的错误信息

### 3. 开机自启动不生效
- 检查 Windows Service 是否已安装且运行：`sc query StartupService`
- 检查计划任务是否存在：`Get-ScheduledTask -TaskName StartupTray`
- 查看 `logs/startup.log` 中的错误信息

### 4. 托盘程序不显示
- 检查是否已安装 .NET 10.0 Runtime
- 手动运行 `publish\Tray\StartupTray.exe` 测试
- 查看 `logs/startup.log` 中的错误信息

## 版本信息

- 版本：2.0.0
- 日期：2026-07-10
- 技术栈：.NET 10.0 + Windows Service + WinForms
- 架构：Windows Service + 系统托盘应用组合

## 许可证

MIT License