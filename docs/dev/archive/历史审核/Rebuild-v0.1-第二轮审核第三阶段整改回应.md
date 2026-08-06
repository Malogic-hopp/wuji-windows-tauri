# Rebuild v0.1 第二轮审核第三阶段整改回应

回应对象：[Rebuild-v0.1-第二轮代码与验收审核报告.md](./Rebuild-v0.1-第二轮代码与验收审核报告.md)（下称"第二轮审核"）
前一阶段：[Rebuild-v0.1-第二轮审核第二阶段整改回应.md](./Rebuild-v0.1-第二轮审核第二阶段整改回应.md)
整改日期：2026-07-23
代码状态：HEAD `b42d2e2` + 第一轮整改工作区 + 第二轮第一/二阶段整改 + S2-04 部分返修（未提交）
权威顺序：AGENTS.md > 09 实施基线（含 §16）> 第二轮审核 > 本轮回应

## 1. 阶段范围

第三阶段记录第二轮审核 S2-04 的专项审核发现和返修设计。第二阶段完成的 S2-04 实现经专项审核判定为**不通过**，需要重新设计 pipeline barrier 的通道拓扑、标识体系和串行化协议。

截至本阶段开始前，已完成 S2-01、S2-02、S2-07、S2-08 共四项；S2-04 已撤回完成结论；S2-03、S2-06、S2-05 尚未开始。

## 2. S2-04 专项审核结论

第二阶段 S2-04 实现存在以下结构性缺陷，专项审核判定不通过：

### 2.1 双通道非单 FIFO

第二阶段的 barrier 通过独立 `barrier_rx` 通道从 command_server 送入 processor，再与 `capture_rx` 经 `tokio::select!` 合并。两个独立 mpsc channel 之间不存在 FIFO 保证：barrier 可能在已在途的 RawCapture 之前到达 processor，导致 barrier 之前的 backlog 未排空就切换了状态。

**要求**：Capture Loop 必须是 Capture→Processor 管道的唯一有序生产者。command_server 通过独立控制通道请求 Capture Loop 发出 Barrier，由 Capture Loop 自己把 Barrier 写入与 Sample 相同的 FIFO。Processor 只从一个 FIFO 顺序读取。

### 2.2 无唯一 Barrier 标识

第二阶段按 `BarrierKind`（Lifecycle / SettingsApplied）匹配 barrier，不携带唯一 ID。当同时存在两个同 kind barrier（如快速连点 Pause→Resume→Pause）时，Writer 无法区分，可能匹配到错误的 barrier。

**要求**：每个 Barrier 携带唯一 `BarrierId`（ULID）。`WriterControl` 携带相同的 `BarrierId` 和 `kind`。Writer 按 ID 精确匹配。

### 2.3 控制操作未统一串行化

第二阶段只对 Capture 命令使用 `capture_lock` 串行化，Lifecycle、Settings 和 reconciler 三条路径各自独立。Settings 切换期间没有冻结采集，reconciler 使用 `barrier_kind: None` 完全旁路 barrier 机制。

**要求**：所有控制操作（IPC Lifecycle、IPC settings_reload、reconciler）由同一个 transition coordinator 串行化。统一流程：冻结采集 → 创建唯一 BarrierId → 登记 WriterControl → 请求 Capture Loop 在 FIFO 中发出 Barrier → Writer 排空至 Barrier → 提交事务 → 更新共享状态 → 恢复采集。

### 2.4 Settings effectivity 未保证

第二阶段 `RawCapture.settings_revision` 仅携带 revision 值，Processor 使用当前 `settings_rx` 的值（可能与 revision 不匹配）处理样本。`PrivacyExcluded` 和 `CaptureError` 不携带 revision。ActivityEngine 不校验 Observation revision。SQLite 写入 revision 来自引擎而非消息。

**要求**：Settings 切换期间必须冻结采集。Barrier 前的样本按旧 Settings 处理并写旧 revision；Barrier 后的样本按新 Settings 处理并写新 revision。Processor 不得使用与 `RawCapture.settings_revision` 不匹配的当前 Settings。ActivityEngine 拒绝 revision 不一致的 Observation。`excludedProcessNames`、idle threshold、work threshold 全部遵守同一边界。

