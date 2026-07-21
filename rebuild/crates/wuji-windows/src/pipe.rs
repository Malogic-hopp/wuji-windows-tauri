//! Named Pipe 服务端：同用户 DACL + tokio 异步封装（09 §8.1 dev-only 安全边界）。
//!
//! v0.1 认证范围：Pipe 名含 user-scope 哈希 + DACL 仅当前用户。production 的
//! binary/签名清单与 session capability 属于长期规划（09 §8.1、06 §3）。

#[cfg(windows)]
use std::io;

/// 当前用户 SID 字符串（只用于派生 DACL 与 user-scope 哈希，不写入日志或数据库）。
#[cfg(windows)]
pub fn current_user_sid() -> io::Result<String> {
    use windows_sys::Win32::Foundation::{CloseHandle, LocalFree};
    use windows_sys::Win32::Security::Authorization::ConvertSidToStringSidW;
    use windows_sys::Win32::Security::{GetTokenInformation, TOKEN_QUERY, TOKEN_USER, TokenUser};
    use windows_sys::Win32::System::Threading::{GetCurrentProcess, OpenProcessToken};

    unsafe {
        let mut token = std::ptr::null_mut();
        if OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut token) == 0 {
            return Err(io::Error::last_os_error());
        }
        let result = (|| {
            let mut size = 0_u32;
            GetTokenInformation(token, TokenUser, std::ptr::null_mut(), 0, &mut size);
            if size == 0 {
                return Err(io::Error::last_os_error());
            }
            let mut buffer = vec![0_u8; size as usize];
            if GetTokenInformation(
                token,
                TokenUser,
                buffer.as_mut_ptr().cast(),
                size,
                &mut size,
            ) == 0
            {
                return Err(io::Error::last_os_error());
            }
            let sid = (*buffer.as_ptr().cast::<TOKEN_USER>()).User.Sid;
            let mut sid_text = std::ptr::null_mut();
            if ConvertSidToStringSidW(sid, &mut sid_text) == 0 {
                return Err(io::Error::last_os_error());
            }
            let len = (0..).take_while(|&i| *sid_text.add(i) != 0).count();
            let text = String::from_utf16_lossy(std::slice::from_raw_parts(sid_text, len));
            LocalFree(sid_text.cast());
            Ok(text)
        })();
        CloseHandle(token);
        result
    }
}

/// 创建只允许当前用户访问的 Named Pipe 服务端（tokio 异步句柄）。
#[cfg(windows)]
pub fn create_pipe_server(
    pipe_name: &str,
) -> io::Result<tokio::net::windows::named_pipe::NamedPipeServer> {
    use windows_sys::Win32::Foundation::{INVALID_HANDLE_VALUE, LocalFree};
    use windows_sys::Win32::Security::Authorization::{
        ConvertStringSecurityDescriptorToSecurityDescriptorW, SDDL_REVISION_1,
    };
    use windows_sys::Win32::Security::SECURITY_ATTRIBUTES;
    use windows_sys::Win32::Storage::FileSystem::{FILE_FLAG_OVERLAPPED, PIPE_ACCESS_DUPLEX};
    use windows_sys::Win32::System::Pipes::{
        CreateNamedPipeW, PIPE_READMODE_BYTE, PIPE_TYPE_BYTE, PIPE_UNLIMITED_INSTANCES, PIPE_WAIT,
    };

    let sid = current_user_sid()?;
    // 受保护 DACL：仅当前用户完全访问；拒绝其他任何人（含同机其他用户）。
    let sddl = format!("D:P(A;;FA;;;{sid})");
    let sddl_wide: Vec<u16> = sddl.encode_utf16().chain(std::iter::once(0)).collect();
    let name_wide: Vec<u16> = pipe_name.encode_utf16().chain(std::iter::once(0)).collect();

    unsafe {
        let mut descriptor = std::ptr::null_mut();
        if ConvertStringSecurityDescriptorToSecurityDescriptorW(
            sddl_wide.as_ptr(),
            SDDL_REVISION_1,
            &mut descriptor,
            std::ptr::null_mut(),
        ) == 0
        {
            return Err(io::Error::last_os_error());
        }
        let attributes = SECURITY_ATTRIBUTES {
            nLength: std::mem::size_of::<SECURITY_ATTRIBUTES>() as u32,
            lpSecurityDescriptor: descriptor,
            bInheritHandle: 0,
        };
        let handle = CreateNamedPipeW(
            name_wide.as_ptr(),
            PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED,
            PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
            PIPE_UNLIMITED_INSTANCES,
            64 * 1024 + 16,
            64 * 1024 + 16,
            0,
            &attributes,
        );
        LocalFree(descriptor);
        if handle == INVALID_HANDLE_VALUE {
            return Err(io::Error::last_os_error());
        }
        tokio::net::windows::named_pipe::NamedPipeServer::from_raw_handle(handle as _)
    }
}

/// 单实例守卫（09 §4.1：Agent mutex 按 channel 与用户隔离）。
#[cfg(windows)]
pub struct SingleInstanceGuard {
    handle: windows_sys::Win32::Foundation::HANDLE,
}

#[cfg(windows)]
impl SingleInstanceGuard {
    /// 获取 mutex；已被占用时返回 Ok(None)（调用方应退出而不竞争）。
    pub fn acquire(mutex_name: &str) -> io::Result<Option<Self>> {
        use windows_sys::Win32::Foundation::{CloseHandle, ERROR_ALREADY_EXISTS, GetLastError};
        use windows_sys::Win32::System::Threading::CreateMutexW;

        let name_wide: Vec<u16> = mutex_name
            .encode_utf16()
            .chain(std::iter::once(0))
            .collect();
        unsafe {
            let handle = CreateMutexW(std::ptr::null(), 1, name_wide.as_ptr());
            if handle.is_null() {
                return Err(io::Error::last_os_error());
            }
            if GetLastError() == ERROR_ALREADY_EXISTS {
                CloseHandle(handle);
                return Ok(None);
            }
            Ok(Some(Self { handle }))
        }
    }
}

#[cfg(windows)]
impl Drop for SingleInstanceGuard {
    fn drop(&mut self) {
        unsafe {
            windows_sys::Win32::Foundation::CloseHandle(self.handle);
        }
    }
}
