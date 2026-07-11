# 下一步计划：PruneData / ClearHistory 数据清理 MVP（2026-06-24）

本文档作为 `下一步计划-2026-06-22-Settings与数据管理MVP.md` 阶段 6「Settings 与配置应用 MVP」完成后的下一阶段正式计划。

上一阶段已经完成：

```text
Settings 与配置应用 MVP
```

当前项目已经从：

```text
能采集、能诊断、能浏览
```

推进到：

```text
能安全配置，并能证明配置被 Agent 应用
```

下一步不建议马上进入 Named Pipe / gRPC、托盘、安装包、7 天趋势、图表或导出。  
更优先的是补齐阶段 6 中刻意后移的高风险能力：

```text
PruneData / ClearHistory 数据清理 MVP
```

一句话目标：

```text
让用户能通过 Agent 安全清理本地历史数据，并且清理过程可观测、可诊断、失败后不破坏运行状态。
```

---

## 当前状态

当前 WUJI 已具备：

```text
真实 Win32 前台窗口采样
idle / active 判断
采集阶段隐私过滤
foreground_samples 落库
app_sessions 合并
Pause / Resume / Stop 控制
Dashboard 今日统计
Diagnostics 最近事件 / 最近错误
agent_events SQLite 查询索引
agent_events_YYYYMMDD.jsonl 审计日志
SamplesView 最近样本浏览
SessionsView 会话浏览和 close_reason 筛选
AppsView 今日应用排行
SettingsView App / Agent 配置展示、编辑、校验、保存、ReloadConfig
隐私规则编辑与真实生效验收
```

阶段 6 已完成验收：

```text
dotnet build QuantifiedSelf.Windows.sln --no-restore
    通过，0 warnings / 0 errors

dotnet test QuantifiedSelf.Windows.sln --no-restore
    通过，103/103
```

这说明采集、诊断、数据浏览、配置应用四条主链路已经具备继续扩展数据管理能力的基础。

---

## Review 吸收结论

阶段 6 已明确把数据清理后移，原因是：

```text
配置闭环是低风险可恢复操作
数据清理是真实删除操作
两者不应该混在一个阶段里完成
```

本阶段吸收阶段 6 文档和总重构方案中的关键约束：

```text
1. WPF 不直接删除 SQLite 数据
2. WPF 不直接删除 JSONL 日志
3. 数据清理必须由 Agent 执行
4. 清理过程必须进入 Maintenance 状态或等价可观测状态
5. 清理成功 / 失败都必须写 agent_events
6. 清理失败不能让 Agent 卡死在维护中
7. ClearHistory 必须二次确认
8. ClearHistory 后必须重写 runtime_state / health_state，保持 UI 可观测
9. 错误提示和事件 payload 不泄露本机路径、窗口标题或原始文件内容
10. PruneData / ClearHistory 不做复杂 UI，先打通安全闭环
11. SQLite 删除统一使用 UTC cutoff
12. JSONL 文件删除统一使用本地文件日期 cutoff
13. SQLite 多表删除必须使用事务
14. ClearHistory MVP 第一版不删除当天 JSONL
15. DataMaintenanceService 结果只返回数量和状态，不返回完整路径
```

当前代码已有：

```text
AgentCommandType.PruneData
AgentCommandType.ClearHistory
AgentControlService.PruneDataAsync()
AgentControlService.ClearHistoryAsync()
```

但 Agent 侧目前只是 accepted / completed 占位，并未执行真实清理。  
当前代码还没有：

```text
AgentActualState.Maintenance
DataPruned / HistoryCleared 事件类型
清理服务
清理结果统计
Settings / 数据管理 UI 入口
ClearHistory 二次确认
```

这些就是本阶段要补齐的内容。

---

## 下一阶段目标

下一阶段目标命名为：

```text
PruneData / ClearHistory 数据清理 MVP
```

一句话目标：

```text
让用户能安全执行“按保留天数清理历史数据”和“清空所有历史数据”，并能在 Diagnostics 中确认结果。
```

这一阶段重点补齐：

```text
Maintenance 状态
    清理期间停止普通采样写入，runtime / health 可观测

PruneData
    按 retentionDays 删除过期历史数据

ClearHistory
    二次确认后清空历史数据，并让 Agent 保持 Paused

Diagnostics
    DataPruned / HistoryCleared / CommandFailed 可追踪

Settings / 数据管理入口
    用户能触发清理，看到状态和结果
```

阶段完成后应满足：

