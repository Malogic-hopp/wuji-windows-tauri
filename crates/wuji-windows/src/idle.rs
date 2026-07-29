//! 用户空闲秒数（GetLastInputInfo）。
//!
//! API 失败必须返回 Err，调用方按 `unknown + idle_unavailable` 处理，
//! 不得沿用上一状态（09 §6.1）。

#[cfg(windows)]
use crate::CaptureError;

#[cfg(windows)]
pub fn idle_seconds() -> Result<u32, CaptureError> {
    use windows_sys::Win32::System::SystemInformation::GetTickCount;
    use windows_sys::Win32::UI::Input::KeyboardAndMouse::{GetLastInputInfo, LASTINPUTINFO};

    let mut info = LASTINPUTINFO {
        cbSize: std::mem::size_of::<LASTINPUTINFO>() as u32,
        dwTime: 0,
    };
    // SAFETY: info 是指向有效 LASTINPUTINFO 缓冲区的指针，cbSize 已按合同填充。
    let ok = unsafe { GetLastInputInfo(&mut info) };
    if ok == 0 {
        return Err(CaptureError::IdleUnavailable);
    }
    // GetTickCount 与 dwTime 同在 32 位 tick 域内，wrapping_sub 处理 49.7 天回绕。
    let now = unsafe { GetTickCount() };
    Ok(now.wrapping_sub(info.dwTime) / 1000)
}

#[cfg(not(windows))]
pub fn idle_seconds() -> Result<u32, crate::CaptureError> {
    Err(crate::CaptureError::IdleUnavailable)
}
