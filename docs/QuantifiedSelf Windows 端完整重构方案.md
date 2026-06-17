# QuantifiedSelf Windows 端完整重构方案

## 1. 项目定位

本项目定位为一个 Windows 11 本地屏幕使用情况监控软件。

软件由两个核心进程组成：

```text
QuantifiedSelf.Windows.Agent
    后台采集进程

QuantifiedSelf.Windows.App
    WPF 主界面 / 控制台 / 统计看板
```

整体目标不是做一个简单的桌面窗口，而是做一个完整的本地监控系统：

```text
启动 Agent
停止 Agent
暂停采集
恢复采集
查看 Agent 状态
查看今日统计
查看应用排行
查看最近会话
查看最近采样
查看异常和日志
修改配置
```

---

## 2. 总体架构

### 2.1 新架构总览

```text
┌──────────────────────────────────────────────┐
│ QuantifiedSelf.Windows.App                   │
│ WPF 主界面                                   │
│                                              │
│  - Agent 控制                               │
│  - 状态看板                                 │
│  - 使用统计                                 │
│  - 会话列表                                 │
│  - 采样记录                                 │
│  - 设置页面                                 │
│  - 托盘图标                                 │
└───────────────┬──────────────────────────────┘
                │
                │ 控制命令 / 状态查询
                ↓
┌──────────────────────────────────────────────┐
│ 本地控制层                                   │
│                                              │
│  ProcessStartInfo: StartAgent                │
│  IPC V1: Named Pipe / gRPC over Named Pipes   │
│  fallback: agent_control.json                │
│                                              │
│  Pause / Resume / Stop / GetStatus           │
│  ReloadConfig / UpdateAppMetadata            │
│  UpdatePrivacyRules / PruneData / ClearHistory │
└───────────────┬──────────────────────────────┘
                │
                │ Agent 接收命令并返回结果
                ↓
┌──────────────────────────────────────────────┐
│ QuantifiedSelf.Windows.Agent                 │
│ 后台采集进程                                 │
│                                              │
│  - AgentStateMachine                         │
│  - AgentCommandServer                        │
│  - 读取前台窗口                             │
│  - 判断 idle 状态                            │
│  - 合并 app session                          │
│  - 写 SQLite                                 │
│  - 写 JSONL                                  │
│  - 写 runtime_state                          │
│  - 写 health_state                           │
└───────────────┬──────────────────────────────┘
                │
                │ 写入数据
                ↓
┌──────────────────────────────────────────────┐
│ 本地数据层                                   │
│                                              │
│  SQLite + WAL                                │
│  runtime_state.json                          │
│  foreground_samples_YYYYMMDD.jsonl           │
│  agent_events_YYYYMMDD.jsonl                 │
│  windows-agent.json                          │
│  app-name-map.json / app_metadata            │
└───────────────┬──────────────────────────────┘
                │
                │ WPF 只读查询 / 诊断读取
                ↑
┌───────────────┴──────────────────────────────┐
│ QuantifiedSelf.Windows.App                   │
│ 展示统计和状态                               │
└──────────────────────────────────────────────┘
```

### 2.2 设计原则

本项目遵循以下原则：

```text
1. UI 和采集解耦
2. Agent 是唯一的数据写入者
3. WPF App 负责控制和展示
4. SQLite 是主数据源
5. JSONL 是审计和排错日志
6. Named Pipe / gRPC 是长期主控制通道
7. runtime_state / health_state 是状态快照和兜底判断来源
8. agent_control.json 是 V0 控制通道和 V1 fallback / desired state 持久化来源
9. 暂停采集不等于结束 Agent 进程
10. 控制走 IPC，数据走 SQLite，诊断走 JSONL / 状态文件
11. 隐私规则必须在 Agent 采集阶段生效
```

---

## 3. 项目结构设计

建议将原来的 `Overview` 改造成新的 WPF 主程序。

最终目录建议如下：

```text
windows/src
├─ QuantifiedSelf.Windows.Agent
│  ├─ Program.cs
│  ├─ AgentHost.cs
│  ├─ AgentStateMachine.cs
│  ├─ AgentCommandServer.cs
│  ├─ ForegroundCaptureLoop.cs
│  ├─ ForegroundWindowProvider.cs
│  ├─ IdleDetector.cs
│  ├─ SessionAggregator.cs
│  ├─ RuntimeStateWriter.cs
│  ├─ AgentEventLogger.cs
│  └─ AgentOptionsMonitor.cs
│
├─ QuantifiedSelf.Windows.App
│  ├─ App.xaml
│  ├─ App.xaml.cs
│  ├─ MainWindow.xaml
│  ├─ MainWindow.xaml.cs
│  │
│  ├─ Views
│  │  ├─ DashboardView.xaml
│  │  ├─ AppsView.xaml
│  │  ├─ SessionsView.xaml
│  │  ├─ SamplesView.xaml
│  │  ├─ DiagnosticsView.xaml
│  │  └─ SettingsView.xaml
│  │
│  ├─ ViewModels
│  │  ├─ MainWindowViewModel.cs
│  │  ├─ DashboardViewModel.cs
│  │  ├─ AppsViewModel.cs
│  │  ├─ SessionsViewModel.cs
│  │  ├─ SamplesViewModel.cs
│  │  ├─ DiagnosticsViewModel.cs
│  │  └─ SettingsViewModel.cs
│  │
│  ├─ Services
│  │  ├─ AgentProcessService.cs
│  │  ├─ AgentControlService.cs
│  │  ├─ AgentStatusService.cs
│  │  ├─ OverviewDataService.cs
│  │  ├─ StatisticsService.cs
│  │  ├─ RefreshService.cs
│  │  ├─ TrayService.cs
│  │  └─ SettingsService.cs
│  │
│  ├─ Components
│  │  ├─ StatusCard.xaml
│  │  ├─ MetricCard.xaml
│  │  ├─ AppUsageCard.xaml
│  │  └─ EmptyState.xaml
│  │
│  ├─ Themes
│  │  ├─ Colors.xaml
│  │  ├─ Typography.xaml
│  │  ├─ Cards.xaml
│  │  ├─ Buttons.xaml
│  │  └─ DataGrid.xaml
│  │
│  └─ Resources
│     └─ AppIcon.ico
│
├─ QuantifiedSelf.Windows.Core
│  ├─ Models
│  ├─ Options
│  ├─ Paths
│  ├─ AppIdentity
│  ├─ Control
│  └─ Runtime
│
├─ QuantifiedSelf.Windows.Infrastructure
│  ├─ Database
│  ├─ Win32
│  ├─ Logging
│  ├─ RuntimeState
│  ├─ Control
│  ├─ Ipc
│  └─ Settings
│
└─ QuantifiedSelf.Windows.Tests
   ├─ AgentTests
   ├─ DatabaseTests
   └─ StatisticsTests
```

