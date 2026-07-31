//! React/Tauri/Agent 三方共享的 DTO（09 §8.4）。
//!
//! 跨边界表示规则：数据库 ID、UTC 毫秒、duration 毫秒和累计计数器一律使用
//! 十进制字符串（`Int64String`），TypeScript 侧不得转为 number 计算（09 §8.4）。

use serde::{Deserialize, Serialize};
use specta::Type;

use crate::domain::{ActivityState, CaptureState, GapKind, ProcessState, RowStatus, WriterState};
use crate::error::{SafeError, SafeErrorCode};
use crate::settings::Settings;

/// i64 的十进制字符串表示（branded；serde 只接受字符串，避免 JS number 精度问题）。
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Type)]
#[specta(type = String)]
pub struct Int64String(pub i64);

impl std::fmt::Display for Int64String {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{}", self.0)
    }
}

impl From<i64> for Int64String {
    fn from(value: i64) -> Self {
        Self(value)
    }
}

impl Serialize for Int64String {
    fn serialize<S: serde::Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        serializer.serialize_str(&self.0.to_string())
    }
}

impl<'de> Deserialize<'de> for Int64String {
    fn deserialize<D: serde::Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        let raw = String::deserialize(deserializer)?;
        raw.parse::<i64>()
            .map(Int64String)
            .map_err(|_| serde::de::Error::custom("Int64String 必须是十进制整数字符串"))
    }
}

/// 严格 `YYYY-MM-DD` 本地日期（09 §8.4）。
#[derive(Debug, Clone, PartialEq, Eq, PartialOrd, Ord, Type)]
#[specta(type = String)]
pub struct LocalDate(String);

impl LocalDate {
    pub fn parse(raw: &str) -> Result<Self, SafeError> {
        let bytes = raw.as_bytes();
        let valid = bytes.len() == 10
            && bytes[4] == b'-'
            && bytes[7] == b'-'
            && bytes
                .iter()
                .enumerate()
                .all(|(i, b)| i == 4 || i == 7 || b.is_ascii_digit());
        if valid {
            Ok(Self(raw.to_string()))
        } else {
            Err(SafeError::new(
                SafeErrorCode::InvalidArgument,
                "日期必须使用 YYYY-MM-DD 格式",
            ))
        }
    }

    pub fn as_str(&self) -> &str {
        &self.0
    }
}

impl std::fmt::Display for LocalDate {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.0)
    }
}

impl Serialize for LocalDate {
    fn serialize<S: serde::Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        serializer.serialize_str(&self.0)
    }
}

impl<'de> Deserialize<'de> for LocalDate {
    fn deserialize<D: serde::Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        let raw = String::deserialize(deserializer)?;
        Self::parse(&raw).map_err(serde::de::Error::custom)
    }
}

/// ULID 运行实例标识（26 字符；schema `agent_runtime.runtime_id`）。
#[derive(Debug, Clone, PartialEq, Eq, Type)]
#[specta(type = String)]
pub struct RuntimeId(String);

impl RuntimeId {
    pub fn new() -> Self {
        Self(ulid::Ulid::generate().to_string())
    }

    pub fn parse(raw: &str) -> Result<Self, SafeError> {
        if raw.len() == 26 && raw.bytes().all(|b| b.is_ascii_alphanumeric()) {
            Ok(Self(raw.to_string()))
        } else {
            Err(SafeError::new(
                SafeErrorCode::InvalidArgument,
                "runtimeId 必须是 26 字符 ULID",
            ))
        }
    }

    pub fn as_str(&self) -> &str {
        &self.0
    }
}

impl Default for RuntimeId {
    fn default() -> Self {
        Self::new()
    }
}

impl Serialize for RuntimeId {
    fn serialize<S: serde::Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        serializer.serialize_str(&self.0)
    }
}

impl<'de> Deserialize<'de> for RuntimeId {
    fn deserialize<D: serde::Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        let raw = String::deserialize(deserializer)?;
        Self::parse(&raw).map_err(serde::de::Error::custom)
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, Type)]
#[serde(rename_all = "camelCase")]
pub struct AppDto {
    pub app_id: Int64String,
    pub display_name: String,
}

/// `status_get` / `agent_get_status` 的返回（09 §8.4）。
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, Type)]
#[serde(rename_all = "camelCase")]
pub struct AgentStatusDto {
    pub agent_version: String,
    pub protocol_version: u32,
    pub schema_version: u32,
    pub process_state: ProcessState,
    pub capture_state: CaptureState,
    pub writer_state: WriterState,
    pub runtime_id: RuntimeId,
    pub heartbeat_at_utc_ms: Option<Int64String>,
    pub last_observation_at_utc_ms: Option<Int64String>,
    pub last_write_at_utc_ms: Option<Int64String>,
    pub capture_queue_depth: u32,
    pub writer_queue_depth: u32,
    pub dropped_capture_count: Int64String,
    pub dropped_writer_count: Int64String,
    pub safe_error_code: Option<SafeErrorCode>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, Type)]
