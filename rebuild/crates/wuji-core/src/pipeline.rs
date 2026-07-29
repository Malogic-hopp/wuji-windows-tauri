//! 采集流水线领域逻辑：RawCapture → Processor → 已过滤 Observation（09 §5、§6.1）。
//!
//! 隐私边界：原始进程路径在 wuji-windows 内已丢弃；本模块之后 PID 与原始
//! 文件名不再存在。排除 App 不产生 Observation（09 §3.1）。

use crate::domain::{ActivityState, CaptureQuality};
use crate::settings::{Settings, normalize_process_name, sha256_hex};

/// S2-04 返修：pipeline barrier 类型。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum BarrierKind {
    /// 生命周期边界（Pause/Stop/Lock/Sleep）。
    Lifecycle,
    /// Settings 生效边界。
    SettingsApplied,
}

/// S2-04 返修：受验证的 Barrier 唯一标识（内部为规范 ULID）。
#[derive(Debug, Clone, PartialEq, Eq, Hash)]
pub struct BarrierId(String);

impl Default for BarrierId {
    fn default() -> Self {
        Self(ulid::Ulid::generate().to_string())
    }
}

impl BarrierId {
    /// 生成新的唯一 BarrierId。
    pub fn new() -> Self {
        Self::default()
    }

    /// 从现有字符串解析（验证 ULID 格式）。
    pub fn parse(raw: &str) -> Option<Self> {
        ulid::Ulid::from_string(raw)
            .ok()
            .map(|_| Self(raw.to_string()))
    }

    /// 内部 ULID 字符串。
    pub fn as_str(&self) -> &str {
        &self.0
    }
}

/// S2-04 返修：携带唯一 BarrierId 的 pipeline token。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct BarrierToken {
    /// 全局唯一标识。
    pub id: BarrierId,
    /// Barrier 类型。
    pub kind: BarrierKind,
    /// Barrier 前 Writer/Engine 应处于的 revision。
    pub expected_revision: i64,
}

/// S2-04 返修：Capture Loop 到 Processor 的统一 FIFO 元素。
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum CapturePipelineItem {
    Sample(RawCapture),
    Barrier(BarrierToken),
}

/// Capture Loop 输出到 bounded queue 的原始采样（仅内存，永不持久化）。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RawCapture {
    pub sequence: u64,
    pub continuity_epoch: u64,
    pub captured_at_utc_ms: i64,
    pub captured_monotonic_ms: u64,
    /// 原始文件名（未规范化）；进程名不可用为 None（09 §6.1 capture_error 路径）。
    pub process_file_name: Option<String>,
    pub idle: IdleReading,
    /// S2-04：采样时生效的 Settings revision（用于 Writer 端错配检测）。
    pub settings_revision: i64,
}

/// idle 读数；API 失败为 Unavailable，Processor 按 unknown + idle_unavailable 处理。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum IdleReading {
    Seconds(u32),
    Unavailable,
}

/// 通过隐私过滤的 Observation（09 §6.1 的持久化候选）。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct FilteredObservation {
    pub sequence: u64,
    pub continuity_epoch: u64,
    pub captured_at_utc_ms: i64,
    pub captured_monotonic_ms: u64,
    pub app_key: String,
    pub display_name: String,
    pub normalized_process_name: String,
    pub activity_state: ActivityState,
    pub quality: CaptureQuality,
    /// S2-04：采样时生效的 Settings revision（用于 Writer 端错配检测）。
    pub settings_revision: i64,
}