说明：

```text
QuantifiedSelf.Windows.App
    替代原 WinForms Overview，作为新的 WPF 主界面。

QuantifiedSelf.Windows.Agent
    继续作为后台采集进程。

QuantifiedSelf.Windows.Core
    放共享模型、状态枚举、AppIdentity、配置模型、路径解析、控制命令模型。

QuantifiedSelf.Windows.Infrastructure
    放 SQLite、Win32、Named Pipe / gRPC、JSON 文件读写、日志落盘等底层实现。

QuantifiedSelf.Windows.Tests
    后续补充单元测试和统计逻辑测试。
```

---

## 4. 进程模型设计

### 4.1 Agent 进程

Agent 是后台采集进程，职责包括：

```text
1. 单实例运行
2. 初始化数据库
3. 读取配置
4. 读取前台窗口
5. 判断 idle 状态
6. 写 foreground_samples
7. 合并 app_sessions
8. 写 health_state
9. 写 runtime_state
10. 响应 IPC 控制命令，并支持 agent_control.json fallback
```

Agent 不负责：

```text
1. 展示 UI
2. 画图
3. 响应用户点击
4. 做复杂统计展示
5. 管理窗口导航
```

第一版 Agent 建议仍然作为当前用户会话下的后台进程运行，而不是一开始注册为 Windows Service。

原因：

```text
1. Agent 需要读取当前用户桌面的前台窗口
2. Windows Service 与交互式桌面会话隔离，调试和部署复杂度更高
3. MVP 阶段优先验证采集准确性、状态机和数据口径
4. 后续产品化阶段可再评估开机自启、计划任务或 Windows Service
```

如果后续需要读取高完整性进程信息，可在 UI 中提供“以管理员权限重启 Agent”的显式操作，而不是默认要求管理员权限。

### 4.2 WPF App 进程

WPF App 是主界面，职责包括：

```text
1. 启动 Agent
2. 停止 Agent
3. 暂停采集
4. 恢复采集
5. 查看 Agent 状态
6. 查看统计信息
7. 查看会话和采样
8. 修改配置
9. 托盘常驻
10. 提示异常
```

WPF App 不负责：

```text
1. 直接采集前台窗口
2. 直接写 foreground_samples
3. 直接合并 app_sessions
4. 替代 Agent 进行后台采集
```

---

## 5. 控制模型设计

### 5.1 控制动作分类

UI 中的操作需要分清四类：

```text
启动 Agent
    启动后台进程

停止 Agent
    请求 Agent 优雅退出

暂停采集
    Agent 继续运行，但不再采集和写入样本

恢复采集
    Agent 从暂停状态恢复采样
```

### 5.2 不建议的做法

不要这样设计：

```text
暂停 = Kill Agent
恢复 = 重新启动 Agent
关闭 UI = 关闭 Agent
UI 定时器 = 采集逻辑
```

这些做法会导致：

```text
1. 会话边界混乱
2. 数据统计不准确
3. UI 崩溃导致采集停止
4. 后续扩展困难
```

### 5.3 推荐控制通道

控制模型按阶段演进：

```text
V0 / MVP:
    使用 runtime/agent_control.json 跑通闭环
    使用 runtime_state.json / health_state 判断结果

V1 / 推荐长期形态:
    使用 Named Pipe 或 gRPC over Named Pipes 作为主控制通道
    agent_control.json 仅保留为 fallback 和 desired state 持久化文件

V2 / 产品化:
    Agent 支持双向状态流
    WPF 订阅 Agent 状态变化，减少定时轮询
```

控制命令应优先走 IPC，并具备请求响应语义：

```text
WPF App
    ↓ Pause / Resume / Stop / GetStatus
Named Pipe / gRPC
    ↓
AgentCommandServer
    ↓
AgentStateMachine
    ↓
CommandResult
```

Named Pipe 安全边界：

```text
1. Pipe 只允许当前 Windows 用户访问
2. Pipe 名称包含当前用户 SID 的 hash，例如 QuantifiedSelf.Agent.{UserSidHash}
3. Agent 创建 Pipe 时配置当前用户 ACL，拒绝跨用户控制
4. WPF 连接前校验当前用户 SID 与 pipe name 是否匹配
5. Diagnostics 页显示 pipe name、连接状态和最近 IPC 错误
```

命令请求示例：

```json
{
  "command": "Pause",
  "requestId": "20260617150000123",
  "requestedAtUtc": "2026-06-17T20:00:00Z",
  "requestedBy": "QuantifiedSelf.Windows.App",
  "reason": "User clicked pause"
}
```

即时响应示例：

```json
{
  "requestId": "20260617150000123",
  "accepted": true,
  "completed": false,
  "actualState": "Pausing",
  "message": "Pause command accepted"
}
```

完成响应 / 状态查询结果示例：

```json
{
  "requestId": "20260617150000123",
  "accepted": true,
  "completed": true,
  "actualState": "Paused",
  "message": "Collection paused"
}
```

fallback 文件仍然保留：

```text
runtime/agent_control.json
```

文件示例：

```json
{
  "command": "Resume",
  "desiredState": "Running",
  "requestId": "20260617150000123",
  "requestedAtUtc": "2026-06-17T20:00:00Z",
  "requestedBy": "QuantifiedSelf.Windows.App",
  "reason": "User clicked resume"
}
```

字段语义：

```text
command:
    Pause / Resume / Stop / GetStatus / ReloadConfig
    UpdateAppMetadata / UpdatePrivacyRules / PruneData / ClearHistory

desiredState:
    Running / Paused / Stopped

Stop:
    动作命令

Stopped:
    目标状态

Stopping:
    Agent 正在停止的实际状态

NotRunning:
    UI 最终观察到 Agent 进程不存在
```

### 5.4 控制命令模型

放在 `QuantifiedSelf.Windows.Core`：

