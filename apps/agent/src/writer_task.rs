//! Single SQLite Writer 任务：data/control 双 lane、biased select、故障策略（09 §5.2）。
//!
//! - data lane（512）：ProcessorOutput；满时 drop-new（生产侧已处理 epoch/计数器）。
//! - control lane（64）：生命周期、settings applied、heartbeat、checkpoint、shutdown；
//!   发送方等待容量，不得静默丢弃；Writer 优先消费。
//! - 生命周期控制先排空 data backlog 再应用，保证因果顺序。
//! - busy：busy_timeout 后再以 100/250ms 重试 2 次，引擎整体回滚重放；
//!   持续失败停止 Capture 并进入 faulted（IPC 保持在线）。
//! - 其他 I/O 错误：停止 Capture、标记 faulted，不自动修复数据库。

use std::sync::Arc;

use tokio::sync::{mpsc, oneshot, watch};
use wuji_core::domain::{CaptureState, ProcessState, WriterState};
use wuji_core::error::SafeErrorCode;
use wuji_core::pipeline::ProcessorOutput;
use wuji_core::settings::Settings;
use wuji_storage::Writer;
use wuji_storage::error::{Result as StorageResult, StorageError};

use crate::activity::{ActivityEngine, EngineEvent};
use crate::capture_loop::ContinuityState;
use crate::shared::SharedState;

/// data lane 消息就是 ProcessorOutput（内部已携带 epoch）。
pub type WriterDataMessage = ProcessorOutput;

/// pending 规则（阶段 4.2，审核 P1-03）：按 BarrierId 索引、有界、可过期、可诊断。
pub const PENDING_BARRIER_CAPACITY: usize = 64;
/// pending TTL：远长于 drain 等待（5s）与 IPC timeout（3s）。
pub const PENDING_BARRIER_TTL: std::time::Duration = std::time::Duration::from_secs(30);

/// pending 登记结果。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PendingRegister {
    /// 新条目。
    Registered,
    /// 完全相同的重复 Barrier：保留首条，不视为错误（重传幂等）。
    Duplicate,
    /// 同 ID 但 kind 或 expected_revision 不同：冲突，ID 进入毒化状态。
    Conflict,
    /// 该 ID 此前已冲突：TTL 到期前不得再次登记（复审 P2-01 防洗白）。
    Poisoned,
    /// 超出容量：拒绝登记。
    Overflow,
    /// 全局饱和降级（第三轮复审 P1-01）：满容量时发生未知 ID 冲突后，
    /// TTL 内拒绝全部新 ID，防止"释放槽位后洗白"。
    Saturated,
}

/// pending 消费结果。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PendingTake {
    /// ID + kind + expected_revision 三要素匹配，已取走。
    Matched,
    /// 不存在对应条目。
    Absent,
    /// ID + kind 匹配但 expected_revision 不符：冲突并毒化，绝不应用。
    RevisionConflict,
    /// ID 匹配但 kind 不符：冲突并毒化（复审 P2-01：不再静默等待超时）。
    KindConflict,
    /// 该 ID 已毒化：立即失败，不得消费。
    Poisoned,
}

/// Shutdown 遗留汇总（阶段 4.3.1 §二D：可测试的纯数据证据）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PendingSummary {
    /// 未消费的 pending 数量。
    pub pending: usize,
    /// poisoned tombstone 数量。
    pub poisoned: usize,
    /// 是否处于全局饱和降级。
    pub saturated: bool,
}

/// pending 状态（复审 P1-01：普通条目与 tombstone 共用同一个严格总容量）。
enum PendingState {
    Pending(PendingEntry),
    Poisoned(tokio::time::Instant),
}

struct PendingEntry {
    token: wuji_core::pipeline::BarrierToken,
    inserted_at: tokio::time::Instant,
}

/// 按 BarrierId 索引的有界 pending（阶段 4.2；复审 P2-01 增加毒化 tombstone；
/// 复审 P1-01 统一 Pending/Poisoned 总容量；第三轮复审增加饱和降级）。
///
/// 容量规则（总状态数 ≤ PENDING_BARRIER_CAPACITY）：
/// - 全新 ID 登记：满 → Overflow，拒绝并诊断；
/// - 已有 entry 转 poisoned：原地状态转换，不增长，不受容量限制；
/// - 毒化不在表中的 ID 且容量满：记录**全局饱和**（O(1)），TTL 内拒绝全部新 ID，
///   防止"释放槽位后洗白"；TTL 到期自动恢复正常（第三轮复审 P1-01）。
#[derive(Default)]
pub struct PendingBarriers {
    states: std::collections::HashMap<wuji_core::pipeline::BarrierId, PendingState>,
    /// 全局饱和截止时刻：Some(t) 且 t 未过期时拒绝全部新 ID。
    saturated_until: Option<tokio::time::Instant>,
}

