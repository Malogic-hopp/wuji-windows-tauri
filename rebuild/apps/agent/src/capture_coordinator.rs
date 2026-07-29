//! CaptureCoordinator：Lifecycle / settings_reload / reconciler / 系统事件的
//! 唯一生产控制入口（阶段 4.3，审核 P1-02 与 P1-03 的控制闭环；复审 P1-01/P1-02 补修；
//! 阶段 4.5 Lock/Sleep 状态叠加）。
//!
//! 不变量：
//! - 唯一 transition lock 串行化全部控制操作（不再存在分散的 capture_lock）。
//! - 每条路径都等待 injected ack 与 Writer ack；Barrier 注入确认前绝不发送
//!   对应 WriterControl（无悬挂等待）。
//! - 显式建模：desired capture state、effective gate、transition suppression、
//!   三类 fault suppression 与 applied settings revision（Writer 提交后写入
//!   SharedState，Coordinator 在锁内读取作为 expected_revision）：
//!   - `fault`：lifecycle 提交失败的安全冻结，可由合法显式命令解除；
//!   - `writer_fault`：Writer/Process fatal，不可由用户命令解除（复审 P1-01），
//!     只有 Agent 重启并成功完成启动恢复后才重建健康状态；
//!   - `lifecycle_monitor_fault`：session/power 事件泵永久失效，不可由普通
//!     Start/Resume 清除（阶段 4.5），仅 Agent 重启可恢复。
//! - 状态发布集中在返回 `Result` 的 `publish()`：Running 发布前必须确认
//!   控制面健康（RAII 任务存活 + barrier/control channel 存活 + capture watch
//!   有消费者），失败时保持/回退 Stopped 并留下来源明确的诊断，绝不虚假 Running
//!   （复审 P1-02）。
//! - 失败后 shared/watch/DTO 一致：lifecycle 失败 fail-closed；settings 失败保持
//!   last-known-good。
//! - Lock/Sleep 各保存独立 active + committed + first_time 状态（阶段 4.5）；
//!   重复进入事件幂等且可重试。

use std::sync::{Arc, Mutex};

use tokio::sync::{mpsc, oneshot, watch};
use wuji_core::domain::{CaptureState, ProcessState, WriterState};
use wuji_core::error::{ErrorSource, SafeError, SafeErrorCode};
use wuji_core::pipeline::{BarrierId, BarrierKind, BarrierToken};
use wuji_core::settings::Settings;

use crate::activity::EngineEvent;
use crate::barrier::{BarrierInjectError, BarrierRequest};
use crate::pipeline_health::{PipelineHealth, PipelineTask};
use crate::shared::SharedState;
use crate::writer_task::WriterControl;

/// 阶段 4.5：平台无关的四态系统生命周期事件。
/// Windows session/power adapter 只负责产生四态输入；
/// 状态叠加、Barrier、Writer 与发布语义由唯一 Coordinator 决定。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SystemLifecycleEvent {
    Lock { at_utc_ms: i64 },
    Unlock { at_utc_ms: i64 },
    Sleep { at_utc_ms: i64 },
    Resume { at_utc_ms: i64 },
}

impl SystemLifecycleEvent {
    /// 是否为进入事件（需要注入 Barrier 和提交边界）。
    pub fn is_enter(&self) -> bool {
        matches!(self, Self::Lock { .. } | Self::Sleep { .. })
    }

    /// 是否为解除事件（只清对应 suppression，不产生数据库边界）。
    pub fn is_release(&self) -> bool {
        matches!(self, Self::Unlock { .. } | Self::Resume { .. })
    }

    /// 事件发生时刻（UTC 毫秒）。
    pub fn at_utc_ms(&self) -> i64 {
        match self {
            Self::Lock { at_utc_ms }
            | Self::Unlock { at_utc_ms }
            | Self::Sleep { at_utc_ms }
            | Self::Resume { at_utc_ms } => *at_utc_ms,
        }
    }

    /// 对应的 EngineEvent（进入事件）；release 事件不产生 Writer 边界。
    pub fn to_engine_event(&self) -> Option<EngineEvent> {
        match self {
            Self::Lock { at_utc_ms } => Some(EngineEvent::SessionLocked {
                at_utc_ms: *at_utc_ms,
            }),
            Self::Sleep { at_utc_ms } => Some(EngineEvent::SystemSleep {
                at_utc_ms: *at_utc_ms,
            }),
            Self::Unlock { .. } | Self::Resume { .. } => None,
        }
    }

    /// 错误诊断来源。
    pub fn error_source(&self) -> ErrorSource {
        ErrorSource::LifecyclePump
    }
}

/// 阶段 4.5：单个抑制源（Lock 或 Sleep）的独立状态。
#[derive(Debug, Clone, Default)]
struct LockSleepState {
    /// 当前是否处于该抑制状态。
    active: bool,
    /// 首次进入该状态的时间（UTC 毫秒）。仅在 active 时有意义；
    /// 用于重试时复用首次时间，不写新时间。
    first_at_utc_ms: Option<i64>,
    /// 边界是否已成功提交给 Writer。
    committed: bool,
}

impl LockSleepState {
    /// 首次激活：记录首次时间，未提交。
    fn activate(&mut self, at_utc_ms: i64) {
        self.active = true;
        self.first_at_utc_ms = Some(at_utc_ms);
        self.committed = false;
    }

    /// 标记提交成功。
    fn mark_committed(&mut self) {
        self.committed = true;
    }

    /// 完全复位（release）。
    fn reset(&mut self) {
        self.active = false;
        self.first_at_utc_ms = None;
        self.committed = false;
    }

    /// 是否需要在当前 active 状态下提交（或重试提交）边界。
    fn needs_boundary(&self) -> bool {
        self.active && !self.committed
    }

    /// 是否需要注入 Barrier（首次 freeze 或重试时 freeze 尚未完成）。
    pub fn needs_freeze(&self) -> bool {
        self.active && !self.committed
    }
}

