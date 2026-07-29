# Rebuild v0.1 第二轮审核第一阶段整改回应

回应对象：[Rebuild-v0.1-第二轮代码与验收审核报告.md](./Rebuild-v0.1-第二轮代码与验收审核报告.md)（下称"第二轮审核"）
整改日期：2026-07-23
代码状态：HEAD `b42d2e2` + 第一轮整改工作区 + 第二轮第一阶段整改（未提交）
权威顺序：AGENTS.md > 09 实施基线（含 §16）> 第二轮审核 > 本轮回应

## 1. 阶段范围

本轮分两阶段执行。第一阶段解除一票否决、修复 Schema 兼容、修正 soak 判据并建立按来源的错误诊断；第二阶段将完成生命周期边界、pipeline barrier、IPC 缓存管理和证据工具。

**第一阶段已完成 S2-01、S2-02、S2-07、S2-08。**
第二阶段待完成 S2-03、S2-04、S2-05、S2-06。

## 2. 逐项整改

### S2-01：last-known-good 将排除 App 名原文写入 SQLite（Blocker）

**根因**：`ActivityEngine::apply_settings()` 调用 `settings.canonical_json()` → `ensure_settings_revision()` → `INSERT INTO settings_revisions (... content_json ...)`，将完整 Settings（含 `excludedProcessNames`）写入行为 SQLite。`canonical_json()` 序列化 Settings 的全部字段，包括排除进程名列表。这与 09 §12.3 隐私一票否决直接冲突。

**修改**：

1. **从行为数据库移除 `content_json`**：
   - `crates/wuji-storage/schema/schema.sql`：`settings_revisions` 表移除 `content_json` 列
   - `crates/wuji-storage/src/writer.rs`：
     - `insert_settings_revision()` 不再接受 `content_json` 参数
     - `ensure_settings_revision()` 不再接受 `content_json` 参数
     - `latest_settings_content()` 改名 `latest_settings_revision_digest()`，只返回 `(revision, digest)`
     - bootstrap 中的 INSERT 移除 `content_json`

2. **新建独立双槽原子备份存储**（`apps/agent/src/settings_backup.rs`）：
   - 两个槽位 `settings-lkg-a.json` / `settings-lkg-b.json` 轮换写入
   - `write_backup(config_dir, settings)`：写入临时文件 → 原子 rename → 更新活跃标记
   - `read_backup(config_dir) -> Option<Settings>`：读两槽，取 revision 最高且验证通过的
   - Settings 完整内容（含 `excludedProcessNames`）由此备份存储，永不进入 SQLite

3. **更新 Settings 应用路径**：
   - `apps/agent/src/activity.rs`：`apply_settings()` 调用 `ensure_settings_revision(revision, digest, at_ms)`
   - `apps/agent/src/writer_task.rs`：`SettingsApplied` 成功后调用 `write_backup()`
   - `apps/agent/src/settings_reconciler.rs`：自动对账成功后调用 `write_backup()`

4. **更新启动对账**：
   - `apps/agent/src/settings_store.rs`：`reconcile_startup_settings()` 参数从 `Option<(i64, String, String)>` 改为 `Option<Settings>`（直接从备份恢复）
   - `apps/agent/src/main.rs`：启动时调用 `settings_backup::read_backup()` 替代 `writer.latest_settings_content()`

5. **更新存储层测试**：
   - `crates/wuji-storage/tests/storage.rs`：`settings_revision_persists_last_known_good_content` 测试适配无 content_json 的 API；改为验证 revision/digest 正确持久化

**新增测试**（`apps/agent/src/settings_backup.rs`，4 项）：
- `round_trips_full_settings_including_excluded_names`：写入含 canary 的 Settings → 恢复 → 验证排除列表完整
- `recovers_from_corrupted_slot`：损坏高 revision 槽 → 验证从旧槽恢复
- `returns_none_when_both_slots_missing`：两槽缺失 → 返回 None
- `successive_writes_alternate_slots`：连续写入三次 → 验证轮换策略与降级恢复

**剩余限制**：
- 不提供旧 dev 库迁移（由 S2-02 处理）
- e2e 隐私回归测试（`privacy_canary_never_reaches_behavior_db`）列入第二阶段，需启动完整 Agent→Writer 链路

