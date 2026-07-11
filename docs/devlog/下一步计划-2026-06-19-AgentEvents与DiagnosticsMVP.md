# 下一步计划：Agent Events 与 Diagnostics MVP（2026-06-19，review 后修订版）

## 当前状态

真实采样 MVP 已经基本闭环：

```text
真实 Win32 前台窗口采样
idle / active 判断
采集阶段隐私过滤
SQLite foreground_samples 落库
app_sessions 合并
Pause / Resume / Stop 控制
Dashboard 基础统计展示
中文终端输出
WUJI / 吾迹 产品展示名
```

这说明当前项目已经从 Mock MVP 进入真实可用 MVP。  
下一阶段重点不应是托盘、安装包、复杂 UI 或完整 Settings，而是补齐长期运行和排错能力。

## 阶段目标

下一阶段目标：

```text
Agent Events 与 Diagnostics MVP
```

一句话目标：

```text
让 WUJI 不只“能采集”，还要“能解释自己为什么这么运行”。
```

也就是说，系统需要记录关键运行事件，并能在 Diagnostics 中快速看到最近发生了什么。

## 为什么现在做这个

当前已有：

```text
runtime_state.json
health_state.json
foreground_samples
app_sessions
Dashboard
```

这些可以回答：

```text
Agent 当前是什么状态？
今天用了多久？
最近采到了哪些应用？
session 是否合并正确？
```

但还不能很好回答：

```text
某次为什么没有采样？
Pause 命令有没有被 Agent 消费？
配置重载有没有生效？
隐私规则是否命中？
Win32 采样是否失败？
某个 session 为什么被关闭？
坏 agent_control.json 是什么时候出现的？
```

`runtime_state.json` 和 `health_state.json` 更像“当前态快照”，不适合承载历史诊断。  
因此下一步应该引入事件体系。

## 本阶段不做

本阶段暂不优先做：

```text
Named Pipe / gRPC
托盘 TrayService
安装包
开机自启
Windows Service
完整 Settings 编辑页
完整 AppsView / SessionsView / SamplesView
复杂图表
7 天趋势
PruneData / ClearHistory
JSONL 浏览器
```

这些属于体验增强或产品化能力。  
当前更关键的是先让 Agent 的关键行为可追踪、可解释、可诊断。

## 设计原则

```text
1. agent_events 记录低频关键事件，不记录每次普通采样
2. JSONL 作为审计和排错日志，SQLite agent_events 作为查询索引
3. Diagnostics 第一版只查 SQLite，不解析 JSONL
4. 不把真实窗口标题写入事件 message / payload_json
5. payload_json 采用白名单原则
6. PrivacyFiltered / CaptureFailed 必须限流
7. AgentEventWriter 必须旁路化，写事件失败不能影响采样主循环
8. Diagnostics 面向排错，Dashboard 面向日常使用
9. Agent 仍然是唯一写入 SQLite 的进程
10. SessionAggregator 不直接依赖 AgentEventWriter
11. SQLite agent_events 与 JSONL 是 best-effort 双写，不要求强一致
12. 命令事件语义要兼容未来 Named Pipe / gRPC
```

## 命名统一

事件等级统一使用：

```text
AgentEventLevel
event_level
```

不要混用：

```text
severity
event_level
AgentEventSeverity
AgentEventLevel
```

建议枚举：

```csharp
public enum AgentEventLevel
{
    Debug,
    Info,
    Warning,
    Error,
    Critical
}
```

第一版可以定义 `Debug`，但不一定实际写 Debug 事件。

## 推荐事件范围

第一版建议覆盖以下事件：

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

暂不做或后置：

```text
HealthChanged
SampleCaptured
Heartbeat
DashboardRefreshed
普通 UI 刷新
```

原因：

```text
HealthChanged 第一版不容易定义清楚，容易变吵
普通采样已经进入 foreground_samples
heartbeat 已进入 runtime_state / health_state
高频事件会让 agent_events 和 JSONL 变吵
```

## 命令事件语义

命令事件必须从 Agent 视角定义。  
当前控制链路是：

```text
WPF 写 agent_control.json
Agent 读取 agent_control.json
Agent 校验命令
Agent 执行命令
Agent 删除或隔离控制文件
```

WPF 不应该直接写 `agent_events`。  
因此事件语义建议如下：

