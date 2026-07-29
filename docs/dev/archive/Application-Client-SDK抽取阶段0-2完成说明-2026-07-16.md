# Application / Client SDK 抽取阶段 0～2 完成说明

日期：2026-07-16  
状态：已完成  
实施分支：`codex/application-client-sdk`

## 1. 本次实施范围

本次完成《Application / Client SDK 抽取方案》的阶段 0～2：

- 建立调整前的构建、测试行为基线；
- 增加跨项目依赖和 UI 框架隔离的架构测试；
- 创建 `QuantifiedSelf.Windows.Application` 与 `QuantifiedSelf.Windows.Client` 项目；
- 将两个 Agent 状态 DTO 从 App 迁移到 Application；
- 将三个无 UI、无 I/O 的纯计算模块从 App 迁移到 Application；
- 修复 App 与测试项目的命名空间及项目引用；
- 运行分类测试和全量测试。

本次明确未实施阶段 3 及后续工作。IPC、数据库、`App.xaml.cs` 和发布流程均不在本次变更范围内。

## 2. 调整前基线

在代码迁移前，以 Release 配置完成基线验证：

```text
dotnet build QuantifiedSelf.Windows.sln -c Release --no-restore
结果：0 warnings，0 errors

dotnet test QuantifiedSelf.Windows.sln -c Release --no-build
结果：504/504 passed
```

Debug 输出目录当时被正在运行的 Agent 进程（PID 18388）占用，导致测试项目复制 Agent 输出文件时失败。这是本机进程造成的文件锁，不是代码或测试失败；为避免中断正在运行的 Agent，本次基线和最终验证统一使用 Release 配置。

## 3. 新建项目

### 3.1 QuantifiedSelf.Windows.Application

- 路径：`src/QuantifiedSelf.Windows.Application/QuantifiedSelf.Windows.Application.csproj`
- 目标框架：`net8.0`
- 项目依赖：仅引用 `QuantifiedSelf.Windows.Core`
- 不依赖 WPF、WinForms、LiveCharts 或 Skia
- 向测试程序集开放 internal 成员，以保持既有纯计算模块测试的覆盖方式

### 3.2 QuantifiedSelf.Windows.Client

- 路径：`src/QuantifiedSelf.Windows.Client/QuantifiedSelf.Windows.Client.csproj`
- 目标框架：`net8.0-windows10.0.19041`
- 项目依赖：`Application`、`Core`、`Infrastructure`
- 当前只建立项目边界，尚未迁入 IPC、进程控制、数据库适配或组合根代码
- 不依赖 WPF、WinForms、LiveCharts 或 Skia

两个项目均已加入 `QuantifiedSelf.Windows.sln` 的 `src` solution folder。App 已增加对 Application 的引用；测试项目已增加对 Application 和 Client 的引用。

## 4. 已迁移类型

### 4.1 Agent 状态 DTO

由 App 迁移至 Application：

- `AgentStatusSnapshot`
- `AgentProcessInfo`

新位置：`src/QuantifiedSelf.Windows.Application/Models/`。

### 4.2 纯计算模块

由 App 迁移至 Application：

- `FocusMetricsCalculator`
- `HourActivityHeatmapCalculator`
- `InsightSuggestionEngine`

新位置：`src/QuantifiedSelf.Windows.Application/Analytics/`。

三个模块的计算行为保持不变；本次只调整程序集归属和命名空间，没有迁移数据库查询、IPC 调用或 WPF 表现逻辑。

## 5. 命名空间处理

项目程序集名称保持为 `QuantifiedSelf.Windows.Application`，但源码命名空间采用：

- `QuantifiedSelf.Windows.ApplicationLayer.Models`
- `QuantifiedSelf.Windows.ApplicationLayer.Analytics`

原因是命名空间 `QuantifiedSelf.Windows.Application` 会使 App 项目中的未限定 WPF `Application` 标识符优先解析为命名空间，并在不能修改 `App.xaml.cs` 的约束下产生 `CS0118`。采用 `ApplicationLayer` 避免了该名称冲突，同时没有改动 `App.xaml.cs`。

## 6. 架构测试

新增 `tests/QuantifiedSelf.Windows.Tests/ArchitectureBoundaryTests.cs`，共 7 个 Fast 测试，覆盖：

1. solution 必须包含 Application 和 Client 项目；
2. Application 必须以 `net8.0` 为目标且只引用 Core；
3. Client 必须以 Windows TFM 为目标并引用 Application、Core、Infrastructure；
4. Application 源码和项目文件不得包含 WPF、WinForms、LiveCharts 或 Skia 标记；
5. Client 源码和项目文件不得包含上述 UI 技术标记；
6. Application 编译程序集不得引用 UI 程序集；
7. 本次迁移的 DTO 和计算模块必须归属于 Application 程序集。

这些测试把阶段 0～2 建立的依赖边界固化为可执行约束，防止后续迁移时重新把 UI 依赖带入 Application/Client。

## 7. 最终验证

完成迁移后执行：

```text
dotnet restore QuantifiedSelf.Windows.sln --ignore-failed-sources
结果：成功

dotnet build QuantifiedSelf.Windows.sln -c Release --no-restore
结果：0 warnings，0 errors

dotnet test tests/QuantifiedSelf.Windows.Tests/QuantifiedSelf.Windows.Tests.csproj -c Release --no-build --filter "Category=Fast"
结果：98/98 passed

dotnet test tests/QuantifiedSelf.Windows.Tests/QuantifiedSelf.Windows.Tests.csproj -c Release --no-build --filter "Category=Integration"
结果：403/403 passed

dotnet test tests/QuantifiedSelf.Windows.Tests/QuantifiedSelf.Windows.Tests.csproj -c Release --no-build --filter "Category=Wpf"
结果：没有匹配的测试，命令成功退出

dotnet test QuantifiedSelf.Windows.sln -c Release --no-build
结果：511/511 passed
```

全量测试由 504 增加到 511，增量正好对应新增的 7 个架构测试。

## 8. 边界确认

本次没有实施或修改以下内容：

- Named Pipe、文件 fallback 或其他 IPC 实现；
- SQLite、数据库查询实现、schema 或迁移逻辑；
- `src/QuantifiedSelf.Windows.App/App.xaml.cs` 的启动/引导序列；
- `publish/scripts/publish.ps1` 或发布目录组装方式；
- Agent 生命周期和状态机行为；
- 默认 `LegacyMainWindow` 与 preview UI 的双 shell 选择逻辑。

工作树中存在本任务开始前已经存在的其他 UI、文档和状态机改动；本次实施保留了这些改动，没有回退、覆盖或纳入阶段 0～2 的完成结论。

## 9. 后续工作

下一步建议进入阶段 3：定义 Application 端口、用例与稳定 DTO，并逐步把 Windows 进程控制、IPC、设置和 Infrastructure 组合迁入 Client。开始阶段 3 前仍应维持本次架构测试，并把每次迁移控制在可独立构建、可回归的窄切片内。
