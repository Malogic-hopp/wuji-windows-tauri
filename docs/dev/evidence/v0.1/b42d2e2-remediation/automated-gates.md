# v0.1 自动门禁映射表（09 §12.1 → 可执行证据）

生成：2026-07-22；代码状态：HEAD `b42d2e2` + 审核整改工作区（R02–R10，未提交）。
本表逐项列出自动门禁、对应测试/命令与最后验证日期。测试总数只是摘要，逐项映射以本表为准。

## 构建与静态门禁

| 门禁 | 命令 | 结果（2026-07-22） |
|---|---|---|
| Rust fmt | `cd rebuild && cargo fmt --all -- --check` | Passed |
| Rust clippy | `cd rebuild && cargo clippy --workspace --all-targets -- -D warnings` | Passed（零警告） |
| Rust 全量测试 | `cd rebuild && cargo test --workspace` | Passed（119 项） |
| React typecheck | `cd apps/desktop && pnpm typecheck` | Passed |
| React ESLint | `pnpm lint` | Passed（零警告） |
| React Vitest | `pnpm test` | Passed（22 项） |
| React build | `pnpm build` | Passed |
| 旧系统回归 | `dotnet restore/build/test QuantifiedSelf.Windows.sln` | Passed（build 0 错误 0 警告；660 项测试 0 失败） |

## SQLite（09 §12.1 SQLite 行）

| 门禁 | 测试（`crates/wuji-storage/tests/storage.rs` 等） |
|---|---|
| schema 原样执行/空库 bootstrap | `bootstrap_creates_valid_database`、`bootstrap_refuses_existing_and_bad_timezone_cleans_up` |
| 拒绝非 v0.1 库 | `open_existing_rejects_non_v01_database` |
| FK/CHECK/单 open 行 | `foreign_keys_and_single_open_row_are_enforced` |
| 事务回滚 | `transaction_rollback_leaves_no_partial_batch` |
| 只读 reader | `reader_is_read_only_and_missing_db_is_db_unavailable` |
| 并发 WAL | `reader_queries_work_while_writer_holds_connection` |
| 触及桶重算幂等 | `recompute_is_deterministic_and_covers_hours_and_dates`、`dst_fall_back_keeps_two_same_named_local_hours` |
| Observation 重放幂等 | `observation_replay_is_idempotent` |
| last-known-good 内容持久化（R04） | `settings_revision_persists_last_known_good_content` |
| 启动恢复 | `open_rows_are_recoverable_after_reopen` |

## 领域算法（09 §12.1 领域行）

| 门禁 | 测试（`apps/agent` lib 与 integration） |
|---|---|
| 零时长首样本 | `first_observation_creates_zero_duration_segment_only` |
| 采样切换不归属 | `app_switch_records_sampling_transition_and_keeps_work_block` |
| Idle pending | `idle_pending_then_resume_counts_short_idle`、`idle_break_closes_work_retroactively_at_idle_start` |
| Work break / app switch | `unknown_observation_closes_work_block_only`、`same_app_attribution_extends_segment_and_opens_work` |
| restart 恢复 | `startup_recovery_closes_legacy_open_rows`、`startup_recovery_closes_open_segment_and_work_with_agent_restart`、`agent_crash_restart_recovers_open_rows`（e2e） |
| clock change | `clock_backward_produces_zero_length_gap_and_no_negative_time`、`clock_skew_between_utc_and_monotonic_is_detected` |
| UTC/local/DST 固定样本 | `dst_fall_back_engine_recompute_keeps_both_hours`、`timeutil::tests::*`（4 项） |
| 守恒交叉验证 Today↔Timeline | `conservation_cross_check_today_timeline_and_daily` |
| 21+ 应用 Top 20 不少算（R02） | `today_active_total_is_not_truncated_by_top20` |
| 合并 gap 的 event_count 不少算（R02） | `today_dropped_count_sums_merged_gap_event_count` |
| 跨午夜按 local date 拆分（R02） | `today_cross_midnight_splits_by_local_date` |

## 隐私（09 §12.1 隐私行）

| 门禁 | 测试 |
|---|---|
| DB/WAL/DTO 不出现排除 App 名、用户名 | `privacy_canary_never_persists_to_db_wal_or_dto`（字节级扫描） |
| 隐私排除事件合并与恢复 | `privacy_excluded_merges_events_and_resumes_cleanly`、`pipeline::tests::excluded_process_produces_no_observation` |
| 日志扫描 | N/A：v0.1 不写日志文件（仅 stderr 静态中文安全串），扫描为空集，已人工核对 `apps/agent/src` 无日志文件写入 |

## Writer 与队列（09 §12.1 Writer 行）