impl PendingBarriers {
    /// 清除过期条目与过期 tombstone，返回清除数量；同时解除过期饱和。
    fn purge_expired(&mut self) -> usize {
        let now = tokio::time::Instant::now();
        if self.saturated_until.is_some_and(|until| now >= until) {
            self.saturated_until = None;
        }
        let before = self.states.len();
        self.states.retain(|_, state| {
            let at = match state {
                PendingState::Pending(entry) => entry.inserted_at,
                PendingState::Poisoned(at) => *at,
            };
            now.duration_since(at) < PENDING_BARRIER_TTL
        });
        before - self.states.len()
    }

    /// 总状态数（pending + poisoned，复审 P1-01 统一口径）。
    pub fn len(&self) -> usize {
        self.states.len()
    }

    /// 是否为空。
    pub fn is_empty(&self) -> bool {
        self.states.is_empty()
    }

    /// 毒化 tombstone 数量（Shutdown 报告用）。
    pub fn poisoned_count(&self) -> usize {
        self.states
            .values()
            .filter(|s| matches!(s, PendingState::Poisoned(_)))
            .count()
    }

    /// 是否处于全局饱和降级（Shutdown 报告与测试用）。
    pub fn is_saturated(&self) -> bool {
        let now = tokio::time::Instant::now();
        self.saturated_until.is_some_and(|until| now < until)
    }

    /// 遗留汇总（Shutdown 报告与测试证据；总状态数恒 ≤ PENDING_BARRIER_CAPACITY）。
    pub fn summary(&self) -> PendingSummary {
        PendingSummary {
            pending: self.len() - self.poisoned_count(),
            poisoned: self.poisoned_count(),
            saturated: self.is_saturated(),
        }
    }

    /// 毒化 ID：已有 entry 原地转换；不在表中时优先插入 tombstone；
    /// 容量满则记录全局饱和（返回 false，由调用方诊断）。
    pub fn poison(&mut self, id: &wuji_core::pipeline::BarrierId) -> bool {
        self.purge_expired();
        if self.states.contains_key(id) {
            self.states.insert(
                id.clone(),
                PendingState::Poisoned(tokio::time::Instant::now()),
            );
            return true;
        }
        if self.states.len() >= PENDING_BARRIER_CAPACITY {
            // 无法为该 ID 记录 tombstone：进入全局饱和降级，TTL 内拒绝全部新 ID，
            // 保证"冲突 ID 在 TTL 内不得洗白"而不突破容量（第三轮复审 P1-01）。
            self.saturated_until = Some(tokio::time::Instant::now() + PENDING_BARRIER_TTL);
            return false;
        }
        self.states.insert(
            id.clone(),
            PendingState::Poisoned(tokio::time::Instant::now()),
        );
        true
    }

    /// 登记 Barrier（确定性规则见 PendingRegister；测试可直接驱动）。
    pub fn register(&mut self, token: wuji_core::pipeline::BarrierToken) -> PendingRegister {
        self.purge_expired();
        match self.states.get(&token.id) {
            Some(PendingState::Poisoned(_)) => PendingRegister::Poisoned,
            Some(PendingState::Pending(existing)) => {
                if existing.token.kind == token.kind
                    && existing.token.expected_revision == token.expected_revision
                {
                    return PendingRegister::Duplicate;
                }
                // 同 ID 不同 kind/revision：原地毒化（不增长，不受容量限制）。
                self.poison(&token.id);
                PendingRegister::Conflict
            }
            None => {
                if self.is_saturated() {
                    return PendingRegister::Saturated;
                }
                if self.states.len() >= PENDING_BARRIER_CAPACITY {
                    return PendingRegister::Overflow;
                }
                self.states.insert(
                    token.id.clone(),
                    PendingState::Pending(PendingEntry {
                        token,
                        inserted_at: tokio::time::Instant::now(),
                    }),
                );
                PendingRegister::Registered
            }
        }
    }

