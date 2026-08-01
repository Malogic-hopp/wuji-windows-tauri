//! 运行控制语义服务（09 §8.3、§9.3）：顶栏 Tauri 命令与托盘菜单共用的
//! 唯一控制路径。
//!
//! - 统一解析 IPC 响应中的 `ok=false`，业务错误不被静默吞掉；
//! - Stop 在 `willExit`（接受确认）之外继续等待 SQLite runtime 终态
//!   `stopped`，证明 capture_stopped 边界与 graceful shutdown 已按序提交；
//! - 控制操作以 in-flight 互斥串行化，重复点击返回“操作进行中”，不排队重放。

use std::sync::Arc;

use serde_json::Value;
use tokio::sync::{Mutex, MutexGuard};
use wuji_core::domain::{CaptureState, ProcessState};
use wuji_core::dto::AgentStatusDto;
use wuji_core::error::{SafeError, SafeErrorCode};

use crate::agent_controller::AgentController;
use crate::ipc::AgentIpcClient;
use crate::query::QueryService;

const AGENT_STOP_WAIT: std::time::Duration = std::time::Duration::from_secs(20);
const AGENT_STOP_POLL: std::time::Duration = std::time::Duration::from_millis(100);

#[derive(Clone)]
pub struct ControlService {
    controller: AgentController,
    ipc: Arc<AgentIpcClient>,
    /// 控制操作 in-flight 互斥（跨 clone 共享，自动启动任务与命令共用一把锁）。
    in_flight: Arc<Mutex<()>>,
}

impl ControlService {
    pub fn new(controller: AgentController, ipc: Arc<AgentIpcClient>) -> Self {
        Self {
            controller,
            ipc,
            in_flight: Arc::new(Mutex::new(())),
        }
    }

    /// `agent_get_status` / Diagnostics / 托盘状态轮询：不占用 in-flight 锁，
    /// 长时间 Stop 等待期间状态仍可读。
    pub async fn status(&self) -> Result<AgentStatusDto, SafeError> {
        let response = self.ipc.call("status_get", serde_json::json!({})).await?;
        parse_status(response)
    }

    /// 开始记录：先确保 Agent 在线（重新创建进程并等待 hello），再提交
    /// capture_start（09 §9.3：新 Agent 初始 stopped）。
    pub async fn capture_start(&self) -> Result<AgentStatusDto, SafeError> {
        let _guard = self.acquire().await?;
        self.controller.ensure_running().await?;
        self.command("capture_start").await
    }

    /// 启动编排专用（09 §9.3）：先确保 Agent 在线，再提交
    /// `capture_ensure_recording`——Coordinator 内原子判定：Stopped→开始、
    /// Paused→恢复、Running→幂等成功。只由 Desktop 启动任务调用，不暴露给
    /// React；不绕过 Lock/Sleep 抑制、writer fault 与 Barrier 不变量。
    pub async fn ensure_recording(&self) -> Result<AgentStatusDto, SafeError> {
        let _guard = self.acquire().await?;
        self.controller.ensure_running().await?;
        self.command("capture_ensure_recording").await
    }

    pub async fn capture_pause(&self) -> Result<AgentStatusDto, SafeError> {
        let _guard = self.acquire().await?;
        self.command("capture_pause").await
    }

    pub async fn capture_resume(&self) -> Result<AgentStatusDto, SafeError> {
        let _guard = self.acquire().await?;
        self.command("capture_resume").await
    }

    /// 正式“停止 Agent”（09 §8.1/§8.3）：先提交 capture_stopped 边界，再
    /// graceful shutdown。`willExit` 只是接受确认；必须等 DB runtime 终态
    /// `stopped` 后才返回，证明完整退出序列已经推进。
    pub async fn stop_agent(&self, query: &QueryService) -> Result<AgentStatusDto, SafeError> {
        let _guard = self.acquire().await?;
        self.controller.stop_agent().await?;

        let deadline = tokio::time::Instant::now() + AGENT_STOP_WAIT;
        loop {
            if let Some(runtime) = query.latest_runtime()?
                && runtime.process_state == ProcessState::Stopped
            {
                return offline_status(&runtime);
            }
            if tokio::time::Instant::now() >= deadline {
                return Err(SafeError::new(
                    SafeErrorCode::InternalSafeError,
                    "Agent 已接受退出请求，但未在限时内完成关闭",
                ));
            }
            tokio::time::sleep(AGENT_STOP_POLL).await;
        }
    }

    /// 确保 Agent 在线（自动启动 / package-smoke 共用；不占用 in-flight 锁，
    /// 内部由 AgentController 启动互斥串行化，多来源并发只产生一个进程）。
    pub async fn ensure_running(&self) -> Result<Value, SafeError> {
        self.controller.ensure_running().await
    }

    /// Diagnostics 用固定 Agent 路径（脱敏展示）。
    pub fn agent_exe(&self) -> &std::path::PathBuf {
        self.controller.agent_exe()
    }

    async fn acquire(&self) -> Result<MutexGuard<'_, ()>, SafeError> {
        self.in_flight
            .try_lock()
            .map_err(|_| SafeError::new(SafeErrorCode::InternalSafeError, "操作进行中，请稍候重试"))
    }

    async fn command(&self, command: &str) -> Result<AgentStatusDto, SafeError> {
        let response = self.ipc.call(command, serde_json::json!({})).await?;
        parse_status(response)
    }
}