### 2.5 Barrier 失败语义缺失

第二阶段 barrier channel 满时使用 `try_send` 丢弃（静默失败），Writer 超时后仅记录错误但继续处理。Processor 退出时等待中的 barrier 无结果。

**要求**：Barrier channel Full/Closed 不得忽略。Writer 超时后保持安全冻结，不提交边界或 Settings。向 SharedState 记录来源明确的错误。提供可重试或显式恢复路径。Processor 或 Writer 退出时所有等待中的 Barrier 获得稳定失败结果。

### 2.6 测试未覆盖真实路径

第二阶段测试直接向 Writer `data_tx` 注入 `ProcessorOutput::Barrier`，只能作为 Writer 单元测试，不能证明完整 pipeline 中 barrier 正确传递。

**要求**：新增覆盖真实接线路径的 16 项确定性测试，涵盖 barrier 超车、同 kind 不同 ID、channel 满/关闭、processor 退出、writer 超时、in-flight capture、两级 queue drop、settings revision 切换、excludedProcessNames 切换、idle threshold 切换、revision 错配拒绝、reconciler 复用同一路径、失败后状态一致性。

## 3. S2-04 返修设计（待实现）

### 3.1 统一 FIFO 类型

```rust
// crates/wuji-core/src/pipeline.rs

pub type BarrierId = String;  // ULID (26 chars Crockford Base32)

pub struct BarrierToken {
    pub id: BarrierId,
    pub kind: BarrierKind,
    pub settings_revision: i64,
}

pub enum CapturePipelineItem {
    Sample(RawCapture),
    Barrier(BarrierToken),
}
```

### 3.2 通道拓扑

```
command_server ──(CaptureControl channel: mpsc::Sender<BarrierToken>)──→ capture_loop
reconciler     ──(CaptureControl channel: 同上)──────────────────────→ capture_loop

capture_loop ──(CapturePipelineItem FIFO: mpsc::Sender<CapturePipelineItem>)──→ processor

processor ──(ProcessorOutput FIFO: mpsc::Sender<ProcessorOutput>)──→ writer

command_server ──(control lane: mpsc::Sender<WriterControl>)──→ writer
reconciler     ──(control lane: 同上)─────────────────────────→ writer
```

Capture Loop 是 CapturePipelineItem FIFO 的唯一生产者。command_server 和 reconciler 通过 `CaptureControl` 通道请求 Capture Loop 发出 Barrier。Capture Loop 内部使用 `tokio::select! { biased; }`，barrier 请求优先。

### 3.3 CaptureCoordinator 统一串行化

新增 `apps/agent/src/capture_coordinator.rs`：

```rust
pub struct CaptureCoordinator {
    lock: tokio::sync::Mutex<()>,
    barrier_request_tx: mpsc::Sender<BarrierToken>,
    capture_state_tx: watch::Sender<CaptureState>,
    control_tx: mpsc::Sender<WriterControl>,
    shared: Arc<SharedState>,
    settings_tx: watch::Sender<Settings>,
    settings_path: PathBuf,
    settings_digest_for: fn(&Settings) -> String,
}

impl CaptureCoordinator {
    /// 统一入口：冻结采集 → barrier → 控制 → 提交 → 恢复。
    pub async fn apply_lifecycle(&self, event: EngineEvent) -> Result<AgentStatusDto, SafeError>;
    pub async fn apply_settings(&self, settings: Settings) -> Result<i64, SafeError>;
}
```

所有控制操作（IPC capture 命令、IPC settings_reload、reconciler）通过 `CaptureCoordinator` 串行化。内部流程：

1. 获取 `lock`（确保串行）
2. 冻结采集（`capture_state_tx.send(Stopped/Paused)`）
3. 生成唯一 `BarrierId`（ULID）
4. 构造 `BarrierToken { id, kind, settings_revision }`
5. 发送 `WriterControl::Lifecycle/SettingsApplied { barrier_id, barrier_kind, ... }` 到 control lane
6. `barrier_request_tx.send(token).await` 请求 Capture Loop 写入 FIFO
7. 等待 Writer ack（Lifecycle/Settings 提交结果）
8. 若成功：更新 settings_tx（如果是 settings 变更）
9. 根据用户/系统抑制状态决定是否恢复采集

