# WUJI v2 SQLite 与持久化读模型设计

状态：Draft
最后更新：2026-07-18
领域权威：[02-行为分析领域模型.md](./02-行为分析领域模型.md)
运行时权威：[05-Rust-Agent运行时设计.md](./05-Rust-Agent运行时设计.md)
接口权威：[06-本地接口与错误合同.md](./06-本地接口与错误合同.md)

## 1. 目的与范围

本文定义 WUJI v2 行为事实、派生结果、持久化读模型、事务、保留、迁移和查询快照。这里的“必须字段”是逻辑 Schema 合同；最终 migration SQL 是物理执行来源，但不得偏离逻辑合同。

SQLite 是**行为数据、派生结果和运行审计的权威数据源**，不是所有应用状态的唯一数据源。用户可编辑 Settings 保存在 Desktop 原子写入的版本化 JSON 中；数据库只记录已应用 revision。安装状态、日志和 UI cache 也不属于业务库。

## 2. 所有权

### 2.1 正常运行时

- Agent Single Writer 是唯一读写连接所有者；
- Desktop 只使用 `mode=ro`、`query_only=ON` 的短生命周期 reader pool；
- Heartbeat、维护、重建、事件和 side-effect receipt 都通过 Writer；
- React 永远不接触 SQL 或文件路径。

### 2.2 迁移所有权

- Agent 负责同一 major schema 内可在线、向前兼容的 migration；
- 需要替换数据库文件、大表离线复制、v1 导入或 major schema 升级时，Agent 必须停止，由受信任 migrator 获得独占锁；
- Desktop 不执行 migration，只按 [06-本地接口与错误合同.md](./06-本地接口与错误合同.md) 进入 Maintenance 并关闭 reader pool。

## 3. SQLite 基线

Writer 打开连接后必须设置并验证：

```sql
PRAGMA journal_mode = WAL;
PRAGMA foreign_keys = ON;
PRAGMA synchronous = NORMAL;
PRAGMA busy_timeout = 5000;
PRAGMA trusted_schema = OFF;
```

Rebuild v0.1 收紧为 `busy_timeout = 750` 加有限重试以卡进 IPC 3 秒 timeout，见 09 §5.2 与 §15 偏离登记；其余 PRAGMA 与 v0.1 一致。

Desktop reader 必须 `query_only=ON`。所有时长使用整数毫秒；事实时间使用 UTC epoch milliseconds；用户显示的本地日期由 Calendar Generation 解释。

主键可以使用 SQLite integer；跨进程 request、job、generation、snapshot 使用 ULID 文本。所有 `*_version` 使用不可变文本标识。

## 4. Schema 分层

```text
Metadata
  schema_migrations, database_metadata, settings_revisions,
  settings_effectivity_intervals, segmentation_profiles, work_profiles,
  analysis_profiles, calendar_generations, derivation_jobs,
  segmentation_generations, work_generations, analysis_generations,
  identity_resolution_generations, identity_resolution_members,
  result_sets, query_snapshots, query_snapshot_slices, snapshot_leases

Facts / Identity
  identity_generations, identity_links, app_identities, time_epochs,
  context_definitions, context_aliases, safe_feature_sets,
  foreground_observations, data_quality_intervals,
  tracking_expectation_intervals

Derived detail
  activity_segments, segment_classifications, context_segments,
  transition_decisions, interruption_events, context_switch_events,
  decision_evidence_snapshots, work_blocks

Read models
  hourly_app_usage, hourly_context_usage,
  daily_app_usage, daily_context_usage,
  daily_work_metrics, daily_switch_metrics,
  daily_data_quality_metrics, legacy_daily_summaries

Runtime / audit
  agent_runtime, agent_events, maintenance_audits, command_receipts
```

第一版不得包含 Focus Block、daily focus 字段、`fragmented_duration_ms` 或 Plaintext 标题列。

## 5. 元数据与世代

### 5.1 `schema_migrations`

必须字段：`version`、`name`、`checksum`、`applied_at_utc_ms`、`app_version`。已应用 migration checksum 漂移必须拒绝启动写入。

### 5.2 `database_metadata`

单行保存 `database_id`、`schema_major`、`schema_version`、`created_at_utc_ms`、`runtime_channel`、`database_generation` 和 `last_fact_cursor INTEGER NOT NULL DEFAULT 0`。`0` 是“尚无已提交事实”的保留哨兵，不是合法 Fact Cursor；第一条事实使用 1。Writer 在事实事务内执行 `last_fact_cursor + 1` 并把同一值写入输入行；跨 Runtime Instance、Clear 或空 Snapshot 均不重置。不保存用户名、SID 或绝对路径。

### 5.3 `settings_revisions`

必须字段：`revision`、`settings_schema_version`、`content_digest`、`observed_at_utc_ms`、`applied_at_utc_ms?`、`state`、`safe_error_code?`。`state = Observed | Active | Rejected | Superseded`。

`agent_runtime.active_settings_revision` 外键指向唯一 Active revision。数据库不复制完整 Settings JSON。

### 5.4 Profiles 与 `settings_effectivity_intervals`

`segmentation_profiles` 必须字段：`segmentation_profile_id`、`segmentation_version`、`feature_schema_version`、`activity_key_schema_version`、`gap_cap_ms`、`confirm_duration_ms`、`min_samples`、`profile_digest UNIQUE`。

`work_profiles` 必须字段：`work_profile_id`、`work_algorithm_version`、`idle_break_ms`、`boundary_policy_version`、`profile_digest UNIQUE`。

`analysis_profiles` 必须字段：`analysis_profile_id`、`rule_version`、`classifier_version`、`analysis_algorithm_version`、`context_commit_ms`、`minimum_confidence`、`profile_digest UNIQUE`。

`settings_effectivity_intervals` 必须字段：`effectivity_id`、`settings_revision`、`effective_from_fact_cursor`、`effective_to_fact_cursor?`、`capture_profile_digest`、`segmentation_profile_id`、`work_profile_id`、`analysis_profile_id`、`calendar_generation_id`。区间按 Fact Cursor 连续且不重叠。新 revision 在其第一条事实事务中关闭旧区间并打开新区间；未产生事实即被替换的 revision 不创建空区间。完整 Settings Revision 是审计来源；Generation 只绑定对应 Profile。

### 5.5 `calendar_generations`

必须字段：

