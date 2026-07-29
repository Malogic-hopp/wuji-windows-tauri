//! WUJI Rebuild v0.1 Windows 平台 crate：前台窗口、进程文件名、idle 采集。
//!
//! 合同（09 §6.1）：v0.1 不读取窗口标题；进程路径只在本 crate 调用栈内
//! 用于取文件名并立即丢弃；PID 不越过调用方 Processor。
//! 本 crate 不做调度、不持久化、不写日志。

pub mod error;
pub mod foreground;
pub mod idle;
pub mod pipe;
pub mod process;
pub mod process_handle;
pub mod session_power;

pub use error::CaptureError;
pub use foreground::{ForegroundSample, capture_foreground};
pub use idle::idle_seconds;
pub use process::{file_name_from_path, process_image_file_name};

#[cfg(windows)]
pub use pipe::{SingleInstanceGuard, create_pipe_server, current_user_sid};
#[cfg(windows)]
pub use process_handle::{ProcessHandle, ProcessWaitOutcome};
#[cfg(not(windows))]
pub use session_power::SessionPowerEvent;
#[cfg(windows)]
pub use session_power::{SessionPowerEvent, SessionPowerPumpHandle, start_event_pump};
