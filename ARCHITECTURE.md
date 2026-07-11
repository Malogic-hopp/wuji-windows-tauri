# WUJI（吾迹）系统架构说明

> 版本：2026-07-11
> 项目：QuantifiedSelf Windows 端

## 概述

WUJI（吾迹）是一款 Windows 桌面端自动活动追踪应用。它在后台持续记录用户的前台窗口活动（正在使用什么软件、浏览器里访问什么网站），自动聚合成工作时段（Session），并提供 Dashboard、Samples、Sessions、Apps、Insights、Diagnostics、Settings 等页面的历史浏览和趋势分析。

系统由两个独立进程组成：**WPF App**（用户界面 + 系统托盘）和 **Agent**（后台采样服务），通过 Named Pipe（IPC）通信，文件系统作为 fallback 状态通道。

---

## 项目结构

```
Win/
├── QuantifiedSelf.Windows.sln
├── src/
│   ├── QuantifiedSelf.Windows.Core/          # 领域模型、配置、枚举（被所有项目引用）
│   ├── QuantifiedSelf.Windows.Infrastructure/ # 数据层、Win32、IPC 传输、状态文件持久化
│   ├── QuantifiedSelf.Windows.Agent/          # Agent 后台进程（BackgroundService）
│   └── QuantifiedSelf.Windows.App/            # WPF 桌面应用（UI + 系统托盘）
├── tests/
│   └── QuantifiedSelf.Windows.Tests/          # xUnit 测试
└── scripts/                                   # Python 辅助分析脚本
```

### 项目依赖

```
Core  ←── Infrastructure  ←── Agent
  ↑                ↑            ↑（ReferenceOutputAssembly=false）
  └────────────────┼────── App
                   └────────────┘
```

- **Core**：零外部依赖，纯 C# 模型层。目标框架 `net8.0`（非 Windows 限定，可跨平台）
- **Infrastructure**：依赖 Core，引用 `Microsoft.Data.Sqlite`、`Microsoft.Extensions.Configuration`、`Microsoft.Extensions.Configuration.Json`、`Microsoft.Extensions.Logging.Abstractions`
- **Agent**：依赖 Core + Infrastructure，使用 `Microsoft.NET.Sdk.Worker` SDK，引用 `Microsoft.Extensions.Hosting`（含 HostBuilder / DI / BackgroundService 体系）
- **App**：依赖 Core + Infrastructure，WPF 桌面应用（`Microsoft.NET.Sdk` + `UseWPF` + `UseWindowsForms`（NotifyIcon 依赖）），引用 `CommunityToolkit.Mvvm`（MVVM 工具包）、`LiveChartsCore.SkiaSharpView.WPF`（图表）、`Microsoft.Extensions.Logging.Abstractions`。对 Agent 的项目引用仅用于解决方案级构建顺序和 IDE 源码导航（`ReferenceOutputAssembly=false`），不编译 Agent 源码，也不链接 Agent 输出程序集

> App 以 `ReferenceOutputAssembly=false` 引用 Agent 项目，目的是保证解决方案构建顺序正确、允许 IDE 跨项目跳转。运行时两个进程独立部署为 self-contained win-x64 文件夹发布（通过 `publish/scripts/publish.ps1`），Agent 可执行文件复制到 `publish/release/App/Agent/` 子目录下供 App 启动。

---

## 进程架构

```
┌─────────────────────────────────────────────────────────────────┐
│                     WPF App (QuantifiedSelf.Windows.App)         │
│                                                                  │
│  MainWindow              System Tray (NotifyIcon)                │
│  ├─ Dashboard            ├─ Show / Hide Window                   │
│  ├─ Samples              ├─ Start / Stop Agent                   │
│  ├─ Sessions             ├─ Pause / Resume Collection            │
│  ├─ Apps                 └─ Exit                                 │
│  ├─ Insights                                                  │
│  ├─ Diagnostics                                               │
│  └─ Settings                                                 │
│                                                                  │
│  Services:                                                       │
│  ├─ AgentStatusService   (读 Agent 状态，IPC → file fallback)     │
│  ├─ AgentControlService  (下发控制命令)                           │
│  ├─ AgentProcessService  (管理 Agent 进程生命周期)                 │
│  ├─ RefreshService       (2s 状态轮询 + AppSettings.RefreshIntervalSeconds 页面刷新，默认 15s) │
│  ├─ TrayService          (系统托盘图标与菜单)                      │
│  └─ Data Services        (Overview/Samples/Sessions/Apps 查询)    │
└──────────┬──────────────────────────────────────────────────────┘
           │  IPC (Named Pipe) ──→ fallback: 文件系统读写
           │
┌──────────▼──────────────────────────────────────────────────────┐
│                  Agent (QuantifiedSelf.Windows.Agent)            │
│                                                                  │
│  Worker (BackgroundService, 1 秒 tick)                           │
│  └─ AgentStateMachine                                           │
│       ├─ TickAsync()                                             │
│       │   ├─ 读取控制命令 (agent_control.json)                     │
│       │   ├─ Capture 前台窗口 (Win32)                              │
│       │   ├─ 隐私过滤                                              │
│       │   ├─ 写入 SQLite (foreground_samples 表)                   │
│       │   ├─ 会话聚合 (sessions 表)                                │
│       │   └─ PersistAsync (写 runtime_state.json + health_state.json)│
│       └─ ProcessCommandAsync() (处理 Start/Stop/Pause 等命令)     │
│                                                                  │
│  AgentCommandServerHostedService (IPC 服务端)                     │
│  └─ NamedPipeAgentCommandServer                                  │
│                                                                  │
│  Services:                                                       │
│  ├─ ConfiguredForegroundSampleProvider (Win32→Mock 切换)          │
│  ├─ SessionAggregator           (活跃窗口→Session 聚合)           │
│  ├─ ForegroundSamplePrivacyFilter (进程/标题隐私规则)             │
│  └─ AgentEventWriter            (Agent 事件日志，SQLite + JSONL)   │
└──────────────────────────────────────────────────────────────────┘
```