```text
calendar_generation_id
reporting_windows_time_zone_id
reporting_iana_time_zone_id
windows_iana_mapping_version
tzdb_version
created_at_utc_ms
source_settings_revision
state
```

小时桶由 `bucket_start_utc_ms` 唯一定位，并额外保存 `local_date`、`local_hour` 和 `utc_offset_minutes`。Daily 表引用 Calendar Generation，不假定一日只有一个 offset。

Calendar Generation 是纯规则元数据；投影是否追平由 Result Set 的 sealed Fact Cursor 证明，不在 Calendar 行伪造水位。

### 5.6 `derivation_jobs`

有限执行任务。必须字段：`job_id`、`job_kind`、`state`、`input_fact_cursor`、`target_fact_cursor`、`processed_fact_cursor`、`started_at_utc_ms?`、`ended_at_utc_ms?`、`safe_error_code?`、`requested_by_request_id?`。

只有 Job 使用 `Running/Completed/Failed` 生命周期；Generation 不冒充执行任务。

### 5.7 `segmentation_generations`

必须字段：`segmentation_generation_id`、`segmentation_profile_id`、`state`、`staging_through_fact_cursor`、`staging_through_utc_ms`、`created_at_utc_ms`、`created_under_settings_revision`、`rebuild_capability`。

`state = Building | Advancing | Ready | Superseded | Failed`。在线 Advancing Generation 不要求 `source_end`。

### 5.8 `work_generations`

必须字段：`work_generation_id`、`segmentation_generation_id`、`work_profile_id`、`state`、`staging_through_fact_cursor`、`staging_through_utc_ms`、`created_at_utc_ms`、`created_under_settings_revision`。Context Profile 变化不得创建 Work Generation。

### 5.9 `analysis_generations`

必须字段：`analysis_generation_id`、`segmentation_generation_id`、`analysis_profile_id`、`state`、`staging_through_fact_cursor`、`staging_through_utc_ms`、`created_at_utc_ms`、`created_under_settings_revision`、`rebuild_capability`。

Generation 外键冻结算法版本真相；下游行不重复保存版本字符串。组件发布依赖另由具体 Result Set ID 表达，不能用 Generation ID 替代。

### 5.10 `result_sets`

必须字段：

```text
result_set_id, result_kind, data_kind,
coverage_start_epoch_ordinal?, coverage_start_utc_ms?, coverage_start_fact_cursor?,
coverage_end_epoch_ordinal?, coverage_end_utc_ms?, coverage_end_fact_cursor?,
legacy_coverage_start_utc_ms?, legacy_coverage_end_utc_ms?,
sealed_through_fact_cursor?,
source_segmentation_generation_id?, source_work_generation_id?,
source_analysis_generation_id?,
source_segmentation_result_set_id?, source_work_result_set_id?,
source_analysis_result_set_id?,
identity_resolution_generation_id?, calendar_generation_id?,
projection_dependency_mask?, import_job_id?, granularity_mask,
rebuild_capability, explanation_capability, validation_digest?, state,
sealed_at_utc_ms?, created_by_job_id
```

三个 start 字段和三个 end 字段分别组成领域 `FactBoundary`。NativeV2/NativeV2Summary 必须全部非空，并满足 start boundary 按 `(epoch_ordinal, utc_ms, fact_cursor)` 字典序严格小于 end boundary；`coverage_end_fact_cursor` 是 exclusive boundary tie-breaker，`sealed_through_fact_cursor` 才是 inclusive 发布水位。LegacySummary 的六个 Native boundary 字段和 sealed-through 必须全空，两个 Legacy UTC 字段非空且 `end > start`，并引用 import job。禁止混填两套 coverage。

manifest 必须生成等价于 SQLite row-value comparison 的约束：

```sql
(coverage_start_epoch_ordinal,
 coverage_start_utc_ms,
 coverage_start_fact_cursor)
<
(coverage_end_epoch_ordinal,
 coverage_end_utc_ms,
 coverage_end_fact_cursor)
```

Native interval overlap 的唯一判定是 `A.start < B.end AND B.start < A.end`，其中 start/end 都是上述三元组；不得另建一个 UTC-only overlap trigger。

`data_kind = NativeV2 | NativeV2Summary | LegacySummary` 只表示保真度/来源。`result_kind = Segmentation | Work | Analysis | Projection` 只表示组件，不再包含 Summary/Legacy。LegacySummary 第一版只允许 Projection；NativeV2Summary 按保留能力分别创建 Segmentation、Work、Analysis、Projection 组件 Set。

来源绑定规则：

- Segmentation Set 不引用其他组件 Set，必须绑定 Segmentation Generation；
- Work Set 必须绑定 Work + Segmentation Generation；NativeV2 Full 必须绑定实际使用的 `source_segmentation_result_set_id`；NativeV2Summary 应引用新建的 Segmentation Summary Set，只有 capability 明确无 Segmentation 来源时才可为空；
- Analysis Set 同理绑定 Analysis + Segmentation Generation 及其具体 Segmentation Set；
- Projection Set 使用 `projection_dependency_mask = Segmentation | Work | Analysis` 的位集合声明实际依赖；mask 中每一项必须有对应的 `source_*_result_set_id`，mask 外字段必须为空；来源必须是 Sealed、kind 正确、coverage 包含 Projection coverage 且 data kind/capability 兼容；
- Projection 保存的 Generation ID 必须与对应具体来源 Set 的 Generation ID 相同，但 Generation 相同不能替代 Result Set ID 一致性；
- Projection 的 `identity_resolution_generation_id` 对 App 读模型必填，`calendar_generation_id` 对 Hourly/Daily 读模型必填。

来源 ID 使用指回 `result_sets(result_set_id)` 的延迟外键；kind/state/coverage/mask 一致性由 Seal/激活 trigger 与 manifest 合成测试共同拒绝。Projection 不得引用自身或形成依赖环。Seal 时 `validation_digest` 必填，覆盖规范化 Header、具体来源 ID、水位、行数和守恒 totals。只有 `state='Sealed'` 可被 Snapshot 引用；Sealed 行及其明细/读模型不得 UPDATE/DELETE，除非已被 GC 判定为 Garbage。

NativeV2Summary 保留原 Generation、Identity Resolution 和 Calendar。无法按新报告时区或新 Identity Resolution 重投影时，Storage Query 不把不同口径下同一 local date/App 名静默合并；DTO 返回 Slice metadata 并标记历史口径。