```text
1. PruneData 只能由 Agent 执行
2. ClearHistory 只能由 Agent 执行
3. 清理期间 UI 能看到 Maintenance 或明确维护中状态
4. PruneData 不删除 retentionDays 范围内数据
5. PruneData 不删除 open session
6. ClearHistory 有二次确认
7. ClearHistory 后 Samples / Sessions / Apps 显示空态
8. ClearHistory 后 runtime_state / health_state 仍可读
9. ClearHistory 后 Agent 保持 Paused，用户手动 Resume
10. 清理成功写 DataPruned / HistoryCleared
11. 清理失败写 CommandFailed + errorCode
12. 错误文本和 payload 不泄露敏感路径或窗口标题
13. Dashboard / Apps / Sessions / Samples / Diagnostics / Settings 不崩溃
```

---

## 为什么现在做这个

阶段 5 和阶段 6 完成后，用户已经可以：

```text
查看最近采样
查看会话
查看应用排行
查看 Diagnostics
修改采集参数
修改隐私规则
ReloadConfig 并确认生效
```

下一步自然会遇到：

```text
历史数据越来越多怎么办？
retentionDays 配置何时真正生效？
测试数据、坏数据、隐私测试数据如何清理？
用户如果想从零开始，应该如何安全清空？
```

如果没有数据清理能力，`retentionDays` 只是配置项，不是真正的数据管理闭环。  
完成本阶段后，WUJI 的本地 MVP 产品闭环将变成：

```text
采集
诊断
浏览
配置
清理
```

---

## 本阶段不做

本阶段暂不做：

```text
Named Pipe / gRPC 主控制通道
Agent 状态流订阅
托盘 TrayService
开机自启
安装包
Windows Service
7 天趋势
复杂图表
CSV / Excel 导出
按表选择清理
按应用选择清理
按日期范围手动清理
回收站式恢复
数据库压缩 / VACUUM
复杂进度条和取消清理
加密存储
云同步
```

本阶段也暂不做：

```text
WPF 直接打开 SQLite 执行 DELETE
WPF 直接删除 logs 目录文件
WPF 直接重写 runtime_state / health_state
```

这些都必须保持为 Agent 职责。

---

## 设计原则

```text
1. Agent 是唯一的数据写入者，也应是唯一的数据清理者
2. WPF 只发送 PruneData / ClearHistory 命令，不直接删除数据
3. 清理前进入 Maintenance，清理结束后必须退出 Maintenance
4. 清理期间不写普通 foreground sample
5. 清理期间仍要写 health_state / runtime_state，让 UI 可观测
6. 清理成功和失败都必须写 agent_events
7. ClearHistory 必须二次确认，且确认文案明确“不可恢复”
8. ClearHistory 后 Agent 保持 Paused，避免马上写入新样本造成误解
9. PruneData 使用 windows-agent.json 中的 retentionDays
10. retentionDays 只影响 PruneData，不在普通采样 tick 中偷偷清理
11. PruneData 不删除 open session
12. PruneData 不删除 cutoff 之后的数据
13. SQLite 删除使用 UTC cutoff，JSONL 删除使用本地文件日期 cutoff
14. 不删除当前日期 JSONL
15. 不删除配置文件、runtime 控制文件、数据库文件本身
16. 错误信息必须走安全格式化，不能泄露完整路径
17. 清理事件 payload 只记录数量、cutoff、状态，不记录窗口标题、文件完整路径或原始 JSON
18. Diagnostics 和数据浏览页必须能承受表被清空或短暂为空
19. PruneData 和 ClearHistory 的 SQLite 删除必须使用事务
20. DataMaintenanceService 不依赖 UI 层路径，不把完整路径放进结果对象
```

---

## 命令与状态语义

### PruneData

语义：

```text
按当前 Agent Options 的 retentionDays 删除过期历史数据
```

命令来源：

```text
Settings / Data Management 区域
AgentControlService.PruneDataAsync()
runtime/agent_control.json fallback
```

建议流程：

```text
WPF 点击 PruneData
    ↓
写 PruneData command
    ↓
Agent CommandDetected
    ↓
Agent CommandAccepted
    ↓
AgentActualState = Maintenance
    ↓
写 runtime_state / health_state
    ↓
执行过期数据清理
    ↓
写 DataPruned
    ↓
写 CommandCompleted
    ↓
恢复清理前状态 Running 或 Paused
```

### ClearHistory

语义：

```text
清空所有历史采样、会话和事件索引，并删除历史 JSONL；清空后 Agent 保持 Paused
```

命令来源：

```text
Settings / Data Management 区域
必须经过二次确认
AgentControlService.ClearHistoryAsync()
runtime/agent_control.json fallback
```

建议流程：

