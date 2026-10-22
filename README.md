# StartupTool 开机脚本守护工具

基于 .NET 10 的 Windows 开机脚本管理与守护工具，**一个 `StartupTool.exe` 同时承担三种角色**：

| 角色 | 启动方式 | 说明 |
|------|----------|------|
| Windows 服务 | `StartupTool.exe --service` | SYSTEM 身份、开机即启，是脚本的唯一权威所有者 |
| WPF 控制端 | 直接双击（无参） | 托盘 + 主窗口，只通过命名管道下发命令，自身不启停脚本 |
| 心跳投递进程 | `StartupTool.exe --beat <令牌> <时间戳>` | 由脚本调用，投递一次心跳后立即退出 |

> 控制端与服务是两个进程，通过本机命名管道 `StartupTool-v1` 通信。服务不在时界面会显示"未连接"，不会退化成"控制端自己启动脚本"。

## 功能特性

- 按 `order` 顺序启动脚本，支持 `.bat` / `.cmd` / `.ps1` / `.py` / `.pyw` 及任意可执行文件
- **Python 虚拟环境**：可为每个 `.py` 脚本单独指定解释器（如 `venv\Scripts\python.exe`），无需激活虚拟环境
- 启动编排：固定延迟、等待网络就绪、前置脚本依赖、脚本间启动间隔
- 进程守护：崩溃按策略（不重启 / 仅异常退出重启 / 总是重启）自动拉起
- 资源监控：每 5 秒采样 CPU / 内存 / GPU，超阈值自动重启
- 应用层心跳：脚本主动 `--beat` 报活，超时判定卡死并重启；提供 bat / PowerShell / Python 三种心跳示例模板
- 熔断保护：滑动窗口内启动次数超限自动熔断，需人工「恢复守护」，避免崩溃风暴
- 运行会话可选：默认在登录用户桌面会话启动，也可勾选在服务会话（Session 0）运行
- 进程树管理：每个脚本运行在独立 Job 对象中，停止即整树终止，不留孤儿进程
- 实时输出：查看脚本 stdout / stderr，按脚本分目录落盘
- 配置热重载：手工改 `config/scripts.json` 后自动生效
- 导入 / 导出配置、一键导出诊断包（令牌已打码）
- 中英双语界面实时切换、托盘气泡告警
- 单文件自包含发布，目标机器无需安装 .NET 运行时

## 系统要求

- Windows 10 或更高版本（x64）
- 安装服务需要管理员权限
- 运行已发布的单文件 exe 无需预装 .NET；从源码构建需要 .NET 10 SDK

## 安装

### 方式一：单文件 exe（推荐）