```csharp
public enum AgentDesiredState
{
    Running,
    Paused,
    Stopped
}

public enum AgentCommandType
{
    Pause,
    Resume,
    Stop,
    GetStatus,
    ReloadConfig,
    UpdateAppMetadata,
    UpdatePrivacyRules,
    PruneData,
    ClearHistory
}

public sealed class AgentControlCommand
{
    public AgentCommandType Command { get; set; } = AgentCommandType.GetStatus;

    public AgentDesiredState? DesiredState { get; set; }

    public string RequestId { get; set; } = string.Empty;

    public DateTime RequestedAtUtc { get; set; } = DateTime.UtcNow;

    public string RequestedBy { get; set; } = "Unknown";

    public bool WaitForCompletion { get; set; }

    public int TimeoutMilliseconds { get; set; } = 5000;

    public string? Reason { get; set; }
}

public sealed class AgentCommandResult
{
    public string RequestId { get; set; } = string.Empty;

    public bool Accepted { get; set; }

    public bool Completed { get; set; }

    public AgentActualState ActualState { get; set; }

    public string? Message { get; set; }

    public string? ErrorCode { get; set; }
}
```

注意：

```text
1. Start 不是 AgentCommandServer 的普通 IPC 命令
2. Agent 未启动时 IPC 服务不存在，启动必须由 AgentProcessService 通过 ProcessStartInfo 完成
3. Stop 是动作命令，Stopped 是 DesiredState，Stopping / Stopped / NotRunning 是 ActualState
4. accepted=true 只表示 Agent 已接收命令，不一定表示状态转换已经完成
5. completed=true 才表示命令对应的状态转换或写入操作已经完成
```

典型暂停流程：

```text
WPF 发送 Pause
    ↓
Agent 返回 accepted=true, completed=false, actualState=Pausing
    ↓
UI 显示 Pausing
    ↓
状态流 / GetStatus 确认 actualState=Paused
    ↓
UI 显示 Paused
```

### 5.5 Agent 响应控制命令

Agent 不应在采集循环里直接散落处理控制命令，而应由 `AgentStateMachine` 统一完成状态转换。

主逻辑：

```text
接收 Named Pipe / gRPC 命令
    ↓
如果 IPC 不可用，读取 agent_control.json fallback
    ↓
如果命令 = Stop
    关闭当前会话
    写入停止状态
    写入 CommandCompleted / AgentStopped
    优雅退出

如果命令 = Pause
    如果当前有活动会话，先关闭会话
    不采样
    不写 foreground_samples
    持续写 heartbeat
    health_state 标记 Paused
    返回 actualState = Paused

如果命令 = Resume / desiredState = Running
    正常采集
    正常写样本
    正常合并会话
    返回 actualState = Running

如果命令 = GetStatus
    返回当前 actualState、heartbeat、lastSample、最近错误

如果命令 = UpdateAppMetadata / UpdatePrivacyRules
    校验请求来源和参数
    由 Agent 写入 app_metadata 或规则存储
    重新加载内存规则
    返回 completed=true

如果命令 = PruneData
    Agent 进入维护状态
    按 retentionDays 清理 SQLite 历史数据和旧 JSONL
    写入 DataPruned
    按原状态恢复 Running 或 Paused

如果命令 = ClearHistory
    Agent 进入维护状态
    关闭当前 session
    清理 SQLite 历史数据、JSONL 和历史 runtime 快照
    保留或重写当前 runtime_state.json
    写入 HistoryCleared
    立即写入新的 heartbeat
    按用户选择恢复 Running 或保持 Paused
```

暂停状态下需要做到：

```text
1. Agent 进程仍然存在
2. Agent 不读取前台窗口
3. Agent 不新增 foreground_samples
4. Agent 不继续扩展当前会话
5. Agent 持续写 heartbeat
6. UI 显示 Paused
```

---

## 6. 状态模型设计

### 6.1 DesiredState 与 ActualState

状态模型必须区分：

```text
DesiredState
    用户或配置希望 Agent 达到的目标状态

ActualState
    Agent 当前真实状态，由 AgentStateMachine 产生
```

UI 只展示 `ActualState`，用户操作只改变 `DesiredState` 或发送命令。不要在用户点击按钮后立即假设状态已经完成切换。

放在 `Core`：

```csharp
public enum AgentDesiredState
{
    Running,
    Paused,
    Stopped
}

public enum AgentActualState
{
    NotRunning,
    Starting,
    Running,
    Pausing,
    Paused,
    Resuming,
    Stopping,
    Stopped,
    Maintenance,
    Stale,
    Error
}
```

典型状态流转：

```text
Running
    ↓ 用户点击暂停
Pausing
    ↓ Agent 关闭当前会话并确认
Paused

Paused
    ↓ 用户点击恢复
Resuming
    ↓ Agent 开始新采样周期
Running

Running / Paused
    ↓ 用户点击停止
Stopping
    ↓ Agent 关闭会话并退出
NotRunning
```

维护状态流转：

```text
Running / Paused
    ↓ PruneData / ClearHistory
Maintenance
    ↓ 清理完成并写入新 heartbeat
Running / Paused
```

说明：

```text
Stopped:
    Agent 退出前写入 runtime_state 的最后快照状态

NotRunning:
    WPF 通过进程检查得到的最终观察状态

UI 最终显示:
    NotRunning
```

### 6.2 状态判断来源

WPF App 不应只靠进程名判断 Agent 状态。

推荐综合以下来源：

```text
1. runtime_state.json
2. health_state 表
3. 当前系统进程 PID
4. 最近 heartbeat 时间
5. 最近 sample 时间
6. 最近错误信息
7. agent_control.json 的 desiredState
```

### 6.3 状态判断逻辑

```text
没有 runtime_state，且找不到 Agent 进程
    => NotRunning

点击启动后短时间内还没有 heartbeat
    => Starting

Agent 进程存在，heartbeat 新鲜，actualState = Running
    => Running

Agent 进程存在，heartbeat 新鲜，actualState = Paused
    => Paused

命令已发出但 Agent 尚未确认
    => Pausing / Resuming / Stopping

runtime_state 存在，但 heartbeat 过旧
    => Stale

health_state 中存在严重错误
    => Error

Agent 正在响应 Stop
    => Stopping
```

### 6.4 heartbeat 建议

Agent 每 1 到 5 秒写一次运行心跳：

```json
{
  "processId": 12345,
  "startedAtUtc": "2026-06-17T20:00:00Z",
  "lastHeartbeatUtc": "2026-06-17T20:15:10Z",
  "lastSampleUtc": "2026-06-17T20:15:09Z",
  "state": "Running",
  "machineName": "DESKTOP",
  "userName": "WangZhenchi",
  "commandLine": "...",
  "version": "0.1.0"
}
```

---

## 7. 数据库设计

### 7.1 当前保留表

继续保留：

```text
foreground_samples
health_state
app_sessions
```

SQLite 必须按“一个写入者，多只读者”的模型使用：