    /// 按 ID + kind + expected revision 三要素取走匹配项（测试可直接驱动）。
    pub fn take_if_matches(
        &mut self,
        id: &wuji_core::pipeline::BarrierId,
        kind: wuji_core::pipeline::BarrierKind,
        expected_revision: i64,
    ) -> PendingTake {
        self.purge_expired();
        match self.states.get(id) {
            None => PendingTake::Absent,
            Some(PendingState::Poisoned(_)) => PendingTake::Poisoned,
            Some(PendingState::Pending(entry)) if entry.token.kind != kind => {
                self.poison(id);
                PendingTake::KindConflict
            }
            Some(PendingState::Pending(entry))
                if entry.token.expected_revision != expected_revision =>
            {
                self.poison(id);
                PendingTake::RevisionConflict
            }
            Some(PendingState::Pending(_)) => {
                self.states.remove(id);
                PendingTake::Matched
            }
        }
    }
}

pub enum WriterControl {
    /// 生命周期边界（Pause/Stop/Sleep/Lock）。ack 在事务提交后返回（09 §5.2）。
    /// S2-04 返修：barrier_id 精确匹配 Barrier；阶段 4.2：三要素匹配（ID+kind+revision）。
    Lifecycle {
        event: EngineEvent,
        barrier_id: wuji_core::pipeline::BarrierId,
        expected_revision: i64,
        ack: oneshot::Sender<StorageResult<()>>,
    },
    /// Agent 已应用新 settings（09 §9.1）。
    /// S2-04 返修：barrier_id 精确匹配 Barrier；阶段 4.2：三要素匹配。
    SettingsApplied {
        settings: Settings,
        at_utc_ms: i64,
        barrier_id: wuji_core::pipeline::BarrierId,
        expected_revision: i64,
        ack: oneshot::Sender<StorageResult<i64>>,
    },
    /// 心跳（每秒；携带 epoch 与队列观测，09 §5.2）。
    Heartbeat(HeartbeatSnapshot),
    /// WAL checkpoint（MaintenanceLite 唯一维护动作，09 §5）。
    Checkpoint,
    /// 受控退出：关闭 open 行、提交终态后退出进程。
    Shutdown { ack: oneshot::Sender<()> },
}

#[derive(Debug, Clone)]
pub struct HeartbeatSnapshot {
    pub heartbeat_at_utc_ms: i64,
    pub last_observation_at_utc_ms: Option<i64>,
    pub capture_queue_depth: i64,
    pub writer_queue_depth: i64,
    pub dropped_capture_count: i64,
    pub dropped_writer_count: i64,
    pub continuity_epoch: i64,
}

pub struct WriterTask {
    writer: Writer,
    engine: ActivityEngine,
    shared: Arc<SharedState>,
    capture_state_tx: watch::Sender<CaptureState>,
    continuity: Arc<ContinuityState>,
    /// 双槽备份目录（crash-consistent 顺序的第一步写入点，审核 P1-01）。
    settings_backup_dir: std::path::PathBuf,
    /// S2-04 返修：提前到达的 Barrier（登记到 pending，等待对应 control 消费）。
    /// 阶段 4.2：按 ID 索引、有界、TTL、重复/冲突/overflow 规则。
    pending_barriers: PendingBarriers,
    /// 阶段 4.4（P1-04）：revision 协议违例锁存。一旦置位，后续全部数据
    /// 消息（Observation/PrivacyExcluded/CaptureError，无论 revision 是否
    /// 匹配）一律拒绝，本进程内不解除；ActivityEngine/SQLite 零副作用。
    protocol_violation: bool,
    /// 生产任务健康句柄（复审 P1-02：run 进入时登记 RAII 守卫）。
    health: Arc<crate::pipeline_health::PipelineHealth>,
}

impl WriterTask {
    pub fn new(
        writer: Writer,
        engine: ActivityEngine,
        shared: Arc<SharedState>,
        capture_state_tx: watch::Sender<CaptureState>,
        continuity: Arc<ContinuityState>,
        settings_backup_dir: std::path::PathBuf,
        health: Arc<crate::pipeline_health::PipelineHealth>,
    ) -> Self {
        Self {
            writer,
            engine,
            shared,
            capture_state_tx,
            continuity,
            settings_backup_dir,
            pending_barriers: PendingBarriers::default(),
            protocol_violation: false,
            health,
        }
    }

