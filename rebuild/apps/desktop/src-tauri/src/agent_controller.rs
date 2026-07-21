//! Agent 进程控制：detached 启动与版本门（09 §9.3）。
//!
//! Desktop 退出不停止 Agent；不继承 Desktop handles，不加入 Job；
//! 以 hello 验证 channel/version，而不是把 child handle 当作运行状态。

use std::path::PathBuf;
use std::time::Duration;

use serde_json::Value;
use wuji_core::error::{SafeError, SafeErrorCode};

use crate::ipc::AgentIpcClient;
use crate::paths;

const START_WAIT_ATTEMPTS: usize = 50;
const START_WAIT_INTERVAL: Duration = Duration::from_millis(200);

pub struct AgentController {
    agent_exe: PathBuf,
    channel: String,
    ipc: std::sync::Arc<AgentIpcClient>,
}

impl AgentController {
    pub fn new(channel: &str, ipc: std::sync::Arc<AgentIpcClient>) -> Result<Self, String> {
        Ok(Self {
            agent_exe: paths::agent_exe_path(),
            channel: channel.to_string(),
            ipc,
        })
    }

    #[doc(hidden)]
    pub fn with_exe(
        channel: &str,
        ipc: std::sync::Arc<AgentIpcClient>,
        agent_exe: PathBuf,
    ) -> Self {
        Self {
            agent_exe,
            channel: channel.to_string(),
            ipc,
        }
    }

    pub fn agent_exe(&self) -> &PathBuf {
        &self.agent_exe
    }

    /// `agent_process_ensure_running`：先 hello，失败再 detached 启动并等待握手。
    /// 普通启动不传 --capture-on-start（09 §9.3：新 Agent 初始为 stopped）。
    pub async fn ensure_running(&self) -> Result<Value, SafeError> {
        if let Ok(status) = self.ipc.status().await {
            check_compatible(&status)?;
            return Ok(status["result"].clone());
        }
        self.spawn_detached()?;
        for _ in 0..START_WAIT_ATTEMPTS {
            tokio::time::sleep(START_WAIT_INTERVAL).await;
            if let Ok(status) = self.ipc.status().await {
                check_compatible(&status)?;
                return Ok(status["result"].clone());
            }
        }
        Err(SafeError::new(
            SafeErrorCode::InternalSafeError,
            "Agent 启动超时，请查看诊断页",
        ))
    }

    /// CreateProcessW：DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP | CREATE_NO_WINDOW，
    /// 不继承 Desktop handles（09 §9.3）。
    #[cfg(windows)]
    fn spawn_detached(&self) -> Result<(), SafeError> {
        use windows_sys::Win32::Foundation::CloseHandle;
        use windows_sys::Win32::System::Threading::{
            CREATE_NEW_PROCESS_GROUP, CREATE_NO_WINDOW, CreateProcessW, DETACHED_PROCESS,
            PROCESS_INFORMATION, STARTUPINFOW,
        };

        let command = format!(
            "\"{}\" --channel {}",
            self.agent_exe.display(),
            self.channel
        );
        let mut command_wide: Vec<u16> = command.encode_utf16().chain(std::iter::once(0)).collect();
        unsafe {
            let mut info: STARTUPINFOW = std::mem::zeroed();
            info.cb = std::mem::size_of::<STARTUPINFOW>() as u32;
            let mut process: PROCESS_INFORMATION = std::mem::zeroed();
            let ok = CreateProcessW(
                std::ptr::null(),
                command_wide.as_mut_ptr(),
                std::ptr::null(),
                std::ptr::null(),
                0,
                DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP | CREATE_NO_WINDOW,
                std::ptr::null(),
                std::ptr::null(),
                &info,
                &mut process,
            );
            if ok == 0 {
                return Err(SafeError::new(
                    SafeErrorCode::InternalSafeError,
                    "无法启动 Agent 进程",
                ));
            }
            CloseHandle(process.hThread);
            CloseHandle(process.hProcess);
        }
        Ok(())
    }

    #[cfg(not(windows))]
    fn spawn_detached(&self) -> Result<(), SafeError> {
        Err(SafeError::new(
            SafeErrorCode::InternalSafeError,
            "仅支持 Windows",
        ))
    }
}

/// 版本门（09 §9.3）：protocol major 与 Schema version 任一不兼容即拒绝运行控制。
pub fn check_compatible(status: &Value) -> Result<(), SafeError> {
    let protocol = status["result"]["protocolVersion"].as_u64().unwrap_or(0);
    let schema = status["result"]["schemaVersion"].as_u64().unwrap_or(0);
    if protocol != 1 || schema != 1 {
        return Err(SafeError::new(
            SafeErrorCode::VersionIncompatible,
            "Desktop 与 Agent 版本不兼容，请升级后重试",
        ));
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn version_gate_accepts_protocol_and_schema_one() {
        let status = json!({"result": {"protocolVersion": 1, "schemaVersion": 1}});
        assert!(check_compatible(&status).is_ok());
    }

    #[test]
    fn version_gate_rejects_mismatch() {
        for status in [
            json!({"result": {"protocolVersion": 2, "schemaVersion": 1}}),
            json!({"result": {"protocolVersion": 1, "schemaVersion": 2}}),
            json!({"result": {}}),
        ] {
            let error = check_compatible(&status).unwrap_err();
            assert_eq!(error.code, SafeErrorCode::VersionIncompatible);
        }
    }
}
