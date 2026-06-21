# 下一步计划：Samples / Sessions / Apps 数据浏览 MVP（2026-06-22）

本文档作为 `下一步计划-2026-06-19-AgentEvents与DiagnosticsMVP.md` 完成后的下一阶段正式计划。

上一阶段已经完成：

```text
Agent Events 与 Diagnostics MVP
```

当前项目已经从：

```text
真实采样 MVP
```

推进到：

```text
可诊断 MVP
```

下一步不建议马上进入托盘、安装包、Named Pipe / gRPC、复杂图表或完整 Settings。  
更优先的是补齐用户能直接感知的数据浏览能力。

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
Dashboard 基础统计
Diagnostics 最近事件 / 最近错误
agent_events SQLite 查询索引
agent_events_YYYYMMDD.jsonl 审计日志
事件限流、payload 白名单和路径脱敏
```

上一阶段已完成验收：

```text
dotnet build QuantifiedSelf.Windows.sln
    通过，0 warnings / 0 errors

dotnet test QuantifiedSelf.Windows.sln --no-build
    通过

30 分钟长跑验证
    agent_events 数量显著小于 foreground_samples
    未出现 SampleCaptured / Heartbeat 高频事件
    Diagnostics 刷新和滚动正常
```

这说明当前 Agent 主链路和诊断链路已经具备继续扩展 UI 数据浏览的基础。

---

## 下一阶段目标

下一阶段目标命名为：

```text
Samples / Sessions / Apps 数据浏览 MVP
```

一句话目标：

```text
让 WUJI 不只“能采集、能诊断”，还要能查看、筛选和解释历史使用明细。
```

这一阶段重点补齐三个页面：

```text
SamplesView
    查看最近前台采样明细

SessionsView
    查看会话边界、持续时间和 close_reason

AppsView
    查看今日应用使用排行和应用级汇总
