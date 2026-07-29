# WUJI v2 Rust Agent 运行时设计

状态：Draft
最后更新：2026-07-18
架构决策：[ADR-002-React-Tauri-Rust目标架构.md](./ADR-002-React-Tauri-Rust目标架构.md)
领域模型：[02-行为分析领域模型.md](./02-行为分析领域模型.md)
存储设计：[04-SQLite-v2与持久化读模型.md](./04-SQLite-v2与持久化读模型.md)
接口合同：[06-本地接口与错误合同.md](./06-本地接口与错误合同.md)

## 1. 目标

Agent 采用独立异步任务和明确消息边界，不使用一个每秒 Tick 串行控制所有职责。状态机仅用于 Agent 生命周期与命令合法性，不承担采集和聚合调度。

## 2. 任务拓扑

```text
ForegroundCaptureLoop / blocking Win32 adapter
        ↓ bounded capture channel
ObservationProcessor
        ↓ bounded writer capture lane
SingleSQLiteWriter

CommandServer ───────────────→ writer control/exclusive lane
HealthHeartbeatLoop ─────────→ writer control lane
MaintenanceLoop ─────────────→ writer maintenance lane
ResultPublisher ─────────────→ seal Result Set / activate Snapshot
AgentSupervisor ← task health/fatal signals
```

推荐依赖：Tokio、windows、rusqlite、serde/serde_json、tracing、tokio::sync::mpsc、thiserror、ULID。

Win32 title/process 查询和 rusqlite 操作属于阻塞工作，必须在专用线程或 `spawn_blocking` 边界，不得阻塞 Tokio worker。

## 3. Agent 生命周期

```text
Starting → Ready/Running ↔ Paused
                    ↓
                 Degraded
                    ↓ retry success
                 Running

任何非终态 → Stopping → Stopped
不可恢复存储/协议错误 → Faulted → Stopping
受控独占操作 → Maintenance → Ready/Running 或 Faulted
```

- `Ready`：Agent 进程、IPC、Storage 可用但 Capture 尚未开始；
- `Running`：采集和写入正常；
- `Paused`：Agent 进程仍在，用户暂停 Capture，Work Block 已关闭；
- `Degraded`：Writer 可恢复失败，Capture 暂停，IPC 状态/CaptureStop/Shutdown/诊断仍可用；
- `Maintenance`：Clear、数据库替换或升级等独占操作；
- `Faulted`：不可恢复错误，禁止继续写业务数据。

状态转换必须由显式事件触发，不由“大 Tick”遍历所有子系统。

## 4. Foreground Capture Loop

- 使用单调计时器每秒检查调度，按 Settings 的 sampling interval 决定是否捕获；
- 在**捕获尝试开始时**分配 Capture Sequence；即使 Win32 失败或队列满也不复用序号；
- Capture Sequence 仅在 Runtime Instance 内单调；Writer 接受输入时另行分配数据库全局 Fact Cursor；
- `GetForegroundWindow`、`GetWindowThreadProcessId`、`QueryFullProcessImageNameW`、`GetLastInputInfo` 使用受控 adapter；
- 标题读取使用 `SendMessageTimeoutW`、`SMTO_ABORTIFHUNG` 和固定上限；
- 每次 Raw Observation 带 wall-clock UTC 与单调 tick，用于检测时钟倒退；
- 队列满时丢弃新 Raw Observation、清除敏感内存并发送无敏感质量计数；不无限等待或增长内存。

UTC 倒退、单调 tick 不连续或休眠恢复时创建新的 Time Epoch；旧/新 epoch 之间不补算。

## 5. Observation Processor

固定顺序：

1. 检查 capture status；
2. 应用隐私排除；
3. 标准化 App Identity；
4. 提取 allowlisted safe features；
5. 计算 Activity State；
6. 清除 PID、完整标题和路径；
7. 发送 Foreground Observation 或 Data Quality message。

Processor 不写数据库、不维护长生命周期 Session、不写原始日志。隐私排除仍发送不含 App/标题/路径的 `PrivacyExcluded` 质量区间。

