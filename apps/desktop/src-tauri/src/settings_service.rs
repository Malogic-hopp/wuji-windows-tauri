//! Settings 服务：Tauri 唯一写入者（09 §9）。
//!
//! CAS（expectedRevision）+ 原子替换 + Agent reload + Run Key 补偿（09 §9.2）。

use std::path::{Path, PathBuf};

use serde::Deserialize;
use serde_json::json;
use tokio::sync::Mutex;
use wuji_core::dto::SettingsDto;
use wuji_core::error::{SafeError, SafeErrorCode};
use wuji_core::runtime_names::RUN_KEY_VALUE_NAME;
use wuji_core::settings::{DEFAULT_REVISION, Settings};

use crate::ipc::AgentIpcClient;
use crate::query::QueryService;
use crate::startup_registry;

/// settings_update 的输入（React 白名单字段，09 §9）。
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct SettingsPatch {
    pub expected_revision: String,
    pub sampling_interval_seconds: u32,
    pub idle_threshold_seconds: u32,
    pub work_break_idle_seconds: u32,
    pub excluded_process_names: Vec<String>,
    pub start_capture_on_login: bool,
}

pub struct SettingsService {
    path: PathBuf,
    run_key_value: String,
    agent_exe: PathBuf,
    lock: Mutex<()>,
}

impl SettingsService {
    pub fn new(
        channel: &str,
        run_key_value: Option<String>,
        agent_exe: PathBuf,
    ) -> Result<Self, String> {
        let path = crate::paths::data_root(channel)?
            .join("config")
            .join("settings.json");
        let run_key_value = run_key_value.unwrap_or_else(|| RUN_KEY_VALUE_NAME.to_string());
        Ok(Self {
            path,
            run_key_value,
            agent_exe,
            lock: Mutex::new(()),
        })
    }

    pub fn path(&self) -> &Path {
        &self.path
    }

    /// 设置文件三态加载（R04）：损坏文件必须显式上报，不得伪装成默认值。
    fn load_current(&self) -> Result<Option<Settings>, SafeError> {
        match std::fs::read_to_string(&self.path) {
            Ok(raw) => match serde_json::from_str::<Settings>(&raw) {
                Ok(settings) => Ok(Some(settings)),
                Err(_) => Err(SafeError::new(
                    SafeErrorCode::SettingsInvalid,
                    "设置文件损坏，无法读取；请备份后删除该文件以恢复默认设置",
                )),
            },
            Err(_) => Ok(None),
        }
    }

    /// `settings_get`：saved 与 applied 分开返回（09 §8.4、§9.1）。
    pub fn get(&self, query: &QueryService) -> Result<SettingsDto, SafeError> {
        let loaded = self.load_current()?;
        let (settings, persisted) = match loaded {
            Some(settings) => (settings, true),
            None => (Settings::default(), false),
        };
        let applied = query
            .applied_settings_revision()
            .unwrap_or_else(|_| DEFAULT_REVISION.to_string());
        Ok(SettingsDto::from_settings(&settings, persisted, applied))
    }

    /// `settings_update`：CAS → Run Key 先行 → 原子替换 → Agent reload（09 §9.1、§9.2）。
    pub async fn update(
        &self,
        patch: SettingsPatch,
        ipc: &AgentIpcClient,
    ) -> Result<SettingsDto, SafeError> {
        let _guard = self.lock.lock().await;
        let loaded = self.load_current()?;
        let (current, persisted) = match loaded {
            Some(settings) => (settings, true),
            None => (Settings::default(), false),
        };
        if current.revision != patch.expected_revision {
            return Err(SafeError::new(
                SafeErrorCode::SettingsConflict,
                "设置已被其他操作修改，请刷新后重试",
            ));
        }
        let next_revision = current
            .next_revision()
            .ok_or_else(|| SafeError::new(SafeErrorCode::SettingsInvalid, "当前 revision 无效"))?;
        let candidate = Settings {
            schema_version: wuji_core::settings::SETTINGS_SCHEMA_VERSION,
            revision: next_revision,
            sampling_interval_seconds: patch.sampling_interval_seconds,
            idle_threshold_seconds: patch.idle_threshold_seconds,
            work_break_idle_seconds: patch.work_break_idle_seconds,
            excluded_process_names: patch.excluded_process_names,
            start_capture_on_login: patch.start_capture_on_login,
        };
        if let Err(errors) = candidate.validate() {
            let message = errors
                .first()
                .map(|e| e.message.clone())
                .unwrap_or_else(|| "设置字段不合法".to_string());
            return Err(SafeError::new(SafeErrorCode::SettingsInvalid, message));
        }

        let startup_changed = candidate.start_capture_on_login != current.start_capture_on_login;
        let mut run_key_applied = false;
        if startup_changed {
            self.apply_run_key(candidate.start_capture_on_login)?;
            run_key_applied = true;
        }

        if let Err(error) = self.write_atomic(&candidate) {
            if run_key_applied {
                // 补偿：尽力恢复旧 Run Key 状态（09 §9.2）。
                if self.apply_run_key(current.start_capture_on_login).is_err() {
                    return Err(SafeError::new(
                        SafeErrorCode::StartupReconciliationRequired,
                        "登录启动与设置状态不一致，请在诊断页执行“重新同步登录启动”",
                    ));
                }
            }
            return Err(error);
        }

        // 通知 Agent reload（09 §9.1：离线/失败不回滚普通设置）。
        let reload = ipc
            .call(
                "settings_reload",
                json!({
                    "savedRevision": candidate.revision,
                    "contentDigest": candidate.content_digest(),
                }),
            )
            .await;
        let applied = match reload {
            Ok(response) if response["ok"].as_bool().unwrap_or(false) => {
                response["result"]["appliedRevision"]
                    .as_str()
                    .unwrap_or_default()
                    .to_string()
            }
            _ => {
                // saved-not-applied：文件已保存，Agent 保持上一 revision，后续自动重试。
                return Err(SafeError::new(
                    SafeErrorCode::SettingsSavedNotApplied,
                    "设置已保存，Agent 将在下次连接时应用",
                ));
            }
        };
        let _ = persisted;
        Ok(SettingsDto::from_settings(&candidate, true, applied))
    }

