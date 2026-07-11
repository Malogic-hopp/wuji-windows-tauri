# 下一步计划：Agent 状态刷新 / RefreshService 优化 MVP（2026-07-01，review 后修订版）

本文档作为 `下一步计划-2026-07-01-NamedPipe控制通道MVP.md` 阶段 8「Named Pipe 控制通道 MVP」完成后的下一阶段正式计划。

上一阶段已经完成：

```text
Named Pipe 控制通道 MVP
```

当前项目已经从：

```text
能采集、能诊断、能浏览、能配置、能清理
```

推进到：

```text
具备 IPC 请求响应 + 文件 fallback 的本地产品闭环
```

下一步不建议马上进入托盘、开机自启、安装包、趋势图、导出或应用分类。  
更优先的是把 WPF 当前分散的刷新逻辑收束成稳定的状态刷新地基：

```text
Agent 状态刷新 / RefreshService 优化 MVP
```

一句话目标：

```text
让 WPF 不再主要依赖分散定时刷新来感知 Agent 状态，而是通过统一 RefreshService 和 IPC GetStatus 轻量状态轮询，获得更及时、更稳定、更低噪声的 UI 状态更新。
```

本文档文件名中仍保留“状态流订阅”，但阶段 9 MVP 的实际实现范围明确降调为：

```text
本阶段先做集中状态轮询，不实现长连接推送式状态流；
真正 Named Pipe 状态流 / 事件订阅作为后续增强。
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
PruneData / ClearHistory 数据清理
Maintenance 状态
Named Pipe IPC 主控制通道
agent_control.json file fallback
Diagnostics IPC 状态展示
```

阶段 8 已完成验收：

```text
dotnet build QuantifiedSelf.Windows.sln --no-restore
    通过，0 warnings / 0 errors

dotnet test QuantifiedSelf.Windows.sln --no-restore
    通过，197/197

手动验收与长跑验证通过
```

当前 WPF 刷新链路大致是：

```text
DispatcherTimer
    ↓
MainWindowViewModel.RefreshAsync
    ↓
AgentStatusService.GetStatusAsync
    ↓
AgentProcessService.GetAgentProcessInfoAsync
    ↓
RefreshCommonStatus
    ↓
RefreshCurrentPageDataAsync
```

这条链路已经可用，但开始出现几个自然限制：

```text
1. Agent 状态刷新和重数据页刷新绑在同一个 RefreshAsync 里。
2. 当前页数据刷新慢时，Agent 状态也会被拖慢。
3. 按钮可用性、Maintenance 守卫、状态文案仍主要散落在 MainWindowViewModel。
4. Diagnostics 只能看到 IPC 状态，不能看到 Refresh loop 本身是否健康。
5. Settings IsDirty 守卫已经补上，但仍缺少统一刷新策略来避免未来回归。
6. 后续托盘菜单、状态 tooltip、最小化后台运行都依赖统一的状态快照。
```

---

## Review 吸收结论

阶段 8 的下一阶段建议明确指向：

```text
阶段 9：Agent 状态刷新 / RefreshService 优化 MVP
```

完整重构方案中也把长期状态通信定义为：

```text
IPC GetStatus / 状态流 + runtime_state.json + health_state
```

本阶段吸收这些结论，但把范围收紧为：

```text
先集中 RefreshService
先用 IPC GetStatus 做轻量状态轮询
先统一 UI 状态快照和按钮可用性
暂不直接实现真正长连接状态流
暂不做托盘、开机自启或安装包
```

原因：

```text
1. 阶段 8 刚建立 request-response IPC，先复用 GetStatus 最稳。
2. 真正状态流需要长连接、重连、背压、事件顺序和生命周期管理，复杂度更高。
3. 当前痛点首先是刷新职责分散，不是缺少高频数据流。
4. 托盘和自启都需要稳定状态快照作为基础，不适合抢在 RefreshService 前做。
```

本阶段可接受的 MVP 形态是：

```text
WPF App
    ↓ 2s 轻量状态轮询
IPC GetStatus
    ↓ 成功
AgentStatusSnapshot
    ↓ 派发
MainWindow / Diagnostics / 当前页按钮状态

IPC 不可用
    ↓ fallback
runtime_state.json / health_state.json
```

本轮 review 后继续补充以下执行约束：

