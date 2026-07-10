# 开机自运行工具 - 项目架构与开发规范

## 一、项目概述

本项目是一个基于 .NET 10.0 的 Windows 开机自运行脚本管理工具，采用 **单程序双模式** 架构：同一可执行文件 `StartupTool.exe` 既可作为 Windows Service 在开机时自动运行，也可作为系统托盘应用供用户交互管理。

### 核心功能

- 脚本管理：添加、删除、更新启动脚本配置
- 脚本运行：单独启动/停止、选中启动/停止、批量启动/停止脚本
- 命令行参数：支持为脚本传递命令行参数
- 脚本禁用：支持临时禁用部分脚本，开机时不会启动
- 开机自启：通过 Windows Service 实现系统级开机自动运行
- 配置管理：JSON 配置文件，支持热重载
- 日志记录：分层日志，主程序日志写入 `logs/startup/`，脚本日志写入 `logs/{脚本拼音名}/`，文件名格式为 `yyyy-MM-dd_NNN.log`

### 运行模式

| 模式 | 触发方式 | 说明 |
|------|----------|------|
| **服务模式** | `StartupTool.exe --service` 或 Windows Service 启动 | 开机自动运行，无界面，启动所有启用的脚本 |
| **托盘模式** | 直接双击 `StartupTool.exe` | 显示系统托盘图标和管理窗口，供用户交互 |

## 二、项目架构

### 2.1 解决方案结构

```
StartupTool.slnx
├── StartupCommon/              # 共享类库（核心逻辑）
│   ├── AppConfig.cs              # 应用配置模型
│   ├── AppHelper.cs              # 应用辅助工具类
│   ├── ConfigManager.cs          # 配置管理（加载/保存/热重载）
│   ├── Logger.cs                 # 日志记录器
│   ├── ScriptItem.cs             # 脚本项模型
│   ├── ScriptRunner.cs           # 脚本运行器（启动/停止进程）
│   ├── PinyinConverter.cs          # 拼音转换工具类（中文→拼音目录名）
│   └── StartupCommon.csproj
│
├── StartupTool/                # 主程序（服务 + 托盘合一）
│   ├── Program.cs                # 程序入口，判断运行模式并启动对应模块
│   ├── MainForm.cs               # 主窗体（服务管理 + 脚本管理）
│   └── StartupTool.csproj
│
├── config/                     # 配置目录
│   └── scripts.json              # 脚本配置文件
│
├── build.bat                   # 构建脚本
├── install.ps1                 # 服务安装脚本（管理员）
├── uninstall.ps1               # 服务卸载脚本（管理员）
└── README.md                   # 项目说明文档
```

### 2.2 项目依赖关系

| 项目 | 类型 | 依赖项 | 职责 |
|------|------|--------|------|
| **StartupCommon** | .NET 类库 | ToolGood.Words.Pinyin | 核心逻辑共享：配置管理、拼音转换、脚本运行、日志记录、工具方法 |
| **StartupTool** | WinExe | StartupCommon + Microsoft.Extensions.Hosting.WindowsServices + System.ServiceProcess.ServiceController + System.Management | 单程序双模式：Windows Service + 系统托盘应用 |

```
┌─────────────────────────────────┐
│        StartupTool.slnx         │
├──────────────┬──────────────────┤
│              │                  │
│  Startup     │   Startup        │
│  Common      │   Tool           │
│  (类库)      │   (WinExe)       │
│              │   (服务+托盘)     │
└──────┬───────┴────────┬─────────┘
       │                │
       ▼                │
  StartupCommon ────────┘
    (共享依赖)
```

### 2.3 运维脚本说明

| 文件 | 类型 | 职责描述 |
|------|------|----------|
| `build.bat` | Batch 脚本 | 一键构建单文件 exe（输出到 build\，用于测试） |
| `install.ps1` | PowerShell 脚本 | 编译发布单文件 exe、安装 Windows Service（输出到 publish\，用于部署） |
| `uninstall.ps1` | PowerShell 脚本 | 卸载 Windows Service 并终止运行程序 |