| 门禁 | 测试（`apps/agent/tests/fault_injection.rs`、`writer_watermark.rs` 等） |
|---|---|
| 两条 queue 满载 + continuity epoch | `capture_loop::tests::full_queue_drops_new_and_bumps_epoch`、`processor_task::tests::full_writer_lane_drops_and_bumps_epoch`、`queue_drop_epoch_change_closes_continuity_with_gap` |
| control 优先 + 固定水位（R03/R04） | `full_queue_pause_drains_backlog_before_boundary`、`straggler_after_watermark_is_post_boundary_observation`、`backlog_before_watermark_keeps_old_revision` |
| busy 故障注入 | `busy_lock_degrades_then_recovers` |
| corruption/FK 故障注入（不自动修复） | `corrupt_schema_faults_writer_and_does_not_auto_repair` |
| checkpoint busy | `checkpoint_busy_only_records_diagnostic` |
| disk-full | 手工门禁（见 manual-checklist.md）；SQLITE_IOERR 走与 corruption 相同的 mark_fatal 路径 |
| 真实队列深度表（R09） | `processor_task::tests::queue_depth_gauges_track_backlog` |

## IPC（09 §12.1 IPC 行）

| 门禁 | 测试（`apps/agent/tests/ipc_protocol.rs`、`agent_e2e.rs`） |
|---|---|
| hello/version/channel | `agent_rejects_oversize_and_wrong_channel_hello`、`ipc_handshake_fsm_and_settings_reload_roundtrip` |
| 非法 command | `command_server::tests::unknown_command_has_no_transition` |
| 超长消息 | `agent_rejects_oversize_and_wrong_channel_hello` |
| 非法 UTF-8 / hello 字段（R05） | `agent_rejects_invalid_utf8_and_malformed_hello` |
| ULID / sentAtUtcMs 校验（R05） | `invalid_request_id_is_rejected`、`non_decimal_sent_at_is_rejected` |
| 逐命令 payload（R05） | `unknown_payload_fields_are_rejected_per_command` |
| timeout 不取消副作用 + 同 ID 重试（R05） | `timeout_does_not_cancel_side_effect_and_retry_returns_real_result` |
| 重复/冲突 request ID | `conflicting_payload_with_same_id_is_rejected`、e2e `agent_end_to_end_capture_fsm_ipc_and_shutdown` |
| 所有 Capture 状态转换 | `command_server::tests::capture_transition_table_matches_baseline` |

## Settings（09 §12.1 Settings 行）

| 门禁 | 测试 |
|---|---|
| revision CAS / saved-not-applied / Run Key 补偿 | `settings_update_cas_run_key_and_saved_not_applied`（desktop host_integration） |
| 文件删除（新库/已有 revision 两态，R04） | `settings_store::tests::missing_file_on_fresh_database_allows_defaults`、`missing_file_with_applied_revision_recovers_last_known_good` |
| 文件损坏（R04） | `settings_store::tests::corrupt_file_recovers_last_known_good` |
| revision 降级（R04） | `settings_store::tests::downgrade_revision_is_rejected`、`downgrade_settings_applied_is_rejected` |
| 同 revision digest 冲突（R04） | `settings_store::tests::same_revision_digest_conflict_is_rejected` |
| 生效边界 watermark（R04） | `backlog_before_watermark_keeps_old_revision` |
| 自动重试/reconciler（R04） | `reconciler_applies_newer_saved_file` |
| 不可恢复时禁止采集（R04） | `settings_store::tests::unrecoverable_last_known_good_blocks_capture` |

## 生命周期（09 §12.1 生命周期行）

| 门禁 | 测试 |
|---|---|
| Desktop/父进程退出后 Agent 存活 | `agent_survives_parent_exit_and_offline_read_works_after_kill`（e2e，进程级断言） |
| detached Agent 初始 stopped | `ensure_running_spawns_detached_agent_in_stopped_state` |
| Agent restart/open-row recovery | `agent_crash_restart_recovers_open_rows` |
| CaptureStop/重复启动 | `agent_end_to_end_capture_fsm_ipc_and_shutdown`、`second_instance_is_rejected` |
| 版本不兼容 | `agent_controller::tests::version_gate_rejects_mismatch`、`version_gate_accepts_protocol_and_schema_one` |
| 真实 Sleep/Lock 事件（R03） | `sleep_and_lock_events_close_rows_with_matching_kinds`、`sleep_during_pause_keeps_original_gap`（事件泵本身：`session_power::tests::pump_starts_and_yields_receiver`；真实锁屏/休眠为手工门禁） |
| Agent 离线读取已有历史 | `agent_survives_parent_exit_and_offline_read_works_after_kill`（崩溃后 ro reader + quick_check）、`query_service_reads_seeded_database` |

## 打包（09 §12.1 打包行）

| 门禁 | 命令/证据 |
|---|---|
| 固定命名空间与 Agent 布局 | `python scripts/build_dev_package.py`（REQUIRED_LAYOUT 校验） |
| 包内无 Bridge/.NET/旧合同 | 同上（FORBIDDEN_PATTERNS 扫描） |
| Agent 二进制 byte 级一致 | 同上（错版拒绝，2026-07-22 曾正确拦下过期 installer） |
| 安装版 Desktop 拉起安装目录 Agent（R06） | 同上（`verify_installed_launch`，test channel 隔离） |
| dev package manifest | `dist/dev-package-manifest.json` → 证据包 `package-validation.json` |
