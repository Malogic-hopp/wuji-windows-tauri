这份计划整体是对的，而且方向很稳：**不是继续堆 UI，而是先把“运行历史、异常原因、命令链路、隐私命中”记录下来**。这和你计划里“让 WUJI 不只‘能采集’，还要‘能解释自己为什么这么运行’”的目标完全一致。

我主要建议你在现有计划上做几处收紧。

---

# 1. 先统一命名：Severity 还是 Level

你计划里用了：

```text
AgentEventSeverity
severity
```

之前方案里也可能出现过：

```text
event_level
AgentEventLevel
```

建议现在就统一，避免后面 SQLite 字段、JSONL 字段、C# 枚举名不一致。

我建议用：

```text
AgentEventLevel
event_level
```

枚举：

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

如果你更喜欢 `Severity` 也可以，但要全项目统一，不要一边叫 `severity`，一边叫 `event_level`。

---

# 2. CommandReceived 的语义要再明确

你计划里有：

```text
CommandReceived
CommandConsumed
CommandInvalidJson
```

这里容易混乱，因为现在是 file fallback：

```text
WPF 写 agent_control.json
Agent 读 agent_control.json
Agent 执行命令
```

但根据“Agent 是唯一写 SQLite 的进程”原则，**WPF 不应该直接写 agent_events**。所以建议事件语义按 Agent 视角定义：

```text
CommandDetected
    Agent 发现 agent_control.json 中存在命令

CommandAccepted
    Agent 验证命令合法，准备执行

CommandCompleted
    Agent 执行完成，状态转换完成

CommandFailed
    Agent 收到命令但执行失败

CommandInvalidJson
    Agent 读取到坏 JSON，并移动为 .bad
```

如果继续用 `CommandReceived / CommandConsumed` 也可以，但我建议至少补上：

```text
CommandCompleted
CommandFailed
```

否则 Diagnostics 里只能看到“命令被读到了”，但看不到“命令到底有没有执行完成”。

---

# 3. AgentEventWriter 一定要“旁路化”

你的计划里写了：

```text
AgentEventWriter
    一次调用同时写 SQLite 和 JSONL
    失败时不应拖垮采样主循环
```

这个非常关键。建议实现时明确三条规则：

```text
1. 写事件失败不能抛到 Agent 主循环
2. SQLite 写失败时，尽量继续写 JSONL
3. JSONL 写失败时，不能影响 SQLite 和采样
```

也就是说，事件系统是“诊断增强”，不是核心采集依赖。

第一版可以这样设计：

```text
AgentEventWriter.WriteAsync(event)
    try write SQLite
    catch 保存 lastEventWriteError 到 health_state 或内存

    try write JSONL
    catch 保存 lastJournalWriteError 到 health_state 或内存

    永远不让异常向上传播到采集循环
```

这样更符合长期运行软件的要求。

---

# 4. PrivacyFiltered 和 CaptureFailed 必须限流

你的事件范围是合理的，但这两个事件如果不限制，很容易刷爆：

```text
PrivacyFiltered
CaptureFailed
```

例如用户一直停留在被排除的应用，3 秒一次采样，一小时就是 1200 条 `PrivacyFiltered`。

建议第一版就加简单限流：

```text
同一 eventType + processName + errorCode / ruleType
60 秒内最多写 1 条
```

或者更简单：

```text
每类 PrivacyFiltered 每分钟最多 1 条
每类 CaptureFailed 每分钟最多 1 条
```

这样 Diagnostics 能看到问题，又不会污染事件表。

---

# 5. payload_json 要加“白名单”原则

你现在写了：

```text
payload_json 只放非敏感结构化字段
不要写真实窗口标题
```

这个方向对，但我建议再明确成白名单，不要靠开发时自觉。

允许写：

```text
ruleType
processName
sessionId
durationSeconds
activeSeconds
idleSeconds
closeReason
requestId
actualState
desiredState
errorCode
```

不建议写：

```text
windowTitle
rawTitle
executablePath
commandLine
fullUserPath
exception.ToString()
```

尤其是 `exception.ToString()`，可能带路径、窗口标题、命令行、用户名。建议只写：

```text
exceptionType
errorCode
shortMessage
```

完整异常如果以后要写，也应该进本地开发日志，而不是 agent_events 的 payload。

---

# 6. SQLite 索引建议再补一个组合索引

你计划里有：

```sql
idx_agent_events_time
idx_agent_events_type
```

建议再加：

```sql
CREATE INDEX IF NOT EXISTS idx_agent_events_level_time
ON agent_events(severity, event_time_utc);
```

如果你改成 `event_level`，就是：