/// Processor 输出（09 §5：排除 App 与不可用进程名只携带时间，不携带进程信息）。
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ProcessorOutput {
    Observation(FilteredObservation),
    /// 命中排除规则：不写 Observation，由下游写不含 App 信息的 privacy_excluded gap。
    PrivacyExcluded {
        sequence: u64,
        continuity_epoch: u64,
        captured_at_utc_ms: i64,
        /// S2-04 返修：采样时的 Settings revision。
        settings_revision: i64,
    },
    /// 无法得到非空文件名：写不含进程信息的 capture_error gap（09 §6.1）。
    CaptureError {
        sequence: u64,
        continuity_epoch: u64,
        captured_at_utc_ms: i64,
        /// S2-04 返修：采样时的 Settings revision。
        settings_revision: i64,
    },
    /// S2-04 返修：携带唯一 BarrierId 的 barrier——经有序 FIFO 传递。
    Barrier(BarrierToken),
    /// 阶段 4.4（P1-04）：Processor 在业务处理前检测到的 revision 协议违例。
    /// 样本 revision 与当前 settings watch revision 不一致时，Processor 不得
    /// 继续用当前 Settings 处理、不得静默丢弃、不得重标 revision，而是发出
    /// 本条显式内部错误消息并退出；Writer 收到后统一 fail-closed。
    /// 只携带 sequence 与 revision 诊断字段，不含进程名等隐私内容。
    SettingsRevisionMismatch {
        sequence: u64,
        continuity_epoch: u64,
        /// 样本携带的 Settings revision。
        sample_revision: i64,
        /// Processor 处理时从 settings watch 读到的当前 revision。
        current_revision: i64,
    },
}

impl ProcessorOutput {
    pub fn continuity_epoch(&self) -> u64 {
        match self {
            ProcessorOutput::Observation(obs) => obs.continuity_epoch,
            ProcessorOutput::PrivacyExcluded {
                continuity_epoch, ..
            }
            | ProcessorOutput::CaptureError {
                continuity_epoch, ..
            }
            | ProcessorOutput::SettingsRevisionMismatch {
                continuity_epoch, ..
            } => *continuity_epoch,
            ProcessorOutput::Barrier(_) => 0,
        }
    }

    pub fn sequence(&self) -> u64 {
        match self {
            ProcessorOutput::Observation(obs) => obs.sequence,
            ProcessorOutput::PrivacyExcluded { sequence, .. }
            | ProcessorOutput::CaptureError { sequence, .. }
            | ProcessorOutput::SettingsRevisionMismatch { sequence, .. } => *sequence,
            ProcessorOutput::Barrier(_) => 0,
        }
    }

    /// 阶段 4.4：三类数据变体的统一 revision 访问（Barrier 与协议违例消息
    /// 不携带数据 revision，返回 None）。Writer 在转换任何 EngineEvent 前
    /// 用同一防线校验 Observation、PrivacyExcluded、CaptureError。
    pub fn settings_revision(&self) -> Option<i64> {
        match self {
            ProcessorOutput::Observation(obs) => Some(obs.settings_revision),
            ProcessorOutput::PrivacyExcluded {
                settings_revision, ..
            }
            | ProcessorOutput::CaptureError {
                settings_revision, ..
            } => Some(*settings_revision),
            ProcessorOutput::Barrier(_) | ProcessorOutput::SettingsRevisionMismatch { .. } => None,
        }
    }
}

pub struct ObservationProcessor;

