//! WUJI Rebuild v0.1 Tauri Desktop Host（09 §4、§8.3、§9.3）。
//!
//! 不含 Bridge/BridgeSupervisor：React → 本 Host → Rust Agent / SQLite（只读）。

pub mod agent_controller;
mod commands;
pub mod control;
pub mod desktop_prefs;
pub mod ipc;
pub mod paths;
pub mod query;
pub mod settings_service;
mod single_instance;
pub mod startup_registry;
mod stats_assembly;
mod tray;

use commands::{
    AppServices, activity_get_heatmap, activity_get_timeline, activity_get_today, agent_get_status,
    agent_process_stop, auto_start_status, capture_pause, capture_resume, capture_start,
    desktop_prefs_get, desktop_prefs_update, diagnostics_get_summary, settings_get,
    settings_resync_login_startup, settings_update, stats_get_home, stats_get_status,
};
use tauri::Manager as _;
use wuji_core::error::SafeError;

const DESKTOP_VERSION: &str = env!("CARGO_PKG_VERSION");
const PACKAGE_SMOKE_AUTOSTART_ENV: &str = "WUJI_REBUILD_PACKAGE_SMOKE_AUTOSTART";

fn package_smoke_autostart_enabled(channel: &str) -> bool {
    channel.starts_with("rebuild-v01-test-")
        && std::env::var(PACKAGE_SMOKE_AUTOSTART_ENV).as_deref() == Ok("1")
}

/// 启动仲裁（09 §9.3/§9.4）三态决策。
///
/// package-smoke 验收**优先**：新 test channel 无偏好文件（缺失默认开启），
/// 若按偏好走 `capture_ensure_recording` 会意外开始采集；smoke 只验证拉起链路，
/// 一律 `SmokeEnsureOnly`，与偏好无关。普通启动才按偏好决定是否自动开录。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum AutoStartDecision {
    /// package-smoke 验收：只 `ensure_running` 验证安装目录 Agent 拉起，不自动开录。
    SmokeEnsureOnly,
    /// 偏好开启：先确保 Agent 在线，再提交内部 ensure 命令自动开始记录。
    StartRecording,
    /// 偏好关闭的普通启动：不拉起、不记录。
    DoNothing,
}

fn decide_auto_start(desktop_pref: bool, package_smoke: bool) -> AutoStartDecision {
    if package_smoke {
        AutoStartDecision::SmokeEnsureOnly
    } else if desktop_pref {
        AutoStartDecision::StartRecording
    } else {
        AutoStartDecision::DoNothing
    }
}

