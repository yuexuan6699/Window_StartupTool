# D 类 / E 类 收尾清单（核实于 2026-10-18）

> 依据：用户指出 `改动清单.md` 里 D 类（D1–D9）、E 类（E1–E4）与 H8 仍标"待办"，与实际不符，要求继续完成。
> 核实方法：对 `src\**`（排除 `obj`/`bin`）逐条 grep **消费点**——字段"存在"不等于能力"生效"，
> 必须找到真正使用该字段的业务逻辑（ScriptSupervisor / ProcessLauncher / IpcServer），才算已实现。

---

## 一、三态核实台账（逐条带证据）

| 编号 | 能力 | 结论 | 证据（消费点，非字段定义） |
|---|---|---|---|
| **D1** | 开机启动 / 允许手动启动分离 | ✅ **已完成** | `ScriptConfig.cs:25/28` 两字段；`ScriptSupervisor.cs:186` `StartAllAutoAsync` 按 `AutoStart` 过滤、`ScriptSupervisor.cs:235` `if (manual && !cfg.ManualEnabled)` 拒绝手动启动；`ConfigService.cs:290-291` 旧 `Enabled` 迁移拆为 `AutoStart=old.Enabled` + `ManualEnabled=true`；`IpcServer.cs:331` 亦按 `ManualEnabled` 过滤 |
| **D2** | 崩溃自愈 | ✅ **已完成** | `CrashPolicy`（`ScriptConfig.cs:67`）+ `ScriptSupervisor` WatchLoop：Job 内进程归零且非用户主动停止 → 按策略延时重启；M3 集成测试「崩溃自愈」覆盖（含 M3 踩坑 4 的旧 cts 吞重启根因修复） |
| **D3** | 启动前等待系统就绪 | ❌ **未完成（仅全局）** | 全局仅有 `SupervisionWorker.cs:19/66` 的 `BootReadyDelay=10s`；**每脚本的 `InitialDelaySeconds`/`WaitForNetwork` 只在 `ScriptConfig.cs:43/46`（定义）、`IpcServer.cs:408-409`（服务端保留）、`MainViewModel.cs:429-430`（克隆）出现，ScriptSupervisor / ProcessLauncher 里**零消费点**；编辑窗"启动设置"卡也未暴露这两个字段（`ScriptEditWindow.xaml:128-142` 只有 AutoStart/ManualEnabled/SystemSession/StartInterval） |
| **D4** | 脚本依赖与间隔 | ⚠️ **部分完成** | 「启动间隔」✅ 生效：`ScriptSupervisor.cs:199-200` `if (cfg.StartIntervalSeconds > 0) await Task.Delay(...)`；「依赖 `DependsOn`」❌ 无消费点（仅 `ScriptConfig.cs:49` 定义、`IpcServer.cs:406` 保留、`MainViewModel.cs:431` 克隆、`ConfigService.cs:317` 初始化空表），编辑窗也未暴露 |
| **D5** | 每脚本工作目录 + 环境变量 | ✅ **已完成** | `LaunchOptions.WorkingDirectory` → `ProcessLauncher.ResolveWorkingDirectory`（`ProcessLauncher.cs:189-199`，显式配置→脚本目录→宿主目录）；`EnvironmentVariables` → `EnvironmentBlock.Build`（`:24`）与 `RebuildWithOverrides`（`:58`，在用户环境块上叠加） |
| **D6** | 每脚本提权运行 | ❌ **未完成** | `RequireElevation` 仅 `ScriptConfig.cs:40`（定义）、`IpcServer.cs:407`（保留）、`MainViewModel.cs:428`（克隆）；实际启动 `ProcessLauncher.LaunchAsUser`（`:49-76`）用的是**登录用户令牌**（中等完整性），全代码库**没有任何提权/runas 路径** |
| **D7** | 导入/导出配置 | ❌ **未做** | 全库 grep `Import|Export` 无任何业务命中（仅 `OutputBufferService.cs:49` 注释"预留：恢复显示/导出用"） |
| **D8** | 导出诊断包 | ❌ **未做** | 无 zip/诊断打包实现；IPC 协议 `IpcTypes` 里无相关命令 |
| **D9** | 脚本模板库 | ❌ **未做** | 无模板定义/资源；`项目文档.md` §5.3 仍写"…M3/M6 交付时补充…（模板库内置心跳示例）" |
| **E1** | `项目文档.md` | ✅ **已完成** | 文件存在且持续维护（含定位/安装卸载/使用说明/配置字段表/心跳接入/日志/FAQ/变更记录） |
| **E2** | `开发文档.md` | ✅ **已完成** | 文件存在且持续维护（架构、IPC 协议、心跳契约、构建与调试、变更日志、7.1 第七轮备忘） |
| **E3** | 每改代码同步更新 E1+E2 | 🔄 **常驻（执行中）** | 已按此约定执行多轮（本轮同样同步 4 份文档 + 项目记忆） |
| **E4** | `改动清单.md` 随进度更新 | 🔄 **常驻（本次修）** | 正在本轮回填 D/E/H 状态；此前 D/E 台账一直未回填，造成"明明做了却标待办"的错觉 |
| **H8** | 心跳发送要求 + bat/PS/Python 示例 | ❌ **未完成** | `开发文档.md` §5.1（协议）§5.2（超时行为）已写，但**缺脚本示例**；`项目文档.md` §5.3 仍是占位句；退出码表（0/10/11/12/13/14/15）已在项目文档 §5.1 给出 |