/// Writer ack 的 Coordinator operation deadline（S2-06）：
/// 必须大于 Writer 内部 drain 上限（5s）；与 IPC 客户端 3 秒 timeout 分离命名。
/// 超时定义为"提交结果未知"，不是"未提交"。
const WRITER_ACK_TIMEOUT: std::time::Duration = std::time::Duration::from_secs(8);
/// Writer control lane 取得容量的期限。permit 未取得时 control 尚未入队，
/// 因而可安全按“未提交”收尾；不得让 transition lock 永久等待共享维护队列。
const CONTROL_SEND_TIMEOUT: std::time::Duration = std::time::Duration::from_secs(2);

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum ControlSendError {
    Timeout,
    Closed,
}

/// Capture 状态机（09 §8.2 转换表），作用于 Coordinator 的 desired state。
/// Ok(next)：允许转换；Err(Some(state))：幂等成功；Err(None)：非法转换。
pub fn capture_transition(
    current: CaptureState,
    command: &str,
) -> Result<CaptureState, Option<CaptureState>> {
    match (command, current) {
        ("capture_start", CaptureState::Stopped) => Ok(CaptureState::Running),
        ("capture_start", CaptureState::Running) => Err(Some(CaptureState::Running)),
        ("capture_pause", CaptureState::Running) => Ok(CaptureState::Paused),
        ("capture_pause", CaptureState::Paused) => Err(Some(CaptureState::Paused)),
        ("capture_resume", CaptureState::Paused) => Ok(CaptureState::Running),
        ("capture_resume", CaptureState::Running) => Err(Some(CaptureState::Running)),
        ("capture_stop", CaptureState::Running | CaptureState::Paused) => Ok(CaptureState::Stopped),
        ("capture_stop", CaptureState::Stopped) => Err(Some(CaptureState::Stopped)),
        _ => Err(None),
    }
}

/// 需要引擎边界的 capture 命令 → 生命周期事件（start/resume 无边界）。
fn capture_lifecycle_event(command: &str, at_utc_ms: i64) -> Option<EngineEvent> {
    match command {
        "capture_pause" => Some(EngineEvent::CapturePaused { at_utc_ms }),
        "capture_stop" => Some(EngineEvent::CaptureStopped { at_utc_ms }),
        _ => None,
    }
}

/// effective gate 的抑制源（任何一个置位都会关闭 gate）。
#[derive(Debug, Default)]
struct Suppressions {
    /// transition 进行中（settings/lifecycle 切换骨架；4.4 补全 effectivity）。
    transition: bool,
    /// lifecycle 提交失败的安全冻结：可由合法显式命令解除（start/resume/stop）。
    fault: bool,
    /// Writer/Process fatal（复审 P1-01）：不可由用户命令解除；
    /// 仅 Agent 重启并成功完成启动恢复后重建（本进程内一旦置位不再清除）。
    writer_fault: bool,
    /// 阶段 4.5：session/power 事件泵永久失效。
    /// 不可由普通 Start/Resume 清除；仅 Agent 重启可恢复。
    lifecycle_monitor_fault: bool,
    /// 阶段 4.5：会话锁定抑制源（含 active + committed + first_time）。
    lock: LockSleepState,
    /// 阶段 4.5：系统睡眠抑制源（含 active + committed + first_time）。
    sleep: LockSleepState,
}

#[derive(Debug)]
struct CoordinatorState {
    /// 用户期望的 capture state（capture_start/pause/resume/stop 的目标）。
    desired: CaptureState,
    /// 最近一次发布到 capture watch/SharedState 的 effective 值（外部故障覆盖检测用）。
    last_published: CaptureState,
    suppressions: Suppressions,
}

impl CoordinatorState {
    fn gate_open(&self) -> bool {
        self.desired == CaptureState::Running
            && !self.suppressions.transition
            && !self.suppressions.fault
            && !self.suppressions.writer_fault
            && !self.suppressions.lifecycle_monitor_fault
            && !self.suppressions.lock.active
            && !self.suppressions.sleep.active
    }

    /// effective capture state：watch 与 SharedState 的唯一发布值。
    /// desired 为 Running 但被抑制时发布 Paused（采集实际未进行）。
    fn effective(&self) -> CaptureState {
        if self.gate_open() {
            CaptureState::Running
        } else if self.desired == CaptureState::Running {
            CaptureState::Paused
        } else {
            self.desired
        }
    }
}

/// 统一协调器：全部控制操作（Lifecycle/Settings/reconciler/系统事件）的串行化入口。
pub struct CaptureCoordinator {
    /// 唯一 transition lock。
    lock: tokio::sync::Mutex<()>,
    /// desired/effective/suppression 状态（只在 transition lock 内变更；
    /// std Mutex 仅为只读访问，绝不跨 await 持有）。
    state: Mutex<CoordinatorState>,
    /// 请求 Capture Loop 写入 Barrier 的通道（唯一持有者；带 injected ack）。
    barrier_request_tx: mpsc::Sender<BarrierRequest>,
    /// effective gate 发布点：Capture Loop 据此启停采样。
    capture_state_tx: watch::Sender<CaptureState>,
    /// 发送 WriterControl 到 WriterTask（Lifecycle/SettingsApplied 的唯一构造点）。
    control_tx: mpsc::Sender<WriterControl>,
    /// 共享状态（DTO/心跳快照；applied revision 由 Writer 提交后写入）。
    shared: Arc<SharedState>,
    /// Settings watch（成功应用后更新，Capture Loop/Processor 切换 revision）。
    settings_tx: watch::Sender<Settings>,
    /// 生产任务健康句柄（复审 P1-02：Running 发布前置条件）。
    health: Arc<PipelineHealth>,
}

/// control 已被 Writer lane 接受后的取消安全护栏。future 在 ack 结果被解释前
/// panic/abort 时，提交结果无法证明，Drop 必须锁存 writer fault。正常路径在
/// 收到确定 ack 后显式 disarm。
struct AcceptedControlGuard<'a> {
    coordinator: &'a CaptureCoordinator,
    armed: bool,
}

impl<'a> AcceptedControlGuard<'a> {
    fn new(coordinator: &'a CaptureCoordinator) -> Self {
        Self {
            coordinator,
            armed: true,
        }
    }