    /// 阶段 4.4 复审补修 P1-01：协议违例锁存后的稳定拒绝错误。
    /// protocol_violation 为 true 时返回 SETTINGS_CONFLICT；
    /// 当前进程内只能通过 Agent 重启恢复，不可解除。
    fn protocol_fault_error(&self) -> Option<StorageError> {
        if self.protocol_violation {
            Some(StorageError::new(
                SafeErrorCode::SettingsConflict,
                "流水线 revision 协议违例已锁存，拒绝提交控制边界",
            ))
        } else {
            None
        }
    }

    /// 阶段 4.4 复审补修 P1-01：控制边界提交前的协议健康守卫。
    /// Lifecycle/SettingsApplied 在 drain 成功后、产生副作用前的 belt-and-suspenders。
    fn ensure_protocol_healthy(&self) -> StorageResult<()> {
        if let Some(error) = self.protocol_fault_error() {
            Err(error)
        } else {
            Ok(())
        }
    }

    pub fn into_parts(self) -> (Writer, ActivityEngine) {
        (self.writer, self.engine)
    }

    /// 生产运行入口（第二次复审 P1）：返回前先**同步**注册 Writer 健康
    /// （`compare_exchange(NotStarted → Alive)`），guard 由返回的 future 捕获——
    /// 正常返回、panic、poll 后 abort、首次 poll 前 abort 都会置 Dead。
    /// main 与生产拓扑测试统一使用 `tokio::spawn(task.into_run_future(..))`。
    pub fn into_run_future(
        self,
        data_rx: mpsc::Receiver<WriterDataMessage>,
        control_rx: mpsc::Receiver<WriterControl>,
    ) -> impl std::future::Future<Output = (Writer, ActivityEngine)> {
        let health_guard = self.health.register_writer();
        async move {
            let _health_guard = health_guard;
            self.run(data_rx, control_rx).await
        }
    }

    /// biased 双 lane 循环；Shutdown 或通道全闭后返回。
    /// 本函数不注册健康状态（注册只能在 spawn 前同步完成，见 `into_run_future`）；
    /// 直接调用仅用于不接入健康模型的既有单元测试。
    pub async fn run(
        mut self,
        mut data_rx: mpsc::Receiver<WriterDataMessage>,
        mut control_rx: mpsc::Receiver<WriterControl>,
    ) -> (Writer, ActivityEngine) {
        loop {
            tokio::select! {
                biased;
                control = control_rx.recv() => {
                    match control {
                        Some(WriterControl::Lifecycle { event, barrier_id, expected_revision, ack }) => {
                            // 阶段 4.2：三要素匹配（ID + kind + expected revision）。
                            if let Err(e) = self
                                .drain_to_barrier(
                                    &mut data_rx,
                                    &barrier_id,
                                    wuji_core::pipeline::BarrierKind::Lifecycle,
                                    expected_revision,
                                )
                                .await
                            {
                                // 阶段 4.4 复审补修 P1-01：保留 drain 返回的精确
                                // 错误码（如 SETTINGS_CONFLICT），不覆盖为
                                // INTERNAL_SAFE_ERROR；mark_fatal 已在
                                // reject_protocol_violation 中由 process_data
                                // 调用设置。
                                self.shared.set_error(
                                    wuji_core::error::ErrorSource::Writer,
                                    e.code,
                                );
                                let _ = ack.send(Err(e));
                                continue;
                            }
                            // 阶段 4.4 复审补修 P1-01：drain 成功后提交前的协议健康
                            // 守卫（belt-and-suspenders：pending/control-first/
                            // FIFO 三条路径均已检查，此处是提交前最终防线）。
                            if let Err(e) = self.ensure_protocol_healthy() {
                                let _ = ack.send(Err(e));
                                continue;
                            }
                            let result = self.engine.handle(&mut self.writer, event);
                            if let Err(error) = &result {
                                self.mark_fatal(error);
                            }
                            let _ = ack.send(result);
                        }
                        Some(WriterControl::SettingsApplied { settings, at_utc_ms, barrier_id, expected_revision, ack }) => {
                            // 阶段 4.2：三要素匹配（ID + kind + expected revision）。
                            if let Err(e) = self
                                .drain_to_barrier(
                                    &mut data_rx,
                                    &barrier_id,
                                    wuji_core::pipeline::BarrierKind::SettingsApplied,
                                    expected_revision,
                                )
                                .await
                            {
                                let _ = ack.send(Err(e));
                                continue;
                            }
                            // 阶段 4.4 复审补修 P1-01：drain 成功后提交前的协议健康
                            // 守卫（belt-and-suspenders：pending/control-first/
                            // FIFO 三条路径均已检查，此处是提交前最终防线）。
                            if let Err(e) = self.ensure_protocol_healthy() {
                                let _ = ack.send(Err(e));
                                continue;
                            }
                            let result =
                                crate::settings_persist::apply_settings_persistent(
                                    &mut self.engine,
                                    &mut self.writer,
                                    &self.settings_backup_dir,
                                    &settings,
                                    at_utc_ms,
                                )
                                .map(|outcome| outcome.revision());
                            match &result {
                                Ok(revision) => {
                                    self.shared.set_applied_settings_revision(*revision);
                                    // 复审 P2-01：成功应用后只清除 Settings 来源的过期错误。
                                    self.shared.clear_error(wuji_core::error::ErrorSource::Settings);
                                }
                                Err(error) => {
                                    // 复审 P2-01：Settings 失败只更新 Settings 来源。
                                    self.shared
                                        .set_error(wuji_core::error::ErrorSource::Settings, error.code);
                                }
                            }
                            let _ = ack.send(result);
                        }
                        Some(WriterControl::Shutdown { ack }) => {
                            // 排空全部 data backlog（不等待 barrier）。
                            self.drain_all(&mut data_rx).await;
                            self.shutdown().await;
                            let _ = ack.send(());
                            break;
                        }
                        Some(control) => {
                            if !self.handle_control(control).await {
                                break;
                            }
                        }
                        None => break,
                    }
                }
                message = data_rx.recv() => {
                    match message {
                        Some(message) => {
                            self.continuity.note_writer_dequeue();
                            if !self.process_data(message).await {
                                break;
                            }
                        }
                        None => {
                            if control_rx.is_closed() {
                                break;
                            }
                        }
                    }
                }
            }
        }
        self.into_parts()
    }