```text
1. RefreshService 只返回 RefreshResult，不直接写 ViewModel 绑定属性。
2. MainWindowViewModel 负责把 RefreshResult 应用到 UI 属性，并触发 NotifyCanExecuteChanged。
3. 状态刷新和页面刷新使用独立防重入 / 取消机制。
4. RefreshResult 必须带 RefreshSequence / StartedAtUtc / CompletedAtUtc，避免旧结果覆盖新状态。
5. 状态 polling 默认 2 秒；1 秒不作为 MVP 默认值。
6. Settings IsDirty 只阻止配置表单重新加载，不阻止 Agent 状态、IPC 状态、RefreshHealth 更新。
7. MainWindowViewModel 和 SettingsViewModel 应共用同一个 AgentStatusSnapshot 更新按钮可用性。
8. Refresh loop 健康状态第一版只保存在 App 内存，不写 agent_events。
9. 手动 Refresh 语义固定为：立即刷新 Agent 状态 + 当前页数据。
10. 阶段 9.1 不引入新的 status polling timer，状态 polling 放到阶段 9.2。
```

---

## 下一阶段目标

下一阶段目标命名为：

```text
Agent 状态刷新 / RefreshService 优化 MVP
```

一句话目标：

```text
把 Agent 状态刷新从页面数据刷新中解耦出来，形成统一、可取消、可观测、不会覆盖未保存编辑的 RefreshService。
```

这一阶段重点补齐：

```text
RefreshService
    统一 Agent status refresh
    统一当前页数据 refresh
    管理刷新节流、取消、跳过、失败记录

Agent status polling
    基于 IPC GetStatus 的轻量状态轮询
    IPC 不可用时 fallback 到 runtime_state / health_state

UI 状态派发
    Start / Pause / Resume / Stop
    ReloadConfig
    PruneData / ClearHistory
    Maintenance / NotRunning / Stale

Diagnostics
    展示 refresh loop 健康状态
    展示 last refresh success / error / skipped count
```

阶段完成后应满足：

```text
1. Agent 状态刷新有单一协调入口。
2. 当前页数据刷新仍只刷新当前页。
3. Agent 状态刷新不被 Apps / Sessions / Samples / Diagnostics 大查询拖慢。
4. IPC GetStatus 成功时 UI 能更及时更新状态。
5. IPC 不可用时继续 fallback 到 runtime_state / health_state。
6. Start / Pause / Resume / Stop / ReloadConfig / PruneData / ClearHistory 按钮可用性口径统一。
7. Maintenance 中控制按钮和清理按钮禁用。
8. Settings IsDirty 时不会被自动刷新覆盖。
9. Diagnostics 能看到 refresh loop status / last success / last error / skipped count。
10. Agent 重启后 IPC 状态和 UI 状态能自动恢复。
11. 长跑 30 分钟无 Stale 误判、无 UI 卡顿、无状态漂移。
```

---

## 为什么现在做这个

阶段 8 后，控制通道已经从：

```text
文件投递 + 状态轮询
```

升级为：

```text
IPC 请求响应 + 文件 fallback
```

但 UI 状态体验仍然主要靠：

```text
一个全局 RefreshAsync
一个 DispatcherTimer
当前页数据查询
若干 ViewModel 自己的 RefreshCommand
```

下一步如果直接做托盘，会立刻遇到：

```text
托盘菜单如何知道 Agent 已 Paused？
Stop 后托盘 tooltip 何时显示 NotRunning？
Maintenance 中托盘菜单如何禁用 PruneData / ClearHistory？
Agent 崩溃后主窗口和托盘如何同步状态？
IPC 断连后何时恢复？
```

这些都需要一个统一的状态刷新和状态派发层。  
所以阶段 9 要先把状态体验地基补齐，再做托盘、自启和安装包。

---

## 本阶段不做

本阶段暂不做：

```text
真正长连接状态流协议
采样数据流推送
SQLite 查询改 IPC
托盘图标
开机自启
安装包
Windows Service
趋势图
导出
应用分类
分页 SamplesView
复杂事件总线
跨进程通知中心
```

本阶段不删除：

```text
agent_control.json fallback
runtime_state.json
health_state.json
DispatcherTimer
页面自己的手动 RefreshCommand
```

它们仍然作为：

```text
fallback 控制通道
状态快照
手动恢复入口
故障排查入口
```

---

## 架构原则

### 1. 状态刷新和数据刷新解耦

Agent 状态刷新应该轻量、频繁、短 timeout：

```text
IPC GetStatus
runtime_state fallback
health_state fallback
```

页面数据刷新可以相对慢、按当前页触发：

```text
Dashboard 查询
Apps Top 50
Sessions 列表
Samples 列表
Diagnostics 最近事件
Settings 配置文件
```

不要让慢数据页查询阻塞 Agent 状态更新。

### 2. RefreshService 是协调器，不是业务大杂烩

RefreshService 负责：

