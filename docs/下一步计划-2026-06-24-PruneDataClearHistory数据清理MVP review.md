这份阶段 7 计划整体**可以执行**，而且比我预期写得更稳。它准确承接了阶段 6 的边界：阶段 6 只做配置应用，阶段 7 才进入真实数据清理。计划里明确要求 WPF 不直接删除 SQLite/JSONL、清理必须由 Agent 执行、清理期间进入 `Maintenance`、成功/失败都写事件、`ClearHistory` 二次确认、失败后不能卡死，这些都是关键安全边界。

我建议你吸收下面几个小调整后，就可以正式按这份计划推进。

---

# 总体审核结论

这份计划的方向是正确的：

```text
阶段 7：PruneData / ClearHistory 数据清理 MVP
```

目标也很清楚：

```text
让用户能通过 Agent 安全清理本地历史数据，并且清理过程可观测、可诊断、失败后不破坏运行状态。
```

尤其推荐的开发顺序很稳：

```text
7.1 Maintenance 状态骨架
7.2 PruneData 数据层服务
7.3 PruneData Agent 集成与 Diagnostics
7.4 ClearHistory 数据层服务
7.5 ClearHistory Agent 集成与二次确认 UI
7.6 Settings 数据管理入口与刷新联动
7.7 验收、长跑验证与收口
```

先做状态骨架，再做低风险 `PruneData`，最后做高风险 `ClearHistory`，这个顺序不要改。

---

# 我建议保留的关键设计

## 1. WPF 只发命令，不直接删数据

必须坚持：

```text
WPF 不直接删除 SQLite
WPF 不直接删除 JSONL
WPF 不直接清空 runtime
```

这能继续保持你前面一直坚持的架构原则：Agent 是采集域和数据域的唯一写入者、维护者。

## 2. 先做 Maintenance 状态骨架

这个是最重要的第一步。

不要一上来写 DELETE 逻辑。
先让 Agent 能：

```text
Running / Paused -> Maintenance -> Running / Paused
```

并且能写：

```text
runtime_state
health_state
CommandAccepted
CommandCompleted
CommandFailed
```

先确认不会卡死在 Maintenance，再做真实删除。

## 3. ClearHistory 后保持 Paused

这个设计很好，建议保留。

如果 ClearHistory 后立刻 Running，Agent 会马上写入新 sample，用户会误以为没清空。
所以清空后保持 Paused，等待用户手动 Resume，是更清晰的产品语义。

## 4. HistoryCleared 清空后再写

计划中特别提醒：

```text
如果 ClearHistory 清空 agent_events 表，HistoryCleared 必须在清空后重新写入。
```

这个非常关键。否则 Diagnostics 会看不到“清空历史”这件事本身。

---

# 建议调整 1：PruneData 的 cutoff 口径要明确“UTC cutoff + 本地文件日期”

你现在写：

```text
cutoffUtc = nowUtc - retentionDays
JSONL 文件日期 < cutoffLocalDate
```

这个基本对，但建议再明确：

```text
SQLite 删除用 cutoffUtc
JSONL 文件删除用 cutoffLocalDate
```

也就是说：

```text
SQLite:
    sample_time_utc < cutoffUtc
    ended_at_utc < cutoffUtc
    event_time_utc < cutoffUtc

JSONL:
    fileDateLocal < cutoffLocalDate
```

不要用 UTC 日期去删本地命名的日志文件。因为文件名 `YYYYMMDD` 通常是按本地日期生成，和 UTC 日期可能在跨时区时差一天。

---

# 建议调整 2：PruneData 不要删除当前 open session，也不要删除正在进行的当天事件

计划里已经写了不删除 open session。建议再补一句：

```text
PruneData 不删除当前日期 JSONL，不删除当前 open session，不删除 runtime / health 当前状态。
```

尤其 `agent_events` 表删除旧事件时，如果当前命令相关事件发生在 cutoff 之前理论上不可能，但为了语义清晰，可以规定：

```text
PruneData 先计算 cutoff，再执行删除，再写 DataPruned；
DataPruned / CommandCompleted 永远在删除之后写入，因此不会被本次 PruneData 删除。
```

---

# 建议调整 3：ClearHistory 的 SQLite 操作建议用事务

ClearHistory 会连续清空多个表：

```text
foreground_samples
app_sessions
agent_events
```

建议数据层服务里明确：

```text
SQLite 清空历史表使用事务
```

这样避免清到一半失败导致状态不一致。

建议语义：

```text
BEGIN TRANSACTION
DELETE FROM foreground_samples
DELETE FROM app_sessions
DELETE FROM agent_events
COMMIT
```

失败则 rollback，并由 Agent 写 `CommandFailed`。

注意：`HistoryCleared` 事件应在清空事务成功后再写入。

---

# 建议调整 4：PruneData 也建议小事务包裹 SQLite 删除

`PruneData` 删除三张表也可以放在一个事务里：

```text
DELETE foreground_samples old rows
DELETE app_sessions old closed rows
DELETE agent_events old rows
COMMIT
```

JSONL 删除可以放在 SQLite 事务之后。
如果 JSONL 删除失败，建议仍返回部分失败，并写 `CommandFailed` 或 `DataPruned` + warning？

这里要先定语义。我建议 MVP 简单一点：

```text
SQLite 删除失败：PruneData failed，写 CommandFailed
JSONL 删除失败：PruneData failed，写 CommandFailed，但 SQLite 删除可能已完成
```