### 2.4 核心模块职责

#### StartupCommon

| 模块 | 文件 | 职责描述 |
|------|------|----------|
| **配置管理** | ConfigManager.cs | JSON 配置文件的加载、保存、热重载（FileSystemWatcher） |
| **脚本运行** | ScriptRunner.cs | 进程启动/停止管理、运行状态追踪、批量操作、命令行参数传递、相对路径自动补全（基于项目根目录） |
| **日志记录** | Logger.cs | 多级别日志输出（INFO/ERROR/WARN/SUCCESS），文件+控制台双输出 |
| **模型定义** | AppConfig.cs / ScriptItem.cs | 应用配置与脚本项的数据模型（含 Enabled、Arguments 字段） |
| **辅助工具** | AppHelper.cs | 路径查找、文件验证、自身路径获取等通用工具方法 |
| **拼音转换** | PinyinConverter.cs | 中文转拼音（不含声调），用于构建脚本日志目录名 |

#### StartupTool

| 模块 | 文件 | 职责描述 |
|------|------|----------|
| **程序入口** | Program.cs | 判断运行模式（服务/托盘），启动对应模块，单例检查（排除服务进程） |
| **工作者** | StartupWorker（内嵌） | 服务模式下加载配置并启动所有启用的脚本，服务停止时清理进程 |
| **主窗体** | MainForm.cs | 服务状态管理、脚本列表展示、脚本增删改查、启用/禁用切换、选中启动/停止、右键菜单、系统托盘图标 |

## 三、代码规范与命名约定

### 3.1 命名规范

| 类型 | 规范 | 示例 |
|------|------|------|
| **命名空间** | PascalCase | `StartupCommon`、`StartupTool` |
| **类名** | PascalCase | `ConfigManager`、`ScriptRunner` |
| **接口名** | I + PascalCase | （当前项目无接口定义，如后续扩展） |
| **方法名** | PascalCase | `StartScript`、`GetScripts` |
| **属性名** | PascalCase | `Name`、`Path`、`Order`、`Enabled`、`Arguments` |
| **字段名** | 下划线开头 + camelCase | `_configManager`、`_lock`、`_isRefreshing` |
| **局部变量** | camelCase | `script`、`baseDir` |
| **常量** | UPPER_SNAKE_CASE | `MAX_RETRY_COUNT`（如后续扩展） |

### 3.2 代码风格

#### 3.2.1 文件范围命名空间

使用 C# 10 的文件范围命名空间语法：

```csharp
namespace StartupCommon;

public class Logger { }
```

#### 3.2.2 Nullable 引用类型

项目启用了 `Nullable`，所有引用类型必须显式处理可空性：

```csharp
public string Name { get; set; } = string.Empty;
public ScriptItem? GetScript(string name);
```

#### 3.2.3 隐式 Using

项目启用了 `ImplicitUsings`，基础命名空间自动引入：

- `System`
- `System.Collections.Generic`
- `System.Linq`
- `System.Threading.Tasks`

#### 3.2.4 异步编程

优先使用 `async/await` 模式处理异步操作：

```csharp
public async Task StartAllScriptsAsync(List<ScriptItem> scripts);
```

### 3.3 设计原则

#### 3.3.1 线程安全

对共享状态使用 `lock` 关键字进行线程同步：

```csharp
private readonly object _lock = new();

public bool AddScript(ScriptItem script)
{
    lock (_lock)
    {
        // 线程安全操作
    }
}
```

#### 3.3.2 资源管理

实现 `IDisposable` 接口管理非托管资源（如 `FileSystemWatcher`）：

```csharp
public void Dispose()
{
    _watcher?.Dispose();
}
```

#### 3.3.3 单一职责

每个类只负责一项功能：
- `ConfigManager`：仅处理配置
- `ScriptRunner`：仅处理进程运行
- `Logger`：仅处理日志记录