#[serde(rename_all = "camelCase")]
pub struct TopAppDto {
    pub app: AppDto,
    pub active_duration_ms: Int64String,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, Type)]
#[serde(rename_all = "camelCase")]
pub struct TodayQualityDto {
    /// 该 local date 无非 sampling_transition gap 且 droppedCount 为 0（09 §8.4 字段口径）。
    pub is_complete: bool,
    pub gap_count: Int64String,
    pub dropped_count: Int64String,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, Type)]
#[serde(rename_all = "camelCase")]
pub struct TodayDto {
    pub local_date: LocalDate,
    pub reporting_time_zone_id: String,
    pub active_duration_ms: Int64String,
    pub current_app: Option<AppDto>,
    pub last_app: Option<AppDto>,
    pub longest_work_block_active_ms: Int64String,
    pub work_block_count: Int64String,
    pub raw_app_switch_count: Int64String,
    pub top_apps: Vec<TopAppDto>,
    pub quality: TodayQualityDto,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, Type)]
#[serde(rename_all = "camelCase")]
pub struct TimelineSegmentDto {
    pub segment_id: Int64String,
    pub app: AppDto,
    pub activity_state: ActivityState,
    pub start_at_utc_ms: Int64String,
    pub end_at_utc_ms: Int64String,
    pub duration_ms: Int64String,
    pub status: RowStatus,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, Type)]
#[serde(rename_all = "camelCase")]
pub struct TimelineGapDto {
    pub gap_id: Int64String,
    pub gap_kind: GapKind,
    pub start_at_utc_ms: Int64String,
    pub end_at_utc_ms: Option<Int64String>,
    pub status: RowStatus,
    pub event_count: u32,
}

/// Timeline 分页项（`kind` 判别；09 §8.4）。
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, Type)]
#[serde(tag = "kind", rename_all = "camelCase")]
pub enum TimelineItem {
    #[serde(rename = "segment")]
    Segment(TimelineSegmentDto),
    #[serde(rename = "gap")]
    Gap(TimelineGapDto),
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, Type)]
#[serde(rename_all = "camelCase")]
pub struct TimelinePageDto {
    pub local_date: LocalDate,
    pub reporting_time_zone_id: String,
    pub items: Vec<TimelineItem>,
    pub next_cursor: Option<String>,
}

/// Settings 面向 React 的表示（09 §8.4）：保存与已应用分开显示。
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, Type)]
#[serde(rename_all = "camelCase")]
pub struct SettingsDto {
    pub schema_version: u32,
    pub revision: String,
    pub persisted: bool,
    pub applied_revision: String,
    pub sampling_interval_seconds: u32,
    pub idle_threshold_seconds: u32,
    pub work_break_idle_seconds: u32,
    pub excluded_process_names: Vec<String>,
    pub start_capture_on_login: bool,
}

impl SettingsDto {
    pub fn from_settings(settings: &Settings, persisted: bool, applied_revision: String) -> Self {
        Self {
            schema_version: settings.schema_version,
            revision: settings.revision.clone(),
            persisted,
            applied_revision,
            sampling_interval_seconds: settings.sampling_interval_seconds,
            idle_threshold_seconds: settings.idle_threshold_seconds,
            work_break_idle_seconds: settings.work_break_idle_seconds,
            excluded_process_names: settings.excluded_process_names.clone(),
            start_capture_on_login: settings.start_capture_on_login,
        }
    }
}

/// cursor 内的条目种类（09 §8.4：segment 先于 gap）。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Type)]
#[serde(rename_all = "lowercase")]
pub enum TimelineItemKind {
    Segment,
    Gap,
}

/// Timeline 分页 cursor 的内部表示；线上形态是 opaque base64url 字符串。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TimelineCursor {
    pub start_at_utc_ms: i64,
    pub item_kind: TimelineItemKind,
    pub id: i64,
}

impl TimelineCursor {
    pub fn encode(&self) -> String {
        use base64::Engine as _;
        let json = serde_json::to_vec(self).expect("cursor 序列化不应失败");
        base64::engine::general_purpose::URL_SAFE_NO_PAD.encode(json)
    }

