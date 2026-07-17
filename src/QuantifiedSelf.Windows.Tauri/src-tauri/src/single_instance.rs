use std::{ptr, thread, time::Duration};

use windows_sys::Win32::{
    Foundation::{CloseHandle, ERROR_ALREADY_EXISTS, GetLastError, HANDLE},
    System::Threading::CreateMutexW,
    UI::WindowsAndMessaging::{FindWindowW, SW_RESTORE, SetForegroundWindow, ShowWindowAsync},
};

pub const DEV_SINGLE_INSTANCE_MUTEX: &str = r"Local\WUJI.Tauri.Dev.SingleInstance.v1";
pub const DEV_WINDOW_TITLE: &str = "吾迹 · 开发预览";
const ACTIVATION_ATTEMPTS: usize = 100;
const ACTIVATION_RETRY_DELAY: Duration = Duration::from_millis(50);

pub enum InstanceDecision {
    Primary(SingleInstanceGuard),
    SecondaryActivated,
}

pub struct SingleInstanceGuard {
    handle: HANDLE,
}

impl Drop for SingleInstanceGuard {
    fn drop(&mut self) {
        if !self.handle.is_null() {
            // SAFETY: the handle was returned by CreateMutexW and is owned by this guard.
            unsafe {
                CloseHandle(self.handle);
            }
        }
    }
}

pub fn acquire_dev_instance() -> Result<InstanceDecision, &'static str> {
    let mutex_name = wide_null_terminated(DEV_SINGLE_INSTANCE_MUTEX);
    // SAFETY: the name is a valid null-terminated UTF-16 buffer and no security attributes are used.
    let handle = unsafe { CreateMutexW(ptr::null(), 0, mutex_name.as_ptr()) };
    if handle.is_null() {
        return Err("无法建立开发预览单实例门禁。");
    }

    // SAFETY: GetLastError must be read immediately after CreateMutexW.
    let already_exists = unsafe { GetLastError() } == ERROR_ALREADY_EXISTS;
    if already_exists {
        // SAFETY: the secondary instance owns this duplicate mutex handle.
        unsafe {
            CloseHandle(handle);
        }
        activate_existing_window();
        return Ok(InstanceDecision::SecondaryActivated);
    }

    Ok(InstanceDecision::Primary(SingleInstanceGuard { handle }))
}

fn activate_existing_window() {
    let title = wide_null_terminated(DEV_WINDOW_TITLE);
    for _ in 0..ACTIVATION_ATTEMPTS {
        // SAFETY: the title is a valid null-terminated UTF-16 buffer; a null class matches any class.
        let window = unsafe { FindWindowW(ptr::null(), title.as_ptr()) };
        if !window.is_null() {
            // SAFETY: FindWindowW returned a live top-level window handle owned by the primary instance.
            unsafe {
                ShowWindowAsync(window, SW_RESTORE);
                SetForegroundWindow(window);
            }
            return;
        }
        thread::sleep(ACTIVATION_RETRY_DELAY);
    }
}

fn wide_null_terminated(value: &str) -> Vec<u16> {
    value.encode_utf16().chain(std::iter::once(0)).collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn single_instance_identity_is_fixed_to_dev() {
        assert_eq!(
            DEV_SINGLE_INSTANCE_MUTEX,
            r"Local\WUJI.Tauri.Dev.SingleInstance.v1"
        );
        assert!(DEV_SINGLE_INSTANCE_MUTEX.contains("Dev"));
        assert!(!DEV_SINGLE_INSTANCE_MUTEX.contains("Prod"));
        assert_eq!(DEV_WINDOW_TITLE, "吾迹 · 开发预览");
    }

    #[test]
    fn win32_strings_are_null_terminated_without_changing_content() {
        let value = wide_null_terminated(DEV_WINDOW_TITLE);
        assert_eq!(value.last(), Some(&0));
        assert_eq!(
            String::from_utf16(&value[..value.len() - 1]).unwrap(),
            DEV_WINDOW_TITLE
        );
    }
}
