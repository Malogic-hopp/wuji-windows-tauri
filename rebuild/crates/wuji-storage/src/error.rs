//! 存储层错误：稳定错误码 + 安全消息 + 仅供日志的诊断细节。

use wuji_core::error::{SafeError, SafeErrorCode};

#[derive(Debug)]
pub struct StorageError {
    pub code: SafeErrorCode,
    /// 中文安全说明，可跨边界展示。
    pub message: String,
    /// 仅用于本地日志的诊断细节，不得跨边界传给 React（09 §8.2）。
    pub detail: Option<String>,
}

pub type Result<T> = std::result::Result<T, StorageError>;

impl StorageError {
    pub fn new(code: SafeErrorCode, message: impl Into<String>) -> Self {
        Self {
            code,
            message: message.into(),
            detail: None,
        }
    }

    pub fn with_detail(mut self, detail: impl Into<String>) -> Self {
        self.detail = Some(detail.into());
        self
    }

    pub fn db_unavailable(message: impl Into<String>) -> Self {
        Self::new(SafeErrorCode::DbUnavailable, message)
    }

    pub fn schema_unsupported(message: impl Into<String>) -> Self {
        Self::new(SafeErrorCode::DbSchemaUnsupported, message)
    }

    pub fn time_zone_unavailable() -> Self {
        Self::new(
            SafeErrorCode::TimeZoneUnavailable,
            "无法解析系统报告时区，已放弃建库",
        )
    }

    pub fn internal(message: impl Into<String>) -> Self {
        Self::new(SafeErrorCode::InternalSafeError, message)
    }

    /// SQLite 失败归类：busy/locked 供 V01-5 重试策略使用，其余按内部安全错误。
    pub fn from_sqlite(error: rusqlite::Error) -> Self {
        let is_busy = matches!(
            &error,
            rusqlite::Error::SqliteFailure(code, _)
                if matches!(
                    code.code,
                    rusqlite::ErrorCode::DatabaseBusy | rusqlite::ErrorCode::DatabaseLocked
                )
        );
        let safe = if is_busy {
            Self::new(SafeErrorCode::AgentWriterDegraded, "数据库暂时繁忙")
        } else {
            Self::internal("数据库写入失败")
        };
        safe.with_detail(error.to_string())
    }

    pub fn to_safe_error(&self) -> SafeError {
        SafeError::new(self.code, self.message.clone())
    }
}

impl std::fmt::Display for StorageError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{}", self.message)
    }
}

impl std::error::Error for StorageError {}

impl From<rusqlite::Error> for StorageError {
    fn from(error: rusqlite::Error) -> Self {
        Self::from_sqlite(error)
    }
}