```text
WPF 点击 Clear History
    ↓
第一次确认
    ↓
输入 CLEAR 二次确认
    ↓
写 ClearHistory command
    ↓
Agent CommandDetected
    ↓
Agent CommandAccepted
    ↓
AgentActualState = Maintenance
    ↓
关闭当前 open session
    ↓
清空历史表和历史 JSONL
    ↓
重新写 HistoryCleared 事件
    ↓
写 CommandCompleted
    ↓
AgentActualState = Paused
    ↓
写 runtime_state / health_state
```

注意：

```text
如果 ClearHistory 清空 agent_events 表，HistoryCleared 必须在清空后重新写入。
否则 Diagnostics 会看不到清理发生过。
```

### Maintenance

建议新增：

```text
AgentActualState.Maintenance
```

Maintenance 的含义：

```text
Agent 仍在运行，但正在执行维护任务，不进行普通采样写入
```

UI 展示：

```text
ActualState = Maintenance
状态文案：Maintenance / 正在维护
Settings 数据清理按钮在 Maintenance 中不可重复点击
```

Maintenance 期间按钮语义 MVP 先保持保守：

```text
Start 禁用
Pause 禁用
Resume 禁用
PruneData 禁用
ClearHistory 禁用
Stop 第一版也禁用，不支持中断维护任务
```

原因：

```text
当前架构尚未设计维护任务取消和中断恢复。
第一版依靠 try/finally、CommandFailed 和超时验收，确保维护任务不会卡死。
后续如果要支持维护中 Stop，应单独设计“维护结束后停止”或“可取消维护任务”语义。
```

---

## 数据清理范围

### PruneData 删除范围

cutoff 计算：

```text
cutoffUtc = nowUtc - retentionDays
cutoffLocalDate = localNow.Date - retentionDays
```

SQLite：

```text
foreground_samples
    DELETE WHERE sample_time_utc < cutoffUtc

app_sessions
    DELETE WHERE ended_at_utc IS NOT NULL
       AND ended_at_utc < cutoffUtc

agent_events
    DELETE WHERE event_time_utc < cutoffUtc
```

SQLite 删除必须在一个事务中完成：

```text
BEGIN TRANSACTION
DELETE FROM foreground_samples WHERE sample_time_utc < cutoffUtc
DELETE FROM app_sessions WHERE ended_at_utc IS NOT NULL AND ended_at_utc < cutoffUtc
DELETE FROM agent_events WHERE event_time_utc < cutoffUtc
COMMIT
```

失败时：

```text
ROLLBACK
PruneData failed
写 CommandFailed + errorCode = PruneDataFailed
```

不删除：

```text
app_sessions 中 ended_at_utc IS NULL 的 open session
cutoffUtc 之后的数据
数据库文件本身
SQLite WAL / SHM 文件
配置文件
runtime_state / health_state / agent_control
当天 JSONL
```

命令自身事件语义：

```text
PruneData 先计算 cutoff，再执行删除，再写 DataPruned / CommandCompleted。
DataPruned 和 CommandCompleted 永远在删除之后写入，因此不会被本次 PruneData 删除。
```

JSONL：

```text
agent_events_YYYYMMDD.jsonl
foreground_samples_YYYYMMDD.jsonl（如果存在）
```

删除规则：

```text
fileDateLocal < cutoffLocalDate
```

注意：

```text
JSONL 文件名按本地日期解释，不使用 UTC 日期解释。
跨时区边界时，SQLite 仍按 UTC 时间戳删除，JSONL 仍按本地文件日期删除。
```

JSONL 删除失败语义：

```text
SQLite 删除失败：
    整体 PruneData failed，写 CommandFailed

JSONL 删除失败：
    整体 PruneData failed，写 CommandFailed
    SQLite 事务可能已经成功提交，不做复杂补偿
    payload / message 只记录 jsonlDeleteErrorCount 或安全错误摘要，不记录完整路径
```

暂不做：

```text
解析 JSONL 内部逐行裁剪
VACUUM
按表选择清理
```

### ClearHistory 删除范围

SQLite：

```text
foreground_samples
app_sessions
agent_events
```

SQLite 清空必须在一个事务中完成：

```text
BEGIN TRANSACTION
DELETE FROM foreground_samples
DELETE FROM app_sessions
DELETE FROM agent_events
COMMIT
```

失败时：

```text
ROLLBACK
ClearHistory failed
写 CommandFailed + errorCode = ClearHistoryFailed
```

建议顺序：

```text
1. 如果存在 open session，先关闭为 ClearHistory / Maintenance close reason
2. 清空 foreground_samples
3. 清空 app_sessions
4. 清空 agent_events
5. 清空后写入 HistoryCleared
```

JSONL：

