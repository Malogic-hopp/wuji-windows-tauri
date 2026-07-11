这份阶段 10 计划整体**可行，可以作为下一阶段正式执行计划**。它的方向是对的：阶段 9 已经把状态刷新、断连恢复、MainWindow/Settings 状态一致性打稳，下一步做“托盘图标与后台运行 MVP”是自然的产品化推进，而不是过早进入开机自启、安装包或真正状态流。

---

# 总体审核结论

建议执行，但在正式拆分提示词前补强几个细节。

这份计划的核心目标清楚：

```text
让 WPF App 支持关闭/最小化到系统托盘，
托盘能显示 Agent 当前状态，
并能通过托盘执行 Show / Exit / Start / Pause / Resume / Stop。
```

范围控制也比较稳：

```text
不做开机自启
不做安装包
不做 Windows Service
不做真正状态流
不做复杂通知中心
不把数据查询搬到 IPC
不把 PruneData / ClearHistory 放进托盘
```

这些边界都合理。

---

# 我认可的关键设计

## 1. 关闭主窗口不等于停止 Agent

这个设计必须坚持。

阶段 10 应该明确：

```text
点击窗口 X：
    隐藏主窗口
    App 继续运行
    Agent 不受影响

托盘 Exit App：
    退出 WPF App
    不默认 Stop Agent

托盘 Stop Agent：
    用户明确点击后才停止 Agent
```

这能避免用户只是想隐藏窗口，却误停后台采集。计划里这一点写得很清楚。

---

## 2. 托盘复用阶段 9 的状态体系

这是阶段 10 最重要的架构原则。

托盘不要自己查状态，不要自己开 polling，不要自己读 `runtime_state.json`。应该复用：

```text
AgentStatusSnapshot
AgentCommandAvailability
RefreshService / 2 秒 status polling
MainWindowViewModel 当前已应用状态
AgentIpcStatusService
```

否则就会出现：

```text
MainWindow 显示 Running
Settings 认为 Paused
托盘菜单显示 NotRunning
```

计划中明确要求托盘复用阶段 9 状态快照，这是正确的。

---

## 3. 托盘命令复用现有控制服务

托盘菜单只应该是一个新入口，不应该重写控制逻辑。

也就是说：

```text
Start Agent → 复用 AgentProcessService / MainWindow 现有 Start 路径
Pause / Resume → 复用 AgentControlService
Stop → 复用 AgentProcessService graceful stop
```

不要在 `TrayService` 里直接写 `agent_control.json`，也不要直接实现 Named Pipe 调用。计划里也已经明确了这一点。

---

# 建议补强 1：先明确托盘依赖方案

计划里列了两个方案：

```text
H.NotifyIcon.Wpf
System.Windows.Forms.NotifyIcon
```

建议在 10.2 开始前先明确一个 MVP 选择。

我的建议是：

```text
MVP 优先使用 System.Windows.Forms.NotifyIcon。
```

理由：

```text
1. 不新增第三方依赖。
2. Windows 桌面托盘场景成熟稳定。
3. 适合先跑通生命周期、Show、Exit、Dispose。
4. 后续如果想更 MVVM 化，再评估 H.NotifyIcon.Wpf。
```

但需要注意：

```text
需要显式引用 Windows Forms。
NotifyIcon 必须 Dispose。
ContextMenuStrip 点击后要安全切回 WPF Dispatcher。
```

如果你更想要 MVVM 绑定方便，也可以选择 `H.NotifyIcon.Wpf`，但建议在计划里要求 agent 写清楚：

```text
为什么引入该 NuGet
版本号
是否影响发布
是否有替代方案
```

---

# 建议补强 2：CloseToTray 时不能触发 StopStatusPolling

当前阶段 9 的行为是窗口关闭时 `StopStatusPolling`。阶段 10 里如果点击 X 变成 Hide，那么要明确：

```text
CloseToTray=true 时：
    Cancel Closing
    Hide window
    不触发 StopStatusPolling
    不 Dispose TrayService
    不 Shutdown App
```

只有真正 Exit App 时才：

```text
StopStatusPolling
Dispose TrayService
Application.Shutdown
```

这个非常关键。否则会出现“窗口隐藏了，但 2 秒状态轮询停了，托盘状态不再更新”。

建议在计划 10.3 里补一句：

```text
CloseToTray / MinimizeToTray 只是隐藏窗口，必须保持 status polling 继续运行；只有 Exit App 才停止 status polling。
```

---

# 建议补强 3：隐藏到托盘后，页面数据刷新是否继续要明确

阶段 9 已经有页面自动刷新。窗口隐藏后是否继续刷新 Dashboard / Apps / Sessions / Samples？

MVP 有两种选择：

```text
方案 A：隐藏后页面 refresh timer 继续运行
优点：改动小
缺点：后台仍然查 SQLite，可能浪费

方案 B：隐藏后暂停 page refresh，只保留 status polling
优点：更省资源
缺点：要改刷新生命周期
```

我建议阶段 10 MVP 先用方案 A，**不改变阶段 9 的页面刷新语义**，避免扩大范围。

但计划里应明确：

```text
阶段 10 不改变页面自动刷新语义；隐藏窗口后是否暂停 page refresh 留到后续优化。
```

否则 agent 可能顺手改 page timer，带来新风险。

---

# 建议补强 4：TrayService 不要直接依赖 MainWindow 过深

计划里说 TrayService 可以管理窗口显示/隐藏。建议边界再明确一点：

```text
TrayService 负责托盘图标、菜单、tooltip、事件转发。
窗口 Show/Hide、Agent 命令执行通过回调或 command bridge 注入。
TrayService 不直接持有大量 ViewModel 业务状态。
```