    async fn handle_control(&mut self, control: WriterControl) -> bool {
        match control {
            WriterControl::Heartbeat(snapshot) => {
                if let Err(error) = self.write_heartbeat(&snapshot) {
                    self.mark_fatal(&error);
                    return true;
                }
                self.shared.note_heartbeat(
                    snapshot.heartbeat_at_utc_ms,
                    Some(snapshot.heartbeat_at_utc_ms),
                    snapshot.capture_queue_depth as u32,
                    snapshot.writer_queue_depth as u32,
                    snapshot.dropped_capture_count as u64,
                    snapshot.dropped_writer_count as u64,
                );
                true
            }
            WriterControl::Checkpoint => {
                // checkpoint busy 不阻断写入（09 §5.2）：失败仅留安全诊断，下周期重试。
                // S2-08：checkpoint 错误按 Checkpoint 来源管理。
                if let Err(error) = self.writer.checkpoint_truncate() {
                    self.shared
                        .set_error(wuji_core::error::ErrorSource::Checkpoint, error.code);
                } else {
                    self.shared
                        .clear_error(wuji_core::error::ErrorSource::Checkpoint);
                }
                true
            }
            WriterControl::Lifecycle { .. }
            | WriterControl::SettingsApplied { .. }
            | WriterControl::Shutdown { .. } => {
                unreachable!("Lifecycle/SettingsApplied/Shutdown 已在 run 中单独处理")
            }
        }
    }