## 6. Writer lanes 与调度

Single Writer 拥有一个连接和四类逻辑 lane：

| 优先级 | Lane | 消息示例 | 饥饿边界 |
|---:|---|---|---|
| 0 | Control | CaptureStop、Shutdown、status barrier、heartbeat、cutover | 每轮优先，必须有速率限制 |
| 1 | Capture | Observation、quality batch | 正常运行不得被 maintenance 连续跳过 |
| 2 | Maintenance | prune chunk、rebuild chunk、checkpoint | 每 chunk 后让出 |
| 3 | Exclusive | Clear、major migration handoff | 先完成显式 quiesce |

实现可以使用多个 bounded mpsc 或一个带类型的调度器，但必须满足：

- Control flood 不得无限饿死 Capture；状态轮询在 CommandServer 内存中合并；
- 每处理最多 N 个 Control 消息必须尝试一个 Capture batch；
- Maintenance 每个事务受行数/时长上限约束；
- queue depth、oldest age、drop count 分 lane 观测；
- CaptureStop 与 Shutdown 在任何 Degraded/Maintenance 状态都可达；Shutdown 在安全持久化终态 receipt 后退出进程。

具体 N、容量和 chunk 大小是运行参数，必须经过压力测试并版本化配置默认值。

## 7. Maintenance 行为

### 7.1 在线维护

Prune、历史 Rebuild 和 Checkpoint 使用分块 Maintenance lane。Capture Loop 继续采集，Capture queue 保持有界；Writer 每个 chunk 后优先排空 Capture。若 backlog 达高水位，Maintenance 自动 yield；若持续超限则暂停 Maintenance，不丢弃已经接受的 Capture。

### 7.2 独占维护

Clear、major Schema migration、v1 import、数据库文件替换必须：

1. Command receipt 进入 Accepted；
2. 停止产生新 Capture，关闭当前 Work Block；
3. 排空已接受的 Processor/Writer 消息至明确水位；
4. Desktop 通过协议关闭 reader pool；
5. 进入 Maintenance 并执行/移交 migrator；
6. 验证后重新 Ready，按用户原状态决定是否恢复 Running。

失败不得自动恢复采集到不确定数据库；必须报告稳定错误并保留可回滚 pointer。

## 8. Writer 错误策略

### 8.1 可恢复错误

`SQLITE_BUSY`、短暂 I/O、可判定磁盘压力等：

- 当前事务回滚；
- Agent 进入 Degraded，暂停 Capture Loop，阻止队列继续增长；
- Control/IPC 保持可用，heartbeat 写失败时由 IPC 报告内存状态；
- 指数退避 + jitter 重试，设最大间隔但不无限高频；
- 成功后写质量 gap、创建新连续性边界并恢复原先 Running/Paused 状态。

### 8.2 不可恢复错误

Schema major 不兼容、migration checksum 漂移、`foreign_key_check`/完整性失败、确认 corruption：

- 禁止业务写入和采集；
- 状态变为 Faulted；
- IPC 仍允许 `GetStatus`、`GetCommandStatus`、`CaptureStop`、`ShutdownAgent` 和安全诊断；
- 不执行自动 DROP、重建空库或覆盖旧库；
- Supervisor 完成日志 flush 后受控退出，或等待 Desktop 发起修复流程。

## 9. 背压和数据质量

每个丢弃/失败都必须形成可持久化质量事实。若 Writer 当前不可用，先在固定容量的安全计数器中按 kind 聚合；恢复后写入区间和计数。聚合结构不得包含敏感字符串。

队列丢弃产生同 Runtime Instance 的 Capture Sequence gap，后续 Writer 必须关闭 Activity/Work 连续性。跨重启发布水位使用 Fact Cursor。禁止使用 sampling interval 推测 gap 中时长。

用户 CaptureStart/CapturePause/CaptureStop、计划时段、独占维护和系统 sleep/logoff 必须驱动 Tracking Expectation Interval。上次意图为 Expected 而 Agent 意外离线的 gap 计为 AgentUnavailable；用户主动暂停/停止和 ScheduledOff 不进入 expected tracking 分母。

