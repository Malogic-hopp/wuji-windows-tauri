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
use crate::shared::SharedState;

/// data lane 消息就是 ProcessorOutput（内部已携带 epoch）。
pub type WriterDataMessage = ProcessorOutput;

pub enum WriterControl {
    /// 生命周期边界（Pause/Stop/Sleep/Lock）。ack 在事务提交后返回（09 §5.2）。
    Lifecycle {
        event: EngineEvent,
        ack: oneshot::Sender<StorageResult<()>>,
    },
    /// Agent 已应用新 settings（09 §9.1）。
    SettingsApplied {
        settings: Settings,
        at_utc_ms: i64,
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
}

impl WriterTask {
    pub fn new(
        writer: Writer,
        engine: ActivityEngine,
        shared: Arc<SharedState>,
        capture_state_tx: watch::Sender<CaptureState>,
    ) -> Self {
        Self {
            writer,
            engine,
            shared,
            capture_state_tx,
        }
    }

    pub fn into_parts(self) -> (Writer, ActivityEngine) {
        (self.writer, self.engine)
    }

    /// biased 双 lane 循环；Shutdown 或通道全闭后返回。
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
                        Some(WriterControl::Lifecycle { event, ack }) => {
                            // 生命周期边界先排空 data backlog，保证因果顺序。
                            self.drain_data(&mut data_rx).await;
                            let result = self.engine.handle(&mut self.writer, event);
                            if let Err(error) = &result {
                                self.mark_fatal(error);
                            }
                            let _ = ack.send(result);
                        }
                        Some(WriterControl::Shutdown { ack }) => {
                            self.drain_data(&mut data_rx).await;
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
            WriterControl::SettingsApplied {
                settings,
                at_utc_ms,
                ack,
            } => {
                let revision = settings.revision.parse::<i64>().unwrap_or(-1);
                let result = self
                    .engine
                    .apply_settings(&mut self.writer, settings, at_utc_ms)
                    .map(|()| revision);
                if let Err(error) = &result {
                    self.shared.set_safe_error(Some(error.code));
                }
                let _ = ack.send(result);
                true
            }
            WriterControl::Heartbeat(snapshot) => {
                if let Err(error) = self.write_heartbeat(&snapshot) {
                    self.mark_fatal(&error);
                    return true;
                }
                self.shared.note_heartbeat(
                    snapshot.heartbeat_at_utc_ms,
                    None,
                    snapshot.capture_queue_depth as u32,
                    snapshot.writer_queue_depth as u32,
                    snapshot.dropped_capture_count as u64,
                    snapshot.dropped_writer_count as u64,
                );
                true
            }
            WriterControl::Checkpoint => {
                // checkpoint busy 不阻断写入（09 §5.2）：失败仅留安全诊断，下周期重试。
                if let Err(error) = self.writer.checkpoint_truncate() {
                    self.shared.set_safe_error(Some(error.code));
                }
                true
            }
            WriterControl::Lifecycle { .. } | WriterControl::Shutdown { .. } => {
                unreachable!("Lifecycle/Shutdown 已在 run 中单独处理")
            }
        }
    }

    /// 排空 data backlog（生命周期控制前保证因果顺序）。
    async fn drain_data(&mut self, data_rx: &mut mpsc::Receiver<WriterDataMessage>) {
        while let Ok(message) = data_rx.try_recv() {
            self.process_data(message).await;
        }
    }

    /// 处理单条 data 消息：busy 时引擎整体回滚并以 100/250ms 重试（09 §5.2）。
    async fn process_data(&mut self, message: WriterDataMessage) -> bool {
        let event = match message {
            ProcessorOutput::Observation(obs) => EngineEvent::Observation(obs),
            ProcessorOutput::PrivacyExcluded {
                captured_at_utc_ms, ..
            } => EngineEvent::PrivacyExcluded { captured_at_utc_ms },
            ProcessorOutput::CaptureError {
                captured_at_utc_ms, ..
            } => EngineEvent::CaptureError { captured_at_utc_ms },
        };
        let snapshot = self.engine.snapshot();
        let mut attempt = 0_u32;
        loop {
            match self.engine.handle(&mut self.writer, event.clone()) {
                Ok(()) => {
                    if let EngineEvent::Observation(obs) = &event {
                        self.shared.note_observation(obs.captured_at_utc_ms);
                    }
                    if attempt > 0 && self.shared.writer_state() == WriterState::Degraded {
                        self.shared.set_writer_state(WriterState::Healthy);
                    }
                    return true;
                }
                Err(error) if error.code == SafeErrorCode::AgentWriterDegraded && attempt < 2 => {
                    // busy：回滚后按 100/250ms 退避重试，不确认未提交消息（09 §5.2）。
                    self.engine.restore(&snapshot);
                    self.shared.set_writer_state(WriterState::Degraded);
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
            None,
        )?;
        tx.commit()
    }

    /// 不可恢复写入失败：停止 Capture、Writer faulted、IPC 保持在线（09 §5.2）。
    fn mark_fatal(&mut self, error: &StorageError) {
        self.shared.set_writer_state(WriterState::Faulted);
        self.shared.set_safe_error(Some(error.code));
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