    pub fn decode(raw: &str) -> Result<Self, SafeError> {
        use base64::Engine as _;
        let invalid = || SafeError::new(SafeErrorCode::InvalidArgument, "分页 cursor 无效");
        let bytes = base64::engine::general_purpose::URL_SAFE_NO_PAD
            .decode(raw)
            .map_err(|_| invalid())?;
        serde_json::from_slice(&bytes).map_err(|_| invalid())
    }
}

/// Heatmap 单格（09 §8.4）：稀疏返回，强度等级由 Rust 归一化，前端不得重推。
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, Type)]
#[serde(rename_all = "camelCase")]
pub struct HeatmapCellDto {
    pub local_date: String,
    pub local_hour: u32,
    pub active_duration_ms: Int64String,
    pub idle_duration_ms: Int64String,
    pub unknown_duration_ms: Int64String,
    pub intensity_level: u32,
}

/// Heatmap 响应：以 `range_end_local_date` 为范围终点的最近 days 天 × 24 小时。
/// `today` 始终是查询时 DB reporting 时区下的真实今天，不随历史范围改变；
/// cells 只含时长 > 0 的格子（09 §8.4）。
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, Type)]
#[serde(rename_all = "camelCase")]
pub struct HeatmapDto {
    pub today: LocalDate,
    pub range_end_local_date: LocalDate,
    pub reporting_time_zone_id: String,
    pub days: u32,
    pub cells: Vec<HeatmapCellDto>,
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::domain::WriterState;

    #[test]
    fn int64_serializes_as_decimal_string_only() {
        let value = Int64String(1784300000000);
        assert_eq!(serde_json::to_string(&value).unwrap(), "\"1784300000000\"");
        assert_eq!(
            serde_json::from_str::<Int64String>("\"1784300000000\"").unwrap(),
            value
        );
        assert!(serde_json::from_str::<Int64String>("1784300000000").is_err());
        assert!(serde_json::from_str::<Int64String>("\"12.5\"").is_err());
    }

    #[test]
    fn local_date_validation() {
        assert!(LocalDate::parse("2026-07-18").is_ok());
        assert!(LocalDate::parse("2026/07/18").is_err());
        assert!(LocalDate::parse("2026-7-18").is_err());
        assert!(serde_json::from_str::<LocalDate>("\"2026-07-18\"").is_ok());
    }

    #[test]
    fn runtime_id_roundtrip_and_format() {
        let id = RuntimeId::new();
        assert_eq!(id.as_str().len(), 26);
        assert!(RuntimeId::parse(id.as_str()).is_ok());
        assert!(RuntimeId::parse("short").is_err());
    }

    #[test]
    fn cursor_roundtrip_and_tamper_rejection() {
        let cursor = TimelineCursor {
            start_at_utc_ms: 1784300000000,
            item_kind: TimelineItemKind::Gap,
            id: 42,
        };
        let encoded = cursor.encode();
        assert_eq!(TimelineCursor::decode(&encoded).unwrap(), cursor);
        assert!(TimelineCursor::decode("not-base64!!!").is_err());
        // "{}" 缺字段，同样必须拒绝。
        assert!(TimelineCursor::decode("e30").is_err());
    }

    #[test]
    fn settings_dto_separates_saved_and_applied() {
        let dto = SettingsDto::from_settings(&Settings::default(), false, "0".to_string());
        assert_eq!(dto.revision, "0");
        assert!(!dto.persisted);
        assert_eq!(dto.applied_revision, "0");
        let json = serde_json::to_value(&dto).unwrap();
        assert_eq!(json["schemaVersion"], 1);
        assert_eq!(json["startCaptureOnLogin"], false);
    }

    #[test]
    fn agent_status_uses_int64_strings() {
        let status = AgentStatusDto {
            agent_version: "0.1.0".to_string(),
            protocol_version: 1,
            schema_version: 1,
            process_state: ProcessState::Running,
            capture_state: CaptureState::Running,
            writer_state: WriterState::Healthy,
            runtime_id: RuntimeId::new(),
            heartbeat_at_utc_ms: Some(Int64String(1784300000000)),
            last_observation_at_utc_ms: None,
            last_write_at_utc_ms: None,
            capture_queue_depth: 0,
            writer_queue_depth: 0,
            dropped_capture_count: Int64String(0),
            dropped_writer_count: Int64String(0),
            safe_error_code: None,
        };
        let json = serde_json::to_value(&status).unwrap();
        assert_eq!(json["heartbeatAtUtcMs"], "1784300000000");
        assert_eq!(json["droppedCaptureCount"], "0");
        assert_eq!(json["processState"], "running");
    }
}
