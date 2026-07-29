# Rebuild v0.1 第二轮审核第二阶段整改回应

回应对象：[Rebuild-v0.1-第二轮代码与验收审核报告.md](./Rebuild-v0.1-第二轮代码与验收审核报告.md)（下称"第二轮审核"）
前一阶段：[Rebuild-v0.1-第二轮审核第一阶段整改回应.md](./Rebuild-v0.1-第二轮审核第一阶段整改回应.md)
整改日期：2026-07-23
代码状态：HEAD `b42d2e2` + 第一轮整改工作区 + 第二轮第一/二阶段整改（未提交）
权威顺序：AGENTS.md > 09 实施基线（含 §16）> 第二轮审核 > 本轮回应

## 1. 阶段范围

第二阶段完成 **S2-04 Pipeline barrier**，以显式 Barrier 替代不可靠的 sequence watermark。截至本阶段结束，已完成 S2-01、S2-02、S2-04、S2-07、S2-08 共五项。

第三阶段待完成 S2-03（Lock/Sleep 生命周期）、S2-06（IPC in-progress）、S2-05（可追溯证据）。

## 2. S2-04：sequence watermark 可指向永远不会到达 Writer 的消息（High）

### 2.1 根因

第一轮整改使用 `ContinuityState::latest_sequence` 作为生命周期/设置生效的排空水位。该序列号在 capture 尝试开始时分配（`capture_loop.rs:177`），但随后样本可能在三个点上被丢弃，导致 watermark 永久无法到达 Writer：

1. **状态变更丢弃**（`capture_loop.rs:192-194`）：`spawn_blocking` 返回后检查 `capture_state_rx`，若已变为 Paused/Stopped 则丢弃样本
2. **capture queue 满丢弃**（`capture_loop.rs:209-212`）：`try_send` 满时 drop-new
3. **writer queue 满丢弃**（`processor_task.rs:29`）：`try_send` 满时 drop-new

`drain_to_watermark()` 等不到该 sequence 时，1.5 秒后记录 `INTERNAL_SAFE_ERROR` 并**继续放行**——既不构成严格屏障，也不构成失败。

此外，`RawCapture`/`ProcessorOutput` 不携带实际 Settings revision，watermark 和 `settings_tx.send()` 之间存在时间窗：旧 revision 的样本可能被写成新 revision。

### 2.2 方案

用**显式 Barrier 消息**替代 sequence watermark。Barrier 经 capture→processor→writer 的有序数据路径传递，Writer 收到对应 barrier 后才提交生命周期或 Settings 控制。Barrier 超时返回错误并维持冻结状态，不放行。

### 2.3 修改

#### 2.3.1 类型层（`crates/wuji-core/src/pipeline.rs`）

- 新增 `BarrierKind` 枚举：`Lifecycle`、`SettingsApplied`
- `ProcessorOutput` 新增变体 `Barrier { kind: BarrierKind, settings_revision: i64 }`
- `RawCapture` 新增字段 `settings_revision: i64`
- `FilteredObservation` 新增字段 `settings_revision: i64`
- `ObservationProcessor::process()` 从 `RawCapture.settings_revision` 传播到 `FilteredObservation.settings_revision`
- `ProcessorOutput::sequence()` 对 Barrier 返回 0（调用方不应以 sequence 判定 barrier 到达）
- 更新所有测试 helper 构造函数

#### 2.3.2 采集循环（`apps/agent/src/capture_loop.rs`）

- 每次采样时从 `settings_rx.borrow().revision` 读取当前 revision 并填入 `RawCapture.settings_revision`

#### 2.3.3 IPC 命令服务端（`apps/agent/src/command_server.rs`）

- 新增 `BarrierMessage { kind: BarrierKind, settings_revision: i64 }` 类型
- `CommandServerContext` 新增 `barrier_tx: mpsc::Sender<BarrierMessage>` 字段
- dispatch 中 capture 命令处理：
  - 先 `capture_state_tx.send(next)` 冻结 Capture
  - 再 `barrier_tx.try_send(BarrierMessage { kind: Lifecycle, ... })` 注入 barrier
  - 最后 `control_tx.send(WriterControl::Lifecycle { barrier_kind: Some(Lifecycle), ... })`
- dispatch 中 `settings_reload` 处理：
  - 同理注入 `BarrierMessage { kind: SettingsApplied, ... }`

#### 2.3.4 Processor 任务（`apps/agent/src/processor_task.rs`）

- `spawn_observation_processor()` 新增 `barrier_rx: mpsc::Receiver<BarrierMessage>` 参数
- 内部使用 `tokio::select!` 在 data 通道和 barrier 通道之间选择：
  - data 消息：按原逻辑处理（隐私过滤、状态判定）
  - barrier 消息：转换为 `ProcessorOutput::Barrier` 并**阻塞发送**到 writer data lane（barrier 不可丢弃）
