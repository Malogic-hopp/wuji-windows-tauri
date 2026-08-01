//! Tauri commands（09 §8.3 白名单）。
//!
//! React 只调用这些语义命令；Pipe、数据库路径、SQL、channel 与原始采集信息
//! 不越过本层（ADR-002 §4.3）。

use serde::Serialize;
use tauri::State;
use wuji_core::domain::CaptureState;
use wuji_core::dto::{AgentStatusDto, HeatmapDto, SettingsDto, TimelinePageDto, TodayDto};
use wuji_core::error::{SafeError, SafeErrorCode};

use crate::control::ControlService;
use crate::desktop_prefs::{DesktopPrefs, DesktopPrefsPatch, DesktopPrefsService};
use crate::ipc::AgentIpcClient;
use crate::query::QueryService;
use crate::settings_service::{SettingsPatch, SettingsService};

/// 进程级服务集合（Tauri State）。
pub struct AppServices {
    pub channel: String,
    pub ipc: std::sync::Arc<AgentIpcClient>,
    pub query: QueryService,
    pub settings: SettingsService,
    pub desktop_prefs: DesktopPrefsService,
    pub control: ControlService,
    /// 自动开始记录编排结果（09 §9.3）：启动任务写入，顶栏/诊断可读。
    pub auto_start: AutoStartOutcome,
}

/// 自动开始记录编排状态（09 §9.3 启动结果可见化）：不能只写 stderr——
/// 失败必须由 UI 可见提示，成功前顶栏显示“正在开始记录…”瞬态。
/// 纯 Host 侧状态，不进 Specta/DTO 合同；跨 clone 共享。
#[derive(Debug, Clone, Default)]
pub struct AutoStartOutcome {
    inner: std::sync::Arc<std::sync::Mutex<AutoStartSnapshot>>,
}

/// 顶栏轮询的快照。
#[derive(Debug, Clone, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AutoStartSnapshot {
    pub state: AutoStartState,
    pub error: Option<SafeError>,
}

/// 编排阶段：idle=未启用（偏好关闭/smoke）；starting=正在确保记录；
/// recording=启动成功；failed=启动失败（含安全错误码与用户可见消息）。
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum AutoStartState {
    #[default]
    Idle,
    Starting,
    Recording,
    Failed,
}

impl AutoStartOutcome {
    fn set(&self, state: AutoStartState, error: Option<SafeError>) {
        *self.inner.lock().expect("auto-start outcome mutex") = AutoStartSnapshot { state, error };
    }

    pub(crate) fn mark_starting(&self) {
        self.set(AutoStartState::Starting, None);
    }

    pub(crate) fn mark_recording(&self) {
        self.set(AutoStartState::Recording, None);
    }

    pub(crate) fn mark_failed(&self, error: SafeError) {
        self.set(AutoStartState::Failed, Some(error));
    }

    /// 用户手动接管成功：清除自动启动失败提示（审核 P2：重试成功后红色
    /// 提示不得永久残留）。
    pub(crate) fn mark_idle(&self) {
        self.set(AutoStartState::Idle, None);
    }

    /// 顶栏与托盘共用的手动控制结果对账：只有用户意图真正被接受，才清除
    /// 自动启动失败；永久 monitor fault 的 `Ok(Paused)` 必须继续保留提示。
    pub(crate) fn reconcile_manual_control(&self, status: &AgentStatusDto, expected: CaptureState) {
        self.reconcile_manual_result(expected, status.capture_state, status.safe_error_code);
    }

    fn reconcile_manual_result(
        &self,
        expected: CaptureState,
        actual: CaptureState,
        safe_error_code: Option<SafeErrorCode>,
    ) {
        if manual_control_took_over(expected, actual, safe_error_code) {
            self.mark_idle();
        }
    }

    pub fn snapshot(&self) -> AutoStartSnapshot {
        self.inner.lock().expect("auto-start outcome mutex").clone()
    }
}