```text
刷新调度
取消旧刷新
防重入
记录刷新健康
按页面分发刷新任务
输出 RefreshResult / AgentStatusSnapshot / RefreshHealthSnapshot
```

RefreshService 不负责：

```text
直接查询 SQLite 细节
直接写配置
直接执行 Agent 命令
直接修改 AgentStateMachine
直接删除数据
直接写 ViewModel 绑定属性
直接调用 NotifyCanExecuteChanged
直接改 Dashboard / Apps / Sessions / Samples / Settings 集合
```

推荐边界：

```text
RefreshService:
    调度、取消、防重入、记录健康、返回结果

MainWindowViewModel:
    应用 RefreshResult
    更新绑定属性
    更新页面集合
    NotifyCanExecuteChanged

SettingsViewModel:
    接收 AgentStatusSnapshot 或等价状态
    更新 Settings 内部 ReloadConfig / PruneData / ClearHistory 可用性
```

### 3. 控制走 IPC，数据走 SQLite

继续保持阶段 8 的边界：

```text
控制命令:
    Named Pipe IPC 优先
    agent_control.json fallback

状态查询:
    IPC GetStatus 优先
    runtime_state / health_state fallback

数据浏览:
    SQLite 只读查询

诊断:
    agent_events SQLite + JSONL
```

不要在阶段 9 把 Samples / Sessions / Apps 数据查询搬到 IPC。

### 4. UI 状态来自同一个快照

按钮可用性、顶部状态文案、Diagnostics 状态、当前页刷新提示应尽量来自同一个状态快照：

```text
AgentUiStateSnapshot 或 AgentStatusSnapshot
```

目标是避免：

```text
顶部显示 Running
按钮认为 Maintenance
Diagnostics 显示 Stale
Settings 认为 NotRunning
```

### 5. Settings 未保存编辑优先

Settings 页已有 `IsDirty` 守卫，本阶段必须继续保持：

```text
自动刷新不得覆盖未保存编辑
手动刷新也应明确遵守或给出清晰语义
Save 后才同步显示值
ReloadConfig 结果不应覆盖正在编辑的草稿
```

更细的规则是：

```text
Settings IsDirty = true:
    不调用 SettingsViewModel.LoadAsync 覆盖配置字段
    不重载 app-settings.json / windows-agent.json 到编辑草稿
    仍允许更新 Agent 状态
    仍允许更新 IPC 状态
    仍允许更新 RefreshHealth
    仍允许更新按钮可用性
```

### 6. 失败要可观测但不刷屏

RefreshService 失败应该可见：

```text
LastRefreshError
LastRefreshErrorUtc
SkippedRefreshCount
LastSuccessfulRefreshUtc
LastStatusSource
```

但不要高频写 agent_events。阶段 9 的刷新健康优先保存在 App 内存状态并显示到 Diagnostics，除非出现连续严重失败再考虑写事件。

### 7. 状态刷新和页面刷新独立并发控制

阶段 9 应明确拆出两套防重入 / 取消机制：

```text
_statusRefreshGate 或等价状态刷新协调器
_pageRefreshGate 或等价页面刷新协调器
```

MVP 语义：

```text
状态刷新:
    新状态刷新到来时可以取消旧状态请求
    latest wins
    旧结果不得覆盖新结果

页面刷新:
    已有页面刷新进行中时先跳过
    skipped count +1
    不取消正在进行的数据查询
```

这样能避免慢页面查询拖慢状态刷新，也避免第一版引入过多页面查询取消竞态。

### 8. RefreshResult 必须可排序

高频状态轮询后，旧请求可能晚于新请求返回。  
因此 `RefreshResult` 或 `AgentUiStateSnapshot` 必须包含：

```text
RefreshSequence
StartedAtUtc
CompletedAtUtc
```

MainWindowViewModel 应用结果时只接受最新状态：

```text
if result.Sequence < _latestAppliedStatusSequence:
    ignore
```

目标是避免：

```text
旧 fallback 状态覆盖新的 IPC Running 状态
旧 Stale 判断覆盖刚恢复的 Running 状态
```

---

## 建议新增或调整

建议新增 App 层服务：

```text
src/QuantifiedSelf.Windows.App/Services/RefreshService.cs
src/QuantifiedSelf.Windows.App/Services/RefreshResult.cs
src/QuantifiedSelf.Windows.App/Services/RefreshHealthSnapshot.cs
src/QuantifiedSelf.Windows.App/Services/RefreshOptions.cs
```

建议新增或调整模型：

```text
src/QuantifiedSelf.Windows.App/Models/AgentUiStateSnapshot.cs
```

`RefreshOptions` 建议第一版包含：

