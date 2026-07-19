//! 与 schema-v0.1.sql 对齐的领域枚举。
//!
//! 字符串值必须与 docs/rebuild/schema-v0.1.sql 的 CHECK 枚举完全一致；
//! 改值即改持久化合同，v0.1 内不允许。

use serde::{Deserialize, Serialize};
use specta::Type;

/// 单条 Observation / Segment 的活动状态（09 §6.1）。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Type)]
#[serde(rename_all = "lowercase")]
pub enum ActivityState {
    Active,
    Idle,
    Unknown,
}

/// 采集质量（09 §6.1；schema `foreground_observations.quality`）。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Type)]
#[serde(rename_all = "snake_case")]
pub enum CaptureQuality {
    Normal,
    ProcessNameFallback,
    IdleUnavailable,
}

/// 数据缺口类型（schema `capture_gaps.kind`）。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Type)]
#[serde(rename_all = "snake_case")]
pub enum GapKind {
    SamplingTransition,
    CaptureDelayed,
    PrivacyExcluded,
    CaptureQueueDrop,
    WriterQueueDrop,
    CapturePaused,
    CaptureStopped,
    SystemSleep,
    SessionLocked,
    AgentRestart,
    ClockChanged,
    CaptureError,
}

/// open/closed 行状态（schema 各 `status` 列）。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Type)]
#[serde(rename_all = "lowercase")]
pub enum RowStatus {
    Open,
    Closed,
}

/// Activity Segment 关闭原因（schema `activity_segments.close_reason`）。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Type)]
#[serde(rename_all = "snake_case")]
pub enum SegmentCloseReason {
    AppChanged,
    StateChanged,
    CaptureDelayed,
    PrivacyExcluded,
    QueueDrop,
    CapturePaused,
    CaptureStopped,
    SystemSleep,
    SessionLocked,
    AgentRestart,
    ClockChanged,
    AgentShutdown,
}

/// Work Block 关闭原因（schema `work_blocks.close_reason`）。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Type)]
#[serde(rename_all = "snake_case")]
pub enum WorkBlockCloseReason {
    IdleBreak,
    PrivacyExcluded,
    QueueDrop,
    CapturePaused,
    CaptureStopped,
    SystemSleep,
    SessionLocked,
    AgentRestart,
    ClockChanged,
    AgentShutdown,
}

/// Agent 进程状态（schema `agent_runtime.process_state`）。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Type)]
#[serde(rename_all = "snake_case")]
pub enum ProcessState {
    Starting,
    Running,
    Degraded,
    Faulted,
    ShuttingDown,
    Stopped,
}

/// 采集状态（schema `agent_runtime.capture_state`）。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Type)]
#[serde(rename_all = "snake_case")]
pub enum CaptureState {
    Stopped,
    Running,
    Paused,
}

/// Writer 状态（schema `agent_runtime.writer_state`）。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Type)]
#[serde(rename_all = "snake_case")]
pub enum WriterState {
    Healthy,
    Degraded,
    Faulted,
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn enum_strings_match_schema_contract() {
        assert_eq!(
            serde_json::to_string(&ActivityState::Unknown).unwrap(),
            "\"unknown\""
        );
        assert_eq!(
            serde_json::to_string(&CaptureQuality::ProcessNameFallback).unwrap(),
            "\"process_name_fallback\""
        );
        assert_eq!(
            serde_json::to_string(&GapKind::SamplingTransition).unwrap(),
            "\"sampling_transition\""
        );
        assert_eq!(serde_json::to_string(&RowStatus::Open).unwrap(), "\"open\"");
        assert_eq!(
            serde_json::to_string(&SegmentCloseReason::AgentShutdown).unwrap(),
            "\"agent_shutdown\""
        );
        assert_eq!(
            serde_json::to_string(&WorkBlockCloseReason::IdleBreak).unwrap(),
            "\"idle_break\""
        );
        assert_eq!(
            serde_json::to_string(&ProcessState::ShuttingDown).unwrap(),
            "\"shutting_down\""
        );
        assert_eq!(
            serde_json::to_string(&CaptureState::Paused).unwrap(),
            "\"paused\""
        );
        assert_eq!(
            serde_json::to_string(&WriterState::Healthy).unwrap(),
            "\"healthy\""
        );
    }
}