### 3.4 WriterControl 变更

```rust
pub enum WriterControl {
    Lifecycle {
        event: EngineEvent,
        barrier_id: BarrierId,       // 新增：唯一匹配标识
        barrier_kind: BarrierKind,
        ack: oneshot::Sender<StorageResult<()>>,
    },
    SettingsApplied {
        settings: Settings,
        at_utc_ms: i64,
        barrier_id: BarrierId,       // 新增：唯一匹配标识
        barrier_kind: BarrierKind,
        ack: oneshot::Sender<StorageResult<i64>>,
    },
    // ... 其他变体不变
}
```

### 3.5 WriterTask drain_to_barrier 按 ID 匹配

```rust
async fn drain_to_barrier(
    &mut self,
    data_rx: &mut mpsc::Receiver<WriterDataMessage>,
    expected_id: &str,
    expected_kind: BarrierKind,
) -> StorageResult<()> {
    // 5 秒超时
    // 匹配条件：token.id == expected_id && token.kind == expected_kind
    // 不匹配的 barrier 保留在内部 pending_barriers 集合中
    // 超时返回错误，不提交边界
}

/// 提前到达的不同 barrier：登记到 pending，等待对应控制消费。
pending_barriers: HashMap<BarrierId, BarrierToken>,
```

当 barrier 在 WriterControl 之前到达 data lane 时，登记到 `pending_barriers`。当 `drain_to_barrier` 被调用时，先检查 `pending_barriers` 中是否已有匹配的 barrier。Barrier 不得在普通 data 分支中被跳过。

### 3.6 ActivityEngine Observation revision 校验

```rust
// activity.rs: on_observation()
fn on_observation(&mut self, writer: &mut Writer, obs: &FilteredObservation) -> Result<()> {
    // S2-04 返修：拒绝 revision 不匹配的 Observation
    if obs.settings_revision != self.settings_revision {
        return Err(StorageError::new(
            SafeErrorCode::SettingsConflict,
            "Observation revision 与引擎当前 revision 不匹配",
        ));
    }
    // ... 正常处理
}
```

### 3.7 Reconciler 复用同一路径

Reconciler 通过 `CaptureCoordinator::apply_settings()` 与 IPC `settings_reload` 走同一 barrier 路径，不再使用 `barrier_kind: None` 旁路。

## 4. 当前代码状态

S2-04 返修已开始但尚未完成。以下类型层变更已落实：

| 文件 | 状态 | 说明 |
|------|------|------|
| `crates/wuji-core/src/pipeline.rs` | 部分完成 | `BarrierToken`、`BarrierId`、`CapturePipelineItem` 类型已定义；`ProcessorOutput::Barrier(BarrierToken)` 已改为携带 token；`PrivacyExcluded`/`CaptureError` 已增加 `settings_revision` 字段 |
| `apps/agent/src/processor_task.rs` | 部分完成 | 已改为从单个 FIFO 读取 `CapturePipelineItem`；Barrier 透传到 writer data lane |
| `apps/agent/src/capture_loop.rs` | 待返修 | 尚未改为 `CapturePipelineItem` 输出 + `barrier_request_rx` 输入 |
| `apps/agent/src/command_server.rs` | 待返修 | 尚未替换 `BarrierMessage` 为 `CaptureCoordinator` 集成 |
| `apps/agent/src/writer_task.rs` | 待返修 | `drain_to_barrier` 尚未按 `BarrierId` 匹配；`pending_barriers` 尚未实现 |
| `apps/agent/src/activity.rs` | 待返修 | Observation revision 校验尚未实现 |
| `apps/agent/src/settings_reconciler.rs` | 待返修 | 尚未改为通过 `CaptureCoordinator` 走同一路径 |
| `apps/agent/src/main.rs` | 待返修 | 尚未创建 `CaptureCoordinator` 并接线 |
| `apps/agent/src/capture_coordinator.rs` | 未创建 | 新文件尚未开始 |
| 所有测试文件 | 待返修 | 16 项真实路径测试尚未开始 |