### 5.11 `query_snapshots` 与 `query_snapshot_slices`

`query_snapshots` 必须字段：`query_snapshot_id`、`published_through_fact_cursor?`、`published_through_utc_ms?`、`default_calendar_generation_id`、`created_under_settings_revision`、`empty_reason?`、`empty_at_fact_cursor?`、`state`、`created_at_utc_ms`、`activated_at_utc_ms?`、`superseded_at_utc_ms?`。

空 Snapshot 允许零个 Slice，且必须满足 `empty_reason IN (NoFacts,Cleared)`、published-through 两字段均为空；`Cleared` 必须保存 `empty_at_fact_cursor`（允许值 0 表示清理前从无事实），`NoFacts` 的 empty-at 为空。非空 Snapshot 至少一个 Slice，empty 字段全空，published-through 全非空。Snapshot Header 固定默认 Calendar 与 Settings revision，使零事实 UI 仍可返回稳定元数据。

`query_snapshot_slices` 必须字段：`query_snapshot_id`、`ordinal`、`coverage_start_epoch_ordinal?`、`coverage_start_utc_ms?`、`coverage_start_fact_cursor?`、`coverage_end_epoch_ordinal?`、`coverage_end_utc_ms?`、`coverage_end_fact_cursor?`、`legacy_coverage_start_utc_ms?`、`legacy_coverage_end_utc_ms?`、`segmentation_result_set_id?`、`work_result_set_id?`、`analysis_result_set_id?`、`projection_result_set_id`、`data_kind`。主键 `(query_snapshot_id, ordinal)`。Native 与 Legacy coverage 的 nullability、三元组顺序和 overlap SQL 规则与 Result Set 完全相同。

约束：

- 部分唯一索引保证最多一个 `state='Active'`；
- Slice 只能引用 Sealed 且复合 coverage 完整包含 Slice 的组件 Result Set；kind 必须与列匹配；Native Slice 的不重叠按完整 Fact Boundary 字典序判定，不按 UTC-only trigger；相邻 Slice 可共享 UTC 毫秒但必须 `previous.endBoundary = next.startBoundary`；
- Slice 的非空 Segmentation/Work/Analysis ID 必须逐项等于 Projection Set 的 dependency mask/source Result Set ID；只匹配 Generation 不合格；
- Legacy Slice cursor/epoch 全空；Snapshot Builder 以 Native 优先裁剪任何 UTC 冲突的整个毫秒，并在 validation digest 中记录；
- 当前尾部 Result Set 的 `sealed_through_fact_cursor`/UTC 决定非空 Snapshot published-through；
- Snapshot 和 Slice 创建后不可变，只允许一次 `Preparing→Active→Superseded→Collectable` 状态推进；
- 激活 Snapshot 与旧 Snapshot Supersede 在同一事务完成。

一个 Snapshot 可以引用多个原生 v2 Generation 的历史 Result Set 和 Legacy Summary，因此不需要跨 Snapshot 拼接。

### 5.12 `snapshot_leases`

必须字段：`lease_id`、`query_snapshot_id`、`desktop_instance_id`、`acquired_at_utc_ms`、`expires_at_utc_ms`、`last_renewed_at_utc_ms`。该表仍由 Agent Writer 独占写入：Desktop 通过 IPC 请求获取/续租，不能用只读连接写 lease。Lease 只保护回收，不改变 SQLite 读事务语义。

Agent 离线时没有 Writer/GC，Desktop 可以无 Lease 读取 Active Snapshot。IPC 在线但 Writer Degraded/Faulted 时，Supervisor 必须先将 GC 置为内存锁存的 `SuspendedWriterUnavailable`；所有 Snapshot（包括已过期 Lease）在锁存期间不得进入 Collectable，Desktop 可使用标记为 `GcSuspended` 的无 Lease 只读模式。Writer 恢复后先持久化 `gc_resume_not_before_utc_ms`，进入至少覆盖一个完整 Lease TTL 与 Desktop 重连窗口的恢复宽限期；宽限期内允许重新获取/续租但禁止 GC。无法持久化宽限水位时继续暂停 GC，不得以恢复 Writer 为由立即回收旧 Snapshot。每次 Agent 启动也先进入同一恢复宽限期，防止上次 Faulted/崩溃留下的无 Lease reader 在重启后立即失去 Snapshot。

## 6. 身份、事实与质量

### 6.1 `identity_generations` 与 `identity_links`

`identity_generations` 必须字段：`identity_generation_id`、`fingerprint_key_version UNIQUE`、`state`、`created_at_utc_ms`、`retired_at_utc_ms?`、`predecessor_generation_id?`、`protection_scheme`、`protected_key_digest`。部分唯一索引保证一个 Active；`state = Active | Retired | Lost`。

`identity_links` 必须字段：`identity_link_id`、`from_app_identity_id`、`to_app_identity_id`、`link_kind`、`created_at_utc_ms`、`safe_evidence_digest`。`link_kind = TrustedRuntimeContinuity | UserConfirmed`；禁止仅按 fingerprint 自动跨世代链接。

`identity_resolution_generations` 必须字段：`identity_resolution_generation_id`、`predecessor_generation_id?`、`state`、`created_at_utc_ms`、`created_by_identity_link_id?`、`mapping_digest`。`state = Building | Ready | Superseded | Failed`。

`identity_resolution_members` 必须字段：`identity_resolution_generation_id`、`app_identity_id`、`canonical_app_identity_id`。主键 `(identity_resolution_generation_id, app_identity_id)`；这里只保存非恒等 Link closure，未出现的 App Identity 由查询函数稳定解析到自身。canonical 必须是已存在 App Identity，映射必须幂等、单终点且无环。新 Link 创建新 Resolution Generation 和受影响 Projection Result Set；新发现但未链接的 App 不创建 Resolution；旧 Resolution/Sealed Set 不修改。

### 6.2 `app_identities`

必须字段：`app_identity_id`、`identity_generation_id`、`app_key`、`canonical_display_name`、`normalized_process_name`、`executable_fingerprint?`、`first_seen_at_utc_ms`、`last_seen_at_utc_ms`。唯一 `(identity_generation_id, app_key)`。

初始化必须创建 `app:unknown`，所有 Observation 和 App usage 都使用非空 App FK。