```

阶段完成后应满足：

```text
1. 用户能查看最近 foreground_samples
2. 用户能查看最近 app_sessions
3. 用户能看到 session close_reason
4. 用户能查看今日应用排行
5. 应用排行按 active duration 为主
6. UI 查询只读 SQLite
7. 大量数据下刷新和滚动不卡顿
8. Dashboard / Diagnostics / Agent 控制按钮不回归
```

---

## 为什么现在做这个

当前 Dashboard 能回答：

```text
今天总共用了多久？
今天 Active / Idle / Unknown 各多少？
Top Apps 是哪些？
最近 sessions 是哪些？
```

当前 Diagnostics 能回答：

```text
Agent 最近发生了什么？
命令是否被消费？
隐私是否命中？
采样或写入是否异常？
事件系统是否健康？
```

但当前还不能很好回答：

```text
最近 200 条原始采样是什么？
某个应用的样本是否持续写入？
某个窗口标题是否已正确脱敏？
某个 session 为什么关闭？
Pause / Stop / ProcessChanged 是否真实反映到 session 明细？
哪些应用今天使用时间最高？
active / idle / unknown 在应用维度如何分布？
```

因此下一阶段应该优先做数据浏览，而不是继续扩展底层控制通道。

---

## 本阶段不做

本阶段暂不做：

```text
Named Pipe / gRPC
托盘 TrayService
安装包
开机自启
Windows Service
完整 Settings 编辑页
PruneData / ClearHistory
复杂图表
7 天趋势
CSV / Excel 导出
应用分类编辑
浏览器网页级识别
JSONL 浏览器
全文搜索或复杂查询语法
```

这些属于产品化、长期统计或高级管理能力。  
当前阶段只做“能稳定看明细”的 MVP。

---

## 设计原则

```text
1. WPF App 只读 SQLite，不直接写采集域数据
2. Agent 仍然是唯一写入 foreground_samples / app_sessions / agent_events 的进程
3. 新页面优先使用现有 SQLite 表，不引入新表
4. 查询必须有 LIMIT，避免一次性加载全量历史
5. Data Views 查询服务明确使用只读连接，例如 Mode=ReadOnly;Pooling=False
6. UI 优先稳定、可扫读、可刷新，不做复杂图表
7. maskWindowTitles=true 时，SamplesView 不泄露真实窗口标题
8. excludedProcesses 命中时，SamplesView / SessionsView 不应出现对应采样和 session
9. AppsView 排行优先按 active_duration，而不是 total_duration
10. display_name 必须有 fallback：display_name -> process_name -> Unknown
11. Dashboard 和 Diagnostics 原有行为不得回归
12. 每个页面先做 MVP，再考虑筛选、分页和视觉优化
```

---

## 推荐开发顺序

建议分成 6 个小阶段：

```text
阶段 5.1：查询服务补齐
阶段 5.2：SamplesView MVP
阶段 5.3：SessionsView MVP
阶段 5.4：AppsView MVP
阶段 5.5：导航与页面收口
阶段 5.6：验收、稳定化与后续边界
```

这个顺序先补数据能力，再接 UI。  
不要先大拆 `MainWindow`，避免把“数据浏览 MVP”拖成 UI 架构重构。

---

# 阶段 5.1：查询服务补齐

## 阶段目标

补齐 Samples / Sessions / Apps 三类只读查询能力。

## 建议新增或扩展

建议新增：

```text
src/QuantifiedSelf.Windows.Infrastructure/Database/SampleQueryService.cs
src/QuantifiedSelf.Windows.Infrastructure/Database/SessionQueryService.cs
src/QuantifiedSelf.Windows.Infrastructure/Database/AppUsageQueryService.cs
```

如果现有 `OverviewQueryService` 已经覆盖部分 Apps / Sessions 查询，可以复用，但不要把 Samples 查询塞进 Overview。

查询连接要求：

```text
1. SampleQueryService 使用只读连接
2. SessionQueryService 使用只读连接
3. AppUsageQueryService 使用只读连接
4. DiagnosticsQueryService 继续保持只读连接
5. 代码审查时确认这些查询服务不会使用 ReadWrite / ReadWriteCreate
```

## Samples 查询

第一版支持：

```text
GetRecentSamplesAsync(limit = 200)
```

字段建议：

```text
id
sample_time_utc
process_name
window_title
executable_path
idle_seconds
activity_state
```

排序：

```sql
ORDER BY sample_time_utc DESC, id DESC
LIMIT $limit
```

## Sessions 查询

第一版支持：

```text
GetRecentSessionsAsync(limit = 200)
GetSessionsForLocalDayAsync(localDate)
```

`GetSessionsForLocalDayAsync(localDate)` 必须按“与本地当天时间区间有重叠”查询，不按 `started_at_utc` 的日期简单过滤。

语义：

```text
session.started_at_utc < local_day_end_utc
AND COALESCE(session.ended_at_utc, now_utc) > local_day_start_utc
```

第一版 SessionsView 只需要把跨日重叠 session 查出来。  
如果要计算今日精确时长，再由统计层按重叠区间切分，不要在列表查询里偷换统计口径。

字段建议：

```text
id
started_at_utc
ended_at_utc
process_name
window_title
total_duration_seconds
active_duration_seconds
idle_duration_seconds
unknown_duration_seconds
close_reason
```

排序：

```sql
ORDER BY started_at_utc DESC, id DESC
LIMIT $limit
```

## Apps 查询

第一版支持：

```text
GetAppUsageForLocalDayAsync(localDate, limit = 50)
```

字段建议：

```text
process_name
display_name
total_duration_seconds
active_duration_seconds
idle_duration_seconds
unknown_duration_seconds
session_count
last_used_utc
```

排序建议：

```sql
ORDER BY active_duration_seconds DESC, total_duration_seconds DESC, process_name ASC
```

展示名 fallback：

```text
display_name 优先来自 app-name-map / 已有映射
没有 display_name 时显示 process_name
process_name 也为空时显示 Unknown
```

## 验收标准

```text
dotnet build 通过
dotnet test 通过
查询均为只读连接，例如 Mode=ReadOnly;Pooling=False
所有列表查询都有 LIMIT
空库返回空列表，不抛异常
同时间数据排序稳定
Today Sessions 按本地日重叠查询
Apps 排行有稳定 tie-breaking
```

---

# 阶段 5.2：SamplesView MVP

## 阶段目标

让用户能看到最近前台采样明细。

## 页面内容

第一版展示：

```text
Local time
Process
Display name
Window title
Idle seconds
Activity state
Sample id
```

`executable_path` 第一版不建议直接展示完整路径。  
默认不展示完整 `executable_path`。  
如确实需要展示，建议放在详情区域，并且只显示文件名或脱敏路径。  
完整路径暂时只在 Diagnostics / 开发排错场景中查看，避免路径中的用户名、项目名或隐私目录进入普通数据浏览页。

## 基础交互

第一版支持：

```text
Refresh
最近 200 条
ActivityState 轻量筛选：All / Active / Idle / Unknown
```

暂不做：

```text
全文搜索
任意日期选择
无限滚动
导出
复杂筛选
```

## 隐私要求

必须保证：

```text
maskWindowTitles=true 时，window_title 为空或显示 [Masked]
excludedProcesses 命中时，不应出现对应 sample
不要在 UI 辅助文案中暴露被过滤标题
如果当前表结构无法区分空标题和脱敏标题，第一版统一显示 [Hidden]，不要为了这个改采样表结构
```

## 验收标准

```text
SamplesView 能显示最近 200 条样本
按 sample_time_utc DESC, id DESC 排序
ActivityState 筛选可用
空表显示友好空态
窗口标题脱敏不回归
大量数据下刷新不卡顿
```

---

# 阶段 5.3：SessionsView MVP

## 阶段目标

让用户能看到会话边界、时长分布和关闭原因。

## 页面内容

第一版展示：

```text
Started local time
Ended local time
Process
Display name
Total duration
Active duration
Idle duration
Unknown duration
Close reason
Session id
```

## 基础交互

第一版支持：

```text
Refresh
最近 200 条
范围切换：Today / Last 24 Hours / Recent
CloseReason 轻量筛选：All / Open / ProcessChanged / Paused / Stopped / Other
```

## 验收重点

```text
Pause 后可看到 close_reason = Paused
Stop 后可看到 close_reason = Stopped
切换应用后可看到 close_reason = ProcessChanged
当前未关闭的 session 显示 Open
不在第一版筛选中预设尚未确认真实写入的 close_reason
open session 展示不应混乱
```

说明：

```text
AgentStarted / PrivacyExcluded / Sleep / ScreenLock / AgentCrashRecovered 等 close_reason
需要先确认真实链路会写入 app_sessions.close_reason，再加入独立筛选。
第一版统一落到 Other。
```

## 验收标准

```text
SessionsView 能显示最近 session
Today 按本地日重叠查询，Last 24 Hours 范围正确
时长格式可读
close_reason 可见
open session 的 ended time 显示“正在进行”或等价文案，不显示空白
空表显示友好空态
```

---

# 阶段 5.4：AppsView MVP

## 阶段目标

让用户能看到应用维度的今日使用排行。

## 页面内容

第一版展示：

```text
Rank
Display name
Process name
Active duration
Total duration
Idle duration
Unknown duration
Session count
Last used local time
```

## 排行口径

建议第一版使用：

```text
按 active_duration_seconds 降序
active 相同时按 total_duration_seconds 降序
再按 process_name 升序稳定排序
```

原因：

```text
active_duration 更接近真实使用
total_duration 容易被长时间 idle 虚高
```

## 基础交互

第一版支持：

```text
Refresh
Today
Top 50
```

接口建议：

```csharp
GetAppUsageForLocalDayAsync(DateOnly localDate, int limit = 50)
```

第一版 UI 只传 Today，但接口保留 localDate 参数，避免后续扩展 7 天排行时推翻查询服务。

暂不做：

```text
7 天趋势
应用分类
应用别名编辑
图表
导出
```

## 验收标准

```text
AppsView 能显示今日应用排行
排行按 active_duration_seconds 降序
active 相同时按 total_duration_seconds 降序，再按 process_name 升序
display_name fallback：display_name -> process_name -> Unknown
WUJI / WUJI Agent 展示名映射不回归
session_count 正确
last_used_time 正确
空数据时显示友好空态
```

---

# 阶段 5.5：导航与页面收口

## 阶段目标

把新页面接入现有 WPF App，同时避免大规模 UI 架构重构。

## 推荐做法

开发顺序仍然建议：

```text
Samples
Sessions
Apps
```

导航顺序建议面向用户视角：

```text
Dashboard
Apps
Sessions
Samples
Diagnostics
Settings
```

原因：

```text
用户通常先看总览，再看应用排行，再看会话，最后才看原始采样和诊断。
Samples 更偏底层，不适合放在 Apps / Sessions 前面。
```

短期可以继续沿用当前主窗口导航结构，但新页面建议直接拆成独立 View + ViewModel：

```text
src/QuantifiedSelf.Windows.App/Views/SamplesView.xaml
src/QuantifiedSelf.Windows.App/Views/SessionsView.xaml
src/QuantifiedSelf.Windows.App/Views/AppsView.xaml