```sql
PRAGMA journal_mode=WAL;
PRAGMA busy_timeout=5000;
PRAGMA foreign_keys=ON;
```

约束：

```text
1. Agent 是唯一写入者
2. WPF 只使用只读连接
3. WPF 连接建议 Mode=ReadOnly;Pooling=false
4. 所有查询必须短连接、短事务
5. 不在 UI 层长期持有 DataReader
6. Agent 写入使用短事务或小批量事务
7. 复杂统计优先查询 app_sessions，不扫 foreground_samples 全表
```

### 7.2 建议新增表

建议新增：

```text
agent_events
app_metadata
```

`agent_events` 用于记录 Agent 生命周期事件和命令审计。考虑到事件日志主要用于诊断，推荐采用“双层记录”：

```text
agent_events_YYYYMMDD.jsonl
    作为完整审计日志，记录详细 details_json

SQLite agent_events
    作为轻量索引和最近事件查询来源，只记录低频生命周期、命令和错误事件
```

不要把高频采样事件写入 `agent_events`，避免增加 SQLite 事务压力。

字段建议：

```sql
CREATE TABLE IF NOT EXISTS agent_events (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    event_time_utc TEXT NOT NULL,
    event_type TEXT NOT NULL,
    event_level TEXT NOT NULL,
    source TEXT,
    request_id TEXT,
    message TEXT,
    details_json TEXT
);
```

事件类型示例：

```text
AgentStarted
AgentStopped
AgentPaused
AgentResumed
CommandReceived
CommandCompleted
CommandFailed
AgentError
DatabaseError
ConfigReloaded
SleepDetected
WakeDetected
ScreenLocked
ScreenUnlocked
SessionClosed
SessionRecovered
AppMetadataUpdated
PrivacyRulesUpdated
DataPruned
HistoryCleared
```

这个表的好处是：

```text
1. UI 可以展示 Agent 事件时间线
2. 方便排错
3. 可以知道什么时候暂停、恢复、停止
4. 可以辅助统计异常
```

`app_metadata` 用于把应用显示名、分类和归一化身份保存进 SQLite，而不是只依赖 `app-name-map.json`。

字段建议：

```sql
CREATE TABLE IF NOT EXISTS app_metadata (
    app_id TEXT PRIMARY KEY,
    process_name TEXT NOT NULL,
    executable_path TEXT,
    app_user_model_id TEXT,
    display_name TEXT NOT NULL,
    category TEXT,
    first_seen_utc TEXT NOT NULL,
    last_seen_utc TEXT NOT NULL
);
```

`app-name-map.json` 只作为初始种子配置。Agent 发现新应用时自动写入 `app_metadata`。

写入权必须保持一致：

```text
1. WPF 不直接写 SQLite
2. WPF 设置页发起 UpdateAppMetadata / UpdatePrivacyRules IPC 命令
3. Agent 接收命令后写入 app_metadata 或 app_privacy_rules
4. Agent 写入完成后重新加载内存规则
5. WPF 通过 GetStatus / Diagnostics 展示命令结果
```

这样既支持设置页修改显示名、分类、排除和标题脱敏规则，也不破坏“Agent 是唯一 SQLite 写入者”的原则。

职责边界：

```text
app_metadata:
    只保存应用身份、显示名、分类、首次发现、最近使用

windows-agent.json:
    MVP 阶段保存全局隐私默认值和 excludedProcesses

app_privacy_rules:
    V2 保存 per-app 排除、标题脱敏、标题匹配规则和分类覆盖
```

### 7.3 foreground_samples

用途：

```text
保存原始采样点
```

用于：

```text
1. 最近采样记录
2. 诊断
3. 回放
4. 原始数据校验
```

不建议直接用它做主统计。

### 7.4 app_sessions

用途：

```text
保存合并后的应用使用会话
```

用于：

```text
1. 今日使用统计
2. 应用排行
3. 最近 24 小时会话
4. active / idle / unknown 统计
```

统计优先基于 `app_sessions`，而不是 `foreground_samples`。

### 7.5 health_state

用途：

```text
保存 Agent 当前健康状态快照
```

建议字段包括：

```text
last_heartbeat_utc
last_sample_utc
last_error_utc
last_error_message
current_state
sample_count_since_start
database_write_error_count
capture_error_count
current_session_id
```

---

## 8. 会话模型设计

### 8.1 会话合并原则

当前逻辑是：

```text
同一个进程 => 扩展会话
进程变化 => 关闭旧会话，开启新会话
```

这是 MVP 可接受方案。

后续建议升级为：

```text
同一 app identity => 扩展会话
app identity 变化 => 关闭旧会话
```

`app identity` 不应只依赖 `process_name`。推荐抽象 `IAppIdentifier`，按优先级生成稳定指纹：

```text
1. ApplicationUserModelId
2. 归一化 executable_path 的哈希
3. process_name
4. optional_window_group
```

说明：

```text
1. UWP / Store 应用优先使用 ApplicationUserModelId
2. executable_path 需要大小写归一、环境变量归一、去除临时包路径噪声
3. 浏览器第一版可按浏览器进程合并，但接口上预留 BrowserTabIdentity
4. normalized_app_name 只用于展示，不作为唯一身份
```

`app_sessions` 建议增加 `session_checksum` 字段，由 app identity、started_at_utc、ended_at_utc 和 close_reason 生成，用于 Agent 异常重启后的去重和恢复校验。

### 8.2 异常重启恢复协议

Agent 启动时必须检查未关闭会话，避免上一次崩溃留下 `ended_at_utc = NULL` 的脏数据。

启动恢复规则：

```text
1. 查询 ended_at_utc IS NULL 的 app_sessions
2. 如果存在，说明上次 Agent 异常退出或被强制结束
3. 读取上次 runtime_state.lastHeartbeatUtc
4. 如果 lastHeartbeatUtc 可用，用它作为 ended_at_utc
5. 如果 lastHeartbeatUtc 不可用，用当前 Agent 启动时间作为 ended_at_utc
6. close_reason = AgentCrashRecovered / AgentRestarted
7. 无法可靠归类的时间写入 unknown_duration
8. 重新计算 session_checksum
9. 写入 agent_events: SessionRecovered
```

注意：

```text
1. 不把崩溃到重启之间的整段时间算给最后一个应用
2. 恢复动作必须在开始新采样前完成
3. 如果存在多条未关闭 session，全部按恢复协议关闭并记录事件
```

### 8.3 暂停时会话处理

当用户点击暂停采集时：

