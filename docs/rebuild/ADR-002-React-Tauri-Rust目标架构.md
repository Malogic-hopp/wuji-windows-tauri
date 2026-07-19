# ADR-002：WUJI React + Tauri + Rust 目标架构

状态：Proposed；依赖规范 Accepted 后再接受
决策日期：2026-07-18
决策范围：WUJI Windows 桌面应用 v2
替代关系：Accepted 后拟取代 ADR-001；在此之前 ADR-001 仍描述当前 `.NET Bridge` 过渡实现
关联文档：[README.md](./README.md)、[01-产品语义与指标词典.md](./01-产品语义与指标词典.md)、[02-行为分析领域模型.md](./02-行为分析领域模型.md)、[03-上下文识别与算法版本规范.md](./03-上下文识别与算法版本规范.md)、[04-SQLite-v2与持久化读模型.md](./04-SQLite-v2与持久化读模型.md)、[05-Rust-Agent运行时设计.md](./05-Rust-Agent运行时设计.md)、[06-本地接口与错误合同.md](./06-本地接口与错误合同.md)、[07-迁移、切换与旧系统退役计划.md](./07-迁移、切换与旧系统退役计划.md)、[08-验收门禁与测试矩阵.md](./08-验收门禁与测试矩阵.md)、[09-Tauri-Rust-Rebuild-v0.1实施基线.md](./09-Tauri-Rust-Rebuild-v0.1实施基线.md)、[migration-status.md](./migration-status.md)