```text
StatusPollingInterval = 2 seconds
StatusConnectTimeout = 500-1000ms
StatusRequestTimeout = 1000-2000ms
PageRefreshInterval = app-settings refreshIntervalSeconds
```

建议调整：

```text
src/QuantifiedSelf.Windows.App/ViewModels/MainWindowViewModel.cs
    把 RefreshAsync 的调度职责逐步交给 RefreshService
    保留 UI 属性赋值和页面 ViewModel 绑定

src/QuantifiedSelf.Windows.App/Services/AgentStatusService.cs
    保持 IPC GetStatus 优先
    补充状态来源和 timeout 语义

src/QuantifiedSelf.Windows.App/Services/AgentIpcStatusService.cs
    继续记录 IPC 成功 / fallback
    可被 RefreshService 读取为状态来源之一

src/QuantifiedSelf.Windows.App/App.xaml.cs
    注册 RefreshService
```

建议 Diagnostics 增加展示字段：

```text
Refresh status
Last status refresh success
Last status refresh error
Last page refresh success
Last page refresh error
Skipped refresh count
Last status source: IPC / FileFallback / Unknown
Status polling interval
Page refresh interval
```

---

## 刷新语义建议

### Agent 状态刷新

Agent 状态刷新建议默认：

```text
Interval: 2 seconds
ConnectTimeout: 500-1000ms
RequestTimeout: 1000-2000ms
Failure fallback: runtime_state / health_state
```

说明：

```text
状态刷新只读、轻量，频率可以高于页面数据刷新。
如果 IPC 不可用，fallback 不能阻塞 UI。
2 秒已经明显快于页面数据刷新，长跑风险比 1 秒更低。
命令执行后的短时 500ms fast polling 可作为后续增强，不列入阶段 9 MVP 必做。
```

### 当前页数据刷新

页面数据刷新继续遵守 App Settings：

```text
refreshIntervalSeconds: 5..300
默认 15s
```

刷新范围：

```text
Dashboard:
    今日 summary + Top Apps + Recent Sessions

Apps:
    当前页时刷新 Today Top 50

Sessions:
    当前页时刷新当前 range

Samples:
    当前页时刷新当前 filter

Diagnostics:
    当前页时刷新 recent events / errors + refresh health

Settings:
    当前页且 IsDirty=false 时刷新配置字段
    IsDirty=true 时只跳过配置字段重载，不跳过状态 / IPC / RefreshHealth 更新
```

### 手动 Refresh

手动 Refresh 应触发：

```text
立即刷新 Agent 状态
立即刷新当前页数据
更新 RefreshHealthSnapshot
```

如果已有刷新进行中：

```text
状态刷新正在进行:
    取消旧状态刷新
    启动新的状态刷新
    latest wins

页面刷新正在进行:
    MVP 先跳过重复页面刷新
    skipped page refresh count +1
```

固定语义：

```text
手动 Refresh = 立即刷新 Agent 状态 + 当前页数据
状态刷新可以取消旧请求
页面刷新先保持防重入跳过
RefreshHealth 必须记录 skipped count
```

避免第一版引入过多竞态。

---

## 状态派发建议

建议 MainWindowViewModel 最终只消费一个状态结果：

```text
RefreshResult
    long RefreshSequence
    DateTime StartedAtUtc
    DateTime CompletedAtUtc
    AgentStatusSnapshot Status
    AgentProcessInfo? ProcessInfo
    RefreshHealthSnapshot Health
    string CurrentPage
    string StatusSource: NamedPipe / FileFallback / Unknown
    bool PageDataRefreshed
    bool PageRefreshSkipped
```

按钮可用性统一由状态决定：

```text
Start:
    NotRunning / Stopped / Stale 且不在 Maintenance

Stop:
    Running / Paused / Stale 且不在 Maintenance

Pause:
    Running 且不在 Maintenance

Resume:
    Paused 且不在 Maintenance

ReloadConfig:
    Running / Paused，NotRunning 时保留“下次启动生效”语义

PruneData / ClearHistory:
    Running / Paused 且不在 Maintenance
```

状态应同步派发给：

```text
MainWindowViewModel:
    顶部状态
    Start / Stop / Pause / Resume
    Diagnostics IPC / RefreshHealth 展示

SettingsViewModel:
    ReloadConfig
    PruneData
    ClearHistory
    Maintenance / NotRunning 提示
```

可以采用：

```text
SettingsViewModel.UpdateAgentStatus(snapshot)
```

或由 MainWindowViewModel 统一调用等价方法。关键要求是 MainWindow 和 Settings 两边按钮不能出现状态漂移。

注意：