### 6.2.1 `time_epochs`

必须字段：`time_epoch_id`、`epoch_ordinal UNIQUE`、`runtime_instance_id`、`started_at_utc_ms`、`ended_at_utc_ms?`、`start_fact_cursor?`、`end_fact_cursor?`、`close_reason`。ordinal 从 1 单调增加且不因 Clear 重置；Observation 的 `time_epoch_id` 使用 FK。Result Set/Slice boundary 同时保存 ordinal 作为比较快照，并由 trigger 验证与边界事实所属 epoch 一致。

### 6.3 `context_definitions` 与 `context_aliases`

`context_definitions` 必须保存 `context_id`、`canonical_key UNIQUE`、`display_name`、`kind`、`lifecycle_state`、创建/更新时间。

`context_aliases` 必须保存 `alias_key`、`target_context_id`、`valid_from_utc_ms`、`valid_to_utc_ms?`、`rule_version`、`operation_kind`。同一生效区间内 alias 只能解析到一个 target。

### 6.4 `safe_feature_sets`

必须字段：`safe_feature_set_id`、`feature_schema_version`、`canonical_digest`、`features_json`、`rebuild_capability`、`created_at_utc_ms`。唯一 `(feature_schema_version, canonical_digest)`；行不可变。

Writer 对 allowlisted features 使用确定性 key 排序和规范 JSON，最大 2 KiB，计算 BLAKE3 digest 后复用已有行；相同 digest 必须比较规范字节，异常碰撞进入 Faulted。空特征复用每个 Feature Schema 的固定行，禁止每 Observation 创建一行。索引只覆盖 version+digest；Prune 在 Observation、Classification 和 Evidence 均无引用后分块删除 orphan。

### 6.5 `foreground_observations`

必须字段：

```text
observation_id
fact_cursor UNIQUE
runtime_instance_id
capture_sequence
captured_at_utc_ms
time_epoch_id
app_identity_id NOT NULL
activity_state
privacy_mode                 -- Masked | IdentityOnly | Legacy
safe_feature_set_id?
title_feature_available
capture_quality
capture_settings_revision
created_at_utc_ms
```

约束：`UNIQUE(runtime_instance_id, capture_sequence)`；第一版无 PID、原始标题、完整路径和 Plaintext 枚举。

### 6.6 `data_quality_intervals`

必须字段：`quality_interval_id`、`runtime_instance_id`、`start_fact_cursor`、`end_fact_cursor?`、`kind`、`started_at_utc_ms`、`ended_at_utc_ms?`、`first_capture_sequence?`、`last_capture_sequence?`、`event_count`、`safe_reason_code`、`capture_settings_revision`。

`kind = CaptureFailure | QueueDrop | OutOfOrder | TimeEpochBreak | PrivacyExcluded | AgentUnavailable | TitleUnavailable`。PrivacyExcluded 行不得引用 App 或 safe features。QueueDrop/OutOfOrder 必须保留 capture sequence gap。AgentUnavailable 只允许位于 Expected Tracking Expectation 内；UserPaused/UserStopped/ScheduledOff/ExclusiveMaintenance/SystemSleepOrLogoff 只属于 expectation，不创建质量 kind。点事件允许 end 为空，但投影时长仅来自有界区间。

### 6.7 `tracking_expectation_intervals`

必须字段：`expectation_interval_id`、`state`、`started_at_utc_ms`、`ended_at_utc_ms?`、`start_fact_cursor?`、`end_fact_cursor?`、`source`、`runtime_instance_id?`、`settings_revision?`、`safe_reason_code`。

`state = Expected | UserPaused | UserStopped | ScheduledOff | ExclusiveMaintenance | SystemSleepOrLogoff | UnknownEligibility`。同一时间最多一个开放区间；状态转换与命令 receipt/运行状态在同一 Writer 事务提交。只有 Expected 进入质量分母。

## 7. 派生明细与外键

以下是首版逻辑列清单，不再用“起止”“组成字段”等占位词。所有查询可见行包含所属组件的 `result_set_id`，且 Result Set 必须 Sealed。实体键为 `(result_set_id, entity_id)`；跨组件源引用显式保存 `source_*_result_set_id + entity_id` composite FK。Staging 使用 job-scoped 临时/工作表，不属于查询 Schema。

### 7.1 `activity_segments`

必须字段：`result_set_id`、`activity_segment_id`、`segmentation_generation_id`、`app_identity_id`、`stable_window_key?`、`privacy_mode`、`activity_key_hash`、`activity_key_schema_version`、`activity_state`、`started_at_utc_ms`、`ended_at_utc_ms`、`duration_ms`、`first_fact_cursor`、`last_fact_cursor`、`first_runtime_instance_id`、`first_capture_sequence`、`last_runtime_instance_id`、`last_capture_sequence`、`close_reason`。

主键 `(result_set_id, activity_segment_id)`；FK 分别指向 Result Set、Segmentation Generation、App Identity。索引 `(result_set_id, started_at_utc_ms, ended_at_utc_ms)` 和 `(result_set_id, app_identity_id, started_at_utc_ms)`。状态时长只有 `duration_ms`；`CHECK(duration_ms = ended - started AND duration_ms >= 0)`。

### 7.2 `segment_classifications`

必须字段：`result_set_id`、`classification_id`、`analysis_generation_id`、`source_segmentation_result_set_id`、`activity_segment_id`、`context_id?`、`confidence`、`decision_state`、`reason_code`、`evidence_json`、`inheritance_source_classification_id?`。

主键 `(result_set_id, classification_id)`；Composite FK `(source_segmentation_result_set_id, activity_segment_id)` 指向 Activity Segment，继承来源使用 Analysis Set 内 composite FK。`CHECK(confidence BETWEEN 0 AND 1)`；索引 `(result_set_id, context_id, classification_id)`。

### 7.3 `context_segments`

必须字段：`result_set_id`、`context_segment_id`、`analysis_generation_id`、`source_segmentation_result_set_id`、`context_id`、`started_at_utc_ms`、`ended_at_utc_ms`、`duration_ms`、`confidence`、`decision_state`、`close_reason`、`first_activity_segment_id?`、`last_activity_segment_id?`、`context_key_snapshot`、`display_name_snapshot`。

