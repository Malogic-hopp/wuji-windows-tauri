# Rebuild v0.1 审核报告整改回应

回应对象：[Rebuild-v0.1-代码与验收审核报告.md](./Rebuild-v0.1-代码与验收审核报告.md)（下称"审核"）
整改日期：2026-07-22
代码状态：HEAD `b42d2e2` + 整改工作区（按用户要求未提交；本文件所列证据对应该状态）
权威顺序：AGENTS.md > [09 实施基线](./09-Tauri-Rust-Rebuild-v0.1实施基线.md)（含 §16 增补）> 审核报告 > [migration-status](./migration-status.md)
证据包：[evidence/v0.1/b42d2e2-remediation/](./evidence/v0.1/b42d2e2-remediation/)

## 1. 总体结论

审核 R01–R10 全部完成整改，每项行为修复均带"修复前可失败、修复后通过"的回归测试。整改过程中另发现并修复一个审核未列出的 High 级进程故障（事件泵冻结 Agent runtime）和三处脚本/判定缺陷（见 §5）。

当前状态：**V01-1–V01-7 实现完成且自动验证通过；V01-8 自动门禁（package + 8 小时 soak）已通过；09 §12.2 手工门禁与 disk-full 手工注入仍 Pending。** 按审核 §10，在手工项关闭前只宣称实现完成与自动门禁通过，不宣称 v0.1 全部验收通过。

## 2. 逐项整改（R02–R10）

### R02：Today Top 20 少算、drop event_count 少算（High）

- 修改：`rebuild/crates/wuji-storage/src/reader.rs` — `activeDurationMs` 改为对 `daily_app_usage` 全量 `SUM(active_duration_ms)`（不再被 Top 20 的 LIMIT 截断）；`dropped_count_of_date` 改为 `SUM(event_count)`。
- 回归测试（`crates/wuji-storage/tests/storage.rs`）：
  - `today_active_total_is_not_truncated_by_top20`（21 个应用，总量不被截断）；
  - `today_dropped_count_sums_merged_gap_event_count`（合并 gap 的 event_count=4 全计入）;
  - `today_cross_midnight_splits_by_local_date`（跨午夜按 local date 拆分且总量守恒）。

### R03：Pause/Stop watermark、真实 Sleep/Lock（High）

- 修改：
  - `apps/agent/src/capture_loop.rs`：`ContinuityState` 增加 `latest_sequence` 原子水位。
  - `apps/agent/src/command_server.rs`：capture 命令经 `capture_lock` 串行化；接受 Pause/Stop 时先冻结 capture watch 再取 watermark，随 Lifecycle 进 control lane。
  - `apps/agent/src/writer_task.rs`：Lifecycle 携带 watermark；Writer 先排空至 watermark（seq ≤ watermark 按边界前提交，迟到样本按 09 §6.7 作为边界后首条 Observation 关闭 gap；处理侧失联最多等 1.5 秒记诊断放行）。
  - 新文件 `crates/wuji-windows/src/session_power.rs`：message-only 隐藏窗口泵，WTS_SESSION_LOCK/UNLOCK、PBT_APMSUSPEND/RESUME 转发；`main.rs` 接线 Sleep→SystemSleep、Lock→SessionLocked（Resume/Unlock 不成边界）。
- 回归测试（`apps/agent/tests/writer_watermark.rs`）：`full_queue_pause_drains_backlog_before_boundary`、`straggler_after_watermark_is_post_boundary_observation`、`sleep_and_lock_events_close_rows_with_matching_kinds`。
- **附带发现的进程级故障**：事件泵最初在 tokio 任务里直接调用 `std::sync::mpsc::Receiver::recv`，在 current_thread runtime 上冻结整个 Agent（pipe 接受连接但不应答，e2e 全挂）。已改为专用桥接线程转发进 tokio 通道（`main.rs`），并被 `agent_e2e` 全量通过验证。

### R04：Settings last-known-good、revision 单调性、effectivity、自动对账（High）

