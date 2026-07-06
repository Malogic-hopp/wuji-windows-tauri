# 个人洞察与统计分析 MVP 阶段拆分

本文档夹由主计划拆分而来：

```text
docs/下一步计划-2026-07-07-个人洞察与统计分析MVP.md
```

阶段 13 的执行范围：

```text
基于已有 foreground_samples / app_sessions 数据，
先做本地日统计、今日洞察、7 天趋势和规则建议，
让 WUJI 开始提供可理解、可行动的个人反馈。
```

## 阶段列表

```text
01-阶段13.1-DailyStatsService与今日聚合.md
02-阶段13.2-DashboardTodayInsight改造.md
03-阶段13.3-FocusSession与ContextSwitch指标.md
04-阶段13.4-7天趋势与每日对比.md
05-阶段13.5-InsightSuggestions规则建议.md
06-阶段13.6-洞察明细与可解释性.md
07-阶段13.7-手动验收长跑验证与收口.md
```

## 阶段 13 总目标

```text
把 WUJI 从稳定采集和控制工具，推进为能解释“时间花到哪里、注意力如何变化、今天该如何调整”的个人洞察工具。
```

## 硬约束

```text
1. 不改 Agent 采样职责。
2. 不破坏 SQLite 单写入者原则。
3. 不改 Named Pipe IPC 协议。
4. 不改托盘、开机自启、发布脚本主流程。
5. 不引入云同步、联网分析或 AI 大模型建议。
6. 不先做复杂图表系统。
7. 所有洞察必须能解释来源和规则。
8. 建议文案必须温和、克制、可执行。
9. 统计查询不得阻塞 2 秒 status polling。
10. 页面刷新仍遵守阶段 9 的 status/page refresh 解耦语义。
```
