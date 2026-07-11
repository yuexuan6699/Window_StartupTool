# 开机自运行工具

一个基于 .NET 10.0 的 Windows 开机自运行多脚本及管理工具，采用 **单程序双模式** 架构：同一可执行文件 `StartupTool.exe` 既可作为 Windows Service 在开机时自动运行，也可作为系统托盘应用供用户交互管理。

## 功能特点

- ✅ 按指定顺序启动多个脚本（支持 .bat、.cmd、.ps1）
- ✅ 支持随时添加和删除自启动脚本
- ✅ 通过 Windows Service 实现开机自动运行
- ✅ 提供系统托盘管理界面
- ✅ 支持脚本单独启动/停止、批量启动/停止
- ✅ 详细的日志记录（分层日志，按脚本拼音名分类）
- ✅ 配置文件热重载
- ✅ 自动检测并更新服务路径（移动程序后自动提示）
- ✅ 跨进程状态同步（托盘可查看/管理服务启动的脚本）
- ✅ 资源监控（CPU、内存、GPU 占用实时显示）
- ✅ 资源占用过高自动重启脚本

## 系统要求

- Windows 操作系统（Windows 10 或更高版本）
- .NET 10.0 Runtime 或更高版本

## 安装步骤

### 方法一：使用已构造好的可执行程序包（推荐）