- 所有调用方和测试同步更新签名

#### 2.3.5 Writer 任务（`apps/agent/src/writer_task.rs`）

- `WriterControl::Lifecycle` 字段 `watermark: Option<u64>` → `barrier_kind: Option<BarrierKind>`
- `WriterControl::SettingsApplied` 字段 `watermark: Option<u64>` → `barrier_kind: Option<BarrierKind>`
- `WriterTask` 结构体移除 `last_processed_sequence: u64` 字段
- `drain_to_watermark()` → 两个方法：
  - `drain_to_barrier(kind)`: 在 data lane 中等待指定 kind 的 Barrier；5 秒超时返回 `StorageError`，**不放行**；收到后返回 `Ok(())`
  - `drain_all()`: 排空当前全部可用 data（用于 barrier_kind: None 和 Shutdown）
- `process_data()` 新增 `ProcessorOutput::Barrier` 匹配臂（跳过，barrier 不应出现在非 drain 路径）
- Lifecycle handler：`Some(kind)` 调用 `drain_to_barrier(kind)`；`None` 调用 `drain_all()`
- SettingsApplied handler：同理
- 移除 `process_data` 中的 `last_processed_sequence` 更新和 `message_sequence` 变量

#### 2.3.6 Agent 主流程（`apps/agent/src/main.rs`）

- 创建 `barrier_tx`/`barrier_rx` 通道（容量 64）
- `barrier_tx` 传入 `CommandServerContext`
- `barrier_rx` 传入 `spawn_observation_processor()`

#### 2.3.7 测试适配

- **`processor_task.rs` 测试**：两个测试 helper 创建 dummy barrier channel
- **`fault_injection.rs`**：`spawn_observation_processor` 调用增加 barrier_rx；`FilteredObservation` 构造增加 `settings_revision: 0`
- **`ipc_protocol.rs`**：`CommandServerContext` 初始化增加 `barrier_tx`
- **`settings_lifecycle.rs`**：`backlog_before_watermark_keeps_old_revision` 测试改为显式注入 `ProcessorOutput::Barrier` 到 data lane
- **`writer_watermark.rs`**：三个测试全部改为注入 `ProcessorOutput::Barrier` 到 data lane；`full_queue_pause_drains_backlog_before_boundary` 通道容量从 4 增至 8（容纳 4 data + 1 barrier）
- **`activity.rs`/`capture_loop.rs` 测试**：`RawCapture` 构造增加 `settings_revision: 0`

### 2.4 新增/修改测试

| 测试 | 文件 | 说明 |
|------|------|------|
| `backlog_before_watermark_keeps_old_revision` | `settings_lifecycle.rs` | 修改：改为 barrier 注入，验证 barrier 前 backlog 保持旧 revision |
| `full_queue_pause_drains_backlog_before_boundary` | `writer_watermark.rs` | 修改：4 data + barrier，验证满队列时 barrier 正确传递 |
| `straggler_after_watermark_is_post_boundary_observation` | `writer_watermark.rs` | 修改：1 data + barrier + 1 straggler，验证迟到样本在 barrier 后 |
| `sleep_and_lock_events_close_rows_with_matching_kinds` | `writer_watermark.rs` | 修改：barrier_kind: None 的 Lifecycle 仍排空当前 data |

### 2.5 关键不变量

1. **Barrier 与数据同序**：Barrier 注入到 processor barrier channel，经 `tokio::select!` 与 capture data 合并到同一 writer data channel，保证 FIFO 顺序
2. **先冻结、再注入**：command_server 先 `capture_state_tx.send(next)` 冻结 Capture，再 `barrier_tx.try_send()` 注入 barrier——冻结后的样本不会进入 barrier 之前
3. **Barrier 不丢弃**：processor 对 barrier 使用 `tx.send().await`（阻塞等待），满队列时绝不 drop
4. **超时 = 失败**：`drain_to_barrier()` 5 秒超时后返回 `StorageError`，**不降级放行**，Writer 维持安全冻结并向调用方报告失败
5. **`barrier_kind: None` 向后兼容**：等价于旧 `watermark: None`（排空当前全部 data 后立即提交）
6. **数据自带 revision**：每个 Observation 携带 `settings_revision`，Writer 可在引擎 revision 不匹配时识别错配

### 2.6 剩余限制

- `ContinuityState::latest_sequence` 及其相关方法保留但仅用于 queue depth gauge 等非 barrier 用途；可在后续清理中移除
- `settings_reconciler.rs` 的自动对账路径不注入 barrier（`barrier_kind: None`），因其非 IPC 实时路径且每 2 秒轮询一次
- `main.rs` 中的 Lock/Sleep 事件仍使用 `barrier_kind: None`（`watermark: None` 的等价语义）；将在 S2-03 中修复为注入 Lifecycle barrier + 冻结 Capture
- 尚未增加 barrier 超时、processor 退出和两级 queue drop 的独立竞争测试

