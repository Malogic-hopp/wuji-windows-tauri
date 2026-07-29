//! Tauri commands（09 §8.3 白名单）。
//!
//! React 只调用这些语义命令；Pipe、数据库路径、SQL、channel 与原始采集信息
//! 不越过本层（ADR-002 §4.3）。

use serde::Serialize;
use serde_json::Value;
use tauri::State;
use wuji_core::domain::{CaptureState, ProcessState};
use wuji_core::dto::{AgentStatusDto, SettingsDto, TimelinePageDto, TodayDto};
use wuji_core::error::{SafeError, SafeErrorCode};

use crate::agent_controller::AgentController;
use crate::ipc::AgentIpcClient;
use crate::query::QueryService;
use crate::settings_service::{SettingsPatch, SettingsService};

/// 进程级服务集合（Tauri State）。
pub struct AppServices {
    pub channel: String,
    pub ipc: std::sync::Arc<AgentIpcClient>,
    pub query: QueryService,
    pub settings: SettingsService,
    pub controller: AgentController,
}

/// Diagnostics 摘要（09 §10.4：高级信息默认折叠且路径脱敏）。
#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DiagnosticsDto {
    pub status: Option<AgentStatusDto>,
    pub database_reachable: bool,
    pub settings_persisted: bool,
    pub applied_revision: String,
    pub reporting_time_zone_id: Option<String>,
    pub data_root_masked: String,
    pub agent_exe_masked: String,
}

const AGENT_STOP_WAIT: std::time::Duration = std::time::Duration::from_secs(20);
const AGENT_STOP_POLL: std::time::Duration = std::time::Duration::from_millis(100);

fn parse_status(value: Value) -> Result<AgentStatusDto, SafeError> {
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

async fn call_status(ipc: &AgentIpcClient, command: &str) -> Result<AgentStatusDto, SafeError> {
    let response = ipc.call(command, serde_json::json!({})).await?;
    parse_status(response)
}

fn offline_status(runtime: &wuji_storage::RuntimeRow) -> Result<AgentStatusDto, SafeError> {
    let mut dto = wuji_storage::reader::status_dto_from_runtime(
        runtime,
        String::new(),
        &wuji_core::dto::RuntimeId::parse(&runtime.runtime_id)?,
    );
    // IPC 不可达时，历史快照不能证明进程或采集仍在运行。
    dto.process_state = ProcessState::Stopped;
    dto.capture_state = CaptureState::Stopped;
    Ok(dto)
}

/// 实时状态优先来自 IPC；Agent 离线时回退到 DB 最后已知快照（09 §10.4）。
#[tauri::command]
pub async fn agent_get_status(
    services: State<'_, AppServices>,
) -> Result<AgentStatusDto, SafeError> {
    match call_status(&services.ipc, "status_get").await {
        Ok(status) => Ok(status),
        Err(_) => {
            let runtime = services.query.latest_runtime()?.ok_or_else(|| {
                SafeError::new(
                    SafeErrorCode::DbUnavailable,
                    "无法连接 Agent，且没有历史运行记录",
                )
            })?;
            offline_status(&runtime)
        }
    }
}

#[tauri::command]
pub async fn capture_start(services: State<'_, AppServices>) -> Result<AgentStatusDto, SafeError> {
    // Agent 已被显式停止时，Start 负责重新创建进程并等待 hello，再开始记录。
    services.controller.ensure_running().await?;
    call_status(&services.ipc, "capture_start").await
}

#[tauri::command]
pub async fn capture_pause(services: State<'_, AppServices>) -> Result<AgentStatusDto, SafeError> {
    call_status(&services.ipc, "capture_pause").await
}

#[tauri::command]
pub async fn capture_resume(services: State<'_, AppServices>) -> Result<AgentStatusDto, SafeError> {
    call_status(&services.ipc, "capture_resume").await
}

#[tauri::command]
pub async fn agent_process_stop(
    services: State<'_, AppServices>,
) -> Result<AgentStatusDto, SafeError> {
    services.controller.stop_agent().await?;

    // willExit 是接受确认，不是退出证明。等待 Writer 把 runtime 标记为 stopped，
    // 证明 capture_stopped + AgentShutdown 已按顺序提交完毕。
    let deadline = tokio::time::Instant::now() + AGENT_STOP_WAIT;
    loop {
        if let Some(runtime) = services.query.latest_runtime()?
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

#[tauri::command]
pub async fn activity_get_today(services: State<'_, AppServices>) -> Result<TodayDto, SafeError> {
    services.query.today()
}

#[tauri::command]
pub async fn activity_get_timeline(
    services: State<'_, AppServices>,
    local_date: String,
    cursor: Option<String>,
    limit: Option<u32>,
) -> Result<TimelinePageDto, SafeError> {
    services.query.timeline(&local_date, cursor, limit)
}

#[tauri::command]
pub async fn settings_get(services: State<'_, AppServices>) -> Result<SettingsDto, SafeError> {
    services.settings.get(&services.query)
}

#[tauri::command]
pub async fn settings_update(
    services: State<'_, AppServices>,
    patch: SettingsPatch,
) -> Result<SettingsDto, SafeError> {
    services.settings.update(patch, &services.ipc).await
}

#[tauri::command]
pub async fn settings_resync_login_startup(
    services: State<'_, AppServices>,
) -> Result<SettingsDto, SafeError> {
    services.settings.resync_login_startup(&services.query)
}

#[tauri::command]
pub async fn diagnostics_get_summary(
    services: State<'_, AppServices>,
) -> Result<DiagnosticsDto, SafeError> {
    let status = call_status(&services.ipc, "status_get").await.ok();
    let persisted = services.settings.path().exists();
    let applied = services
        .query
        .applied_settings_revision()
        .unwrap_or_else(|_| "0".to_string());
    let tz = wuji_storage::Reader::open(services.query.database_path())
        .ok()
        .map(|reader| reader.schema_meta().reporting_time_zone_id.clone());
    Ok(DiagnosticsDto {
        status,
        database_reachable: services.query.database_reachable(),
        settings_persisted: persisted,
        applied_revision: applied,
        reporting_time_zone_id: tz,
        data_root_masked: mask_local_app_data(
            &crate::paths::data_root(&services.channel)
                .map(|p| p.display().to_string())
                .unwrap_or_default(),
        ),
        agent_exe_masked: mask_local_app_data(
            &services.controller.agent_exe().display().to_string(),
        ),
    })
}

/// 路径脱敏（09 §10.4）：用户目录一律以 %LOCALAPPDATA% 表示。
fn mask_local_app_data(path: &str) -> String {
    if let Some(prefix) = std::env::var_os("LOCALAPPDATA") {
        let prefix = prefix.to_string_lossy().to_string();
        if path.starts_with(&prefix) {
            return format!("%LOCALAPPDATA%{}", &path[prefix.len()..]);
        }
    }
    path.to_string()
}
