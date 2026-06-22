# 下一步计划：Samples / Sessions / Apps 数据浏览 MVP

这是一份把 `docs/下一步计划-2026-06-22-SamplesSessionsApps数据浏览MVP.md` 拆成可独立实施、可独立验收的阶段版计划。

## 总目标

让 WUJI 不只“能采集、能诊断”，还要能查看、筛选和解释历史使用明细。

## 当前基础

当前 WUJI 已具备：

```text
真实 Win32 前台采样
idle / active 判断
采集阶段隐私过滤
foreground_samples 落库
app_sessions 合并
Pause / Resume / Stop 控制
Dashboard 基础统计
Diagnostics 最近事件 / 最近错误
agent_events SQLite 查询索引
agent_events_YYYYMMDD.jsonl 审计日志
事件限流、payload 白名单和路径脱敏
```

这意味着当前阶段已经从：

```text
可诊断 MVP
```

进入：

```text
数据浏览 MVP
```

## 拆分原则

1. 先补只读查询服务，再做页面。
2. 开发顺序按数据层到 UI 层推进：Samples -> Sessions -> Apps。
3. 导航顺序按用户视角组织：Dashboard -> Apps -> Sessions -> Samples -> Diagnostics -> Settings。
4. WPF App 只读 SQLite，Agent 仍然是唯一写入者。
5. 所有列表查询必须有 LIMIT，默认不加载全量历史。
6. 新页面至少拆成 UserControl，优先独立 View + ViewModel。
7. 隐私规则优先于可观测性，SamplesView 默认不展示完整 `executable_path`。

## 阶段目录

- [阶段 5.1：查询服务补齐](./01-阶段5.1-查询服务补齐.md)
- [阶段 5.2：SamplesView MVP](./02-阶段5.2-SamplesViewMVP.md)
- [阶段 5.3：SessionsView MVP](./03-阶段5.3-SessionsViewMVP.md)
- [阶段 5.4：AppsView MVP](./04-阶段5.4-AppsViewMVP.md)
- [阶段 5.5：导航与页面收口](./05-阶段5.5-导航与页面收口.md)
- [阶段 5.6：验收、稳定化与收口](./06-阶段5.6-验收稳定化与收口.md)

## 暂缓事项

本阶段不优先做：

- Named Pipe / gRPC
- 托盘 TrayService
- 安装包
- 开机自启
- Windows Service
- 完整 Settings 编辑页
- PruneData / ClearHistory
- 复杂图表
- 7 天趋势
- CSV / Excel 导出
- 应用分类编辑
- 浏览器网页级识别
- JSONL 浏览器
- 全文搜索或复杂查询语法

