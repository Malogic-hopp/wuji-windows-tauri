use tauri::State;

use crate::{
    bridge::{BridgeSupervisor, CommandError},
    contracts::{
        ActivityOverviewResult, AgentStatus, ClientInitializeResult, CommandResult,
        SettingsGetResult, SettingsUpdateParams, SettingsUpdateResult,
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

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn exposes_only_semantic_commands() {
        assert_eq!(COMMAND_WHITELIST.len(), 10);
        assert!(COMMAND_WHITELIST.iter().all(|command| {
            !command.contains("shell")
                && !command.contains("file")
                && !command.contains("sql")
                && !command.contains("http")
                && !command.contains("execute")
        }));
    }
}
