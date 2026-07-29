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
const PACKAGE_SMOKE_AUTOSTART_ENV: &str = "WUJI_REBUILD_PACKAGE_SMOKE_AUTOSTART";

fn package_smoke_autostart_enabled(channel: &str) -> bool {
    channel.starts_with("rebuild-v01-test-")
        && std::env::var(PACKAGE_SMOKE_AUTOSTART_ENV).as_deref() == Ok("1")
}

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
    // 普通 Desktop 启动不拉起 Agent；只有 package 验收在隔离 test channel
    // 显式启用此钩子，以验证安装目录固定 Agent 路径与 ensure_running 链路。
    let package_smoke_controller =
        package_smoke_autostart_enabled(&channel).then(|| services.controller.clone());

    tauri::Builder::default()
        .setup(move |app| {
            app.manage(services);
            tray::setup_tray(app)?;
            if let Some(controller) = package_smoke_controller {
                tauri::async_runtime::spawn(async move {
                    if let Err(error) = controller.ensure_running().await {
                        eprintln!("安装包启动烟测无法拉起 Agent: {}", error.message);
                    }
                });
            }
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

#[cfg(test)]
mod tests {
    use super::package_smoke_autostart_enabled;

    #[test]
    fn package_smoke_autostart_never_accepts_normal_channel() {
        // 无论调用环境是否设置 smoke 开关，正常 channel 都不能走自动拉起路径。
        assert!(!package_smoke_autostart_enabled("rebuild-v01-dev"));
    }
}
