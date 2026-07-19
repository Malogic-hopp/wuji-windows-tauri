//! 前台采样：前台 HWND → PID → 进程文件名 + idle。
//!
//! 字段级降级：进程名与 idle 各自独立失败，互不影响（09 §6.1）。

use crate::CaptureError;

/// 一次前台采样的字段结果（路径不越过本 crate）。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ForegroundSample {
    pub process_file_name: Result<String, CaptureError>,
    pub idle_seconds: Result<u32, CaptureError>,
}

#[cfg(windows)]
pub fn capture_foreground() -> Result<ForegroundSample, CaptureError> {
    use windows_sys::Win32::UI::WindowsAndMessaging::{
        GetForegroundWindow, GetWindowThreadProcessId,
    };

    // SAFETY: 均为无指针输出的标准查询调用；pid 为栈上有效 u32。
    let hwnd = unsafe { GetForegroundWindow() };
    if hwnd.is_null() {
        return Err(CaptureError::ForegroundUnavailable);
    }
    let mut pid = 0_u32;
    unsafe {
        GetWindowThreadProcessId(hwnd, &mut pid);
    }
    if pid == 0 {
        return Err(CaptureError::ProcessQueryFailed);
    }
    Ok(ForegroundSample {
        process_file_name: crate::process::process_image_file_name(pid),
        idle_seconds: crate::idle::idle_seconds(),
    })
}

#[cfg(not(windows))]
pub fn capture_foreground() -> Result<ForegroundSample, CaptureError> {
    Err(CaptureError::ForegroundUnavailable)
}