```text
删除当前日期之前的 agent_events_YYYYMMDD.jsonl 历史文件
删除当前日期之前的 foreground_samples_YYYYMMDD.jsonl 历史文件（如果存在）
```

MVP 固定采用保守规则：

```text
ClearHistory 不删除当前日期 agent_events_YYYYMMDD.jsonl
ClearHistory 不删除当前日期 foreground_samples_YYYYMMDD.jsonl
HistoryCleared 追加到当天 agent_events_YYYYMMDD.jsonl
```

原因：

```text
当前日期日志文件可能正被 AgentEventJournal 使用。
保留当天 JSONL 可以减少文件句柄、重建和 Diagnostics 当前日志路径漂移风险。
如果未来需要“彻底清空当天日志”，单独作为增强阶段处理。
```

不删除：

```text
windows-agent.json
app-settings.json
*.bak
runtime_state.json
health_state.json
agent_control.json.bad
数据库文件本体 quantified_self_windows.db
当天 JSONL
```

---

## 事件设计

建议新增事件类型：

```text
DataPruned
HistoryCleared
```

可选新增：

```text
MaintenanceStarted
MaintenanceCompleted
```

MVP 可以先不新增 MaintenanceStarted / MaintenanceCompleted，只用：

```text
CommandAccepted
DataPruned / HistoryCleared
CommandCompleted
CommandFailed
```

### DataPruned payload

建议 payload：

```json
{
  "retentionDays": 30,
  "cutoffUtc": "2026-05-25T00:00:00Z",
  "cutoffLocalDate": "2026-05-25",
  "foregroundSamplesDeleted": 1234,
  "sessionsDeleted": 56,
  "agentEventsDeleted": 20,
  "jsonlFilesDeleted": 3,
  "jsonlDeleteErrorCount": 0,
  "actualState": "Maintenance"
}
```

不得包含：

```text
窗口标题
完整文件路径
原始 JSONL 内容
SQL 文本
异常原文
```

### HistoryCleared payload

建议 payload：

```json
{
  "foregroundSamplesDeleted": 1234,
  "sessionsDeleted": 56,
  "agentEventsDeleted": 20,
  "jsonlFilesDeleted": 3,
  "jsonlDeleteErrorCount": 0,
  "finalState": "Paused"
}
```

---

## 推荐开发顺序

建议分成 7 个小阶段：

```text
阶段 7.1：数据清理命令与 Maintenance 状态骨架
阶段 7.2：PruneData 数据层服务
阶段 7.3：PruneData Agent 集成与 Diagnostics
阶段 7.4：ClearHistory 数据层服务
阶段 7.5：ClearHistory Agent 集成与二次确认 UI
阶段 7.6：Settings 数据管理入口与刷新联动
阶段 7.7：验收、长跑验证与收口
```

这个顺序先做状态骨架，再做风险较低的 PruneData，最后做高风险 ClearHistory。

第一批提交必须很小：

```text
只做 AgentActualState.Maintenance
只做 AgentEventType.DataPruned / HistoryCleared
只做 PruneData / ClearHistory command 进入 Maintenance 后模拟完成
只验证 runtime_state / health_state 可见
不碰 SQLite DELETE
不删除 JSONL
```

建议提交信息：

```text
feat(maintenance): add maintenance state for data cleanup commands
```

---

# 阶段 7.1：数据清理命令与 Maintenance 状态骨架

## 阶段目标

先让 Agent 能接收 PruneData / ClearHistory，并进入 / 退出 Maintenance，但暂不执行真实删除。

## 建议新增或调整

```text
src/QuantifiedSelf.Windows.Core/Control/AgentActualState.cs
    新增 Maintenance

src/QuantifiedSelf.Windows.Core/Events/AgentEventType.cs
    新增 DataPruned / HistoryCleared

src/QuantifiedSelf.Windows.Agent/State/AgentStateMachine.cs
    PruneData / ClearHistory 不再只是 accepted 占位
    先进入 Maintenance
    模拟完成后退出
    本阶段不做真实 DELETE，不删除 JSONL
```

## 状态恢复语义

PruneData：

```text
Running -> Maintenance -> Running
Paused -> Maintenance -> Paused
```

ClearHistory：

```text
Running -> Maintenance -> Paused
Paused -> Maintenance -> Paused
```

失败：

```text
任何状态 -> Maintenance -> 原状态或 Paused
```

ClearHistory 失败建议回到 Paused，避免边删边采集。

## 验收标准

