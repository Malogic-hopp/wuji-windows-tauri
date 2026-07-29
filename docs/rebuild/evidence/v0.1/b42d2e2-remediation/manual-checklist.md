# v0.1 手工与长时间门禁清单

生成：2026-07-22（2026-07-22 更新：8 小时 soak 已通过）。以下门禁只能人工或长时间执行。**全部完成前，不得声称 Rebuild v0.1（V01-8）验收通过。**

| 门禁（09 §12.2 及审核补充） | 状态 | 说明 |
|---|---|---|
| 真实锁屏 / 休眠-恢复采集行为 | Pending | R03 已接入 WTS/PBT 事件泵并有状态机级测试；真实硬件行为仍需人工核对 Today/Timeline |
| Today/Timeline 与受控 30 分钟脚本记录一致 | Pending | 人工对照 |
| 960×640 与 1280×800 布局 | Pending | 截图归档 |
| 100% / 150% / 200% DPI | Pending | 截图归档 |
| Light / Dark / Windows High Contrast 主题 | Pending | R10 已加 forced-colors 适配；需实机核对边界/焦点/徽章 |
| 键盘导航与焦点可见 | Pending | |
| 屏幕阅读器输出 | Pending | R10 已修 Timeline 切换间隔 aria-hidden；需实机核对 |
| Agent 离线时读取已有历史并显示安全状态 | Pending（自动已覆盖一半） | 自动：`agent_survives_parent_exit_and_offline_read_works_after_kill`、`query_service_reads_seeded_database`；UI 显示状态需人工核对 |
| 8 小时 soak（整改后脚本） | Done（2026-07-22） | verdict=pass：28801 秒、480 采样、优雅退出 exit code 0、RSS 15.3→17.9MB 有界、WAL 峰值 1.1MB、7764 条 Observation、dropped=0、quick_check ok、旧库 verified_stable；证据 `soak-summary.json` |
| disk-full 故障注入 | Pending（手工） | 自动门禁已覆盖 busy/corruption/checkpoint；disk-full 需手工制造（如配额/满载卷），预期行为：writer faulted、停止采集、IPC 在线、不自动修复 |

## 关闭规则

- 8 小时 soak 完成后，将新报告复制为 `soak-summary.json` 并更新 `manifest.json` 的 `pending` 段与 `migration-status.md` §6。
- 手工项由执行人逐项打勾并签名/日期后，`migration-status.md` 才能从“实现完成”改为“验收完成”。