推荐结构：

```text
TrayService
    ShowRequested
    ExitRequested
    StartRequested
    PauseRequested
    ResumeRequested
    StopRequested
    UpdateMenuState(TrayMenuState state)
```

然后由 `App.xaml.cs` 或 `MainWindowViewModel` 做桥接。

这样后续测试会更容易，也不容易让 `TrayService` 变成大杂烩。

---

# 建议补强 5：托盘菜单状态建议先做模型，再接真实 NotifyIcon

阶段 10.4 要避免 UI 控件难测。建议先有一个纯模型：

```text
TrayMenuState
    TooltipText
    CanStart
    CanPause
    CanResume
    CanStop
    IsMaintenance
    IpcStatusText
```

测试先验证：

```text
TrayMenuState.From(status, availability, ipcStatus)
```

然后 `TrayService` 只是把这个状态渲染到实际菜单。

这样能保证：

```text
托盘菜单状态和 MainWindow / Settings 使用同一套规则
```

---

# 建议补强 6：Exit App 语义要防止误关 Agent

计划已经写了 Exit App 不默认 Stop Agent。建议再补一个确认点：

```text
Exit App 不能调用 StopAgentCommand、StopAgentGracefullyAsync、Pause、ClearHistory 或任何 Agent 命令。
```

自动化或手动验收中要确认：

```text
Exit App 后 WPF 进程退出
Agent 进程仍然存在
```

这对用户信任很重要。

---

# 建议补强 7：通知先保守，甚至可以阶段 10.6 只做 tooltip

计划里 10.6 提到通知策略。我的建议是：

```text
阶段 10 MVP 可以不弹 toast，只更新 tooltip / menu 状态。
```

如果要做通知，只做低噪声状态转换通知，例如：

```text
Running → Stale
NamedPipe → FileFallback
Maintenance → Running
```

不要对每次 2 秒轮询失败弹通知。计划里已经写了“不刷屏”，这是对的。

---

# 建议补强 8：10.1 disabled visual state 很适合作为第一步

阶段 9.6 已经暴露禁用态视觉不明显。把它放在 10.1 很合理。

建议 10.1 不要混托盘逻辑，只修：

```text
SecondaryButtonStyle disabled visual state
```

验收：

```text
Pause / Resume 禁用态肉眼明显
不改变 CanExecute 逻辑
build/test 不回归
```

这是一个很好的“小而稳”的第一批提交。

---

# 推荐补充到计划里的关键约束

你可以在计划中加一段“阶段 10 硬约束”：

```text
阶段 10 硬约束：

1. 托盘不新增状态轮询，必须复用阶段 9 的 status polling 和 AgentStatusSnapshot。
2. CloseToTray / MinimizeToTray 只 Hide 窗口，不停止 status polling，不 Dispose TrayService，不 Shutdown App。
3. 只有 Exit App 才 StopStatusPolling + Dispose TrayService + Shutdown。
4. Exit App 不默认 Stop Agent。
5. TrayService 不直接实现 Agent 控制逻辑，只调用现有 command / service / command bridge。
6. 托盘菜单状态必须来源于 AgentCommandAvailability，不允许单独写一套规则。
7. 阶段 10 不改变页面自动刷新语义，隐藏窗口后 page refresh 是否暂停留到后续优化。
8. 托盘通知第一版低噪声；允许只更新 tooltip，不强制 toast。
```

---

# 阶段 10 执行顺序建议

你现在的拆分是合理的，我建议保持：

```text
10.1 状态可见性与托盘前置整理
10.2 TrayService 基础设施与托盘图标
10.3 CloseToTray / MinimizeToTray / Show / Exit 语义
10.4 托盘状态展示与菜单可用性绑定
10.5 托盘基础控制命令 Start / Pause / Resume / Stop
10.6 托盘异常提示与状态恢复验收
10.7 手动验收、长跑验证与收口
```

不要把 10.2、10.3、10.4 合并成一个大提交。托盘生命周期问题比较容易出错，必须分阶段做。

---

# 阶段 10 重点验收项

我建议最终验收时重点盯这些：

```text
1. App 启动后托盘图标出现。
2. 关闭主窗口后 App 仍在，Agent 不停止。
3. 隐藏后 status polling 继续运行。
4. 托盘 Show 能恢复并激活窗口。
5. Exit App 后 WPF 退出，Agent 不被停止。
6. Exit App 后托盘图标不残留。
7. 托盘 tooltip 与 MainWindow 状态一致。
8. 托盘菜单可用性与 AgentCommandAvailability 一致。
9. Maintenance 中托盘控制命令禁用。
10. Stale + IsRunning=false 时托盘 Start 可用。
11. Stale + IsRunning=true 时托盘 Start 不可用、Stop 可用。
12. 托盘 Pause / Resume / Stop 走现有 IPC-first/fallback 链路。
13. Settings dirty 时托盘状态更新不覆盖编辑草稿。
```

---

# 最终审核结论

这份阶段 10 计划**可以执行**。

它的方向正确，边界也比较稳。建议正式开始前补强三点：

```text
1. 明确托盘依赖方案，MVP 推荐 System.Windows.Forms.NotifyIcon。
2. 明确 CloseToTray / MinimizeToTray 只是 Hide，不停止 status polling。
3. 明确托盘不新增状态轮询、不重写控制逻辑，只复用阶段 9 状态快照和现有命令服务。
```

补完后就可以从：

```text
阶段 10.1：状态可见性与托盘前置整理
```

开始执行。