    fn disarm(&mut self) {
        self.armed = false;
    }
}

impl Drop for AcceptedControlGuard<'_> {
    fn drop(&mut self) {
        if self.armed {
            self.coordinator.latch_writer_fault();
        }
    }
}

/// 阶段 4.5：Lock/Sleep 抑制源标识。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum LockSleepSource {
    Lock,
    Sleep,
}

impl LockSleepSource {
    fn lock_sleep_mut<'a>(&self, s: &'a mut Suppressions) -> &'a mut LockSleepState {
        match self {
            Self::Lock => &mut s.lock,
            Self::Sleep => &mut s.sleep,
        }
    }

    fn lock_sleep_mut_read<'a>(&self, s: &'a Suppressions) -> &'a LockSleepState {
        match self {
            Self::Lock => &s.lock,
            Self::Sleep => &s.sleep,
        }
    }
}

/// 从事件确定抑制源。
fn event_source(event: &SystemLifecycleEvent) -> LockSleepSource {
    match event {
        SystemLifecycleEvent::Lock { .. } | SystemLifecycleEvent::Unlock { .. } => {
            LockSleepSource::Lock
        }
        SystemLifecycleEvent::Sleep { .. } | SystemLifecycleEvent::Resume { .. } => {
            LockSleepSource::Sleep
        }
    }
}

impl CaptureCoordinator {
    #[allow(clippy::too_many_arguments)]
    pub fn new(
        barrier_request_tx: mpsc::Sender<BarrierRequest>,
        capture_state_tx: watch::Sender<CaptureState>,
        control_tx: mpsc::Sender<WriterControl>,
        shared: Arc<SharedState>,
        settings_tx: watch::Sender<Settings>,
        initial_capture: CaptureState,
        health: Arc<PipelineHealth>,
    ) -> Self {
        Self {
            lock: tokio::sync::Mutex::new(()),
            state: Mutex::new(CoordinatorState {
                desired: initial_capture,
                last_published: initial_capture,
                suppressions: Suppressions::default(),
            }),
            barrier_request_tx,
            capture_state_tx,
            control_tx,
            shared,
            settings_tx,
            health,
        }
    }

    /// 当前 desired capture state（只读）。
    pub fn desired_state(&self) -> CaptureState {
        self.state.lock().expect("coordinator state").desired
    }

    /// 当前 effective capture state（只读；与 watch/SharedState 发布值一致）。
    pub fn effective_state(&self) -> CaptureState {
        self.state.lock().expect("coordinator state").effective()
    }

