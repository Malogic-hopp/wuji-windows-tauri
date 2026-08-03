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

// ===== 统计主页 DTO（10 设计 §5.3 + 11 实施方案阶段一）=====
// 合同：全部字段整数表达（不引入 f64）；比较对象 DTO 始终存在，不可用只由
// direction / unavailableReason 单轨表达（禁止外层 Option 与内部 unavailable 双轨）。

/// 同时刻比较方向五态（10 §4.1；TS 字面量 "up"|"down"|"stable"|"upFromZero"|"unavailable"）。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Type)]
#[serde(rename_all = "camelCase")]
pub enum ComparisonDirection {
    Up,
    Down,
    Stable,
    UpFromZero,
    Unavailable,
}

/// 比较不可用原因（仅 direction = Unavailable 时有值；单轨表达）。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Type)]
#[serde(rename_all = "camelCase")]
pub enum UnavailableReason {
    NoData,
    InsufficientSamples,
}

/// 摘要方向五档（10 §5.3 SummaryDto.direction）。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Type)]
#[serde(rename_all = "camelCase")]
pub enum SummaryDirection {
    Up,
    UpSlight,
    Flat,
    DownSlight,
    Down,
}

/// 主要活跃时段（10 §5.3 primaryPeriod：6-12 morning，12-18 afternoon，18-24 evening，0-6 night）。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Type)]
#[serde(rename_all = "lowercase")]
pub enum PeriodKind {
    Morning,
    Afternoon,
    Evening,
    Night,
}

/// 惯性可靠性（10 §4.4：有效日 <3 → null，3-6 → preliminary，≥7 → normal）。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Type)]
#[serde(rename_all = "lowercase")]
pub enum ReliabilityKind {
    Preliminary,
    Normal,
}

/// 构成桶粒度（10 §4.4：7/14 天 = day，30 天 = week）。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Type)]
#[serde(rename_all = "lowercase")]
pub enum BucketKind {
    Day,
    Week,
}

/// 同时刻比较对象（10 §5.3）：对象始终存在；`activeDurationMs` 无基线时为 null，
/// `deltaPercent` 仅基线 > 0 时如实携带（含 |delta| ≤ 5% 的 stable）。
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, Type)]
#[serde(rename_all = "camelCase")]
pub struct SameTimeComparisonDto {
    pub active_duration_ms: Option<Int64String>,
    pub delta_percent: Option<i32>,
    pub direction: ComparisonDirection,
    pub sample_days: i32,
    pub unavailable_reason: Option<UnavailableReason>,
}

/// 轻量轮询载荷的实时状态（11 阶段零 P0-1）：不含摘要；摘要只在 StatsHomeDto.status 返回。
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, Type)]
#[serde(rename_all = "camelCase")]
pub struct LiveStatusDto {
    pub today_active_ms: Int64String,
    pub work_block_count: Int64String,
    pub cutoff_local_time: String,
    pub yesterday_same: SameTimeComparisonDto,
    pub last7_avg_same: SameTimeComparisonDto,
}

/// 状态摘要主卡（仅 StatsHomeDto 使用；10 §5.3 原形：实时五字段 + summary）。
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, Type)]
#[serde(rename_all = "camelCase")]
pub struct StatusDto {
    pub today_active_ms: Int64String,
    pub work_block_count: Int64String,
    pub cutoff_local_time: String,
    pub yesterday_same: SameTimeComparisonDto,
    pub last7_avg_same: SameTimeComparisonDto,
    pub summary: SummaryDto,
}

/// 一行自然语言摘要（10 §5.3）：direction = 7 日窗口日均比较，primaryPeriod = 14 日惯性峰值时段。
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, Type)]
#[serde(rename_all = "camelCase")]
pub struct SummaryDto {
    pub direction: Option<SummaryDirection>,
    pub primary_period: Option<PeriodKind>,
}

/// 活跃趋势单日点（10 §4.2）：hasData=false 柱留空斜纹；isToday 用进行中样式且不进入均线。
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, Type)]
#[serde(rename_all = "camelCase")]
pub struct TrendPointDto {
    pub local_date: LocalDate,
    pub active_duration_ms: Int64String,
    pub work_block_count: Int64String,
    pub has_data: bool,
    pub is_today: bool,
    pub moving_avg7_active_ms: Option<Int64String>,
    pub moving_avg7_sample_days: i32,
}

/// 周柱点（10 §4.3）：当前周 = 截至同时刻总量 + 进行中样式；虚框参考值仅当前周有值。
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, Type)]
#[serde(rename_all = "camelCase")]
pub struct WeeklyPointDto {
    pub week_start_date: LocalDate,
    pub active_duration_ms: Int64String,
    pub is_current_week: bool,
    pub completed_recorded_days: i32,
    pub current_week_daily_avg_ms: Option<Int64String>,
}