```sql
CREATE INDEX IF NOT EXISTS idx_agent_events_level_time
ON agent_events(event_level, event_time_utc);
```

因为 Diagnostics 很可能经常查：

```sql
WHERE severity IN ('Warning', 'Error', 'Critical')
ORDER BY event_time_utc DESC
LIMIT 10
```

这个索引会更贴合“最近错误”查询。

---

# 7. HealthChanged 先谨慎做

你列了：

```text
HealthChanged
```

这个事件有用，但第一版可能不好定义。

如果每次 health_state 写入都记录 `HealthChanged`，会太吵。建议第一版只记录真正的状态变化：

```text
Healthy -> Warning
Warning -> Healthy
Healthy -> Error
Error -> Healthy
Running -> Stale
```

或者先不做 `HealthChanged`，等 health_state 的状态模型更稳定后再加。

我的建议：

```text
第一版可以暂缓 HealthChanged
先做 AgentStarted / Stopped / Paused / Resumed / Command / Privacy / Capture / Session
```

---

# 8. SessionStarted / SessionClosed 不要过度侵入聚合器

你计划里已经提醒了：

```text
SessionStarted / SessionClosed 如果 SessionAggregator 当前不方便返回事件，
可以先只记录 AgentPaused / AgentStopped / ProcessChanged 等上层事件。
不要为了事件体系过度重构 session 聚合器。
```

这个判断很好。

我建议更具体一点：让 `SessionAggregator` 返回一个轻量结果，而不是直接依赖 `AgentEventWriter`。

例如：

```csharp
public sealed class SessionAggregationResult
{
    public long? StartedSessionId { get; init; }
    public long? ClosedSessionId { get; init; }
    public string? CloseReason { get; init; }
}
```

然后由 `AgentStateMachine` 统一写事件。

这样可以保持：

```text
SessionAggregator 负责 session 逻辑
AgentStateMachine 负责事件编排
AgentEventWriter 负责落地
```

职责会更干净。

---

# 9. Diagnostics 第一版不要解析 JSONL

你的计划里说：

```text
数据来源优先 SQLite agent_events
JSONL 暂时作为文件级审计，不一定第一版直接在 UI 中解析
```

这个决定很好。

第一版 Diagnostics 只查 SQLite：

```text
Recent Events
Recent Errors
```

JSONL 只提供：

```text
当前 JSONL 文件路径
打开日志目录按钮
```

不要第一版就做 JSONL 浏览器，否则会引入文件锁、分页、滚动、日期切换等额外复杂度。

---

# 10. 建议把验收标准再加 3 条

你现有验收标准已经不错，我建议补这三条：

```text
13. AgentEventWriter 写 SQLite 或 JSONL 失败时，Agent 不崩溃
14. PrivacyFiltered / CaptureFailed 连续触发时不会刷爆 agent_events
15. Diagnostics 查询事件时，WPF 只读 SQLite，不直接写 agent_events
```

这三条可以防止事件系统反过来破坏稳定性。

---

# 我建议你把计划改成这个执行顺序

你现在计划的顺序是合理的，我只建议微调为：

```text
1. 定义 Core Events 模型
2. 新增 agent_events 表和索引
3. 实现 AgentEventRepository
4. 实现 AgentEventJournal
5. 实现 AgentEventWriter，要求失败不影响主循环
6. 先接生命周期事件：AgentStarted / AgentStopped / AgentPaused / AgentResumed
7. 再接命令事件：CommandDetected / CommandAccepted / CommandCompleted / CommandFailed / CommandInvalidJson
8. 再接采集与隐私事件：CaptureFailed / PrivacyFiltered，并做限流
9. 再接 session 事件：SessionStarted / SessionClosed
10. Diagnostics 查询 SQLite 最近事件 / 最近错误
11. 补测试
12. 手动运行 30 分钟看事件量是否合理
```

这个顺序比一口气把所有事件接入更稳。

---

# 总体评价

这份计划可以执行，方向没有问题。我的核心建议是：

```text
1. 统一 Level / Severity 命名
2. 增加 CommandCompleted / CommandFailed
3. 事件写入失败不能影响采集
4. PrivacyFiltered / CaptureFailed 必须限流
5. payload_json 采用白名单，严禁真实标题、路径、命令行、完整异常
6. Diagnostics 第一版只查 SQLite，不解析 JSONL
7. HealthChanged 可以后置
```

这样做完以后，WUJI 的阶段会从：

```text
真实采样 MVP
```

升级到：

```text
可诊断 MVP
```

这一步非常值得做，而且比现在去做托盘、安装包、IPC 更优先。
