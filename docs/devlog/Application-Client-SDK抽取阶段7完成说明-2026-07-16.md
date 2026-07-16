# Application / Client SDK 抽取阶段 7 完成说明

日期：2026-07-16

状态：已完成

实施分支：`codex/application-client-sdk`

## 1. 实施范围

本次完成《Application / Client SDK 抽取方案》的阶段 7：

- 建立 Core、Application、Infrastructure、Client、App、Agent 六个分层测试项目；
- 把可独立运行的 Client 与 App 测试从原混合测试程序集迁出；
- 为 Core、Application、Infrastructure、Agent 增加各层首批独立回归；
- 固化测试项目 target framework、ProjectReference 和 UI 框架边界；
- 修正 Fast、Integration、Wpf 分类；
- 删除 Client 公共 `WujiClientPaths` 对 Core `WindowsAgentPaths` 的隐式转换；
- 明确 Core 是稳定无 UI 领域合同，App 保留 Core 引用不是 Infrastructure 实现泄漏；
- 保持现有大型 `DataFlowTests` 全量回归，不在一次重构中拆散 1.2 万行稳定覆盖。

本阶段没有修改 IPC 协议、SQLite schema、设置格式、Agent 状态机、App 启动序列、XAML 或发布流程。

## 2. 分层测试项目

新增项目：

| 测试项目 | Target framework | 直接引用 | 分类 |
|---|---|---|---|
| `QuantifiedSelf.Windows.Core.Tests` | `net8.0` | Core | Fast |
| `QuantifiedSelf.Windows.Application.Tests` | `net8.0` | Application、Core | Fast |
| `QuantifiedSelf.Windows.Infrastructure.Tests` | `net8.0-windows` | Infrastructure、Core | Integration |
| `QuantifiedSelf.Windows.Client.Tests` | `net8.0-windows10.0.19041` | Client、Core | Integration |
| `QuantifiedSelf.Windows.App.Tests` | `net8.0-windows10.0.19041` + WPF | App、Core | Fast、Wpf |
| `QuantifiedSelf.Windows.Agent.Tests` | `net8.0-windows` | Agent、Core | Fast |

六个项目均已加入 solution 的 `tests` solution folder，并沿用现有 xUnit、Microsoft.NET.Test.Sdk 和 coverlet 版本。

Core/Application 测试工程没有：

- Windows TFM；
- `UseWPF` 或 `UseWindowsForms`；
- `System.Windows`；
- LiveCharts 或 Skia；
- App、Client、Infrastructure 或 Agent 项目引用。

因此 Application 的纯计算和用例测试可以在不加载 WPF desktop runtime 的快速通道执行。

## 3. 已迁移的现有测试

原 `QuantifiedSelf.Windows.Tests` 不再编译以下文件：

- `AdaptiveLayoutTests.cs` → App.Tests；
- `TodayPageTests.cs` → App.Tests；
- `InsightsTests.cs` → App.Tests；
- `AgentExeLocatorTests.cs` → Client.Tests；
- `WujiClientTests.cs` → Client.Tests。

测试源暂时通过 MSBuild `Compile Include` 链接到新项目，避免仅为移动文件制造大范围 namespace 和历史噪声；同一测试只由一个程序集编译，不会重复执行。

迁移后：

- App.Tests 独立执行 46 项；
- Client.Tests 独立执行 14 项；
- 原混合测试程序集由 540 项降为 486 项；
- solution 完整测试增加到 560 项，新增覆盖来自分层边界和各层首批回归。

## 4. 各层新增覆盖

Core.Tests 覆盖：

- runtime channel 规范化；
- prod/dev/custom channel 的稳定名称；
- Named Pipe channel 隔离；
- 用户身份只以哈希进入 pipe name。

Application.Tests 覆盖：

- `FocusMetricsCalculator` 无 UI runtime 计算；
- `HourActivityHeatmapCalculator` 返回完整 7×24 矩阵；
- `InsightSuggestionEngine` 在数据不足时不生成推断。

Infrastructure.Tests 覆盖：

- `RuntimeStateStore` 保持现有 JSON 往返格式；
- 写入后不遗留 `.tmp`；
- 缺失文件读取不创建目录。

Agent.Tests 覆盖：

- `ProcessedRequestCache` 重复请求识别；
- `ForegroundSamplePrivacyFilter` 标题遮罩和样本元数据保留；
- Agent assembly 不引用桌面 UI 框架。

## 5. 测试分类收口

按照仓库测试规则调整：

- `AgentExeLocatorTests` 使用真实文件系统，改为 `Category=Integration`；
- `RuntimeChannelTests` 移除整类 Fast 标签：纯解析/命令构建保持 Fast，环境变量、真实路径和进程启动信息测试标记为 Integration；
- `AdaptiveLayoutTests` 依赖 WPF layout 类型，改为 `Category=Wpf`；
- `SidebarWidthTests`、Today/Insights ViewModel 纯状态测试继续为 Fast；
- 清除 `VersionTests` 重复的同名 Fast trait。