/// 手动控制只有真正接管自动启动意图后，才可清除此前的失败提示。
///
/// `start/resume` 在 Lock/Sleep 临时抑制下会合法返回 `Paused`，此时没有
/// `safe_error_code`，用户意图已经被接受、解除抑制后会自动恢复；永久 monitor
/// fault 同样会返回 `Ok(Paused)`，但保留 `safe_error_code`，必须继续显示失败，
/// 不能把“命令返回 Ok”误当成“故障已恢复”。
fn manual_control_took_over(
    expected: CaptureState,
    actual: CaptureState,
    safe_error_code: Option<SafeErrorCode>,
) -> bool {
    actual == expected
        || (expected == CaptureState::Running
            && actual == CaptureState::Paused
            && safe_error_code.is_none())
}

/// Diagnostics 摘要（09 §10.5：高级信息默认折叠且路径脱敏）。
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

/// 实时状态优先来自 IPC；Agent 离线时回退到 DB 最后已知快照（09 §10.5）。
#[tauri::command]
pub async fn agent_get_status(
    services: State<'_, AppServices>,
) -> Result<AgentStatusDto, SafeError> {
    match services.control.status().await {
        Ok(status) => Ok(status),
        Err(_) => {
            let runtime = services.query.latest_runtime()?.ok_or_else(|| {
                SafeError::new(
                    SafeErrorCode::DbUnavailable,
                    "无法连接 Agent，且没有历史运行记录",
                )
            })?;
            crate::control::offline_status(&runtime)
        }
    }
}

#[tauri::command]
pub async fn capture_start(services: State<'_, AppServices>) -> Result<AgentStatusDto, SafeError> {
    let status = services.control.capture_start().await?;
    services
        .auto_start
        .reconcile_manual_control(&status, CaptureState::Running);
    Ok(status)
}

#[tauri::command]
pub async fn capture_pause(services: State<'_, AppServices>) -> Result<AgentStatusDto, SafeError> {
    let status = services.control.capture_pause().await?;
    services
        .auto_start
        .reconcile_manual_control(&status, CaptureState::Paused);
    Ok(status)
}

#[tauri::command]
pub async fn capture_resume(services: State<'_, AppServices>) -> Result<AgentStatusDto, SafeError> {
    let status = services.control.capture_resume().await?;
    services
        .auto_start
        .reconcile_manual_control(&status, CaptureState::Running);
    Ok(status)
}

#[tauri::command]
pub async fn agent_process_stop(
    services: State<'_, AppServices>,
) -> Result<AgentStatusDto, SafeError> {
    services.control.stop_agent(&services.query).await
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
pub async fn activity_get_heatmap(
    services: State<'_, AppServices>,
    days: Option<u32>,
    week_offset: Option<i32>,
) -> Result<HeatmapDto, SafeError> {
    services.query.heatmap(days, week_offset)
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

/// `desktop_prefs_get`：Desktop 本地偏好（09 §9.4），与 Agent effectivity
/// Settings 分离，不参与 digest/CAS/LKG/数据库。
#[tauri::command]
pub async fn desktop_prefs_get(
    services: State<'_, AppServices>,
) -> Result<DesktopPrefs, SafeError> {
    services.desktop_prefs.get()
}

#[tauri::command]
pub async fn desktop_prefs_update(
    services: State<'_, AppServices>,
    patch: DesktopPrefsPatch,
) -> Result<DesktopPrefs, SafeError> {
    services.desktop_prefs.update(patch).await
}

/// `auto_start_status`：自动开始记录编排状态（09 §9.3）。顶栏用它显示
/// “正在开始记录…”瞬态与启动失败提示；Host 侧状态，不进 React 控制面。
#[tauri::command]
pub async fn auto_start_status(
    services: State<'_, AppServices>,
) -> Result<AutoStartSnapshot, SafeError> {
    Ok(services.auto_start.snapshot())
}

#[tauri::command]
pub async fn diagnostics_get_summary(
    services: State<'_, AppServices>,
) -> Result<DiagnosticsDto, SafeError> {
    let status = services.control.status().await.ok();
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
        agent_exe_masked: mask_local_app_data(&services.control.agent_exe().display().to_string()),
    })
}