---

## 进程通信（IPC）

### 主通道：Named Pipe

- App 端：`Infrastructure/Ipc/NamedPipeAgentControlClient`（客户端）
- Agent 端：`Infrastructure/Ipc/NamedPipeAgentCommandServer`（服务端）
- Pipe 名基于当前用户 SID，保证单用户隔离
- 协议版本：1

支持的命令：

| 命令 | 说明 | 是否改变 Agent 状态 | 通道 |
|------|------|-------------------|------|
| `Ping` | 心跳检测（传输层命令，不在 AgentCommandType 枚举中） | 否 | IPC |
| `GetStatus` | 获取完整运行状态 | 否 | IPC / 状态文件读取 |
| `Pause` | 暂停采集 | 是 | IPC / 文件 |
| `Resume` | 恢复采集 | 是 | IPC / 文件 |
| `Stop` | 停止 Agent | 是 | IPC / 文件 |
| `ReloadConfig` | 重新加载配置 | 是 | IPC / 文件 |
| `UpdateAppMetadata` | 更新 App 元数据 | 否 | 文件 |
| `UpdatePrivacyRules` | 更新隐私规则 | 否 | 文件 |
| `PruneData` | 清理过期数据 | 是 | IPC / 文件 |
| `ClearHistory` | 清空所有历史数据 | 是 | IPC / 文件 |

### Fallback：文件系统（控制文件 + 状态文件）

当 IPC 不可用时，App 通过读写以下文件与 Agent 通信：

| 文件 | 写入方 | 说明 |
|------|--------|------|
| `runtime_state.json` | Agent (PersistAsync，按心跳间隔至少一次，采样成功时额外写入) | 进程 PID、状态、心跳时间戳、版本 |
| `health_state.json` | Agent (PersistAsync，按心跳间隔至少一次，采样成功时额外写入) | 健康指标、错误计数、Tick 诊断 |
| `agent_control.json` | App (AgentControlService) | 控制命令（Pause/Stop/PruneData 等），Agent 在 finally 块中删除（异常也保证清理） |

Agent 使用 `File.Delete` + `File.Move` 替换状态快照，配合 `FileShare.Delete` 和重试规避读写竞态；该替换不是严格原子操作（目标文件在 Delete 与 Move 之间存在短暂不存在窗口），但状态文件可重写，短暂读取失败由下一轮轮询恢复。App 以 `FileShare.ReadWrite | FileShare.Delete` 读取。

---

## 数据流

### 前台采样流程

```
[Win32 API]
    │  GetForegroundWindow() → GetWindowThreadProcessId() → GetWindowText()
    │  注：GetWindowText 已替换为 SendMessageTimeout(WM_GETTEXT, SMTO_ABORTIFHUNG, 500ms)
    │
    ▼
[ConfiguredForegroundSampleProvider]
    │  根据 UseMockCapture 配置选择 Win32 或 Mock 实现
    │
    ▼
[AgentStateMachine.TickAsync]
    │  判断 sampleDue（距上次采样 ≥ SamplingIntervalSeconds）
    │
    ▼
[ForegroundSamplePrivacyFilter]
    │  检查 ExcludedProcesses / ExcludedTitlePatterns
    │
    ▼
[ForegroundSampleRepository.InsertAsync]
    │  写入 foreground_samples 表 (SQLite)
    │
    ▼
[SessionAggregator.HandleSampleAsync]
    │  判断是否与上一个样本属于同一 Session
    │  新窗口 → Close 旧 Session + Start 新 Session
    │  同一窗口 → 更新 Session duration
    │  IdleSeconds ≥ IdleThresholdSeconds → 标记 Idle；< IdleThresholdSeconds → 标记 Active
    │
    ▼
[PersistAsync]
    │  写 runtime_state.json + health_state.json
```

