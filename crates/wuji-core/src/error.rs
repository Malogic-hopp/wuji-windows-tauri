//! 稳定错误码与安全错误结构（09 §8.2）。
//!
//! 错误码集合冻结；跨边界只传 code 与安全 message，不传原始异常（ADR-002 §13）。

use serde::{Deserialize, Serialize};
use specta::Type;

/// 09 §8.2 冻结的稳定错误码。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Type)]
#[serde(rename_all = "SCREAMING_SNAKE_CASE")]
pub enum SafeErrorCode {
    IpcProtocolUnsupported,
    IpcChannelMismatch,
    IpcInvalidMessage,
    IpcPayloadTooLarge,
    IpcRequestIdReused,
    InvalidArgument,
    CaptureInvalidState,
    AgentWriterDegraded,
    AgentWriterFaulted,
    DbUnavailable,
    DbSchemaUnsupported,
    TimeZoneUnavailable,
    SettingsConflict,
    SettingsInvalid,
    SettingsSavedNotApplied,
    StartupRegistryFailed,
    StartupReconciliationRequired,
    VersionIncompatible,
    InternalSafeError,
}

/// IPC/Tauri 边界上的安全错误结构（09 §8.2 envelope 的 `error` 字段）。
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, Type)]
#[serde(rename_all = "camelCase")]
pub struct SafeError {
    pub code: SafeErrorCode,
    /// 面向用户的中文安全说明；不得包含原始异常、路径、SQL、SID 或标题。
    pub message: String,
}

impl SafeErrorCode {
    /// 与 serde 表示一致的稳定码字符串（DB 持久化与离线诊断共用）。
    pub fn as_str(&self) -> &'static str {
        match self {
            Self::IpcProtocolUnsupported => "IPC_PROTOCOL_UNSUPPORTED",
            Self::IpcChannelMismatch => "IPC_CHANNEL_MISMATCH",
            Self::IpcInvalidMessage => "IPC_INVALID_MESSAGE",
            Self::IpcPayloadTooLarge => "IPC_PAYLOAD_TOO_LARGE",
            Self::IpcRequestIdReused => "IPC_REQUEST_ID_REUSED",
            Self::InvalidArgument => "INVALID_ARGUMENT",
            Self::CaptureInvalidState => "CAPTURE_INVALID_STATE",
            Self::AgentWriterDegraded => "AGENT_WRITER_DEGRADED",
            Self::AgentWriterFaulted => "AGENT_WRITER_FAULTED",
            Self::DbUnavailable => "DB_UNAVAILABLE",
            Self::DbSchemaUnsupported => "DB_SCHEMA_UNSUPPORTED",
            Self::TimeZoneUnavailable => "TIME_ZONE_UNAVAILABLE",
            Self::SettingsConflict => "SETTINGS_CONFLICT",
            Self::SettingsInvalid => "SETTINGS_INVALID",
            Self::SettingsSavedNotApplied => "SETTINGS_SAVED_NOT_APPLIED",
            Self::StartupRegistryFailed => "STARTUP_REGISTRY_FAILED",
            Self::StartupReconciliationRequired => "STARTUP_RECONCILIATION_REQUIRED",
            Self::VersionIncompatible => "VERSION_INCOMPATIBLE",
            Self::InternalSafeError => "INTERNAL_SAFE_ERROR",
        }
    }

    /// 由稳定码字符串还原（无法识别返回 None，不 panic）。
    pub fn from_code(code: &str) -> Option<Self> {
        Some(match code {
            "IPC_PROTOCOL_UNSUPPORTED" => Self::IpcProtocolUnsupported,
            "IPC_CHANNEL_MISMATCH" => Self::IpcChannelMismatch,
            "IPC_INVALID_MESSAGE" => Self::IpcInvalidMessage,
            "IPC_PAYLOAD_TOO_LARGE" => Self::IpcPayloadTooLarge,
            "IPC_REQUEST_ID_REUSED" => Self::IpcRequestIdReused,
            "INVALID_ARGUMENT" => Self::InvalidArgument,
            "CAPTURE_INVALID_STATE" => Self::CaptureInvalidState,
            "AGENT_WRITER_DEGRADED" => Self::AgentWriterDegraded,
            "AGENT_WRITER_FAULTED" => Self::AgentWriterFaulted,
            "DB_UNAVAILABLE" => Self::DbUnavailable,
            "DB_SCHEMA_UNSUPPORTED" => Self::DbSchemaUnsupported,
            "TIME_ZONE_UNAVAILABLE" => Self::TimeZoneUnavailable,
            "SETTINGS_CONFLICT" => Self::SettingsConflict,
            "SETTINGS_INVALID" => Self::SettingsInvalid,
            "SETTINGS_SAVED_NOT_APPLIED" => Self::SettingsSavedNotApplied,
            "STARTUP_REGISTRY_FAILED" => Self::StartupRegistryFailed,
            "STARTUP_RECONCILIATION_REQUIRED" => Self::StartupReconciliationRequired,
            "VERSION_INCOMPATIBLE" => Self::VersionIncompatible,
            "INTERNAL_SAFE_ERROR" => Self::InternalSafeError,
            _ => return None,
        })
    }
}

/// 安全错误来源（S2-08：按来源管理诊断状态，互不覆盖）。
#[derive(
    Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash, Serialize, Deserialize, Type,
)]
#[serde(rename_all = "camelCase")]
pub enum ErrorSource {
    /// Writer（数据库写入故障、busy 恢复）。
    Writer,
    /// Checkpoint（WAL checkpoint busy）。
    Checkpoint,
    /// Settings（启动对账、revision 冲突、文件损坏）。
    Settings,
    /// IPC（协议错误、channel 不匹配）。
    Ipc,
    /// 生命周期事件泵（Lock/Sleep 监视不可用）。
    LifecyclePump,
}

/// 按来源的当前安全错误集合。
pub type ErrorSet = std::collections::BTreeMap<ErrorSource, SafeErrorCode>;

/// 将当前错误集合合并为逗号分隔的稳定字符串（写入 DB heartbeat）。
pub fn format_error_set(errors: &ErrorSet) -> Option<String> {
    if errors.is_empty() {
        return None;
    }
    let codes: Vec<&str> = errors.values().map(|c| c.as_str()).collect();
    Some(codes.join(","))
}

impl SafeError {
    pub fn new(code: SafeErrorCode, message: impl Into<String>) -> Self {
        Self {
            code,
            message: message.into(),
        }
    }
}

impl std::fmt::Display for SafeError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{}", self.message)
    }
}

impl std::error::Error for SafeError {}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn codes_serialize_as_screaming_snake() {
        assert_eq!(
            serde_json::to_string(&SafeErrorCode::SettingsSavedNotApplied).unwrap(),
            "\"SETTINGS_SAVED_NOT_APPLIED\""
        );
        assert_eq!(
            serde_json::to_string(&SafeErrorCode::IpcRequestIdReused).unwrap(),
            "\"IPC_REQUEST_ID_REUSED\""
        );
    }

    #[test]
    fn error_envelope_shape_is_fixed() {
        let err = SafeError::new(SafeErrorCode::CaptureInvalidState, "当前状态不能暂停采集");
        let json = serde_json::to_value(&err).unwrap();
        assert_eq!(json["code"], "CAPTURE_INVALID_STATE");
        assert_eq!(json["message"], "当前状态不能暂停采集");
    }
}
