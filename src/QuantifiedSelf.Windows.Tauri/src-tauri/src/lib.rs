#![allow(
    linker_messages,
    reason = "MSVC emits a localized informational line when creating the import library"
)]

mod bridge;
mod commands;
mod contracts;

use std::sync::{
    Arc,
    atomic::{AtomicBool, Ordering},
};

use bridge::{BridgeSupervisor, fixed_bridge_path};
use commands::{
    activity_get_overview, agent_get_status, agent_pause, agent_resume, agent_start, agent_stop,
    app_initialize, bridge_retry,
};
use tauri::{Manager, RunEvent};

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    let app = tauri::Builder::default()
        .setup(|app| {
            app.manage(BridgeSupervisor::start(
                fixed_bridge_path(),
                app.handle().clone(),
            ));
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            app_initialize,
            agent_get_status,
            agent_start,
            agent_pause,
            agent_resume,
            agent_stop,
            activity_get_overview,
            bridge_retry,
        ])
        .build(tauri::generate_context!())
        .expect("failed to build WUJI Tauri application");

    let shutdown_started = Arc::new(AtomicBool::new(false));
    app.run(move |app_handle, event| {
        if matches!(event, RunEvent::ExitRequested { .. })
            && !shutdown_started.swap(true, Ordering::SeqCst)
        {
            let supervisor = app_handle.state::<BridgeSupervisor>();
            let _ = tauri::async_runtime::block_on(supervisor.shutdown());
        }
    });
}

#[cfg(test)]
mod tests {
    use super::commands::COMMAND_WHITELIST;

    #[test]
    fn invoke_handler_and_documented_whitelist_stay_small() {
        assert_eq!(
            COMMAND_WHITELIST,
            [
                "app_initialize",
                "agent_get_status",
                "agent_start",
                "agent_pause",
                "agent_resume",
                "agent_stop",
                "activity_get_overview",
                "bridge_retry",
            ]
        );
    }
}
