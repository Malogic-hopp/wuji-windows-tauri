# 下一步计划审核：Agent Events 与 Diagnostics MVP（2026-06-19）

## 审核范围

本审核文档只针对以下两份文档做对齐审查：

```text
docs\下一步计划-2026-06-19-AgentEvents与DiagnosticsMVP.md
docs\QuantifiedSelf Windows 端完整重构方案.md
```

本审核不再使用《真实采样稳定性手动验收清单》作为依据。  
原因是当前审核重点不是验证真实采样 MVP 是否通过，而是判断 Agent Events 与 Diagnostics MVP 是否和完整重构方案保持一致，并给出执行层面的修订建议。

## 总体结论

当前《下一步计划：Agent Events 与 Diagnostics MVP》方向正确，可以作为下一阶段执行稿。

它和完整重构方案中的几个核心原则是一致的：

```text
Agent 是唯一数据写入者
WPF App 只负责控制和展示
SQLite 是主数据源
JSONL 是审计和排错日志
runtime_state / health_state 是当前态快照
agent_control.json 是 V0 控制 fallback
Diagnostics 面向工程诊断
Dashboard 面向日常使用
隐私规则必须在 Agent 采集阶段生效
```

更重要的是，这份计划没有急着进入托盘、安装包、完整 Settings、Named Pipe / gRPC 或复杂图表，而是先补“历史诊断”和“事件审计”。这个优先级和完整重构方案的长期架构是兼容的。

建议保留当前阶段定位：

```text
从真实采样 MVP 升级到可诊断 MVP
```

不要把它扩展成产品化阶段。

---

## 1. 本计划和完整重构方案的对齐点

### 1.1 Agent Events 属于长期架构，不是临时补丁

完整重构方案在数据层和最终技术组合中都明确提到：

```text
agent_events_YYYYMMDD.jsonl
SQLite agent_events 轻量索引
JSONL 是审计和排错日志
DiagnosticsView 读取 agent_events 和最近错误
```

因此当前计划新增：

```text
agent_events 表
AgentEventRepository
AgentEventJournal
AgentEventWriter
Diagnostics Recent Events / Recent Errors
```

不是额外复杂化，而是在补完整方案中本来就预留的诊断层。

### 1.2 暂不做 IPC 是合理的

完整重构方案里 Named Pipe / gRPC 是 V1 长期主控制通道，但当前项目仍处于 V0/V0.5 阶段，`agent_control.json` fallback 已经能支撑 Pause / Resume / Stop。

所以本计划暂不做：

```text
Named Pipe / gRPC
Agent 状态流订阅
复杂控制响应 UI
```

这个取舍是合理的。  
当前更应该先把 file fallback 命令链路中的关键行为记录下来：

```text
CommandDetected
CommandAccepted
CommandCompleted
CommandFailed
CommandInvalidJson
```

以后迁移到 IPC 时，这套事件语义仍然可复用。

### 1.3 Diagnostics 第一版只读 SQLite 是正确边界

完整重构方案里 WPF App 的职责是读取本地数据并展示，不直接写采集域数据。  
当前计划要求：

```text
Diagnostics 第一版只查 SQLite
不解析 JSONL
WPF 只读 agent_events
```

这是正确边界。  
JSONL 作为审计文件保留，但第一版不做浏览器，可以避免过早引入：

```text
文件锁
大文件分页
日期切换
滚动性能
解析失败兜底
```

### 1.4 隐私要求与完整重构方案一致

完整重构方案强调：

```text
隐私规则必须在 Agent 采集阶段生效
UI 展示层脱敏不能替代 Agent 采集阶段脱敏
默认开启窗口标题脱敏
excludedProcesses 命中时不写 foreground_samples 和 app_sessions
```

当前计划进一步要求：

```text
不写真实窗口标题到 message / payload_json
payload_json 白名单
不写 exception.ToString()
PrivacyFiltered 只写泛化原因
CaptureFailed 只写 errorCode / exceptionType / shortMessage
```

