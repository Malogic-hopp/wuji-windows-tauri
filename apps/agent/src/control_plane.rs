//! 生产控制面装配（阶段 4.3 复审 P2-01）：`main.rs` 与生产接线测试共用同一函数。
//!
//! 装配保证（结构性，非人工约定）：
//! - 唯一 `Arc<CaptureCoordinator>`；
//! - BarrierRequest sender 只移交给 Coordinator（从不暴露给调用方）；
//! - `WriterControl::Lifecycle/SettingsApplied` 只能由 Coordinator 构造——
//!   完整 `mpsc::Sender<WriterControl>` 不暴露给任何业务调用方；
//! - Heartbeat/Checkpoint/Shutdown 经窄通道 `MaintenanceControl`（类型上就无法
//!   构造 transition control）；
//! - CommandServer/reconciler/session-power 只获得 Coordinator 克隆。

use std::sync::Arc;

use tokio::sync::{mpsc, oneshot, watch};
use wuji_core::domain::CaptureState;
use wuji_core::settings::Settings;

use crate::capture_coordinator::CaptureCoordinator;
use crate::pipeline_health::{PipelineHealth, PipelineTask};
use crate::shared::SharedState;
use crate::writer_task::{HeartbeatSnapshot, WriterControl};

/// control lane 容量（09 §5.2）。
pub const CONTROL_LANE_CAPACITY: usize = 64;
/// Barrier 请求通道容量。
pub const BARRIER_REQUEST_CAPACITY: usize = 64;

/// 控制通道已关闭（Writer 任务退出）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ControlClosed;

/// Heartbeat/Checkpoint/Shutdown 的窄控制通道。
/// 类型上无法构造 Lifecycle/SettingsApplied（transition control 只能由
/// Coordinator 创建，复审 P2-01）。
#[derive(Clone)]
pub struct MaintenanceControl {
    tx: mpsc::Sender<WriterControl>,
}

impl MaintenanceControl {
    /// 心跳（09 §5.2；写满时等待容量，不丢弃）。
    pub async fn heartbeat(&self, snapshot: HeartbeatSnapshot) -> Result<(), ControlClosed> {
        self.tx
            .send(WriterControl::Heartbeat(snapshot))
            .await
            .map_err(|_| ControlClosed)
    }

    /// WAL checkpoint（MaintenanceLite 唯一维护动作）。
    pub async fn checkpoint(&self) -> Result<(), ControlClosed> {
        self.tx
            .send(WriterControl::Checkpoint)
            .await
            .map_err(|_| ControlClosed)
    }

    /// 受控退出：发送 Shutdown 并等待 Writer 终态提交（09 §5.2）。
    pub async fn shutdown(&self) -> Result<(), ControlClosed> {
        let (ack_tx, ack_rx) = oneshot::channel();
        self.tx
            .send(WriterControl::Shutdown { ack: ack_tx })
            .await
            .map_err(|_| ControlClosed)?;
        ack_rx.await.map_err(|_| ControlClosed)
    }
}

/// 生产控制面：唯一 Coordinator + 三个生产任务的接线端。
pub struct ControlPlane {
    /// 唯一 CaptureCoordinator（CommandServer/reconciler/session-power 只拿它的克隆）。
    pub coordinator: Arc<CaptureCoordinator>,
    /// Heartbeat/Checkpoint/Shutdown 的窄通道。
    pub maintenance: MaintenanceControl,
    /// 生产任务健康句柄（各任务进入时登记 RAII 守卫）。
    pub health: Arc<PipelineHealth>,
    /// 生产任务退出事件；main 的 supervisor 必须持续消费并通知 Coordinator。
    pub pipeline_exit_rx: mpsc::UnboundedReceiver<PipelineTask>,
    /// 交给 Capture Loop 的 capture watch 接收端。
    pub capture_state_rx: watch::Receiver<CaptureState>,
    /// 交给 Capture Loop/Processor 的 settings watch 接收端（可 clone）。
    pub settings_rx: watch::Receiver<Settings>,
    /// 交给 Capture Loop 的 Barrier 请求接收端。
    pub barrier_request_rx: mpsc::Receiver<crate::barrier::BarrierRequest>,
    /// 交给 WriterTask 的 control 接收端。
    pub control_rx: mpsc::Receiver<WriterControl>,
    /// 仅供 WriterTask mark_fatal 故障安全停止使用的 capture watch 发送端。
    pub writer_capture_stop_tx: watch::Sender<CaptureState>,
}

/// 装配生产控制面（唯一入口；`main.rs` 与 `tests/production_wiring.rs` 共用）。
pub fn assemble(
    shared: Arc<SharedState>,
    settings: Settings,
    initial_capture: CaptureState,
) -> ControlPlane {
    let (health, pipeline_exit_rx) = PipelineHealth::with_exit_events();
    let (settings_tx, settings_rx) = watch::channel(settings);
    let (capture_state_tx, capture_state_rx) = watch::channel(initial_capture);
    let (control_tx, control_rx) = mpsc::channel::<WriterControl>(CONTROL_LANE_CAPACITY);
    // BarrierRequest sender 在此之后只存在于 Coordinator 内部。
    let (barrier_request_tx, barrier_request_rx) =
        crate::barrier::barrier_request_channel(BARRIER_REQUEST_CAPACITY);

    let coordinator = Arc::new(CaptureCoordinator::new(
        barrier_request_tx,
        capture_state_tx.clone(),
        control_tx.clone(),
        shared,
        settings_tx,
        initial_capture,
        health.clone(),
    ));

    ControlPlane {
        coordinator,
        maintenance: MaintenanceControl { tx: control_tx },
        health,
        pipeline_exit_rx,
        capture_state_rx,
        settings_rx,
        barrier_request_rx,
        control_rx,
        writer_capture_stop_tx: capture_state_tx,
    }
}

/// 持续把生产任务退出事件收敛到唯一 Coordinator。退出事件在 guard Drop 时
/// 已写入无界队列，因此 supervisor 即使稍后启动也不会漏掉启动窗口内的死亡。
pub async fn supervise_pipeline_exits(
    mut exit_rx: mpsc::UnboundedReceiver<PipelineTask>,
    coordinator: Arc<CaptureCoordinator>,
) {
    while let Some(task) = exit_rx.recv().await {
        coordinator.report_pipeline_exit(task).await;
    }
}