1. 取得 `StartupTool.exe`，放到一个固定目录，例如 `C:\StartupTool\`。
2. 以管理员身份打开终端，执行：

```powershell
C:\StartupTool\StartupTool.exe --install
```

安装器会完成：注册服务（`StartupTool`，SYSTEM，自动启动）、写入服务描述、配置崩溃自动重启策略、写入 HKLM Run 登录自启（`--minimized`）、立即启动服务。

3. 登录桌面后控制端自动驻留托盘；也可手动双击 `StartupTool.exe` 打开主窗口。

### 方式二：源码构建 + 一键安装

```powershell
# 管理员 PowerShell，在仓库根目录执行
.\tests\install.ps1
```

脚本会发布到 `publish\`（Release / win-x64 / 自包含 / 单文件），再调用 `StartupTool.exe --install`。每步校验退出码，失败立即终止。覆盖安装前会先备份 `publish\config`，安装后原样还原，配置与脚本不丢。

### 方式三：构建 MSI 安装包

```powershell
# 仓库根目录执行
.\tests\build.bat
```

脚本会发布单文件 exe 并打成 MSI，产物为 `build\StartupTool-Setup.msi`（约 67 MB，内含完整运行时）。

- 双击安装，或 `msiexec /i build\StartupTool-Setup.msi`；安装过程会自动注册并启动服务、写入登录自启。
- 静默安装：`msiexec /i build\StartupTool-Setup.msi /qn`。
- 卸载走「设置 → 应用」或 `msiexec /x build\StartupTool-Setup.msi`，会一并停止并删除服务、清理登录自启；`config`、`logs` 保留在安装目录。
- 打包需要 WiX v4（固定 4.0.5，WiX 5+ 的许可策略不同）。首次使用先在本仓库安装一次：

```powershell
dotnet tool install wix --version 4.0.5 --tool-path tools
```

`build.bat` 会优先使用 `tools\wix.exe`，其次使用 PATH 上的 `wix`；版本不是 4.x 时直接报错并给出安装命令。

## 命令行参数

| 参数 | 作用 |
|------|------|
| 无参 | 启动 WPF 控制端（托盘 + 主窗口） |
| `--minimized` / `--tray` | 控制端只驻留托盘，不弹主窗口（登录自启用） |
| `--service` | 以 Windows 服务模式运行宿主 |
| `--console` | 控制台调试模式：不安装服务，直接用当前会话跑同一套宿主 |
| `--install` | 注册并启动服务（需管理员） |
| `--uninstall` | 停止并删除服务、移除登录自启（需管理员） |
| `--beat <令牌> <Unix秒时间戳>` | 投递一次脚本心跳 |

`--install` 退出码：`0` 成功、`5` 需要管理员、`20` 找不到 exe、`21` 服务已注册但启动失败。

`--beat` 退出码：`0` 成功、`10` 参数或令牌格式错误、`11` 连不上服务、`12` 令牌无效、`13` 时间戳偏差超过 300 秒、`14` 通信错误、`15` 令牌有效但脚本未运行。

## 使用说明

### 主窗口

- 顶部工具栏：添加、从模板添加、全部停止、刷新、打开配置文件、打开日志目录、搜索、导入配置、导出配置、导出诊断包、设置。
- 表格列：名称、状态、PID、会话、运行时长、重启次数、CPU、内存、GPU、最后心跳、外部同名进程、退出码、最近故障、操作。
- 操作列与右键菜单：启动、停止、重启、查看输出、编辑、心跳命令、外部进程、打开所在位置、上移/下移、删除；熔断状态下额外提供「恢复守护」。
- 拖拽行可调整顺序，顺序即启动顺序。
- 关闭窗口只是隐藏到托盘，托盘菜单「退出控制端」才真正结束进程。

### 托盘菜单

- 显示主窗口、全部停止、按脚本逐个启停、退出控制端。
- 故障（崩溃、超限重启、心跳超时、熔断）会弹气泡提示，可在设置中关闭。

### 脚本编辑窗口

| 分组 | 字段 |
|------|------|
| 基本信息 | 脚本/程序路径、Python 解释器、名称、启动参数、工作目录、环境变量 |
| 启动设置 | 开机自动启动、允许手动启动、在服务会话运行、启动间隔、启动前延迟、等待网络、需要管理员 |
| 启动依赖 | 勾选前置脚本；保存前会拦截自依赖与环依赖 |
| 守护与资源 | CPU / 内存 / GPU 上限、重启延迟、熔断阈值、崩溃策略、心跳开关与间隔/超时 |

### 添加 Python 脚本（含虚拟环境）

1. 点「添加」，路径选择 `.py` 或 `.pyw` 文件。
2. 若该脚本要使用项目里的虚拟环境，在「Python 解释器」填该环境的完整路径，例如：

```text
D:\project\.venv\Scripts\python.exe
```

  虚拟环境目录名任意，不需要激活，也不需要把 `Scripts` 加进 PATH——直接用该解释器启动，依赖自然来自这个环境。
3. 留空则按系统 `py.exe` → `python.exe` 的顺序回退。此时若系统没装对应依赖，脚本会启动失败并在日志中给出原因。
4. 工作目录建议显式填写（留空时取脚本所在目录），这样脚本里的相对路径才稳定。

### 脚本心跳

1. 编辑脚本，勾选「启用心跳」，保存后右键该行选「心跳命令…」。
2. 窗口里会显示 `StartupTool.exe --beat <64位令牌> <Unix秒时间戳>` 与完整脚本示例，可直接复制。
3. 或在「从模板添加」中选 bat / PowerShell / Python 心跳模板，把 `BEAT_PATH`、`TOKEN` 换成实际值。
4. 超过「心跳超时」秒数未收到心跳即判定卡死，按崩溃策略重启。

### 全局设置

语言（跟随系统 / 中文 / English）、故障气泡开关、单个日志文件大小上限、日志保留天数、熔断滑动窗口与窗口内最大启动次数。这些设置由服务端落盘到 `config/scripts.json` 的 `settings` 段。

### 配置导入 / 导出 / 诊断包

- 导出配置：可选是否携带心跳令牌。
- 导入配置：可选是否连全局设置一起整体替换。
- 导出诊断包：生成 `logs\diagnostics\startuptool-diagnostics-yyyyMMdd-HHmmss.zip`，内含打码后的配置、运行状态快照、环境信息与最近服务日志（最多 50 个），便于反馈问题。

## 配置文件

路径：**exe 所在目录** `\config\scripts.json`（服务与控制端共用同一份）。写入为原子替换，并保留 `scripts.json.bak`；服务运行时会收紧该目录 ACL。

```json
{
  "version": 2,
  "scripts": [
    {
      "id": "6a1f0c3d9b8e4f2a9c7d5e1b3a2c4d6e",
      "name": "示例采集脚本",
      "path": "D:\\project\\collect.py",
      "arguments": "--mode fast",
      "pythonExecutable": "D:\\project\\.venv\\Scripts\\python.exe",
      "order": 0,
      "autoStart": true,
      "manualEnabled": true,
      "runInSystemSession": false,
      "workingDirectory": "D:\\project",
      "environmentVariables": { "APP_ENV": "prod" },
      "requireElevation": false,
      "initialDelaySeconds": 0,
      "waitForNetwork": true,
      "dependsOn": [],
      "startIntervalSeconds": 0,
      "maxCpuPercent": 90,
      "maxMemoryMb": 0,
      "maxGpuPercent": 90,
      "restartDelaySeconds": 10,
      "crashRestart": "OnFailure",
      "maxRestartsPerWindow": 0,
      "heartbeatEnabled": true,
      "heartbeatIntervalSeconds": 30,
      "heartbeatTimeoutSeconds": 90,
      "heartbeatToken": "32 字节随机数的 64 位十六进制"
    }
  ],
  "settings": {
    "logMaxSizeMb": 5,
    "logRetentionDays": 30,
    "circuitWindowSeconds": 60,
    "circuitMaxStarts": 10,
    "notifyOnFault": true,
    "language": "zh-CN"
  }
}
```

### 脚本字段

| 字段 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `id` | string | 自动生成 | 稳定唯一标识（GUID N），依赖与运行状态都用它关联 |
| `name` | string | `""` | 显示名，允许重复 |
| `path` | string | `""` | 脚本或可执行文件绝对路径 |
| `arguments` | string | `""` | 命令行参数，原样拼接 |
| `pythonExecutable` | string? | `null` | `.py` / `.pyw` 专用解释器路径；为空回退 `py.exe` → `python.exe` |
| `order` | int | `0` | 启动顺序，数值小的先启动 |
| `autoStart` | bool | `true` | 服务启动时自动拉起 |
| `manualEnabled` | bool | `true` | 是否允许在控制端手动启动 |
| `runInSystemSession` | bool | `false` | `true` 在 Session 0 运行；`false` 在登录用户桌面运行 |
| `workingDirectory` | string? | `null` | 工作目录；为空取脚本所在目录 |
| `environmentVariables` | object | `{}` | 追加给子进程的环境变量 |
| `requireElevation` | bool | `false` | 是否需要管理员权限（UAC 提升） |
| `initialDelaySeconds` | int | `0` | 启动前固定延迟 |
| `waitForNetwork` | bool | `false` | 启动前等待网络就绪（最长 60 秒） |
| `dependsOn` | string[] | `[]` | 前置脚本 id 列表，前置进入 Running 后才启动 |
| `startIntervalSeconds` | int | `0` | 与上一个脚本之间的最小间隔 |
| `maxCpuPercent` | int | `90` | CPU 上限（%），超限重启；`0` 不限制 |
| `maxMemoryMb` | long | `0` | 内存上限（MB），超限重启；`0` 不限制 |
| `maxGpuPercent` | int | `90` | GPU 上限（%），超限重启；`0` 不限制 |
| `restartDelaySeconds` | int | `10` | 停止到重启的等待秒数 |
| `crashRestart` | string | `OnFailure` | `Never` / `OnFailure` / `Always` |
| `maxRestartsPerWindow` | int | `0` | 本脚本熔断阈值；`0` 用全局设置 |
| `heartbeatEnabled` | bool | `false` | 是否启用应用层心跳 |
| `heartbeatIntervalSeconds` | int | `30` | 建议心跳间隔（仅提示） |
| `heartbeatTimeoutSeconds` | int | `90` | 心跳超时秒数 |
| `heartbeatToken` | string | `""` | 服务端生成并保存的心跳令牌 |

### 全局设置字段

| 字段 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `logMaxSizeMb` | int | `5` | 单个日志文件大小上限（1–1024） |
| `logRetentionDays` | int | `30` | 日志保留天数（1–3650） |
| `circuitWindowSeconds` | int | `60` | 熔断滑动窗口秒数（1–86400） |
| `circuitMaxStarts` | int | `10` | 窗口内默认最大启动次数（1–1000） |
| `notifyOnFault` | bool | `true` | 故障是否弹托盘气泡 |
| `language` | string? | `"zh-CN"` | `zh-CN` / `en` / `system` |

旧版（v1）的 `Enabled` 单字段配置会在首次加载时自动迁移为 v2 的 `autoStart` + `manualEnabled`，并以新格式回写。

## 日志

目录同样以 exe 所在目录为基准：

```text
logs\
  service\service-yyyyMMdd.log                 # 全局运行日志
  scripts\<脚本名_短哈希>\output-yyyyMMdd.log   # 各脚本 stdout / stderr
  diagnostics\startuptool-diagnostics-*.zip    # 诊断包