/// 在创建异步任务前同步发布 starting。Tauri setup 返回后 WebView 即可查询
/// Host 状态，因此不能等 spawned future 首次被调度才设置，否则瞬态不确定。
fn prepare_auto_start_outcome(decision: AutoStartDecision, outcome: &commands::AutoStartOutcome) {
    if decision == AutoStartDecision::StartRecording {
        outcome.mark_starting();
    }
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
    // package-smoke 验收在隔离 test channel 显式启用时，验证安装目录固定
    // Agent 路径与 ensure_running 链路；与普通自动启动偏好合并为单一决策，
    // setup 只 spawn 一次（09 §9.3/§9.4）。
    let package_smoke = package_smoke_autostart_enabled(&channel);

    tauri::Builder::default()
        .setup(move |app| {
            app.manage(services);
            tray::setup_tray(app)?;

            // 启动吾迹时自动开始记录（默认开启，可在设置页关闭）：先确保
            // Agent 在线，再提交内部 capture_ensure_recording——Coordinator
            // 内原子判定 Stopped→开始 / Paused→恢复 / Running→幂等（09 §9.3）。
            // package-smoke 优先，只拉起不自动开录；普通启动按偏好决策。
            // 启动结果写入 AutoStartOutcome：顶栏显示“正在开始记录…”瞬态，
            // 失败显示可见提示，不伪装成功。
            let svc = app.state::<AppServices>();
            let decision = decide_auto_start(
                svc.desktop_prefs.should_auto_start_recording(),
                package_smoke,
            );
            if decision != AutoStartDecision::DoNothing {
                let control = svc.control.clone();
                let auto_start = svc.auto_start.clone();
                prepare_auto_start_outcome(decision, &auto_start);
                tauri::async_runtime::spawn(async move {
                    let result: Result<(), SafeError> = match decision {
                        AutoStartDecision::StartRecording => {
                            let result = control.ensure_recording().await;
                            match &result {
                                Ok(_) => auto_start.mark_recording(),
                                Err(error) => auto_start.mark_failed(error.clone()),
                            }
                            result.map(|_| ())
                        }
                        AutoStartDecision::SmokeEnsureOnly => {
                            control.ensure_running().await.map(|_| ())
                        }
                        AutoStartDecision::DoNothing => return,
                    };
                    if let Err(error) = result {
                        eprintln!("auto-start recording failed: {}", error.message);
                    }
                });
            }
            Ok(())
        })
        .on_window_event(|window, event| {
            use tauri::WindowEvent;
            match event {
                // 关闭窗口 → 隐藏到托盘，不退出应用。
                WindowEvent::CloseRequested { api, .. } => {
                    api.prevent_close();
                    let _ = window.hide();
                }
                // 最小化 → 隐藏到托盘，不在任务栏占位。
                // Tauri 2.x 没有 Minimized 事件，通过失去焦点 + is_minimized 检测。
                WindowEvent::Focused(false) if window.is_minimized().unwrap_or(false) => {
                    let _ = window.hide();
                }
                _ => {}
            }
            // 窗口状态变更后同步托盘菜单显示/隐藏互斥状态。
            if let Some(tray_state) = window
                .app_handle()
                .try_state::<crate::tray::TrayMenuState>()
            {
                tray_state.menu.sync_visibility(window.app_handle());
            }
        })
        .invoke_handler(tauri::generate_handler![
            agent_process_stop,
            agent_get_status,
            capture_start,
            capture_pause,
            capture_resume,
            activity_get_today,
            activity_get_timeline,
            activity_get_heatmap,
            settings_get,
            settings_update,
            settings_resync_login_startup,
            desktop_prefs_get,
            desktop_prefs_update,
            auto_start_status,
            diagnostics_get_summary,
            stats_get_home,
            stats_get_status,
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
    let desktop_prefs = desktop_prefs::DesktopPrefsService::new(channel)?;
    Ok(AppServices {
        channel: channel.to_string(),
        ipc: ipc.clone(),
        query,
        settings,
        desktop_prefs,
        control: control::ControlService::new(controller, ipc),
        auto_start: commands::AutoStartOutcome::default(),
    })
}

#[cfg(test)]
mod tests {
    use super::{decide_auto_start, package_smoke_autostart_enabled, prepare_auto_start_outcome};

    #[test]
    fn package_smoke_autostart_never_accepts_normal_channel() {
        // 无论调用环境是否设置 smoke 开关，正常 channel 都不能走自动拉起路径。
        assert!(!package_smoke_autostart_enabled("rebuild-v01-dev"));
    }

    #[test]
    fn package_smoke_requires_test_channel_and_env() {
        // Rust 2024 中 env 修改为 unsafe；测试进程内无并发读，短暂清除后恢复。
        unsafe {
            std::env::remove_var("WUJI_REBUILD_PACKAGE_SMOKE_AUTOSTART");
        }
        assert!(!package_smoke_autostart_enabled("rebuild-v01-test-abc"));
    }

    #[test]
    fn smoke_priority_only_ensures_running_never_records() {
        // smoke 验收优先：即使偏好缺失默认开启，也只拉起不自动开录。
        // （新 test channel 无偏好文件时 should_auto_start_recording 为 true，
        // 若按偏好走 capture_ensure_recording 会意外开始采集 —— 必须由枚举决策覆盖。）
        assert_eq!(
            decide_auto_start(true, true),
            super::AutoStartDecision::SmokeEnsureOnly
        );
        assert_eq!(
            decide_auto_start(false, true),
            super::AutoStartDecision::SmokeEnsureOnly
        );
    }

    #[test]
    fn ordinary_start_follows_desktop_pref() {
        assert_eq!(
            decide_auto_start(true, false),
            super::AutoStartDecision::StartRecording
        );
        assert_eq!(
            decide_auto_start(false, false),
            super::AutoStartDecision::DoNothing
        );
    }

    #[test]
    fn starting_is_published_synchronously_only_for_real_auto_recording() {
        for (decision, expected) in [
            (
                super::AutoStartDecision::StartRecording,
                crate::commands::AutoStartState::Starting,
            ),
            (
                super::AutoStartDecision::SmokeEnsureOnly,
                crate::commands::AutoStartState::Idle,
            ),
            (
                super::AutoStartDecision::DoNothing,
                crate::commands::AutoStartState::Idle,
            ),
        ] {
            let outcome = crate::commands::AutoStartOutcome::default();
            prepare_auto_start_outcome(decision, &outcome);
            assert_eq!(outcome.snapshot().state, expected, "{decision:?}");
        }
    }
}