/// 本周进度卡（10 §4.3）：当前周存在时始终返回，仅 lastWeekSame 基线可空。
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, Type)]
#[serde(rename_all = "camelCase")]
pub struct WeekProgressDto {
    pub current_active_ms: Int64String,
    pub last_week_same: SameTimeComparisonDto,
    pub recorded_days: i32,
    pub cutoff_local_time: String,
}

/// 构成桶内单个应用条目（10 §4.4；与今日页 TopAppDto 同形但独立类型，口径各自演进）。
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, Type)]
#[serde(rename_all = "camelCase")]
pub struct TopEntryDto {
    pub app: AppDto,
    pub active_duration_ms: Int64String,
}

/// 应用构成桶（10 §4.4）：日桶/周桶；hasData 区分"无记录数据"与"有记录但活跃为 0"。
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, Type)]
#[serde(rename_all = "camelCase")]
pub struct CompositionBucketDto {
    pub start_date: LocalDate,
    pub end_date: LocalDate,
    pub bucket_kind: BucketKind,
    pub is_current: bool,
    pub has_data: bool,
    pub apps: Vec<TopEntryDto>,
    pub others_active_ms: Int64String,
}

/// 周期内固定槽位（10 §4.4）：slot ∈ {0,1,2}，槽位 → 色值由前端 CSS 令牌映射。
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, Type)]
#[serde(rename_all = "camelCase")]
pub struct AppPaletteEntryDto {
    pub app: AppDto,
    pub slot: u32,
}

/// 惯性每小时均值（10 §4.4）：Rust 统一分母后返回，前端只做柱高归一化。
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, Type)]
#[serde(rename_all = "camelCase")]
pub struct HourlyPointDto {
    pub local_hour: u32,
    pub avg_active_ms: Int64String,
}

/// 惯性标注（10 §4.4）：全零曲线或 reliability = null 时派生字段全部 null。
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, Type)]
#[serde(rename_all = "camelCase")]
pub struct InertiaDto {
    pub start_hour: Option<i32>,
    pub peak_hour: Option<i32>,
    pub end_hour: Option<i32>,
    pub lunch_lowest_hour: Option<i32>,
    pub effective_days: i32,
    pub total_days: i32,
    pub reliability: Option<ReliabilityKind>,
}

/// 长期里程碑（10 §4.5）：firstRecordedMonth 无任何有效记录时为 null（禁止空字符串）。
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, Type)]
#[serde(rename_all = "camelCase")]
pub struct MilestoneDto {
    pub total_recorded_days: Int64String,
    pub longest_consecutive_days: Int64String,
    pub first_recorded_month: Option<String>,
}

/// 月度柱点（10 §4.5）：主值 = 每有效记录日均值；当前月进行中，recordedDays/均值不含今日。
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, Type)]
#[serde(rename_all = "camelCase")]
pub struct MonthlyPointDto {
    pub month: String,
    pub active_duration_ms: Int64String,
    pub recorded_days: i32,
    pub is_current_month: bool,
    pub avg_active_ms_per_recorded_day: Option<Int64String>,
}

/// 统计主页全量（10 §5.3）：跨日期/切换范围时整页刷新；hasAnyData=false 整页空状态。
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, Type)]
#[serde(rename_all = "camelCase")]
pub struct StatsHomeDto {
    pub has_any_data: bool,
    pub local_date: LocalDate,
    pub reporting_time_zone_id: String,
    pub status: StatusDto,
    pub trend: Vec<TrendPointDto>,
    pub weekly: Vec<WeeklyPointDto>,
    pub week_progress: WeekProgressDto,
    pub composition: Vec<CompositionBucketDto>,
    pub palette: Vec<AppPaletteEntryDto>,
    pub hourly_profile: Vec<HourlyPointDto>,
    pub inertia: InertiaDto,
    pub milestone: MilestoneDto,
    pub monthly: Vec<MonthlyPointDto>,
}

/// 轻量轮询（11 阶段零 P0-1/P0-2）：5s 同拍刷新 liveStatus + weekProgress + todayTrendPoint，
/// 携带报告时区的 localDate 供前端跨日检测；不含摘要，不触发惯性/月度/里程碑查询。
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, Type)]
#[serde(rename_all = "camelCase")]
pub struct StatsStatusDto {
    pub local_date: LocalDate,
    pub reporting_time_zone_id: String,
    pub live_status: LiveStatusDto,
    pub week_progress: WeekProgressDto,
    pub today_trend_point: TrendPointDto,
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