```text
1. Agent 收到 Paused 命令
2. 如果当前存在打开的 app_session
3. 立即关闭当前会话
4. close_reason = Paused
5. Agent 进入 paused loop
```

不要让暂停时间继续累加到某个应用里。

### 8.4 停止时会话处理

当用户点击停止 Agent 时：

```text
1. Agent 收到 Stop 命令
2. 关闭当前会话
3. close_reason = Stopped
4. 写入 AgentStopped 事件
5. 退出进程
```

### 8.5 睡眠和断层处理

Agent 必须显式处理 Windows 电源和会话事件，而不是只依赖采样间隔推断。

需要监听：

```text
SystemEvents.PowerModeChanged
SystemEvents.SessionSwitch
```

由于 Agent 是 Worker Host / BackgroundService，不一定天然具备稳定的桌面消息循环，建议封装事件源接口：

```text
IPowerSessionEventSource
    OnSuspend
    OnResume
    OnSessionLock
    OnSessionUnlock
```

实现建议：

```text
V0:
    使用 SystemEvents.PowerModeChanged / SessionSwitch

V1:
    增加 WindowsMessageEventSource
    通过隐藏窗口或专用消息线程接收 WM_POWERBROADCAST、WM_WTSSESSION_CHANGE
    再转换为 IPowerSessionEventSource 事件
```

这样即使 `SystemEvents` 在无窗口 Worker 进程中表现不稳定，也能替换底层实现而不影响会话聚合逻辑。

处理规则：

```text
Suspend / SessionLock:
    立即关闭当前会话
    close_reason = Sleep / ScreenLock
    记录 SleepDetected / ScreenLocked
    暂停 Stopwatch 口径，不把锁屏或睡眠时间算给当前应用

Resume / SessionUnlock:
    记录 WakeDetected / ScreenUnlocked
    下一次有效采样开启新会话
```

同时保留采样断层兜底判断。如果 Agent 发现两次采样间隔异常大，例如超过：

```text
sampling_interval * 5
```

或超过固定阈值，例如：

```text
60 秒
```

应视为时间断层：

```text
SleepGap / UnknownGap
```

处理方式：

```text
1. 不把断层全部算给上一个应用
2. 关闭当前会话
3. 记录 agent_events
4. 新采样开始后重新开启会话
```

---

## 9. WPF UI 设计

### 9.1 主窗口布局

推荐布局：

```text
┌─────────────────────────────────────────────────────────────┐
│ 顶部栏：QuantifiedSelf   Agent 状态   最近采样   操作按钮     │
├──────────────┬──────────────────────────────────────────────┤
│              │                                              │
│  左侧导航栏   │                页面内容区域                   │
│              │                                              │
│  总览         │                                              │
│  应用         │                                              │
│  会话         │                                              │
│  采样         │                                              │
│  诊断         │                                              │
│  设置         │                                              │
│              │                                              │
└──────────────┴──────────────────────────────────────────────┘
```

### 9.2 顶部控制区

顶部必须包含：

```text
Agent 状态
最近心跳
最近采样
今日总时长
启动按钮
停止按钮
暂停按钮
恢复按钮
刷新按钮
```

按钮启用规则：

```text
NotRunning:
    启动可用
    停止禁用
    暂停禁用
    恢复禁用

Starting:
    全部控制按钮暂时禁用
    显示启动中

Running:
    启动禁用
    停止可用
    暂停可用
    恢复禁用

Paused:
    启动禁用
    停止可用
    暂停禁用
    恢复可用

Stale:
    启动可用
    停止可用或禁用，视 PID 是否存在
    暂停禁用
    恢复禁用

Error:
    显示错误
    允许刷新
    允许停止或重启
```

### 9.3 Dashboard 总览页

Dashboard 是首页。

内容：

```text
1. Agent 当前状态
2. 今日总屏幕使用时长
3. 今日 active 时长
4. 今日 idle 时长
5. 今日 unknown 时长
6. 今日采样数量
7. 应用使用排行
8. 每小时使用趋势
9. 最近错误
10. 最近 5 条会话
```

建议卡片：

```text
状态卡片
今日使用卡片
Active / Idle 卡片
采样健康卡片
数据库状态卡片
最近错误卡片
```

### 9.4 应用页 AppsView

展示应用维度统计。

功能：

```text
1. 今日应用排行
2. 最近 7 天应用排行
3. 应用 active / idle / unknown 占比
4. 应用显示名映射
5. 应用分类
```

字段：

```text
应用名
进程名
使用总时长
Active 时长
Idle 时长
Unknown 时长
会话数
最近使用时间
```

### 9.5 会话页 SessionsView

展示 `app_sessions`。

功能：

```text
1. 最近 24 小时会话
2. 按日期筛选
3. 按应用筛选
4. 按时长排序
5. 查看会话详情
6. 分页加载更多
```

字段：

```text
开始时间
结束时间
应用名
进程名
窗口标题
总时长
Active
Idle
Unknown
关闭原因
```

### 9.6 采样页 SamplesView

展示 `foreground_samples`。

功能：

```text
1. 最近采样
2. 异常采样
3. idle 状态
4. 原始窗口标题
5. 进程路径
6. 限量加载和按需追加
```

字段：

```text
采样时间
应用名
进程名
窗口标题
路径
Idle 秒数
状态
错误
```

### 9.7 诊断页 DiagnosticsView

用于排错。

内容：

```text
1. runtime_state.json 原始摘要
2. health_state 当前内容
3. agent_control.json 当前命令
4. Agent 进程 PID
5. 数据库路径
6. 日志目录
7. 配置文件路径
8. 最近 agent_events
9. 最近错误
```

### 9.8 设置页 SettingsView

功能：

```text
1. 采样间隔
2. idle 阈值
3. idle 摘要间隔
4. 数据保留天数
5. 应用名映射
6. 是否开机自启
7. 是否最小化到托盘
8. 是否启动 UI 时自动启动 Agent
9. 窗口标题脱敏
10. 排除应用列表
11. 打开数据目录
12. 清理历史数据
```

### 9.9 UI 性能约束

WPF 页面必须避免一次性把大量历史数据绑定到 UI。

约束：

```text
1. SessionsView 和 SamplesView 默认 LIMIT 200
2. 历史数据通过“加载更多”追加
3. DataGrid 开启行虚拟化和列虚拟化
4. 刷新时优先原地更新集合，避免整表替换 ItemsSource
5. 后台查询使用 async/await，不阻塞 UI 线程
6. Dispatcher 只用于提交最终 UI 变更
7. Dashboard 面向用户价值，Diagnostics 面向工程诊断
```

