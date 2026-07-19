//! Settings 模型、默认值、验证与内容摘要（09 §5.1、§9）。
//!
//! Tauri 是 Settings JSON 唯一写入者，Agent 只读；双方共用本模块的同一份
//! 默认值、验证器和 digest 算法，避免两端漂移（09 §9.1）。

use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use specta::Type;
use unicode_normalization::UnicodeNormalization;

/// Settings 文件 schema 版本（09 §9）。
pub const SETTINGS_SCHEMA_VERSION: u32 = 1;
/// 内建默认值的 revision（09 §9.1：文件不存在时使用 revision 0，不创建文件）。
pub const DEFAULT_REVISION: &str = "0";

pub const SAMPLING_INTERVAL_DEFAULT_SECONDS: u32 = 3;
pub const IDLE_THRESHOLD_DEFAULT_SECONDS: u32 = 60;
pub const WORK_BREAK_IDLE_DEFAULT_SECONDS: u32 = 300;

/// Settings JSON 完整字段集（09 §9：只允许这六个业务字段加 schemaVersion/revision）。
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, Type)]
#[serde(rename_all = "camelCase")]
pub struct Settings {
    pub schema_version: u32,
    /// 十进制字符串（09 §9：revision 是 string，不是 number）。
    pub revision: String,
    pub sampling_interval_seconds: u32,
    pub idle_threshold_seconds: u32,
    pub work_break_idle_seconds: u32,
    pub excluded_process_names: Vec<String>,
    pub start_capture_on_login: bool,
}

impl Default for Settings {
    /// Core 内建默认值 = revision 0（09 §9.1）。
    fn default() -> Self {
        Self {
            schema_version: SETTINGS_SCHEMA_VERSION,
            revision: DEFAULT_REVISION.to_string(),
            sampling_interval_seconds: SAMPLING_INTERVAL_DEFAULT_SECONDS,
            idle_threshold_seconds: IDLE_THRESHOLD_DEFAULT_SECONDS,
            work_break_idle_seconds: WORK_BREAK_IDLE_DEFAULT_SECONDS,
            excluded_process_names: Vec::new(),
            start_capture_on_login: false,
        }
    }
}

/// 字段级安全错误（09 §8.2 `fieldErrors`；message 为中文安全提示）。
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, Type)]
#[serde(rename_all = "camelCase")]
pub struct FieldError {
    pub field: String,
    pub message: String,
}

fn field_error(field: &str, message: &str) -> FieldError {
    FieldError {
        field: field.to_string(),
        message: message.to_string(),
    }
}

impl Settings {
    /// 整份验证（09 §9：失败时旧设置全部继续生效，不允许部分应用）。
    pub fn validate(&self) -> Result<(), Vec<FieldError>> {
        let mut errors = Vec::new();

        if self.schema_version != SETTINGS_SCHEMA_VERSION {
            errors.push(field_error("schemaVersion", "设置文件版本不受支持"));
        }
        if self.revision.parse::<u64>().is_err() {
            errors.push(field_error("revision", "revision 必须是十进制数字字符串"));
        }
        if !matches!(self.sampling_interval_seconds, 1 | 3 | 5 | 10) {
            errors.push(field_error(
                "samplingIntervalSeconds",
                "采样间隔只能是 1、3、5 或 10 秒",
            ));
        }
        if !(30..=1800).contains(&self.idle_threshold_seconds) {
            errors.push(field_error(
                "idleThresholdSeconds",
                "空闲阈值必须在 30 到 1800 秒之间",
            ));
        }
        if !(60..=3600).contains(&self.work_break_idle_seconds) {
            errors.push(field_error(
                "workBreakIdleSeconds",
                "工作块打断阈值必须在 60 到 3600 秒之间",
            ));
        }
        if self.work_break_idle_seconds <= self.idle_threshold_seconds {
            errors.push(field_error(
                "workBreakIdleSeconds",
                "工作块打断阈值必须大于空闲阈值",
            ));
        }
        for name in &self.excluded_process_names {
            match normalize_process_name(name) {
                Some(normalized) if normalized == *name => {}
                _ => errors.push(field_error(
                    "excludedProcessNames",
                    "排除进程名必须是规范化后的小写文件名",
                )),
            }
        }

        if errors.is_empty() {
            Ok(())
        } else {
            Err(errors)
        }
    }

