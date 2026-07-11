我审核后觉得：**这份计划可以执行，方向是对的，而且阶段边界比之前更清楚**。它很好地承接了上一阶段 `Agent Events 与 Diagnostics MVP`，没有急着做托盘、IPC、安装包，而是转向“让用户能看到历史明细”，这个优先级是合理的。计划里把下一阶段聚焦为 `SamplesView / SessionsView / AppsView`，并明确 WPF 只读 SQLite、Agent 继续作为唯一写入者、查询必须有 LIMIT，这些都符合完整重构方案的架构原则。

我建议你按这份计划推进，但做几处小调整。

---

# 总体评价

这份计划的最大优点是：**没有把 Data Views 做成大而全的 UI 重构**。

它明确说：

```text
阶段 5.1：查询服务补齐
阶段 5.2：SamplesView MVP
阶段 5.3：SessionsView MVP
阶段 5.4：AppsView MVP
阶段 5.5：导航与页面收口
阶段 5.6：验收、稳定化与后续边界
```

这个顺序是稳的。先补只读查询服务，再做页面，最后再收导航和验收，比一上来大拆 `MainWindow` 更安全。文档里也明确“不先大拆 MainWindow，避免把数据浏览 MVP 拖成 UI 架构重构”，这个判断是对的。

从整体路线看，它也符合你后续规划里“数据浏览 → 设置与数据管理 → IPC 与实时状态 → 常驻体验 → 长期统计与产品化”的主线。

---

# 我建议保留的部分

## 1. 先做查询服务，再做 UI

这个很好。建议你严格执行：

```text
SampleQueryService
SessionQueryService
AppUsageQueryService
```

不要把这些查询直接写到 ViewModel 里。否则后面测试、复用、分页都会麻烦。

## 2. SamplesView / SessionsView / AppsView 的顺序合理

建议顺序不要改：

```text
SamplesView → SessionsView → AppsView
```

原因是：

```text
SamplesView 验证真实采样是否正确
SessionsView 验证 session 合并和 close_reason 是否正确
AppsView 验证应用级统计口径是否正确
```

这三者是从底层到上层的关系。

## 3. 排行按 active_duration 是对的

AppsView 按：

```text
active_duration_seconds DESC
total_duration_seconds DESC
process_name ASC
```

这个口径比按 total duration 更合理。否则用户离开电脑时，某个前台应用的 idle 时间会把排行拉高。

## 4. 暂不做复杂图表和导出是对的

这阶段最重要的是“能看、能筛、不卡、口径对”。
图表、7 天趋势、CSV/Excel 导出都可以后置。

---

# 需要调整的地方

## 1. SessionsView 的 CloseReason 筛选项需要核对真实枚举

计划里写了：

```text
All / ProcessChanged / Paused / Stopped / AgentStarted / PrivacyExcluded
```

这里我建议谨慎一点。

你当前真实链路里明确存在的主要 close_reason 应该是：

```text
ProcessChanged
Paused
Stopped
Open
```

至于：

```text
AgentStarted
PrivacyExcluded
```

是否真的会作为 `app_sessions.close_reason` 出现，需要先核对代码和数据库。如果没有实际写入，就不要先放到 UI 筛选项里。

建议第一版改成：

```text
All
Open
ProcessChanged
Paused
Stopped
Other
```

等后面真的实现：

```text
AgentCrashRecovered
Sleep
ScreenLock
PrivacyExcluded
```

再加独立筛选。

---

## 2. `GetSessionsForLocalDayAsync(localDate)` 要注意“跨日重叠”

计划里提出：

```text
GetSessionsForLocalDayAsync(localDate)
```

这个很有必要，但实现时不能只写：

```sql
WHERE date(started_at_utc) = localDate
```

因为跨天 session 会漏掉。

正确语义应该是：

```text
会话与本地当天时间区间有重叠
```

也就是逻辑上：

```text
session.started_at < local_day_end
AND COALESCE(session.ended_at, now) > local_day_start
```

第一版如果只是展示列表，可以先查“与当天重叠的 session”。
如果要计算今日时长，则还要做重叠切分。Dashboard 已经有相关统计口径，SessionsView 第一版可以先不切分，只要列表包含跨日重叠 session 即可。

建议你在计划里把这条写清楚：

```text
Today Sessions 按“与本地当天有时间重叠”查询，不按 started_at 日期简单过滤。
```

---

## 3. SamplesView 不建议直接展示 executable_path

计划里已经说第一版不建议直接展示完整路径，这点我赞同。建议进一步明确：

```text
默认不显示 executable_path
详情区只显示文件名或脱敏路径
完整路径暂时只在 Diagnostics / 开发模式下查看
```

原因是路径里可能有用户名、项目名、隐私目录。你前一阶段刚刚强化了路径脱敏和 payload 白名单，这里不要在 UI 上又把完整路径放出来。

---

## 4. AppsView 的 display_name 映射要有 fallback

计划里提到：

```text
Display name
Process name
WUJI / WUJI Agent 展示名映射不回归
```

建议明确 fallback 规则：

```text
display_name 优先来自 app-name-map / 已有映射
如果没有 display_name，则显示 process_name
如果 process_name 也为空，则显示 Unknown
```

这样 AppsView 不会因为某些进程没有映射而出现空白行。

---

## 5. 查询服务要明确只读连接字符串