```text
ClearHistory 的 CLEAR 二次确认不属于 RefreshService。
RefreshService 只提供按钮是否可用的状态依据。
```

---

## 阶段拆分

建议拆成：

```text
阶段 9.1：RefreshService 现状梳理与统一接口
阶段 9.2：Agent status 集中轮询 GetStatus
阶段 9.3：ViewModel 状态派发与按钮可用性统一
阶段 9.4：当前页数据刷新与 Agent 状态刷新解耦
阶段 9.5：Diagnostics 刷新健康状态展示
阶段 9.6：断连 / 重连 / Agent 重启场景验收
阶段 9.7：长跑验证与收口
```

这个顺序先抽调度边界，再优化状态更新，最后做 Diagnostics 和验收。

第一批提交必须很小：

```text
只新增 RefreshService / RefreshResult / RefreshHealthSnapshot / RefreshOptions
只迁移 MainWindowViewModel.RefreshAsync 的防重入与健康记录
只做防重入 / skipped count / safe error
不改变页面 UI
不改变 Agent 命令语义
不改变 SQLite 查询
不引入新的 status polling timer
```

建议提交信息：

```text
feat(refresh): add refresh coordination service
```

---

# 阶段 9.1：RefreshService 现状梳理与统一接口

## 阶段目标

新增 RefreshService 的最小接口和刷新健康模型，把现有 MainWindowViewModel 的刷新调度职责先迁出一层，但不改变用户可见行为。

## 建议新增

```text
src/QuantifiedSelf.Windows.App/Services/RefreshService.cs
src/QuantifiedSelf.Windows.App/Services/RefreshHealthSnapshot.cs
src/QuantifiedSelf.Windows.App/Services/RefreshOptions.cs
```

## 行为要求

```text
RefreshService:
    提供 RefreshAsync(currentPage, cancellationToken)
    内部调用 AgentStatusService.GetStatusAsync
    内部调用 AgentProcessService.GetAgentProcessInfoAsync
    记录 last success / error / skipped count
    防止刷新重入
    返回 RefreshResult，不直接写 ViewModel 属性
```

MainWindowViewModel：

```text
继续保留 RefreshCommand
继续保留 DispatcherTimer
RefreshAsync 改为调用 RefreshService
UI 属性更新仍在 ViewModel 内完成
```

## 验收标准

- 用户可见刷新行为不变。
- build 0 warning / 0 error。
- test 全部通过。
- MainWindowViewModel 不再直接管理刷新健康计数。
- RefreshService 记录 last refresh success / error / skipped count。
- RefreshResult 包含 RefreshSequence / StartedAtUtc / CompletedAtUtc。
- Refresh 失败文案仍然脱敏。
- Settings IsDirty 守卫不回归。
- 本阶段不新增 status polling timer。

## 建议测试

```text
RefreshService_RefreshesStatusAndProcessInfo
RefreshService_RecordsLastSuccess
RefreshService_RecordsSafeError
RefreshService_SkipsReentrantRefresh
RefreshService_ReturnsSequenceAndTimestamps
MainWindowViewModel_RefreshUsesRefreshService
MainWindowViewModel_SettingsDirtyStillSkipsLoad
```

## 不做什么

- 不改变自动刷新间隔。
- 不新增状态轮询 timer。
- 不改变按钮可用性。
- 不改页面 UI。

---

# 阶段 9.2：Agent status 集中轮询 GetStatus

## 阶段目标

新增轻量 Agent 状态轮询机制，让 Agent 状态更新频率和页面数据刷新频率解耦。

## 行为要求

```text
Status polling:
    周期 2s
    优先 IPC GetStatus
    失败 fallback runtime_state / health_state
    更新统一 AgentStatusSnapshot
    不刷新重数据页
    状态请求 latest wins
```

UI：

```text
顶部 AgentStatusText / LastHeartbeatText / LastSampleText 更及时更新
当前页数据仍按 refreshIntervalSeconds 刷新
```

## 验收标准

- Agent Running 时状态轮询能持续更新 heartbeat。
- Pause / Resume 后顶部状态不必等待重数据页刷新。
- IPC unavailable 时 fallback 状态可用。
- 状态轮询失败不会让页面数据刷新失败。
- 状态轮询不会覆盖 Settings 未保存编辑。
- Settings IsDirty 时仍允许状态 / IPC / RefreshHealth 更新。
- 旧状态轮询结果不能覆盖新状态结果。
- 状态轮询不会高频写 agent_events。

## 建议测试

