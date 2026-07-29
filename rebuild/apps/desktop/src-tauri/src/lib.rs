//! WUJI Rebuild v0.1 Tauri Desktop Host（09 §4、§8.3、§9.3）。
//!
//! 不含 Bridge/BridgeSupervisor：React → 本 Host → Rust Agent / SQLite（只读）。

pub mod agent_controller;
mod commands;
pub mod ipc;
pub mod paths;
pub mod query;
pub mod settings_service;
mod single_instance;
pub mod startup_registry;
mod tray;

use commands::{
    AppServices, activity_get_timeline, activity_get_today, agent_get_status, agent_process_stop,
    capture_pause, capture_resume, capture_start, diagnostics_get_summary, settings_get,
    settings_resync_login_startup, settings_update,
};
use tauri::Manager as _;

const DESKTOP_VERSION: &str = env!("CARGO_PKG_VERSION");

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    let channel = std::env::var("WUJI_REBUILD_CHANNEL")
        .unwrap_or_else(|_| wuji_core::runtime_names::CHANNEL.to_string());

    match single_instance::acquire(&channel) {
        Ok(single_instance::InstanceDecision::Primary(guard)) => {
            std::mem::forget(guard);
        }
        Ok(single_instance::InstanceDecision::Secondary) => {
            eprintln!("已有同 channel Desktop 在运行");
            return;
        }
        Err(error) => {
            eprintln!("单实例检查失败: {error}");
            return;
        }
    }

    let services = match build_services(&channel) {
        Ok(services) => services,
        Err(error) => {
            eprintln!("服务初始化失败: {error}");
            return;
        }
    };

    tauri::Builder::default()
        .setup(move |app| {
            app.manage(services);
            tray::setup_tray(app)?;
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            agent_process_stop,
            agent_get_status,
            capture_start,
            capture_pause,
            capture_resume,
            activity_get_today,
            activity_get_timeline,
            settings_get,
            settings_update,
            settings_resync_login_startup,
            diagnostics_get_summary,
        ])
        .build(tauri::generate_context!())
        .expect("failed to build WUJI Rebuild desktop")
        .run(|_app, _event| {});
}

fn build_services(channel: &str) -> Result<AppServices, String> {
    let ipc = std::sync::Arc::new(ipc::AgentIpcClient::new(channel, DESKTOP_VERSION)?);
    let query = query::QueryService::new(channel)?;
    let controller = agent_controller::AgentController::new(channel, ipc.clone())?;
    let settings =
        settings_service::SettingsService::new(channel, None, controller.agent_exe().clone())?;
    Ok(AppServices {
        channel: channel.to_string(),
        ipc,
        query,
        settings,
        controller,
    })
}