- 修改：
  - `crates/wuji-storage/schema/schema.sql`：`settings_revisions` 增加 `content_json`（规范 JSON 全文，跨重启 last-known-good）；`writer.rs` 同步持久化并新增 `latest_settings_content()`。
  - `apps/agent/src/settings_store.rs`：新增 `reconcile_startup_settings` 纯函数——文件缺失（新库允许 revision 0；已有 revision 从 DB 恢复并上报）、损坏、降级、同 revision digest 冲突分别按 09 §9.1 拒绝并从 DB 恢复；last-known-good 自身不可恢复时禁止采集。
  - `apps/agent/src/activity.rs`：`apply_settings` 拒绝低于当前内存 revision 的降级。
  - `apps/agent/src/command_server.rs`：`settings_reload` 拒绝低于 `appliedRevision` 的文件；携带 watermark。
  - `apps/agent/src/writer_task.rs`：`SettingsApplied` 先排空至 watermark——backlog 保持旧 revision，新样本用新 revision（"只影响未来数据"的可执行定义）。
  - 新文件 `apps/agent/src/settings_reconciler.rs`：每 2 秒检查文件，revision 严格大于已应用值时经 control lane 自动应用（saved-not-applied 自动重试）。
  - `apps/desktop/src-tauri/src/settings_service.rs`：`load_current` 三态化，损坏文件返回 `SETTINGS_INVALID` 不再伪装默认值；`resync_login_startup` 的 `appliedRevision` 改取数据库 MAX（不再误报 saved revision）。
- 回归测试：`settings_store::tests::*`（8 项对账矩阵）、`apps/agent/tests/settings_lifecycle.rs`（`backlog_before_watermark_keeps_old_revision`、`downgrade_settings_applied_is_rejected`、`reconciler_applies_newer_saved_file`）、`storage.rs::settings_revision_persists_last_known_good_content`、desktop `host_integration` resync 断言。

### R05：IPC timeout 不取消副作用、严格协议（High）

- 修改（`apps/agent/src/command_server.rs`）：
  - dispatch 在独立任务执行；3 秒 timeout 只结束本次等待，不取消已接受命令；request cache 只在任务真正完成后写 Completed；timeout 响应不落 cache；相同 ID 重试等待原任务结果。
  - 严格 `String::from_utf8`（拒绝替换解码，回 `IPC_INVALID_MESSAGE` 后断开）；`requestId` 必须 ULID；`sentAtUtcMs` 必须十进制字符串；hello 校验 `desktopVersion` 非空、envelope 与 payload 的 protocolVersion 均为 1、channel 匹配；逐命令强类型 payload 并 `deny_unknown_fields`（无 payload 命令只接受缺省/null/空对象）。
- 修改（`apps/desktop/src-tauri/src/ipc.rs`）：timeout/断线后保存并用同一 request ID 重连重试一次；`sentAtUtcMs` 改真实时间戳。
- 回归测试（`apps/agent/tests/ipc_protocol.rs`）：`timeout_does_not_cancel_side_effect_and_retry_returns_real_result`、`invalid_request_id_is_rejected`、`non_decimal_sent_at_is_rejected`、`unknown_payload_fields_are_rejected_per_command`、`conflicting_payload_with_same_id_is_rejected`；e2e `agent_rejects_invalid_utf8_and_malformed_hello`。

### R06：package/soak 验收脚本与证据（High）

- `rebuild/scripts/soak.py` 重写：
  - 先 hello 再 `agent_shutdown_dev`，解析两次响应（含 `willExit`）；要求限时 exit code 0，任何强杀判失败；
  - 心跳全程严格单调、writer 任一采样点不得 faulted、WAL 趋势显式判据（结束 ≤4 MiB 且末段均值 ≤ 前段 ×2 + 1 MiB）；
  - prod/dev 两个旧库候选各自记录存在性，缺失报告 `not_verifiable_no_old_db_present`，不声称"两库 checksum 不变"；
  - 输出脱敏可提交证据（git commit、命令、OS、二进制 SHA-256、Cargo.lock SHA-256、采样摘要、判据文本），无用户名与本机绝对路径。
  - 附带修复：runtime 查询补 `runtime_id DESC` 决胜（bootstrap 占位行与当前运行行 `started_at` 相同，曾导致心跳读数抖动、单调性误判失败）。
- `rebuild/scripts/build_dev_package.py`：
  - "安装版 Desktop 启动并拉起安装目录 Agent"并入自动流程（test channel 隔离，校验 DB 创建与 Agent 进程路径）；
  - 旧库记录改 prod/dev 脱敏标签；installer/manifest 输出不打印绝对路径。
  - 该错版校验在整改期间正确拦下了一次过期 installer（R 改动后的 release Agent 与旧包内副本不一致），重打包后通过。