```text
RefreshService_PollsAgentStatusWithoutRefreshingCurrentPageData
RefreshService_StatusPollingUsesIpcWhenAvailable
RefreshService_StatusPollingFallsBackToRuntimeState
RefreshService_IgnoresOlderStatusResult
MainWindowViewModel_StatusPollingUpdatesCommonStatus
MainWindowViewModel_StatusPollingDoesNotLoadSettingsWhenDirty
MainWindowViewModel_StatusPollingUpdatesStatusWhenSettingsDirty
```

## 不做什么

- 不做真正长连接状态流。
- 不推送 Samples / Sessions / Apps 数据。
- 不调整 SQLite 查询服务。

---

# 阶段 9.3：ViewModel 状态派发与按钮可用性统一

## 阶段目标

把 Start / Pause / Resume / Stop / ReloadConfig / PruneData / ClearHistory 的按钮可用性统一到同一套 Agent 状态判断。

## 行为要求

```text
统一 CanExecute:
    Start
    Stop
    Pause
    Resume
    ReloadConfig
    PruneData
    ClearHistory
```

状态变化后：

```text
RefreshService 更新 AgentStatusSnapshot
MainWindowViewModel 应用状态
MainWindowViewModel 派发状态给 SettingsViewModel
所有相关 command NotifyCanExecuteChanged
```

## 验收标准

- Running 时 Pause / Stop / ReloadConfig / PruneData / ClearHistory 可用。
- Paused 时 Resume / Stop / ReloadConfig / PruneData / ClearHistory 可用。
- Maintenance 时控制按钮和清理按钮禁用。
- NotRunning 时 ReloadConfig 保持“下次启动生效”语义，清理按钮不可用。
- Stale 时按钮策略明确，并与当前既有行为一致或更安全。
- 按钮状态不会和顶部 AgentStatusText 漂移。
- MainWindow 和 Settings 两边的 ReloadConfig / PruneData / ClearHistory 可用性不漂移。

## 建议测试

```text
MainWindowViewModel_CommandStatesFollowRunningStatus
MainWindowViewModel_CommandStatesFollowPausedStatus
MainWindowViewModel_CommandStatesDisableDuringMaintenance
MainWindowViewModel_ReloadConfigKeepsNotRunningSemantics
MainWindowViewModel_DataCleanupDisabledWhenNotRunning
SettingsViewModel_CommandStatesFollowSharedAgentStatus
SettingsViewModel_DataCleanupDisabledDuringMaintenance
```

## 不做什么

- 不改变命令执行链路。
- 不改变 ClearHistory 二次确认。
- 不改变 AgentStateMachine。

---

# 阶段 9.4：当前页数据刷新与 Agent 状态刷新解耦

## 阶段目标

把当前页数据刷新作为独立任务管理，避免慢页面查询拖慢 Agent 状态刷新。

## 行为要求

```text
Status refresh:
    高频、轻量、短 timeout
    独立状态刷新 gate
    新请求可取消旧请求
    latest wins

Page refresh:
    按当前页和 refreshIntervalSeconds 触发
    只刷新当前页
    独立页面刷新 gate
    有防重入和安全错误状态
    重入时跳过并记录 skipped count
```

当前页分发继续保持：

```text
Dashboard
Apps
Sessions
Samples
Diagnostics
Settings
```

## 验收标准

- Apps / Sessions / Samples 慢查询时，Agent 状态仍能继续更新。
- 切页时只刷新新当前页。
- Settings IsDirty 时仍不覆盖编辑表单。
- 手动 Refresh 能刷新状态和当前页数据。
- 手动 Refresh 遇到页面刷新重入时跳过页面刷新并记录 skipped count。
- 页面刷新错误不污染 Agent 状态。
- Agent 状态刷新错误不清空页面列表。

## 建议测试

```text
RefreshService_PageRefreshOnlyRefreshesCurrentPage
RefreshService_SlowPageRefreshDoesNotBlockStatusRefresh
RefreshService_PageRefreshErrorDoesNotClearAgentStatus
RefreshService_StatusRefreshErrorDoesNotClearPageData
MainWindowViewModel_ManualRefreshRefreshesStatusAndCurrentPage
MainWindowViewModel_ManualRefreshSkipsReentrantPageRefresh
```

## 不做什么

- 不做后台预加载所有页。
- 不做分页。
- 不做图表。

---

# 阶段 9.5：Diagnostics 刷新健康状态展示

## 阶段目标

让 Diagnostics 能解释 UI 刷新本身是否健康。

Refresh loop 健康状态第一版只保存在 WPF App 内存中：

```text
不写 agent_events
不写 JSONL
不新增 runtime 持久化文件
```

## 建议展示

```text
Refresh status
Last status refresh success
Last status refresh error
Last page refresh success
Last page refresh error
Skipped refresh count
Last status source
Status polling interval
Page refresh interval
```