主键 `(result_set_id, context_segment_id)`；first/last 来源 FK 为 `(source_segmentation_result_set_id, *_activity_segment_id)`，`data_kind=NativeV2` 的 Analysis Set 使用 RESTRICT；`data_kind=NativeV2Summary` 的 Analysis Set 在创建时把实体来源置空并保存快照，但 Result Set Header 仍绑定其 Segmentation Summary 组件。`CHECK(duration_ms = ended - started AND duration_ms >= 0)`，索引 `(result_set_id, started_at_utc_ms, ended_at_utc_ms)`。

### 7.4 `transition_decisions`

必须字段：`result_set_id`、`transition_decision_id`、`analysis_generation_id`、`from_context_id?`、`from_context_key_snapshot?`、`candidate_context_id?`、`candidate_context_key_snapshot?`、`next_context_id?`、`next_context_key_snapshot?`、`candidate_started_at_utc_ms`、`candidate_ended_at_utc_ms?`、`state`、`reason_code`、`confidence`、`decided_at_utc_ms?`。主键 `(result_set_id, transition_decision_id)`；`CHECK(confidence BETWEEN 0 AND 1)`；它保存正向和负向决策。

### 7.5 `interruption_events`

必须字段：`result_set_id`、`interruption_event_id`、`analysis_generation_id`、`transition_decision_id`、`started_at_utc_ms`、`ended_at_utc_ms`、`duration_ms`、`from_context_id`、`from_context_key_snapshot`、`interruption_context_id?`、`interruption_context_key_snapshot?`、`returned_context_id`、`returned_context_key_snapshot`、`source_context_segment_id?`、`returned_context_segment_id?`、`reason_code`、`confidence`、`evidence_snapshot_id`。

主键 `(result_set_id, interruption_event_id)`；decision 唯一；Full Result Set 的源 Segment composite FK 使用 RESTRICT，Summary Result Set 来源为空；`CHECK(duration_ms = ended - started AND duration_ms >= 0)`。

### 7.6 `context_switch_events`

必须字段：`result_set_id`、`context_switch_event_id`、`analysis_generation_id`、`transition_decision_id`、`switched_at_utc_ms`、`confirmed_at_utc_ms`、`from_context_id`、`from_context_key_snapshot`、`to_context_id`、`to_context_key_snapshot`、`from_context_segment_id?`、`to_context_segment_id?`、`reason_code`、`confidence`、`evidence_snapshot_id`。

主键 `(result_set_id, context_switch_event_id)`；decision 唯一；Full Result Set 的源 Segment composite FK 使用 RESTRICT，Summary Result Set 来源为空。跨两事件表的 decision 互斥由 Writer 事务和触发器/manifest 测试共同保证。

### 7.7 `decision_evidence_snapshots`

必须字段：`result_set_id`、`evidence_snapshot_id`、`analysis_generation_id`、`rule_ids_json`、`rule_version`、`classifier_version`、`analysis_algorithm_version`、`source_segment_count`、`source_started_at_utc_ms`、`source_ended_at_utc_ms`、`safe_evidence_digest`、`reason_code`、`confidence`、`explanation_capability`、`created_at_utc_ms`。

主键 `(result_set_id, evidence_snapshot_id)`；JSON 为有序规则 ID allowlist，禁止标题/路径。事件引用使用 composite FK。源明细删除后，该行保留至事件保留期结束并把查询能力降为 DecisionSummary。

### 7.8 `work_blocks`

必须字段：`result_set_id`、`work_block_id`、`work_generation_id`、`source_segmentation_result_set_id?`、`started_at_utc_ms`、`ended_at_utc_ms`、`active_duration_ms`、`short_idle_duration_ms`、`close_reason`、`first_activity_segment_id?`、`last_activity_segment_id?`。Unknown 是强制边界，不属于 Work Block 内部时长。

主键 `(result_set_id, work_block_id)`；`data_kind=NativeV2` 的 Work Set 对实体来源使用 `(source_segmentation_result_set_id, activity_segment_id)` composite FK 与 RESTRICT；`data_kind=NativeV2Summary` 的实体来源为空，但 Result Set Header 仍按 capability 绑定 Segmentation Summary 组件。`CHECK(ended > started)`、`CHECK(active_duration_ms >= 0 AND short_idle_duration_ms >= 0)`。不得包含 Context Segment 或 Analysis Generation ID。

## 8. 持久化读模型

所有读模型以不可变 Projection `result_set_id` 为发布边界，保存 `source_end_fact_cursor`。Projection Header 已绑定实际组件 Result Set、Calendar 与按需 Identity Resolution；行不得另行选择 Generation/组件。Daily 表包含 Calendar Generation + local date；同一 Snapshot 由 Storage Query Service 根据 Slice 选择行。

### 8.1 `hourly_app_usage`

字段：`result_set_id`、`bucket_start_utc_ms`、`calendar_generation_id`、`identity_resolution_generation_id`、`local_date`、`local_hour`、`utc_offset_minutes`、`canonical_app_identity_id`、`active_duration_ms`、`idle_duration_ms`、`unknown_duration_ms`、`observation_count`、`segment_count`、`source_end_fact_cursor`。主键 `(result_set_id, bucket_start_utc_ms, canonical_app_identity_id)`；索引 `(result_set_id, local_date, local_hour)`。Resolution Generation 必须等于 Projection Header。

### 8.2 `daily_app_usage`

字段：`result_set_id`、`calendar_generation_id`、`identity_resolution_generation_id`、`local_date`、`canonical_app_identity_id`、`active_duration_ms`、`idle_duration_ms`、`unknown_duration_ms`、`observation_count`、`activity_segment_count`、`first_seen_utc_ms`、`last_seen_utc_ms`、`source_end_fact_cursor`、`is_final`。主键 `(result_set_id, local_date, canonical_app_identity_id)`。未链接身份分别成行，不按显示名合并。

### 8.3 `hourly_context_usage`

字段：`result_set_id`、`bucket_start_utc_ms`、`calendar_generation_id`、`local_date`、`local_hour`、`utc_offset_minutes`、`context_id?`、`duration_ms`、`confidence_weighted_duration_ms`、`unclassified_duration_ms`、`source_end_fact_cursor`。主键 `(result_set_id, bucket_start_utc_ms, context_id)`，Unknown 使用稳定 sentinel context ID 而非 NULL 主键。

### 8.4 `daily_context_usage`

