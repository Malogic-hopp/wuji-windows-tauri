//! Desktop 本地偏好（09 §9.4）：不属于 Agent effectivity Settings。
//!
//! `autoStartRecordingWhenAppStarts` 决定 Desktop 启动时是否自动开始记录
//! （先 `ensure_running` 确保 Agent 在线，再提交内部 `capture_ensure_recording`），不进入
//! Settings digest/CAS/LKG/数据库。因此旧 settings.json、双槽 LKG 与数据库
//! 中的同 revision 摘要升级不受本文件影响，无需任何迁移（R07 摘要兼容由
//! wuji-core Settings 字段集冻结保证）。

use std::path::PathBuf;

use serde::{Deserialize, Serialize};
use tokio::sync::Mutex;
use wuji_core::error::{SafeError, SafeErrorCode};

use crate::settings_service::rename_replace;

/// desktop_prefs.json 完整字段集（09 §9.4）。只允许本字段，禁止混入
/// Agent effectivity Settings 字段。
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DesktopPrefs {
    /// Desktop 启动时是否自动开始记录（缺失默认 true）。
    #[serde(default = "default_auto_start_recording")]
    pub auto_start_recording_when_app_starts: bool,
}

fn default_auto_start_recording() -> bool {
    true
}

impl Default for DesktopPrefs {
    fn default() -> Self {
        Self {
            auto_start_recording_when_app_starts: true,
        }
    }
}

/// v0.1 引入初期的旧键名（语义仅为“拉起 Agent 进程”）。读取时兼容，
/// 保存一律写新键。
const LEGACY_AUTO_START_KEY: &str = "autoStartAgentWhenAppStarts";

/// `desktop_prefs_update` 的输入（09 §9.4）。
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DesktopPrefsPatch {
    pub auto_start_recording_when_app_starts: bool,
}

pub struct DesktopPrefsService {
    path: PathBuf,
    lock: Mutex<()>,
}

impl DesktopPrefsService {
    pub fn new(channel: &str) -> Result<Self, String> {
        let path = crate::paths::data_root(channel)?
            .join("config")
            .join("desktop_prefs.json");
        Ok(Self {
            path,
            lock: Mutex::new(()),
        })
    }

    /// 三态加载（R04 同口径）：缺失 → None；损坏/未知字段 → SettingsInvalid
    /// 显式上报，不得伪装成默认值；其他 I/O 故障 → 稳定安全错误，不得伪装成
    /// 首次运行（权限拒绝、路径错误等不是“文件不存在”）；合法 → 值。
    /// 旧键名仅在偏好键缺失时生效（一次性读取兼容，保存即写新键）。
    fn load(&self) -> Result<Option<DesktopPrefs>, SafeError> {
        match std::fs::read_to_string(&self.path) {
            Ok(raw) => Self::parse(&raw).map(Some),
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(None),
            Err(_) => Err(SafeError::new(
                SafeErrorCode::DbUnavailable,
                "Desktop 偏好文件无法读取，请检查文件权限后重试",
            )),
        }
    }

    /// 严格解析（09 §9.4）：只接受偏好键与旧键名两个字段，未知字段或字段
    /// 类型错误一律视为损坏（SettingsInvalid），不得静默忽略。
    fn parse(raw: &str) -> Result<DesktopPrefs, SafeError> {
        let corrupt = || {
            SafeError::new(
                SafeErrorCode::SettingsInvalid,
                "Desktop 偏好文件损坏，无法读取；将使用默认值并在下次保存时修复",
            )
        };
        let parsed: serde_json::Value = serde_json::from_str(raw).map_err(|_| corrupt())?;
        let object = parsed.as_object().ok_or_else(corrupt)?;
        for key in object.keys() {
            if key != "autoStartRecordingWhenAppStarts" && key != LEGACY_AUTO_START_KEY {
                return Err(corrupt());
            }
        }
        let new_value = object.get("autoStartRecordingWhenAppStarts");
        let legacy = object.get(LEGACY_AUTO_START_KEY);
        let value = match (new_value, legacy) {
            (Some(value), _) => value.as_bool().ok_or_else(corrupt)?,
            (None, Some(value)) => value.as_bool().ok_or_else(corrupt)?,
            (None, None) => default_auto_start_recording(),
        };
        Ok(DesktopPrefs {
            auto_start_recording_when_app_starts: value,
        })
    }