- AgentActualState 支持 Maintenance。
- PruneData 命令产生 CommandDetected / CommandAccepted / CommandCompleted。
- ClearHistory 命令产生 CommandDetected / CommandAccepted / CommandCompleted。
- 清理命令处理期间 runtime_state / health_state 能看到 Maintenance。
- 清理命令结束后不会卡在 Maintenance。
- Maintenance 期间 PruneData / ClearHistory 不可重复触发。
- Maintenance 期间 Start / Pause / Resume / Stop 第一版均禁用或不被 UI 触发。
- Unsupported command 行为不回归。

## 建议测试

```text
AgentStateMachine_PruneData_EntersAndLeavesMaintenance
AgentStateMachine_ClearHistory_EndsPaused
AgentStateMachine_MaintenanceWritesRuntimeAndHealthState
AgentStateMachine_MaintenanceFailureDoesNotGetStuck
SettingsViewModel_DisablesCleanupCommandsDuringMaintenance
AgentControlFileStore_RoundsTripPruneDataAndClearHistory
```

---

# 阶段 7.2：PruneData 数据层服务

## 阶段目标

实现独立、可测试的数据清理服务，但先不接 UI。

## 建议新增

```text
src/QuantifiedSelf.Windows.Infrastructure/Database/DataMaintenanceService.cs
src/QuantifiedSelf.Windows.Core/Maintenance/PruneDataResult.cs
```

或按现有项目命名选择等价位置。

服务依赖边界：

```text
DataMaintenanceService 可以接收 WindowsAgentPaths
或接收 databasePath / logsDir / cutoffUtc / cutoffLocalDate
但不得依赖 WPF UI 层传入的路径口径
PruneDataResult 不返回完整路径，只返回数量、cutoff 和错误计数
```

## 服务职责

```text
根据 cutoffUtc 清理 SQLite 过期数据
根据 cutoffLocalDate 清理历史 JSONL
返回删除统计
```

## 删除规则

```text
foreground_samples.sample_time_utc < cutoffUtc
app_sessions.ended_at_utc IS NOT NULL AND ended_at_utc < cutoffUtc
agent_events.event_time_utc < cutoffUtc
```

SQLite 删除规则：

```text
三张表删除放在同一个事务中
任一 DELETE 失败则 ROLLBACK
事务成功后再删除 JSONL
```

JSONL 删除规则：

```text
按本地文件日期解析 agent_events_YYYYMMDD.jsonl / foreground_samples_YYYYMMDD.jsonl
只删除 fileDateLocal < cutoffLocalDate 的文件
不删除当前日期 JSONL
JSONL 删除失败时不做复杂补偿，返回失败和 jsonlDeleteErrorCount
```

## 验收标准

- 删除数量统计准确。
- 不删除 cutoff 之后的数据。
- 不删除 open session。
- 缺表时安全返回 0 或清晰错误，不让 Agent 崩溃。
- JSONL 只删除符合日期规则的历史文件。
- JSONL 文件日期按本地日期解释。
- SQLite 删除使用事务。
- JSONL 删除失败不暴露完整路径。
- 不删除非目标文件。

## 建议测试

```text
DataMaintenanceService_PruneDataDeletesExpiredRows
DataMaintenanceService_PruneDataKeepsRecentRows
DataMaintenanceService_PruneDataKeepsOpenSessions
DataMaintenanceService_PruneDataHandlesMissingTables
DataMaintenanceService_PruneDataDeletesOldJsonlFilesOnly
DataMaintenanceService_PruneDataUsesLocalDateForJsonlCutoff
DataMaintenanceService_PruneDataUsesTransactionForSqliteDeletes
DataMaintenanceService_PruneDataReportsJsonlDeleteFailureWithoutPaths
DataMaintenanceService_PruneDataDoesNotDeleteConfigRuntimeOrDatabaseFiles
```

---

# 阶段 7.3：PruneData Agent 集成与 Diagnostics

## 阶段目标

把 PruneData 数据层服务接入 AgentStateMachine，并让 Diagnostics 能看到清理结果。

## 交互

```text
Settings 点击 PruneData
    ↓
AgentControlService.PruneDataAsync()
    ↓
AgentStateMachine.ProcessCommandAsync()
    ↓
Maintenance
    ↓
DataMaintenanceService.PruneDataAsync()
    ↓
DataPruned
    ↓
CommandCompleted
```

## 状态反馈

UI 应至少显示：

```text
PruneData command queued
PruneData completed
PruneData failed: <safe message>
```

Diagnostics 应显示：

```text
CommandDetected
CommandAccepted
DataPruned
CommandCompleted
```

失败时：

```text
CommandFailed
errorCode = PruneDataFailed
```

## 验收标准

