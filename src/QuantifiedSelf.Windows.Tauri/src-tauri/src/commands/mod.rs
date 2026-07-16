use tauri::State;

use crate::{
    bridge::{BridgeSupervisor, CommandError},
    contracts::{AgentStatus, ClientInitializeResult, CommandResult},
};

#[cfg(test)]
pub const COMMAND_WHITELIST: &[&str] = &[
    "app_initialize",
    "agent_get_status",
    "agent_start",
    "agent_pause",
    "agent_resume",
    "agent_stop",
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
        assert_eq!(COMMAND_WHITELIST.len(), 7);
        assert!(COMMAND_WHITELIST.iter().all(|command| {
            !command.contains("shell")
                && !command.contains("file")
                && !command.contains("sql")
                && !command.contains("http")
                && !command.contains("execute")
        }));
    }
}