    /// 阶段 4.2：按 ID + kind + expected revision 三要素匹配 Barrier。
    /// 复审 P1-01：匹配成功后还必须验证 Engine 当前 revision == expected_revision，
    /// 否则拒绝并毒化该 ID（token/control 可能携带相同的过期 revision）。
    /// 先查 pending（先到已登记），再从 FIFO 读取；普通 data 分支遇到的 Barrier
    /// 一律登记 pending 不得跳过；超时 5s 返回错误，不提交边界。
    async fn drain_to_barrier(
        &mut self,
        data_rx: &mut mpsc::Receiver<WriterDataMessage>,
        expected_id: &wuji_core::pipeline::BarrierId,
        expected_kind: wuji_core::pipeline::BarrierKind,
        expected_revision: i64,
    ) -> StorageResult<()> {
        // 阶段 4.4 复审第二次补修：协议违例是本进程内不可恢复的全局
        // fencing。已锁存后收到的任何新 control 都必须在访问 pending/FIFO
        // 前立即、稳定地返回 SETTINGS_CONFLICT，不能再等待 Barrier 超时，
        // 也不能让 Lifecycle 的通用 drain 错误覆盖精确诊断。
        self.ensure_protocol_healthy()?;
        match self
            .pending_barriers
            .take_if_matches(expected_id, expected_kind, expected_revision)
        {
            PendingTake::Matched => {
                // 阶段 4.4 复审补修 P1-01：pending 匹配成功也必须先检查协议健康，
                // 再检查 Engine revision（防线）。
                self.ensure_protocol_healthy()?;
                return self.verify_engine_revision(expected_id, expected_revision);
            }
            PendingTake::RevisionConflict | PendingTake::KindConflict | PendingTake::Poisoned => {
                return Err(StorageError::new(
                    SafeErrorCode::SettingsConflict,
                    "Barrier 冲突或已毒化，拒绝应用边界",
                ));
            }
            PendingTake::Absent => {
                // 第三轮复审 P1-01：饱和降级必须覆盖 control-first 的直接 FIFO 路径——
                // ID 不在表中且全局饱和时立即拒绝，不得继续从 FIFO 匹配全新 ID。
                // 已在表中的 Pending ID（Matched 分支）不受饱和影响，允许消费。
                if self.pending_barriers.is_saturated() {
                    return Err(StorageError::new(
                        SafeErrorCode::SettingsConflict,
                        "pending 全局饱和降级，拒绝新 Barrier 边界",
                    ));
                }
            }
        }

        let deadline = tokio::time::Instant::now() + std::time::Duration::from_millis(5_000);
        loop {
            match data_rx.try_recv() {
                Ok(ProcessorOutput::Barrier(token)) => {
                    if token.id == *expected_id
                        && token.kind == expected_kind
                        && token.expected_revision == expected_revision
                    {
                        // 阶段 4.4 复审补修 P1-01：直接 FIFO 匹配成功也必须先检查
                        // 协议健康，再检查 Engine revision（防线）。
                        self.ensure_protocol_healthy()?;
                        return self.verify_engine_revision(expected_id, expected_revision);
                    }
                    if token.id == *expected_id {
                        // kind 或 revision 不符：毒化并立即失败（复审 P2-01，不再等超时）。
                        self.pending_barriers.poison(expected_id);
                        return Err(StorageError::new(
                            SafeErrorCode::SettingsConflict,
                            "Barrier kind/revision 与 control 不符，拒绝应用边界",
                        ));
                    }
                    // 其他 Barrier：登记 pending（有界/TTL/冲突/毒化规则）。
                    self.register_pending(token);
                }
                Ok(message) => {
                    self.continuity.note_writer_dequeue();
                    self.process_data(message).await;
                    // 阶段 4.4 复审补修 P1-01：process_data 可能锁存
                    // protocol_violation。一旦锁存必须立即停止寻找 Barrier，
                    // 不得继续消费后续数据或匹配边界。
                    if self.protocol_violation {
                        return Err(StorageError::new(
                            SafeErrorCode::SettingsConflict,
                            "drain 中检测到 revision 协议违例，拒绝提交边界",
                        ));
                    }
                }
                Err(mpsc::error::TryRecvError::Empty) => {
                    if tokio::time::Instant::now() >= deadline {
                        return Err(StorageError::new(
                            wuji_core::error::SafeErrorCode::InternalSafeError,
                            "barrier 超时",
                        ));
                    }
                    tokio::time::sleep(std::time::Duration::from_millis(10)).await;
                }
                Err(mpsc::error::TryRecvError::Disconnected) => {
                    return Err(StorageError::new(
                        wuji_core::error::SafeErrorCode::InternalSafeError,
                        "data lane 关闭",
                    ));
                }
            }
        }
    }

    /// 复审 P1-01：Barrier 提交前验证 Engine 真实 revision；过期边界拒绝并毒化 ID。
    fn verify_engine_revision(
        &mut self,
        expected_id: &wuji_core::pipeline::BarrierId,
        expected_revision: i64,
    ) -> StorageResult<()> {
        if self.engine.settings_revision() != expected_revision {
            // 毒化失败时（容量满且 ID 不在表中）失败信号已由本错误给出，不突破容量。
            if !self.pending_barriers.poison(expected_id) {
                eprintln!("pending 已满：无法记录毒化 tombstone（已由 control 拒绝）");
            }
            self.shared.set_error(
                wuji_core::error::ErrorSource::Writer,
                SafeErrorCode::SettingsConflict,
            );
            return Err(StorageError::new(
                SafeErrorCode::SettingsConflict,
                "Engine revision 已前进，Barrier 过期，拒绝提交边界",
            ));
        }
        Ok(())
    }