- PruneData 使用当前 `_options.RetentionDays`。
- 成功后写 DataPruned，payload 只有数量和 cutoff。
- 失败后写 CommandFailed，错误文本脱敏。
- PruneData 后 Samples / Sessions / Apps 仍可刷新。
- PruneData 后 Agent 恢复原 Running / Paused 状态。
- DataPruned / CommandCompleted 在删除之后写入，不会被本次 PruneData 删除。
- JSONL 删除失败时写 CommandFailed，message 不含完整路径。

## 建议测试

```text
AgentStateMachine_PruneDataUsesRetentionDays
AgentStateMachine_PruneDataWritesDataPrunedEvent
AgentStateMachine_PruneDataFailureWritesCommandFailedAndSafeMessage
AgentStateMachine_PruneDataRestoresPreviousState
AgentStateMachine_PruneDataDoesNotDeleteItsOwnCompletionEvents
AgentStateMachine_PruneDataJsonlFailureDoesNotLeakPath
DiagnosticsDataService_LoadsDataPrunedEvent
```

---

# 阶段 7.4：ClearHistory 数据层服务

## 阶段目标

实现清空历史数据的数据层服务，但暂不接 UI 二次确认。

## 建议新增

```text
src/QuantifiedSelf.Windows.Core/Maintenance/ClearHistoryResult.cs
```

可与 `DataMaintenanceService` 共用。

服务依赖边界：

```text
ClearHistoryResult 不返回完整文件路径
只返回删除数量、jsonlFilesDeleted、jsonlDeleteErrorCount 和状态
```

## 服务职责

```text
关闭 open session 或由 Agent 先关闭 open session
清空 foreground_samples
清空 app_sessions
清空 agent_events
删除历史 JSONL
返回删除统计
```

SQLite 清空规则：

```text
foreground_samples / app_sessions / agent_events 清空放在同一个事务中
任一 DELETE 失败则 ROLLBACK
HistoryCleared 不在事务内提前写入
事务成功后由 Agent 重新写 HistoryCleared
```

## 关键语义

ClearHistory 后必须保留可观测性：

```text
runtime_state 可读
health_state 可读
agent_events 中能看到 HistoryCleared
当天 agent_events_YYYYMMDD.jsonl 能定位
```

注意：

```text
如果服务清空 agent_events，那么 HistoryCleared 必须由 Agent 在清空之后再写入。
```

## 验收标准

- 清空 foreground_samples。
- 清空 app_sessions。
- 清空 agent_events 后能重新写 HistoryCleared。
- 只删除当前日期之前的历史 JSONL。
- 不删除当天 agent_events_YYYYMMDD.jsonl。
- 删除历史 JSONL 后当天事件日志仍可定位。
- 不删除配置、runtime、数据库文件。
- 清理失败不留下半截 .tmp 或不可读状态文件。
- 清理失败时 SQLite 事务回滚。

## 建议测试

```text
DataMaintenanceService_ClearHistoryDeletesHistoryRows
DataMaintenanceService_ClearHistoryDeletesHistoricalJsonlFiles
DataMaintenanceService_ClearHistoryKeepsTodayJsonlFiles
DataMaintenanceService_ClearHistoryUsesTransaction
DataMaintenanceService_ClearHistoryKeepsConfigRuntimeAndDatabaseFiles
AgentStateMachine_ClearHistoryWritesHistoryClearedAfterClearingEvents
```

---

# 阶段 7.5：ClearHistory Agent 集成与二次确认 UI

## 阶段目标

把 ClearHistory 接入 Agent 命令，并在 WPF 中提供保守的二次确认入口。

## UI 建议

放在 Settings 页新增 Data Management 区域：

```text
Prune old data
Clear all history
```

Clear all history 必须二次确认：

```text
第一次：确认弹窗说明会清空历史采样、会话、应用排行和诊断事件
第二次：输入 CLEAR 才能继续
```

按钮文案建议：

```text
Clear all history
```

中文提示建议：

```text
这会清空本机历史采样、会话、应用排行和诊断事件。配置文件不会被删除。此操作不可撤销。
请输入 CLEAR 继续。
```

## Agent 集成语义

```text
ClearHistory 成功后 AgentActualState = Paused
用户需要手动 Resume 才继续采集
```

原因：

```text
清空后如果立刻恢复 Running，Agent 很快写入新 sample，用户可能误以为没有清空。
```

## 验收标准

- ClearHistory 不经过二次确认无法触发。
- 输入错误确认词不会发送命令。
- ClearHistory 命令只由 Agent 执行。
- 成功后 Samples / Sessions / Apps 显示空态。
- 成功后 Diagnostics 至少能看到 HistoryCleared 和 CommandCompleted。
- ClearHistory 后 Diagnostics Recent Events 至少显示 HistoryCleared。
- ClearHistory 后 Diagnostics Recent Errors 可以为空，但 UI 不报错。
- ClearHistory 后当天 agent_events JSONL 仍可定位。
- 成功后 Agent 显示 Paused。
- 失败后 CommandFailed 可见，错误文本安全。