建议 XAML 默认开启：

```xml
VirtualizingStackPanel.IsVirtualizing="True"
VirtualizingStackPanel.VirtualizationMode="Recycling"
EnableRowVirtualization="True"
EnableColumnVirtualization="True"
```

---

## 10. WPF MVVM 设计

### 10.1 MainWindowViewModel

职责：

```text
1. 管理当前页面
2. 管理 Agent 顶部状态
3. 管理启动 / 停止 / 暂停 / 恢复命令
4. 管理自动刷新
5. 管理全局错误提示
```

核心属性：

```text
AgentStatus
AgentStatusText
LastHeartbeatText
LastSampleText
TodayTotalText
IsBusy
CurrentPage
CurrentViewModel
```

核心命令：

```text
StartAgentCommand
StopAgentCommand
PauseCollectionCommand
ResumeCollectionCommand
RefreshCommand
NavigateCommand
OpenSettingsCommand
```

### 10.2 DashboardViewModel

职责：

```text
1. 读取今日摘要
2. 读取应用排行
3. 读取趋势图数据
4. 读取最近错误
5. 读取最近会话
```

### 10.3 AppsViewModel

职责：

```text
1. 查询应用统计
2. 按日期范围筛选
3. 按应用排序
4. 查看应用详情
```

### 10.4 SessionsViewModel

职责：

```text
1. 查询会话
2. 筛选会话
3. 排序会话
4. 展示会话详情
```

### 10.5 SamplesViewModel

职责：

```text
1. 查询最近采样
2. 查询异常采样
3. 展示原始数据
```

### 10.6 DiagnosticsViewModel

职责：

```text
1. 读取 runtime_state
2. 读取 health_state
3. 读取 agent_control
4. 读取 agent_events
5. 展示路径和错误
```

### 10.7 SettingsViewModel

职责：

```text
1. 读取配置
2. 校验配置
3. 保存配置
4. 提示是否需要重启 Agent
```

---

## 11. 服务层设计

### 11.1 AgentProcessService

负责进程控制：

```text
StartAgent()
StopAgentGracefully()
KillAgentAsFallback()
IsAgentProcessRunning()
GetAgentProcessInfo()
```

启动逻辑：

```text
1. 查找 Agent exe 路径
2. 检查是否已经运行
3. 启动 Agent
4. 等待 runtime_state 更新
5. 返回启动结果
```

注意：

```text
1. StartAgent 是 WPF 本地进程管理行为
2. Agent 未运行时不存在 IPC 服务，因此不能通过 AgentCommandServer 发送 Start
3. RestartAgent = StopAgentGracefully + 等待退出 + StartAgent
```

停止逻辑：

```text
1. 优先通过 IPC 发送 Stop 命令
2. IPC 不可用时写入 agent_control.json fallback
3. 等待 Agent 优雅退出
4. 如果超时，提示用户
5. 必要时提供强制结束选项
```

### 11.2 AgentControlService

负责发送控制命令：

```text
RequestResumeAsync()
RequestPauseAsync()
RequestStopAsync()
GetStatusAsync()
ReloadConfigAsync()
UpdateAppMetadataAsync()
UpdatePrivacyRulesAsync()
PruneDataAsync()
ClearHistoryAsync()
ReadCurrentCommandAsync()
```

实现优先级：

```text
1. 优先通过 Named Pipe / gRPC over Named Pipes 发送命令并等待 AgentCommandResult
2. IPC 不可用时写 agent_control.json fallback
3. 每个命令必须带 requestId
4. Diagnostics 页显示最近 command request / result
```

### 11.3 AgentStatusService

负责判断状态：

```text
GetStatusAsync()
ReadRuntimeStateAsync()
ReadHealthStateAsync()
CheckProcessAsync()
CheckHeartbeatFreshness()
```

### 11.4 OverviewDataService

负责数据查询：

```text
GetDashboardSummaryAsync()
GetRecentSessionsAsync()
GetRecentSamplesAsync()
GetAppUsageRankAsync()
GetHourlyUsageAsync()
GetAgentEventsAsync()
```

### 11.5 StatisticsService

负责统计聚合：

```text
CalculateTodaySummary()
CalculateAppRank()
CalculateHourlyUsage()
SplitSessionsByHour()
FormatDuration()
```

### 11.6 SettingsService

负责配置读写：

```text
ReadOptionsAsync()
SaveOptionsAsync()
ReadAppNameMapAsync()
SaveAppNameMapAsync()
RequestAgentOptionsUpdateAsync()
RequestPrivacyRulesUpdateAsync()
RequestPruneDataAsync()
RequestClearHistoryAsync()
ValidateOptions()
BackupBeforeSave()
```

边界：

```text
1. app-settings.json 属于 WPF，可由 SettingsService 直接保存
2. windows-agent.json、隐私规则和 app_metadata 属于 Agent 采集域
3. WPF 可以编辑表单，但保存时应通过 IPC 请求 Agent 校验、写入和热加载
4. 如果 Agent 未运行，可先保存为待应用草稿，并提示需要启动 Agent 后应用
```

### 11.7 RefreshService

负责定时刷新：

```text
1. V0 默认 15 秒刷新一次
2. V1 优先订阅 Agent IPC 状态流
3. 页面隐藏时降低刷新频率
4. 防止重复刷新
5. 支持手动立即刷新
6. 后台线程查询，UI 线程只做最终集合更新
```

### 11.8 TrayService

负责托盘：

```text
1. 最小化到托盘
2. 双击显示窗口
3. 右键菜单
4. 显示 Agent 状态
5. 退出 App
```

托盘运行时需要注意：

```text
1. Close to Tray 只 Hide 主窗口
2. 不 Dispose App Host 和后台服务
3. 托盘 tooltip 继续显示最新 Agent 状态
4. 真正退出 App 时再释放 Host
```

---

## 12. 统计口径设计

### 12.1 今日总时长

推荐口径：

```text
基于 app_sessions
统计当天与会话重叠的时间
```

注意：

```text
跨天会话需要切分
不能简单按 start_time 日期归类
```

### 12.2 Active 时长

来源：

```text
app_sessions.active_duration
```

含义：

```text
用户有输入活动时的应用使用时间
```

### 12.3 Idle 时长

来源：

```text
app_sessions.idle_duration
```

含义：

```text
前台应用仍然存在，但用户长时间没有输入
```

### 12.4 Unknown 时长

来源：

```text
app_sessions.unknown_duration
```

含义：

```text
采集失败、状态未知、时间断层等无法可靠归类的时间
```

