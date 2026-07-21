//! 运行路径解析（09 §4.1、§9.3）。
//!
//! Agent 固定安装位置：`<desktop-exe-dir>\Agent\wuji-rebuild-agent-v01.exe`；
//! debug 开发态例外：target 目录下与 desktop 同级的 agent 二进制。

use std::path::PathBuf;

use wuji_core::runtime_names;

/// Agent 可执行文件路径（09 §9.3 固定位置 + debug 开发态）。
pub fn agent_exe_path() -> PathBuf {
    #[cfg(debug_assertions)]
    {
        if let Ok(current) = std::env::current_exe()
            && let Some(dir) = current.parent()
        {
            let candidate = dir.join(wuji_core::runtime_names::AGENT_EXE_NAME);
            if candidate.exists() {
                return candidate;
            }
        }
        PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .join("../../../target/debug")
            .join(wuji_core::runtime_names::AGENT_EXE_NAME)
    }
    #[cfg(not(debug_assertions))]
    {
        std::env::current_exe()
            .ok()
            .and_then(|p| p.parent().map(|d| d.to_path_buf()))
            .unwrap_or_else(|| PathBuf::from("."))
            .join(wuji_core::runtime_names::AGENT_EXE_RELATIVE_PATH)
    }
}

/// 当前 channel 的 user-scope 与 pipe/mutex 名（与 agent 侧一致，含 test channel 隔离）。
pub fn channel_names(channel: &str) -> Result<(String, String), String> {
    let sid = wuji_windows::current_user_sid().map_err(|e| format!("无法读取当前用户 SID: {e}"))?;
    let scope = runtime_names::user_scope(&sid);
    if channel == runtime_names::CHANNEL {
        Ok((
            runtime_names::pipe_name(&scope),
            runtime_names::agent_mutex_name(&scope),
        ))
    } else if runtime_names::is_allowed_channel(channel) {
        Ok((
            format!("\\\\.\\pipe\\WUJI.Rebuild.V01.Test.{channel}.{scope}"),
            format!("Local\\WUJI.Rebuild.V01.Test.{channel}.Agent.{scope}"),
        ))
    } else {
        Err(format!("拒绝非法 channel: {channel}"))
    }
}

/// Desktop 单实例 mutex 名（dev 固定值或 test channel 派生）。
pub fn desktop_mutex_name(channel: &str) -> Result<String, String> {
    let sid = wuji_windows::current_user_sid().map_err(|e| format!("无法读取当前用户 SID: {e}"))?;
    let scope = runtime_names::user_scope(&sid);
    if channel == runtime_names::CHANNEL {
        Ok(runtime_names::desktop_mutex_name(&scope))
    } else if runtime_names::is_allowed_channel(channel) {
        Ok(format!(
            "Local\\WUJI.Rebuild.V01.Test.{channel}.Desktop.{scope}"
        ))
    } else {
        Err(format!("拒绝非法 channel: {channel}"))
    }
}

/// 数据根（与 agent 侧规则一致）。
pub fn data_root(channel: &str) -> Result<PathBuf, String> {
    if !runtime_names::is_allowed_channel(channel) {
        return Err(format!("拒绝非法 channel: {channel}"));
    }
    let suffix = if channel == runtime_names::CHANNEL {
        "dev"
    } else {
        channel
    };
    let local_app_data = std::env::var_os("LOCALAPPDATA").ok_or("LOCALAPPDATA 未设置")?;
    Ok(PathBuf::from(local_app_data)
        .join("WUJI-Rebuild-V01")
        .join(suffix))
}
