//! 采集适配器错误：只携带错误类别与系统错误码，不含路径/进程名等敏感字符串。

/// 单次 Win32 调用失败的安全类别。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CaptureError {
    /// 无前台窗口（锁定、无交互会话或瞬间切换）。
    ForegroundUnavailable,
    /// 无法打开或查询目标进程（退出中、权限不足）。
    ProcessQueryFailed,
    /// 无法读取进程文件名或文件名为空。
    ProcessNameUnavailable,
    /// idle API 失败；调用方必须按 unknown + idle_unavailable 处理（09 §6.1）。
    IdleUnavailable,
}

impl CaptureError {
    /// 对应的安全诊断码（不写日志原文，只写类别）。
    pub fn safe_code(self) -> &'static str {
        match self {
            CaptureError::ForegroundUnavailable => "FOREGROUND_UNAVAILABLE",
            CaptureError::ProcessQueryFailed => "PROCESS_QUERY_FAILED",
            CaptureError::ProcessNameUnavailable => "PROCESS_NAME_UNAVAILABLE",
            CaptureError::IdleUnavailable => "IDLE_UNAVAILABLE",
        }
    }
}

impl std::fmt::Display for CaptureError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(self.safe_code())
    }
}

impl std::error::Error for CaptureError {}