- 验证：42 秒 smoke（pass）+ 完整 package 流程（pass）+ 8 小时正式 soak（pass，见 §4）。

### R07：Int64String 精度与双副本（Medium）

- 修改：`crates/wuji-core/src/bindings.rs` 生成时对 `export type Int64String = string;` 做确定性品牌替换（`string & { readonly __brand: "Int64String" }`），specta 输出漂移即报错；drift 测试同时比对 crate 与 desktop 两份文件；`WUJI_UPDATE_BINDINGS=1` 流程同步写两处。
- 修改：`apps/desktop/src/lib/format.ts` 时长全部 BigInt 运算；时间戳转 Date 前校验 |ms| ≤ 2^53-1（超出显示 `—`）。
- 回归测试：`bindings::tests::int64_fields_export_as_branded_string`、双副本 drift 断言；React 测试夹具全部改用 `i64()` 品牌断言（typecheck 门禁）。

### R08：Timeline 时区（Medium）

- 修改：`TimelinePage.tsx` 首次查询改用 `activity_get_today` 返回的 DB reporting 时区日期，不再用浏览器本地日期。
- 回归测试：`TimelinePage.test.tsx` 断言首次 `activity_get_timeline` 调用的 localDate 来自 today DTO。

### R09：Diagnostics（Medium）

- 修改：
  - `DiagnosticsPage.tsx`：时间基准随每次轮询存入 model（`{dto, atMs}`），相对年龄不再冻结在首次渲染。
  - 队列深度改真实原子表（`capture_loop/processor_task/writer_task` 入出队计数），替换原先回读自身心跳的恒 0 环路。
  - `writer_task.rs`：safe_error_code 随心跳持久化；`reader.rs::status_dto_from_runtime` 透传（`SafeErrorCode::as_str/from_code` 稳定映射）。
- 回归测试：`processor_task::tests::queue_depth_gauges_track_backlog`（含负值钳制）；React 非零队列深度显示与"年龄随轮询更新"两项断言。

### R10：High Contrast 与无障碍（Medium）

- 修改：`design-system/global.css` 增加 `@media (forced-colors: active)` 系统色规则（边界、焦点、徽章、禁用态、链接不依赖颜色）；Timeline 用户勾选显示的切换间隔行去掉 `aria-hidden`，改为可访问名。
- 回归测试：`TimelinePage.test.tsx` 断言显示时无 `aria-hidden` 且有 `aria-label`。
- 手工核对（实机 HC/键盘/读屏）仍 Pending，见 §6。

### R01：migration-status 与文档一致性（High）

- `docs/rebuild/migration-status.md` 重写：撤回"V01-1–V01-8 全部完成"的旧结论；V01 阶段与长期 M0–M11 分表；自动验证标明日期与命令；手工门禁全部 Pending；链接审核报告与证据包；soak/package 行在 2026-07-22 重跑通过后如实更新。
- `09` 新增 §16 审核整改增补合同：last-known-good 恢复、sequence watermark、IPC 副作用不取消、checkpoint busy 结果行判定、soak 可执行判据、`settings_revisions.content_json` Schema 增补；头部状态改为"Draft；实现/验收结论以 migration-status 与证据包为准，正式接受留待产品评审"。
- 新建证据包 `docs/rebuild/evidence/v0.1/b42d2e2-remediation/`：`manifest.json`、`automated-gates.md`（09 §12.1 → 测试/命令逐项映射）、`soak-smoke-summary.json`、`soak-summary.json`（8 小时）、`package-validation.json`、`manual-checklist.md`；全部脱敏校验通过。

## 3. 审核 §7 自动门禁缺口的覆盖

逐项映射与测试名见 [automated-gates.md](./evidence/v0.1/b42d2e2-remediation/automated-gates.md)。摘要：

- busy/corruption/checkpoint 故障注入：`fault_injection.rs` 三项；disk-full 转手工（自动路径同一 mark_fatal 分支）。
- 满队列 control 优先与固定水位：`writer_watermark.rs` 三项。
- DB/WAL/DTO 隐私字节级扫描：`privacy_canary_never_persists_to_db_wal_or_dto`；日志扫描为空集（v0.1 不写日志文件）。
- IPC timeout/非法 UTF-8/重复冲突 ID：`ipc_protocol.rs` 五项 + e2e 两项。
- Settings 删除/损坏/降级/digest 冲突/自动重试：对账矩阵 8 项 + 集成 3 项。
- Desktop/父进程退出 Agent 存活 + 离线历史可读：`agent_survives_parent_exit_and_offline_read_works_after_kill`（进程级）。
- 安装版固定路径端到端启动：已并入 `build_dev_package.py` 自动流程。
- 21+ 应用/跨午夜守恒：R02 三项 storage 测试。