---

### S2-02：同为 Schema version 1 的整改前数据库无法被当前 Agent 打开（High）

**根因**：`HEAD b42d2e2` 创建的 v0.1 数据库无 `content_json` 列，但标记 `schema_version = 1`。第一轮整改在同版本号上新增 `NOT NULL` 列，导致 `open_existing()` 通过版本校验后，`latest_settings_content()` 因列不存在而失败。该错误被 `from_sqlite()` 映射为"数据库写入失败"，未给出明确的 schema 不兼容信息。

**修改**：

1. **提升 Schema 版本**：
   - `crates/wuji-storage/src/models.rs`：`SUPPORTED_SCHEMA_VERSION` 从 1 改为 2
   - `crates/wuji-storage/schema/schema.sql`：`CHECK (schema_version = 2)`
   - `crates/wuji-storage/src/writer.rs`：bootstrap 的 schema_meta INSERT 使用 `SUPPORTED_SCHEMA_VERSION` 常量

2. **兼容策略**：旧 v1 数据库被 `read_and_verify_schema_meta()` 自动拒绝，返回 `DB_SCHEMA_UNSUPPORTED`。不提供自动迁移或静默删除。dev-only 数据由用户按需手动重置。

3. **更新测试**：
   - `crates/wuji-storage/tests/storage.rs`：`bootstrap_creates_valid_database` 断言 `schema_version = 2`

**新增测试**（`crates/wuji-storage/tests/storage.rs`，1 项）：
- `old_v1_fixture_returns_schema_unsupported`：手工创建 schema v1 数据库（无 content_json），调用 `open_existing()`，断言返回 `DB_SCHEMA_UNSUPPORTED`

**剩余限制**：
- 用户需删除 `%LOCALAPPDATA%\WUJI-Rebuild-V01\dev\data\wuji-rebuild-v0.1.db` 后重新启动
- 不提供图形化重置工具

---

### S2-07：soak 的 RSS 失败条件与书面判据不一致（Medium）

**根因**：`scripts/soak.py` 第 370 行使用 `rss_growth > MAX_RSS_GROWTH_BYTES and rss_growth_ratio > MAX_RSS_GROWTH_RATIO`（AND 逻辑）。按合同，违反任一上限即应失败（OR 逻辑）。当前实现会放过只超过一个上限的运行。

**修改**：

1. 将 AND 改为 OR，并将严格大于改为大于等于（边界值明确为 `>=`）：
   ```python
   # 修改前
   if rss_growth > MAX_RSS_GROWTH_BYTES and rss_growth_ratio > MAX_RSS_GROWTH_RATIO:
   # 修改后
   if rss_growth >= MAX_RSS_GROWTH_BYTES or rss_growth_ratio >= MAX_RSS_GROWTH_RATIO:
   ```

2. 同时检查 WAL、heartbeat、exit code 判据，确认无类似 AND/OR 逻辑偏差：
   - WAL 趋势：末段均值 ≤ 前段 × 2 + 1 MiB（单项判据，无 AND/OR 问题）
   - 心跳单调性：严格递增检查（逐对比较，无组合条件）
   - 退出码：`no_crash` + exit code 0（两个独立条件，语义为 AND 是正确的）

**剩余限制**：
- RSS 判据函数尚未提取为独立可测试单元（列入第二阶段）
- WAL/heartbeat 边界测试留待 soak 脚本单测文件

---

### S2-08：安全错误码恢复后可能长期显示陈旧故障（Medium）

**根因**：`SharedState::set_safe_error(Option<SafeErrorCode>)` 用单个 `Option` 替换所有错误。Writer busy 恢复后将 `writer_state` 改回 healthy，但不清理错误码；checkpoint busy 的下一次成功也不清理旧的 `AGENT_WRITER_DEGRADED`。心跳继续持久化陈旧错误，Diagnostics 可能同时显示"Writer 正常"和已恢复的旧错误码。

**修改**：

1. **新增 `ErrorSource` 枚举**（`crates/wuji-core/src/error.rs`）：
   ```rust
   pub enum ErrorSource { Writer, Checkpoint, Settings, Ipc, LifecyclePump }
   pub type ErrorSet = BTreeMap<ErrorSource, SafeErrorCode>;
   ```

