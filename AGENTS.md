# AGENTS.md · 开发约定

本文件面向修改本仓库代码的人或 AI 助手，说明架构边界、必须遵守的约定，以及容易踩的坑。
使用说明见 [README.md](README.md)；历史文档与测试记录见 [docs/](docs/)。

## 1. 项目定位

Windows 开机启动项守护工具：**一个 exe，三种身份**。

| 身份 | 启动方式 | 说明 |
|------|----------|------|
| Windows 服务 | `--service`（SCM 调用） | SYSTEM 权限，开机即运行（无需登录），是脚本的**唯一权威所有者** |
| WPF 控制端 | 无参 / `--minimized` | 登录用户会话，只通过命名管道下发命令与展示状态 |
| 心跳投递端 | `--beat <令牌> <Unix秒>` | 脚本调用，向服务投递一次存活心跳 |

另有 `--console`（控制台调试宿主）、`--install` / `--uninstall`（服务注册/移除）。

## 2. 硬性架构约束

1. **控制端绝不自行启停脚本进程**。所有启停、配置修改都走命名管道（`StartupTool-v1`）由服务执行。
2. **服务是配置的唯一写入者**。控制端提交的是"意图"，服务端负责合并、校验、落盘、广播。
3. **心跳令牌由服务端生成**。客户端提交的 `heartbeatToken` 一律忽略（新建置空，更新保留原值）。
4. **进程存活单位是 Job 对象**，不是单个 PID。停止 = 终止整个 Job（含脚本派生的子孙进程）。
5. **配置写入必须原子化**：临时文件 → 替换 → 保留 `.bak`；解析失败时沿用内存中的旧配置，绝不清空。
6. **单文件发布**：不引入运行期外部文件依赖（模板、图标等一律内嵌或代码内置）。

## 3. 目录与职责

```text
src/
  Directory.Build.props        net10.0-windows / x64 / nullable / ImplicitUsings
  StartupTool.Core/            与 UI 无关的核心（可被测试工程直接引用）
    AppPaths.cs                所有路径以 exe 所在目录为基准
    Configuration/             ConfigService（加载/原子写/热重载/v1 迁移/ACL）、JSON 选项
    Models/                    AppConfig / ScriptConfig / GlobalSettings / CrashPolicy / 依赖校验
    Processes/                 命令行构造、进程启动、输出读取、网络就绪、PE 子系统识别
    Supervision/               ScriptSupervisor（状态机）、心跳、熔断、会话恢复
    Monitoring/                资源采样、外部同名进程扫描
    Ipc/                       命名管道服务端/客户端、帧协议、命令与事件定义
    Logging/                   LogStore（按天分文件、按大小分片、按保留天数清理）
    Diagnostics/               诊断包（zip）打包
    Templates/                 内置脚本模板（内存常量）
  StartupTool.Service/         Windows 服务宿主
    ServiceHost.cs             服务 / 控制台两种生命周期
    SupervisionWorker.cs       依赖装配、开机启动顺序、事件汇聚
    Installation/              ServiceInstaller（sc.exe 注册/删除、失败重启、Run 键）
  StartupTool.App/             WPF 控制端（MVVM）
    Program.cs                 唯一入口，按参数分流
    MainWindow.xaml(.cs)       主窗口 + 托盘
    Views/ ViewModels/         各弹窗与视图模型
    Services/                  本地设置、本地化、输出缓冲
    Resources/ app.ico         双语资源（zh-CN / en-US 键集必须一致）+ 应用与托盘图标
tests/
  StartupTool.Core.Tests/      xUnit；引用 Core / Service / App（InternalsVisibleTo）
  install.ps1 / uninstall.ps1  发布安装 / 卸载脚本
docs/                          开发文档、项目文档、改动清单、测试与检查记录
```

## 4. 关键流程

**启动脚本**（`ScriptSupervisor`）：
按 `order` 遍历 `autoStart` 脚本 → 每脚本一把信号量按顺序执行：检查会话 → 前置等待（延迟 / 网络 / 依赖）→ 构造命令行 → 挂起创建进程 → 加入 Job → 恢复线程 → 启动输出读取与监控。

**停止脚本**：置用户停止意图 → 终止 Job → 等待进程组归零（最多 10 秒）→ 超时强杀 → 状态置 `Stopped`。

**崩溃/超限/心跳超时**：判定故障 → 按 `crashRestart` 策略与 `restartDelaySeconds` 重启 → 触发熔断计数。

