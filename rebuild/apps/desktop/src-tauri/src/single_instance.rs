//! 单实例（09 §4.1：Desktop mutex 按 channel 与用户隔离）。

use crate::paths;

pub enum InstanceDecision {
    Primary(wuji_windows::SingleInstanceGuard),
    Secondary,
}

pub fn acquire(channel: &str) -> Result<InstanceDecision, String> {
    let mutex = paths::desktop_mutex_name(channel)?;
    match wuji_windows::SingleInstanceGuard::acquire(&mutex)
        .map_err(|e| format!("单实例 mutex 创建失败: {e}"))?
    {
        Some(guard) => Ok(InstanceDecision::Primary(guard)),
        None => Ok(InstanceDecision::Secondary),
    }
}