    #[test]
    fn stats_enum_strings_match_design_ts_literals() {
        // 10 §5.3 TS 字面量：direction / unavailableReason / summary / reliability / bucketKind
        assert_eq!(
            serde_json::to_string(&ComparisonDirection::UpFromZero).unwrap(),
            "\"upFromZero\""
        );
        assert_eq!(
            serde_json::to_string(&ComparisonDirection::Up).unwrap(),
            "\"up\""
        );
        assert_eq!(
            serde_json::to_string(&ComparisonDirection::Down).unwrap(),
            "\"down\""
        );
        assert_eq!(
            serde_json::to_string(&ComparisonDirection::Stable).unwrap(),
            "\"stable\""
        );
        assert_eq!(
            serde_json::to_string(&ComparisonDirection::Unavailable).unwrap(),
            "\"unavailable\""
        );
        assert_eq!(
            serde_json::to_string(&UnavailableReason::NoData).unwrap(),
            "\"noData\""
        );
        assert_eq!(
            serde_json::to_string(&UnavailableReason::InsufficientSamples).unwrap(),
            "\"insufficientSamples\""
        );
        assert_eq!(
            serde_json::to_string(&SummaryDirection::UpSlight).unwrap(),
            "\"upSlight\""
        );
        assert_eq!(
            serde_json::to_string(&SummaryDirection::DownSlight).unwrap(),
            "\"downSlight\""
        );
        assert_eq!(
            serde_json::to_string(&SummaryDirection::Flat).unwrap(),
            "\"flat\""
        );
        assert_eq!(
            serde_json::to_string(&PeriodKind::Morning).unwrap(),
            "\"morning\""
        );
        assert_eq!(
            serde_json::to_string(&PeriodKind::Night).unwrap(),
            "\"night\""
        );
        assert_eq!(
            serde_json::to_string(&ReliabilityKind::Preliminary).unwrap(),
            "\"preliminary\""
        );
        assert_eq!(
            serde_json::to_string(&ReliabilityKind::Normal).unwrap(),
            "\"normal\""
        );
        assert_eq!(serde_json::to_string(&BucketKind::Day).unwrap(), "\"day\"");
        assert_eq!(
            serde_json::to_string(&BucketKind::Week).unwrap(),
            "\"week\""
        );
    }

    #[test]
    fn stats_status_dto_camel_case_and_optional_roundtrip() {
        let dto = StatsStatusDto {
            local_date: LocalDate::parse("2026-08-03").unwrap(),
            reporting_time_zone_id: "Asia/Shanghai".to_string(),
            live_status: LiveStatusDto {
                today_active_ms: Int64String(12_000_000),
                work_block_count: Int64String(8),
                cutoff_local_time: "15:20".to_string(),
                yesterday_same: SameTimeComparisonDto {
                    active_duration_ms: None,
                    delta_percent: None,
                    direction: ComparisonDirection::Unavailable,
                    sample_days: 0,
                    unavailable_reason: Some(UnavailableReason::NoData),
                },
                last7_avg_same: SameTimeComparisonDto {
                    active_duration_ms: Some(Int64String(10_000_000)),
                    delta_percent: Some(5),
                    direction: ComparisonDirection::Up,
                    sample_days: 5,
                    unavailable_reason: None,
                },
            },
            week_progress: WeekProgressDto {
                current_active_ms: Int64String(58_800_000),
                last_week_same: SameTimeComparisonDto {
                    active_duration_ms: None,
                    delta_percent: None,
                    direction: ComparisonDirection::Unavailable,
                    sample_days: 0,
                    unavailable_reason: Some(UnavailableReason::NoData),
                },
                recorded_days: 3,
                cutoff_local_time: "15:20".to_string(),
            },
            today_trend_point: TrendPointDto {
                local_date: LocalDate::parse("2026-08-03").unwrap(),
                active_duration_ms: Int64String(12_000_000),
                work_block_count: Int64String(8),
                has_data: true,
                is_today: true,
                moving_avg7_active_ms: None,
                moving_avg7_sample_days: 5,
            },
        };
        let json = serde_json::to_value(&dto).unwrap();
        assert_eq!(json["localDate"], "2026-08-03");
        assert_eq!(json["liveStatus"]["todayActiveMs"], "12000000");
        assert_eq!(json["liveStatus"]["last7AvgSame"]["deltaPercent"], 5);
        assert_eq!(json["liveStatus"]["last7AvgSame"]["direction"], "up");
        assert!(json["liveStatus"]["yesterdaySame"]["activeDurationMs"].is_null());
        assert_eq!(
            json["liveStatus"]["yesterdaySame"]["unavailableReason"],
            "noData"
        );
        assert_eq!(json["weekProgress"]["lastWeekSame"]["sampleDays"], 0);
        assert!(json["todayTrendPoint"]["movingAvg7ActiveMs"].is_null());
        assert_eq!(json["todayTrendPoint"]["isToday"], true);
        // 反序列化回环（含 Option = null 与 branded Int64String）
        let back: StatsStatusDto = serde_json::from_value(json).unwrap();
        assert_eq!(back, dto);
    }