```

当天日志超过 `logMaxSizeMb` 自动分片为 `-2.log`、`-3.log`…；超过 `logRetentionDays` 的文件在服务启动及跨天时清理。日志写盘失败只会被吞掉，绝不影响守护逻辑。

## 守护与自动恢复

- 每个脚本一个 Job 对象，停止时整树终止（含脚本拉起的子进程），超时 10 秒后强制。
- 崩溃策略：`Never` 不重启；`OnFailure`（默认）仅异常退出重启；`Always` 无论退出码都重启。
- 资源超限、心跳超时同样按崩溃策略处理，间隔取 `restartDelaySeconds`。
- 熔断：默认 60 秒窗口内启动达到 10 次即进入 `Faulted`，自动守护暂停；控制端「恢复守护」可人工解除。
- 开机时服务先等 10 秒避让启动风暴；若当时没有登录会话，脚本排队，用户登录后 5 秒轮询内补发。

## 卸载

```powershell
# 管理员
.\tests\uninstall.ps1
```

或直接执行 `StartupTool.exe --uninstall`。会停止并删除服务、移除 HKLM Run 登录自启；**程序文件、`config`、`logs` 保留**，需要时手动删除。

## 故障排除

| 现象 | 排查方向 |
|------|----------|
| 安装失败 | 是否管理员运行；`--install` 返回码 5/20/21 的含义见上表 |
| 控制端显示未连接 | `sc query StartupTool` 确认服务状态；查看 `logs\service` |
| 脚本没起来 | 查看对应 `logs\scripts\...\output-*.log`；确认路径存在、参数正确 |
| Python 脚本报模块缺失 | 是否填了正确的虚拟环境 `python.exe`；留空时会用系统 Python |
| 脚本一直 `Faulted` | 启动即失败触发熔断，修好脚本后点「恢复守护」 |
| 心跳一直超时 | 心跳命令里的令牌是否被重置；`--beat` 返回码 11/12/13/15 |
| 改了配置没生效 | 配置文件是否保存在 exe 目录的 `config\scripts.json`；保存后看底部信息条提示 |
| 换目录后开机不自启 | 重新执行 `StartupTool.exe --install`，服务 binPath 与 Run 键都指向原路径 |

## 项目结构

```text
StartupTool.slnx
src/
  Directory.Build.props              # 统一 net10.0-windows / x64 / nullable
  StartupTool.Core/                  # 与界面无关的核心逻辑
    AppPaths.cs                      # 统一路径基准（exe 目录）
    Configuration/                   # 配置加载、原子写入、热重载、迁移、ACL
    Models/                          # AppConfig / ScriptConfig / GlobalSettings / 依赖校验
    Processes/                       # 命令行构造、进程启动、输出读取、网络就绪、PE 信息
    Supervision/                     # 状态机、脚本守护、心跳、熔断、会话恢复
    Monitoring/                      # 资源采样、外部进程扫描
    Ipc/                             # 命名管道服务端/客户端、帧协议、心跳客户端
    Logging/                         # 日志落盘与清理
    Diagnostics/                     # 诊断包打包
    Templates/                       # 内置脚本模板（只读，不写盘）
  StartupTool.Service/               # Windows 服务宿主
    ServiceHost.cs                   # 服务 / 控制台两种生命周期
    SupervisionWorker.cs             # 依赖装配与开机启动流程
    Installation/ServiceInstaller.cs  # sc.exe 注册/删除、失败重启、登录自启
  StartupTool.App/                   # WPF 控制端
    Program.cs                       # 唯一入口，按参数分流
    MainWindow.xaml(.cs)             # 主窗口 + 托盘
    Views/ ViewModels/ Services/     # MVVM、本地化、输出缓冲
    Resources/                       # 双语资源、app.ico（同时作应用与托盘图标）
    Themes/                          # 颜色与控件样式
tests/
  StartupTool.Core.Tests/            # xUnit：命令行、配置、IPC、守护、安装器等
  install.ps1                        # 发布到 publish\ 并安装服务
  uninstall.ps1                      # 卸载服务与登录自启
  build.bat                          # 发布并打包 build\StartupTool-Setup.msi
docs/                                # 开发文档、项目文档、改动清单、测试与检查记录
StartupTool.wxs                      # WiX v4 安装包定义
```

## 开发

```powershell
dotnet build StartupTool.slnx -c Release
dotnet test tests\StartupTool.Core.Tests\StartupTool.Core.Tests.csproj
```

调试时不想装服务，可用 `StartupTool.exe --console` 直接跑宿主，再另开一个 `StartupTool.exe` 作为控制端。

## 许可证

[Attribution-NonCommercial 4.0 International](LICENSE)（CC BY-NC 4.0）。