impl ObservationProcessor {
    /// 固定顺序（09 §5）：标准化 → 排除规则 → Activity State → 丢弃原始文件名。
    pub fn process(raw: RawCapture, settings: &Settings) -> ProcessorOutput {
        let Some(raw_name) = raw.process_file_name.as_deref() else {
            return ProcessorOutput::CaptureError {
                sequence: raw.sequence,
                continuity_epoch: raw.continuity_epoch,
                captured_at_utc_ms: raw.captured_at_utc_ms,
                settings_revision: raw.settings_revision,
            };
        };
        let Some(normalized) = normalize_process_name(raw_name) else {
            return ProcessorOutput::CaptureError {
                sequence: raw.sequence,
                continuity_epoch: raw.continuity_epoch,
                captured_at_utc_ms: raw.captured_at_utc_ms,
                settings_revision: raw.settings_revision,
            };
        };

        if settings
            .excluded_process_names
            .iter()
            .any(|excluded| excluded == &normalized)
        {
            return ProcessorOutput::PrivacyExcluded {
                sequence: raw.sequence,
                continuity_epoch: raw.continuity_epoch,
                captured_at_utc_ms: raw.captured_at_utc_ms,
                settings_revision: raw.settings_revision,
            };
        }

        let (activity_state, quality) = match raw.idle {
            IdleReading::Seconds(seconds) if seconds >= settings.idle_threshold_seconds => {
                (ActivityState::Idle, CaptureQuality::Normal)
            }
            IdleReading::Seconds(_) => (ActivityState::Active, CaptureQuality::Normal),
            // idle API 失败不得沿用上一状态（09 §6.1）。
            IdleReading::Unavailable => (ActivityState::Unknown, CaptureQuality::IdleUnavailable),
        };

        ProcessorOutput::Observation(FilteredObservation {
            sequence: raw.sequence,
            continuity_epoch: raw.continuity_epoch,
            captured_at_utc_ms: raw.captured_at_utc_ms,
            captured_monotonic_ms: raw.captured_monotonic_ms,
            app_key: app_key_for(&normalized),
            display_name: display_name_of(raw_name),
            normalized_process_name: normalized,
            activity_state,
            quality,
            settings_revision: raw.settings_revision,
        })
    }
}

/// `app_key = "proc:" + sha256(normalized_process_name)`（09 §6.1）。
pub fn app_key_for(normalized_process_name: &str) -> String {
    format!("proc:{}", sha256_hex(normalized_process_name.as_bytes()))
}

