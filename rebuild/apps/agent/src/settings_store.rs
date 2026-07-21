//! Settings 文件加载（09 §9.1）。
//!
//! 文件不存在 → revision 0 内建默认值，不创建文件；
//! 解析失败/验证失败 → Agent 保留最后已应用值并上报安全诊断。

use std::path::Path;

use wuji_core::settings::Settings;

pub enum SettingsLoad {
    Missing,
    Ready(Settings),
    Invalid(String),
}

pub fn load_settings_file(path: &Path) -> SettingsLoad {
    let raw = match std::fs::read_to_string(path) {
        Ok(raw) => raw,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => return SettingsLoad::Missing,
        Err(_) => return SettingsLoad::Invalid("设置文件不可读".to_string()),
    };
    let settings: Settings = match serde_json::from_str(&raw) {
        Ok(settings) => settings,
        Err(_) => return SettingsLoad::Invalid("设置文件不是合法 JSON".to_string()),
    };
    if let Err(errors) = settings.validate() {
        let message = errors
            .first()
            .map(|e| format!("{}: {}", e.field, e.message))
            .unwrap_or_else(|| "设置字段不合法".to_string());
        return SettingsLoad::Invalid(message);
    }
    SettingsLoad::Ready(settings)
}