```text
CommandDetected
    Agent 发现 agent_control.json 中存在命令

CommandAccepted
    Agent 验证命令合法，准备执行

CommandCompleted
    Agent 执行完成，状态转换或动作已完成

CommandFailed
    Agent 收到命令，但执行失败

CommandInvalidJson
    Agent 读到坏 JSON，并将控制文件隔离为 .bad
```

`CommandInvalidJson` 需要单独约定：

```text
request_id 允许为空
event_level = Warning
error_code = CommandInvalidJson
message 只写泛化文案
payload_json 只允许写 quarantined=true / fileKind=agent_control
不写原始 JSON 内容
不写完整文件路径
```

命令来源需要预留未来 IPC：

```text
commandSource = FileFallback
commandSource = NamedPipe
commandSource = Grpc
```

第一版实际只写：

```text
commandSource = FileFallback
```

Diagnostics 中应该能看出：

```text
命令有没有被发现
命令有没有被接受
命令有没有执行完成
命令失败的 errorCode 是什么
```

## 数据模型建议

### Core 模型

建议新增：

```text
src/QuantifiedSelf.Windows.Core/Events/AgentEvent.cs
src/QuantifiedSelf.Windows.Core/Events/AgentEventType.cs
src/QuantifiedSelf.Windows.Core/Events/AgentEventLevel.cs
```

字段建议：

```text
id
event_time_utc
event_type
event_level
message
source
request_id
error_code
process_name
session_id
payload_json
```

注意：

```text
message 要短，偏中文直白
payload_json 只放白名单内的非敏感结构化字段
不要写真实窗口标题
```

### SQLite 表

建议新增：

```sql
CREATE TABLE IF NOT EXISTS agent_events (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    event_time_utc TEXT NOT NULL,
    event_type TEXT NOT NULL,
    event_level TEXT NOT NULL,
    message TEXT NOT NULL,
    source TEXT,
    request_id TEXT,
    error_code TEXT,
    process_name TEXT,
    session_id INTEGER,
    payload_json TEXT
);

CREATE INDEX IF NOT EXISTS idx_agent_events_time
ON agent_events(event_time_utc);

CREATE INDEX IF NOT EXISTS idx_agent_events_type
ON agent_events(event_type);

CREATE INDEX IF NOT EXISTS idx_agent_events_level_time
ON agent_events(event_level, event_time_utc);
```

`idx_agent_events_level_time` 用于 Diagnostics 查询最近错误：

```sql
WHERE event_level IN ('Warning', 'Error', 'Critical')
ORDER BY event_time_utc DESC, id DESC
LIMIT 10;
```

Recent Events / Recent Errors 查询必须稳定排序。  
推荐：

```sql
ORDER BY event_time_utc DESC, id DESC
```

如果实现中事件时间精度不足，也可以直接使用：

```sql
ORDER BY id DESC
```

### JSONL 文件

建议路径：

```text
%LocalAppData%\WUJI\WindowsAgent\logs\agent_events_YYYYMMDD.jsonl
```

每行一条 JSON：

```json
{"eventTimeUtc":"2026-06-19T14:00:00Z","eventType":"AgentStarted","eventLevel":"Info","message":"Agent 已启动"}
```

JSONL 只追加，不反复重写。

### JSONL 开关语义

当前配置中已有：

```json
"enableJsonlJournal": true
```

完整方案中可能同时存在：

```text
foreground_samples_YYYYMMDD.jsonl
agent_events_YYYYMMDD.jsonl
```

建议不要让一个开关长期同时控制两类不同用途的 journal。  
第一版推荐新增：

```json
"enableAgentEventJournal": true
```

语义：

```text
enableJsonlJournal
    控制采样类 JSONL，若未来实现 foreground_samples_YYYYMMDD.jsonl，则使用此开关

enableAgentEventJournal
    控制 agent_events_YYYYMMDD.jsonl
```

这样后续可以分别关闭高频采样流水和低频事件审计。

## payload_json 白名单

`payload_json` 必须采用白名单原则，不靠开发时自觉。

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
quarantined
fileKind
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

禁止写入：

```text
windowTitle
rawTitle
executablePath
commandLine
fullUserPath
exception.ToString()
rawJson
```

尤其不要写完整异常：

```text
exception.ToString()
```