    /// Desktop 启动时是否应自动开始记录（09 §9.4 启动决策的偏好输入）。
    /// 缺失（首次运行）默认 true；损坏时失败开放（仍开启）并在 stderr 说明，
    /// 与“新安装默认开启”一致，避免用户以为已记录实际未记录；
    /// 其他 I/O 故障时失败关闭（不自动开始记录）——文件存在却读不到说明
    /// 用户已保存过选择，无凭据启动采集比多记录一次更安全。
    pub fn should_auto_start_recording(&self) -> bool {
        match self.load() {
            Ok(Some(prefs)) => prefs.auto_start_recording_when_app_starts,
            Ok(None) => true,
            Err(error) if error.code == SafeErrorCode::SettingsInvalid => {
                eprintln!("[desktop_prefs] 偏好文件损坏，按默认值决定自动开始记录");
                true
            }
            Err(error) => {
                eprintln!(
                    "[desktop_prefs] 偏好文件读取失败（{}），本次不自动开始记录",
                    error.message
                );
                false
            }
        }
    }

    /// `desktop_prefs_get`：缺失返回默认值但不创建文件（与 Settings 同口径）。
    pub fn get(&self) -> Result<DesktopPrefs, SafeError> {
        Ok(self.load()?.unwrap_or_default())
    }

    /// `desktop_prefs_update`：临时文件 + flush + 原子替换（09 §9 同法）。
    /// 损坏文件被合法保存覆盖，即自愈；无 CAS（纯 Desktop 偏好，无共享摘要）。
    pub async fn update(&self, patch: DesktopPrefsPatch) -> Result<DesktopPrefs, SafeError> {
        let _guard = self.lock.lock().await;
        let candidate = DesktopPrefs {
            auto_start_recording_when_app_starts: patch.auto_start_recording_when_app_starts,
        };
        self.write_atomic(&candidate)?;
        Ok(candidate)
    }

