//! 托盘（09 §3.1：窗口、托盘、单实例为 Desktop 基本能力）。
//!
//! Agent 状态只在托盘/顶栏展示；不复制 Bridge 时代的 agent.* 命令语义。

use tauri::{
    App, AppHandle, Manager, Wry,
    menu::{Menu, MenuItem, PredefinedMenuItem},
    tray::{MouseButton, TrayIconBuilder, TrayIconEvent},
};
use tokio::time::{Duration, MissedTickBehavior, interval};
use wuji_core::domain::CaptureState;

use crate::commands::AppServices;

const TRAY_STATUS_ID: &str = "agent-status";
const TRAY_SHOW_ID: &str = "show-main-window";
const TRAY_EXIT_ID: &str = "exit-wuji";
const STATUS_REFRESH_INTERVAL: Duration = Duration::from_secs(5);

#[derive(Clone)]
struct TrayMenu {
    status: MenuItem<Wry>,
    show: MenuItem<Wry>,
}

fn show_main_window(app: &AppHandle) {
    if let Some(window) = app.get_webview_window("main") {
        let _ = window.show();
        let _ = window.set_focus();
    }
}

pub fn setup_tray(app: &App) -> tauri::Result<()> {
    let menu = TrayMenu {
        status: MenuItem::with_id(app, TRAY_STATUS_ID, "Agent：正在连接…", false, None::<&str>)?,
        show: MenuItem::with_id(app, TRAY_SHOW_ID, "显示吾迹", true, None::<&str>)?,
    };
    let exit = MenuItem::with_id(app, TRAY_EXIT_ID, "退出吾迹", true, None::<&str>)?;
    let separator1 = PredefinedMenuItem::separator(app)?;
    let separator2 = PredefinedMenuItem::separator(app)?;
    let tray_menu = Menu::with_items(
        app,
        &[&menu.status, &separator1, &menu.show, &separator2, &exit],
    )?;

    TrayIconBuilder::with_id("wuji-main")
        .menu(&tray_menu)
        .tooltip("吾迹 Rebuild v0.1（开发）")
        .show_menu_on_left_click(false)
        .on_menu_event(|app, event| match event.id().as_ref() {
            TRAY_SHOW_ID => show_main_window(app),
            TRAY_EXIT_ID => app.exit(0),
            _ => {}
        })
        .on_tray_icon_event(|tray, event| {
            if matches!(
                event,
                TrayIconEvent::DoubleClick {
                    button: MouseButton::Left,
                    ..
                }
            ) {
                show_main_window(tray.app_handle());
            }
        })
        .build(app)?;

    start_status_monitor(app.handle().clone(), menu);
    Ok(())
}

fn start_status_monitor(app: AppHandle, menu: TrayMenu) {
    tauri::async_runtime::spawn(async move {
        let mut refresh = interval(STATUS_REFRESH_INTERVAL);
        refresh.set_missed_tick_behavior(MissedTickBehavior::Skip);
        loop {
            refresh.tick().await;
            let services = app.state::<AppServices>();
            let label = match services.ipc.status().await {
                Ok(response) => {
                    let state = response["result"]["captureState"]
                        .as_str()
                        .unwrap_or_default();
                    connected_capture_label(state)
                }
                Err(_) => "Agent：未运行",
            };
            let _ = menu.status.set_text(label);
        }
    });
}

fn capture_state_label(state: CaptureState) -> &'static str {
    match state {
        CaptureState::Running => "Agent：运行中 · 正在记录",
        CaptureState::Paused => "Agent：运行中 · 记录已暂停",
        CaptureState::Stopped => "Agent：运行中 · 未记录",
    }
}

fn connected_capture_label(state: &str) -> &'static str {
    match state {
        "running" => capture_state_label(CaptureState::Running),
        "paused" => capture_state_label(CaptureState::Paused),
        "stopped" => capture_state_label(CaptureState::Stopped),
        _ => "Agent：运行中 · 状态未知",
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn tray_labels_distinguish_process_and_capture_state() {
        assert_eq!(
            connected_capture_label("running"),
            "Agent：运行中 · 正在记录"
        );
        assert_eq!(
            connected_capture_label("paused"),
            "Agent：运行中 · 记录已暂停"
        );
        assert_eq!(connected_capture_label("stopped"), "Agent：运行中 · 未记录");
    }
}