## 建议测试

```text
SettingsViewModel_ClearHistoryRequiresConfirmation
SettingsViewModel_ClearHistoryRejectsWrongConfirmationText
SettingsViewModel_ClearHistoryQueuesCommandAfterConfirmation
AgentStateMachine_ClearHistoryClearsDataAndEndsPaused
AgentStateMachine_ClearHistoryFailureWritesCommandFailed
DiagnosticsViewModel_ClearHistoryAllowsEmptyRecentErrors
```

---

# 阶段 7.6：Settings 数据管理入口与刷新联动

## 阶段目标

把阶段 7 的功能以最小 UI 形式放入 Settings，并确保清理后其它页面刷新正确。

## 页面建议

Settings 页新增：

```text
Data Management
    retentionDays 当前值（只读或链接到 Agent Options）
    Prune old data
    Clear all history
    Last maintenance status
```

PruneData 按钮语义：

```text
Agent Running / Paused:
    可用

Agent NotRunning / Stopped / Stale:
    不可用，提示需要 Agent 运行

Agent Maintenance:
    不可用，提示正在维护
```

Maintenance 期间：

```text
PruneData / ClearHistory 不重复发送命令
Settings 未保存编辑表单不被全局 Refresh 覆盖
```

ClearHistory 按钮语义：

```text
Agent Running / Paused:
    可用，但必须二次确认

Agent NotRunning / Stopped / Stale:
    不可用，提示需要 Agent 运行

Agent Maintenance:
    不可用，提示正在维护
```

## 刷新联动

PruneData 成功后：

```text
当前页是 Samples / Sessions / Apps / Diagnostics 时，下一次刷新应看到清理结果
Dashboard 今日统计不崩溃
```

ClearHistory 成功后：

```text
Samples 显示空态
Sessions 显示空态
Apps 显示空态
Diagnostics 显示 HistoryCleared
Dashboard 显示 0 或合理空态
```

## 验收标准

- Settings 页新增 Data Management 区域，不膨胀 MainWindow。
- 清理按钮状态随 AgentActualState 更新。
- 清理后当前页 Refresh 不报错。
- Settings 未保存配置编辑不被全局 Refresh 覆盖的阶段 6 语义不回归。
- Maintenance 状态下重复点击 PruneData / ClearHistory 不会重复发送命令。

## 建议测试

```text
SettingsViewModel_DisablesDataCleanupWhenAgentNotRunning
SettingsViewModel_DisablesDataCleanupDuringMaintenance
SettingsViewModel_DoesNotQueueDuplicateCleanupDuringMaintenance
MainWindowViewModel_RefreshAfterClearHistoryShowsEmptyStates
MainWindowViewModel_SettingsDirtyGuardStillWorksWithDataManagement
```

---

# 阶段 7.7：验收、长跑验证与收口

## 自动化验收

完成后应满足：

```text
1. dotnet build 0 warning / 0 error
2. dotnet test 全部通过
3. Maintenance 状态有测试覆盖
4. PruneData 删除规则有测试覆盖
5. PruneData 不删除 open session 有测试覆盖
6. ClearHistory 清空后重写 HistoryCleared 有测试覆盖
7. ClearHistory 成功后 Agent Paused 有测试覆盖
8. CommandFailed 脱敏有测试覆盖
9. Settings 数据管理按钮状态有测试覆盖
10. 数据浏览页空库 / 空表刷新有测试覆盖
11. PruneData 后 DataPruned / CommandCompleted 不会被本次清理删除
12. ClearHistory 后 Diagnostics Recent Events 至少显示 HistoryCleared
13. ClearHistory 后 Recent Errors 为空时 UI 不报错
14. ClearHistory 后当天 agent_events JSONL 仍可定位
15. 清理过程中 Settings 未保存编辑表单不会被全局 Refresh 覆盖
16. Maintenance 状态下重复点击 PruneData / ClearHistory 不会重复发送命令
17. JSONL 删除失败时错误文本不包含完整路径
```

## 手动验收流程

建议手动验证：

