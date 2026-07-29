# Application / Client SDK 抽取阶段 3 完成说明

日期：2026-07-16
状态：已完成
实施分支：`codex/application-client-sdk`

## 1. 实施范围

本次完成《Application / Client SDK 抽取方案》的阶段 3：

- 在 Application 定义 Activity 数据查询端口；
- 在 Application 定义供 UI 消费的 Activity 用例接口；
- 在 Infrastructure 增加 SQLite 查询适配器；
- 将九个只读数据/统计/洞察服务由 App 迁移至 Application；
- 让 WPF 数据 ViewModel 依赖 Application 用例接口；
- 解除 Heatmap Application 服务对 WPF ViewModel 和图表类型的反向依赖；
- 扩展项目引用、程序集归属、UI 隔离和 WPF 消费边界的架构测试；
- 复用原有 SQLite 集成测试验证迁移前后查询与聚合行为。

本次没有实施阶段 4 及后续工作。Agent IPC、文件 fallback、进程控制、设置、自启、Client facade 和发布流程保持不变。

## 2. Application 查询端口

新增 `ApplicationLayer.Abstractions.Data` 查询端口：

- `IOverviewQueryPort`
- `IDiagnosticsQueryPort`
- `ISampleQueryPort`
- `ISessionQueryPort`
- `IAppUsageQueryPort`
- `IDailyStatsQueryPort`

端口只使用 Core 模型、普通 .NET 类型、`Task` 和 `CancellationToken`，不暴露：

- SQLite connection、command 或 database path；
- `WindowsAgentPaths`；
- WPF、WinForms、LiveCharts 或 Skia 类型；
- ViewModel、Dispatcher 或 `ObservableCollection`。

## 3. Application 用例接口与服务

新增供表现层调用的用例接口：

- `IOverviewDataService`
- `IDiagnosticsDataService`
- `ISamplesDataService`
- `ISessionsDataService`
- `IAppsDataService`
- `IDailyStatsService`
- `IWeeklyTrendService`
- `IFocusInterruptionInsightService`
- `IHourActivityHeatmapService`

由 App 迁移至 `src/QuantifiedSelf.Windows.Application/Activity/` 的实现：

- `OverviewDataService`
- `DiagnosticsDataService`
- `SamplesDataService`
- `SessionsDataService`
- `AppsDataService`
- `DailyStatsService`
- `WeeklyTrendService`
- `FocusInterruptionInsightService`
- `HourActivityHeatmapService`

迁移后的服务通过构造函数接收查询端口，不再创建 Infrastructure query service。日期和当前时间通过可注入的 `TimeProvider` 获取，默认行为仍为 `TimeProvider.System`。

`DailyStatsService` 使用的本地日边界和 session overlap 缩放逻辑已作为 Application 内部纯计算保留，算法、舍入方式和异常时返回空结果的行为没有改变。

## 4. Infrastructure SQLite 适配器

Infrastructure 现在引用 `Application + Core`，新增：

`src/QuantifiedSelf.Windows.Infrastructure/Database/SqliteActivityQueryAdapter.cs`

该适配器实现全部六个查询端口，并委托现有查询实现：

- `OverviewQueryService`
- `DiagnosticsQueryService`
- `SampleQueryService`
- `SessionQueryService`
- `AppUsageQueryService`
- `DailyStatsQueryService`

本次没有修改这些查询服务的 SQL、SQLite schema、排序、limit、日期范围、空数据库处理或只读连接方式。

## 5. Heatmap 反向依赖修复

迁移前：

```text
HourActivityHeatmapService
  -> HourActivityHeatmapViewModel
  -> LiveCharts / Skia / WPF 表现模型
```

迁移后：

```text
Application HourActivityHeatmapService
  -> HourActivityHeatmapResult
  -> IReadOnlyList<DailyHourActivityPoint>

WPF DashboardViewModel
  -> HourActivityHeatmapViewModel
  -> LiveCharts / Skia 图表投影
```

新增 `ApplicationLayer.Contracts.Activity.HourActivityHeatmapResult`，只包含 `Today` 和纯数据点集合。颜色、tooltip、坐标轴、series、`ObservableCollection` 和可访问性文本仍由 WPF ViewModel 构建。

## 6. WPF 表现层调整

以下 ViewModel 已改为接收 Application 用例接口：

- `AppsViewModel`
- `SamplesViewModel`
- `SessionsViewModel`
- `InsightsViewModel`
- `DashboardViewModel`
- `MainWindowViewModel`
- `SettingsViewModel`

`App.xaml.cs` 当前只增加阶段 3 的过渡组装：创建一个 `SqliteActivityQueryAdapter`，并将其注入 Application 用例。完整 Client composition root 切换仍留到阶段 6，因此 App 当前仍可直接引用 Infrastructure。

没有改变：

- Legacy/Preview 双 Shell 选择；
- Dispatcher refresh scheduler；
- 页面绑定、命令和 UI 线程切换方式；
- LiveCharts/Skia 的 WPF 图表表现。

## 7. 测试和架构门禁

阶段 3 新增或扩展的 Fast 架构检查覆盖：

1. Infrastructure 只能引用 Application 和 Core；
2. Application、Infrastructure、Client 源码和项目文件不得包含 UI 框架引用；
3. Activity 查询端口、合同和用例必须归属于 Application assembly；
4. SQLite adapter 必须归属于 Infrastructure 并实现全部查询端口；
5. Heatmap 用例必须返回 Application 纯合同；
6. App Services 目录不得保留已迁移的九个服务；
7. WPF 数据 ViewModel 必须接受对应的 Application 用例接口。

原有数据流测试已通过测试专用工厂改为创建真实 `SqliteActivityQueryAdapter + Application service`，因此既有 SQLite 查询、DailyStats、WeeklyTrend、Insights、Heatmap 和 ViewModel 回归用例均覆盖新的端口链路。

## 8. 验证结果

```text
dotnet build QuantifiedSelf.Windows.sln -c Release --no-restore
结果：0 warnings，0 errors

Category=Fast
结果：105/105 passed

Category=Integration
结果：403/403 passed

Category=Wpf
结果：没有匹配测试，命令成功退出

Full suite
结果：518/518 passed
```

全量测试由阶段 0～2 完成时的 511 增加到 518；新增 7 个测试执行项来自阶段 3 架构门禁扩展。

## 9. 边界确认

本次未修改：

- Named Pipe client、IPC DTO、pipe name、timeout 或 requestId 语义；
- control/runtime/health 文件及 IPC-first/file-fallback 编排；
- Agent 进程定位、启动、停止和生命周期；
- SQLite schema、SQL 查询文本或数据库迁移；
- Settings 保存和 Agent options reload；
- 登录自启注册；
- Client facade 和 `IWujiClient`；
- `publish/scripts/publish.ps1` 和发布目录结构。

## 10. 后续工作

下一步应单独实施阶段 4：抽取 Agent 控制、状态、transport 和 Windows 进程端口。阶段 4 需要独立覆盖 IPC success、connect timeout、request timeout、Agent 未运行、文件 fallback、重复请求、残留 PID 和 stop 后退出，不应与本次只读数据迁移混合。