同时 payload 里不要写路径，只写：

```text
jsonlDeleteErrorCount
```

这比搞复杂补偿机制更适合 MVP。

---

# 建议调整 5：ClearHistory 要不要删除当天 JSONL，建议第一版不删

计划里写了两种选择。我的建议是 MVP 第一版：

```text
ClearHistory 不删除当前日期 agent_events_YYYYMMDD.jsonl
ClearHistory 删除当前日期之前的历史 JSONL
ClearHistory 清空 SQLite 后重新写 HistoryCleared
```

原因是：

```text
1. 当前日期日志文件可能正被 AgentEventJournal 使用
2. 不删除当天文件可以减少文件句柄和重建风险
3. Diagnostics 当前 JSONL 路径更稳定
```

如果未来想“彻底清空当天日志”，再专门做增强。MVP 保守一点更好。

---

# 建议调整 6：ClearHistory 是否清空 agent_events，要注意 Diagnostics Recent Errors 空态

清空 `agent_events` 后只写 `HistoryCleared`，这意味着之前的错误、命令记录都会消失。这个符合“清空所有历史”的语义。

但 UI 需要能承受：

```text
Recent Events 只有 HistoryCleared
Recent Errors 为空
```

建议在验收标准里补一句：

```text
ClearHistory 后 Diagnostics Recent Events 至少显示 HistoryCleared，Recent Errors 可以为空但不报错。
```

---

# 建议调整 7：Maintenance 中控制按钮语义要提前定

计划里说 Start/Pause/Resume/Stop 可用性“先保持保守”，建议更明确：

```text
Maintenance:
    Start 禁用
    Pause 禁用
    Resume 禁用
    Stop 可用或禁用二选一
```

我建议 MVP 第一版：

```text
Maintenance 期间 Stop 可用
Pause / Resume / Prune / Clear 禁用
```

原因：如果维护任务卡住，用户可能需要 Stop Agent。
但如果你担心 Stop 打断清理造成状态不一致，也可以第一版全部禁用，超时后再提示强制停止。二选一即可，文档最好明确。

更稳妥的 MVP：

```text
Maintenance 中重复清理按钮禁用；
Stop 按钮保留，Stop 请求在当前维护任务结束后执行，或先不支持中断。
```

如果当前架构不支持中断，那就先全部禁用，并依靠 try/finally 避免卡死。

---

# 建议调整 8：DataMaintenanceService 不要依赖 UI 层路径

计划里建议新增：

```text
DataMaintenanceService
PruneDataResult
ClearHistoryResult
```

我建议它只接收：

```text
databasePath
logsDir
cutoffUtc
cutoffLocalDate
```

或者接收 `WindowsAgentPaths`。
不要从 UI 层传路径，避免 WPF 和 Agent 两边路径口径不一致。

也不要把完整路径写进结果对象；结果对象里只放数量和状态。

---

# 建议调整 9：先不做 VACUUM 是对的

计划已经写不做 `VACUUM`，这个建议保留。

SQLite 删除大量数据后文件大小不马上变小是正常的。
如果用户以后在意磁盘空间，再单独做“压缩数据库”维护任务。

MVP 不要把 `DELETE + VACUUM` 混在一起。VACUUM 可能耗时、锁库，风险更高。

---

# 建议调整 10：阶段 7 的第一批提交要很小

我建议第一批只做：

```text
AgentActualState.Maintenance
AgentEventType.DataPruned / HistoryCleared
PruneData / ClearHistory command 进入 Maintenance 后模拟完成
runtime_state / health_state 可见
```

第一批不要碰 SQLite 删除。
提交可以叫：

```text
feat(maintenance): add maintenance state for data cleanup commands
```

这样即使后面删除逻辑有问题，也不会影响状态机基础。

---

# 建议补充的验收项

建议在最终验收中补这些：

```text
1. PruneData 清理后 DataPruned 和 CommandCompleted 不会被本次清理删除
2. ClearHistory 后 Diagnostics Recent Events 至少显示 HistoryCleared
3. ClearHistory 后 Recent Errors 为空时 UI 不报错
4. ClearHistory 后当天 agent_events JSONL 仍可定位
5. 清理过程中 Settings 未保存编辑表单不会被全局 Refresh 覆盖
6. Maintenance 状态下重复点击 PruneData / ClearHistory 不会重复发送命令
7. JSONL 删除失败时错误文本不包含完整路径
```

这些都是实际容易出问题的点。

---

# 最终审核结论

这份阶段 7 计划可以执行。
我建议只做以下小幅收紧：

```text
1. SQLite 删除用 UTC cutoff，JSONL 删除用本地文件日期 cutoff。
2. SQLite 删除使用事务。
3. ClearHistory 第一版不删除当天 JSONL。
4. Maintenance 期间按钮语义明确。
5. DataMaintenanceService 结果只返回数量，不返回路径。
6. 第一批提交只做 Maintenance 状态骨架，不做真实删除。
```

总体建议执行顺序保持不变：

```text
7.1 Maintenance 状态骨架
7.2 PruneData 数据层服务
7.3 PruneData Agent 集成
7.4 ClearHistory 数据层服务
7.5 ClearHistory Agent 集成 + 二次确认
7.6 Settings 数据管理入口
7.7 验收与长跑
```

第一步直接从 `7.1` 开始，不要先写删除 SQL。