它可能包含路径、命令行、用户名或其他敏感信息。  
第一版只写：

```text
exceptionType
errorCode
shortMessage
```

## 事件限流

以下事件必须限流：

```text
PrivacyFiltered
CaptureFailed
```

原因：

```text
用户如果长时间停留在被排除应用上，PrivacyFiltered 会按采样频率刷屏
Win32 采样持续失败时，CaptureFailed 也会快速刷爆事件表
```

第一版建议：

```text
同 eventType + processName + errorCode / ruleType，60 秒内最多写 1 条
```

如果实现要更简单，也可以先做：

```text
PrivacyFiltered 每分钟最多 1 条
CaptureFailed 每分钟最多 1 条
```

但推荐优先按 key 限流，Diagnostics 的信息量更好。

## 推荐代码结构

建议新增：

```text
src/QuantifiedSelf.Windows.Infrastructure/Events/AgentEventRepository.cs
src/QuantifiedSelf.Windows.Infrastructure/Events/AgentEventJournal.cs
src/QuantifiedSelf.Windows.Agent/Events/AgentEventWriter.cs
src/QuantifiedSelf.Windows.Agent/Events/AgentEventRateLimiter.cs
```

职责：

```text
AgentEventRepository
    写 SQLite agent_events
    查询最近事件
    查询最近错误

AgentEventJournal
    写 JSONL 文件
    按日期滚动文件

AgentEventWriter
    Agent 侧统一入口
    一次调用尝试写 SQLite 和 JSONL
    写事件失败不向采样主循环传播异常
    暴露最近一次 SQLite / JSONL 写入错误状态

AgentEventRateLimiter
    对 PrivacyFiltered / CaptureFailed 做低成本限流
```

## AgentEventWriter 旁路化要求

事件系统是诊断增强，不是采集主链路依赖。  
必须满足：

```text
1. 写 SQLite 事件失败，不影响采样 / session / 控制命令
2. 写 SQLite 失败时，仍尽量继续写 JSONL
3. 写 JSONL 失败时，不影响 SQLite 和采样主循环
4. SQLite 和 JSONL 都失败，也不能让异常冒泡到 TickAsync
5. 可以把 lastEventWriteError / lastJournalWriteError 记录到内存或 health_state，但不要递归写事件
```

这是一种 best-effort 双写：

```text
SQLite agent_events 与 JSONL agent_events_YYYYMMDD.jsonl 不要求强一致
SQLite 写失败时，Diagnostics 可能看不到该事件
JSONL 写失败时，SQLite 查询仍可展示事件
两者都失败时，只记录 lastEventWriteError / lastJournalWriteError
不要为了记录“事件写入失败”再递归写 agent_events
```

伪代码：

```text
AgentEventWriter.WriteAsync(event)
    try write SQLite
    catch remember lastEventWriteError

    try write JSONL
    catch remember lastJournalWriteError

    never throw to AgentStateMachine TickAsync
```

## SessionAggregator 接入边界

不要让 `SessionAggregator` 直接依赖 `AgentEventWriter`。  
更推荐让它返回轻量结果，由 `AgentStateMachine` 统一写事件。

建议结果模型：

```csharp
public sealed class SessionAggregationResult
{
    public long? StartedSessionId { get; init; }
    public long? ClosedSessionId { get; init; }
    public string? CloseReason { get; init; }
}
```

职责边界：

```text
SessionAggregator
    负责 session 聚合逻辑

AgentStateMachine
    负责编排事件语义

AgentEventWriter
    负责事件落地
```

如果第一版改造成本偏高，可以先只记录上层事件：

```text
AgentPaused
AgentStopped
SessionClosed
```

不要新增独立的 `ProcessChanged` 事件类型。  
进程变化统一表达为：

```text
event_type = SessionClosed
payload_json.closeReason = ProcessChanged
```

这样事件口径和现有 `app_sessions.close_reason` 保持一致。

不要为了事件体系过度重构 session 聚合器。

## Agent 接入点

建议在以下位置写事件：

