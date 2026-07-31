//! Agent 进程内共享状态：IPC 状态查询与心跳共用的内存快照（09 §8.1、§10.5）。
//!
//! status_get 从这里取实时状态；SQLite agent_runtime 只表示最后已持久化心跳。

use std::sync::RwLock;
use std::sync::atomic::{AtomicBool, AtomicI64, Ordering};

use wuji_core::domain::{CaptureState, ProcessState, WriterState};
use wuji_core::dto::{AgentStatusDto, Int64String, RuntimeId};
use wuji_core::error::{ErrorSet, ErrorSource, SafeErrorCode};

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
    safe_errors: ErrorSet,
}

/// 进程级共享状态（内部可变性，读多写少）。
#[derive(Debug)]
pub struct SharedState {
    agent_version: String,
    runtime_id: RuntimeId,
    inner: RwLock<SharedInner>,
    /// 当前已应用的 settings revision（Writer 成功提交后更新；自动对账据此判断）。
    applied_settings_revision: AtomicI64,
    /// 启动对账无法恢复可信 settings 时置位：拒绝 capture_start（R04）。
    capture_blocked: AtomicBool,
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
                safe_errors: ErrorSet::new(),
            }),
            applied_settings_revision: AtomicI64::new(0),
            capture_blocked: AtomicBool::new(false),
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
            safe_error_code: inner.safe_errors.values().next().copied(),
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

    /// S2-08：按来源设置安全错误（不覆盖其他来源）。
    pub fn set_error(&self, source: ErrorSource, code: SafeErrorCode) {
        self.inner
            .write()
            .expect("shared state lock")
            .safe_errors
            .insert(source, code);
    }

    /// S2-08：清除指定来源的安全错误（恢复）。
    pub fn clear_error(&self, source: ErrorSource) {
        self.inner
            .write()
            .expect("shared state lock")
            .safe_errors
            .remove(&source);
    }

    /// S2-08：兼容旧接口——返回所有错误中按字母序的第一个。
    pub fn safe_error_code(&self) -> Option<SafeErrorCode> {
        self.inner
            .read()
            .expect("shared state lock")
            .safe_errors
            .values()
            .next()
            .copied()
    }

    /// S2-08：返回当前所有错误快照（用于心跳持久化等）。
    pub fn errors(&self) -> ErrorSet {
        self.inner
            .read()
            .expect("shared state lock")
            .safe_errors
            .clone()
    }

    /// 兼容旧调用：同时设置错误并覆盖。用于需要原子替换的场景。
    pub fn set_safe_error(&self, code: Option<SafeErrorCode>) {
        let mut inner = self.inner.write().expect("shared state lock");
        inner.safe_errors.clear();
        if let Some(code) = code {
            // 向后兼容：未指定来源时使用 Writer 作为默认来源。
            inner.safe_errors.insert(ErrorSource::Writer, code);
        }
    }

    pub fn applied_settings_revision(&self) -> i64 {
        self.applied_settings_revision.load(Ordering::Relaxed)
    }

    pub fn set_applied_settings_revision(&self, revision: i64) {
        self.applied_settings_revision
            .store(revision, Ordering::Relaxed);
    }

    pub fn capture_blocked(&self) -> bool {
        self.capture_blocked.load(Ordering::Relaxed)
    }

    pub fn set_capture_blocked(&self, blocked: bool) {
        self.capture_blocked.store(blocked, Ordering::Relaxed);
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

#[cfg(test)]
mod tests {
    use super::*;

    fn shared() -> SharedState {
        SharedState::new("0.1.0".to_string(), RuntimeId::new())
    }

    /// 复审 P2-01：多来源错误共存，按来源设置/清理互不影响。
    #[test]
    fn errors_are_scoped_by_source() {
        let shared = shared();
        shared.set_error(
            ErrorSource::Settings,
            SafeErrorCode::SettingsSavedNotApplied,
        );
        shared.set_error(ErrorSource::Checkpoint, SafeErrorCode::AgentWriterDegraded);
        shared.set_error(ErrorSource::Writer, SafeErrorCode::AgentWriterFaulted);

        // 清除 Settings 来源：其他来源保留。
        shared.clear_error(ErrorSource::Settings);
        let errors = shared.errors();
        assert!(!errors.contains_key(&ErrorSource::Settings));
        assert_eq!(
            errors.get(&ErrorSource::Checkpoint),
            Some(&SafeErrorCode::AgentWriterDegraded)
        );
        assert_eq!(
            errors.get(&ErrorSource::Writer),
            Some(&SafeErrorCode::AgentWriterFaulted)
        );
    }

    /// 复审 P2-01：Settings 成功路径只清除 Settings 来源（模拟重试成功后的恢复）。
    #[test]
    fn settings_recovery_clears_only_settings_source() {
        let shared = shared();
        shared.set_error(
            ErrorSource::Settings,
            SafeErrorCode::SettingsSavedNotApplied,
        );
        shared.set_error(ErrorSource::Writer, SafeErrorCode::AgentWriterFaulted);

        // 模拟 Settings 应用成功：只清 Settings。
        shared.clear_error(ErrorSource::Settings);
        assert!(!shared.errors().contains_key(&ErrorSource::Settings));
        assert!(shared.errors().contains_key(&ErrorSource::Writer));
    }
}
