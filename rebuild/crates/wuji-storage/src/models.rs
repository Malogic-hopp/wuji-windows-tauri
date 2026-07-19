//! 行结构与 schema 元数据。

use chrono_tz::Tz;
use wuji_core::domain::{
    ActivityState, CaptureState, GapKind, ProcessState, RowStatus, WriterState,
};

use crate::error::{Result, StorageError};

/// 09 §5.1：v0.1 固定算法版本。
pub const ALGORITHM_VERSION: &str = "rebuild-v0.1";
/// 09 §7.2：唯一受支持的 schema 版本。
pub const SUPPORTED_SCHEMA_VERSION: i64 = 1;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SchemaMeta {
    pub schema_version: i64,
    pub algorithm_version: String,
    pub created_at_utc_ms: i64,
    pub reporting_time_zone_id: String,
}

impl SchemaMeta {
    pub fn reporting_tz(&self) -> Result<Tz> {
        self.reporting_time_zone_id
            .parse::<Tz>()
            .map_err(|_| StorageError::time_zone_unavailable())
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RuntimeRow {
    pub runtime_id: String,
    pub process_state: ProcessState,
    pub capture_state: CaptureState,
    pub writer_state: WriterState,
    pub started_at_utc_ms: i64,
    pub ended_at_utc_ms: Option<i64>,
    pub heartbeat_at_utc_ms: i64,
    pub last_observation_at_utc_ms: Option<i64>,
    pub last_write_at_utc_ms: Option<i64>,
    pub capture_queue_depth: i64,
    pub writer_queue_depth: i64,
    pub dropped_capture_count: i64,
    pub dropped_writer_count: i64,
    pub continuity_epoch: i64,
    pub safe_error_code: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SegmentRow {
    pub segment_id: i64,
    pub runtime_id: String,
    pub continuity_epoch: i64,
    pub app_id: i64,
    pub app_display_name: String,
    pub activity_state: ActivityState,
    pub start_at_utc_ms: i64,
    pub end_at_utc_ms: i64,
    pub duration_ms: i64,
    pub status: RowStatus,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct WorkBlockRow {
    pub work_block_id: i64,
    pub start_at_utc_ms: i64,
    pub end_at_utc_ms: i64,
    pub active_duration_ms: i64,
    pub short_idle_duration_ms: i64,
    pub status: RowStatus,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct GapRow {
    pub gap_id: i64,
    pub kind: GapKind,
    pub start_at_utc_ms: i64,
    pub end_at_utc_ms: Option<i64>,
    pub status: RowStatus,
    pub event_count: i64,
}

/// 文本枚举解析：值与 schema CHECK 一一对应，未知值按内部安全错误处理（不写库猜测）。
pub(crate) fn parse_activity_state(raw: &str) -> Result<ActivityState> {
    match raw {
        "active" => Ok(ActivityState::Active),
        "idle" => Ok(ActivityState::Idle),
        "unknown" => Ok(ActivityState::Unknown),
        _ => Err(StorageError::internal("数据库包含未知 activity_state")),
    }
}

pub(crate) fn parse_row_status(raw: &str) -> Result<RowStatus> {
    match raw {
        "open" => Ok(RowStatus::Open),
        "closed" => Ok(RowStatus::Closed),
        _ => Err(StorageError::internal("数据库包含未知 status")),
    }
}

pub(crate) fn parse_gap_kind(raw: &str) -> Result<GapKind> {
    match raw {
        "sampling_transition" => Ok(GapKind::SamplingTransition),
        "capture_delayed" => Ok(GapKind::CaptureDelayed),
        "privacy_excluded" => Ok(GapKind::PrivacyExcluded),
        "capture_queue_drop" => Ok(GapKind::CaptureQueueDrop),
        "writer_queue_drop" => Ok(GapKind::WriterQueueDrop),
        "capture_paused" => Ok(GapKind::CapturePaused),
        "capture_stopped" => Ok(GapKind::CaptureStopped),
        "system_sleep" => Ok(GapKind::SystemSleep),
        "session_locked" => Ok(GapKind::SessionLocked),
        "agent_restart" => Ok(GapKind::AgentRestart),
        "clock_changed" => Ok(GapKind::ClockChanged),
        "capture_error" => Ok(GapKind::CaptureError),
        _ => Err(StorageError::internal("数据库包含未知 gap kind")),
    }
}

pub(crate) fn parse_process_state(raw: &str) -> Result<ProcessState> {
    match raw {
        "starting" => Ok(ProcessState::Starting),
        "running" => Ok(ProcessState::Running),
        "degraded" => Ok(ProcessState::Degraded),
        "faulted" => Ok(ProcessState::Faulted),
        "shutting_down" => Ok(ProcessState::ShuttingDown),
        "stopped" => Ok(ProcessState::Stopped),
        _ => Err(StorageError::internal("数据库包含未知 process_state")),
    }
}

pub(crate) fn parse_capture_state(raw: &str) -> Result<CaptureState> {
    match raw {
        "stopped" => Ok(CaptureState::Stopped),
        "running" => Ok(CaptureState::Running),
        "paused" => Ok(CaptureState::Paused),
        _ => Err(StorageError::internal("数据库包含未知 capture_state")),
    }
}

pub(crate) fn parse_writer_state(raw: &str) -> Result<WriterState> {
    match raw {
        "healthy" => Ok(WriterState::Healthy),
        "degraded" => Ok(WriterState::Degraded),
        "faulted" => Ok(WriterState::Faulted),
        _ => Err(StorageError::internal("数据库包含未知 writer_state")),
    }
}