字段：`result_set_id`、`calendar_generation_id`、`local_date`、`context_id`、`duration_ms`、`stable_duration_ms`、`low_confidence_duration_ms`、`unclassified_duration_ms`、`context_segment_count`、`source_end_fact_cursor`、`is_final`。主键 `(result_set_id, local_date, context_id)`。

### 8.5 `daily_work_metrics`

字段：`result_set_id`、`calendar_generation_id`、`local_date`、`active_duration_ms`、`idle_duration_ms`、`unknown_duration_ms`、`work_block_count`、`started_work_block_count`、`longest_work_block_duration_ms`、`source_end_fact_cursor`、`is_final`。主键 `(result_set_id, local_date)`。

不存在 Focus 或 fragmented 字段。

### 8.6 `daily_switch_metrics`

字段：`result_set_id`、`calendar_generation_id`、`local_date`、`raw_app_switch_count`、`raw_window_switch_count?`、`raw_window_switch_availability`、`candidate_context_transition_count`、`effective_context_switch_count`、`low_confidence_switch_count`、`interruption_count`、`interruption_duration_ms`、`source_end_fact_cursor`、`is_final`。主键 `(result_set_id, local_date)`。

无标题覆盖时 raw window count 为 NULL 并设置 availability，不写 0。

### 8.7 `daily_data_quality_metrics`

字段：`result_set_id`、`calendar_generation_id`、`local_date`、`expected_tracking_duration_ms`、`unknown_eligibility_duration_ms`、`runtime_available_duration_ms`、`pipeline_covered_duration_ms`、`analysis_usable_duration_ms`、`title_available_duration_ms`、`context_classifiable_duration_ms`、`context_classified_duration_ms`、`privacy_excluded_duration_ms`、`queue_gap_duration_ms`、`agent_unavailable_duration_ms`、`observation_count`、`drop_count`、`capture_error_count`、三个 coverage ratio、`context_coverage_ratio`、`source_end_fact_cursor`、`is_final`。主键 `(result_set_id, local_date)`。

所有业务 Daily DTO 必须关联同日质量行。

### 8.8 `legacy_daily_summaries`

只保存 v1-only 聚合：`result_set_id`、`source_database_id`、`source_schema_version`、`legacy_algorithm_version`、`local_date`、`legacy_app_key`、`active_duration_ms`、`idle_duration_ms`、`observation_count`、`rebuild_capability='SummaryOnly'`、`import_job_id`、`validation_digest`。主键 `(result_set_id, local_date, legacy_app_key)`。

Legacy Summary 不与原生 v2 Daily 行混写，不生成 Context、Switch 或 Work 明细。原生 v2 旧世代使用普通 Daily 表 + `NativeV2Summary` Result Set，不进入本表。

## 9. Runtime 与审计

### 9.1 `agent_runtime`

单行最后已知快照：runtime instance、Agent/协议版本、actual state、last heartbeat、last observation、last committed capture sequence、last committed Fact Cursor、各 lane queue depth、drop/error 计数、active settings revision、active Query Snapshot、safe error code、updated time。

该表不替代实时 IPC。SQLite heartbeat 只能表示 last-known；IPC 不可达时不能宣称 Agent 仍在运行。

### 9.2 `agent_events`

保存低频安全事件：时间、instance、level、event type、safe message key、request ID、safe error code、allowlisted payload。禁止原始异常、标题、路径、SID 和每 Observation 成功事件。

### 9.3 `maintenance_audits`

必须保存 maintenance ID、kind、request ID、目标范围摘要、状态、开始/结束、水位、删除/重建计数和 safe error code。不得保存原始过滤值。

### 9.4 `command_receipts`

Side-effect command 的持久化去重记录：`request_id`、`command_kind`、`payload_digest`、`state`、`result_code`、`created_at_utc_ms`、`updated_at_utc_ms`、`completed_at_utc_ms?`、`expires_at_utc_ms`、`runtime_instance_id`、`terminal_response_digest?`。主键 request ID；相同 ID + 不同 digest 必须拒绝；重启后在保留期内返回原最终结果，不重复执行。该表可由 Desktop 的固定只读 Query Service 在 Pipe 不可用时查询。

## 10. 实时写入和有界尾部

### 10.1 捕获事务

Observation 事实应尽快落库；Segment 和投影尾部允许回溯修正。每个 Capture batch 单事务：

```text
BEGIN IMMEDIATE
  validate sequence/time epoch
  allocate next global fact_cursor
  upsert App Identity and safe features
  insert Foreground Observation or quality fact
  determine affected tail anchor
  delete derived tail contributions after anchor
  rebuild closed Activity/Work/Context staging tail to fact cursor
  update Generation staging watermarks, database last_fact_cursor and agent_runtime
COMMIT
```

Observation 插入与 staging 派生原子提交。未确认未来时长不预先增加。去抖和 Idle 回溯通过 staging tail rebuild 修正旧贡献。查询仍读取上一个 Sealed Result Set；Publisher 周期性把稳定 staging 范围 Seal 为新 Result Set 并原子发布新 Snapshot。

### 10.2 尾部范围

Segmentation tail 至少覆盖 Activity confirm + gap cap；Analysis tail 至少覆盖 Context commit；Work tail 至少覆盖 Work Idle threshold。范围上限必须配置并有容量测试；若超过安全上限，关闭连续性并记录质量 gap，不无限重写。

### 10.3 Heartbeat

Heartbeat 是高优先控制消息，只更新 runtime，不修改行为时长。即使没有 Observation 也可独立提交。

## 11. Writer lane 与维护事务

队列和抢占细节见 Runtime 规范。存储层保证：

- Control lane：CaptureStop、Shutdown、状态、Heartbeat、cutover barrier；
- Capture lane：Observation/quality batch；
- Maintenance lane：Prune、Rebuild、Checkpoint，按 chunk 提交；
- Exclusive lane：Clear、major migration/import，只在采集暂停且 Work Block 已关闭时执行。

Prune/Rebuild/Checkpoint 时 Capture 继续进入有界队列，maintenance 每个 chunk 后让出 Writer。Clear/Schema replacement 必须暂停 Capture、关闭 Work Block、排空已接受消息并进入 exclusive maintenance。

## 12. 保留与清理