## 安全要求

```text
错误文本必须走 DiagnosticMessageSanitizer
不展示完整本机路径
不展示异常原文
不展示 FullPipeName / SID
不展示 SQL
```

## 验收标准

- Diagnostics 显示 Refresh loop healthy / degraded。
- 最近刷新错误显示安全短句。
- skipped refresh count 可见。
- IPC / fallback 状态仍可见。
- RefreshHealth 不产生高频 agent_events。
- 旧 Diagnostics recent events / errors 不回归。

## 建议测试

```text
MainWindowViewModel_DiagnosticsShowsRefreshHealth
MainWindowViewModel_DiagnosticsShowsSkippedRefreshCount
MainWindowViewModel_DiagnosticsRedactsRefreshError
MainWindowViewModel_DiagnosticsKeepsIpcStatusVisible
```

## 不做什么

- 不把 RefreshHealth 写入 SQLite。
- 不新增 JSONL 日志文件。
- 不做复杂刷新图表。

---

# 阶段 9.6：断连 / 重连 / Agent 重启场景验收

## 阶段目标

验证新的状态刷新机制在 Agent 停止、重启、IPC 断连、fallback 恢复时表现稳定。

## 自动化验收

建议覆盖：

```text
Agent NotRunning:
    status polling fallback
    UI 不崩溃

IPC unavailable:
    RefreshHealth 记录 fallback
    LastStatusSource = FileFallback

Agent restart:
    状态从 NotRunning / Stale 恢复到 Running
    IPC 状态恢复 NamedPipe

Maintenance:
    状态轮询能看到 Maintenance
    结束后恢复 Running / Paused
```

## 手动验收

建议手动验证：

```text
1. 启动 WPF 和 Agent，确认状态约 2 秒内更新。
2. Pause，确认顶部状态和按钮状态快速变为 Paused。
3. Resume，确认顶部状态和按钮状态快速变为 Running。
4. PruneData，确认 Maintenance 中按钮禁用，完成后恢复。
5. ClearHistory，确认完成后 Paused。
6. Stop Agent，确认 UI 不崩溃，状态变为 NotRunning / fallback。
7. 重启 Agent，确认 IPC 和状态自动恢复。
8. Settings 编辑未保存时等待自动刷新，确认编辑内容不被覆盖。
9. Settings 编辑未保存时确认顶部 Agent 状态和 Settings 内按钮状态仍会更新。
10. 在 Apps / Sessions / Samples 刷新期间观察顶部 Agent 状态不被拖慢。
```

## 验收标准

- 断连不崩 UI。
- 重连能自动恢复。
- Maintenance 状态可见且按钮禁用。
- Settings IsDirty 不回归。
- Diagnostics 能解释刷新来源和最近错误。

---

# 阶段 9.7：长跑验证与收口

## 自动化验收

完成后应满足：

```text
1. build 0 warning / 0 error
2. test 全部通过
3. RefreshService 单元测试通过
4. Agent status polling 测试通过
5. 当前页数据刷新分发测试通过
6. Settings IsDirty 防覆盖测试通过
7. 按钮可用性统一测试通过
8. Diagnostics refresh health 测试通过
9. 阶段 8 IPC 相关测试不回归
10. 阶段 7 PruneData / ClearHistory 测试不回归
```

## 长跑验证

建议至少做：

```text
1. Agent Running 30 分钟，观察状态持续更新。
2. 期间多次切换 Dashboard / Apps / Sessions / Samples / Diagnostics / Settings。
3. Pause / Resume 循环 5 次，确认按钮状态不漂移。
4. ReloadConfig 2 次，确认配置应用不回归。
5. PruneData 1 次，确认 Maintenance 状态和恢复正常。
6. ClearHistory 1 次，确认 Paused 状态和空态正常。
7. 中途 Stop / Start Agent，确认 IPC 和状态恢复。
8. Settings 编辑未保存停留 1-2 分钟，确认表单不被覆盖。
9. Diagnostics RefreshHealth 能解释最近成功、最近错误和 skipped count。
10. MainWindow 与 Settings 内按钮状态始终一致。
```

## 收口文档

阶段完成后建议新增：

```text
docs/下一步计划-2026-07-01-Agent状态流订阅RefreshService优化MVP/
    01-阶段9.1-RefreshService现状梳理与统一接口.md
    02-阶段9.2-AgentStatus集中轮询GetStatus.md
    03-阶段9.3-ViewModel状态派发与按钮可用性统一.md
    04-阶段9.4-当前页数据刷新与Agent状态刷新解耦.md
    05-阶段9.5-Diagnostics刷新健康状态展示.md
    06-阶段9.6-断连重连Agent重启场景验收.md
    07-阶段9.7-长跑验证与收口.md
    阶段9-验收清单-YYYY-MM-DD.md
    阶段9-完成说明-YYYY-MM-DD.md
```