## 10. Heartbeat 与健康

Heartbeat Loop 周期性收集：Agent state、runtime instance、last capture sequence、last Fact Cursor、各 lane depth/age、drop/error count、active settings revision 和 Query Snapshot。

- IPC 返回内存实时状态；
- Writer 成功时将 last-known 快照写入 SQLite；
- Desktop 的 Stale 判断以最后成功 IPC 响应为主；
- IPC 不可用时，SQLite 只显示“最后记录于 …”，不能据此断言 Running；
- heartbeat 消息不修改行为时长。

Stale 的精确阈值和 DTO 见接口合同。

## 11. Command Server

Command Server 负责身份验证、Hello/Ready 握手、payload 限制、request ID 校验和 side-effect receipt 查询。它不直接操作 SQLite；命令转换为 Writer/Supervisor message。

身份验证不是“同一用户即可”：Server 在业务握手前必须通过内核获得 client PID/token，验证用户、logon Session、完整性级别、固定安装路径和 production Authenticode 签名/发布者清单；dev unsigned 仅允许在隔离 dev channel 使用固定 binary hash。验证成功后才通过 bootstrap challenge 建立仅保存在 Rust Host/Agent 内存中的随机 session capability，后续业务帧绑定连接、单调序号和 capability proof。WebView 子进程不符合 Desktop binary 身份，也拿不到 capability，必须被拒绝。完整合同见 [06](./06-本地接口与错误合同.md#3-named-pipe-安全与客户端认证)。

状态查询可以从内存快照响应；副作用命令必须先持久化 receipt，再开始执行。客户端 timeout 不取消已接受命令。若命令会导致 Agent 退出，终态 receipt 必须在退出前提交；Desktop 可在 Pipe 消失后通过只读 Storage Query 查询。

## 12. Settings reload

Desktop 写入完整新 JSON 后发 `ReloadSettings(revision)`。Agent：

1. 读取受信任固定路径；
2. 校验 file schema、revision、全部字段和跨字段不变量；
3. 计算影响矩阵；
4. 将完整 Settings 投影为 Capture Revision 与 Segmentation/Work/Analysis/Calendar Profile；
5. 通过 Writer 记录 Observed revision；
6. 原子替换内存快照并把新 revision 标记为 pending effectivity；
7. 第一条使用该 revision 的事实事务关闭旧区间并从该 Fact Cursor 打开新区间；若此前又被替换则不创建空区间；
8. 只有发生变化且实际生效的算法 Profile 才创建相应 Generation/Result Set Slice。

任一步失败时旧 Settings 全部继续生效；不存在部分应用。响应返回 active/rejected revision 与安全字段错误。

## 13. 启动与崩溃恢复

启动顺序：单实例锁 → 路径/channel 验证 → 打开并迁移 DB → 完整性/receipt/Generation/Result Set 检查 → Settings 启动对账 → 恢复最后 Query Snapshot → 启动 IPC → Ready → 按 tracking intent 决定是否 CaptureStart。

每次启动在启用任何 Snapshot GC 前，Writer 都必须持久化恢复宽限水位并进入 `RecoveryGrace`；Desktop 可在此窗口为崩溃/离线期间读取的 Snapshot 重新获取 Lease。宽限结束、必要 Lease 已建立且完整性检查通过后才允许 GC。

若数据库尚无已提交事实且没有 Active Snapshot，启动事务创建零 Slice、`empty_reason=NoFacts` 的 Active Snapshot；其 published-through 为 null，并固定当前 Settings Revision 与默认 Calendar Generation。Clear 后恢复使用已经提交的 `empty_reason=Cleared` Snapshot。空状态不创建 Projection Result Set，也不把数据库 `last_fact_cursor=0` 或清理前 cursor 冒充发布水位。

Settings 启动对账是强制步骤：

1. 读取固定路径的最新完整 JSON；
2. 比较文件 revision/digest、`settings_revisions` 和 runtime active revision；
3. 同 revision 不同 digest 视为冲突并拒绝采集；低 revision 视为未经授权回滚；
4. 新 revision 即使 Desktop 尚未来得及发 Reload，也执行完整验证并补写 Observed；
5. 验证成功后原子写 Active 并标记 pending effectivity，第一条事实再创建 Interval；失败写 Rejected 并继续使用旧有效 revision；
6. 若没有旧有效 revision 且新文件无效，保持 Ready/Faulted-safe，不开始 Capture。

恢复规则：

- 清除未持久化 Raw Observation；
- Running Job 标为 Failed/Interrupted，再创建新 Job；
- 不把最后 Observation 到启动时刻补算；
- 开放 tail 从最后可验证 anchor 重建；
- 上次处于 Running 只表示 last-known，必须创建新 runtime instance 和 Time Epoch；
- 未完成 side-effect receipt 按命令特定恢复策略继续或标为 NeedsAttention，不重复执行已提交步骤。

## 14. Supervisor

Supervisor 监听每个关键任务的退出和 fatal signal：

- Capture/Processor 意外退出：关闭连续性、进入 Degraded 并有限次数重启；
- Writer 退出：立即暂停 Capture，进入 Faulted/Degraded；
- Command Server 退出：尝试重建 listener，状态写安全日志；
- 多次快速崩溃触发熔断，不形成无限 restart loop；
- Shutdown 顺序为 Capture → Processor drain → Writer flush/close Work → Seal/或明确放弃 staging → 写终态 receipt → IPC `WillExit` → DB/log close。

Writer 进入 Degraded/Faulted 时，Supervisor 在允许历史查询前锁存 `GC SuspendedWriterUnavailable`。此状态下 Command Server 仍可从内存返回安全状态，Desktop 可无 Lease 只读 Active/已知 Snapshot，但 Publisher、Prune 和 GC 全部禁止；已过期 Lease 也不得触发回收。Writer 恢复后必须先提交 GC 恢复宽限水位，等待至少一个 Lease TTL 与 Desktop 重连窗口，并允许客户端重新获取/续租，之后才能重新启用 GC。无法提交宽限水位则继续暂停 GC。

## 15. Result Publisher 与稳定读取

Publisher 只处理越过 provisional tail 的 staging 范围。一次发布：只构建发生变化的 Segmentation/Work/Analysis 组件 → 以这些**具体 Sealed 候选组件 ID**构建 Projection dependency mask/source Result Set FK → 校验 Generation、复合 Fact Boundary、coverage、水位、Identity Resolution、Calendar 与守恒 → Seal 组件 → 复用未变化组件和旧 Snapshot 未受影响 Slice → 验证 Slice 组件 ID 与 Projection 来源逐项相等 → 原子激活新 Snapshot。仅 Generation ID 相同不构成来源一致。Context-only 变化必须复用原 Work Result Set；已 Sealed 行永不更新。

Publisher 可以高频发布当前日的有界尾部，但必须依赖 Snapshot GC 避免无限增长。发布失败只丢弃 Building Result Set，旧 Snapshot 继续服务。

同一毫秒内发生 Effectivity/Profile 边界时，Publisher 必须用完整 Fact Boundary 切分，允许相邻 Slice 共享 UTC 毫秒但不共享复合事实位置；不得先做 UTC-only overlap 检查再用 cursor 例外绕过。没有事实的请求范围只返回 Empty；不得发布零行组件来占位。

## 16. 容量与运行门禁

必须验证：

- 1/3/10 秒采样和持续高频切换；
- Capture/Control/Maintenance 公平性；
- SQLite busy/full/corrupt 与磁盘恢复；
- 休眠、锁屏、UTC 倒退和跨日；
- 24 小时与 7 天 soak，无无界内存/任务/日志增长；
- rebuild 时 Capture backlog 和 drop 为可解释质量结果；
- CaptureStop/Shutdown 在所有状态的最坏响应时间；
- crash point 覆盖每个事务和 exclusive maintenance 阶段。

在这些门禁和依赖文档 Accepted 前，本运行时设计不得标为 Accepted。