    /// 登记 pending 并上报诊断（overflow/冲突绝不静默，阶段 4.2 第 6 条）。
    fn register_pending(&mut self, token: wuji_core::pipeline::BarrierToken) {
        match self.pending_barriers.register(token) {
            PendingRegister::Registered | PendingRegister::Duplicate => {}
            PendingRegister::Conflict
            | PendingRegister::Poisoned
            | PendingRegister::Overflow
            | PendingRegister::Saturated => {
                self.shared.set_error(
                    wuji_core::error::ErrorSource::Writer,
                    SafeErrorCode::InternalSafeError,
                );
            }
        }
    }

    /// 排空全部 data backlog（不等待 barrier）。用于 Shutdown。
    /// 退出时 pending 全部丢弃并报告数量（阶段 4.2 shutdown 清理规则）。
    async fn drain_all(&mut self, data_rx: &mut mpsc::Receiver<WriterDataMessage>) {
        loop {
            match data_rx.try_recv() {
                Ok(ProcessorOutput::Barrier(token)) => {
                    // shutdown 路径同样登记 pending（之后统一清理报告）。
                    self.register_pending(token);
                }
                Ok(message) => {
                    self.continuity.note_writer_dequeue();
                    self.process_data(message).await;
                }
                Err(mpsc::error::TryRecvError::Empty) => break,
                Err(mpsc::error::TryRecvError::Disconnected) => break,
            }
        }
        let summary = self.pending_barriers.summary();
        if summary.pending > 0 || summary.poisoned > 0 || summary.saturated {
            eprintln!(
                "Writer shutdown：丢弃 {} 个未消费状态（pending {} + poisoned {}，saturated={}）",
                summary.pending + summary.poisoned,
                summary.pending,
                summary.poisoned,
                summary.saturated
            );
            self.shared.set_error(
                wuji_core::error::ErrorSource::Writer,
                SafeErrorCode::InternalSafeError,
            );
        }
    }

    /// 处理单条 data 消息：busy 时引擎整体回滚并以 100/250ms 重试（09 §5.2）。
    ///
    /// 阶段 4.4（P1-04）：Observation、PrivacyExcluded、CaptureError 共用同一
    /// revision 防线，在转换任何 EngineEvent 之前校验。revision 错配与
    /// Processor 发来的显式违例消息同属内部协议不变量破坏：零
    /// ActivityEngine/SQLite 副作用、留下来源明确的 SETTINGS_CONFLICT、
    /// fail-closed 锁存（本进程内不解除），IPC/Diagnostics 保持在线；
    /// 禁止把旧消息重标为新 revision 或清队列换绿。
    async fn process_data(&mut self, message: WriterDataMessage) -> bool {
        // 统一 revision 防线（先于任何 EngineEvent 转换）。协议违例锁存后，
        // 后续数据消息无论 revision 是否匹配一律拒绝——违例事实已在首次
        // 上报，数据流不再可信，绝不补写。
        if let Some(revision) = message.settings_revision()
            && (self.protocol_violation || revision != self.engine.settings_revision())
        {
            self.reject_protocol_violation(message.sequence(), revision);
            return true;
        }
        let event = match message {
            ProcessorOutput::Observation(obs) => EngineEvent::Observation(obs),
            ProcessorOutput::PrivacyExcluded {
                captured_at_utc_ms, ..
            } => EngineEvent::PrivacyExcluded { captured_at_utc_ms },
            ProcessorOutput::CaptureError {
                captured_at_utc_ms, ..
            } => EngineEvent::CaptureError { captured_at_utc_ms },
            // 阶段 4.2：普通 data 分支遇到 Barrier 一律登记 pending，不得跳过。
            ProcessorOutput::Barrier(token) => {
                self.register_pending(token);
                return true;
            }
            // 阶段 4.4：Processor 在业务处理前检出的 revision 协议违例，
            // 交给 Writer 统一 fail-closed。
            ProcessorOutput::SettingsRevisionMismatch {
                sequence,
                sample_revision,
                ..
            } => {
                self.reject_protocol_violation(sequence, sample_revision);
                return true;
            }
        };
        let snapshot = self.engine.snapshot();
        let mut attempt = 0_u32;
        loop {
            match self.engine.handle(&mut self.writer, event.clone()) {
                Ok(()) => {
                    if let EngineEvent::Observation(obs) = &event {
                        self.shared.note_observation(obs.captured_at_utc_ms);
                    }
                    if attempt > 0
                        && self.shared.process_state() != ProcessState::Faulted
                        && self.shared.writer_state() == WriterState::Degraded
                    {
                        self.shared.set_writer_state(WriterState::Healthy);
                        // S2-08：busy 恢复只清除 Writer 错误。
                        self.shared
                            .clear_error(wuji_core::error::ErrorSource::Writer);
                    }
                    return true;
                }
                Err(error) if error.code == SafeErrorCode::AgentWriterDegraded && attempt < 2 => {
                    // busy：回滚后按 100/250ms 退避重试，不确认未提交消息（09 §5.2）。
                    self.engine.restore(&snapshot);
                    // Coordinator 的 unknown-outcome fencing 已把 process/writer 锁存
                    // Faulted 时，迟到 Writer 重试不得把 DTO 降级为 Degraded，随后
                    // 又恢复 Healthy/清除 AGENT_WRITER_FAULTED。
                    if self.shared.process_state() != ProcessState::Faulted
                        && self.shared.writer_state() != WriterState::Faulted
                    {
                        self.shared.set_writer_state(WriterState::Degraded);
                    }
                    attempt += 1;
                    let backoff = if attempt == 1 { 100 } else { 250 };
                    tokio::time::sleep(std::time::Duration::from_millis(backoff)).await;
                }
                Err(error) => {
                    self.engine.restore(&snapshot);
                    self.mark_fatal(&error);
                    return true;
                }
            }
        }
    }