计划里说“UI 查询只读 SQLite”，但建议在阶段 5.1 里明确验收：

```text
所有 Data Views 查询服务使用 Mode=ReadOnly;Pooling=false
```

并且测试或代码审查时确认：

```text
SampleQueryService
SessionQueryService
AppUsageQueryService
DiagnosticsQueryService
```

都不创建写连接。

这和完整方案里“Agent 是唯一写入者，WPF 只读查询”的原则一致。

---

## 6. 不要让 MainWindow.xaml 继续无限膨胀

计划里说短期可以继续当前 Tab 结构，这可以接受。

但我建议设一个边界：

```text
如果新增 Samples / Sessions / Apps 后 MainWindow.xaml 超过可维护范围，
本阶段至少拆出 UserControl，不一定完整 MVVM 大重构。
```

折中方案：

```text
Views/SamplesView.xaml
Views/SessionsView.xaml
Views/AppsView.xaml

ViewModels/SamplesViewModel.cs
ViewModels/SessionsViewModel.cs
ViewModels/AppsViewModel.cs
```

Dashboard 和 Diagnostics 可以暂时不拆，但新页面最好直接按独立 View/ViewModel 建。

这样不会把后面 UI 重构债务继续堆高。

---

# 我建议你修改计划中的几个点

可以把计划微调为：

```text
1. SessionsView close_reason 筛选第一版只做：
   All / Open / ProcessChanged / Paused / Stopped / Other

2. Today Sessions 查询按“与本地当天时间区间重叠”，不要只按 started_at 日期。

3. SamplesView 默认不展示完整 executable_path，只显示脱敏路径或文件名。

4. AppsView display_name 增加 fallback：
   display_name → process_name → Unknown

5. 所有 Data Views 查询服务明确使用只读连接。

6. 新增页面尽量直接用独立 View + ViewModel，避免 MainWindow.xaml 继续膨胀。
```

---

# 对阶段 5.1 的具体建议

阶段 5.1 是最关键的基础层。我建议你优先把测试写好。

最重要的测试不是 ViewModel，而是 QueryService：

```text
SampleQueryService_ReturnsRecentSamplesWithStableOrdering
SampleQueryService_UsesLimit
SessionQueryService_ReturnsRecentSessionsWithStableOrdering
SessionQueryService_ReturnsSessionsOverlappingLocalDay
AppUsageQueryService_RanksAppsByActiveDuration
AppUsageQueryService_UsesStableTieBreaking
DataViews_ReturnEmptyListsForEmptyDatabase
```

尤其是：

```text
ORDER BY xxx DESC, id DESC
LIMIT
本地日重叠
active duration 排序
空库不崩
```

这些一旦锁住，后面 UI 就比较稳。

---

# 对阶段 5.2 SamplesView 的建议

第一版 SamplesView 不要做复杂筛选。你计划里的：

```text
All / Active / Idle / Unknown
```

足够了。

建议再加一个小字段：

```text
IsMasked / TitleState
```

如果 `window_title` 为空，用户可能不知道是“真的空标题”还是“标题脱敏”。可以显示：

```text
Title: [Masked]
Title: [Empty]
```

不过这个要看你数据库里是否有足够字段区分。若目前无法区分，就先统一显示：

```text
[Hidden]
```

不要为了这个改采样表结构。

---

# 对阶段 5.3 SessionsView 的建议

SessionsView 要重点展示 `close_reason`，这是这一页的价值。

建议 UI 上把 close_reason 做成明显列，不要放到详情里。因为用户会关心：

```text
为什么这段结束了？
是 ProcessChanged？
是 Paused？
是 Stopped？
还是当前 Open？
```

另外，open session 的 ended time 可以显示：

```text
正在进行
```

不要显示空白。

---

# 对阶段 5.4 AppsView 的建议

AppsView 第一版只做 Today 是可以的。

但建议保留一个时间范围枚举接口，即使 UI 只传 Today：

```csharp
GetAppUsageForLocalDayAsync(DateOnly localDate, int limit = 50)
```

后面扩展 7 天排行时，不要推翻接口。

另外排行卡片里建议同时显示：

```text
Active
Total
Idle
Sessions
```

不要只显示一个总时长，否则 active 排序的口径用户看不出来。

---

# 对阶段 5.5 导航收口的建议

你计划里说：

```text
Dashboard
Diagnostics
Samples
Sessions
Apps
Settings
```

这个顺序我建议改成：

```text
Dashboard
Apps
Sessions
Samples
Diagnostics
Settings
```

原因是用户视角通常是：

```text
先看总览
再看应用
再看会话
最后才看原始采样和诊断
```

`Samples` 更偏底层，不适合放太前面。
但开发顺序仍然可以是 Samples → Sessions → Apps。

也就是：

```text
开发顺序：Samples → Sessions → Apps
导航顺序：Dashboard → Apps → Sessions → Samples → Diagnostics → Settings
```

---

# 最终审核结论

这份计划可以执行，建议你做小幅修改后推进。

我给它的评价是：

```text
方向：正确
阶段边界：清楚
风险控制：较好
查询优先于 UI：正确
暂缓事项：合理
需要补强：local day overlap、close_reason 枚举、只读连接、UI 拆分边界、路径展示隐私
```

推荐你下一步直接从：

```text
阶段 5.1：查询服务补齐
```