现在 `Category=Wpf` 有 15 个实际执行项，不再是空筛选器。Dispatcher、Application.Current 或 WPF layout 测试不会进入 Core/Application 快速通道。

## 6. 架构门禁

`ArchitectureBoundaryTests` 新增：

1. solution 必须包含六个分层测试项目；
2. 每个测试项目必须使用约定 target framework；
3. 每个测试项目只能直接引用允许的产品层；
4. Core/Application 测试源码和项目文件不得出现 UI 框架 marker；
5. 原混合测试项目必须排除已迁出的五个测试文件；
6. `WujiClientPaths` 公共方法不得接收或返回 `WindowsAgentPaths`。

架构门禁目前 35/35 通过。

## 7. Client 公共路径边界

阶段 6 为兼容旧测试曾提供：

```csharp
public static implicit operator WujiClientPaths(WindowsAgentPaths paths)
```

阶段 7 已删除该公共隐式转换。替代 UI 和 SDK consumer 只能使用：

- `IWujiClient.Paths`；
- `WujiClientPaths.FromRoot(...)`；
- `WujiClientOptions.DataRootPath`。

`SettingsViewModel` 正式 composition constructor 继续接收 `WujiClientPaths`。旧大型混合回归通过 App assembly 的 internal 测试构造入口显式转换路径；这些入口只对 friend test assembly 可见，不属于 Client SDK 或 WPF 公共 composition API。

## 8. Core 合同决策

阶段 6 尝试移除 App 的 Core ProjectReference 时产生 113 个 WPF 临时编译错误，原因是 Application 的公开用例签名稳定使用：

- `AppSettings`、`WindowsAgentOptions`；
- `AgentEvent`、`AgentCommandResult`、`AgentActualState`；
- `ForegroundSample`、`AppSession`、活动统计与洞察模型；
- `IRefreshScheduler` 等无 UI 合同。

阶段 7 正式确认这些类型继续作为 Core 稳定领域合同。不会仅为让 App.csproj 少一个 ProjectReference 而复制同义 Application DTO，因为这会引入：

- Core/Application 双份模型映射；
- JSON、IPC 和 UI 状态语义漂移；
- 每次字段扩展的跨层同步成本；
- Bridge 前重复投影、Bridge 后再次序列化的无效复杂度。

最终允许 App 直接引用 Application、Client 和 Core，但有严格限制：

- Core 必须保持 `net8.0`、无 WPF/WinForms/LiveCharts/Skia；
- App 只使用 Core 的稳定领域合同；
- 路径创建、文件、IPC、SQLite、注册表和进程控制只能经 Client；
- App 不得引用 Infrastructure 或 Agent。

这个决策不影响 WinUI/Avalonia 直接引用 Client SDK，也不影响 Tauri/Electron 通过 Bridge 使用 Application/Core DTO。

## 9. 验证结果

```text
dotnet build QuantifiedSelf.Windows.sln -c Release --no-restore
结果：0 warnings，0 errors

ArchitectureBoundaryTests
结果：35/35 passed

Category=Fast（solution）
结果：114/114 passed

Category=Integration（solution）
结果：421/421 passed

Category=Wpf（solution）
结果：15/15 passed

Full suite（solution）
Core.Tests             6/6
Application.Tests      3/3
Infrastructure.Tests   2/2
Client.Tests          14/14
App.Tests             46/46
Agent.Tests            3/3
Legacy mixed Tests   486/486
合计                  560/560 passed
```

首次新增项目还原时，沙箱无法访问 NuGet 源；授权还原成功生成资产。最终通过一次本地缓存还原和 `--no-restore` 构建验证，仓库没有永久关闭 NuGet audit，也没有新增构建警告。

## 10. 未修改边界

本次未修改：

- IPC DTO、Named Pipe 命名、ProtocolVersion 和 fallback；
- SQLite schema、SQL 与数据保留规则；
- JSON 设置、runtime、health 和 control 文件格式；
- Agent 状态机、采样循环和生命周期；
- `App.xaml.cs` 启动/释放序列；
- Legacy/Preview 双 Shell 和 preview gate；
- XAML、样式、可见 UI 与 Dispatcher 行为；
- `publish/scripts/publish.ps1` 和发布目录。

## 11. 后续工作

阶段 8 应以最小 Bridge vertical slice 验证替代前端：

- initialize；
- Agent status/start/pause/resume/stop；
- Dashboard overview；
- Settings get/save。

现有 `DataFlowTests.cs` 仍是大型混合回归文件。后续维护某一领域时，应把相关测试迁入对应分层项目并复用共享 helper；不需要在阶段 8 开始前一次性重写全部历史测试。
