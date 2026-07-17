use tauri::{AppHandle, State};

use crate::{
    bridge::{BridgeSupervisor, CommandError},
    contracts::{
        ActivityOverviewResult, AgentStatus, ClientInitializeResult, CommandResult,
        SettingsGetResult, SettingsUpdateParams, SettingsUpdateResult,
    },
    lifecycle::{
        LifecycleCommandError, cancel_close, hide_main_window, request_exit, set_unsaved_changes,
        show_main_window,
    },
};

#[cfg(test)]
pub const COMMAND_WHITELIST: &[&str] = &[
    "app_initialize",
    "agent_get_status",
    "agent_start",
    "agent_pause",
    "agent_resume",
    "agent_stop",
    "activity_get_overview",
    "settings_get",
    "settings_update",
    "bridge_retry",
    "app_set_unsaved_changes",
    "window_show",
    "window_hide",
    "app_request_exit",
    "app_cancel_close",
];

#[tauri::command]
pub async fn app_initialize(
    supervisor: State<'_, BridgeSupervisor>,
) -> Result<ClientInitializeResult, CommandError> {
    supervisor.request("client.initialize").await
}

#[tauri::command]
pub async fn agent_get_status(
    supervisor: State<'_, BridgeSupervisor>,
) -> Result<AgentStatus, CommandError> {
    supervisor.request("agent.getStatus").await
}

#[tauri::command]
pub async fn agent_start(
    supervisor: State<'_, BridgeSupervisor>,
) -> Result<CommandResult, CommandError> {
    supervisor.request("agent.start").await
}

#[tauri::command]
pub async fn agent_pause(
    supervisor: State<'_, BridgeSupervisor>,
) -> Result<CommandResult, CommandError> {
    supervisor.request("agent.pause").await
}

#[tauri::command]
pub async fn agent_resume(
    supervisor: State<'_, BridgeSupervisor>,
) -> Result<CommandResult, CommandError> {
    supervisor.request("agent.resume").await
}

#[tauri::command]
pub async fn agent_stop(
    supervisor: State<'_, BridgeSupervisor>,
) -> Result<CommandResult, CommandError> {
    supervisor.request("agent.stop").await
}

#[tauri::command]
pub async fn activity_get_overview(
    supervisor: State<'_, BridgeSupervisor>,
) -> Result<ActivityOverviewResult, CommandError> {
    supervisor.request("activity.getOverview").await
}

#[tauri::command]
pub async fn settings_get(
    supervisor: State<'_, BridgeSupervisor>,
) -> Result<SettingsGetResult, CommandError> {
    supervisor.request("settings.get").await
}

#[tauri::command]
pub async fn settings_update(
    supervisor: State<'_, BridgeSupervisor>,
    request: SettingsUpdateParams,
) -> Result<SettingsUpdateResult, CommandError> {
    supervisor
        .request_with_params("settings.update", request)
        .await
}

#[tauri::command]
pub async fn bridge_retry(
    supervisor: State<'_, BridgeSupervisor>,
) -> Result<ClientInitializeResult, CommandError> {
    supervisor.retry().await
}

#[tauri::command]
pub fn app_set_unsaved_changes(app: AppHandle, has_unsaved_changes: bool) {
    set_unsaved_changes(&app, has_unsaved_changes);
}

#[tauri::command]
pub fn window_show(app: AppHandle) -> Result<(), LifecycleCommandError> {
    show_main_window(&app)
}

#[tauri::command]
pub fn window_hide(app: AppHandle) -> Result<(), LifecycleCommandError> {
    hide_main_window(&app)
}

#[tauri::command]
pub fn app_request_exit(app: AppHandle) {
    request_exit(&app);
}

#[tauri::command]
pub fn app_cancel_close(app: AppHandle) {
    cancel_close(&app);
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn exposes_only_semantic_commands() {
        assert_eq!(COMMAND_WHITELIST.len(), 15);
        assert!(COMMAND_WHITELIST.iter().all(|command| {
            !command.contains("shell")
                && !command.contains("file")
                && !command.contains("sql")
                && !command.contains("http")
                && !command.contains("execute")
        }));
    }
}