## 5. 返修任务清单

### 第一批：核心架构

| # | 任务 | 文件 |
|---|------|------|
| 1 | Capture Loop 改为 `CapturePipelineItem` 输出 + `barrier_request_rx` 输入 + biased select | `capture_loop.rs` |
| 2 | 新增 `CaptureCoordinator`，统一串行化 Lifecycle/Settings/reconciler | `capture_coordinator.rs`（新） |
| 3 | Processor 适配 `CapturePipelineItem` 输入（已完成） | `processor_task.rs` |
| 4 | WriterTask `drain_to_barrier` 按 `BarrierId` 匹配 + `pending_barriers` | `writer_task.rs` |
| 5 | WriterControl 增加 `barrier_id` 字段 | `writer_task.rs` |
| 6 | ActivityEngine Observation revision 校验 | `activity.rs` |
| 7 | command_server 改为通过 `CaptureCoordinator` 操作 | `command_server.rs` |
| 8 | Reconciler 改为通过 `CaptureCoordinator` 操作 | `settings_reconciler.rs` |
| 9 | main.rs 创建 `CaptureCoordinator` 并接线 | `main.rs` |

### 第二批：测试

| # | 测试场景 |
|---|---------|
| 1 | capture queue 积压多个 RawCapture + Barrier 同时到达 → 旧数据先于 Barrier |
| 2 | RawCapture 与 Barrier 同时 ready → 重复运行不发生 Barrier 超车 |
| 3 | Barrier 在 WriterControl 前到达 → 登记到 pending_barriers，不被 data 分支吞掉 |
| 4 | 同 kind 不同 ID 的 Barrier → 分别匹配正确控制 |
| 5 | Barrier channel 满 |
| 6 | Barrier channel 关闭 |
| 7 | Processor 在 Barrier 前退出 |
| 8 | Writer 等待 Barrier 超时 |
| 9 | In-flight Win32 capture 遇到 Pause/Stop |
| 10 | Capture queue 满载/drop 后 Barrier 仍可达 |
| 11 | Writer queue 满载/drop 后 Barrier 仍可达 |
| 12 | Settings revision 0→1：Barrier 前后 Observation revision 正确 |
| 13 | 切换 excludedProcessNames：边界前后隐私过滤严格对应 |
| 14 | 切换 Idle threshold：边界前后 ActivityState 严格对应 |
| 15 | Reconciler 使用与 IPC 相同的 Barrier 路径 |
| 16 | Observation revision 错配被拒绝 |
| 17 | Barrier 失败后共享状态、watch 状态和 Diagnostics 一致 |

### 第三批：文档

| # | 文档 |
|---|------|
| 1 | 撤回第二阶段回应中"S2-04 已完成"结论 |
| 2 | 返修完成后新增 `Rebuild-v0.1-第二轮审核第二阶段返修回应.md` |
| 3 | 更新 09 基线中仍使用 watermark 的旧描述 |

## 6. 验证命令（返修完成后执行）

```bash
cargo fmt --all -- --check
cargo clippy --workspace --all-targets -- -D warnings
cargo test -p wuji-rebuild-agent --lib
cargo test -p wuji-rebuild-agent --test settings_lifecycle
cargo test -p wuji-rebuild-agent --test writer_watermark
cargo test -p wuji-rebuild-agent --test ipc_protocol
cargo test --workspace
git diff --check
```

## 7. 推进建议

1. **先评审本设计**，确认 CaptureCoordinator API、通道拓扑和 BarrierId 生命周期与审核要求一致
2. 按第一批→第二批→第三批顺序实现
3. 第一批每完成一项立即 `cargo build` 验证，减少错误累积
4. 测试使用手动 scheduler、oneshot 或 channel 控制，不依赖随机调度或长时间 sleep
5. S2-04 返修通过专项复核前，不修改 migration-status 为完成，不开始 S2-03

---

**本轮没有 commit、push 或修改旧 WUJI/WUJI-Dev 数据库。** 旧 WPF/C#/Bridge 回滚入口保持不变。S2-04 专项审核发现的第一阶段已完成项（S2-01、S2-02、S2-07、S2-08）不受本阶段影响。
