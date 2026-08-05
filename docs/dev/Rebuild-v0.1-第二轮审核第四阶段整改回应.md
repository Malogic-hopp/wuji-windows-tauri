# Rebuild v0.1 第二轮审核第四阶段整改回应

日期：2026-08-04（阶段 4.7 收口时形成）
依据：第四阶段代码审核报告（`下一步计划-2026-07-23-Rebuild-v0.1第二轮审核第四阶段整改/00-第四阶段代码审核报告.md`）
状态：第四阶段整改完成，等待复审

本文档逐项映射第四阶段审核的 P1-01~P1-05 与 P2-01~P2-04 到当前代码与测试。
代码证据以提交 `6d695ff`（阶段 4.1–4.5 实现）与后续复审提交、以及 4.6/4.7
完成说明为基线；未覆盖当前代码的历史 evidence 一律不据此判定（见 migration-status）。

## P1-01：Settings last-known-good 丢失时静默回退 revision 0

- 代码：`apps/agent/src/settings_recovery.rs`（三输入交叉对账）、`settings_backup.rs`
  （DB 感知双槽原子备份）、`settings_persist.rs`（crash-consistent 持久化协议）；
  `settings_revisions` 只保留元数据（revision/content_digest/applied_at_utc_ms），
  完整内容存双槽备份文件（09 §12.3 隐私一票否决，不入 SQLite）。
- 测试：`settings_recovery.rs`（双槽候选、缺槽修复、三输入验证）；09 §16.1 已回写。
- 关闭：✅（阶段 4.1 复审通过）

## P1-02：CaptureCoordinator 未进入生产接线，且自身没有执行冻结/恢复

- 代码：`apps/agent/src/capture_coordinator.rs`（唯一 transition lock、冻结/恢复、
  effective gate）；`control_plane.rs::assemble` 生产接线消除旁路通道。
- 测试：`capture_coordinator.rs`、`settings_effectivity.rs`（真实 wiring）、
  `agent_e2e.rs`（真实进程）。
- 关闭：✅（阶段 4.3/4.3.1 复审通过）

## P1-03：Barrier 可被吞掉、静默丢失或形成幽灵 pending

- 代码：`barrier.rs`（BarrierRequest = BarrierId + injected ack，可注入/超时/关闭语义）；
  Writer `PendingBarriers`（容量/TTL/三要素匹配），普通 data 分支不再吞 Barrier；
  Processor revision 错配发违例后显式退出。
- 测试：`barrier_reliability.rs`（Barrier 先于 control、pending TTL/overflow/冲突、
  FIFO 满载仍可达、真实 Capture Loop 混合流）；真实拓扑映射见 4.6 完成说明。
- 关闭：✅（阶段 4.2 复审通过）

## P1-04：Settings effectivity 仍不成立

- 代码：Coordinator 唯一串行化 capture/settings/系统事件；effectivity 全链路
  revision 防线（Coordinator/Processor/Writer 三处错配显式拒绝）；lifecycle
  expected_revision 来自当前 applied revision，不硬编码。
- 测试：`settings_effectivity.rs` 23 项（真实 wiring：mismatch 拒绝、drain 后
  control 拒绝、fault 后 fencing、late control 立即 fenced）。
- 关闭：✅（阶段 4.4 复审通过）

## P1-05：S2-03 Lock/Sleep 尚未完成

- 代码：`capture_coordinator.rs`（`SystemLifecycleEvent` 四态输入、per-source
  `LockSleepState`、`lifecycle_monitor_fault` 独立 suppression）、
  `session_power_events.rs`（可测试单一 consumer）、`wuji-windows/session_power.rs`
  （不显示顶层窗口 + WTS + WM_POWERBROADCAST，WM_NCCREATE 上下文安装）。
- 测试：`lock_sleep_lifecycle.rs` L01–L20（真实 wiring）+ 真实进程 agent_e2e；
  **真实锁屏→恢复、睡眠→唤醒人工验收通过（2026-08-04）**。
- 关闭：✅（阶段 4.5 复审通过 + 人工验证；S2-03 完整关闭随 4.6/4.7 收口）

## P2-01：真实拓扑测试仍不足

- 解决：`lock_sleep_lifecycle.rs` / `settings_effectivity.rs` 的 `wiring()` /
  `fault_wiring()` 走 `control_plane::assemble` 生产组装（完整链含 Coordinator）；
  `barrier_reliability.rs` 真实 Capture Loop→Processor→Writer 链路段。
  10 类核心失败场景映射与单元级/豁免标注见 4.6 完成说明 §3/§9。
- 关闭：✅（4.6 收口；场景 8 pending TTL/overflow/冲突获用户书面豁免）

## P2-02：完整 Rust 全量测试仍有 flaky

- 解决：`TestAgentGuard`（`apps/agent/tests/common/mod.rs`）——进程身份 = 创建后
  立即 `DuplicateHandle` 复制的句柄，绝不 PID 重开；`launch_via_exiting_parent_tracked`
  独立 launcher + 跨进程句柄交付；唯一 `rebuild-v01-test-{ULID}` channel 隔离。
- 验证：`agent_survives_parent_exit_and_offline_read_works_after_kill` 三次全量
  411/411 均 ok。
- 关闭：✅（4.6 收口）

## P2-03：soak 脚本仍可能产生优雅退出假阳性

- 代码：`scripts/soak.py`——`ipc_graceful_shutdown` 返回结构化 `(hello_ok,
  shutdown_ok, will_exit, note)`；退出判定提取为纯函数 `controlled_exit_failures`
  （shutdown_attempted/hello_ok/shutdown_ok/will_exit/exit_code/forced_kill/
  agent_exited_early）；**任一受控退出条件失败都进入 failures，提前 exit 0 也失败**；
  报告 `gracefulShutdown` 显式记录 7 项。
- 测试：`scripts/tests/test_soak_verdict.py`（unittest，9 项：成功/IPC 失败/
  未尝试/超时强杀/提前 exit 0）。
- 关闭：✅（本阶段 4.7）

## P2-04：权威文档与当前代码互相冲突

- 解决：09 §16.1（Settings LKG：`content_json` → DB metadata + 双槽完整内容）、
  §16.2（sequence watermark → BarrierId + injected ack + Coordinator）、§16.6
  （Schema 增补）回写为当前实现；migration-status 同步真实实现/日期/测试数；
  历史 evidence 保留但明确不覆盖当前代码。
- 关闭：✅（本阶段 4.7）

## 最终自动门禁（4.7）

- `cargo fmt/check/clippy -D warnings`、Agent 专项测试、agent_e2e 串行、
  `cargo test --workspace` 连续两次、React typecheck/lint/test/build、
  `git diff --check`——见 `阶段4.7-完成说明-2026-08-04.md` 验证表。
- NotRun/Pending：正式 package、8 小时 soak、真实 Lock/Sleep（已人工验证，
  4.7 不重复）、UI 人工门禁。

## 结论

第四阶段整改完成，等待复审。不宣称 S2-03/S2-04 最终关闭或 Rebuild v0.1 验收完成。