/// 解析 IPC 响应：`ok=false` 映射为稳定错误码，`result` 反序列化为 DTO。
pub(crate) fn parse_status(value: Value) -> Result<AgentStatusDto, SafeError> {
    if !value["ok"].as_bool().unwrap_or(false) {
        let code = value["error"]["code"].as_str().unwrap_or_default();
        let message = value["error"]["message"].as_str().unwrap_or("命令失败");
        let mapped: SafeErrorCode = serde_json::from_str(&format!("\"{code}\""))
            .unwrap_or(SafeErrorCode::InternalSafeError);
        return Err(SafeError::new(mapped, message));
    }
    serde_json::from_value::<AgentStatusDto>(value["result"].clone())
        .map_err(|_| SafeError::new(SafeErrorCode::InternalSafeError, "状态响应解析失败"))
}

/// IPC 不可达时的离线快照：历史数据不能证明进程或采集仍在运行（09 §10.5）。
pub(crate) fn offline_status(
    runtime: &wuji_storage::RuntimeRow,
) -> Result<AgentStatusDto, SafeError> {
    let mut dto = wuji_storage::reader::status_dto_from_runtime(
        runtime,
        String::new(),
        &wuji_core::dto::RuntimeId::parse(&runtime.runtime_id)?,
    );
    dto.process_state = ProcessState::Stopped;
    dto.capture_state = CaptureState::Stopped;
    Ok(dto)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::time::Duration;

    fn test_channel() -> String {
        format!(
            "rebuild-v01-test-{}",
            ulid::Ulid::generate().to_string().to_lowercase()
        )
    }

    fn service(channel: &str) -> ControlService {
        let ipc = Arc::new(AgentIpcClient::new(channel, "0.1.0").expect("ipc client"));
        let controller =
            AgentController::with_exe(channel, ipc.clone(), crate::paths::agent_exe_path());
        ControlService::new(controller, ipc)
    }

    #[tokio::test]
    async fn in_flight_guard_rejects_concurrent_control() {
        let channel = test_channel();
        let svc = service(&channel);
        let _held = svc.in_flight.try_lock().expect("guard 必须可获取");
        let error = svc.capture_pause().await.unwrap_err();
        assert_eq!(error.code, SafeErrorCode::InternalSafeError);
        assert!(error.message.contains("操作进行中"));
    }

    #[tokio::test]
    async fn ensure_recording_uses_in_flight_guard() {
        // 启动编排命令与顶栏/托盘共用同一 in-flight 互斥，重复进入被拒绝。
        let channel = test_channel();
        let svc = service(&channel);
        let _held = svc.in_flight.try_lock().expect("guard 必须可获取");
        let error = svc.ensure_recording().await.unwrap_err();
        assert_eq!(error.code, SafeErrorCode::InternalSafeError);
        assert!(error.message.contains("操作进行中"));
    }

    #[tokio::test]
    async fn in_flight_guard_released_after_op_finishes() {
        let channel = test_channel();
        let svc = service(&channel);
        // Agent 未运行：pause 应返回传输/连接错误，而不是“操作进行中”，
        // 证明 acquire 后锁已释放、命令真正尝试执行。
        let error = svc.capture_pause().await.unwrap_err();
        assert_ne!(error.message, "操作进行中，请稍候重试");
    }

    #[test]
    fn parse_status_maps_ok_false_to_stable_error() {
        let response = serde_json::json!({
            "ok": false,
            "error": { "code": "CAPTURE_INVALID_STATE", "message": "当前状态不能暂停" }
        });
        let error = parse_status(response).unwrap_err();
        assert_eq!(error.code, SafeErrorCode::CaptureInvalidState);
        assert_eq!(error.message, "当前状态不能暂停");
    }

    #[test]
    fn parse_status_accepts_ok_result() {
        let response = serde_json::json!({
            "ok": true,
            "result": {
                "agentVersion": "0.1.0",
                "protocolVersion": 1,
                "schemaVersion": 1,
                "processState": "running",
                "captureState": "paused",
                "writerState": "healthy",
                "runtimeId": "01JX0000000000000000000000",
                "heartbeatAtUtcMs": null,
                "lastObservationAtUtcMs": null,
                "lastWriteAtUtcMs": null,
                "captureQueueDepth": 0,
                "writerQueueDepth": 0,
                "droppedCaptureCount": "0",
                "droppedWriterCount": "0",
                "safeErrorCode": null
            }
        });
        let status = parse_status(response).expect("ok 响应必须解析成功");
        assert_eq!(
            status.capture_state,
            wuji_core::domain::CaptureState::Paused
        );
    }

    #[tokio::test]
    async fn stop_agent_waits_for_terminal_state() {
        // 与真实 Agent 的完整停止闭环由 host_integration 覆盖；
        // 这里验证无 Agent 时快速失败在传输错误，而不是死等超时。
        let channel = test_channel();
        let query = QueryService::new(&channel).expect("query");
        let svc = service(&channel);
        let result = tokio::time::timeout(Duration::from_secs(10), svc.stop_agent(&query)).await;
        assert!(result.is_ok(), "无 Agent 时必须快速失败而不是等待超时");
        assert!(result.unwrap().is_err());
    }
}