## 4. 验证结果（2026-07-22 实跑）

| 验证项 | 结果 |
|---|---|
| `cargo fmt --all -- --check` | Pass |
| `cargo clippy --workspace --all-targets -- -D warnings` | Pass（零警告） |
| `cargo test --workspace` | Pass（119 项，0 失败） |
| `pnpm typecheck` / `pnpm lint` | Pass（零警告） |
| `pnpm test` | Pass（22 项） |
| `pnpm build` | Pass |
| 旧系统 `dotnet restore/build/test` | Pass（build 0 错误 0 警告；660 项测试 0 失败） |
| `build_dev_package.py` 完整流程 | Pass（重建 installer、静默安装、禁资产、错版校验、安装版启动拉起安装目录 Agent、旧库 verified_stable） |
| 8 小时 soak（整改后脚本） | Pass：28801 秒、480 采样、hello+shutdown 优雅退出 exit code 0 无强杀、RSS 15.3→17.9MB 有界、WAL 峰值 1.1MB/结束 8KB、7764 条 Observation、dropped=0、心跳严格单调、writer 全程 healthy、quick_check ok、旧库 verified_stable（[soak-summary.json](./evidence/v0.1/b42d2e2-remediation/soak-summary.json)） |

## 5. 审核未列出、整改中另发现并已修复的问题

1. **事件泵冻结 Agent runtime（High）**：`std::sync::mpsc::Receiver::recv` 在 current_thread tokio 任务中直接调用，冻结全部任务（Agent 存活但 IPC 无响应）。已改桥接线程转发，e2e 全量通过。09 §16.3 已把"任务内禁止阻塞 runtime"写入合同。
2. **soak runtime 行查询缺决胜（Medium）**：bootstrap 占位 runtime 行与当前运行行 `started_at_utc_ms` 相同，`ORDER BY started_at DESC LIMIT 1` 不确定，导致心跳采样抖动。已补 `runtime_id DESC`（与 Rust reader 一致）。
3. **checkpoint busy 判定错误（Medium）**：`PRAGMA wal_checkpoint(TRUNCATE)` 的 busy 经结果行返回而非 SQL 错误，原 `execute_batch` 会把 busy 误判为成功。`writer.rs::checkpoint_truncate` 改读结果行，busy 归一化为 `AGENT_WRITER_DEGRADED` 诊断。09 §16.4 已记录。
4. **安装包错版（流程验证）**：整改后的 release Agent 与旧 installer 内副本不一致，被 package 脚本正确拦截；重新打包后通过。证明 09 §9.3 错版校验真实有效。

## 6. 剩余 Pending（不阻塞代码结论，阻塞"验收完成"宣称）

- 09 §12.2 手工门禁：真实锁屏/休眠、30 分钟受控对照、960×640/1280×800、100%/150%/200% DPI、Light/Dark/High Contrast、键盘导航、屏幕阅读器、离线历史 UI 显示（[manual-checklist.md](./evidence/v0.1/b42d2e2-remediation/manual-checklist.md)）。
- disk-full 手工故障注入（busy/corruption/checkpoint 已自动覆盖，disk-full 预期走同一 mark_fatal 路径）。
- 09 Draft 的正式接受：留待产品评审。

## 7. 工作区与提交状态

- 40 个已跟踪文件修改（+2048/−404），8 个新增：`settings_reconciler.rs`、`session_power.rs`、`fault_injection.rs`、`ipc_protocol.rs`、`settings_lifecycle.rs`、`writer_watermark.rs`、`docs/rebuild/evidence/`、本文件。
- 未 commit / push / PR（按用户要求）；无生成目录、数据库、日志、安装包、截图或本机路径混入工作区。
- 旧 WPF/C#/Bridge 回滚入口未改动；rebuild 不接管 production channel；运行时测试全部使用 `rebuild-v01-test-*` 隔离 channel，旧库仅只读 checksum。
