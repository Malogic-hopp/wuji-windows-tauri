//! Agent 进程内共享状态：IPC 状态查询与心跳共用的内存快照（09 §8.1、§10.4）。
//!
//! status_get 从这里取实时状态；SQLite agent_runtime 只表示最后已持久化心跳。

use std::sync::RwLock;

use wuji_core::domain::{CaptureState, ProcessState, WriterState};
use wuji_core::dto::{AgentStatusDto, Int64String, RuntimeId};
use wuji_core::error::SafeErrorCode;

#[derive(Debug)]
struct SharedInner {
    process_state: ProcessState,
    capture_state: CaptureState,
    writer_state: WriterState,
    heartbeat_at_utc_ms: Option<i64>,
    last_observation_at_utc_ms: Option<i64>,
    last_write_at_utc_ms: Option<i64>,
    capture_queue_depth: u32,
    writer_queue_depth: u32,
    dropped_capture_count: u64,
    dropped_writer_count: u64,
    safe_error_code: Option<SafeErrorCode>,
}

/// 进程级共享状态（内部可变性，读多写少）。
#[derive(Debug)]
pub struct SharedState {
    agent_version: String,
    runtime_id: RuntimeId,
    inner: RwLock<SharedInner>,
}

impl SharedState {
    pub fn new(agent_version: String, runtime_id: RuntimeId) -> Self {
        Self {
            agent_version,
            runtime_id,
            inner: RwLock::new(SharedInner {
                process_state: ProcessState::Starting,
                capture_state: CaptureState::Stopped,
                writer_state: WriterState::Healthy,
                heartbeat_at_utc_ms: None,
                last_observation_at_utc_ms: None,
                last_write_at_utc_ms: None,
                capture_queue_depth: 0,
                writer_queue_depth: 0,
                dropped_capture_count: 0,
                dropped_writer_count: 0,
                safe_error_code: None,
            }),
        }
    }

    pub fn runtime_id(&self) -> RuntimeId {
        self.runtime_id.clone()
    }

    pub fn agent_version(&self) -> String {
        self.agent_version.clone()
    }

    pub fn status_dto(&self) -> AgentStatusDto {
        let inner = self.inner.read().expect("shared state lock");
        AgentStatusDto {
            agent_version: self.agent_version.clone(),
            protocol_version: 1,
            schema_version: 1,
            process_state: inner.process_state,
            capture_state: inner.capture_state,
            writer_state: inner.writer_state,
            runtime_id: self.runtime_id.clone(),
            heartbeat_at_utc_ms: inner.heartbeat_at_utc_ms.map(Int64String),
            last_observation_at_utc_ms: inner.last_observation_at_utc_ms.map(Int64String),
            last_write_at_utc_ms: inner.last_write_at_utc_ms.map(Int64String),
            capture_queue_depth: inner.capture_queue_depth,
            writer_queue_depth: inner.writer_queue_depth,
            dropped_capture_count: Int64String(inner.dropped_capture_count as i64),
            dropped_writer_count: Int64String(inner.dropped_writer_count as i64),
            safe_error_code: inner.safe_error_code,
        }
    }

    pub fn set_process_state(&self, state: ProcessState) {
        self.inner.write().expect("shared state lock").process_state = state;
    }

    pub fn process_state(&self) -> ProcessState {
        self.inner.read().expect("shared state lock").process_state
    }

    pub fn set_capture_state(&self, state: CaptureState) {
        self.inner.write().expect("shared state lock").capture_state = state;
    }

    pub fn capture_state(&self) -> CaptureState {
        self.inner.read().expect("shared state lock").capture_state
    }

    pub fn set_writer_state(&self, state: WriterState) {
        self.inner.write().expect("shared state lock").writer_state = state;
    }

    pub fn writer_state(&self) -> WriterState {
        self.inner.read().expect("shared state lock").writer_state
    }

    pub fn set_safe_error(&self, code: Option<SafeErrorCode>) {
        self.inner
            .write()
            .expect("shared state lock")
            .safe_error_code = code;
    }

    pub fn note_observation(&self, at_utc_ms: i64) {
        self.inner
            .write()
            .expect("shared state lock")
            .last_observation_at_utc_ms = Some(at_utc_ms);
    }

    /// 心跳快照更新（queue depth、drop 计数、各状态由调用方汇总传入）。
    pub fn note_heartbeat(
        &self,
        at_utc_ms: i64,
        last_write_at_utc_ms: Option<i64>,
        capture_queue_depth: u32,
        writer_queue_depth: u32,
        dropped_capture_count: u64,
        dropped_writer_count: u64,
    ) {
        let mut inner = self.inner.write().expect("shared state lock");
        inner.heartbeat_at_utc_ms = Some(at_utc_ms);
        inner.last_write_at_utc_ms = last_write_at_utc_ms.or(inner.last_write_at_utc_ms);
        inner.capture_queue_depth = capture_queue_depth;
        inner.writer_queue_depth = writer_queue_depth;
        inner.dropped_capture_count = dropped_capture_count;
        inner.dropped_writer_count = dropped_writer_count;
    }
}
