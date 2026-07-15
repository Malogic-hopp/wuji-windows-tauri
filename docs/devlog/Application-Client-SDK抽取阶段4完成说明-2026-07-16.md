# Application / Client SDK 抽取阶段 4 完成说明

日期：2026-07-16  
状态：已完成  
实施分支：`codex/application-client-sdk`

## 1. 实施范围

本次完成《Application / Client SDK 抽取方案》的阶段 4：

- 在 Application 定义 Agent transport、文件 fallback、状态读取和进程控制端口；
- 在 Application 定义 Agent 状态、控制、进程和 transport health 用例接口；
- 将 IPC-first/file-fallback 编排从 WPF App 迁入 Application；
- 将 Windows Agent 可执行文件定位、启动、进程检测和强制终止实现迁入 Client；
- 用 Infrastructure adapter 接入现有 Named Pipe、runtime/health/control/options 文件存储；
- 将 transport health 暴露为框架无关的结构化合同；
- 将 WPF ViewModel 和 RefreshService 切换为依赖 Application Agent 接口；
- 修复既有测试的构造与程序集引用，并扩展架构边界测试。

本次未实施阶段 5 及后续工作。设置用例、登录自启、统一 `IWujiClient` facade、App composition root 收口、测试项目拆分、Bridge 和发布流程保持在后续阶段。

## 2. Application Agent 端口

新增 `ApplicationLayer.Abstractions.Agent` 端口：

- `IAgentTransport`：发送现有 `AgentIpcRequest` 并返回 `AgentIpcResponse`；
- `IAgentRuntimeStateReader`：读取 runtime state；
- `IAgentHealthStateReader`：读取 health state；
- `IAgentControlFallback`：读写文件控制命令；
- `IAgentOptionsReader`：读取 Agent options，用于心跳过期判断；
- `IAgentProcessController`：启动、检测、查询和强制终止 Agent 进程。

这些端口只依赖 Application/Core 合同、普通 .NET 类型、`Task` 和 `CancellationToken`。Application 项目仍为 `net8.0`，只引用 Core，不引用 WPF、WinForms、LiveCharts、Skia、Infrastructure 或 Client。

## 3. Application Agent 用例与合同

新增用例接口：

- `IAgentStatusService`
- `IAgentControlService`
- `IAgentProcessService`
- `IAgentTransportHealthService`

迁入或新增实现：

- `AgentStatusService`
- `AgentControlService`
- `AgentProcessService`
- `AgentTransportHealthService`

新增结构化 transport health 合同：

- `AgentTransportSource`
- `AgentTransportHealthSnapshot`

快照公开最后一次命令来源、最近 transport 成功时间、最近 fallback 时间、安全错误文本和可显示 endpoint 名称。WPF Diagnostics 再根据该快照生成显示文本，不需要读取 transport 实现的可变属性。

## 4. IPC 与 fallback 语义

阶段 4 保留原有协议和控制语义：

- 状态查询优先使用 Named Pipe，失败或响应无效时读取 runtime/health/control 文件；
- Pause、Resume、Stop、ReloadConfig、PruneData、ClearHistory 优先使用 IPC；
- IPC 连接失败时，文件 fallback 复用同一个 requestId，供 Agent 去重；
- request timeout 不再发出第二条文件命令，避免 Agent 已执行但前端重复操作；
- 调用方取消时不执行 fallback；
- IPC 返回 `Completed=false` 时直接保留 Agent 响应，不误判为断连；
- maintenance 命令继续使用 30 秒 completion timeout；普通命令继续使用 5 秒 timeout；
- Agent 未运行时，ReloadConfig、PruneData 和 ClearHistory 的原有提示与拒绝语义保持不变；
- graceful stop 继续先尝试 IPC，再依据进程/runtime state 判断是否需要同 requestId 文件 fallback，并保留轮询退出行为。

Named Pipe DTO、协议版本、pipe name、序列化格式以及 control/runtime/health 文件格式均未修改。

## 5. Infrastructure 适配

`NamedPipeAgentControlClient` 现在直接实现 Application 的 `IAgentTransport`，原 App 专用 `IAgentIpcClient` 已删除。

新增 `FileAgentStateAdapter`，复用现有基础设施组件并实现四个文件边界：

- `RuntimeStateStore` → `IAgentRuntimeStateReader`
- `AgentHealthStateStore` → `IAgentHealthStateReader`
- `AgentControlFileStore` → `IAgentControlFallback`
- `WindowsAgentOptionsStore` → `IAgentOptionsReader`

因此文件路径、JSON 格式、原子写入与备份行为仍由 Infrastructure 负责，Application 不知道 `WindowsAgentPaths` 或具体 store 类型。

## 6. Client Windows 进程实现

原 App `AgentProcessService` 中的 Windows 进程细节迁为 Client 的 `WindowsAgentProcessController`：

- Agent exe/dll 定位和环境变量覆盖；
- `ProcessStartInfo` 和 console 显示策略；
- prod/dev runtime channel 参数与环境变量；
- sanitized environment 构建；
- PID/runtime state 检测；
- 进程启动、等待 runtime state 和强制终止。