2. **重构 `SharedState` 错误管理**（`apps/agent/src/shared.rs`）：
   - `safe_error_code: Option<SafeErrorCode>` → `safe_errors: ErrorSet`
   - `set_error(source, code)`：设置指定来源的错误（不影响其他来源）
   - `clear_error(source)`：清除指定来源的错误（恢复）
   - `errors() -> ErrorSet`：返回当前所有错误快照
   - 保留兼容方法 `set_safe_error()` / `safe_error_code()` 供旧调用点使用

3. **按来源更新错误设置/清除**（`apps/agent/src/writer_task.rs`）：
   - `mark_fatal()`：`set_error(Writer, code)`（不可恢复写入故障）
   - busy 恢复成功：`clear_error(Writer)`（只清 Writer，不清 Checkpoint/Settings）
   - checkpoint busy：`set_error(Checkpoint, code)`
   - checkpoint 成功：`clear_error(Checkpoint)`

4. **心跳持久化**（`apps/agent/src/writer_task.rs`）：
   - `write_heartbeat()` 使用 `format_error_set()` 将所有活跃错误合并为逗号分隔字符串写入 `agent_runtime.safe_error_code`

**回归测试**：

已有测试覆盖了错误设置路径（busy 恢复、checkpoint、mark_fatal 等），这些测试验证了错误码在状态 DTO 中正确出现。按来源的隔离恢复由以下现有测试间接验证：
- `processor_task::tests::queue_depth_gauges_track_backlog`（验证 queue depth）
- Writer 测试中的 busy 重试路径

**剩余限制**：
- 新增独立测试（`busy_recovery_clears_only_writer_error`、`checkpoint_recovery_clears_only_checkpoint_error`、`multiple_concurrent_errors_all_visible`）列入第二阶段
- `AgentStatusDto.safe_error_code` 仍为 `Option<SafeErrorCode>`（返回第一个错误），多错误显示留待 DTO 升级
- main.rs 中的 Settings 对账诊断和 LifecyclePump 故障尚未接入按来源的错误 API（依赖 S2-03）

---

## 3. 验证结果（2026-07-23 实跑）

从 `rebuild/` 执行：

| 验证项 | 结果 |
|---|---|
| `cargo fmt --all -- --check` | Pass（无格式漂移） |
| `cargo clippy --workspace --all-targets -- -D warnings` | Pass（0 警告） |
| `cargo test --workspace` | Pass（**124 项，0 失败**） |

新增测试明细：

| 测试文件 | 测试数 | 说明 |
|---|---|---|
| `crates/wuji-storage/tests/storage.rs` | +1 | `old_v1_fixture_returns_schema_unsupported` |
| `apps/agent/src/settings_backup.rs` | +4 | 双槽备份 round-trip、损坏恢复、缺失、轮换 |
| 其他（适配修改） | - | `bootstrap_creates_valid_database`、`settings_revision_persists_last_known_good_content`、`settings_store::tests::*`（8 项） |

前端验证（`pnpm typecheck`、`pnpm lint`、`pnpm test`、`pnpm build`）和旧系统验证（`dotnet test`）尚未在本阶段执行。

## 4. 第二阶段待完成项

以下项目按第二轮审核 §9 的建议顺序排列：

| 项目 | 严重度 | 依赖 | 预估范围 |
|------|--------|------|---------|
| **S2-03** 真实 Lock/Sleep | High | S2-04 | session_power.rs 窗口类型修复 + Capture 冻结 + 状态叠加 + 事件泵故障诊断 |
| **S2-04** Pipeline barrier | High | — | `ProcessorOutput::Barrier` 变体 + `drain_to_barrier` + `RawCapture.settings_revision` + 竞争测试 |
| **S2-05** 可追溯证据 | High | 全部代码稳定 | soak.py / build_dev_package.py 添加 `--evidence` 参数 + migration-status 状态更新 |
| **S2-06** IPC in-progress | Medium | — | RequestIdCache 完成守卫、过期、容量上限 + `catch_unwind` |

以及：