    /// 规范 JSON：serde_json 按字段声明序、无空白序列化。两端必须用同一函数。
    pub fn canonical_json(&self) -> String {
        serde_json::to_string(self).expect("Settings 序列化不应失败")
    }

    /// 规范 JSON 的 SHA-256 小写十六进制（09 §7.2 bootstrap digest、§8.4 settings_reload）。
    pub fn content_digest(&self) -> String {
        let digest = Sha256::digest(self.canonical_json().as_bytes());
        digest.iter().map(|b| format!("{b:02x}")).collect()
    }

    /// 下一次保存的 revision（09 §9.1：成功恰好加一）。
    pub fn next_revision(&self) -> Option<String> {
        self.revision
            .parse::<u64>()
            .ok()
            .map(|r| (r + 1).to_string())
    }
}

/// 进程名规范化：trim → Unicode NFKC → Unicode lowercase，保留 `.exe`（09 §6.1）。
/// 结果为空或超过 260 字符时返回 None。
pub fn normalize_process_name(raw: &str) -> Option<String> {
    let normalized: String = raw.trim().nfkc().collect::<String>().to_lowercase();
    if normalized.is_empty() || normalized.chars().count() > 260 {
        return None;
    }
    Some(normalized)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn defaults_are_revision_zero_and_valid() {
        let settings = Settings::default();
        assert_eq!(settings.revision, "0");
        assert!(settings.validate().is_ok());
        assert_eq!(settings.next_revision().as_deref(), Some("1"));
    }

    #[test]
    fn digest_is_64_lower_hex_and_stable() {
        let digest = Settings::default().content_digest();
        assert_eq!(digest.len(), 64);
        assert!(
            digest
                .chars()
                .all(|c| c.is_ascii_hexdigit() && !c.is_ascii_uppercase())
        );
        assert_eq!(digest, Settings::default().content_digest());
    }

    #[test]
    fn digest_changes_with_content() {
        let changed = Settings {
            idle_threshold_seconds: 90,
            ..Settings::default()
        };
        assert_ne!(
            Settings::default().content_digest(),
            changed.content_digest()
        );
    }

    #[test]
    fn rejects_invalid_combinations() {
        let bad = Settings {
            sampling_interval_seconds: 4,
            idle_threshold_seconds: 300,
            work_break_idle_seconds: 300,
            ..Settings::default()
        };
        let errors = bad.validate().unwrap_err();
        assert!(errors.iter().any(|e| e.field == "samplingIntervalSeconds"));
        assert!(errors.iter().any(|e| e.field == "workBreakIdleSeconds"));
    }

    #[test]
    fn rejects_non_normalized_excluded_names() {
        let bad = Settings {
            excluded_process_names: vec!["KeePass.EXE ".to_string()],
            ..Settings::default()
        };
        let errors = bad.validate().unwrap_err();
        assert!(errors.iter().any(|e| e.field == "excludedProcessNames"));

        let good = Settings {
            excluded_process_names: vec!["keepass.exe".to_string()],
            ..Settings::default()
        };
        assert!(good.validate().is_ok());
    }

    #[test]
    fn process_name_normalization() {
        assert_eq!(
            normalize_process_name("  NotePad.EXE ").as_deref(),
            Some("notepad.exe")
        );
        assert_eq!(normalize_process_name("　"), None);
        assert_eq!(normalize_process_name(""), None);
        assert_eq!(normalize_process_name(&"a".repeat(261)), None);
    }
}