```text
1. 启动 Agent，确认 Running
2. 让 Agent 正常采样几分钟，确保 Samples / Sessions / Apps 有数据
3. 在 Settings 中执行 PruneData
4. 确认 Diagnostics 出现 CommandDetected / CommandAccepted / DataPruned / CommandCompleted
5. 确认 Agent 没有卡在 Maintenance
6. 准备测试数据或临时缩短 retentionDays，再验证旧数据被删除、新数据保留
7. 执行 ClearHistory，确认必须输入 CLEAR
8. ClearHistory 成功后确认 Agent 进入 Paused
9. 确认 Samples / Sessions / Apps 显示空态
10. 确认 Diagnostics 能看到 HistoryCleared
11. 手动 Resume，确认 Agent 后续能重新采样
12. Dashboard / Settings / Diagnostics 页面刷新和滚动正常
```

## 长跑验证

建议至少做：

```text
1. 正常运行 15-30 分钟后执行 PruneData
2. PruneData 后继续运行 15 分钟
3. ClearHistory 后 Resume，再运行 5-10 分钟
4. 期间确认无卡死、无 Stale、无 UI 刷新异常
```

## 收口文档

阶段完成后建议新增：

```text
docs/下一步计划-2026-06-24-PruneDataClearHistory数据清理MVP/
    01-阶段7.1-数据清理命令与Maintenance状态骨架.md
    02-阶段7.2-PruneData数据层服务.md
    03-阶段7.3-PruneData-Agent集成与Diagnostics.md
    04-阶段7.4-ClearHistory数据层服务.md
    05-阶段7.5-ClearHistory-Agent集成与二次确认UI.md
    06-阶段7.6-Settings数据管理入口与刷新联动.md
    07-阶段7.7-验收长跑验证与收口.md
    阶段7-验收清单-YYYY-MM-DD.md
    阶段7-完成说明-YYYY-MM-DD.md
```

---

## 后续候补

PruneData / ClearHistory 数据清理 MVP 完成后，再进入：

```text
1. Named Pipe / gRPC over Named Pipes 主控制通道
2. Agent 状态流订阅 / RefreshService 优化
3. 托盘
4. 开机自启
5. 安装包
6. 7 天趋势和图表
7. 应用分类
8. 浏览器网页级识别
9. 数据导出
10. 本地数据加密
```

阶段 7 完成后再做控制通道升级更合适，因为届时 WUJI 已具备：

```text
采集
诊断
数据浏览
配置应用
数据清理
```

这时再把 `agent_control.json` fallback 升级为 Named Pipe / gRPC 主通道，收益会更明确。

---

## 风险与注意事项

### 数据误删风险

必须避免：

```text
WPF 直接删除 SQLite
清理时误删配置文件
PruneData 删除 open session
PruneData 删除 retentionDays 范围内数据
ClearHistory 未确认就执行
```

建议：

```text
所有删除动作集中在 DataMaintenanceService
删除前后统计数量
测试覆盖边界时间
ClearHistory 必须二次确认
```

### Maintenance 卡死风险

必须避免：

```text
异常后 AgentActualState 永远停在 Maintenance
health_state 不更新导致 UI 显示 Stale
ClearHistory 清空事件后 Diagnostics 无证据
```

建议：

```text
try/finally 恢复状态
失败也写 CommandFailed
ClearHistory 清空后再写 HistoryCleared
清理前后都 PersistAsync
```

### 数据库并发风险

必须避免：

```text
清理和采样同时写库
清理和 session aggregation 同时更新 app_sessions
WPF 读库时报未处理异常
```

建议：

```text
清理期间 Agent 进入 Maintenance，不执行普通 sample tick
数据库写操作使用既有连接工厂和 WAL 策略
WPF 查询服务继续只读连接
列表页保留空库 / 缺表安全处理
```

### 隐私回归风险

必须避免：

```text
DataPruned / HistoryCleared payload 写入完整路径
CommandFailed message 暴露本机路径
ClearHistory UI 回显窗口标题或 JSONL 内容
```

建议：

```text
沿用 DiagnosticMessageSanitizer
payload 只记录数量、cutoff、状态
不把删除文件完整路径写进 agent_events
```

### 用户体验风险

必须避免：

```text
ClearHistory 后 Agent 立刻采样，让用户误以为没清空
PruneData 按钮没有结果反馈
Maintenance 中还能重复点击清理
```

建议：

```text
ClearHistory 后保持 Paused
UI 显示 queued / completed / failed
Maintenance 中禁用清理按钮
Diagnostics 提供最终证据
```

---

## 最终结论

阶段 6 完成后，WUJI 已经具备：

```text
采集
诊断
数据浏览
配置应用
```

下一步最自然、最有价值的是：

```text
PruneData / ClearHistory 数据清理 MVP
```

这一阶段完成后，WUJI 将从“可配置的本地使用分析工具”推进到“能自我维护历史数据的本地产品 MVP”。