---

## 风险与对策

### 刷新并发引入竞态

风险：

```text
状态刷新和页面刷新同时写 UI 属性，出现旧结果覆盖新结果。
```

对策：

```text
使用版本号或 CancellationToken。
只允许最新状态写入 UI。
页面刷新和状态刷新写入的属性边界分开。
RefreshResult 带 RefreshSequence / StartedAtUtc / CompletedAtUtc。
MainWindowViewModel 丢弃旧 sequence 结果。
测试快速切页和慢查询场景。
```

### 高频状态轮询卡 UI

风险：

```text
2 秒状态轮询如果 timeout 太长，会让 UI 感觉卡顿。
```

对策：

```text
状态轮询全部 async。
IPC connect/request timeout 短。
失败快速 fallback。
默认 StatusPollingInterval = 2s。
命令后的 500ms fast polling 仅作为后续增强。
UI 线程只做最终属性更新。
```

### RefreshService 变成大而全服务

风险：

```text
RefreshService 吞掉所有业务逻辑，后续维护困难。
```

对策：

```text
RefreshService 只协调刷新，不直接实现查询细节。
RefreshService 只返回 RefreshResult，不直接写 ViewModel 属性。
数据查询仍留在 OverviewDataService / AppsDataService / SessionsDataService / SamplesDataService / DiagnosticsDataService。
控制命令仍留在 AgentControlService / AgentProcessService。
```

### Settings 编辑被覆盖

风险：

```text
状态轮询和自动刷新更频繁后，Settings 草稿更容易被覆盖。
```

对策：

```text
IsDirty 守卫保持为硬约束。
IsDirty 只阻止配置字段刷新，不阻止 Agent 状态和按钮状态更新。
测试自动刷新和手动刷新都不覆盖未保存编辑。
Diagnostics 记录 Settings refresh skipped when dirty。
```

### MainWindow 与 Settings 按钮状态漂移

风险：

```text
顶部控制按钮已经因 Maintenance 禁用，但 Settings 里的 PruneData / ClearHistory 仍可点击。
```

对策：

```text
MainWindowViewModel 和 SettingsViewModel 共用同一个 AgentStatusSnapshot。
MainWindowViewModel 应用状态后同步调用 SettingsViewModel.UpdateAgentStatus 或等价方法。
测试 MainWindow 和 Settings 两侧按钮状态一致。
```

### 状态来源混乱

风险：

```text
IPC GetStatus、runtime_state、health_state、process check 得出不同结论。
```

对策：

```text
AgentStatusService 统一状态合成。
RefreshResult 标明 LastStatusSource。
Diagnostics 显示状态来源。
GetStatus 成功时优先 IPC；失败才 fallback。
```

### 托盘需求提前混入

风险：

```text
阶段 9 实施时顺手引入托盘和自启，扩大范围。
```

对策：

```text
阶段 9 明确不做 TrayService。
只为后续托盘准备状态快照。
托盘作为阶段 10 候选。
```

---

## 后续候补

Agent 状态刷新 / RefreshService 优化 MVP 完成后，再考虑：

```text
1. 托盘图标与后台运行 MVP
2. 开机自启 MVP
3. 安装包 / 发布体验
4. 真正 Named Pipe 状态流或事件订阅
5. 最近 7 天趋势和图表
6. 应用分类
7. 分页 SamplesView
8. 数据导出 CSV
9. 本地数据备份与恢复
10. 本地数据加密
11. PipeSecurity / ACL 强化
12. ClearHistory confirmation token
13. WPF Runtime Smoke Test 自动化
```

其中最自然的下一步候选是：

```text
阶段 10：托盘图标与后台运行 MVP
```

但前提是阶段 9 先让 UI 和未来托盘共享稳定状态快照。

---

## 最终结论

阶段 8 完成后，WUJI 已经具备：

```text
本地采集
本地诊断
本地浏览
本地配置
本地清理
IPC 主控制通道
文件 fallback
```

下一步最自然、最有价值的是：

```text
Agent 状态刷新 / RefreshService 优化 MVP
```

这一阶段完成后，WUJI 的 UI 状态体验将从：

```text
分散定时刷新 + 页面刷新顺手带状态
```

升级为：

```text
统一 RefreshService + IPC GetStatus 轻量状态轮询 + 当前页数据刷新解耦
```

这会让后续托盘、开机自启、安装包和更完整的产品化体验有更稳的地基。