**总结**：D1/D2/D5 与 E1/E2 其实早已落地（台账漏回填）；**真正没做的是 D3、D4 的 `DependsOn` 部分、D6、D7、D8、D9、H8**。

---

## 二、待开发项清单（含落点与默认设计）

> 每条给出：目标 / 落点 / **默认设计假设**（如你有异议请指出）/ 测试 / 需同步的文档。

### D3 每脚本"启动前等待就绪"
- **目标**：`initialDelaySeconds`（固定延迟）与 `waitForNetwork`（等网络可用）在**启动前**真正生效；编辑窗可配。
- **落点**：
  - `Core/Processes/NetworkReadiness.cs`（新增）：`IsAvailable()` = `NetworkInterface.GetIsNetworkAvailable()`；`WaitAsync(timeout, ct)` 轮询（间隔 2s）。
  - `Core/Supervision/ScriptSupervisor.cs`：`StartUnderLockAsync` 在 `LaunchCore` 之前插入「延迟 + 等网络」段（沿用无 token 等待的既有写法，避免 M3 踩坑 4 的 cts 吞掉问题）。
  - `App/ViewModels/ScriptEditViewModel.cs` + `Views/ScriptEditWindow.xaml`：启动设置卡新增两个控件。
  - `Resources/Strings.zh-CN.xaml` / `Strings.en-US.xaml`：新增键（两边键集合必须一致）。
- **默认假设**：`waitForNetwork` 判定用"系统报告网络可用"即可，**不引入新的探测主机配置项**；等待上限固定 60s，超时则**跳过该脚本并告警**（进"最近故障"，不启动）。
- **测试**：xUnit——①配置 3s 延迟，断言启动时刻被推迟；②`NetworkReadiness` 的轮询/超时逻辑（注入假探测函数）。
- **文档**：E1 配置字段表补语义；E2 启动时序小节。

### D4 `DependsOn` 依赖就绪
- **目标**：启动某脚本前，等它 `dependsOn` 的脚本进入**运行中**（D3 就绪判定若启用则等"就绪"）；编辑窗可多选依赖。
- **落点**：`ScriptSupervisor` 启动前置等待段（带超时）；`ScriptEditWindow.xaml` 新增"依赖脚本"多选列表（数据来自当前脚本清单，排除自己与已选环，避免自依赖/环依赖）。
- **默认假设**：等待超时 60s → **放弃本次启动并告警**（不静默继续）；检测到**环依赖**时拒绝保存并在编辑窗提示。
- **测试**：xUnit——依赖未就绪不启动 / 依赖就绪后启动 / 依赖超时告警 / 环依赖被拒。
- **文档**：E1 字段语义 + E2 启动时序。

### D6 每脚本提权运行（★需你选方案）
- **现状**：`LaunchAsUser` 用登录用户令牌（中等完整性），**服务端无法弹 UAC**，故"提权"必须有明确语义。
- **候选方案**：
  - **(a) 以 SYSTEM 身份在用户桌面启动**（`Launch` + `lpDesktop=winsta0\default`，用服务自身 SYSTEM 令牌）。最简单、无 UAC、脚本**确实具备管理员权限**；代价：进程身份是 SYSTEM（环境变量、写用户目录、HKCU 行为与"以该用户提权"不同）。UI 文案需写成"以最高权限运行（SYSTEM）"。
  - **(b) 用户令牌 + 提升完整性**：需要用户在交互桌面点 UAC 同意，服务端要拉起 consent UI，可靠性差、易卡住。
  - **(c)** 仅当该用户本身是管理员时，借 `ShellExecuteEx(runas)` 在用户桌面触发 UAC——机械上仍属 (b) 类。
- **默认（待确认）建议**：**选 (a)**。
- **测试**：真机——提权脚本能写到 `C:\Windows` 等受保护位置（普通脚本应失败）。

### D7 导入/导出配置
- **目标**：一键导出脚本+全局设置到文件；另一台机器导入。
- **落点**：
  - `Core/Ipc/IpcTypes.cs`：新增 `exportConfig` / `importConfig` 命令；`IpcServer` 实现两分支。
  - 导出：服务端序列化 `AppConfig`，**默认剔除心跳令牌**（勾选"包含令牌"才带）。
  - 导入：服务端校验（`version`、字段白名单、`Id` 唯一化、`dependsOn` 引用有效性）→ 原子写 + `.bak` + 热应用。
  - `App`：工具栏新增"导入/导出配置"，用 WPF 内置 `Microsoft.Win32.OpenFileDialog/SaveFileDialog`（**无新依赖**）。
  - 令牌重新生成：导入时若不含令牌，服务端为每个脚本生成新令牌。
