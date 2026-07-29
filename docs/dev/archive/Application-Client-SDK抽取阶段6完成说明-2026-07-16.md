# Application / Client SDK 抽取阶段 6 完成说明

日期：2026-07-16

状态：已完成

实施分支：`codex/application-client-sdk`

## 1. 实施范围

本次完成《Application / Client SDK 抽取方案》的阶段 6：

- 建立统一的 `IWujiClient` facade、五组 feature client、options、context、paths 和 factory；
- 将 Windows 路径、JSON store、Named Pipe、Agent 状态与进程控制、SQLite 查询、设置和登录自启的组装移入 Client；
- 为 WPF ViewModel 与刷新服务增加 feature client 构造入口；
- 将 `App.xaml.cs` 收口为 Client 初始化以及 WPF window、tray、theme、Dispatcher 和 ViewModel 接线；
- 从 App 项目移除 Infrastructure、Agent 直接项目引用以及不再需要的 logging 包；
- 增加 facade 初始化、设置、取消、释放语义和架构边界测试；
- 保持 Legacy/Preview 双 Shell、prod/dev channel 隔离和现有发布流程。

本阶段没有修改 IPC 协议、SQLite schema、设置格式、Agent 状态机或发布脚本。

## 2. Client facade

新增公开入口：

- `IWujiClient`
  - `Agent`
  - `Activity`
  - `Diagnostics`
  - `Settings`
  - `Startup`
  - `Context`
  - `Paths`
  - `InitializeAsync`
- `IAgentClient`
- `IActivityClient`
- `IDiagnosticsClient`
- `ISettingsClient`
- `IStartupClient`
- `WujiClientOptions`
- `WujiClientContext`
- `WujiClientPaths`
- `WujiClientFactory`

feature clients 只聚合阶段 3～5 已抽取的 Application 用例接口，不向 WPF 暴露 Infrastructure adapter。Client 继续是 Windows-only SDK，但不引用 WPF、WinForms、LiveCharts、Skia 或 Dispatcher。

`WujiClientOptions.FromLaunchOptions` 把 WPF 已解析的启动参数映射为 channel、Agent console 和 startup context。测试还可显式提供 data root、用户身份和进程路径，避免访问生产目录、真实 SID 或真实启动项。

## 3. composition factory

`WujiClientFactory.Create` 现在集中创建并连接：

```text
WindowsAgentPaths
  -> runtime / health / control / settings stores
  -> WindowsSettingsStoreAdapter
  -> Named Pipe transport + file fallback
  -> Agent Application services
  -> WindowsAgentProcessController
  -> SqliteActivityQueryAdapter + Activity Application services
  -> Startup registry + registration service
  -> IWujiClient feature clients
```

因此 App 不再知道或创建以下实现：

- `RuntimeStateStore`、`AgentHealthStateStore`、`AgentControlFileStore`；
- `NamedPipeAgentControlClient`、pipe name 或 IPC options；
- `FileAgentStateAdapter`、`WindowsAgentProcessController`；
- `SqliteActivityQueryAdapter`；
- `WindowsSettingsStoreAdapter`；
- `RegistryStartupRegistry`；
- 各 Application service 的具体组合关系。

Named Pipe 构造失败时仍由 Client 记录 transport fallback，并继续使用既有文件 fallback；没有改变协议或超时行为。

## 4. 初始化与释放语义

`InitializeAsync` 的行为：

- 创建当前 channel 的 config、data、logs 和 runtime 目录；
- 重复调用保持幂等；
- 在已取消 token 下不创建目录并抛出 `OperationCanceledException`；
- Client 已释放后拒绝再次初始化。

`DisposeAsync` 只释放当前前端对 SDK 的所有权，不代表停止后台采集。它不会：

- 调用 Agent Stop；
- 写入 `agent_control.json` stop fallback；
- 终止 Agent 进程。

停止 Agent 仍必须由用户通过 `IAgentClient.Control`/现有 UI 命令明确触发。这保证 WPF、WinUI、Avalonia 或 Bridge 退出时不会意外停止独立 sidecar。

SDK 不持有 WPF timer，不捕获 Dispatcher 或 UI SynchronizationContext。刷新和状态轮询仍由 WPF 的 `DispatcherRefreshScheduler` 驱动。

## 5. WPF composition root

`App.xaml.cs` 当前启动序列为：

1. 解析 `StartupLaunchOptions`；
2. 通过 `WujiClientFactory` 创建并初始化 Client；
3. 通过 `client.Settings` 安全读取启动设置并应用 Preview 主题；
4. 将 feature clients 注入 ViewModel 和 `RefreshService`；
5. 按 `UsePreviewUi` 选择 `MainWindow` 或 `LegacyMainWindow`；
6. 接线托盘、CloseToTray、MinimizeToTray 和 Dispatcher scheduler；
7. 窗口真正关闭时停止 UI 状态轮询并释放 Client。

保留行为：

- 默认继续使用 `LegacyMainWindow`；
- `--channel dev --ui-preview` 继续是 Preview Shell gate；
- dev 窗口标题继续使用 channel product display name；
- hidden autostart 继续显式初始化 ViewModel，因为窗口 `Loaded` 不会触发；
- tray 的启停、暂停、恢复命令继续调用相同 Application 用例；
- CloseToTray 和 MinimizeToTray 的运行时设置更新方式未变。

## 6. WPF 消费边界

以下类型增加 Client feature interface 构造入口：