这个方向应保留，而且应作为本阶段硬约束。

---

## 2. 建议收紧的地方

### 2.1 明确 JSONL 开关语义

完整重构方案里同时存在：

```text
foreground_samples_YYYYMMDD.jsonl
agent_events_YYYYMMDD.jsonl
```

当前配置中已有：

```json
"enableJsonlJournal": true
```

但下一步计划没有明确这个开关是否同时控制：

```text
foreground samples JSONL
agent events JSONL
```

建议在计划中补充一种明确口径：

```text
方案 A：
enableJsonlJournal 同时控制 foreground_samples JSONL 和 agent_events JSONL。

方案 B：
保留 enableJsonlJournal 控制采样 journal，
新增 enableAgentEventJournal 控制 agent_events_YYYYMMDD.jsonl。
```

更推荐方案 B，原因是事件审计和采样流水的用途不同，后续也可能分别打开或关闭。

### 2.2 `CaptureFailed` 的范围需要拆清

当前计划中 `CaptureFailed` 同时可能覆盖：

```text
Win32 前台窗口读取失败
采样对象构造失败
写 foreground_samples 失败
session 合并或写库失败
```

这会让 Diagnostics 中的错误过于泛化。

建议二选一：

```text
方案 A：保留 CaptureFailed，但 errorCode 必须区分阶段
    ForegroundWindowUnavailable
    ProcessLookupFailed
    SampleWriteFailed
    SessionAggregationFailed
    SessionWriteFailed

方案 B：拆成更明确的事件类型
    CaptureFailed
    SampleWriteFailed
    SessionWriteFailed
```

第一版可以选方案 A，改动较小，但必须要求 `error_code` 足够稳定。

### 2.3 `CommandInvalidJson` 需要定义 request_id 为空时的行为

坏 `agent_control.json` 很可能无法解析出：

```text
requestId
command
desiredState
```

建议补充：

```text
CommandInvalidJson 的 request_id 允许为空
event_level = Warning
error_code = CommandInvalidJson
message 只写泛化文案
payload_json 只允许写 quarantined=true / fileKind=agent_control
不写原始 JSON 内容
不写完整文件路径
```

这样既方便测试，也不会因为坏控制文件泄露敏感内容。

### 2.4 `SessionClosed` 应通过 closeReason 表达 ProcessChanged

当前计划中推荐事件范围包含：

```text
SessionStarted
SessionClosed
```

但在 SessionAggregator 接入边界里又提到低成本时可先记录：

```text
AgentPaused
AgentStopped
ProcessChanged
```

建议不要新增 `ProcessChanged` 事件类型。  
更推荐统一为：

```text
SessionClosed
payload_json.closeReason = ProcessChanged
```

这样事件类型更收敛，也和现有 `app_sessions.close_reason` 口径一致。

### 2.5 Recent Events 查询需要稳定排序

当前索引建议是：

```sql
idx_agent_events_time
idx_agent_events_type
idx_agent_events_level_time
```

建议计划里补充 Diagnostics 查询排序：

```sql
ORDER BY event_time_utc DESC, id DESC
```

或在实现中直接使用：

```sql
ORDER BY id DESC
```

同一个 tick 内可能写入多条事件，只按时间排序可能导致展示顺序不稳定。

### 2.6 明确 SQLite 与 JSONL 是 best-effort 双写

计划已经要求 AgentEventWriter 旁路化，这是对的。  
建议再明确：

```text
SQLite agent_events 与 JSONL agent_events_YYYYMMDD.jsonl 不要求强一致。
AgentEventWriter 是 best-effort 双写。
SQLite 写失败时，Diagnostics 可能看不到该事件。
JSONL 写失败时，SQLite 查询仍可展示事件。
两者都失败时，只记录 lastEventWriteError / lastJournalWriteError 到内存或 health_state，不递归写事件。
```

