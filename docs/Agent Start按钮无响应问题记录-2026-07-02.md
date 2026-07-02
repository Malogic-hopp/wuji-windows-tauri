# Agent Start 按钮无响应问题记录（2026-07-02）

## 背景

阶段 9.3 引入 `AgentCommandAvailability` 后，MainWindow 的 `Start / Stop / Pause / Resume` 按钮可用性不再只看 `_isMaintenance`，而是统一基于 `AgentStatusSnapshot` 计算。

阶段 9.3 当时为 Stale 制定的 MVP 策略是：

```text
Stale:
    Stop 可用
    Start / Pause / Resume / ReloadConfig / PruneData / ClearHistory 不可用
```

这个策略适用于“Agent 进程仍在，但心跳过期”的场景，避免重复启动第二个 Agent。

## 现象

用户反馈：

```text
App 中点击 Start Agent 没有反应。
之前点击 Start Agent 会弹出 Agent 终端窗口。
本次不弹出窗口。
```

用户随后补充了操作顺序：

```text
上次退出时，先关闭了 App，然后才关闭 Agent。
```

并检查到文件状态仍残留为 Running：

```json
{
  "actualState": "Running",
  "isHealthy": true,
  "lastHeartbeatUtc": "2026-07-02T04:58:36.9028041Z",
  "message": "Heartbeat"
}
```

`runtime_state.json` 中也仍是：

```json
{
  "state": "Running"
}
```

## 分析

`runtime_state.json` / `health_state.json` 是 Agent 运行时写出的状态文件。若 Agent 被关闭时没有机会写入 `Stopped`，这些文件可能残留为 `Running`。

App 下次启动时，`AgentStatusService` 会读取状态文件，同时检查 Agent 进程是否真实存在。

在本次场景中：

```text
状态文件：Running
进程探测：Agent 进程不存在
```

因此 `AgentStatusService` 会把状态归类为：

```text
ActualState = Stale
IsRunning = false
IsStale = true
```

也就是说，`Stale` 在这里不是“进程仍在但心跳过期”，而是“状态文件陈旧，进程已经不在”。

阶段 9.3 原规则把所有 `Stale` 都视为不能 Start：

```text
CanStart = ActualState is NotRunning or Stopped
```

所以即使进程已经不存在，只要陈旧状态被归为 `Stale`，Start 按钮也会不可用。用户看到的表现就是“点击 Start Agent 没有反应 / 不弹出 Agent 终端窗口”。

## 根因

根因不是 Agent 仍在运行，而是 UI 按钮可用性规则把两种不同的 `Stale` 场景混在了一起：

```text
1. 进程仍在，心跳过期：
   IsRunning = true
   ActualState = Stale
   应避免 Start，允许 Stop。

2. 进程已退出，状态文件残留 Running：
   IsRunning = false
   ActualState = Stale
   应允许 Start。
```

阶段 9.3 的规则只看 `ActualState`，没有优先使用更可靠的进程探测结果 `IsRunning`。

## 修复

修复位置：

```text
src\QuantifiedSelf.Windows.App\Services\AgentCommandAvailability.cs
```

修复前：

```csharp
CanStart = (state is AgentActualState.NotRunning or AgentActualState.Stopped) && !isMaintenance
```

修复后：

```csharp
CanStart = !status.IsRunning && !isMaintenance
```

含义：

```text
只要进程探测确认 Agent 没有运行，且不在 Maintenance，就允许 Start。
```

这样可以覆盖：

```text
NotRunning
Stopped
Stale + IsRunning=false
状态文件残留 Running 但进程已经退出
```

同时仍然避免：

```text
Stale + IsRunning=true 时重复 Start
Running / Paused / Maintenance 时重复 Start
```

新增测试：

```text
AgentCommandAvailability_AllowsStartWhenStaleButProcessGone
```

验证：

```text
ActualState = Stale
IsStale = true
IsRunning = false

=> CanStart = true
=> CanReloadConfigNow = false
```

原有测试 `AgentCommandAvailability_HandlesStaleConservatively` 仍覆盖：

```text
ActualState = Stale
IsStale = true
IsRunning = true

=> CanStart = false
=> CanStop = true
```

## 审核结论

这次修复是合理的。

`IsRunning` 来自进程探测，比陈旧的 `runtime_state.json` / `health_state.json` 更适合作为 Start 按钮的第一判断依据。

修复后的规则更精确：

```text
进程还在：不允许 Start，避免重复启动。
进程不在：允许 Start，即使状态文件残留 Running / Stale。
```

## 风险与后续观察

当前修复依赖 `AgentStatusService` 的进程探测准确性。如果未来支持多实例、服务模式或不同启动路径，需要继续确认 `IsRunning` 是否能稳定代表“当前用户 Agent 已运行”。

可考虑后续补充更完整的按钮可用性测试矩阵：

```text
Starting + IsRunning=true
Stopping + IsRunning=true
Error + IsRunning=false
Maintenance + IsRunning=false
Stale + IsRunning=false
Stale + IsRunning=true
```

但对当前 MVP 来说，本次修复已经覆盖用户遇到的实际问题。

## 手动恢复建议

如果再次遇到类似问题，可先检查：

```powershell
Get-Process | Where-Object { $_.ProcessName -like '*QuantifiedSelf.Windows.Agent*' }
```

如果进程不存在但状态文件仍显示 Running，App 应在下一次状态刷新后把状态识别为 `Stale + IsRunning=false`，此时 Start Agent 应可用。