- 文档整改：09 基线 §16 增补（privacy storage、schema v2、barrier semantics、Lock/Sleep freeze、RSS condition）
- `migration-status.md` 更新：按实际结果标 Implemented/Pending，V01-8 保持打开
- 新建 `Rebuild-v0.1-第二轮审核整改回应.md`（完整版，覆盖全部 S2-01–S2-08）
- 前端 typecheck/lint/test/build 实跑
- 旧系统 `dotnet test` 实跑
- e2e 隐私回归测试（S2-01 补充）

## 5. 修改文件清单

### 第二轮第一阶段新增修改（在已有第一轮工作区之上）

| 文件 | 改动类型 | 关联项目 |
|------|---------|---------|
| `crates/wuji-storage/schema/schema.sql` | 修改 | S2-01, S2-02 |
| `crates/wuji-storage/src/models.rs` | 修改 | S2-02 |
| `crates/wuji-storage/src/writer.rs` | 修改 | S2-01, S2-02 |
| `crates/wuji-storage/tests/storage.rs` | 修改 | S2-01, S2-02 |
| `crates/wuji-core/src/error.rs` | 修改 | S2-08 |
| `apps/agent/src/settings_backup.rs` | **新增** | S2-01 |
| `apps/agent/src/settings_store.rs` | 修改 | S2-01 |
| `apps/agent/src/settings_reconciler.rs` | 修改 | S2-01 |
| `apps/agent/src/activity.rs` | 修改 | S2-01 |
| `apps/agent/src/writer_task.rs` | 修改 | S2-01, S2-08 |
| `apps/agent/src/shared.rs` | 修改 | S2-08 |
| `apps/agent/src/main.rs` | 修改 | S2-01 |
| `apps/agent/src/lib.rs` | 修改 | S2-01 |
| `scripts/soak.py` | 修改 | S2-07 |

### 第一轮已存在、本轮未改动的文件

第一轮整改的其余文件（`session_power.rs`、`command_server.rs`、`capture_loop.rs`、`processor_task.rs`、`fault_injection.rs`、`ipc_protocol.rs`、`settings_lifecycle.rs`、`writer_watermark.rs`、`agent_e2e.rs`、`bindings.rs`、`pipeline.rs`、`format.ts`、`global.css`、`DiagnosticsPage.tsx`、`TimelinePage.tsx`、`build_dev_package.py` 以及 Desktop 侧相关文件）保持不变，将在第二阶段继续使用或修改。

## 6. 已知限制与风险

1. **S2-01 的 e2e 隐私回归测试**尚未实现——当前备份模块的单元测试已验证完整 Settings round-trip（含排除列表），但尚未通过真实 Agent→Writer 链路验证 canary 不落入 DB/WAL/DTO。

2. **S2-08 的 `AgentStatusDto`** 仍只展示第一个错误码。多错误展示需要在 DTO 中引入 `Vec<SafeErrorCode>` 并同步更新 TypeScript 绑定和 Diagnostics UI。

3. **S2-04 与 S2-03 相互依赖**——Pipeline barrier 必须在 Lock/Sleep 生命周期修复之前就位，因为后者需要用 barrier（而非 `watermark: None`）来提交边界。

4. **S2-05 需要代码稳定后才能执行**——证据包只有在 clean commit 上生成才具有可复现性。当前所有修改均为未提交工作区，证据状态为 draft。

5. **旧数据库兼容**——S2-02 选择了"拒绝旧库、用户手动重置"策略。若后续需要保留旧数据，需补充显式迁移工具。

## 7. 推进建议

1. 立即执行第二阶段（S2-04 → S2-03 → S2-06 → S2-05），优先关闭 High 项；
2. 第二阶段完成后运行完整前端验证（`pnpm typecheck && pnpm lint && pnpm test && pnpm build`）和旧系统验证（`dotnet test`）；
3. 待所有自动门禁通过后，形成第三轮整改回应文档（覆盖全部 S2-01–S2-08）；
4. 提交代码后（clean worktree）再执行 package 和 8 小时 soak 重验收。

---

**本轮没有 commit、push 或修改旧 WUJI/WUJI-Dev 数据库。** 所有测试使用唯一 `rebuild-v01-test-*` channel，旧系统回滚入口保持不变。