这可以避免后续把事件系统实现成采集主链路上的强事务依赖。

### 2.7 Diagnostics 可以展示事件系统自身健康

完整重构方案里 DiagnosticsView 职责包括展示路径和错误。  
当前计划已经提到可以记录：

```text
lastEventWriteError
lastJournalWriteError
```

建议把它落到 Diagnostics 第一版中：

```text
事件 SQLite 写入状态
事件 JSONL 写入状态
当前 agent_events JSONL 文件路径
最近一次事件写入错误
最近一次 journal 写入错误
```

这不需要复杂 UI，但能回答一个关键问题：

```text
为什么 Recent Events 没有新事件？
```

### 2.8 命令事件需要兼容未来 IPC

完整重构方案最终会从 `agent_control.json` 迁移到 Named Pipe / gRPC。  
当前计划的命令事件不要写死为“文件命令专用语义”。

建议在 `source` 或 `payload_json` 中允许：

```text
commandSource = FileFallback
commandSource = NamedPipe
commandSource = Grpc
```

第一版实际只写：

```text
FileFallback
```

这样以后接 IPC 时不需要重新定义事件体系。

### 2.9 `AgentStarted` 和 `AgentStopped` 建议记录版本与退出原因

完整重构方案中的 runtime_state 建议包含：

```text
version
processId
startedAtUtc
state
```

事件中不应写敏感路径，但可以写非敏感运行信息。

建议：

```text
AgentStarted payload_json:
    processId
    version
    actualState

AgentStopped payload_json:
    processId
    actualState
    stopReason
```

不要写：

```text
commandLine
exePath
fullUserPath
```

### 2.10 事件量验收需要量化

当前验收标准中有：

```text
普通采样不会大量刷 agent_events
PrivacyFiltered / CaptureFailed 连续触发时不会刷爆 agent_events
```

建议量化，便于后续手动或自动验收：

```text
30 分钟正常运行时，agent_events 数量应显著小于 foreground_samples。
普通采样不应产生 SampleCaptured / Heartbeat 类事件。
同一 PrivacyFiltered key 连续触发 5 分钟，最多写 5 条。
同一 CaptureFailed key 连续触发 5 分钟，最多写 5 条。
```

如果实现采用“每 key 60 秒 1 条”，这些标准自然成立。

---

## 3. 建议调整后的事件范围

建议第一版事件范围保持克制：

```text
AgentStarted
AgentStopped
AgentPaused
AgentResumed

CommandDetected
CommandAccepted
CommandCompleted
CommandFailed
CommandInvalidJson

ConfigReloaded
PrivacyFiltered
CaptureFailed

SessionStarted
SessionClosed
```

暂不做：

```text
HealthChanged
SampleCaptured
Heartbeat
DashboardRefreshed
普通 UI 刷新
ProcessChanged 独立事件
```

其中：

```text
ProcessChanged 通过 SessionClosed.closeReason 表达
Heartbeat 继续由 runtime_state / health_state 表达
SampleCaptured 继续由 foreground_samples 表达
HealthChanged 等 health_state 模型稳定后再做
```

---

## 4. 建议调整后的 payload 白名单

建议把白名单分成“通用字段”和“事件专用字段”，实现时更不容易误放敏感内容。

通用允许字段：

```text
requestId
actualState
desiredState
errorCode
exceptionType
shortMessage
```

Agent 生命周期允许字段：

```text
processId
version
stopReason
```

命令事件允许字段：

```text
command
commandSource
accepted
completed
requestedBy
requestedAtUtc
waitForCompletion
timeoutMilliseconds
```

隐私事件允许字段：

```text
ruleType
processName
```

session 事件允许字段：

```text
sessionId
durationSeconds
activeSeconds
idleSeconds
unknownSeconds
closeReason
```

禁止字段保持不变：

```text
windowTitle
rawTitle
executablePath
commandLine
fullUserPath
exception.ToString()
rawJson
```