### 12.5 应用排行

推荐主排行按：

```text
active_duration 降序
```

同时显示：

```text
total_duration
idle_duration
unknown_duration
session_count
```

不要只按总时长排行，否则用户离开电脑时的 idle 时间会放大某些应用。

### 12.6 每小时趋势

第一版简化：

```text
按 session start_time 所在小时统计
```

第二版升级：

```text
将跨小时 session 拆分到每个小时
```

推荐最终采用第二版，因为统计更准确。

---

## 13. 配置设计

### 13.1 windows-agent.json

建议配置：

```json
{
  "samplingIntervalSeconds": 3,
  "idleThresholdSeconds": 60,
  "idleSummaryIntervalMinutes": 5,
  "retentionDays": 30,
  "heartbeatIntervalSeconds": 3,
  "staleThresholdSeconds": 15,
  "enableJsonlJournal": true,
  "enableSessionMerge": true,
  "maskWindowTitles": true,
  "excludedProcesses": [
    "KeePass",
    "1Password",
    "Bitwarden"
  ],
  "excludedTitlePatterns": []
}
```

职责：

```text
1. 保存 Agent 全局配置
2. 保存 MVP 阶段的全局隐私默认值
3. 例如默认 maskWindowTitles=true、全局 excludedProcesses
4. 不保存 Agent 运行时状态
5. 不保存 WPF UI 偏好
```

### 13.2 app-name-map.json

`app-name-map.json` 只作为初始种子配置。长期应用元数据以 SQLite `app_metadata` 为准。

示例：

```json
{
  "chrome": "Google Chrome",
  "msedge": "Microsoft Edge",
  "devenv": "Visual Studio",
  "Code": "Visual Studio Code",
  "explorer": "File Explorer"
}
```

规则来源建议：

```text
MVP:
    windows-agent.json 管全局隐私规则和 excludedProcesses
    app-name-map.json 只做显示名种子
    app_metadata 由 Agent 发现和维护

V2:
    引入 app_privacy_rules.json 或 app_rules 表
    保存用户修改的 per-app 排除、标题脱敏和分类规则
    WPF 仍通过 IPC 请求 Agent 写入，不能直接写 SQLite
```

### 13.3 app-settings.json

这是 WPF App 自己的配置。

建议新增：

```json
{
  "autoStartAgentWhenAppStarts": false,
  "minimizeToTray": true,
  "closeToTray": true,
  "refreshIntervalSeconds": 15,
  "theme": "Light",
  "lastSelectedPage": "Dashboard"
}
```

注意：

```text
windows-agent.json 是 Agent 配置
app-settings.json 是 WPF App 配置
两者不要混在一起
```

---

## 14. 路径设计

建议统一由 `WindowsAgentPaths` 或新的 `WindowsAppPaths` 管理。

根目录优先级：

```text
1. QUANTIFIEDSELF_WINDOWS_AGENT_ROOT 环境变量
2. %LocalAppData%\QuantifiedSelf\WindowsAgent
3. Debug / Dev 模式下允许 D:\QuantifiedSelf\WindowsAgent
```

目录结构：

```text
WindowsAgentRoot
├─ config
│  ├─ windows-agent.json
│  ├─ app-name-map.json
│  └─ app-settings.json
│
├─ data
│  └─ quantified_self_windows.db
│
├─ logs
│  ├─ foreground_samples_20260617.jsonl
│  ├─ agent_events_20260617.jsonl
│  └─ agent.log
│
└─ runtime
   ├─ runtime_state.json
   ├─ agent_control.json
   └─ app_runtime_state.json
```

---

## 15. UI 状态流转

### 15.1 启动 Agent

```text
用户点击“启动 Agent”
    ↓
WPF 检查 Agent 是否已运行
    ↓
如果未运行，启动 Agent exe
    ↓
WPF 显示 Starting
    ↓
通过 IPC GetStatus 或轮询 runtime_state / health_state fallback
    ↓
收到新 heartbeat
    ↓
状态变为 Running
```

### 15.2 暂停采集

```text
用户点击“暂停采集”
    ↓
WPF 通过 IPC 发送 Pause 命令
    ↓
Agent 返回 command accepted，UI 显示 Pausing
    ↓
AgentStateMachine 处理命令
    ↓
Agent 关闭当前 session
    ↓
Agent 停止采样
    ↓
Agent 写 Paused heartbeat
    ↓
WPF 显示 Paused
```

如果 IPC 不可用，WPF 写 `agent_control.json`，其中 `command = Pause`、`desiredState = Paused`，再通过状态快照确认结果。

### 15.3 恢复采集

```text
用户点击“恢复采集”
    ↓
WPF 通过 IPC 发送 Resume 命令
    ↓
Agent 返回 command accepted，UI 显示 Resuming
    ↓
AgentStateMachine 处理命令
    ↓
Agent 恢复采样
    ↓
写入 foreground_samples
    ↓
开启新 session
    ↓
WPF 显示 Running
```

如果 IPC 不可用，WPF 写 `agent_control.json`，其中 `command = Resume`、`desiredState = Running`，再通过状态快照确认结果。

### 15.4 停止 Agent

```text
用户点击“停止 Agent”
    ↓
WPF 通过 IPC 发送 Stop 命令
    ↓
Agent 返回 command accepted，UI 显示 Stopping
    ↓
Agent 关闭当前 session
    ↓
Agent 写 AgentStopped 事件
    ↓
Agent 退出进程
    ↓
WPF 显示 NotRunning
```

如果 IPC 不可用，WPF 写 `agent_control.json`，其中 `command = Stop`、`desiredState = Stopped`，再等待进程退出和状态快照更新。

---

## 16. 异常处理设计

### 16.1 Agent 启动失败

可能原因：

```text
1. Agent exe 路径不存在
2. 权限不足
3. 单实例互斥体已存在
4. 配置文件损坏
5. 数据库无法打开
```

UI 处理：

```text
1. 顶部显示 Error
2. 弹出简短错误提示
3. Diagnostics 页显示详细错误
4. 提供打开日志目录按钮
```

### 16.2 SQLite 读取失败

处理：

```text
1. UI 不崩溃
2. 显示数据库读取失败
3. 保留上一次成功数据显示
4. Diagnostics 页显示异常详情
```

### 16.3 Agent heartbeat 过旧

处理：

```text
1. 状态显示 Stale
2. 提示 Agent 可能卡死或异常退出
3. 允许用户重新启动 Agent
```

### 16.4 控制命令无响应

例如点击暂停后，Agent 长时间没有变成 Paused。

处理：

