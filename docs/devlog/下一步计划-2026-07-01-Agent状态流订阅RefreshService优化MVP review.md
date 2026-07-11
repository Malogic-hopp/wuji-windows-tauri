这份阶段 9 计划整体**可以执行**，方向也对。它没有急着做托盘、自启、安装包，而是先把 WPF 里分散的刷新逻辑收束成统一状态刷新地基，这个顺序很稳。阶段 8 已经完成了 IPC 请求响应主通道，现在下一步确实应该先优化“状态如何被 UI 感知和派发”，再进入托盘/后台运行。

---

# 总体审核结论

我建议这份计划可以作为阶段 9 正式执行文档。

当前目标很准确：

```text id="vvgzo4"
统一 RefreshService
IPC GetStatus 轻量状态轮询
Agent 状态刷新与当前页数据刷新解耦
按钮可用性统一
Diagnostics 展示 refresh loop 健康状态
```

这一阶段不是做“真正长连接状态流”，而是先用阶段 8 已经稳定的 `IPC GetStatus` 做集中轮询。这是合理的，因为真正的状态流会引入长连接、重连、事件顺序、背压和生命周期管理，复杂度会明显增加。

---

# 我认可的关键设计

## 1. 先集中 RefreshService，而不是直接做托盘

这个判断是对的。

如果现在直接做托盘，会马上遇到状态同步问题：

```text id="lygvnh"
主窗口显示 Running，托盘菜单显示 Paused
Maintenance 中主窗口按钮禁用，托盘按钮仍可点
Agent Stop 后托盘 tooltip 还显示 Running
IPC 断连后托盘和主窗口状态不一致
```

所以先做统一状态快照，后面托盘才能复用。

---

## 2. 状态刷新和数据刷新解耦

这是阶段 9 最重要的设计点。

建议严格保留这个边界：

```text id="s6aqk9"
Agent 状态刷新：
    高频、轻量、短 timeout
    IPC GetStatus 优先
    runtime_state / health_state fallback

当前页数据刷新：
    低频、按当前页触发
    SQLite 只读查询
    只刷新当前页面
```

不要让 Apps/Sessions/Samples 这些较重查询拖慢顶部 Agent 状态、按钮状态和 Maintenance 状态显示。

---

## 3. 不把 SQLite 查询搬到 IPC

计划里明确：

```text id="kpi8ji"
控制走 IPC
状态走 IPC GetStatus + runtime/health fallback
数据浏览继续走 SQLite 只读查询
诊断继续走 agent_events SQLite + JSONL
```

这个边界必须继续保持。
阶段 9 不应该把 Samples / Sessions / Apps 数据查询搬到 IPC，否则会把控制通道变成数据通道，复杂度会失控。

---

# 建议补强 1：阶段名称可以稍微降调

文档标题叫：

```text id="65kdvn"
Agent 状态流订阅 / RefreshService 优化 MVP
```

但实际本阶段“不做真正长连接状态流”，而是先做 `IPC GetStatus` 轻量轮询。为了避免后续误解，建议在文档开头再强调一句：

```text id="6b9x2e"
本阶段的“状态流订阅”先采用集中状态轮询实现，不实现长连接推送式状态流；真正状态流作为后续增强。
```

或者阶段名可以更准确地写成：

```text id="q4fqxr"
Agent 状态刷新 / RefreshService 优化 MVP
```

不过不改名也可以，只要文档里写清楚。

---

# 建议补强 2：RefreshService 不要直接改 ViewModel 属性

计划里说 RefreshService 是协调器，不是业务大杂烩，这点很好。执行时建议更明确：

```text id="gb299x"
RefreshService 只返回 RefreshResult
MainWindowViewModel 负责把 RefreshResult 应用到 UI 属性
```

不要让 RefreshService 直接写：

```text id="86f6tt"
AgentStatusText
LastHeartbeatText
Button CanExecute
Dashboard items
Settings fields
```

否则它会变成“隐藏 ViewModel”，后面会难维护。

推荐边界：

```text id="o7fki1"
RefreshService:
    调度、取消、防重入、记录健康、返回结果

MainWindowViewModel:
    应用结果、更新绑定属性、NotifyCanExecuteChanged
```

---

# 建议补强 3：状态刷新和页面刷新用两个防重入通道

阶段 9.4 要避免慢页面查询阻塞状态刷新。实现上建议直接拆成两套锁/信号量：

```text id="9odtwa"
_statusRefreshGate
_pageRefreshGate
```

状态刷新可以取消旧请求：

```text id="ussm62"
新状态刷新到来 -> cancel previous status request -> latest wins
```

页面刷新建议先跳过重入：

```text id="2u80f2"
已有页面刷新进行中 -> skip -> skipped count +1
```

这和计划里的 MVP 建议一致。这样能避免第一版引入太多并发竞态。

---

# 建议补强 4：状态结果要有版本号或时间戳

高频状态轮询后，很容易出现旧结果晚于新结果返回，覆盖 UI。

建议 `RefreshResult` 或 `AgentUiStateSnapshot` 加：

```text id="9x1sbf"
RefreshSequence
StartedAtUtc
CompletedAtUtc
```

MainWindowViewModel 应用状态时只接受最新结果：

```text id="hhnxqj"
if result.Sequence < _latestAppliedStatusSequence:
    ignore
```

这个能防止：

```text id="jl34wh"
旧的 fallback 状态覆盖新的 IPC Running 状态
旧的 Stale 判断覆盖刚恢复的 Running 状态
```

---

# 建议补强 5：状态 polling 默认用 2 秒，不要一开始 1 秒