规范强度：本文中的“必须/不得、应当、可以、候选”遵循 [README.md](./README.md#3-规范用语)。本文只接受技术栈与进程边界；行为算法、Schema 和协议由关联规范接受后方可实施为稳定产品语义。

适用层级：本文描述长期目标架构。当前 dev-only Rebuild v0.1 只实现其最小子集，实施范围和明确延期以 [09](./09-Tauri-Rust-Rebuild-v0.1实施基线.md) 为准；v0.1 完成不代表本 ADR 已 Accepted 或 production 门禁已通过。

## 1. 背景

当前系统已经同时包含 WPF、React 19、Tauri 2、Rust Shell、`.NET Client.Bridge`、C# Application/Client/Core/Infrastructure 和 C# Agent。现有 Tauri 调用路径为：

```text
React WebView
→ Tauri Rust command
→ Rust BridgeSupervisor
→ .NET Client.Bridge
→ IWujiClient
→ Application / Client / Infrastructure
→ SQLite / Agent
```

ADR-001 的目标是以最低风险验证 React/Tauri UI，保留 C# 业务和数据实现。该策略已经完成过渡验证，但继续扩展会长期维护三语言合同、Bridge 生命周期、C# 与 Rust 两套宿主以及 WPF/Tauri 双 UI，不能满足本轮“简化最终架构”的目标。

同时，当前 C# Agent 以一个每秒 Tick 驱动的大状态机串联控制、采集、隐私、写库、Session、事件、心跳和维护，Observation 与 Session 写入也不处于同一事务。本轮重构不逐行翻译该结构，而重新建立清晰的运行时流水线和行为分析模型。

## 2. 决策

WUJI v2 采用以下技术组合：

```text
React 19 + TypeScript
Tauri 2 + Rust
独立 Rust Agent
共享 Rust Core / Storage
SQLite + WAL
```

最终运行时只保留两个长期进程：

```text
WUJI Desktop（Tauri）
WUJI Agent（当前用户会话中的独立 Rust 进程）
```

React WebView 是 Desktop 进程内的表现层，不是第三个业务服务。`.NET Client.Bridge`、C# Application/Client/Infrastructure、C# Agent 和 WPF App 不属于最终运行时。

## 3. 目标进程模型

```text
┌──────────────────────────────────────────────────────┐
│ WUJI Desktop                                         │
│                                                      │
│ React 19 + TypeScript                                │
│   └─ semantic Tauri commands/events                  │
│                                                      │
│ Tauri 2 Rust Host                                    │
│   ├─ Window / Tray / Single Instance                 │
│   ├─ Agent Process Controller                        │
│   ├─ Settings Writer                                 │
│   ├─ Read-only Query Services                        │
│   └─ Local Export / Startup Registration             │
└───────────────┬───────────────────────┬──────────────┘
                │ Named Pipe            │ SQLite read-only
                │ commands/status       │
┌───────────────▼───────────────────────▼──────────────┐
│ WUJI Agent                          SQLite WAL       │
│                                                      │
│ Capture Thread → bounded mpsc → Processor            │
│                    → priority lanes → Single Writer  │
│                                                      │
│ CommandServer / HeartbeatLoop / MaintenanceLoop      │
└──────────────────────────────────────────────────────┘
```

## 4. 强制架构不变量

以下内容是实现必须遵守的强制边界。

### 4.1 Agent 与 Desktop 生命周期独立

- Desktop 退出、隐藏、崩溃或升级不自动停止 Agent；
- 用户显式 CaptureStop 只停止采集；只有 Shutdown、卸载流程或受控升级可以停止 Agent 进程；
- Desktop 启动时应发现并连接已经运行的同 channel Agent；
- Agent 进程不存在时，由 Desktop Process Controller 通过固定受信任路径创建进程；`StartAgentProcess` 与 Pipe 内的 `CaptureStart` 是不同动作；
- Agent 单实例按 Windows 当前用户和 runtime channel 隔离；
- Bridge 或 UI 生命周期不得重新成为 Agent 生命周期的所有者。

### 4.2 Agent 是正常运行时唯一 SQLite 写入者

- Agent 的 Single SQLite Writer 独占正常业务写入；
- Observation、Activity Segment、Work/Context 派生、投影、事件和 runtime heartbeat 都通过 Writer 优先级 lane 串行写入；
- CommandServer、HeartbeatLoop、MaintenanceLoop 不自行打开第二个读写连接；
- Tauri Desktop 只使用 read-only/query-only 连接；
- 离线 v1→v2 导入器和迁移工具只能在 Agent 已停止并获得独占锁时写入，不属于正常运行时例外。

### 4.3 React 只访问语义接口

React 不得获得：

- 任意 SQL；
- 任意 Shell 或命令行执行；
- 任意文件系统路径读写；
- 任意进程启动参数；
- Named Pipe 名称、SID、数据库路径和 Agent executable 路径；
- 原始窗口标题、完整可执行路径或未清洗异常。
- confirmation token、IPC session capability、trusted-action proof 或导出目标路径。

React 只能调用固定的 Tauri commands，例如：

```text
agent_get_status
agent_process_ensure_running
capture_start
capture_pause
capture_resume
capture_stop
agent_process_shutdown
activity_get_today
activity_get_timeline
activity_get_trends
settings_get
settings_update
privacy_clear_history_request
```

不得增加 `execute_sql`、`read_file`、`forward_method` 等通用接口。

### 4.4 Desktop 只读历史数据，控制走 IPC

- Dashboard、Timeline、Trends、Apps、Insights 和 Diagnostics 的历史查询由 Tauri 通过共享 Storage 查询层只读 SQLite；
- pause、resume、stop、reload、prune、clear 等运行时命令通过 Named Pipe 发给 Agent；
- UI 在 Agent 未运行时仍能读取既有历史数据；
- 实时 Agent 状态优先来自 IPC；SQLite `agent_runtime` 只表示最后已知 heartbeat，不得单独证明 Agent 当前仍在运行；
- 历史查询只读取不可变 Query Snapshot/Result Set；一个 Snapshot 可以通过不重叠 Slice 组合多个历史算法世代；
- Native Slice 的不重叠按 `(TimeEpochOrdinal, UTC, FactCursor)` 复合边界判定；Projection 必须绑定其实际使用的具体组件 Result Set，而不只绑定 Generation；
- 零事实数据库使用零 Slice 空 Snapshot 和 nullable published-through，不创建假 Projection；
- 不再维护 `runtime_state.json`、`health_state.json` 和 `agent_control.json` 作为正常产品协议。

### 4.5 Settings 所有权唯一

- Tauri Desktop 是用户设置文件的唯一写入者；
- 设置使用原子替换和版本化 Schema；
- Agent 只读取设置，并通过 IPC reload 或受控文件观察更新内存快照；
- Agent 未运行时，用户仍可修改设置；
- Agent 不把运行状态写回用户设置；
- React 只提交白名单字段 DTO，不直接读写 JSON。

### 4.6 隐私在 Agent 内存流水线中最先落实

- Win32 捕获后的原始标题和路径只允许在受控内存范围内存在；
- 隐私排除发生在 Observation 持久化和日志之前；
- 排除应用不写 Observation、Activity Segment、Project Hint 或事件 payload；
- 默认不持久化完整可执行路径；
- 标题默认脱敏或不保存；
- 结构化日志不得包含原始窗口标题、路径、SID、用户名或机器名。

## 5. Desktop 职责

Tauri Desktop 负责：

- React WebView 宿主；
- 主窗口、托盘、关闭到托盘、单实例和激活；
- 启动、发现和连接 Agent；
- 用户设置与应用设置；
- SQLite read-only 查询和安全 DTO 投影；
- 登录启动注册；
- 导出和清理确认交互；
- 安装、更新和版本兼容提示；
- 将 Rust DTO 以固定 Tauri commands 暴露给 React。

Tauri Desktop 不负责：

- 前台窗口采集；
- Observation、Segment 或投影写入；
- 在 React 中计算 Context、Interruption 或 Focus；
- Agent 心跳和维护；
- 在 Agent 离线时使用第二个写入器修改数据库。

## 6. Agent 职责

Rust Agent 负责：

- 当前用户和 channel 单实例；
- Win32 前台窗口、PID、进程身份、标题和 idle 捕获；
- 隐私过滤、应用标准化和活动状态判断；
- Activity Segment、独立 Work Generation、Context 派生任务和不可变 Result Set/读模型发布；
- SQLite Schema 初始化、迁移和唯一写入；
- Named Pipe CommandServer；
- heartbeat、可观测性和维护；
- 数据保留、投影重建和历史清理；
- 启动恢复和崩溃一致性处理。

Agent 不负责：

- UI 导航和图表表现；
- 读取 React 参数中的路径、SQL 或 channel；
- 作为 Windows Service 跨会话采集；
- 上传云端、远程遥测或账户同步；
- 生成无法解释的黑盒产品结论。

## 7. Agent 内部运行时

Agent 不采用单一大 Tick 状态机。推荐任务如下：

```text
ForegroundCaptureThread
    每 1 秒检查调度，按 SamplingInterval 决定是否捕获
    同步执行 Win32 调用和带超时标题读取
        ↓ bounded mpsc<RawObservation>

ObservationProcessor
    AppIdentity 标准化
    隐私过滤
    ActivityState 判断
    ProjectHint 提取
        ↓ bounded mpsc<WriterMessage>

SingleSQLiteWriter
    Observation + Activity Segment 原子写入
    更新 provisional projections
    串行执行 heartbeat / maintenance / rebuild / publish
```

独立任务：

```text
CommandServer
HealthHeartbeatLoop
MaintenanceLoop
AgentSupervisor
```

Win32 的 `SendMessageTimeoutW`、进程路径查询和 rusqlite 操作是阻塞调用，必须放在专用线程或 `spawn_blocking` 边界中，不得阻塞 Tokio async worker。

队列必须有界。队列满时不得无限增长内存；首轮策略为丢弃新 Observation、累计安全诊断计数，并禁止用固定采样周期补算丢失时长。

## 8. Rust Workspace 边界

目标仓库结构：

```text
apps/
  desktop/
    src/                 React + TypeScript
    src-tauri/           Tauri Host
  agent/
    src/                 Rust Agent binary

crates/
  wuji-core/
    domain/
    settings/
    privacy/
    analytics/
    protocol/
    error.rs
  wuji-storage/
    migrations/
    writer/
    queries/
    projections/
```

约束：

- `wuji-core` 不依赖 Tauri、rusqlite 或 Win32；
- `wuji-storage` 依赖 Core，提供 Writer 和只读 Query API；
- Agent 依赖 Core、Storage 和 Windows adapter；
- Tauri Host 依赖 Core、Storage query 和 Agent IPC client；
- React 类型从 Rust command DTO 生成，不再维护 C#/Rust/TypeScript 三端 Schema 生成链；
- 不为每个 service/interface 建立独立 crate，只有出现真实独立复用或编译边界时才拆分。

## 9. IPC 决策

Desktop 与 Agent 使用 Windows Named Pipe。协议采用版本化的长度受限 JSON 消息，DTO 定义在共享 Rust Core 中。

强制要求：

- Pipe DACL 只允许当前 Windows 用户；
- Pipe 名按用户身份 hash 与 runtime channel 隔离；
- DACL/SID/Session 只是第一层；Agent 必须从内核取得 client PID/token，验证预期完整性、固定安装路径、file identity、production Authenticode 发布者和签名兼容清单，拒绝 WebView 子进程与未列入清单的同用户进程；
- 验证通过后建立仅 Rust Host/Agent 内存持有的随机 session capability，业务帧绑定连接、序号和 capability proof；React 不得读取或转发；
- request ID 使用 ULID；
- 固定协议版本、最大 payload、timeout 和稳定错误码；
- side-effect 命令支持 request ID 去重；
- stdout/stderr 不作为产品 IPC；
- 不提供网络监听端口；
- 不保留控制文件 fallback；
- 状态订阅可后续增加，但第一版可使用安全低频轮询。

## 10. SQLite 决策

- SQLite 是行为事实、派生结果和运行审计的权威数据源；用户 Settings 仍由版本化 JSON 持有；
- 使用 WAL、foreign keys、busy timeout 和明确的 synchronous 策略；
- 正常运行时一个写连接，Desktop 使用短生命周期只读查询；
- Observation 与 staging 尾部在同一事务提交；查询可见 Result Set 只有 Seal 后才能由 Snapshot 发布；
- Work、Context、Interruption 和 Switch 分属可版本化世代；Work 不依赖 Context Generation；
- Projection Result Set 通过外键绑定具体 Segmentation/Work/Analysis Result Set；Summary/Legacy 由 `data_kind` 表示，`result_kind` 只表示组件；
- 小时和每日读模型服务 Today、Trends 和 Heatmap；
- Timeline 才读取明细 Segment/Observation；
- Schema 使用只前进的编号迁移；
- 任何 Schema 不兼容不得自动 DROP 用户表；
- v1 数据通过离线导入器迁入新的 v2 数据库，原库先备份、不原地破坏。

详细设计见 [04-SQLite-v2与持久化读模型.md](./04-SQLite-v2与持久化读模型.md)。

## 11. 运行 channel 与路径

保留当前已验证的 runtime channel 语义：

```text
prod → WUJI
dev  → WUJI Dev
```

channel 必须隔离：

- data root；
- SQLite database；
- Named Pipe；
- Agent mutex；
- 登录启动项；
- 日志和诊断；
- v1/v2 影子验证数据。

React 不得传入 channel 或 data root。channel 由编译配置、宿主启动参数或受控开发入口决定。

## 12. 自启动和 Windows 会话

Agent 需要访问当前交互用户的前台窗口，因此不作为 Windows Service 运行。

产品化方案采用当前用户登录启动：

- Agent 可以由安装器或稳定启动器注册到当前用户；
- Desktop 可以独立配置是否随登录启动和是否启动后隐藏到托盘；
- 注册命令不得包含用户可编辑任意参数；
- 升级后必须刷新到受信任的新 binary 路径；
- 卸载必须在确认后停止并注销 Agent；
- 启动注册按 dev/prod channel 隔离。

## 13. 错误与可观测性

内部错误使用 `thiserror` 建立稳定分类，跨边界错误使用安全结构：

```text
code
kind
retryable
safeMessage
requestId
fieldErrors（可选）
```

日志使用 `tracing`，至少区分：

```text
desktop
agent.capture
agent.processor
agent.writer
agent.ipc
agent.maintenance
storage.query
```

实时健康状态来自 IPC；最后已持久化 heartbeat 位于 SQLite `agent_runtime`。不再用两个内容重叠的 runtime/health JSON 文件维护第二套真相。

## 14. 安全边界

- Tauri capability 不授予通用 shell、fs、http、sql 或 process；
- 所有 Tauri command 使用具体 DTO 和固定参数；
- React CSP 禁止远程脚本、eval、iframe 和对象嵌入；
- SQLite、设置和日志目录使用当前用户访问控制；
- Named Pipe 限当前用户并认证受信任 Desktop binary/session capability；知道 Pipe 名或拥有同一 SID 不授予控制权；
- 数据默认不上传、不使用 CDN 字体、不依赖在线服务；
- 清空历史、导出、卸载删除数据和隐私削弱必须使用独立于 WebView DOM 的 Win32 原生确认；确认能力只在 Rust 内存中单次消费，React 无 token/consume command；
- Diagnostics 默认隐藏路径、PID、Pipe、SQLite 和原始状态，仅在折叠高级区域显示已脱敏信息。

## 15. 非目标

本 ADR 不决定：

- 云同步、账户体系和远程 API；
- macOS/Linux Agent；
- 机器学习上下文分类；
- SQLite 加密具体实现；
- 完整自动更新提供方；
- 用户手工编辑 Context 的最终交互；
- UI 视觉稿和图表库最终选择；
- 每个算法阈值的最终产品数值。

## 16. 被拒绝的方案

### 16.1 永久保留 .NET Bridge

拒绝原因：继续维护 Rust→.NET→C# 多层调用、三端合同、Bridge 生命周期和打包依赖，无法达到最终简化目标。

### 16.2 把 Agent 嵌入 Tauri 进程

拒绝原因：Desktop 真正退出或崩溃会停止采集，不满足生命周期独立要求。

### 16.3 Windows Service Agent

拒绝原因：Service 与当前交互桌面 Session 隔离，不能可靠读取当前用户前台窗口。

### 16.4 React 直接查询 SQLite

拒绝原因：扩大 WebView 权限面、暴露 Schema、复制查询规则并增加任意 SQL 风险。

### 16.5 所有查询都通过 Agent IPC

拒绝原因：Agent 停止时 UI 无法查看历史数据，且会把只读分析请求与采集写入生命周期耦合。

### 16.6 原地修改 v1 数据库

拒绝原因：当前 v1 Schema 和算法语义不足，原地迁移会放大回滚和数据损坏风险。v2 使用新库离线导入。

## 17. 后果

正面结果：

- 正常运行时从 React/Rust/.NET/C# 多栈收敛为 React/Rust；
- 删除 Bridge、文件 fallback 和多份状态真相；
- Agent 采集、处理和写入职责清晰；
- SQLite 写入具备统一事务边界；
- UI 在 Agent 离线时仍可查询历史；
- 行为分析算法可以版本化重建；
- 30 天和 12 周趋势拥有稳定读模型基础。

代价与风险：

- Win32、SQLite、统计和设置逻辑需要在 Rust 中重新实现和验证；
- v1→v2 数据导入必须处理历史语义差异；
- WebView2 的 DPI、无障碍和 High Contrast 仍需完整验收；
- Context 识别会引入 provisional/finalized 和重算复杂度；
- 单写入器需要明确背压、维护和崩溃恢复策略；
- 在正式切换前需要同时维护 v1 参考实现和 v2 dev channel。

## 18. 实施约束与接受门禁

- 不逐行翻译 C# `AgentStateMachine`；
- 不把现有 Core/Application/Client/Infrastructure 项目数量映射成同等数量的 Rust crate；
- 先接受产品语义、领域模型、算法、SQLite、Agent Runtime 和接口合同，再将本 ADR 转为 Accepted；
- 用脱敏黄金样本固定现有正确行为和 v2 新语义；
- v1 与 v2 Agent 绝不同时写同一数据库；
- v2 在独立 dev channel 完成 shadow、crash、soak 和 UI 验收后才允许 production cutover；
- 删除旧系统必须有单独退役门禁，不能因为新系统能编译就提前删除回滚入口。

迁移阶段、数据库 pointer 切换、生产回滚和旧系统删除条件以 [07](./07-迁移、切换与旧系统退役计划.md) 为准；对应的 G-ADR、G-DDL、G-DEV、G-PROD 和 G-RETIRE 证据以 [08](./08-验收门禁与测试矩阵.md) 为准。仓库当前完成度只在 [migration-status.md](./migration-status.md) 中记录，不构成本 ADR 的接受证据。

最低接受门禁：

- Work Block/Context 依赖、时区和数据质量语义通过产品/领域评审；
- migration DDL 与逻辑 Schema drift gate 有可执行方案；
- Agent 背压、Degraded、维护和重建 cutover 通过故障设计评审；
- IPC 兼容、安全、幂等和升级矩阵通过评审；
- 具体组件 Result Set FK、复合 Slice boundary、空 Snapshot 与 Writer Degraded Lease/GC 三态通过合成测试设计评审；
- production Desktop binary 验签/session capability 和可信原生确认链通过威胁模型与故障测试评审；
- Focus、碎片时间和 Plaintext 持久化继续明确延期，不以空列冒充能力。