停止命令的 IPC/file fallback 编排没有留在 Windows controller 中，而是归入 Application `AgentProcessService`。Client 的释放或普通生命周期不会默认停止 Agent。

## 7. WPF 接线

`App.xaml.cs` 仍是本阶段的 composition root，但现在组装以下边界：

1. Infrastructure `NamedPipeAgentControlClient` 和 `FileAgentStateAdapter`；
2. Client `WindowsAgentProcessController`；
3. Application `AgentStatusService`、`AgentControlService`、`AgentProcessService` 和 `AgentTransportHealthService`；
4. WPF ViewModel、RefreshService、窗口、托盘和主题。

WPF 消费端已改为依赖 Application 接口：

- `MainWindowViewModel`：状态、控制、进程和 transport health；
- `SettingsViewModel`：状态和控制；
- `RefreshService`：状态和进程。

Legacy/Preview 双 shell、`--channel dev --ui-preview` gate 和启动顺序未改变。将底层对象创建整体移出 `App.xaml.cs` 属于阶段 6，不在本阶段提前实施。

## 8. 删除的 App 实现

以下旧实现已从 App 删除：

- `Services/AgentStatusService.cs`
- `Services/AgentControlService.cs`
- `Services/AgentProcessService.cs`
- `Services/AgentIpcStatusService.cs`

App 不再声明 `IAgentIpcClient`，Agent 编排实现由 Application 持有，Windows 进程实现由 Client 持有。

## 9. 架构测试

`ArchitectureBoundaryTests` 新增并固化以下规则：

1. App 项目必须引用 Client；
2. Agent ports、services 和 transport health contracts 必须属于 Application assembly；
3. `WindowsAgentProcessController` 必须属于 Client assembly 并实现 `IAgentProcessController`；
4. `NamedPipeAgentControlClient` 必须实现 `IAgentTransport`；
5. `FileAgentStateAdapter` 必须实现四个文件状态端口；
6. App Services 目录不得保留已迁移的四个 Agent 服务；
7. App 源码不得重新引用 `IAgentIpcClient`；
8. WPF ViewModel 和 RefreshService 必须接受 Application Agent 接口；
9. Application、Client 和 Infrastructure 继续通过 UI framework source/project marker 门禁；
10. Application assembly 继续不引用任何 UI framework assembly。

架构边界测试现为 21/21 通过。

## 10. 行为回归覆盖

既有 Named Pipe、控制命令和进程测试已切换到新的端口链路，并继续覆盖：

- IPC status success 与无效响应 fallback；
- Named Pipe connect timeout 和 request timeout；
- IPC 不可用时的文件 fallback；
- timeout/cancellation/`Completed=false` 时禁止重复 fallback；
- fallback requestId 与原 IPC requestId 一致；
- duplicate requestId 抑制；
- Pause、Resume、ReloadConfig、PruneData、ClearHistory；
- Agent 未运行时的命令结果；
- stop IPC、文件 fallback、已退出进程、残留 PID/runtime state；
- exe/dll 定位、console policy、环境清理和 dev channel 参数。

测试使用 `AgentTestServices` 统一组装真实 Infrastructure adapter、Application services 和 Client controller。状态测试使用 runtime-state-aware 的测试 process controller，避免本机正在运行的 Agent 干扰进程名 fallback 判定。

## 11. 验证结果

```text
dotnet build QuantifiedSelf.Windows.sln -c Release --no-restore
结果：0 warnings，0 errors

ArchitectureBoundaryTests
结果：21/21 passed

Category=Fast
结果：112/112 passed

Category=Integration
结果：403/403 passed

Category=Wpf
结果：没有匹配测试，命令成功退出

Full suite
结果：525/525 passed
```

全量测试由阶段 3 完成时的 518 增加到 525；新增 7 个执行项来自阶段 4 架构门禁扩展。

本阶段没有可见 UI 改动，因此未执行 preview UI 的视觉验收。所有验证均使用 Release 输出，未停止或覆盖本机正在运行的 Agent。

## 12. 未修改边界

本次未修改：

- SQLite schema、SQL 查询、数据库迁移或 Activity 用例；
- AppSettings/Agent options 的存储与保存流程；
- 登录自启注册表实现；
- Named Pipe 协议、数据文件格式或 runtime channel 命名；
- Agent state machine、采样循环或后台服务；
- `IWujiClient`/factory/DI composition facade；
- `publish/scripts/publish.ps1` 和发布目录结构。

## 13. 后续工作

下一步应单独实施阶段 5：抽取设置和登录自启。阶段 5 需要保持 prod/dev 数据目录、Run Key value name、启动参数、备份恢复和错误展示语义完全兼容。

完成阶段 5 后，再进入阶段 6 建立统一 Client facade，并把底层对象创建从 `App.xaml.cs` 移入 Client composition；届时 App 才能移除对 Core、Infrastructure 和 Agent 的直接项目引用，真正收敛为纯 UI host。
