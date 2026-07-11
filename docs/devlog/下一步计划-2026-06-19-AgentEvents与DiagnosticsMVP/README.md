# 下一步计划：Agent Events 与 Diagnostics MVP

这是一份把原始长计划拆成可独立实施、可独立验收的阶段版计划。

## 总目标

让 WUJI 不只“能采集”，还要“能解释自己为什么这样运行”。

## 拆分原则

1. 先补事件基础设施，再接入写入链路。
2. 先让 Diagnostics 能查到历史，再做 UI 增强。
3. 事件写入必须 best-effort，不能影响采样主链路。
4. 第一版只读 SQLite，不做 JSONL 浏览器。
5. 隐私与敏感信息规则优先于可观测性。

## 阶段目录

- [阶段 1：事件基础设施](./01-阶段1-事件基础设施.md)
- [阶段 2：事件写入链路](./02-阶段2-事件写入链路.md)
- [阶段 3：隐私、Session 与 Diagnostics 增强](./03-阶段3-隐私、Session与Diagnostics增强.md)
- [阶段 4：验收、稳定化与收口](./04-阶段4-验收、稳定化与收口.md)

## 暂缓事项

本阶段不优先做：

- Named Pipe / gRPC
- 托盘 TrayService
- 安装包
- 开机自启
- 完整 Settings 编辑页
- 完整 SamplesView / AppsView / SessionsView
- 复杂图表
- JSONL 浏览器