    #[test]
    fn stats_home_dto_roundtrip() {
        let dto = StatsHomeDto {
            has_any_data: true,
            local_date: LocalDate::parse("2026-08-03").unwrap(),
            reporting_time_zone_id: "Asia/Shanghai".to_string(),
            status: StatusDto {
                today_active_ms: Int64String(12_000_000),
                work_block_count: Int64String(8),
                cutoff_local_time: "15:20".to_string(),
                yesterday_same: SameTimeComparisonDto {
                    active_duration_ms: Some(Int64String(11_000_000)),
                    delta_percent: Some(9),
                    direction: ComparisonDirection::Up,
                    sample_days: 1,
                    unavailable_reason: None,
                },
                last7_avg_same: SameTimeComparisonDto {
                    active_duration_ms: Some(Int64String(10_000_000)),
                    delta_percent: Some(5),
                    direction: ComparisonDirection::Up,
                    sample_days: 5,
                    unavailable_reason: None,
                },
                summary: SummaryDto {
                    direction: Some(SummaryDirection::UpSlight),
                    primary_period: Some(PeriodKind::Morning),
                },
            },
            trend: vec![TrendPointDto {
                local_date: LocalDate::parse("2026-08-03").unwrap(),
                active_duration_ms: Int64String(12_000_000),
                work_block_count: Int64String(8),
                has_data: true,
                is_today: true,
                moving_avg7_active_ms: None,
                moving_avg7_sample_days: 5,
            }],
            weekly: vec![WeeklyPointDto {
                week_start_date: LocalDate::parse("2026-07-27").unwrap(),
                active_duration_ms: Int64String(58_800_000),
                is_current_week: true,
                completed_recorded_days: 3,
                current_week_daily_avg_ms: Some(Int64String(11_900_000)),
            }],
            week_progress: WeekProgressDto {
                current_active_ms: Int64String(58_800_000),
                last_week_same: SameTimeComparisonDto {
                    active_duration_ms: None,
                    delta_percent: None,
                    direction: ComparisonDirection::Unavailable,
                    sample_days: 0,
                    unavailable_reason: Some(UnavailableReason::NoData),
                },
                recorded_days: 3,
                cutoff_local_time: "15:20".to_string(),
            },
            composition: vec![CompositionBucketDto {
                start_date: LocalDate::parse("2026-08-03").unwrap(),
                end_date: LocalDate::parse("2026-08-03").unwrap(),
                bucket_kind: BucketKind::Day,
                is_current: true,
                has_data: true,
                apps: vec![TopEntryDto {
                    app: AppDto {
                        app_id: Int64String(1),
                        display_name: "VS Code".to_string(),
                    },
                    active_duration_ms: Int64String(6_000_000),
                }],
                others_active_ms: Int64String(1_000_000),
            }],
            palette: vec![AppPaletteEntryDto {
                app: AppDto {
                    app_id: Int64String(1),
                    display_name: "VS Code".to_string(),
                },
                slot: 0,
            }],
            hourly_profile: vec![HourlyPointDto {
                local_hour: 9,
                avg_active_ms: Int64String(3_000_000),
            }],
            inertia: InertiaDto {
                start_hour: Some(9),
                peak_hour: Some(10),
                end_hour: Some(19),
                lunch_lowest_hour: Some(13),
                effective_days: 11,
                total_days: 14,
                reliability: Some(ReliabilityKind::Normal),
            },
            milestone: MilestoneDto {
                total_recorded_days: Int64String(143),
                longest_consecutive_days: Int64String(67),
                first_recorded_month: Some("2026-03".to_string()),
            },
            monthly: vec![MonthlyPointDto {
                month: "2026-08".to_string(),
                active_duration_ms: Int64String(20_000_000),
                recorded_days: 3,
                is_current_month: true,
                avg_active_ms_per_recorded_day: Some(Int64String(6_000_000)),
            }],
        };
        let json = serde_json::to_value(&dto).unwrap();
        assert_eq!(json["hasAnyData"], true);
        assert_eq!(json["status"]["summary"]["primaryPeriod"], "morning");
        assert_eq!(json["trend"][0]["movingAvg7SampleDays"], 5);
        assert_eq!(json["composition"][0]["bucketKind"], "day");
        assert_eq!(json["palette"][0]["slot"], 0);
        assert_eq!(json["milestone"]["firstRecordedMonth"], "2026-03");
        assert_eq!(json["monthly"][0]["avgActiveMsPerRecordedDay"], "6000000");
        let back: StatsHomeDto = serde_json::from_value(json).unwrap();
        assert_eq!(back, dto);
    }
}