---

## 5. 建议调整后的开发顺序

当前开发顺序基本合理。  
建议微调为先让查询和页面空态跑通，再逐步接事件：

```text
1. 定义 Core Events 模型
2. 新增 agent_events 表和索引
3. 实现 AgentEventRepository
4. 实现 DiagnosticsQueryService 查询最近事件 / 最近错误
5. Diagnostics 页面先展示空列表 / 无事件状态
6. 实现 AgentEventJournal
7. 实现 AgentEventWriter，要求 best-effort 双写且失败不影响主循环
8. 实现 AgentEventRateLimiter
9. 接生命周期事件：AgentStarted / AgentStopped / AgentPaused / AgentResumed
10. 接命令事件：CommandDetected / CommandAccepted / CommandCompleted / CommandFailed / CommandInvalidJson
11. 接采集与隐私事件：CaptureFailed / PrivacyFiltered，并做限流
12. 轻量接 session 事件：SessionStarted / SessionClosed
13. Diagnostics 页面展示事件系统健康、JSONL 路径、Recent Events、Recent Errors
14. 补测试
15. 手动运行一段时间，确认事件量合理
```

这样做的好处是：

```text
Diagnostics 骨架可以更早验证
每接一种事件都能马上在 UI 看到
事件落地和事件展示不会等到最后才集成
```

---

## 6. 建议补充的测试

当前计划中的测试列表已经比较完整。  
建议额外补充：

```text
AgentEventRepository_ReturnsEventsWithStableOrdering
AgentEventWriter_ContinuesJournalWhenRepositoryFails
AgentEventWriter_ContinuesRepositoryWhenJournalFails
AgentEventWriter_RecordsLastWriteErrorsWithoutRecursiveEvents
AgentStateMachine_WritesCommandInvalidJsonWithoutRequestId
AgentStateMachine_WritesCommandSourceForFileFallback
AgentStateMachine_WritesSessionClosedWithProcessChangedCloseReason
AgentEventPayloadSanitizer_RemovesForbiddenKeys
DiagnosticsQueryService_ReturnsEmptyListsWhenAgentEventsTableIsEmpty
DiagnosticsQueryService_DoesNotParseJsonl
```

测试重点仍然是：

```text
事件存在
事件顺序稳定
事件量受控
敏感字段不泄露
写事件失败不影响主流程
Diagnostics 只读 SQLite
```

---

## 7. 可以不改的内容

以下内容当前计划已经处理得比较好，不建议再展开：

```text
不做 Named Pipe / gRPC
不做托盘
不做安装包
不做完整 Settings
不做 JSONL 浏览器
不记录普通采样事件
不记录 Heartbeat 事件
不让 SessionAggregator 直接依赖 AgentEventWriter
不让 WPF 写 agent_events
```

这些克制很重要。  
如果本阶段范围继续膨胀，容易把“可诊断 MVP”拖成“半个产品化版本”，反而影响稳定性。

---

## 8. 审核结论

这份下一步计划可以执行。  
它与完整重构方案的大方向一致，而且优先级正确。

建议在执行前补充或修订以下要点：

```text
1. 明确 agent events JSONL 与 enableJsonlJournal 的配置关系
2. 拆清 CaptureFailed 的错误阶段或 errorCode 体系
3. 定义 CommandInvalidJson 在 request_id 为空时的事件形态
4. 用 SessionClosed.closeReason 表达 ProcessChanged，不新增 ProcessChanged 事件
5. Recent Events 查询增加稳定排序
6. 明确 SQLite 与 JSONL 是 best-effort 双写，不要求强一致
7. Diagnostics 展示事件系统自身健康
8. 命令事件预留 commandSource，以兼容未来 IPC
9. AgentStarted / AgentStopped 记录非敏感运行元信息
10. 将事件量验收标准量化
```

完成这些调整后，这份计划就可以作为 Agent Events 与 Diagnostics MVP 的开发依据。