默认候选：Observation/Activity/Context 明细与 FullEvidence 90 天，Work/Switch/Interruption/DecisionSummary/Hourly 一年，Daily 至少两年，Agent event 30 天，command receipt 至少覆盖最长重试窗口。

Prune/compaction 顺序必须在一个可恢复 maintenance job 中分块执行：

1. 从即将过期的 Full 组件构建 `data_kind=NativeV2Summary` 的独立 Segmentation/Work/Analysis/Projection Result Set：Segmentation Summary 保存 App/质量守恒摘要，Work/Analysis 保存仍需长期保留的 Work/Event/Decision Evidence Summary，Projection 保存 Daily/Hourly；不创建综合 Summary kind；
2. Projection Summary 的 dependency mask 和 `source_*_result_set_id` 指向本次新建的具体 Summary 组件；源 Segment 实体 ID可置空，但组件来源、Generation、水位和 validation digest 不得丢失；
3. 验证守恒、版本、复合 coverage、组件 FK、rebuild/explanation capability 后分别 Seal；
4. 发布新 Snapshot，以 NativeV2Summary Slice 替换过期 Full Slice；
5. 等旧 Snapshot lease/回滚保留期结束后，整组删除零引用的 Full Result Set，不对 Sealed Set 做行级原地修改；
6. 删除已无任何 Full Result Set 解释需要的过期 Observation、质量明细和 Safe Feature orphan；
7. 清理零引用 Generation/Identity Resolution，执行 FK/守恒检查并写 audit。

Active/Superseded Snapshot Slice 引用的 Result Set 不得删除；Result Set 引用的组件 Set、Generation 和 Identity Resolution 元数据不得删除。Sealed Result Set 的不可变性优先于行级 prune，保留降级通过“新的 NativeV2Summary 组件组 + Snapshot 替换”完成。

Clear History 必须明确范围：行为事实、App Identity/Identity Link、派生、读模型、Legacy、事件、Evidence 和旧 Snapshot。`command_receipts` 是操作幂等元数据，不属于历史清理范围；当前 Clear receipt 及其他未过期 receipt 均保留到统一 Receipt TTL 后由独立 Maintenance 清理。首版没有 `export_receipts` 表；未来导出若需要审计必须另建 `export_jobs`。

Clear 保留 Schema、Active Identity Generation 的密钥版本元数据、Settings、用户显式 Context 定义、空 runtime、当前 Clear receipt 和一条不含历史 payload 的 Clear audit；观察得到的 App Identity/Link/Resolution 必须删除。Clear 是普通 Snapshot 保留规则的显式例外：进入 Exclusive Maintenance 前 Desktop 必须关闭 reader、Agent 撤销相关 lease；事务创建零 Slice 的 `empty_reason=Cleared` Active Snapshot，`published_through_*` 为 NULL、`empty_at_fact_cursor` 记录清理前 last Fact Cursor，且数据库 `last_fact_cursor` 不回退；随后删除/不可恢复地撤销全部旧 Snapshot/Result Set。Clear 不创建零行 Projection Set，也不保留 24 小时回滚集合。失败整体回滚或按 chunk manifest 恢复，不能出现“UI 显示已清空但旧 Snapshot 仍可查”。

### 12.1 Snapshot lease 与 GC

- Agent 在线且 Writer Healthy 时每次查询通过 IPC 获取/续租 Snapshot Lease；候选 TTL 5 分钟；Agent 离线时不写 lease，也不存在 GC；Writer Degraded/Faulted 时使用 5.12 的 `GcSuspended` 分支；
- Superseded Snapshot 至少保留 24 小时且至少保留最近 2 个可回滚 Snapshot；这些数值保持候选并需容量评审；
- Snapshot 有未过期 lease、处于 Active、在最小回滚集合中或被 upgrade receipt 固定时不得 Collect；
- Desktop 崩溃通过 lease 到期释放，不依赖客户端显式关闭；
- GC 在一个 Writer maintenance 事务中重新检查条件，先删 Slice/Lease/Header，再删除零引用 Result Set，最后删除零引用 Generation；
- GC 每次分块并写 maintenance audit，避免无界保存每次实时发布产生的旧 Result Set。

## 13. 历史重建与原子切换

按领域模型的 W0/W1/W2 流程执行。新 Generation 在 staging 构建；Writer cutover barrier 只阻塞短时间提交，不停止 Capture Thread。验证通过后 Seal Result Set，新 Snapshot 复用旧清单未受影响 Slice 并替换受影响时间范围，随后原子激活；失败保持旧 Snapshot。

Writer Healthy 时，所有页面查询先获取 Snapshot Lease，并在**同一个 SQLite read-only connection 的显式 read transaction**中解析 Header/Slice 和执行该语义 command 的全部 SQL。Agent 离线或 Writer Degraded/Faulted 时，只有在确认 GC 不可能运行或已锁存 `GcSuspended` 后才走无 Lease 只读分支。SQLite WAL read transaction 固定物理视图；Sealed Result Set 不变保证后续分页即使使用新 read transaction，只要 Lease 有效或 GC suspension/恢复宽限仍有效并携带同一 Snapshot ID，仍能重现同一逻辑视图。

查询 SQL 必须以命中 Slice 的 `result_set_id` 过滤，不得只按 Generation 或日期读取；Projection 查询还必须验证 Slice 组件 ID 与 Projection dependency/source ID 完全相同。因此不会读到 published-through 之后的 staging 行或同 Generation 的较旧组件。Snapshot 激活不会改变进行中的读事务。Calendar/Identity Resolution 的投影追平由 Projection 的具体来源 Set、`sealed_through_fact_cursor` 和行内 `source_end_fact_cursor` 共同验证，而非 Generation 元数据本身。

## 14. 数据库文件替换

major migration/v1 import 使用版本化文件与受控 pointer：

```text
Desktop/Agent enter Maintenance
→ Desktop closes and drains reader pool
→ Agent stops and releases WAL/DB handles
→ migrator builds database-v2-<generation>.db.tmp
→ validate + fsync + atomic rename
→ atomically update trusted database pointer
→ Agent opens/migrates/verifies
→ Desktop recreates reader pool after Ready
```

Desktop 持有旧 reader 时不得替换。失败时 pointer 仍指旧库；新 Desktop 不自行猜测最新文件。Windows 上文件替换和回滚必须有集成测试。

## 15. v1 导入