### 3.4 配置文件规范

配置文件路径：`config/scripts.json`

**JSON 结构**：

```json
{
  "Scripts": [
    {
      "Name": "示例脚本",
      "Path": "C:\\example\\script.bat",
      "Enabled": true,
      "Arguments": "--debug",
      "Order": 0
    }
  ],
  "RunMode": "normal",
  "WebPort": 13000
}
```

**ScriptItem 模型定义**（`StartupCommon/ScriptItem.cs`）：

| 字段 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `Name` | `string` | `""` | 脚本名称（唯一标识） |
| `Path` | `string` | `""` | 脚本文件完整路径 |
| `Order` | `int` | `0` | 启动顺序（数值越小越先启动） |
| `Enabled` | `bool` | `true` | 是否启用（禁用的脚本开机不启动） |
| `Arguments` | `string` | `""` | 传递给脚本的命令行参数 |

**AppConfig 模型定义**（`StartupCommon/AppConfig.cs`）：

| 字段 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `Scripts` | `List<ScriptItem>` | 空列表 | 脚本配置列表 |
| `RunMode` | `string` | `"normal"` | 运行模式（预留字段） |
| `WebPort` | `int` | `13000` | Web 服务端口（预留字段） |

### 3.5 日志规范

日志文件路径：`logs/` 目录在 exe 所在目录下创建（基于 `AppContext.BaseDirectory`），支持 `build/` 或 `publish/` 等部署目录。

| 日志目录 | 说明 |
|----------|------|
| `logs/startup/` | 主程序运行日志 |
| `logs/{脚本拼音名}/` | 各脚本运行日志，中文名自动转拼音 |

日志文件名格式：`yyyy-MM-dd_NNN.log`（日期_序号.log），每次程序启动或脚本启动时创建新文件。

日志格式：

```
[2026-07-11 10:00:00.123] [INFO] 服务启动，工作目录: E:\test\Multi-script_self-starting_tool
[2026-07-11 10:00:01.456] [SUCCESS] 脚本已启动: 示例脚本 (PID: 1234)
[2026-07-11 10:00:02.789] [ERROR] 脚本文件不存在: 测试脚本 (C:\test\missing.bat)
```

## 四、开发环境

### 4.1 目标框架

| 项目 | 目标框架 |
|------|----------|
| StartupCommon | .NET 10.0 |
| StartupTool | .NET 10.0-windows |

### 4.2 依赖包

| 项目 | 依赖包 | 版本 |
|------|--------|------|
| StartupCommon | ToolGood.Words.Pinyin | 3.1.0.4 |
| StartupTool | Microsoft.Extensions.Hosting | 10.0.9 |
| StartupTool | Microsoft.Extensions.Hosting.WindowsServices | 10.0.9 |
| StartupTool | System.ServiceProcess.ServiceController | 10.0.9 |
| StartupTool | System.Management | 10.0.0 |

### 4.3 运行时标识

所有项目统一使用 `win-x64` 作为运行时标识。

## 五、构建与部署

### 5.1 构建命令

```powershell
dotnet build StartupTool.slnx
```

### 5.2 发布命令

```powershell
dotnet publish StartupTool/StartupTool.csproj -c Release -o publish -r win-x64 --self-contained false
```

### 5.3 服务安装

```powershell
sc create StartupTool binPath= "E:\path\to\StartupTool.exe --service" start= auto
sc description StartupTool "开机自运行工具 - 自动启动配置脚本的服务"
sc start StartupTool
```

### 5.4 使用流程

1. **构建**：运行 `build.bat` 或 `dotnet build`
2. **安装**：以管理员身份运行 `install.ps1`（自动编译发布并安装服务）
3. **管理**：运行 `StartupTool.exe` 打开系统托盘管理窗口
4. **卸载**：以管理员身份运行 `uninstall.ps1`

---

**文档版本**：2.1  
**最后更新**：2026-07-11  
**适用项目**：开机自运行工具（StartupTool）