    fn write_heartbeat(&mut self, snapshot: &HeartbeatSnapshot) -> StorageResult<()> {
        // safe_error_code 随心跳持久化（审核 R09）：离线诊断能看到当前所有安全错误。
        let safe_error_str = wuji_core::error::format_error_set(&self.shared.errors());
        let tx = self.writer.transaction()?;
        tx.update_runtime_heartbeat(
            self.engine.runtime_id(),
            snapshot.heartbeat_at_utc_ms,
            snapshot.last_observation_at_utc_ms,
            Some(snapshot.heartbeat_at_utc_ms),
            snapshot.capture_queue_depth,
            snapshot.writer_queue_depth,
            snapshot.dropped_capture_count,
            snapshot.dropped_writer_count,
            snapshot.continuity_epoch,
            self.shared.process_state(),
            self.shared.capture_state(),
            self.shared.writer_state(),
            safe_error_str.as_deref(),
        )?;
        tx.commit()
    }

    /// 阶段 4.4：revision 协议违例的统一收尾。首次违例留下安全诊断（只含
    /// sequence/revision，不含进程名等隐私内容）并 mark_fatal fail-closed；
    /// 锁存后后续数据消息直接拒绝，不再重复刷诊断（与 fenced transition
    /// 的拒绝语义一致，违例事实已在首次上报）。
    fn reject_protocol_violation(&mut self, sequence: u64, sample_revision: i64) {
        if self.protocol_violation {
            return;
        }
        self.protocol_violation = true;
        eprintln!(
            "SETTINGS_CONFLICT：流水线 revision 协议违例（seq={sequence}, sample_rev={sample_revision}, engine_rev={}），采集已安全停止",
            self.engine.settings_revision()
        );
        self.mark_fatal(&StorageError::new(
            SafeErrorCode::SettingsConflict,
            "流水线 revision 协议违例，采集已安全停止",
        ));
    }

    /// 不可恢复写入失败：停止 Capture、Writer faulted、IPC 保持在线（09 §5.2）。
    /// S2-08：按 Writer 来源设置错误。
    fn mark_fatal(&mut self, error: &StorageError) {
        self.shared.set_writer_state(WriterState::Faulted);
        self.shared
            .set_error(wuji_core::error::ErrorSource::Writer, error.code);
        self.shared.set_process_state(ProcessState::Faulted);
        let _ = self.capture_state_tx.send(CaptureState::Stopped);
        self.shared.set_capture_state(CaptureState::Stopped);
    }

    async fn shutdown(&mut self) {
        let now = crate::capture_loop::now_utc_ms();
        if let Err(error) = self
            .engine
            .handle(&mut self.writer, EngineEvent::Shutdown { at_utc_ms: now })
        {
            self.shared.set_safe_error(Some(error.code));
        }
        self.shared.set_process_state(ProcessState::Stopped);
    }
}