**配置变更**：服务写入（`Mutate`）→ 广播给控制端；外部直接改盘（`FileSystemWatcher`）→ 重新加载并广播状态与整份配置。

**无登录会话**：脚本排队（不消耗重启次数、不熔断）；`SessionResumePoller` 每 5 秒轮询，登录后补发到用户桌面。

## 5. 编码约定

- 全部代码使用 C# 13 / .NET 10 语法；`nullable` 已开启，不要用 `!` 掩盖真实的空引用风险。
- 命名：类型/方法/属性 PascalCase；私有字段 `_camelCase`；异步方法以 `Async` 结尾。
- 注释与用户可见文案使用中文；界面文案必须走 `Resources/Strings.zh-CN.xaml` 与 `Strings.en-US.xaml`，**两边键集合保持一致**。
- 控制端 MVVM 使用 CommunityToolkit.Mvvm 源生成（`[ObservableProperty]` / `[RelayCommand]`），不要手写重复通知代码。
- P/Invoke 统一放在 `Core/Native/`，结构体显式 `StructLayout`，句柄与 `SafeHandle`/`Dispose` 成对出现。
- 日志写入失败必须吞掉，绝不能让日志异常影响守护逻辑；日志中不得出现心跳令牌明文。
- 不要为已删除的功能保留兼容分支或注释掉的旧代码。

## 6. 构建与测试

```powershell
dotnet build StartupTool.slnx -c Release
dotnet test tests\StartupTool.Core.Tests\StartupTool.Core.Tests.csproj
.\tests\build.bat           # 发布到 publish\ 并打包 build\StartupTool-Setup.msi（需 WiX v4）
.\tests\install.ps1         # 管理员：发布到 publish\ 并安装服务
.\tests\uninstall.ps1       # 管理员：卸载服务
```

发布参数固定为（缺一不可，否则单文件体积会从约 76 MB 涨到约 175 MB）：

```powershell
dotnet publish src\StartupTool.App\StartupTool.App.csproj -c Release -r win-x64 --self-contained true -o publish `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
```

提交前必须：`dotnet build` **0 警告 0 错误**，`dotnet test` 全绿。
注意：`BeatCommandContractTests` 里"令牌合法但无服务返回 11"的用例在本机已安装真实服务时会失败（默认管道被占用），属环境前提而非回归。

## 7. 易踩坑清单

| 坑 | 说明 |
|----|------|
| 管道帧上限 1 MB | 大文件（诊断包）走磁盘路径返回，不要塞进管道载荷 |
| `SemaphoreSlim` 不可重入 | 持锁路径内构造状态快照必须用 `BuildSnapshotUnderLock`，否则每次自锁 2 秒 |
| 用户令牌启动 GUI | 必须显式 `lpDesktop = "winsta0\default"`；CUI 程序才加 `SW_HIDE` + `CREATE_NO_WINDOW` |
| Job 归属判定 | 打不开进程句柄时应视为"未知"而不是"已退出"，避免误杀/误重启 |
| 相对路径 | 未配工作目录时默认取**脚本所在目录**（不是 cmd/powershell 的目录） |
| `DataGridColumn` 不在可视树 | 语言切换不会让 `DynamicResource` 重新求值，列头必须显式重刷 |
| 只读属性绑定 | `TextBox.Text` 默认 TwoWay，绑到只读计算属性会在布局阶段抛异常直接崩溃，需 `Mode=OneWay` |
| 语言切换 | 新增文案必须同步两个资源文件；派生文案需要显式 `RefreshLocalization()` |
| 安装路径含空格 | `sc create` 的 `binPath` 必须用内层引号包裹完整路径 |
| 卸载锁文件 | 卸载时若从安装目录运行 exe 会锁住自身，程序会先复制到 `%TEMP%` 再执行 |
| 配置 ACL | 配置目录内含令牌，仅 SYSTEM 与 Administrators 可访问；收紧失败必须写警告日志 |
| 服务停机 | 多脚本并行停止，避免串行累计超时被宿主强杀 |

## 8. 修改文档的约定

- 面向使用者的行为变化 → 更新 [README.md](README.md) 与 `docs/项目文档.md`。
- 架构、协议、内部时序变化 → 更新 `docs/开发文档.md`。
- 每次代码改动 → 在 `docs/改动清单.md` 登记。
- 归档类文档（代码检查记录、测试报告、测试交接文档）属于历史记录，**不要改写结论**，只在必要时追加新章节。
