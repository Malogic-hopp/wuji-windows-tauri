# Agent 状态刷新 / RefreshService 优化 MVP 阶段拆分

本文档夹由主计划拆分而来：

```text
docs/下一步计划-2026-07-01-Agent状态流订阅RefreshService优化MVP.md
```

虽然文件夹名沿用“状态流订阅”，但阶段 9 MVP 的执行范围已经在 review 后降调为：

```text
先做集中状态轮询，不实现长连接推送式状态流；
真正 Named Pipe 状态流 / 事件订阅作为后续增强。
```

## 阶段列表

```text
01-阶段9.1-RefreshService现状梳理与统一接口.md
02-阶段9.2-AgentStatus集中轮询GetStatus.md
03-阶段9.3-ViewModel状态派发与按钮可用性统一.md
04-阶段9.4-当前页数据刷新与Agent状态刷新解耦.md
05-阶段9.5-Diagnostics刷新健康状态展示.md
06-阶段9.6-断连重连Agent重启场景验收.md
07-阶段9.7-长跑验证与收口.md
```

## 阶段 9 总目标

```text
把 Agent 状态刷新从页面数据刷新中解耦出来，形成统一、可取消、可观测、不会覆盖未保存编辑的 RefreshService。
```

## Review 后硬约束

```text
1. RefreshService 只返回 RefreshResult，不直接写 ViewModel 绑定属性。
2. MainWindowViewModel 负责把 RefreshResult 应用到 UI 属性，并触发 NotifyCanExecuteChanged。
3. 状态刷新和页面刷新使用独立防重入 / 取消机制。
4. RefreshResult 必须带 RefreshSequence / StartedAtUtc / CompletedAtUtc。
5. 状态 polling 默认 2 秒；1 秒不作为 MVP 默认值。
6. Settings IsDirty 只阻止配置表单重新加载，不阻止 Agent 状态、IPC 状态、RefreshHealth 更新。
7. MainWindowViewModel 和 SettingsViewModel 应共用同一个 AgentStatusSnapshot 更新按钮可用性。
8. Refresh loop 健康状态第一版只保存在 App 内存，不写 agent_events。
9. 手动 Refresh = 立即刷新 Agent 状态 + 当前页数据。
10. 阶段 9.1 不引入新的 status polling timer，状态 polling 放到阶段 9.2。
```
