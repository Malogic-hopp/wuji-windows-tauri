# 下一步计划：Named Pipe 控制通道 MVP

这是一份把 `docs/下一步计划-2026-07-01-NamedPipe控制通道MVP.md` 拆成可独立实施、可独立验收的阶段版计划。

本阶段承接阶段 7「PruneData / ClearHistory 数据清理 MVP」。阶段 7 已完成数据清理命令、Maintenance 状态、PruneData、ClearHistory、Settings 数据管理入口、手动验收和长跑验证。

## 总目标

把当前 `agent_control.json` 文件控制主通道升级为：

```text
Named Pipe 请求响应主通道 + agent_control.json fallback
```

阶段完成后，WUJI 应具备：

```text
IPC Ping / GetStatus
IPC 控制 Pause / Resume / Stop / ReloadConfig
IPC 控制 PruneData / ClearHistory
IPC timeout / fallback
Diagnostics IPC 状态展示
当前用户 Named Pipe 安全边界
```

## 当前基础

当前 WUJI 已具备：

```text
真实 Win32 前台窗口采样
foreground_samples / app_sessions / agent_events 落库
agent_events_YYYYMMDD.jsonl 审计日志
Pause / Resume / Stop / ReloadConfig 控制命令
Dashboard / Apps / Sessions / Samples / Diagnostics / Settings
Agent Options 校验、保存、ReloadConfig 和隐私规则生效闭环
PruneData / ClearHistory 数据清理闭环
Maintenance 状态和清理事件
```

阶段 7 验证结果：

```text
dotnet build QuantifiedSelf.Windows.sln --no-restore
    通过，0 warnings / 0 errors

dotnet test QuantifiedSelf.Windows.sln --no-restore
    通过，142/142

手动验收与长跑验证通过
```

## 拆分原则

1. 协议先行，server/client 后接。
2. Ping / GetStatus 先行，控制命令后接。
3. Pause / Resume / Stop / ReloadConfig 先迁移，PruneData / ClearHistory 后迁移。
4. `agent_control.json` 永远保留 fallback。
5. IPC 失败必须安全超时，不能卡 UI。
6. Diagnostics 要能说明当前用的是 IPC 还是 fallback。
7. Pipe name 和错误信息必须脱敏。
8. NamedPipeProtocol 必须限制 `MaxPayloadBytes = 16 KB`。
9. timeout 区分 `ConnectTimeout`、`RequestTimeout` 和 `MaintenanceCommandTimeout`。
10. requestId 防重先做轻量内存缓存，不做磁盘级命令队列。
11. Diagnostics IPC 状态第一版只保存本次 App 会话内存状态。
12. AgentCommandServer 异常不能拖垮采样循环。

## 阶段目录

- [阶段 8.1：IPC 协议与 Named Pipe 基础设施](./01-阶段8.1-IPC协议与NamedPipe基础设施.md)
- [阶段 8.2：AgentCommandServer 与 Ping / GetStatus](./02-阶段8.2-AgentCommandServer与PingGetStatus.md)
- [阶段 8.3：WPF AgentControlClient 与 fallback 策略](./03-阶段8.3-WPF-AgentControlClient与Fallback策略.md)
- [阶段 8.4：基础控制命令迁移到 IPC](./04-阶段8.4-基础控制命令迁移到IPC.md)
- [阶段 8.5：维护命令迁移到 IPC](./05-阶段8.5-维护命令迁移到IPC.md)
- [阶段 8.6：Diagnostics IPC 状态展示](./06-阶段8.6-Diagnostics-IPC状态展示.md)
- [阶段 8.7：验收、断连测试与收口](./07-阶段8.7-验收断连测试与收口.md)

## 本阶段不做

- gRPC over Named Pipes
- Agent 状态流订阅
- 托盘图标
- 开机自启
- 安装包
- Windows Service
- 多用户控制
- 远程控制
- 复杂权限管理 UI
- 维护任务取消
- PruneData / ClearHistory 进度条
- 命令队列持久化

## 第一批提交要求

第一批提交必须很小：

```text
只做 Core/Ipc 协议模型
只做 AgentPipeName
只做 NamedPipeProtocol length-prefixed JSON
只做 Stream / MemoryStream 协议测试
不创建真实 server/client
不接 Agent / WPF
```

建议提交信息：

```text
feat(ipc): add named pipe protocol contracts
```