- `SamplesViewModel(IActivityClient)`；
- `SessionsViewModel(IActivityClient)`；
- `AppsViewModel(IActivityClient)`；
- `DashboardViewModel(IActivityClient)`；
- `InsightsViewModel(IActivityClient)`；
- `SettingsViewModel(ISettingsClient, IAgentClient, IDiagnosticsClient, WujiClientPaths, IStartupClient)`；
- `MainWindowViewModel(IAgentClient, IActivityClient, IDiagnosticsClient, ..., ISettingsClient, ..., IStartupClient, ...)`；
- `RefreshService(IAgentClient, ...)`。

原有窄 Application 接口和 delegate 构造入口暂时保留，供现有 ViewModel 单元测试使用；它们不创建 Infrastructure 实现，也不构成反向依赖。

`SettingsViewModel` 的路径依赖由 `WindowsAgentPaths` 改为只读的 `WujiClientPaths`，避免 WPF 直接接触 Core 的路径构造规则。

## 7. 项目引用与 Core 残留

App 项目的直接 WUJI 引用当前为：

```text
QuantifiedSelf.Windows.Application
QuantifiedSelf.Windows.Client
QuantifiedSelf.Windows.Core
```

已移除：

- `QuantifiedSelf.Windows.Infrastructure`；
- `QuantifiedSelf.Windows.Agent`，包括原 `ReferenceOutputAssembly="false"`；
- App 中不再使用的 `Microsoft.Extensions.Logging.Abstractions` 包。

原方案期望阶段 6 同时移除 Core。实际尝试移除时，WPF 临时编译项目暴露出 Application 现有公开签名仍使用大量 Core 稳定领域模型，包括 `AppSettings`、`WindowsAgentOptions`、`AgentEvent`、`AgentActualState`、`ForegroundSample`、`AppSession`、统计摘要和 `IRefreshScheduler`。直接删除引用会产生 113 个编译错误。

本阶段选择保留显式 Core 引用，而不是添加隐式 DLL 引用或复制领域类型。原因是：

- Core 为 `net8.0` 且没有 WPF/UI 框架依赖；
- App 只消费领域合同，不消费 Core 中的运行时实现组合；
- Infrastructure 和 Agent 的实现依赖已完全消除；
- 一次性复制全部 DTO 会扩大阶段 6 风险，并制造双份领域语义。

因此当前 App 已是“只含 UI 与领域合同消费”的表现层，但还未满足最终的“仅 Application + Client 两个 ProjectReference”形式门禁。阶段 7 应决定哪些 Core 类型保持稳定公共合同、哪些投影为 Application DTO，并在完成投影后移除 App 的 Core ProjectReference。

## 8. 架构与生命周期测试

新增 `WujiClientTests`，覆盖：

1. dev channel factory、全部 feature clients、context 与目录初始化；
2. 现有 AppSettings JSON store 读写往返；
3. 损坏启动设置返回安全默认值；
4. `DisposeAsync` 不发送 Agent stop fallback；
5. Client 释放后拒绝重新初始化；
6. 初始化取消不创建 runtime 目录。

架构门禁新增并固化：

1. App 仅允许引用 Application、Client 和暂留的 Core；
2. facade、feature interfaces、options、context 和 paths 必须属于 Client assembly；
3. App C# 源码不得出现 Infrastructure/Agent 命名空间；
4. App C# 源码不得出现底层 store、Named Pipe、SQLite、process controller、registry 或 settings adapter 类型；
5. WPF ViewModel 与刷新服务必须接受对应 feature client interface；
6. Application、Client 和 Infrastructure 继续不得引用 UI 框架。

## 9. 验证结果

```text
dotnet build QuantifiedSelf.Windows.sln -c Release --no-restore
结果：0 warnings，0 errors

ArchitectureBoundaryTests + WujiClientTests
结果：35/35 passed

Category=Fast
结果：121/121 passed

Category=Integration
结果：409/409 passed

Category=Wpf
结果：没有匹配测试，命令成功退出

Full suite
结果：540/540 passed
```

全量测试由阶段 5 的 531 增加到 540；新增执行项来自 facade 生命周期/设置集成测试和架构门禁。

本阶段没有 XAML、样式或可见 UI 改动，因此未执行 Preview UI 视觉验收。测试使用临时目录、测试用户身份和 fake 进程路径，没有访问生产数据或修改当前用户真实 Run Key。

## 10. 未修改边界

本次未修改：

- Named Pipe DTO、ProtocolVersion、连接/请求 timeout 和文件 fallback 格式；
- runtime、health 和 control 文件格式；
- SQLite schema、SQL 查询与数据保留规则；
- AppSettings、WindowsAgentOptions JSON schema、备份与原子替换算法；
- Agent 状态机、采样循环和 BackgroundService；
- Legacy/Preview shell 选择与 Preview promotion gate；
- `publish/scripts/publish.ps1`、Agent 独立发布和 `App/Agent/` 目录结构。

## 11. 后续工作

阶段 7 适合处理测试与架构收口：

- 拆分或至少按层整理现有单一测试项目；
- 清理阶段性 compatibility constructors/wrappers；
- 明确 Core 稳定领域合同与 Application DTO 的最终边界；
- 完成必要 DTO 投影后移除 App 的 Core ProjectReference；
- 保持 architecture、Fast、Integration、Wpf 和 full suite 门禁。

阶段 8 再实现最小 Bridge vertical slice，验证 Tauri/Electron 的进程打包、权限、退出顺序和 Agent 独立运行；发布脚本在该阶段按明确方案调整，而不是在阶段 6 顺带修改。