### 数据库表结构

```
foreground_samples
├─ id (INTEGER PK)
├─ sample_time_utc (TEXT, ISO 8601)
├─ process_name (TEXT)
├─ window_title (TEXT, 可为 NULL)
├─ executable_path (TEXT, 可为 NULL)
├─ idle_seconds (INTEGER)
├─ activity_state (TEXT: Active / Idle / Unknown)
└─ INDEX: idx_foreground_samples_time ON sample_time_utc

app_sessions
├─ id (INTEGER PK)
├─ process_name (TEXT)
├─ window_title (TEXT, 可为 NULL)
├─ started_at_utc (TEXT, ISO 8601)
├─ ended_at_utc (TEXT, ISO 8601, 可为 NULL)
├─ total_duration_seconds (INTEGER NOT NULL DEFAULT 0)
├─ active_duration_seconds (INTEGER NOT NULL DEFAULT 0)
├─ idle_duration_seconds (INTEGER NOT NULL DEFAULT 0)
├─ unknown_duration_seconds (INTEGER NOT NULL DEFAULT 0)
├─ close_reason (TEXT NOT NULL DEFAULT 'Open')
├─ INDEX: idx_app_sessions_started ON started_at_utc
├─ INDEX: idx_app_sessions_process ON process_name

agent_events
├─ id (INTEGER PK)
├─ event_time_utc (TEXT, ISO 8601)
├─ event_type (TEXT)
├─ event_level (TEXT: Info / Warning / Error / Critical)
├─ message (TEXT)
├─ source (TEXT, 可为 NULL)
├─ request_id (TEXT, 可为 NULL)
├─ error_code (TEXT, 可为 NULL)
├─ process_name (TEXT, 可为 NULL)
├─ session_id (INTEGER, 可为 NULL)
├─ payload_json (TEXT, JSON, 可为 NULL)
├─ INDEX: idx_agent_events_time ON event_time_utc
├─ INDEX: idx_agent_events_type ON event_type
└─ INDEX: idx_agent_events_level_time ON (event_level, event_time_utc)
```

---

## 状态管理

### Agent 生命周期状态

```csharp
enum AgentActualState
{
    NotRunning,   // 进程不存在或未初始化
    Starting,     // 初始化中
    Running,      // 正常采集
    Pausing,      // 正在暂停
    Paused,       // 已暂停，只写心跳不采样
    Resuming,     // 正在恢复
    Stopping,     // 正在停止
    Stopped,      // 已停止
    Stale,        // 进程存活但心跳过期 / 进程已退出但状态文件残留
    Error,        // 异常状态
    Maintenance   // 执行 PruneData / ClearHistory 中
}
```

### Stale 判定规则（App 端，文件 fallback 模式）

| 条件 | 判定 |
|------|------|
| `processRunning && heartbeatFresh` | 正常（状态从 runtime_state.json 读取） |
| `processRunning && !heartbeatFresh` | **Stale** — 进程活着但心跳超过 `StaleThresholdSeconds`（默认 15s） |
| `!processRunning && runtimeState exists` | **Stale** — 进程已退出但状态文件残留 |
| `!processRunning && runtimeState is null` | NotRunning |

### Tick 诊断字段（health_state.json）

| 字段 | 说明 |
|------|------|
| `LastTickPhase` | 进入最近阻塞调用前设置的阶段 |
| `LastTickDurationMs` | 整个 tick 耗时 |
| `LastCaptureDurationMs` | 前台采集耗时 |
| `LastPersistDurationMs` | 状态持久化耗时 |
| `LastMaintenanceDurationMs` | 维护操作耗时 |
| `LastErrorCode` / `LastErrorMessage` | 最近一次错误的错误码和消息（成功路径清空） |
| `LastSuccessUtc` | 最近一次成功完成 tick 的时间 |

---

## 配置（WindowsAgentOptions）

Agent 运行时可配置参数，通过 WPF App 的 Settings 页面编辑，存入 `windows-agent.json`：

| 参数 | 默认值 | 说明 |
|------|--------|------|
| `SamplingIntervalSeconds` | 3 | 采样间隔 |
| `IdleThresholdSeconds` | 60 | 判定为 Idle 的无操作时间阈值 |
| `HeartbeatIntervalSeconds` | 3 | 心跳持久化间隔 |
| `StaleThresholdSeconds` | 15 | App 判定 Agent Stale 的超时 |
| `RetentionDays` | 30 | 数据保留天数 |
| `IdleSummaryIntervalMinutes` | 5 | Idle 聚合粒度 |
| `UseMockCapture` | false | 测试用，使用模拟采样数据 |
| `EnableJsonlJournal` | true | 是否同时写 JSONL 日志 |
| `EnableAgentEventJournal` | true | 是否记录 Agent 事件 |
| `EnableSessionMerge` | true | 是否合并相邻 Session |
| `MaskWindowTitles` | true | 是否脱敏窗口标题 |
| `ExcludedProcesses` | `["KeePass","1Password","Bitwarden","explorer"]` | 排除的进程列表 |
| `ExcludedTitlePatterns` | `["InPrivate"]` | 排除的标题模式列表 |