    fn write_atomic(&self, prefs: &DesktopPrefs) -> Result<(), SafeError> {
        let parent = self
            .path
            .parent()
            .ok_or_else(|| SafeError::new(SafeErrorCode::InternalSafeError, "偏好路径无效"))?;
        std::fs::create_dir_all(parent)
            .map_err(|_| SafeError::new(SafeErrorCode::DbUnavailable, "无法创建偏好目录"))?;
        let temp = parent.join(format!("desktop_prefs.json.tmp-{}", std::process::id()));
        let write_result = (|| {
            let mut file = std::fs::File::create(&temp)?;
            use std::io::Write as _;
            file.write_all(
                serde_json::to_string(prefs)
                    .expect("偏好序列化不应失败")
                    .as_bytes(),
            )?;
            file.sync_all()?;
            rename_replace(&temp, &self.path)
        })();
        if write_result.is_err() {
            let _ = std::fs::remove_file(&temp);
        }
        write_result.map_err(|_| SafeError::new(SafeErrorCode::DbUnavailable, "偏好保存失败"))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn service_in(temp: &std::path::Path) -> DesktopPrefsService {
        DesktopPrefsService {
            path: temp.join("desktop_prefs.json"),
            lock: Mutex::new(()),
        }
    }

    #[tokio::test]
    async fn missing_file_defaults_to_auto_start_recording() {
        let temp = tempfile::tempdir().unwrap();
        let service = service_in(temp.path());
        assert!(!service.path.exists());
        assert!(service.should_auto_start_recording());
        assert_eq!(
            service.get().unwrap(),
            DesktopPrefs {
                auto_start_recording_when_app_starts: true
            }
        );
        assert!(!service.path.exists(), "get 不得创建文件");
    }

    #[tokio::test]
    async fn update_persists_and_roundtrips() {
        let temp = tempfile::tempdir().unwrap();
        let service = service_in(temp.path());
        let off = service
            .update(DesktopPrefsPatch {
                auto_start_recording_when_app_starts: false,
            })
            .await
            .unwrap();
        assert!(!off.auto_start_recording_when_app_starts);
        assert!(service.path.exists());
        assert!(!service.should_auto_start_recording());
        let on = service
            .update(DesktopPrefsPatch {
                auto_start_recording_when_app_starts: true,
            })
            .await
            .unwrap();
        assert!(on.auto_start_recording_when_app_starts);
        assert!(service.should_auto_start_recording());
    }

    #[tokio::test]
    async fn corrupt_file_is_reported_by_get_and_fails_open_at_startup() {
        let temp = tempfile::tempdir().unwrap();
        let service = service_in(temp.path());
        std::fs::write(&service.path, b"{ not json").unwrap();
        let error = service.get().unwrap_err();
        assert_eq!(error.code, SafeErrorCode::SettingsInvalid);
        // 启动决策失败开放（与缺失同默认 true），损坏同时被显式上报，不伪装成默认值。
        assert!(service.should_auto_start_recording());
    }

    #[tokio::test]
    async fn update_over_corrupt_file_self_heals() {
        let temp = tempfile::tempdir().unwrap();
        let service = service_in(temp.path());
        std::fs::write(&service.path, b"corrupt").unwrap();
        let saved = service
            .update(DesktopPrefsPatch {
                auto_start_recording_when_app_starts: false,
            })
            .await
            .unwrap();
        assert!(!saved.auto_start_recording_when_app_starts);
        assert_eq!(service.get().unwrap(), saved);
    }

    #[tokio::test]
    async fn legacy_key_is_honored_only_when_new_key_missing() {
        let temp = tempfile::tempdir().unwrap();
        let service = service_in(temp.path());

        // 仅旧键：读取兼容，保留用户已选值。
        std::fs::write(&service.path, br#"{"autoStartAgentWhenAppStarts": false}"#).unwrap();
        assert!(!service.should_auto_start_recording());

        // 新旧键并存：新键优先。
        std::fs::write(
            &service.path,
            br#"{"autoStartRecordingWhenAppStarts": false, "autoStartAgentWhenAppStarts": true}"#,
        )
        .unwrap();
        assert!(!service.should_auto_start_recording());

        // 保存后只写新键，旧键不再出现。
        let saved = service
            .update(DesktopPrefsPatch {
                auto_start_recording_when_app_starts: true,
            })
            .await
            .unwrap();
        let raw = std::fs::read_to_string(&service.path).unwrap();
        assert!(raw.contains("autoStartRecordingWhenAppStarts"));
        assert!(!raw.contains(LEGACY_AUTO_START_KEY));
        assert!(saved.auto_start_recording_when_app_starts);
    }

    #[tokio::test]
    async fn unknown_field_is_rejected_as_corrupt() {
        let temp = tempfile::tempdir().unwrap();
        let service = service_in(temp.path());
        // 未知字段（混入 Agent effectivity Settings 字段）一律视为损坏，不静默忽略。
        std::fs::write(
            &service.path,
            br#"{"autoStartRecordingWhenAppStarts": true, "startCaptureOnLogin": false}"#,
        )
        .unwrap();
        let error = service.get().unwrap_err();
        assert_eq!(error.code, SafeErrorCode::SettingsInvalid);
    }

    #[tokio::test]
    async fn legacy_key_with_wrong_type_is_rejected() {
        let temp = tempfile::tempdir().unwrap();
        let service = service_in(temp.path());
        std::fs::write(&service.path, br#"{"autoStartAgentWhenAppStarts": "yes"}"#).unwrap();
        let error = service.get().unwrap_err();
        assert_eq!(error.code, SafeErrorCode::SettingsInvalid);
    }

    #[tokio::test]
    async fn io_error_is_not_masked_as_missing() {
        let temp = tempfile::tempdir().unwrap();
        let service = service_in(temp.path());
        // 路径被目录占用（读取返回非 NotFound 错误）：不得伪装成首次运行。
        std::fs::create_dir(&service.path).unwrap();
        let error = service.get().unwrap_err();
        assert_ne!(error.code, SafeErrorCode::SettingsInvalid);
        assert_eq!(error.code, SafeErrorCode::DbUnavailable);
        // 启动决策失败关闭：读不到用户已保存的选择，不自动开始记录。
        assert!(!service.should_auto_start_recording());
    }
}