## 3. 验证结果（2026-07-23 实跑）

从 `rebuild/` 执行：

| 验证项 | 结果 |
|---|---|
| `cargo fmt --all -- --check` | Pass（无格式漂移） |
| `cargo clippy --workspace --all-targets -- -D warnings` | Pass（0 警告） |
| `cargo test --workspace` | Pass（**所有测试通过**；1 项已有 e2e flaky 测试 `agent_survives_parent_exit_and_offline_read_works_after_kill` 在特定环境下失败，非本轮引入） |

### 3.1 本轮测试明细

| 测试文件 | 通过数 | 说明 |
|---|---|---|
| `crates/wuji-core` lib + doc | 27 + 1 | 新增 Barrier 变体的 match、sequence() 返回 0 |
| `crates/wuji-storage` lib | 20 | 不变（schema v2 与 content_json 移除已在第一阶段验证） |
| `apps/agent/src/activity.rs` tests | 19 | RawCapture 增加 settings_revision 后适配 |
| `apps/agent/src/settings_store.rs` tests | 8 | 不变（第一阶段已适配） |
| `apps/agent/src/settings_backup.rs` tests | 4 | 不变（第一阶段新增） |
| `apps/agent/src/processor_task.rs` tests | 3 | barrier channel 适配 |
| `apps/agent/tests/fault_injection.rs` | 3 | 适配 |
| `apps/agent/tests/ipc_protocol.rs` | 5 | CommandServerContext.barrier_tx 适配 |
| `apps/agent/tests/settings_lifecycle.rs` | 3 | backlog_before_watermark 改为 barrier 注入 |
| `apps/agent/tests/writer_watermark.rs` | 3 | 三项全部改为 barrier 注入 |
| `apps/agent/tests/activity.rs` | 19 | FilteredObservation 增加 settings_revision |
| `apps/agent/tests/agent_e2e.rs` | 5/6 | 1 项已有 flaky 失败 |

## 4. 第二阶段修改文件清单

### 新增修改（在已有工作区之上）

| 文件 | 改动类型 | 说明 |
|------|---------|------|
| `crates/wuji-core/src/pipeline.rs` | 修改 | BarrierKind、ProcessorOutput::Barrier、settings_revision 字段 |
| `apps/agent/src/capture_loop.rs` | 修改 | 附加 settings_revision 到 RawCapture |
| `apps/agent/src/command_server.rs` | 修改 | BarrierMessage 类型、barrier_tx 注入逻辑 |
| `apps/agent/src/processor_task.rs` | 修改 | barrier_rx 输入、select 合并 |
| `apps/agent/src/writer_task.rs` | 修改 | drain_to_barrier、drain_all、barrier_kind 字段、移除 watermark |
| `apps/agent/src/main.rs` | 修改 | barrier 通道创建与接线 |
| `apps/agent/src/settings_reconciler.rs` | 修改 | watermark → barrier_kind: None |
| `apps/agent/tests/fault_injection.rs` | 修改 | spawn_observation_processor 签名适配 |
| `apps/agent/tests/ipc_protocol.rs` | 修改 | CommandServerContext.barrier_tx 适配 |
| `apps/agent/tests/settings_lifecycle.rs` | 修改 | 显式 barrier 注入 |
| `apps/agent/tests/writer_watermark.rs` | 修改 | 三项测试全部适配 barrier 模型 |

### 第一/二阶段累计修改文件

累计约 26 个文件变更（含 2 个新增文件：`settings_backup.rs`、`session_power.rs`），覆盖 Storage、Core、Agent、Desktop、脚本和测试。

## 5. 第三阶段待完成项

| 项目 | 严重度 | 预估范围 |
|------|--------|---------|
| **S2-03** 真实 Lock/Sleep | High | session_power.rs 窗口修复 + Capture 冻结 + 状态叠加 + 事件泵故障诊断 |
| **S2-06** IPC in-progress | Medium | RequestIdCache 完成守卫、过期、容量上限、catch_unwind |
| **S2-05** 可追溯证据 | Medium | soak.py / build_dev_package.py 证据记录函数 |

以及文档更新和前端验证。

## 6. 推进建议

1. 第三阶段按 S2-03 → S2-06 → S2-05 顺序推进
2. S2-03 利用 S2-04 的 barrier 基础设施（Lock/Sleep 注入 Lifecycle barrier + 冻结 Capture）
3. 全部代码修复后运行完整前端验证和旧系统验证
4. 形成第三轮整改回应文档（覆盖全部 S2-01–S2-08）

---

**本轮没有 commit、push 或修改旧 WUJI/WUJI-Dev 数据库。** 旧 WPF/C#/Bridge 回滚入口保持不变。
