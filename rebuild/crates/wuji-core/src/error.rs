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
