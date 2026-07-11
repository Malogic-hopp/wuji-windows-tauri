# 下一步计划：PruneData / ClearHistory 数据清理 MVP

这是一份把 `docs/下一步计划-2026-06-24-PruneDataClearHistory数据清理MVP.md` 拆成可独立实施、可独立验收的阶段版计划。

本阶段承接阶段 6「Settings 与配置应用 MVP」。阶段 6 已完成配置查看、编辑、校验、保存、Restore Backup、ReloadConfig 和隐私规则真实生效验收；阶段 7 开始进入真实数据清理能力。

## 总目标

让用户能通过 Agent 安全清理本地历史数据，并且清理过程可观测、可诊断、失败后不破坏运行状态。

阶段完成后，WUJI 应具备：

```text
Maintenance 状态
PruneData 过期数据清理
ClearHistory 清空历史数据
清理事件可诊断
Settings 数据管理入口
清理失败可恢复、可观测
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
```

阶段 6 验证结果：

```text
dotnet build QuantifiedSelf.Windows.sln --no-restore
    通过，0 warnings / 0 errors

dotnet test QuantifiedSelf.Windows.sln --no-restore
    通过，103/103
```

## 拆分原则

1. WPF 不直接删除 SQLite / JSONL。
2. 数据清理必须由 Agent 执行。
3. 先做 Maintenance 状态骨架，再做真实删除。
4. 先做低风险 PruneData，再做高风险 ClearHistory。
5. SQLite 删除使用 UTC cutoff。
6. JSONL 删除使用本地文件日期 cutoff。
7. SQLite 多表删除必须使用事务。
8. ClearHistory MVP 第一版不删除当天 JSONL。
9. ClearHistory 必须二次确认。
10. ClearHistory 后 Agent 保持 Paused。
11. 清理事件 payload 只记录数量、cutoff、状态，不记录完整路径或敏感内容。
12. JSONL 删除失败不做复杂补偿，但必须写安全错误。

## 阶段目录

- [阶段 7.1：数据清理命令与 Maintenance 状态骨架](./01-阶段7.1-数据清理命令与Maintenance状态骨架.md)
- [阶段 7.2：PruneData 数据层服务](./02-阶段7.2-PruneData数据层服务.md)
- [阶段 7.3：PruneData Agent 集成与 Diagnostics](./03-阶段7.3-PruneData-Agent集成与Diagnostics.md)
- [阶段 7.4：ClearHistory 数据层服务](./04-阶段7.4-ClearHistory数据层服务.md)
- [阶段 7.5：ClearHistory Agent 集成与二次确认 UI](./05-阶段7.5-ClearHistory-Agent集成与二次确认UI.md)
- [阶段 7.6：Settings 数据管理入口与刷新联动](./06-阶段7.6-Settings数据管理入口与刷新联动.md)
- [阶段 7.7：验收、长跑验证与收口](./07-阶段7.7-验收长跑验证与收口.md)

## 本阶段不做

- Named Pipe / gRPC 主控制通道
- Agent 状态流订阅
- 托盘 TrayService
- 开机自启
- 安装包
- 7 天趋势和复杂图表
- CSV / Excel 导出
- 按表选择清理
- 按应用选择清理
- 按日期范围手动清理
- 回收站式恢复
- 数据库压缩 / VACUUM
- 复杂进度条和取消清理

## 第一批提交要求

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

