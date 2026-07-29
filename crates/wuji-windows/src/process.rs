//! 进程文件名解析：OpenProcess + QueryFullProcessImageNameW。
//!
//! 完整路径只存在于本函数调用栈内，取文件名后立即丢弃（09 §6.1）。

#[cfg(windows)]
use crate::CaptureError;

/// 从完整路径取文件名部分（不规范化；规范化属于 wuji-core）。
pub fn file_name_from_path(path: &str) -> Option<&str> {
    let name = path.rsplit(['\\', '/']).next()?;
    if name.is_empty() { None } else { Some(name) }
}

#[cfg(windows)]
pub fn process_image_file_name(pid: u32) -> Result<String, CaptureError> {
    use windows_sys::Win32::Foundation::CloseHandle;
    use windows_sys::Win32::System::Threading::{
        OpenProcess, PROCESS_QUERY_LIMITED_INFORMATION, QueryFullProcessImageNameW,
    };

    // SAFETY: OpenProcess/CloseHandle/QueryFullProcessImageNameW 均为标准 Win32 调用；
    // 进程句柄在离开作用域前必定关闭。
    let handle = unsafe { OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, pid) };
    if handle.is_null() {
        return Err(CaptureError::ProcessQueryFailed);
    }
    (|| {
        let mut buffer = [0u16; 1024];
        let mut size = buffer.len() as u32;
        let ok = unsafe { QueryFullProcessImageNameW(handle, 0, buffer.as_mut_ptr(), &mut size) };
        unsafe { CloseHandle(handle) };
        if ok == 0 {
            return Err(CaptureError::ProcessNameUnavailable);
        }
        let path = String::from_utf16_lossy(&buffer[..size as usize]);
        file_name_from_path(&path)
            .map(|name| name.to_string())
            .ok_or(CaptureError::ProcessNameUnavailable)
    })()
}

#[cfg(not(windows))]
pub fn process_image_file_name(_pid: u32) -> Result<String, crate::CaptureError> {
    Err(crate::CaptureError::ProcessQueryFailed)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn file_name_extraction() {
        assert_eq!(
            file_name_from_path("C:\\Windows\\notepad.exe"),
            Some("notepad.exe")
        );
        assert_eq!(file_name_from_path("\\\\?\\C:\\a b\\c.exe"), Some("c.exe"));
        assert_eq!(file_name_from_path("notepad.exe"), Some("notepad.exe"));
        assert_eq!(file_name_from_path("C:\\dir\\"), None);
        assert_eq!(file_name_from_path(""), None);
    }
}