/// 路径脱敏（09 §10.5）：用户目录一律以 %LOCALAPPDATA% 表示。
fn mask_local_app_data(path: &str) -> String {
    if let Some(prefix) = std::env::var_os("LOCALAPPDATA") {
        let prefix = prefix.to_string_lossy().to_string();
        if path.starts_with(&prefix) {
            return format!("%LOCALAPPDATA%{}", &path[prefix.len()..]);
        }
    }
    path.to_string()
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 启动结果状态机（09 §9.3）：idle → starting → recording；失败保留
    /// 安全错误且不伪装成功；snapshot 与序列化形状供顶栏消费。
    #[test]
    fn auto_start_outcome_transitions() {
        let outcome = AutoStartOutcome::default();
        assert_eq!(outcome.snapshot().state, AutoStartState::Idle);
        assert!(outcome.snapshot().error.is_none());

        outcome.mark_starting();
        assert_eq!(outcome.snapshot().state, AutoStartState::Starting);

        outcome.mark_recording();
        assert_eq!(outcome.snapshot().state, AutoStartState::Recording);

        outcome.mark_failed(SafeError::new(
            SafeErrorCode::AgentWriterFaulted,
            "写入器故障且无法恢复，采集保持停止",
        ));
        let snapshot = outcome.snapshot();
        assert_eq!(snapshot.state, AutoStartState::Failed);
        assert_eq!(
            snapshot.error.as_ref().expect("失败必须携带错误").code,
            SafeErrorCode::AgentWriterFaulted
        );

        // 序列化形状（camelCase state + error envelope）由前端 client.ts 消费。
        let json = serde_json::to_value(&snapshot).unwrap();
        assert_eq!(json["state"], "failed");
        assert_eq!(json["error"]["code"], "AGENT_WRITER_FAULTED");

        // 用户手动接管成功 → mark_idle：失败提示不得永久残留（审核 P2）。
        outcome.mark_idle();
        let idle = outcome.snapshot();
        assert_eq!(idle.state, AutoStartState::Idle);
        assert!(idle.error.is_none());
    }

    #[test]
    fn permanent_monitor_fault_is_not_hidden_by_ok_paused_manual_start() {
        let outcome = AutoStartOutcome::default();
        outcome.mark_failed(SafeError::new(
            SafeErrorCode::InternalSafeError,
            "事件监视已永久失效，无法开始采集",
        ));

        outcome.reconcile_manual_result(
            CaptureState::Running,
            CaptureState::Paused,
            Some(SafeErrorCode::InternalSafeError),
        );
        let snapshot = outcome.snapshot();
        assert_eq!(snapshot.state, AutoStartState::Failed);
        assert_eq!(
            snapshot.error.expect("永久故障提示必须保留").message,
            "事件监视已永久失效，无法开始采集"
        );
    }

    #[test]
    fn manual_control_clears_failure_only_after_intent_is_accepted() {
        // 已真正恢复 Running：即使状态仍带历史诊断，也已经完成手动接管。
        assert!(manual_control_took_over(
            CaptureState::Running,
            CaptureState::Running,
            Some(SafeErrorCode::InternalSafeError),
        ));
        // Lock/Sleep 临时抑制：返回 Paused 且无故障码，意图已被接受。
        assert!(manual_control_took_over(
            CaptureState::Running,
            CaptureState::Paused,
            None,
        ));
        // pause 只有实际进入 Paused 才算接管完成。
        assert!(manual_control_took_over(
            CaptureState::Paused,
            CaptureState::Paused,
            None,
        ));
        assert!(!manual_control_took_over(
            CaptureState::Paused,
            CaptureState::Stopped,
            None,
        ));
    }
}