计划写 1–2 秒都可以。我建议 MVP 默认：

```text id="zz6nzp"
StatusPollingInterval = 2s
```

理由：

```text id="vc8jbq"
1. 2 秒已经比页面刷新快很多。
2. IPC GetStatus 虽轻，但长跑更稳。
3. 后续可以在命令执行后的短时间内临时加速。
```

比如执行 Pause/Resume/ReloadConfig/PruneData/ClearHistory 后，短时间内进入：

```text id="6o229q"
Fast status polling: 500ms, duration 5s
```

这个可以作为后续增强，不一定阶段 9 必做。

---

# 建议补强 6：按钮状态统一要覆盖 SettingsViewModel

阶段 9.3 里说统一 Start/Pause/Resume/Stop/ReloadConfig/PruneData/ClearHistory 的按钮状态。这里要注意：部分按钮在 `MainWindowViewModel`，部分在 `SettingsViewModel`。

建议明确：

```text id="f2zhlo"
AgentStatusSnapshot 应同步派发给 MainWindowViewModel 和 SettingsViewModel。
```

否则可能出现：

```text id="r55pnh"
顶部按钮已经禁用
Settings 里的 PruneData / ClearHistory 仍然可点
```

可以采用：

```text id="8js2yj"
SettingsViewModel.UpdateAgentStatus(snapshot)
```

或者 MainWindowViewModel 统一调用 SettingsViewModel 的状态更新方法。

---

# 建议补强 7：Settings IsDirty 要区分“配置表单刷新”和“状态刷新”

现在文档强调 Settings 未保存编辑优先，这是对的。执行时建议把规则写得更细：

```text id="hmlove"
Settings IsDirty = true:
    不调用 LoadAsync 覆盖配置字段
    仍允许更新 Agent 状态、IPC 状态、RefreshHealth、按钮可用性
```

也就是说，不能因为 Settings 正在编辑，就停止所有状态更新。
应该只跳过配置文件重新加载，不跳过 Agent 状态刷新。

---

# 建议补强 8：Diagnostics refresh health 先用 App 内存，不写 agent_events

计划已经写“不高频写 agent_events”，这个要坚持。

Refresh loop 健康属于 UI 运行时状态，第一版存在内存里就可以：

```text id="7ybgb4"
LastStatusRefreshSuccessUtc
LastStatusRefreshError
LastPageRefreshSuccessUtc
LastPageRefreshError
SkippedRefreshCount
LastStatusSource
```

不要每次刷新失败都写 agent_events，否则会变成新的日志噪声。

---

# 建议补强 9：手动 Refresh 的语义要固定

建议文档里再明确手动 Refresh：

```text id="zt4g3n"
手动 Refresh = 立即刷新 Agent 状态 + 当前页数据
```

如果页面数据刷新正在进行：

```text id="noa55a"
MVP：跳过重复页面刷新，记录 skipped count
```

如果状态刷新正在进行：

```text id="ylzqs1"
MVP：取消旧状态刷新，启动新状态刷新
```

这样用户点击 Refresh 时行为更可预测。

---

# 建议补强 10：阶段 9.1 第一批提交要更小

9.1 第一批建议只做：

```text id="z1vaf7"
RefreshService
RefreshHealthSnapshot
RefreshOptions
防重入 / skipped count
MainWindowViewModel.RefreshAsync 改为调用 RefreshService
用户可见行为不变
```

不要在 9.1 就引入新的 status polling timer。
状态 polling 放 9.2，再做更安全。

第一批提交可以用：

```text id="5zf4t6"
feat(refresh): add refresh coordination service
```

---

# 推荐执行顺序

可以按计划执行：

```text id="xat0gw"
9.1 RefreshService 现状梳理与统一接口
9.2 Agent status 集中轮询 GetStatus
9.3 ViewModel 状态派发与按钮可用性统一
9.4 当前页数据刷新与 Agent 状态刷新解耦
9.5 Diagnostics 刷新健康状态展示
9.6 断连 / 重连 / Agent 重启场景验收
9.7 长跑验证与收口
```

这个顺序合理，不建议合并阶段。

---

# 阶段 9 验收时重点盯这些

```text id="wf29kc"
1. Pause / Resume 后顶部状态和按钮状态是否快速一致。
2. Maintenance 中 MainWindow 和 Settings 两边按钮是否同时禁用。
3. Apps/Sessions/Samples 慢查询是否不影响 Agent 状态刷新。
4. Settings IsDirty 时配置字段是否不被覆盖。
5. Agent Stop/Start 后 IPC 状态和 UI 状态是否自动恢复。
6. RefreshHealth 是否能解释最近一次失败和 skipped count。
7. 长跑 30 分钟是否无 Stale 误判、无 UI 卡顿、无状态漂移。
```

---

# 最终审核结论

这份阶段 9 计划可以执行。
建议补充的主要是 6 个细节：

```text id="rcyc4p"
1. 明确本阶段不做真正长连接状态流，只做集中状态轮询。
2. RefreshService 只返回结果，不直接写 ViewModel 属性。
3. 状态刷新和页面刷新用独立防重入/取消机制。
4. RefreshResult 加 sequence/timestamp，防止旧结果覆盖新结果。
5. Settings IsDirty 只阻止配置字段刷新，不阻止状态刷新。
6. MainWindowViewModel 和 SettingsViewModel 共用同一 AgentStatusSnapshot 更新按钮状态。
```

可以开始执行，第一步从 **9.1：RefreshService 现状梳理与统一接口** 开始，不要一上来做真正状态流或托盘。