- v1 库只读、先备份并记录 checksum；
- 有 Sample 的范围导入 Observation 后按 v2 规则重建；
- 只有 Session 的范围进入 `legacy_daily_summaries`；
- v1 PID、原始路径/标题不因导入新增持久化；
- 新盐只建立本机 v2 identity，不宣称与其他设备/盐一致；
- 导入报告比较计数、时间范围、App 分布、隐私、守恒和 FK；算法差异必须解释，不复制旧错误。

## 16. Schema migration 与 drift gate

Migration 命名 `0001_initial.sql`、`0002_name.sql`，嵌入 binary 并校验 checksum，只向前迁移，不自动降级或 DROP 不兼容数据。

### 16.1 持久化枚举域

首版稳定值如下；扩展值必须提升 Schema/协议 reader range。Writer 拒绝未知值，Reader 对新值使用安全 Unknown 显示，不猜测旧含义。

| Domain | 稳定值 |
|---|---|
| `activity_segment_close_reason` | `ActivityKeyChanged, StateChanged, Gap, PrivacyBoundary, Pause, Stop, Sleep, TimeEpochBreak, Clear, Maintenance, EndOfRange` |
| `context_segment_close_reason` | `ContextChanged, Gap, PrivacyBoundary, IdleBoundary, Pause, Stop, TimeEpochBreak, FinalizedRange` |
| `work_block_close_reason` | `IdleThreshold, Gap, PrivacyExcluded, Pause, Stop, Sleep, TimeEpochBreak, Clear, Maintenance, CrashRecovery, EndOfRange` |
| `capture_quality` | `Normal, TitleTimeout, ProcessUnavailable, PartialIdentity, LegacyImported` |
| `context_lifecycle_state` | `Active, Merged, Retired` |
| `derivation_job_kind` | `StreamingPublish, SegmentationRebuild, WorkRebuild, AnalysisRebuild, CalendarReprojection, V1Import, SchemaMigration, Prune, Clear, Checkpoint` |
| `maintenance_state` | `Accepted, Quiescing, Running, Validating, Succeeded, Failed, NeedsAttention` |
| `data_quality_kind` | `CaptureFailure, QueueDrop, OutOfOrder, TimeEpochBreak, PrivacyExcluded, AgentUnavailable, TitleUnavailable` |
| `safe_reason_code` | `None, Win32Timeout, ProcessAccessDenied, QueueFull, LateSequence, ClockRegression, PrivacyRuleMatched, UserCommand, ScheduledOff, SystemSignalMissing, StorageUnavailable` |

业务 Transition reason code 仍以[算法规范](./03-上下文识别与算法版本规范.md#8-决策原因码)为权威，不与运行质量 reason 混用。

### 16.2 机器可映射 manifest 门禁

本文已经逐列展开查询关键表，但 Markdown 不是最终机器输入。正式 DDL 冻结前必须新增并评审 `schema-v2-manifest.yaml`，逐表声明 SQLite type、nullability、default、CHECK、PK、FK/delete action、index 和 enum domain；该文件由生成器同时校验 migration 与 Rust row mapper。

在 manifest 尚未提交前，可以实现 migration harness 和原型表，但**不得冻结正式 DDL，也不得把 drift gate 标为通过**。这项门禁保留在 Draft 状态，避免把概括性 prose 冒充机器合同。

每次 PR 必须生成/维护“逻辑字段 → migration table.column/constraint/index”清单，并通过自动门禁：

- migration 从空库和每个支持的历史版本升级；
- `PRAGMA table_info/index_list/foreign_key_list` 与逻辑 manifest 一致；
- 必须字段、NULL、CHECK、UNIQUE、Composite FK 和 delete action 一致；
- Rust row mapper/DTO 不读取 manifest 外列；
- migration checksum 不漂移；
- `foreign_key_check`、守恒和 Query Snapshot 激活测试通过。

逻辑文档与 DDL 不一致时不得以“实现为准”合入，必须先修正文档或 migration。

## 17. 查询映射

| 用例 | 默认来源 |
|---|---|
| Today | Daily App/Work/Switch + Data Quality |
| Timeline | Activity/Context Segment；按需展开 Observation |
| Apps/Heatmap | Daily/Hourly App usage；按 Projection 固定的 Identity Resolution Generation 与 canonical App Identity |
| Context/Insights | Context usage + Work/Switch/Interruption + quality |
| 30 天/12 周 | finalized Daily 表，不扫描 Observation |
| Diagnostics | runtime/events/audits 的安全 DTO |

查询返回 Query Snapshot、命中 Slice、复合 Fact Boundary、published-through、Calendar/Identity Resolution、final 状态、质量、rebuild/explanation capability。NativeV2Summary 与 Legacy Summary 分别标识。空 Snapshot/空请求范围返回带 Header 元数据的 Empty，不创建假 Slice。Storage 层按 Snapshot 清单组合不重叠 Slice；React 不累加业务时长、合并同名 App 或选择 Generation/Result Set。

## 18. 性能与一致性门禁

候选目标：Today/30 天 p95 < 100 ms；12 周/7 天 Heatmap/Timeline 首页 p95 < 150 ms。必须用 90 天、1/3/10 秒采样、DST、频繁切换和至少两套 Generation 的合成库验证。

硬门禁：长期趋势不得扫描 Observation；普通 Writer p95 明显低于采样间隔；maintenance 不得持续饿死 Capture；Desktop reader 不得长期阻塞 checkpoint；rebuild 切换无混合 Snapshot；WAL/DB/索引增长有 24 小时和 90 天报告。

## 19. 测试分类

- Schema/migration：空库、历史库、checksum、磁盘不足、FK、drift manifest；
- Writer fault injection：每个事务步骤、busy/full/corrupt、tail rebuild、queue gap；
- Projection：具体组件 Result Set FK/dependency mask、App Identity Resolution、日期/小时守恒、DST、质量比例、Legacy 隔离；
- Snapshot/rebuild：W0/W1/W2、同毫秒复合 Fact Boundary、跨 Generation Slice、Sealed 不可变、同一 read transaction、失败保持旧快照、零 Slice 空快照；
- Retention/clear：90 天源删除后 DecisionSummary 仍可解释、Healthy/离线/Writer Degraded 三态 lease/GC、Receipt 保留、Clear 原子可恢复；
- Query：只读、cursor/limit、安全字段、30 天/12 周不读事实。