/// 展示名：首次成功采集的原文件名去掉末尾 `.exe`（大小写保留，09 §6.1）。
pub fn display_name_of(raw_file_name: &str) -> String {
    let trimmed = raw_file_name.trim();
    trimmed
        .strip_suffix(".exe")
        .or_else(|| trimmed.strip_suffix(".EXE"))
        .or_else(|| trimmed.strip_suffix(".Exe"))
        .unwrap_or(trimmed)
        .to_string()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn settings_with_exclusions(excluded: &[&str]) -> Settings {
        Settings {
            excluded_process_names: excluded.iter().map(|s| s.to_string()).collect(),
            ..Settings::default()
        }
    }

    fn raw(name: Option<&str>, idle: IdleReading) -> RawCapture {
        RawCapture {
            sequence: 7,
            continuity_epoch: 3,
            captured_at_utc_ms: 1_784_332_800_000,
            captured_monotonic_ms: 42_000,
            process_file_name: name.map(str::to_string),
            idle,
            settings_revision: 0,
        }
    }

    #[test]
    fn active_below_threshold_with_normal_quality() {
        let output = ObservationProcessor::process(
            raw(Some("  Code.EXE "), IdleReading::Seconds(12)),
            &Settings::default(),
        );
        let ProcessorOutput::Observation(obs) = output else {
            panic!("应为 Observation: {output:?}")
        };
        assert_eq!(obs.normalized_process_name, "code.exe");
        assert_eq!(obs.display_name, "Code");
        assert_eq!(obs.activity_state, ActivityState::Active);
        assert_eq!(obs.quality, CaptureQuality::Normal);
        assert_eq!(obs.sequence, 7);
        assert_eq!(obs.continuity_epoch, 3);
        assert!(obs.app_key.starts_with("proc:"));
        assert_eq!(obs.app_key.len(), 5 + 64);
        assert_eq!(obs.app_key, app_key_for("code.exe"));
    }

    #[test]
    fn idle_at_threshold() {
        let output = ObservationProcessor::process(
            raw(Some("code.exe"), IdleReading::Seconds(60)),
            &Settings::default(),
        );
        let ProcessorOutput::Observation(obs) = output else {
            panic!("应为 Observation: {output:?}")
        };
        assert_eq!(obs.activity_state, ActivityState::Idle);
    }

    #[test]
    fn unavailable_idle_is_unknown_never_previous_state() {
        let output = ObservationProcessor::process(
            raw(Some("code.exe"), IdleReading::Unavailable),
            &Settings::default(),
        );
        let ProcessorOutput::Observation(obs) = output else {
            panic!("应为 Observation: {output:?}")
        };
        assert_eq!(obs.activity_state, ActivityState::Unknown);
        assert_eq!(obs.quality, CaptureQuality::IdleUnavailable);
    }

    #[test]
    fn excluded_process_produces_no_observation() {
        let settings = settings_with_exclusions(&["keepass.exe"]);
        let output = ObservationProcessor::process(
            raw(Some("  KeePass.EXE"), IdleReading::Seconds(3)),
            &settings,
        );
        assert_eq!(
            output,
            ProcessorOutput::PrivacyExcluded {
                sequence: 7,
                continuity_epoch: 3,
                captured_at_utc_ms: 1_784_332_800_000,
                settings_revision: 0,
            }
        );
    }

    #[test]
    fn missing_or_empty_process_name_is_capture_error() {
        let settings = Settings::default();
        assert!(matches!(
            ObservationProcessor::process(raw(None, IdleReading::Seconds(1)), &settings),
            ProcessorOutput::CaptureError { .. }
        ));
        assert!(matches!(
            ObservationProcessor::process(raw(Some("   "), IdleReading::Seconds(1)), &settings),
            ProcessorOutput::CaptureError { .. }
        ));
    }

    #[test]
    fn display_name_strips_exe_case_variants() {
        assert_eq!(display_name_of("NotePad.EXE"), "NotePad");
        assert_eq!(display_name_of("notepad.exe"), "notepad");
        assert_eq!(display_name_of("MyApp.Exe"), "MyApp");
        assert_eq!(display_name_of("NoSuffix"), "NoSuffix");
    }

    #[test]
    fn nfkc_normalization_applies() {
        // 全角 ＣＯＤＥ．ＥＸＥ → code.exe
        let output = ObservationProcessor::process(
            raw(
                Some("\u{FF23}\u{FF4F}\u{FF44}\u{FF45}\u{FF0E}\u{FF45}\u{FF58}\u{FF45}"),
                IdleReading::Seconds(1),
            ),
            &Settings::default(),
        );
        let ProcessorOutput::Observation(obs) = output else {
            panic!("应为 Observation: {output:?}")
        };
        assert_eq!(obs.normalized_process_name, "code.exe");
    }

    /// 阶段 4.4：三类数据变体共用统一 revision 访问；Barrier 与协议违例
    /// 消息返回 None（不参与 revision 防线）。
    #[test]
    fn settings_revision_accessor_covers_data_variants_only() {
        let observation = ObservationProcessor::process(
            raw(Some("code.exe"), IdleReading::Seconds(1)),
            &Settings::default(),
        );
        assert_eq!(observation.settings_revision(), Some(0));

        let excluded = ObservationProcessor::process(
            raw(Some("keepass.exe"), IdleReading::Seconds(1)),
            &settings_with_exclusions(&["keepass.exe"]),
        );
        assert_eq!(excluded.settings_revision(), Some(0));

        let capture_error =
            ObservationProcessor::process(raw(None, IdleReading::Seconds(1)), &Settings::default());
        assert_eq!(capture_error.settings_revision(), Some(0));

        let barrier = ProcessorOutput::Barrier(BarrierToken {
            id: BarrierId::new(),
            kind: BarrierKind::SettingsApplied,
            expected_revision: 0,
        });
        assert_eq!(barrier.settings_revision(), None);

        let mismatch = ProcessorOutput::SettingsRevisionMismatch {
            sequence: 9,
            continuity_epoch: 1,
            sample_revision: 0,
            current_revision: 1,
        };
        assert_eq!(mismatch.settings_revision(), None);
        assert_eq!(mismatch.sequence(), 9);
        assert_eq!(mismatch.continuity_epoch(), 1);
    }
}
