//! 固定运行命名空间（09 §4.1）。
//!
//! 这些值是编译期常量，不接受 React 或命令行传入的任意路径/标识。
//! 原始用户 SID 只用于派生 user-scope 哈希，不写入任何产物。

use sha2::{Digest, Sha256};

/// v0.1 固定 channel。
pub const CHANNEL: &str = "rebuild-v01-dev";
/// Desktop 可执行文件名。
pub const DESKTOP_EXE_NAME: &str = "wuji-rebuild-desktop-v01.exe";
/// Agent 可执行文件名。
pub const AGENT_EXE_NAME: &str = "wuji-rebuild-agent-v01.exe";
/// Desktop 安装目录下 Agent 的固定相对位置（09 §9.3）。
pub const AGENT_EXE_RELATIVE_PATH: &str = "Agent\\wuji-rebuild-agent-v01.exe";
/// Tauri identifier。
pub const TAURI_IDENTIFIER: &str = "com.wuji.rebuild.v01.dev";
/// 产品名。
pub const PRODUCT_NAME: &str = "吾迹 Rebuild v0.1（开发）";
/// %LOCALAPPDATA% 下的数据根（相对路径）。
pub const DATA_ROOT_RELATIVE: &str = "WUJI-Rebuild-V01\\dev";
/// 数据根下的数据库相对路径。
pub const DATABASE_RELATIVE: &str = "data\\wuji-rebuild-v0.1.db";
/// 数据根下的 Settings 相对路径。
pub const SETTINGS_RELATIVE: &str = "config\\settings.json";
/// 数据根下的日志目录相对路径。
pub const LOGS_RELATIVE: &str = "logs";
/// Run Key 值名。
pub const RUN_KEY_VALUE_NAME: &str = "WUJI Rebuild v0.1 Dev";

const PIPE_PREFIX: &str = "\\\\.\\pipe\\WUJI.Rebuild.V01.Dev.";
const AGENT_MUTEX_PREFIX: &str = "Local\\WUJI.Rebuild.V01.Dev.Agent.";
const DESKTOP_MUTEX_PREFIX: &str = "Local\\WUJI.Rebuild.V01.Dev.Desktop.";
const TEST_CHANNEL_PREFIX: &str = "rebuild-v01-test-";

/// user-scope：当前用户 SID UTF-8 表示的 SHA-256 前 16 个小写十六进制字符（09 §4.1）。
pub fn user_scope(windows_sid: &str) -> String {
    let digest = Sha256::digest(windows_sid.as_bytes());
    digest[..8].iter().map(|b| format!("{b:02x}")).collect()
}

/// 固定 Pipe 全名；不含原始 SID。
pub fn pipe_name(scope: &str) -> String {
    format!("{PIPE_PREFIX}{scope}")
}

/// Agent 单实例 mutex 名。
pub fn agent_mutex_name(scope: &str) -> String {
    format!("{AGENT_MUTEX_PREFIX}{scope}")
}

/// Desktop 单实例 mutex 名。
pub fn desktop_mutex_name(scope: &str) -> String {
    format!("{DESKTOP_MUTEX_PREFIX}{scope}")
}

/// channel 是否合法：固定 dev channel，或显式测试 channel `rebuild-v01-test-<ulid>`（09 §4.1）。
pub fn is_allowed_channel(channel: &str) -> bool {
    if channel == CHANNEL {
        return true;
    }
    channel
        .strip_prefix(TEST_CHANNEL_PREFIX)
        .is_some_and(|suffix| {
            suffix.len() == 26 && suffix.bytes().all(|b| b.is_ascii_alphanumeric())
        })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn user_scope_is_16_lowercase_hex_and_hides_sid() {
        let sid = "S-1-5-21-3623811015-3361044348-30300820-1013";
        let scope = user_scope(sid);
        assert_eq!(scope.len(), 16);
        assert!(
            scope
                .chars()
                .all(|c| c.is_ascii_hexdigit() && !c.is_ascii_uppercase())
        );
        assert!(!pipe_name(&scope).contains(sid));
        assert!(!agent_mutex_name(&scope).contains(sid));
        assert!(!desktop_mutex_name(&scope).contains(sid));
    }

    #[test]
    fn names_match_baseline() {
        let scope = "0123456789abcdef";
        assert_eq!(
            pipe_name(scope),
            "\\\\.\\pipe\\WUJI.Rebuild.V01.Dev.0123456789abcdef"
        );
        assert_eq!(
            agent_mutex_name(scope),
            "Local\\WUJI.Rebuild.V01.Dev.Agent.0123456789abcdef"
        );
        assert_eq!(
            desktop_mutex_name(scope),
            "Local\\WUJI.Rebuild.V01.Dev.Desktop.0123456789abcdef"
        );
        assert_eq!(CHANNEL, "rebuild-v01-dev");
        assert_eq!(TAURI_IDENTIFIER, "com.wuji.rebuild.v01.dev");
    }

    #[test]
    fn channel_validation() {
        assert!(is_allowed_channel(CHANNEL));
        assert!(is_allowed_channel(
            "rebuild-v01-test-01J0000000000000000000000X"
        ));
        assert!(!is_allowed_channel("rebuild-v01-test-short"));
        assert!(!is_allowed_channel(
            "rebuild-v01-test-01J000000000000000000000!"
        ));
        assert!(!is_allowed_channel("prod"));
        assert!(!is_allowed_channel("rebuild-v02-dev"));
    }
}
