#![allow(
    linker_messages,
    reason = "MSVC emits a localized informational line when creating the import library"
)]

mod bridge;
mod commands;
mod contracts;
mod lifecycle;

use bridge::{BridgeSupervisor, fixed_bridge_path};
use commands::{
    activity_get_overview, agent_get_status, agent_pause, agent_resume, agent_start, agent_stop,
    app_cancel_close, app_initialize, app_request_exit, app_set_unsaved_changes, bridge_retry,
    settings_get, settings_update, window_hide, window_show,
};
use lifecycle::{HostLifecycle, handle_window_event, permits_exit, request_exit, setup_tray};
use tauri::{Manager, RunEvent};

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    let app = tauri::Builder::default()
        .setup(|app| {
            app.manage(HostLifecycle::default());
            app.manage(BridgeSupervisor::start(
                fixed_bridge_path(),
                app.handle().clone(),
            ));
            setup_tray(app)?;
            Ok(())
        })
        .on_window_event(handle_window_event)
        .invoke_handler(tauri::generate_handler![
            app_initialize,
            agent_get_status,
            agent_start,
            agent_pause,
            agent_resume,
            agent_stop,
            activity_get_overview,
            settings_get,
            settings_update,
            bridge_retry,
            app_set_unsaved_changes,
            window_show,
            window_hide,
            app_request_exit,
            app_cancel_close,
        ])
        .build(tauri::generate_context!())
        .expect("failed to build WUJI Tauri application");

    app.run(move |app_handle, event| {
        if let RunEvent::ExitRequested { api, .. } = event
            && !permits_exit(app_handle)
        {
            api.prevent_exit();
            request_exit(app_handle);
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
                "settings_get",
                "settings_update",
                "bridge_retry",
                "app_set_unsaved_changes",
                "window_show",
                "window_hide",
                "app_request_exit",
                "app_cancel_close",
            ]
        );
    }
}
