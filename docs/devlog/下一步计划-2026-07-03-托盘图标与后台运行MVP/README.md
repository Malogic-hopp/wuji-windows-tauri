# 托盘图标与后台运行 MVP 阶段拆分

本文档夹由主计划拆分而来：

```text
docs/下一步计划-2026-07-03-托盘图标与后台运行MVP.md
```

## 阶段列表

```text
01-阶段10.1-状态可见性与托盘前置整理.md
02-阶段10.2-TrayService基础设施与托盘图标.md
03-阶段10.3-CloseToTray与后台运行语义.md
04-阶段10.4-托盘状态展示与菜单可用性绑定.md
05-阶段10.5-托盘基础控制命令.md
06-阶段10.6-托盘异常提示与状态恢复验收.md
07-阶段10.7-手动验收长跑验证与收口.md
```

## 阶段 10 总目标

```text
让 WPF App 支持关闭/最小化到系统托盘，托盘能显示 Agent 当前状态，并能通过托盘执行 Show / Exit / Start / Pause / Resume / Stop 等基础操作。
```

## 硬约束

```text
1. 托盘不新增状态轮询，必须复用阶段 9 的 status polling 和 AgentStatusSnapshot。
2. CloseToTray / MinimizeToTray 只 Hide 窗口，不停止 status polling，不 Dispose TrayService，不 Shutdown App。
3. 只有 Exit App 才 StopStatusPolling + Dispose TrayService + Shutdown。
4. Exit App 不默认 Stop Agent，也不得调用 StopAgentCommand / StopAgentGracefullyAsync / Pause / ClearHistory 或任何 Agent 命令。
5. TrayService 不直接实现 Agent 控制逻辑，只调用现有 command / service / command bridge。
6. 托盘菜单状态必须来源于 AgentCommandAvailability，不允许单独写一套规则。
7. 阶段 10 不改变页面自动刷新语义，隐藏窗口后是否暂停 page refresh 留到后续优化。
8. 托盘通知第一版低噪声；允许只更新 tooltip，不强制 toast。
9. NotifyIcon Dispose 前先设置 Visible=false，Dispose 必须幂等。
10. 如果暂无正式 .ico 资源，优先复用现有 App 图标；没有则使用简单 fallback，不因图标资产阻塞托盘生命周期。
11. TrayService 回调触碰 WPF Window/ViewModel 时必须经 Dispatcher；如果 Dispatcher 已 shutdown 或窗口已释放，应安全忽略。
12. Show Main Window 只做 Show + WindowState=Normal + Activate；不引入复杂 Win32 强制前台 hack。
13. CloseToTray / MinimizeToTray 后 status polling 必须继续，page refresh 语义保持阶段 9 现状。
```

## 不做范围

```text
开机自启
安装包
Windows Service
真正 Named Pipe 状态流 / subscribe 协议
托盘复杂通知中心
托盘直接执行 PruneData / ClearHistory
托盘显示完整 Diagnostics
Exit App 时自动 Stop Agent 的组合操作
```