    /// 集成测试 rendezvous：显式钉住唯一 transition lock，以证明多个真实调用方
    /// 已同时进入控制面。仅用于确定性接线测试，不参与生产编排。
    #[doc(hidden)]
    pub async fn acquire_transition_lock_for_test(&self) -> tokio::sync::MutexGuard<'_, ()> {
        self.lock.lock().await
    }

    /// 集成测试 rendezvous：非阻塞尝试钉住唯一 transition lock。供测试在
    /// abort 周期任务前排空其可能在飞的 transition（避免 abort 砍掉半个
    /// transition 导致 gate 永久冻结）。仅用于确定性接线测试。
    #[doc(hidden)]
    pub fn try_acquire_transition_lock_for_test(
        &self,
    ) -> Result<tokio::sync::MutexGuard<'_, ()>, tokio::sync::TryLockError> {
        self.lock.try_lock()
    }

    /// IPC capture 命令统一入口（capture_start/pause/resume/stop）。
    /// 成功返回发布后的 effective state；幂等命令按 09 §8.2 返回成功。
    pub async fn apply_capture_command(
        &self,
        command: &str,
        at_utc_ms: i64,
    ) -> Result<CaptureState, SafeError> {
        let _guard = self.lock.lock().await;
        self.sync_external();

        // 复审 P1-01：Writer/Process fatal 不可由用户 capture 命令复活；
        // 拒绝时 desired/effective/watch/shared/DTO 全部保持 Stopped，零副作用。
        if matches!(command, "capture_start" | "capture_resume") && self.writer_faulted() {
            return Err(SafeError::new(
                SafeErrorCode::AgentWriterFaulted,
                "写入器故障且无法恢复，采集保持停止",
            ));
        }
        // 启动对账无法恢复可信 settings 时禁止开始采集（R04：不静默回 revision 0）。
        if command == "capture_start" && self.shared.capture_blocked() {
            return Err(SafeError::new(
                SafeErrorCode::SettingsInvalid,
                "设置不可用且无法恢复最后已应用值，已禁止采集",
            ));
        }
        let current = self.desired_state();
        let next = match capture_transition(current, command) {
            Ok(next) => next,
            Err(Some(_)) => return Ok(self.effective_state()), // 幂等成功（09 §8.2）
            Err(None) => {
                return Err(SafeError::new(
                    SafeErrorCode::CaptureInvalidState,
                    "当前状态不能执行该采集命令",
                ));
            }
        };

        let Some(event) = capture_lifecycle_event(command, at_utc_ms) else {
            // start/resume：无引擎边界。显式用户命令解除 lifecycle fault 冻结；
            // Running 发布必须先通过控制面健康检查（publish 内置，复审 P1-02）。
            {
                let mut state = self.state.lock().expect("coordinator state");
                state.desired = next;
                state.suppressions.fault = false;
            }
            return match self.publish() {
                Ok(()) => Ok(self.effective_state()),
                Err(error) => {
                    // 发布 Running 失败：回退 Stopped，shared/DTO 绝不说 Running。
                    self.force_stopped(ErrorSource::Writer, SafeErrorCode::InternalSafeError);
                    Err(error)
                }
            };
        };

        // pause/stop：先冻结（watch+shared 一致进入目标非 Running 态），
        // 再注入 Barrier、发送 control、等待 Writer ack。
        if let Err(error) = self.begin_transition(Some(next)) {
            // capture watch 无消费者：采集任务已退出，fail-closed 到 Stopped。
            self.force_stopped(ErrorSource::Writer, SafeErrorCode::InternalSafeError);
            return Err(error);
        }
        let expected_revision = self.shared.applied_settings_revision();
        let barrier_id = match self.inject(BarrierKind::Lifecycle, expected_revision).await {
            Ok(id) => id,
            Err(inject) => {
                // fail-closed：保持安全冻结，绝不恢复采集（P1-02 第 5 条）。
                let _ = self.end_transition(true);
                self.shared
                    .set_error(ErrorSource::Writer, SafeErrorCode::InternalSafeError);
                return Err(SafeError::new(
                    SafeErrorCode::InternalSafeError,
                    format!("Barrier 注入失败（{inject:?}），采集已安全停止"),
                ));
            }
        };
        let (ack_tx, ack_rx) = oneshot::channel();
        if let Err(send_error) = self
            .send_control(WriterControl::Lifecycle {
                event,
                barrier_id,
                expected_revision,
                ack: ack_tx,
            })
            .await
        {
            let _ = self.end_transition(true);
            self.shared
                .set_error(ErrorSource::Writer, SafeErrorCode::InternalSafeError);
            return Err(SafeError::new(
                SafeErrorCode::InternalSafeError,
                format!("控制消息未入队（{send_error:?}），采集已安全停止"),
            ));
        }
        let mut accepted = AcceptedControlGuard::new(self);
        let ack_result = tokio::time::timeout(WRITER_ACK_TIMEOUT, ack_rx).await;
        accepted.disarm();
        match ack_result {
            // S2-06：Writer ack 超过 operation deadline——提交结果未知，
            // 锁存 writer_fault 并安全停止（不得重发副作用、不得声称未提交）。
            Err(_) => Err(self.enter_unknown_outcome_fault()),
            Ok(Err(_)) => Err(self.enter_unknown_outcome_fault()),
            Ok(Ok(Err(storage))) => {
                // Writer 已设置自身诊断（必要时已 mark_fatal 安全停止）。
                let _ = self.end_transition(true);
                Err(SafeError::new(
                    storage.code,
                    "采集状态变更提交失败，采集已安全停止",
                ))
            }
            Ok(Ok(Ok(()))) => match self.end_transition(false) {
                Ok(()) => Ok(self.effective_state()),
                Err(_) => {
                    // 复审二 P2-01：生命周期边界已提交（不回滚、不伪造未提交），
                    // 但最终发布失败（采集任务在双 ack 期间退出）——安全停止并
                    // 返回稳定错误；不覆盖 Writer 已产生的更精确 fatal 诊断。
                    if !self.shared.errors().contains_key(&ErrorSource::Writer) {
                        self.shared
                            .set_error(ErrorSource::Writer, SafeErrorCode::InternalSafeError);
                    }
                    self.force_stopped_keep_diagnostic();
                    Err(SafeError::new(
                        SafeErrorCode::InternalSafeError,
                        "生命周期边界已提交但采集流水线不可用，采集已安全停止",
                    ))
                }
            },
        }
    }

    /// Settings 应用统一入口（IPC settings_reload 与 reconciler 共用同一路径）。
    /// 骨架：冻结 effective gate → 注入 Barrier → control → Writer ack →
    /// 先交付 settings watch → 再解除冻结（完整 effectivity 在 4.4）。
    /// 失败保持 last-known-good：采集不冻结，reconciler 下周期可重试。
    pub async fn apply_settings(
        &self,
        settings: Settings,
        at_utc_ms: i64,
    ) -> Result<i64, SafeError> {
        let _guard = self.lock.lock().await;
        self.sync_external();

        // S2-06 fencing：writer_fault 已锁存时 settings 路径不得再进入 Writer。
        if self.writer_faulted() {
            return Err(SafeError::new(
                SafeErrorCode::AgentWriterFaulted,
                "写入器故障且无法恢复，Settings 未应用",
            ));
        }
        let target_revision = settings
            .revision
            .parse::<i64>()
            .map_err(|_| SafeError::new(SafeErrorCode::InvalidArgument, "revision 非数字"))?;
        let expected_revision = self.shared.applied_settings_revision();
        // revision 单调性（R04）：低于已应用值一律拒绝；等于已应用值允许
        // 同 digest 幂等重放（由 Writer 的 crash-consistent 协议把关）。
        if target_revision < expected_revision {
            return Err(SafeError::new(
                SafeErrorCode::SettingsConflict,
                "设置 revision 低于已应用值，Agent 保持上一 revision",
            ));
        }

        if let Err(error) = self.begin_transition(None) {
            // capture watch 无消费者：采集任务已退出；Settings 未应用，安全停止。
            self.force_stopped(
                ErrorSource::Settings,
                SafeErrorCode::SettingsSavedNotApplied,
            );
            return Err(error);
        }
        let barrier_id = match self
            .inject(BarrierKind::SettingsApplied, expected_revision)
            .await
        {
            Ok(id) => id,
            Err(inject) => {
                self.end_settings_transition_uncommitted();
                return Err(SafeError::new(
                    SafeErrorCode::InternalSafeError,
                    format!("Barrier 注入失败（{inject:?}），Settings 未应用"),
                ));
            }
        };
        let (ack_tx, ack_rx) = oneshot::channel();
        if let Err(send_error) = self
            .send_control(WriterControl::SettingsApplied {
                settings: settings.clone(),
                at_utc_ms,
                barrier_id,
                expected_revision,
                ack: ack_tx,
            })
            .await
        {
            self.end_settings_transition_uncommitted();
            return Err(SafeError::new(
                SafeErrorCode::InternalSafeError,
                format!("控制消息未入队（{send_error:?}），Settings 未应用"),
            ));
        }
        let mut accepted = AcceptedControlGuard::new(self);
        let ack_result = tokio::time::timeout(WRITER_ACK_TIMEOUT, ack_rx).await;
        accepted.disarm();
        match ack_result {
            // S2-06：Writer ack 超时——提交结果未知（applied revision 可能已前进），
            // 锁存 writer_fault 并安全停止；不重发副作用、不声称未提交。
            Err(_) => Err(self.enter_unknown_outcome_fault()),
            Ok(Err(_)) => Err(self.enter_unknown_outcome_fault()),
            Ok(Ok(Err(storage))) => {
                // Writer 已按 storage.code 设置 Settings 来源诊断（精确码）；
                // Coordinator 不再覆盖，只负责收尾（last-known-good 继续生效）。
                let code = storage.code;
                let message = storage.message;
                if self.end_transition(false).is_err() {
                    // 解冻失败（采集任务已退出）：安全停止，诊断仍保留 Writer 的精确码。
                    self.force_stopped_keep_diagnostic();
                }
                Err(SafeError::new(code, message))
            }
            Ok(Ok(Ok(applied))) => {
                // settings watch 必须先于解冻交付；无法交付 = 运行时消费者已退出
                // （复审 P1-02：DB 已提交但运行时消费者不可用，不得伪装完整成功）。
                if self.settings_tx.receiver_count() == 0 {
                    self.force_stopped(ErrorSource::Settings, SafeErrorCode::InternalSafeError);
                    return Err(SafeError::new(
                        SafeErrorCode::InternalSafeError,
                        "Settings 已提交但运行时消费者不可用，采集已安全停止",
                    ));
                }
                self.settings_tx.send_replace(settings);
                match self.end_transition(false) {
                    Ok(()) => Ok(applied),
                    Err(_) => {
                        // DB 已提交、applied revision 保留，但 Running 无法交付：
                        // 安全停止并返回错误，绝不虚假 Running。
                        self.force_stopped(ErrorSource::Settings, SafeErrorCode::InternalSafeError);
                        Err(SafeError::new(
                            SafeErrorCode::InternalSafeError,
                            "Settings 已提交但采集流水线不可用，采集已安全停止",
                        ))
                    }
                }
            }
        }
    }

    /// 阶段 4.5：系统生命周期事件唯一入口。
    ///
    /// 进入事件（Lock/Sleep）的流程：
    /// 1. 设置对应 source active + 首次冻结时间
    /// 2. freeze effective capture
    /// 3. 注入 Barrier + 等待 injected ack
    /// 4. 发送 Lifecycle WriterControl（含对应 EngineEvent） + 等待 Writer ack
    /// 5. 标记 committed
    /// 6. 恢复 effective capture（但 suppression 仍 active → Paused）
    ///
    /// 解除事件（Unlock/Resume）：
    /// 1. 清除对应 source 的全部状态
    /// 2. 若所有 suppression 均解除 + desired Running + 健康检查通过 → 恢复 Running
    pub async fn apply_system_lifecycle_event(
        &self,
        event: SystemLifecycleEvent,
    ) -> Result<(), SafeError> {
        let _guard = self.lock.lock().await;
        self.sync_external();

        // S2-06 fencing：writer_fault 已锁存时系统事件路径不得再进入 Writer。
        if self.writer_faulted() {
            return Err(SafeError::new(
                SafeErrorCode::AgentWriterFaulted,
                "写入器故障且无法恢复，系统事件未处理",
            ));
        }
        // lifecycle_monitor_fault：事件泵永久失效，只能拒绝后续输入；
        // 诊断已存在，不重复刷写。
        if self.monitor_faulted() {
            return Err(SafeError::new(
                SafeErrorCode::InternalSafeError,
                "session/power 事件泵已永久失效，系统事件不可处理",
            ));
        }

        match event {
            SystemLifecycleEvent::Lock { at_utc_ms }
            | SystemLifecycleEvent::Sleep { at_utc_ms } => {
                self.apply_enter_event(event, at_utc_ms).await
            }
            SystemLifecycleEvent::Unlock { .. } | SystemLifecycleEvent::Resume { .. } => {
                self.apply_release_event(event)
            }
        }
    }

    /// 处理进入事件（Lock/Sleep）：先设置 state、再走 Barrier → Writer → ack 链路。
    async fn apply_enter_event(
        &self,
        event: SystemLifecycleEvent,
        at_utc_ms: i64,
    ) -> Result<(), SafeError> {
        let source = event_source(&event);
        let needs_boundary;
        let needs_freeze;
        {
            let mut state = self.state.lock().expect("coordinator state");
            let ls = source.lock_sleep_mut(&mut state.suppressions);
            if ls.active && ls.committed {
                return Ok(());
            }
            if !ls.active {
                ls.activate(at_utc_ms);
            }
            needs_boundary = ls.needs_boundary();
            needs_freeze = ls.needs_freeze();
        }

        if needs_freeze {
            // 首次或重试且未完成 freeze：发布安全冻结。
            if let Err(error) = self.begin_transition(None) {
                self.force_stopped_keep_suppression(
                    ErrorSource::LifecyclePump,
                    SafeErrorCode::InternalSafeError,
                );
                return Err(error);
            }
        }

        if needs_boundary {
            // 注入 Barrier + 发送 WriterControl + 等待 Writer ack。
            return self.commit_enter_boundary(&event, at_utc_ms, source).await;
        }

        // freeze 已完成但边界也已完成（concurrent 重复事件极罕见）。
        Ok(())
    }

    /// 提交进入边界：Barrier → WriterControl(Lifecycle) → Writer ack。
    async fn commit_enter_boundary(
        &self,
        event: &SystemLifecycleEvent,
        at_utc_ms: i64,
        source: LockSleepSource,
    ) -> Result<(), SafeError> {
        let expected_revision = self.shared.applied_settings_revision();
        let barrier_id = match self.inject(BarrierKind::Lifecycle, expected_revision).await {
            Ok(id) => id,
            Err(inject) => {
                // 未提交：保持 suppression + 首次时间，可重试。
                if self.end_transition(false).is_err() {
                    self.force_stopped_keep_suppression(
                        ErrorSource::LifecyclePump,
                        SafeErrorCode::InternalSafeError,
                    );
                } else {
                    self.shared
                        .set_error(ErrorSource::LifecyclePump, SafeErrorCode::InternalSafeError);
                }
                return Err(SafeError::new(
                    SafeErrorCode::InternalSafeError,
                    format!("Barrier 注入失败（{inject:?}），系统事件边界未提交，保持安全冻结"),
                ));
            }
        };

        // 阶段 4.5 P1-03：从 LockSleepState 读取首次事件时间构造
        // EngineEvent。失败重试时复用首次时间，gap 起点不推迟。
        let boundary_at = {
            let state = self.state.lock().expect("coordinator state");
            source
                .lock_sleep_mut_read(&state.suppressions)
                .first_at_utc_ms
                .unwrap_or(at_utc_ms)
        };
        let engine_event = match event {
            SystemLifecycleEvent::Lock { .. } => EngineEvent::SessionLocked {
                at_utc_ms: boundary_at,
            },
            SystemLifecycleEvent::Sleep { .. } => EngineEvent::SystemSleep {
                at_utc_ms: boundary_at,
            },
            _ => {
                return Err(SafeError::new(
                    SafeErrorCode::InternalSafeError,
                    "commit_enter_boundary 只能用于进入事件",
                ));
            }
        };

        let (ack_tx, ack_rx) = oneshot::channel();
        if let Err(send_error) = self
            .send_control(WriterControl::Lifecycle {
                event: engine_event,
                barrier_id: barrier_id.clone(),
                expected_revision,
                ack: ack_tx,
            })
            .await
        {
            // control 未入队：边界未提交，保持 suppression。
            if self.end_transition(false).is_err() {
                self.force_stopped_keep_suppression(
                    ErrorSource::LifecyclePump,
                    SafeErrorCode::InternalSafeError,
                );
            } else {
                self.shared
                    .set_error(ErrorSource::LifecyclePump, SafeErrorCode::InternalSafeError);
            }
            return Err(SafeError::new(
                SafeErrorCode::InternalSafeError,
                format!("控制消息未入队（{send_error:?}），系统事件边界未提交，保持安全冻结"),
            ));
        }

        let mut accepted = AcceptedControlGuard::new(self);
        let ack_result = tokio::time::timeout(WRITER_ACK_TIMEOUT, ack_rx).await;
        accepted.disarm();

        match ack_result {
            Err(_) | Ok(Err(_)) => {
                // unknown outcome：沿用 4.3.1 writer_fault fencing。
                Err(self.enter_unknown_outcome_fault())
            }
            Ok(Ok(Err(storage))) => {
                // Writer 明确失败：保持 suppression，保留诊断。
                let _ = self.end_transition(false);
                Err(SafeError::new(
                    storage.code,
                    "系统事件边界提交失败，保持安全冻结",
                ))
            }
            Ok(Ok(Ok(()))) => {
                // 提交成功：标记 committed。
                {
                    let mut state = self.state.lock().expect("coordinator state");
                    source
                        .lock_sleep_mut(&mut state.suppressions)
                        .mark_committed();
                }
                match self.end_transition(false) {
                    Ok(()) => {
                        self.shared.clear_error(ErrorSource::LifecyclePump);
                        Ok(())
                    }
                    Err(_) => {
                        // 已提交但最终 publish 失败：安全停止，不回滚已提交事实。
                        self.force_stopped_keep_suppression(
                            ErrorSource::LifecyclePump,
                            SafeErrorCode::InternalSafeError,
                        );
                        Err(SafeError::new(
                            SafeErrorCode::InternalSafeError,
                            "系统事件已提交但采集流水线不可用，采集已安全停止",
                        ))
                    }
                }
            }
        }
    }

    /// 处理解除事件（Unlock/Resume）：只清对应 suppression；
    /// 若全部解除 + desired Running → 恢复采集。
    fn apply_release_event(&self, event: SystemLifecycleEvent) -> Result<(), SafeError> {
        let source = event_source(&event);
        {
            let mut state = self.state.lock().expect("coordinator state");
            let ls = source.lock_sleep_mut(&mut state.suppressions);
            if !ls.active {
                // 乱序 release：幂等 no-op。
                return Ok(());
            }
            ls.reset();
        }
        // 不创建反向 Writer 边界。若所有 suppression 解除 → 恢复。
        match self.publish() {
            Ok(()) => Ok(()),
            Err(error) => {
                // publish 失败（如 capture watch 无消费者）：fail-closed。
                self.force_stopped(ErrorSource::LifecyclePump, SafeErrorCode::InternalSafeError);
                Err(error)
            }
        }
    }

    /// lifecycle_monitor_fault 是否已锁存。
    fn monitor_faulted(&self) -> bool {
        self.state
            .lock()
            .expect("coordinator state")
            .suppressions
            .lifecycle_monitor_fault
    }

    /// 锁存 lifecycle_monitor_fault（事件泵永久失效）。
    /// 与 writer_fault 同级：本进程内不可由普通 Start/Resume 清除。
    pub fn latch_monitor_fault(&self) {
        {
            let mut state = self.state.lock().expect("coordinator state");
            state.suppressions.lifecycle_monitor_fault = true;
            state.desired = CaptureState::Stopped;
            state.last_published = CaptureState::Stopped;
        }
        self.capture_state_tx.send_replace(CaptureState::Stopped);
        self.shared.set_capture_state(CaptureState::Stopped);
        self.shared
            .set_error(ErrorSource::LifecyclePump, SafeErrorCode::InternalSafeError);
    }

    /// 安全停止但保留当前 suppression 状态（已提交后 publish 失败、active 冻结保持）。
    fn force_stopped_keep_suppression(&self, source: ErrorSource, code: SafeErrorCode) {
        {
            let mut state = self.state.lock().expect("coordinator state");
            state.desired = CaptureState::Stopped;
            state.suppressions.transition = false;
            state.last_published = CaptureState::Stopped;
        }
        self.capture_state_tx.send_replace(CaptureState::Stopped);
        self.shared.set_capture_state(CaptureState::Stopped);
        self.shared.set_error(source, code);
    }

    /// 注入 Barrier 并等待 injected ack（阶段 4.2 可靠注入协议）。
    async fn inject(
        &self,
        kind: BarrierKind,
        expected_revision: i64,
    ) -> Result<BarrierId, BarrierInjectError> {
        let barrier_id = BarrierId::new();
        crate::barrier::inject_barrier(
            &self.barrier_request_tx,
            BarrierToken {
                id: barrier_id.clone(),
                kind,
                expected_revision,
            },
        )
        .await?;
        Ok(barrier_id)
    }

    /// 有界取得 control lane permit。Timeout/Closed 都发生在 permit.send 前，
    /// 调用方可据此稳定声明 control 未入队。
    async fn send_control(&self, control: WriterControl) -> Result<(), ControlSendError> {
        let permit = tokio::time::timeout(CONTROL_SEND_TIMEOUT, self.control_tx.reserve())
            .await
            .map_err(|_| ControlSendError::Timeout)?
            .map_err(|_| ControlSendError::Closed)?;
        permit.send(control);
        Ok(())
    }

    /// 生产 supervisor 的唯一任务退出入口。与所有 transition 使用同一把锁，
    /// 因而任务死亡与控制结果解释不会发生状态覆盖竞态。
    pub async fn report_pipeline_exit(&self, task: PipelineTask) {
        let _guard = self.lock.lock().await;
        self.latch_writer_fault();
        eprintln!("生产流水线任务退出（{task:?}），采集已安全停止");
    }

    /// 开始 transition：设置 transition suppression（与可选的新 desired），发布 effective。
    /// 发布失败（capture watch 无消费者）由调用方转入安全停止。
    fn begin_transition(&self, desired: Option<CaptureState>) -> Result<(), SafeError> {
        {
            let mut state = self.state.lock().expect("coordinator state");
            state.suppressions.transition = true;
            if let Some(desired) = desired {
                state.desired = desired;
            }
        }
        self.publish()
    }

    /// 结束 transition：解除 transition suppression；fault=true 时进入 lifecycle
    /// 安全冻结。发布前先采用 Writer 故障路径（mark_fatal）可能写入的外部状态，
    /// 保证 shared/watch/DTO 一致。
    fn end_transition(&self, fault: bool) -> Result<(), SafeError> {
        {
            let mut state = self.state.lock().expect("coordinator state");
            state.suppressions.transition = false;
            if fault {
                state.suppressions.fault = true;
            }
        }
        self.sync_external();
        self.publish()
    }

    /// Settings 未提交路径的统一收尾：last-known-good 继续生效；解冻失败
    /// （采集任务已退出）时安全停止，绝不虚假 Running。
    fn end_settings_transition_uncommitted(&self) {
        if self.end_transition(false).is_err() {
            self.force_stopped(
                ErrorSource::Settings,
                SafeErrorCode::SettingsSavedNotApplied,
            );
        } else {
            self.shared.set_error(
                ErrorSource::Settings,
                SafeErrorCode::SettingsSavedNotApplied,
            );
        }
    }

    /// S2-06：Writer ack 在 operation deadline 内未返回——提交结果未知
    /// （不得声称未提交、不得重发相同副作用、Writer 迟到完成也不解除）。
    /// 锁存 writer_fault（本进程内后续 transition 全部拒绝）、强制 Stopped、
    /// IPC 保持在线；返回稳定 AGENT_WRITER_FAULTED。
    fn enter_unknown_outcome_fault(&self) -> SafeError {
        self.latch_writer_fault();
        SafeError::new(
            SafeErrorCode::AgentWriterFaulted,
            "提交结果未知（Writer 未给出可证明终态的确认），采集已安全停止，需要重启 Agent 或诊断",
        )
    }

    /// 锁存进程内不可恢复的流水线/Writer fault。该方法同步且幂等，供 ack
    /// unknown、取消 guard 与生产 supervisor 共用；迟到 ack 不会清除此状态。
    /// 诊断精度：Writer 已留下更精确的 fatal 诊断（如 IO 错误码、阶段 4.4
    /// 的 SETTINGS_CONFLICT 协议违例）时保留之，不覆盖为泛化的
    /// AGENT_WRITER_FAULTED（与 apply_capture_command 最终 publish 失败路径
    /// 的既有规则一致）；无既有诊断时才写入 AGENT_WRITER_FAULTED。
    fn latch_writer_fault(&self) {
        {
            let mut state = self.state.lock().expect("coordinator state");
            state.suppressions.transition = false;
            state.suppressions.writer_fault = true;
            state.desired = CaptureState::Stopped;
            state.last_published = CaptureState::Stopped;
        }
        self.capture_state_tx.send_replace(CaptureState::Stopped);
        self.shared.set_capture_state(CaptureState::Stopped);
        self.shared.set_writer_state(WriterState::Faulted);
        self.shared.set_process_state(ProcessState::Faulted);
        if !self.shared.errors().contains_key(&ErrorSource::Writer) {
            self.shared
                .set_error(ErrorSource::Writer, SafeErrorCode::AgentWriterFaulted);
        }
    }

    /// Writer/Process fatal 是否已锁存（复审 P1-01）。
    fn writer_faulted(&self) -> bool {
        self.state
            .lock()
            .expect("coordinator state")
            .suppressions
            .writer_fault
    }

    /// 采用外部状态：Writer 故障路径（mark_fatal 是唯一绕过 Coordinator 的故障
    /// 停止通道）把 watch 与 shared 同时写为 Stopped，并标记 WriterState/ProcessState
    /// Faulted。本函数把外部状态并入 desired，并锁存不可由用户命令解除的
    /// writer_fault suppression（复审 P1-01）。
    fn sync_external(&self) {
        let capture = self.shared.capture_state();
        let fatal = self.shared.writer_state() == WriterState::Faulted
            || self.shared.process_state() == ProcessState::Faulted;
        let mut state = self.state.lock().expect("coordinator state");
        if fatal {
            state.suppressions.writer_fault = true;
            state.desired = CaptureState::Stopped;
            state.last_published = CaptureState::Stopped;
            return;
        }
        if capture != state.last_published {
            state.desired = capture;
            state.last_published = capture;
        }
    }

    /// 唯一状态发布点（复审 P1-02）：watch 与 SharedState 永远写同一个 effective 值。
    /// - capture watch 无消费者（采集任务已退出）→ 失败；
    /// - 发布 Running 前必须确认控制面健康（RAII 任务存活 + barrier/control
    ///   channel 存活），不允许仅凭 SharedState 推断任务存活；
    /// - 失败时不写 watch、不写 shared，调用方决定回退策略。
    fn publish(&self) -> Result<(), SafeError> {
        let effective = self.state.lock().expect("coordinator state").effective();
        if self.capture_state_tx.receiver_count() == 0 {
            return Err(SafeError::new(
                SafeErrorCode::InternalSafeError,
                "采集消费任务已退出，状态无法交付",
            ));
        }
        if effective == CaptureState::Running {
            self.ensure_control_plane()?;
        }
        // send_replace 保留最新值；零 receiver 已在上方显式拒绝，不用它掩盖任务死亡。
        self.capture_state_tx.send_replace(effective);
        self.shared.set_capture_state(effective);
        self.state.lock().expect("coordinator state").last_published = effective;
        Ok(())
    }

    /// Running 发布前置条件：三个生产任务存活（RAII 守卫维护）且
    /// barrier/control channel 未关闭。
    fn ensure_control_plane(&self) -> Result<(), SafeError> {
        if self.health.all_alive()
            && !self.barrier_request_tx.is_closed()
            && !self.control_tx.is_closed()
        {
            Ok(())
        } else {
            Err(SafeError::new(
                SafeErrorCode::InternalSafeError,
                "采集流水线任务不可用，无法恢复采集",
            ))
        }
    }

    /// 安全停止：发布/控制面失败后的统一回退。绕过 publish 的健康检查直接写
    /// Stopped（采集此时已不可能进行），并设置来源明确的诊断。
    fn force_stopped(&self, source: ErrorSource, code: SafeErrorCode) {
        self.force_stopped_keep_diagnostic();
        self.shared.set_error(source, code);
    }

    /// 安全停止但保留既有诊断（调用方已确保诊断精确，如 Writer 按 storage.code
    /// 设置的 Settings 来源错误）。
    fn force_stopped_keep_diagnostic(&self) {
        {
            let mut state = self.state.lock().expect("coordinator state");
            state.desired = CaptureState::Stopped;
            state.suppressions.transition = false;
            state.suppressions.fault = true;
            state.last_published = CaptureState::Stopped;
        }
        // 尽力交付（可能已无 receiver）；shared/DTO 必须是 Stopped。
        self.capture_state_tx.send_replace(CaptureState::Stopped);
        self.shared.set_capture_state(CaptureState::Stopped);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn capture_transition_table_matches_baseline() {
        // start：stopped → running；running 幂等；paused 拒绝。
        assert_eq!(
            capture_transition(CaptureState::Stopped, "capture_start"),
            Ok(CaptureState::Running)
        );
        assert_eq!(
            capture_transition(CaptureState::Running, "capture_start"),
            Err(Some(CaptureState::Running))
        );
        assert_eq!(
            capture_transition(CaptureState::Paused, "capture_start"),
            Err(None)
        );
        // pause：running → paused；paused 幂等；stopped 拒绝。
        assert_eq!(
            capture_transition(CaptureState::Running, "capture_pause"),
            Ok(CaptureState::Paused)
        );
        assert_eq!(
            capture_transition(CaptureState::Paused, "capture_pause"),
            Err(Some(CaptureState::Paused))
        );
        assert_eq!(
            capture_transition(CaptureState::Stopped, "capture_pause"),
            Err(None)
        );
        // resume：paused → running；running 幂等；stopped 拒绝。
        assert_eq!(
            capture_transition(CaptureState::Paused, "capture_resume"),
            Ok(CaptureState::Running)
        );
        assert_eq!(
            capture_transition(CaptureState::Running, "capture_resume"),
            Err(Some(CaptureState::Running))
        );
        assert_eq!(
            capture_transition(CaptureState::Stopped, "capture_resume"),
            Err(None)
        );
        // stop：running/paused → stopped；stopped 幂等。
        assert_eq!(
            capture_transition(CaptureState::Running, "capture_stop"),
            Ok(CaptureState::Stopped)
        );
        assert_eq!(
            capture_transition(CaptureState::Paused, "capture_stop"),
            Ok(CaptureState::Stopped)
        );
        assert_eq!(
            capture_transition(CaptureState::Stopped, "capture_stop"),
            Err(Some(CaptureState::Stopped))
        );
    }

    #[test]
    fn unknown_command_has_no_transition() {
        assert_eq!(capture_transition(CaptureState::Running, "nope"), Err(None));
    }

    /// effective gate：任一 suppression 置位都关闭 gate；desired Running 被抑制时
    /// 发布 Paused（采集实际未进行）。writer_fault 与 fault 独立生效（复审 P1-01）。
    #[test]
    fn effective_gate_reflects_suppressions() {
        let base = |desired| CoordinatorState {
            desired,
            last_published: desired,
            suppressions: Suppressions::default(),
        };
        assert_eq!(
            base(CaptureState::Running).effective(),
            CaptureState::Running
        );
        assert_eq!(base(CaptureState::Paused).effective(), CaptureState::Paused);
        assert_eq!(
            base(CaptureState::Stopped).effective(),
            CaptureState::Stopped
        );

        let mut state = base(CaptureState::Running);
        state.suppressions.transition = true;
        assert_eq!(state.effective(), CaptureState::Paused);
        state.suppressions.transition = false;
        state.suppressions.fault = true;
        assert_eq!(state.effective(), CaptureState::Paused);
        state.suppressions.fault = false;
        state.suppressions.writer_fault = true;
        assert_eq!(state.effective(), CaptureState::Paused);
        state.suppressions.writer_fault = false;
        state.suppressions.lifecycle_monitor_fault = true;
        assert_eq!(state.effective(), CaptureState::Paused);
        state.suppressions.lifecycle_monitor_fault = false;
        state.suppressions.lock.active = true;
        assert_eq!(state.effective(), CaptureState::Paused);
        state.suppressions.lock.active = false;
        state.suppressions.sleep.active = true;
        assert_eq!(state.effective(), CaptureState::Paused);
    }
}