```text
AgentStateMachine.InitializeAsync
    AgentStarted
    payload_json: processId / version / actualState

TransitionToPausedAsync
    AgentPaused
    SessionClosed（如能拿到结果）

TransitionToRunningAsync
    AgentResumed

TransitionToStoppedAsync
    AgentStopped
    SessionClosed（如能拿到结果）
    payload_json: processId / actualState / stopReason

TickAsync 发现控制命令
    CommandDetected

ProcessCommandAsync 校验通过
    CommandAccepted

ProcessCommandAsync 执行完成
    CommandCompleted

ProcessCommandAsync 执行失败
    CommandFailed

TickAsync 读取坏 control 文件
    CommandInvalidJson

ReloadConfig
    ConfigReloaded

隐私过滤命中
    PrivacyFiltered（限流）

采样或写库异常
    CaptureFailed（限流）

SessionAggregator 开启 / 关闭 session
    SessionStarted
    SessionClosed（如能轻量接入）
```

`CaptureFailed` 第一版可以保留为单一事件类型，但 `error_code` 必须区分失败阶段。  
建议稳定 errorCode：

```text
ForegroundWindowUnavailable
ProcessLookupFailed
SampleWriteFailed
SessionAggregationFailed
SessionWriteFailed
UnknownCaptureError
```

如果后续 Diagnostics 中发现过于泛化，再拆成：

```text
CaptureFailed
SampleWriteFailed
SessionWriteFailed
```

## Diagnostics 页面增强

当前 Diagnostics 已能展示：

```text
runtime_state.json
health_state.json
agent_control.json
```

下一步建议增加：

```text
Recent Events
Recent Errors
事件 SQLite 写入状态
事件 JSONL 写入状态
当前 JSONL 文件路径
最近一次事件 SQLite 写入错误
最近一次事件 JSONL 写入错误
打开日志目录按钮
```

事件列表建议展示：

```text
event_time_utc
event_type
event_level
message
error_code
request_id
```

数据来源：

```text
Recent Events / Recent Errors
    只读 SQLite agent_events

JSONL
    第一版只显示路径或提供打开日志目录，不在 UI 中解析
```

Diagnostics 需要能回答：

```text
为什么 Recent Events 没有新事件？
事件 SQLite 是否写入失败？
事件 JSONL 是否写入失败？
当前审计文件在哪里？
```

不要第一版做 JSONL 浏览器，避免引入：

```text
文件锁
分页
滚动性能
日期切换
大文件读取
```

## 隐私要求

必须遵守：

```text
1. 不写真实窗口标题
2. 不把 excludedTitlePatterns 命中的原始标题写进 message
3. 不把敏感路径或窗口内容写进 payload_json
4. PrivacyFiltered 只写泛化原因
5. CaptureFailed 只写 errorCode / exceptionType / shortMessage
6. 不写 exception.ToString()
```

推荐示例：

```text
已跳过采样：命中标题隐私规则
已跳过采样：命中进程隐私规则
采样失败：获取前台窗口失败，errorCode=ProcessNotFound
```

不推荐：

```text
已跳过采样：命中标题 My Secret Bank Account
```

## 验收标准

本阶段完成后，应满足：

```text
1. dotnet build 0 warnings / 0 errors
2. dotnet test 全部通过
3. agent_events 表存在，并包含必要索引
4. Agent 启动后写 AgentStarted
5. Pause / Resume / Stop 写对应事件
6. 命令链路能看到 CommandDetected / CommandAccepted / CommandCompleted
7. 命令失败时写 CommandFailed
8. 坏 agent_control.json 写 CommandInvalidJson
9. 隐私过滤命中写 PrivacyFiltered，且不泄露原始标题
10. 采样异常写 CaptureFailed，带 errorCode
11. JSONL 文件按日期写入
12. Diagnostics 能展示最近事件
13. Diagnostics 能展示最近错误
14. 普通采样不会大量刷 agent_events
15. PrivacyFiltered / CaptureFailed 连续触发时不会刷爆 agent_events
16. AgentEventWriter 写 SQLite 或 JSONL 失败时，Agent 不崩溃
17. Diagnostics 查询事件时，WPF 只读 SQLite，不直接写 agent_events
18. foreground_samples / app_sessions 原有行为不回归
19. CommandInvalidJson 在 request_id 为空时仍能写事件
20. 命令事件包含 commandSource=FileFallback
21. Recent Events / Recent Errors 排序稳定
22. 30 分钟正常运行时，agent_events 数量显著小于 foreground_samples
23. 普通采样不产生 SampleCaptured / Heartbeat 类事件
24. 同一 PrivacyFiltered key 连续触发 5 分钟，最多写 5 条
25. 同一 CaptureFailed key 连续触发 5 分钟，最多写 5 条
```