---

## 多进程文件读写保护

Agent 写状态文件和 App 读状态文件存在并发。当前保护机制：

1. **读端**：使用 `FileShare.ReadWrite | FileShare.Delete` 打开只读流，不阻止写端替换文件
2. **写端**：`File.Delete` + `File.Move` 替换（非严格原子操作，配合 3 次 × 50ms 间隔重试和 `FileShare.Delete` 降低竞态概率）
3. **PersistAsync 兜底**：写异常被捕获记日志，不传播到 TickAsync 主循环
4. **Worker 顶级兜底**：TickAsync 任何未处理异常（排除 `OperationCanceledException`）只记日志，不杀进程

---

## UI 结构

### 主窗口（MainWindow.xaml）

```
┌──────────────────────────────────────────────────────────────────┐
│ WUJI 吾迹                                    [Agent Status Pill] │
│ [Start Agent] [Stop Agent] [Pause] [Resume] [Refresh] [Open Settings] │
├──────────────────────────────────────────────────────────────────┤
│ [Dashboard] [Apps] [Sessions] [Samples] [Diagnostics] [Insights] [Settings] │
├──────────────────────────────────────────────────────────────────┤
│                                                                  │
│  Tab Content                                                      │
│                                                                  │
└──────────────────────────────────────────────────────────────────┘
```

### 各页面

| 页面 | 功能 | ViewModel |
|------|------|-----------|
| Dashboard | 概览卡片、小时热力图、今日洞察、趋势图 | `DashboardViewModel` |
| Apps | App 使用统计排行 | `AppsViewModel` |
| Sessions | 聚合后的工作时段列表 | `SessionsViewModel` |
| Samples | 原始采样记录列表 | `SamplesViewModel` |
| Diagnostics | Agent 状态、Tick 诊断、IPC 状态、事件日志、启动注册 | `MainWindowViewModel` (内联) |
| Insights | 专注度洞察与趋势分析 | `InsightsViewModel` |
| Settings | Agent 参数编辑、页面刷新间隔、数据清理 | `SettingsViewModel` |

---

## 系统托盘

```
┌──────────────┐
│ WUJI 吾迹    │  ← 双击打开窗口
│ Agent: xxx   │
├──────────────┤
│ Show Window  │
│ Start/Stop   │
│ Pause/Resume │
├──────────────┤
│ Exit         │
└──────────────┘
```

---

## 关键技术决策

| 决策 | 理由 |
|------|------|
| App + Agent 双进程架构 | 后台采样不依赖 UI 进程存活，UI 崩溃不影响数据采集 |
| Named Pipe IPC + 文件 fallback | IPC 为主（低延迟、双向、类型安全），文件为 fallback（保证 App 和 Agent 独立启动时也能互相发现状态） |
| SQLite 本地数据库 + WAL 模式 | 零配置、嵌入式、跨版本兼容，适合单用户桌面场景；WAL（Write-Ahead Logging）提升并发读写性能 |
| 心跳文件 + Stale 阈值判定 | 无需额外 watchdog 进程，利用已有的文件持久化即可探测 Agent 健康状态 |
| `SendMessageTimeout` + `SMTO_ABORTIFHUNG` | 防止挂起窗口把 Agent 主循环拖死 |
| `File.Delete` + `File.Move` 替代 `File.Move(overwrite:true)` | 兼容 `FileShare.Delete`，避免读写竞态导致 Agent 崩溃 |
| WPF + .NET 8 | 原生 Windows 桌面体验，高性能，self-contained win-x64 文件夹发布 |

---

## 构建、测试与发布

```bash
# 构建
dotnet build QuantifiedSelf.Windows.sln

# 运行测试（468 个 xUnit 测试，含 Agent 状态机、数据流、并发 I/O）
dotnet test

# 发布（self-contained win-x64 文件夹发布，含 Agent 嵌入与产物校验）
.\publish\scripts\publish.ps1
```

发布脚本将 App 输出到 `publish/release/App/`，Agent 输出到 `publish/release/Agent/`，然后把 Agent 产物整体复制到 `publish/release/App/Agent/` 子目录，保证两个 self-contained 可执行文件的依赖集隔离。最后校验入口可执行文件是否存在，并确认 Agent 可执行文件未被错误放在 App 根目录。