1. **访问 Releases 页面下载最新版本**：

   [https://github.com/yuexuan6699/Window_StartupTool/releases](https://github.com/yuexuan6699/Window_StartupTool/releases)

2. **下载**：

   下载 `StartupTool.exe` - 主程序（单文件，无需额外依赖）
   移动到自己所需放置的位置，如 `C:\startuptool\StartupTool.exe`
 
### 方法二：从源码构建

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
dotnet publish StartupTool/StartupTool.csproj -c Release -o publish -r win-x64 --self-contained false
```

3. **安装 Windows Service**：

```powershell
sc create StartupTool binPath= "E:\path\to\publish\StartupTool.exe --service" start= auto
sc description StartupTool "开机自运行工具 - 自动启动配置脚本的服务"
sc start StartupTool
```

4. **启动托盘程序**：

```powershell
.\publish\StartupTool.exe
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
- **启动选中**：启动选中的脚本
- **关闭选中**：停止选中的脚本
- **批量启动脚本**：按顺序启动所有脚本
- **停止所有**：停止所有运行中的脚本

**脚本右键菜单**：
- **单独启动**：启动选中的脚本
- **停止**：停止选中的脚本

**查看运行中的脚本**：
- 点击"运行中程序"按钮查看所有运行中的脚本
- 实时显示 CPU、内存、GPU 占用
- 支持查看脚本日志
- 支持终止脚本进程

### 3. 配置文件

配置文件位于 `config/scripts.json`，格式如下：

```json
{
  "Scripts": [
    {
      "Name": "示例脚本",
      "Path": "C:\\example\\script.bat",
      "Enabled": true,
      "Arguments": "--debug",
      "Order": 0,
      "MaxCpuPercent": 90,
      "MaxMemoryMb": 0,
      "MaxGpuPercent": 90,
      "RestartDelaySeconds": 10
    }
  ],
  "RunMode": "normal",
  "WebPort": 13000
}
```

| 字段 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `Name` | string | `""` | 脚本名称（唯一标识） |
| `Path` | string | `""` | 脚本文件完整路径 |
| `Order` | int | `0` | 启动顺序（数值越小越先启动） |
| `Enabled` | bool | `true` | 是否启用（禁用的脚本开机不启动） |
| `Arguments` | string | `""` | 传递给脚本的命令行参数 |
| `MaxCpuPercent` | int | `90` | CPU 占用上限（%），超过则自动重启 |
| `MaxMemoryMb` | long | `0` | 内存占用上限（MB），0 表示不限制 |
| `MaxGpuPercent` | int | `90` | GPU 占用上限（%），超过则自动重启 |
| `RestartDelaySeconds` | int | `10` | 重启前等待的秒数 |
| `RunMode` | string | `"normal"` | 运行模式（预留字段） |
| `WebPort` | int | `13000` | Web 服务端口（预留字段） |

### 4. 资源监控与自动重启

程序会每隔 5 秒自动检测所有脚本的资源占用情况：
- **CPU 占用过高**：超过 `MaxCpuPercent` 时自动重启
- **内存占用过高**：超过 `MaxMemoryMb` 时自动重启（0 表示不限制）
- **GPU 占用过高**：超过 `MaxGpuPercent` 时自动重启

**防抖动机制**：同一脚本 60 秒内只会重启一次，避免频繁重启。

## 项目结构

```
开机自运行工具/
├── StartupCommon/              # 共享类库（核心逻辑）
│   ├── AppConfig.cs              # 应用配置模型
│   ├── AppHelper.cs              # 应用辅助工具类
│   ├── ConfigManager.cs          # 配置管理（加载/保存/热重载）
│   ├── Logger.cs                 # 日志记录器
│   ├── ProcessMonitor.cs         # 进程资源监控（CPU/内存/GPU）
│   ├── ScriptItem.cs             # 脚本项模型
│   ├── ScriptRunner.cs           # 脚本运行器（启动/停止进程）
│   ├── PinyinConverter.cs        # 拼音转换工具类（中文→拼音目录名）
│   └── StartupCommon.csproj
│
├── StartupTool/                 # 主程序（服务 + 托盘合一）
│   ├── Program.cs                # 程序入口，判断运行模式并启动对应模块
│   ├── MainForm.cs               # 主窗体（服务管理 + 脚本管理）
│   └── StartupTool.csproj
│
├── build.bat                    # 快速构建脚本（输出到 build\，用于测试）
├── install.ps1                  # 一键安装脚本（管理员，输出到 publish\，用于部署）
├── uninstall.ps1                # 一键卸载脚本（管理员）
└── README.md                    # 项目说明文档
```

## 日志管理

### 日志目录结构

- `logs/startup/` - 主程序运行日志
- `logs/{脚本拼音名}/` - 各脚本运行日志（中文名自动转拼音）

### 日志文件名格式

`yyyy-MM-dd_NNN.log`（日期_序号.log），每次程序启动或脚本启动时创建新文件。

### 日志格式

```
[2026-07-11 10:00:00.123] [INFO] 服务启动，工作目录: E:\test\Multi-script_self-starting_tool
[2026-07-11 10:00:01.456] [SUCCESS] 脚本已启动: 示例脚本 (PID: 1234)
[2026-07-11 10:00:02.789] [ERROR] 脚本文件不存在: 测试脚本 (C:\test\missing.bat)
[2026-07-11 10:00:03.012] [WARN] 脚本资源占用过高，将重启: 示例脚本 - CPU占用过高(95.5% > 90%)
```

## 卸载

以管理员身份运行卸载脚本：

```powershell
.\uninstall.ps1
```

卸载脚本会自动完成以下操作：
- 停止并删除 Windows Service
- 终止运行的托盘程序

> **注意**：配置文件和日志文件保留未删除。如需完全清理，请手动删除项目目录。

## 故障排除

### 1. 服务安装失败
- 确保以管理员身份运行 `install.ps1`
- 检查 .NET 10.0 Runtime 是否已安装
- 查看 `logs/startup/` 中的错误信息

### 2. 脚本无法启动
- 检查脚本路径是否正确
- 确保脚本文件存在且可执行
- 查看对应脚本日志目录中的错误信息

### 3. 开机自启动不生效
- 检查 Windows Service 是否已安装且运行：`sc query StartupTool`
- 查看 `logs/startup/` 中的错误信息

### 4. 托盘程序不显示
- 检查是否已安装 .NET 10.0 Runtime
- 手动运行 `publish\StartupTool.exe` 测试
- 查看 `logs/startup/` 中的错误信息

### 5. 移动程序后服务无法启动
- 双击运行 `StartupTool.exe`，程序会自动检测服务路径是否变更
- 如果路径不一致，会弹出提示框询问是否更新
- 点击"是"后会自动更新服务路径

## 版本信息

- 版本：2.1.0
- 日期：2026-07-11
- 技术栈：.NET 10.0 + Windows Service + WinForms
- 架构：单程序双模式（Windows Service + 系统托盘应用）

## 许可证

MIT License