## 推荐测试

建议新增或扩展测试：

```text
AgentEventRepository_CreatesAndReadsEvents
AgentEventRepository_ReturnsRecentErrorsByLevel
AgentEventJournal_WritesJsonLines
AgentEventWriter_DoesNotThrowWhenRepositoryFails
AgentEventWriter_DoesNotThrowWhenJournalFails
AgentEventRateLimiter_SuppressesRepeatedPrivacyEvents
AgentStateMachine_WritesLifecycleEvents
AgentStateMachine_WritesCommandEvents
AgentStateMachine_WritesCommandInvalidJsonEvent
AgentStateMachine_WritesCommandInvalidJsonWithoutRequestId
AgentStateMachine_WritesCommandSourceForFileFallback
AgentStateMachine_WritesPrivacyFilteredEventWithoutWindowTitle
AgentStateMachine_WritesCaptureFailedEventWithoutSensitivePayload
AgentStateMachine_WritesSessionClosedWithProcessChangedCloseReason
AgentStateMachine_DoesNotWriteEventForEverySample
AgentEventPayloadSanitizer_RemovesForbiddenKeys
DiagnosticsQueryService_ReturnsRecentEvents
DiagnosticsQueryService_ReturnsRecentErrors
DiagnosticsQueryService_ReturnsEventsWithStableOrdering
DiagnosticsQueryService_ReturnsEmptyListsWhenAgentEventsTableIsEmpty
DiagnosticsQueryService_DoesNotParseJsonl
```

重点测试：

```text
事件存在
事件顺序正确
JSON 枚举为字符串
隐私内容不泄露
普通采样不刷事件
限流有效
事件写入失败不影响主流程
```

## 推荐开发顺序

```text
1. 定义 Core Events 模型
2. 新增 agent_events 表和索引
3. 实现 AgentEventRepository
4. 实现 Diagnostics 查询服务：Recent Events / Recent Errors
5. Diagnostics 页面先展示空列表 / 无事件状态
6. 实现 AgentEventJournal
7. 实现 AgentEventWriter，要求 best-effort 双写且失败不影响主循环
8. 实现 AgentEventRateLimiter
9. 先接生命周期事件：AgentStarted / AgentStopped / AgentPaused / AgentResumed
10. 再接命令事件：CommandDetected / CommandAccepted / CommandCompleted / CommandFailed / CommandInvalidJson
11. 再接采集与隐私事件：CaptureFailed / PrivacyFiltered，并做限流
12. 再轻量接 session 事件：SessionStarted / SessionClosed
13. Diagnostics 页面展示事件系统健康、JSONL 路径、Recent Events / Recent Errors
14. 补测试
15. 手动运行 30 分钟，确认事件量合理
```

这个顺序比一次性把所有事件都接入更稳。  
先让 Diagnostics 空态和查询骨架跑通，可以保证每接入一种事件，都能马上在 UI 中验证。

## 之后再做什么

事件和诊断稳定后，推荐顺序：

```text
1. SamplesView
2. AppsView
3. SessionsView
4. Settings 编辑能力
5. PruneData / ClearHistory
6. Named Pipe / gRPC
7. 托盘
8. 开机自启
9. 安装包
10. Windows Service / 计划任务评估
```

## 阶段结论

真实采样 MVP 已经证明主链路可用。  
下一阶段不要急着堆 UI 或产品化能力，优先补：

```text
事件记录
历史诊断
最近错误
JSONL 审计
Diagnostics 可视化
```

这会让 WUJI 从：

```text
真实采样 MVP
```

升级到：

```text
可诊断 MVP
```

这一步比托盘、安装包、IPC 更优先。


阶段 1：事件基础设施
Core Events 模型
agent_events 表和索引
AgentEventRepository
DiagnosticsQueryService
Diagnostics 空态

阶段 2：事件写入链路
AgentEventJournal
AgentEventWriter
AgentEventRateLimiter
生命周期事件
命令事件

阶段 3：诊断完善
PrivacyFiltered / CaptureFailed
轻量 session 事件
Diagnostics Recent Events / Recent Errors
事件系统健康状态
测试和 30 分钟手动验收