- **默认假设**：导入=**整体替换**（脚本+全局设置），导入前自动备份；文件扩展名 `.startuptool.json`；导出默认**不含**令牌。
- **测试**：端到端——导出→导入回环（字段全等、令牌按选项处理）、非法/高版本文件被拒且旧配置保留。
- **文档**：E1 使用说明 + FAQ；E2 IPC 协议章节。

### D8 导出诊断包
- **目标**：一键打包配置+日志+运行快照，便于排障。
- **落点**：
  - `Core/Ipc/IpcTypes.cs`：新增 `exportDiagnostics`；`IpcServer` 实现。
  - 服务端把 zip 写到 `{exe目录}\logs\diagnostics\diag-yyyyMMdd-HHmmss.zip`（**不经命名管道传输大文件**——管帧有 1MB 上限），返回路径；客户端据此 `explorer /select` 定位。
  - zip 内容：`scripts.json`（**心跳令牌打码为 `***`**）、全局设置、`logs\service\` 最近文件、运行中脚本状态快照、环境信息（OS 版本/工具版本/管道与 ACL 状态）。
- **默认假设**：令牌打码；日志内容原样打包（不脱敏业务数据，需在 UI 上提示"含脚本输出，可能含敏感信息"）。
- **测试**：xUnit——zip 条目集合断言 + 令牌已被打码断言。
- **文档**：E1 FAQ（排障流程）；E2 诊断包结构。

### D9 脚本模板库
- **目标**：内置常用模板，减少手填。
- **落点**：`Core/Templates/ScriptTemplates.cs`（纯内存内置列表）+ `App` 添加脚本处提供"从模板新建"下拉；模板项 = 名称/路径/参数/说明。
- **默认模板集**：① 心跳示例（bat / PowerShell / Python 三份，与 H8 同源）② 映射网络盘 ③ 清理临时文件 ④ 启动/停止 Windows 服务 ⑤ 等待网络后启动指定程序。
- **默认假设**：模板只预填字段（用户仍可在弹窗改）；**不落盘模板文件**（单文件发布友好）。
- **测试**：xUnit——模板字段合法、模板新建走"新建"分支（Id 为空）、模板集合不为空且路径非空。
- **文档**：E1 使用说明（模板一览）；E2 模板模块位置。

### H8 心跳接入文档与示例
- **目标**：`开发文档.md` 里把《脚本心跳包发送要求》写全；`项目文档.md` §5.3 给出可直接用的 bat/ps1/python 示例（与 D9 模板同源）。
- **落点**：两份文档；示例需含"取指令退出码并据此告警/停发"的建议写法。
- **测试**：文档评审（无代码）；示例里的命令行须与 `--beat` 实际参数一致（`StartupTool.exe --beat <64位hex> <Unix秒>`）。

---

## 三、建议实施顺序

1. **D9 + H8**（纯新增、风险最低，且 H8 的示例正是 D9 的模板内容 → 一起做）
2. **D3**（supervisor 启动前置段 + UI 两控件）
3. **D7 + D8**（IPC 新命令，服务端为主）
4. **D4**（依赖就绪 + 环检测 + UI 多选）
5. **D6**（按你选的方案落地）
6. **回填**：`改动清单.md` 的 D/E/H 状态 → `项目文档.md` / `开发文档.md` 变更记录 → 项目记忆

每步都要：`dotnet build` Debug/Release **0 警告 0 错误** + `dotnet test` 全绿（当前 104 项，新增用例后应递增） + 同步文档。

---

## 四、需你确认的决策点

| # | 决策 | 我的建议（默认） |
|---|---|---|
| 1 | **D6 提权语义**：(a) 以 SYSTEM 身份在用户桌面运行 / (b) 用户令牌+UAC / (c) 仅管理员用户可用 | **(a)** |
| 2 | D3 是否需要"自定义探测条件"（如 HTTP 探活、端口探活） | 本轮**不做**，只做"延迟 + 等系统网络可用" |
| 3 | D4 依赖等待超时后的行为 | **放弃本次启动并告警**（不静默继续） |
| 4 | D7 导出是否默认**不含**心跳令牌 | **不含**（换机器应重新生成），另给"包含令牌"复选 |
| 5 | D8 诊断包里心跳令牌是否打码 | **打码**为 `***` |
| 6 | 是否引入第三方包（如 `System.IO.Compression`/`DotNetZip`）实现 D8 | **不引入**（.NET 10 自带 `System.IO.Compression.ZipFile`） |

---

## 五、核实过程中的一处分歧（如实记录）

"资深评审"建议里曾判断 **D1–D6 都已实现、只需做 D7/D8/D9**。但代码级证据显示：
`InitialDelaySeconds` / `WaitForNetwork` / `DependsOn` / `RequireElevation` **只有字段定义与"保留不覆盖"，没有任何业务消费点**，
编辑窗也未暴露 → D3、D4（依赖部分）、D6 **属于真正未完成**。本清单按**代码证据**（而非该判断）编排。