```text
1. 显示“等待 Agent 响应”
2. 超时后显示“Agent 未响应”
3. 提供刷新、重试、停止 Agent
```

---

## 17. 安全与隐私设计

这个软件会记录窗口标题、进程名、可执行文件路径，属于较敏感的本地行为数据。

建议：

```text
1. 所有数据默认只保存在本机
2. 不上传云端
3. 设置页明确显示数据库路径
4. 提供一键打开数据目录
5. 提供清理历史数据功能，并通过 Agent ClearHistory 命令执行
6. 默认开启窗口标题脱敏
7. MVP 提供排除应用列表
8. 隐私规则在 Agent 采集阶段生效
9. Dashboard 或初始化界面明确显示“数据仅保存在本机”
```

MVP 隐私规则直接写在 `windows-agent.json` 中，不再新增独立排除列表文件。

配置片段示例：

```json
{
  "excludedProcesses": [
    "KeePass",
    "1Password",
    "Bitwarden"
  ],
  "maskWindowTitles": true
}
```

采集规则：

```text
1. excludedProcesses 命中的应用不写入 foreground_samples 和 app_sessions
2. maskWindowTitles = true 时，Agent 在写入前脱敏窗口标题
3. 浏览器、密码管理器、聊天工具可默认启用标题脱敏
4. UI 展示层脱敏不能替代 Agent 采集阶段脱敏
5. 清理历史数据需要清理 SQLite、JSONL 和历史 runtime 快照
6. 清理历史数据必须由 Agent 执行，WPF 不直接删除数据库或日志文件
7. ClearHistory 后必须重写当前 runtime_state 并立即写入 heartbeat，保持 UI 可观测
```

---

## 18. 第一版 MVP 范围

第一版不要做太复杂，建议只做这些：

```text
1. WPF 主窗口
2. 左侧导航栏
3. 顶部 Agent 控制栏
4. 启动 Agent
5. 停止 Agent
6. 暂停采集
7. 恢复采集
8. Agent DesiredState / ActualState 状态机
9. V0 agent_control.json fallback
10. V1 预留 Named Pipe / gRPC 接口边界
11. 显示 Agent 状态
12. 显示今日统计
13. 显示应用排行 Top 5
14. 显示最近 5 条会话
15. Diagnostics 基础页
16. 15 秒自动刷新
17. 窗口标题脱敏
18. 排除应用列表
19. 数据目录可见和一键打开
```

第一版暂缓：

```text
1. 深色主题
2. 复杂动画
3. 完整应用分类
4. 高级图表
5. 安装包
6. 云同步
7. 多用户
8. 浏览器网页级识别
9. 全量 SamplesView 历史浏览
```

---

## 19. 第二版增强范围

第二版再做：

```text
1. Named Pipe / gRPC over Named Pipes 主控制通道
2. Agent 状态流订阅
3. ScottPlot 图表
4. 最近 7 天趋势
5. 应用分类
6. 设置页完整化
7. 托盘图标
8. 开机自启动
9. 数据保留策略
10. 清理历史数据
11. 分页 SamplesView
```

---

## 20. 第三版产品化范围

第三版考虑：

```text
1. 安装包
2. 自动更新
3. 崩溃日志收集
4. 更漂亮的 Windows 11 风格 UI
5. 数据导出 CSV
6. 数据备份与恢复
7. 周报/月报
8. 应用使用目标和提醒
9. 本地数据加密
10. Windows Service / 计划任务 / 高权限 Agent 方案评估
```

---

## 21. 推荐开发顺序

严格按纵向切片顺序做：

```text
1. 新建 QuantifiedSelf.Windows.App WPF 项目
2. 引用 Core 和 Infrastructure
3. 搭建 MainWindow + 顶部控制栏
4. 实现 AgentActualState / AgentDesiredState
5. 实现 AgentStateMachine 骨架
6. 实现 AgentStatusService
7. 显示 Agent 状态
8. 实现 AgentProcessService
9. 实现启动 Agent
10. 新增 agent_control.json fallback
11. Agent 支持 Pausing / Paused / Resuming / Running / Stopping
12. WPF 支持暂停 / 恢复 / 停止
13. 实现 Diagnostics 页
14. 实现 SQLite WAL 初始化和只读查询约束
15. 跑通最小采样链路
16. 跑通 app_sessions 合并
17. 实现隐私规则：标题脱敏和排除应用
18. 实现 Dashboard 今日统计
19. 实现应用排行 Top 5
20. 实现最近 5 条会话
21. 实现自动刷新
22. 增加异常提示
23. 引入 Named Pipe / gRPC 主控制通道
24. 再做分页 SamplesView、图表和美化
```

建议在真实 Win32 抓取完成前增加一个 Mock Agent：

```text
1. 随机生成 runtime_state
2. 写入少量 app_sessions 测试数据
3. 模拟 Running / Paused / Error 状态
4. 让 WPF 和数据查询可以并行开发
```

---

## 22. 最终技术组合

建议最终采用：

```text
语言：
    C#

平台：
    .NET 8

后台：
    Worker Host / BackgroundService

界面：
    WPF

架构：
    双进程 + Agent 状态机 + 轻量 MVVM

MVVM：
    CommunityToolkit.Mvvm

数据库：
    SQLite + WAL

图表：
    ScottPlot.WPF

托盘：
    H.NotifyIcon.Wpf

进程控制：
    ProcessStartInfo + PID 检查 + runtime_state

控制通信：
    V0: agent_control.json fallback
    V1: Named Pipe / gRPC over Named Pipes

状态通信：
    IPC GetStatus / 状态流 + runtime_state.json + health_state

审计日志：
    agent_events_YYYYMMDD.jsonl + SQLite agent_events 轻量索引

应用身份：
    AppIdentity / app_metadata

隐私：
    Agent 采集阶段标题脱敏 + 排除应用
```

---

## 23. 最终一句话方案

QuantifiedSelf Windows 端应设计为一个基于 C#/.NET 8 的本地双进程系统：

```text
Agent 作为后台采集进程，负责前台窗口采样、idle 判断、会话合并和 SQLite 持久化；
WPF App 作为统一控制台，负责启动、停止、暂停、恢复 Agent，并读取本地数据展示统计信息；
两者通过 IPC 进行实时控制，通过 SQLite + WAL 读取结构化数据，通过 runtime_state.json、health_state、agent_control.json fallback 和 JSONL 完成状态兜底、审计与排错。
```

这套方案既保留了后台采集的稳定性，又让 UI 成为真正的控制中心，而不是单纯的观察窗。
