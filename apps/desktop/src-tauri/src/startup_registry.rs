//! 登录启动注册（09 §9.2）：HKCU Run Key 写入/删除/查询。
//!
//! 只写固定值名与固定 Agent 路径，不接受任意命令行（ADR-002 §12）。

use std::io;

#[cfg(windows)]
use windows_sys::Win32::System::Registry::{
    HKEY_CURRENT_USER, KEY_READ, KEY_WRITE, REG_SZ, RegCloseKey, RegDeleteValueW, RegOpenKeyExW,
    RegQueryValueExW, RegSetValueExW,
};

const RUN_KEY_PATH: &str = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";

fn to_wide(text: &str) -> Vec<u16> {
    text.encode_utf16().chain(std::iter::once(0)).collect()
}

#[cfg(windows)]
fn open_run_key(access: u32) -> io::Result<windows_sys::Win32::System::Registry::HKEY> {
    let mut key = std::ptr::null_mut();
    let path = to_wide(RUN_KEY_PATH);
    let result = unsafe { RegOpenKeyExW(HKEY_CURRENT_USER, path.as_ptr(), 0, access, &mut key) };
    if result != 0 {
        return Err(io::Error::from_raw_os_error(result as i32));
    }
    Ok(key)
}

/// 写入 Run Key 值（完整命令行由调用方按固定模板生成）。
#[cfg(windows)]
pub fn set_run_key(value_name: &str, command_line: &str) -> io::Result<()> {
    let key = open_run_key(KEY_WRITE)?;
    let name = to_wide(value_name);
    let data = to_wide(command_line);
    let bytes = unsafe { std::slice::from_raw_parts(data.as_ptr().cast::<u8>(), data.len() * 2) };
    let result = unsafe {
        RegSetValueExW(
            key,
            name.as_ptr(),
            0,
            REG_SZ,
            bytes.as_ptr(),
            bytes.len() as u32,
        )
    };
    unsafe { RegCloseKey(key) };
    if result != 0 {
        return Err(io::Error::from_raw_os_error(result as i32));
    }
    Ok(())
}

/// 删除 Run Key 值；不存在视为成功。
#[cfg(windows)]
pub fn delete_run_key(value_name: &str) -> io::Result<()> {
    let key = open_run_key(KEY_WRITE)?;
    let name = to_wide(value_name);
    let result = unsafe { RegDeleteValueW(key, name.as_ptr()) };
    unsafe { RegCloseKey(key) };
    const ERROR_FILE_NOT_FOUND: u32 = 2;
    if result != 0 && result != ERROR_FILE_NOT_FOUND {
        return Err(io::Error::from_raw_os_error(result as i32));
    }
    Ok(())
}

/// 读取 Run Key 值；不存在返回 Ok(None)。
#[cfg(windows)]
#[allow(dead_code)]
pub fn get_run_key(value_name: &str) -> io::Result<Option<String>> {
    let key = open_run_key(KEY_READ)?;
    let name = to_wide(value_name);
    let mut size = 0_u32;
    let mut value_type = 0_u32;
    let query = unsafe {
        RegQueryValueExW(
            key,
            name.as_ptr(),
            std::ptr::null(),
            &mut value_type,
            std::ptr::null_mut(),
            &mut size,
        )
    };
    const ERROR_FILE_NOT_FOUND: u32 = 2;
    if query == ERROR_FILE_NOT_FOUND {
        unsafe { RegCloseKey(key) };
        return Ok(None);
    }
    if query != 0 {
        unsafe { RegCloseKey(key) };
        return Err(io::Error::from_raw_os_error(query as i32));
    }
    let mut buffer = vec![0_u16; (size as usize).div_ceil(2)];
    let bytes =
        unsafe { std::slice::from_raw_parts_mut(buffer.as_mut_ptr().cast::<u8>(), size as usize) };
    let query = unsafe {
        RegQueryValueExW(
            key,
            name.as_ptr(),
            std::ptr::null(),
            &mut value_type,
            bytes.as_mut_ptr(),
            &mut size,
        )
    };
    unsafe { RegCloseKey(key) };
    if query != 0 {
        return Err(io::Error::from_raw_os_error(query as i32));
    }
    let text = String::from_utf16_lossy(&buffer)
        .trim_end_matches('\0')
        .to_string();
    Ok(Some(text))
}

/// Run Key 命令行模板（09 §9.2）：固定 Agent 路径 + 固定 channel 参数。
pub fn run_key_command(agent_exe: &std::path::Path) -> String {
    format!(
        "\"{}\" --channel {} --capture-on-start",
        agent_exe.display(),
        wuji_core::runtime_names::CHANNEL
    )
}

#[cfg(not(windows))]
pub fn set_run_key(_: &str, _: &str) -> io::Result<()> {
    Ok(())
}
#[cfg(not(windows))]
pub fn delete_run_key(_: &str) -> io::Result<()> {
    Ok(())
}
#[cfg(not(windows))]
pub fn get_run_key(_: &str) -> io::Result<Option<String>> {
    Ok(None)
}