    /// `settings_resync_login_startup`：按当前 Settings 重放 Run Key 同步（09 §9.2）。
    /// appliedRevision 来自数据库最大已应用 revision（R04：不得误报为 saved revision）。
    pub fn resync_login_startup(&self, query: &QueryService) -> Result<SettingsDto, SafeError> {
        let loaded = self.load_current()?;
        let (current, persisted) = match loaded {
            Some(settings) => (settings, true),
            None => (Settings::default(), false),
        };
        self.apply_run_key(current.start_capture_on_login)?;
        let applied = query
            .applied_settings_revision()
            .unwrap_or_else(|_| DEFAULT_REVISION.to_string());
        Ok(SettingsDto::from_settings(&current, persisted, applied))
    }

    fn apply_run_key(&self, enabled: bool) -> Result<(), SafeError> {
        let result = if enabled {
            startup_registry::set_run_key(
                &self.run_key_value,
                &startup_registry::run_key_command(&self.agent_exe),
            )
        } else {
            startup_registry::delete_run_key(&self.run_key_value)
        };
        result.map_err(|_| {
            SafeError::new(
                SafeErrorCode::StartupRegistryFailed,
                "登录启动项修改失败，设置未保存",
            )
        })
    }

    /// 原子替换：临时文件 + flush + MoveFileEx 替换（09 §9）。
    fn write_atomic(&self, settings: &Settings) -> Result<(), SafeError> {
        let parent = self
            .path
            .parent()
            .ok_or_else(|| SafeError::new(SafeErrorCode::InternalSafeError, "设置路径无效"))?;
        std::fs::create_dir_all(parent)
            .map_err(|_| SafeError::new(SafeErrorCode::DbUnavailable, "无法创建设置目录"))?;
        let temp = parent.join(format!("settings.json.tmp-{}", std::process::id()));
        let write_result = (|| {
            let mut file = std::fs::File::create(&temp)?;
            use std::io::Write as _;
            file.write_all(settings.canonical_json().as_bytes())?;
            file.sync_all()?;
            rename_replace(&temp, &self.path)
        })();
        if write_result.is_err() {
            let _ = std::fs::remove_file(&temp);
        }
        write_result.map_err(|_| SafeError::new(SafeErrorCode::DbUnavailable, "设置保存失败"))
    }
}

#[cfg(windows)]
pub(crate) fn rename_replace(from: &Path, to: &Path) -> std::io::Result<()> {
    use windows_sys::Win32::Storage::FileSystem::{
        MOVEFILE_REPLACE_EXISTING, MOVEFILE_WRITE_THROUGH, MoveFileExW,
    };
    let from_wide: Vec<u16> = from
        .to_string_lossy()
        .encode_utf16()
        .chain(std::iter::once(0))
        .collect();
    let to_wide: Vec<u16> = to
        .to_string_lossy()
        .encode_utf16()
        .chain(std::iter::once(0))
        .collect();
    let ok = unsafe {
        MoveFileExW(
            from_wide.as_ptr(),
            to_wide.as_ptr(),
            MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH,
        )
    };
    if ok == 0 {
        return Err(std::io::Error::last_os_error());
    }
    Ok(())
}

#[cfg(not(windows))]
pub(crate) fn rename_replace(from: &Path, to: &Path) -> std::io::Result<()> {
    std::fs::rename(from, to)
}