src/QuantifiedSelf.Windows.App/ViewModels/SamplesViewModel.cs
src/QuantifiedSelf.Windows.App/ViewModels/SessionsViewModel.cs
src/QuantifiedSelf.Windows.App/ViewModels/AppsViewModel.cs
```

Dashboard 和 Diagnostics 可以暂时不拆。  
但不要把 Samples / Sessions / Apps 全部继续塞进 `MainWindow.xaml`，避免 UI 债务继续膨胀。  
如果现有项目结构暂时不适合完整 MVVM，本阶段至少拆出 UserControl。

## 保持不回归

必须保留：

```text
顶部 Agent 控制栏
Start Agent
Stop Agent
Pause
Resume
Refresh
Open Settings
Dashboard 原统计
Diagnostics 原事件和错误展示
```

## 验收标准

```text
页面切换正常
Refresh 能刷新当前页面数据
导航顺序为 Dashboard / Apps / Sessions / Samples / Diagnostics / Settings
Dashboard 不回归
Diagnostics 不回归
Agent 控制按钮不回归
新页面不显著增加 MainWindow.xaml 复杂度
```

---

# 阶段 5.6：验收、稳定化与收口

## 自动化测试建议

建议新增或扩展：

```text
SampleQueryService_ReturnsRecentSamplesWithStableOrdering
SampleQueryService_UsesLimit
SessionQueryService_ReturnsRecentSessionsWithStableOrdering
SessionQueryService_ReturnsSessionsOverlappingLocalDay
AppUsageQueryService_RanksAppsByActiveDuration
AppUsageQueryService_UsesStableTieBreaking
AppUsageQueryService_ReturnsLastUsedTime
SamplesViewModel_LoadsRecentSamples
SessionsViewModel_LoadsRecentSessions
AppsViewModel_LoadsTodayAppUsage
DataViews_ReturnEmptyListsForEmptyDatabase
DataViews_DoNotLeakExcludedProcessSamples
DataViews_QueryServicesUseReadOnlyConnections
```

## 手动验收建议

建议手动验证：

```text
运行 Agent 10-15 分钟
切换几个常用应用
触发 Pause / Resume / Stop
打开 SamplesView 查看最近样本
打开 SessionsView 查看 session close_reason
打开 AppsView 查看今日排行
确认 Dashboard / Diagnostics 仍正常
确认 maskWindowTitles=true 时 SamplesView 不显示真实标题
确认排除进程不进入 samples/sessions
```

## 最终验收标准

本阶段完成后，应满足：

```text
1. dotnet build 0 warning / 0 error
2. dotnet test 全部通过
3. Dashboard 原功能不回归
4. Diagnostics 原功能不回归
5. SamplesView 能显示最近 200 条样本
6. SamplesView 支持 ActivityState 基础筛选
7. SessionsView 能显示最近 session
8. SessionsView 能看到 close_reason
9. AppsView 能显示今日应用排行
10. AppsView 按 active duration 排序
11. AppsView display_name fallback 正确
12. 所有数据查询使用只读连接，例如 Mode=ReadOnly;Pooling=False
13. 所有列表查询有 LIMIT
14. Today Sessions 按本地日重叠查询
15. 空库 / 空表时 UI 显示友好空态
16. maskWindowTitles=true 时不泄露真实窗口标题
17. SamplesView 默认不展示完整 executable_path
18. excludedProcesses 命中时不出现对应 samples/sessions
19. 大量数据下刷新和滚动不卡顿
20. 新页面至少拆成 UserControl，优先独立 View + ViewModel
```

---

## 推荐提交顺序

建议按小提交推进：

```text
1. feat(data): add samples sessions apps query services
2. feat(samples): add recent samples view
3. feat(sessions): add sessions view
4. feat(apps): add app usage view
5. test(data): cover query ordering limits and empty database
6. docs(plan): record data views mvp acceptance
```

---

## 后续候补

Data Views MVP 完成后，再进入：

```text
1. Settings 编辑能力 MVP
2. PruneData / ClearHistory
3. Named Pipe / gRPC IPC
4. Agent 状态流订阅 / RefreshService 优化
5. 托盘
6. 开机自启
7. 安装包
8. 7 天趋势和图表
9. 应用分类
10. 浏览器网页级识别
```

---

## 一句话总结

Agent Events 与 Diagnostics MVP 已经让 WUJI 具备“能解释自己为什么这么运行”的能力。  
下一步最值得做的是 `Samples / Sessions / Apps 数据浏览 MVP`：先把现有 SQLite 数据变成可浏览、可筛选、可验证的用户界面，再继续推进 Settings、IPC、托盘和产品化能力。
