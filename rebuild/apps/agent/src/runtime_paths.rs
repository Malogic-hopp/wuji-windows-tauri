//! 固定运行路径解析（09 §4.1）。
//!
//! 路径由可信 Rust Path Resolver 生成；channel 只能是固定 dev channel 或
//! `rebuild-v01-test-<ulid>` 测试 channel（09 §4.1），不接受任意路径。

use std::path::PathBuf;

use wuji_core::runtime_names;

pub struct RuntimePaths {
    pub channel: String,
    pub data_root: PathBuf,
    pub database: PathBuf,
    pub settings: PathBuf,
    pub logs: PathBuf,
    pub pipe_name: String,
    pub agent_mutex: String,
}

pub fn resolve(channel: &str) -> Result<RuntimePaths, String> {
    if !runtime_names::is_allowed_channel(channel) {
        return Err(format!("拒绝非法 channel: {channel}"));
    }
    let sid = wuji_windows::current_user_sid().map_err(|e| format!("无法读取当前用户 SID: {e}"))?;
    let scope = runtime_names::user_scope(&sid);

    let (suffix, pipe_name, agent_mutex) = if channel == runtime_names::CHANNEL {
        (
            "dev".to_string(),
            runtime_names::pipe_name(&scope),
            runtime_names::agent_mutex_name(&scope),
        )
    } else {
        // 测试 channel 派生隔离命名空间（09 §4.1）。
        (
            channel.to_string(),
            format!("\\\\.\\pipe\\WUJI.Rebuild.V01.Test.{channel}.{scope}"),
            format!("Local\\WUJI.Rebuild.V01.Test.{channel}.Agent.{scope}"),
        )
    };
    let local_app_data =
        std::env::var_os("LOCALAPPDATA").ok_or_else(|| "LOCALAPPDATA 未设置".to_string())?;
    let data_root = PathBuf::from(local_app_data)
        .join("WUJI-Rebuild-V01")
        .join(suffix);

    Ok(RuntimePaths {
        channel: channel.to_string(),
        database: data_root.join("data").join("wuji-rebuild-v0.1.db"),
        settings: data_root.join("config").join("settings.json"),
        logs: data_root.join("logs"),
        pipe_name,
        agent_mutex,
        data_root,
    })
}
