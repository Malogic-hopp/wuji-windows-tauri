use std::sync::{
    Arc,
    atomic::{AtomicBool, Ordering},
};

use tauri::{
    App, AppHandle, Listener, Manager, Wry,
    menu::{Menu, MenuItem, PredefinedMenuItem},
    tray::{MouseButton, TrayIconBuilder, TrayIconEvent},
};
use tokio::time::{Duration, MissedTickBehavior, interval};

use crate::{
    bridge::BridgeSupervisor,
    contracts::{AgentState, AgentStatus, CommandResult},
    lifecycle::{HOST_LIFECYCLE_EVENT, hide_main_window, request_exit, show_main_window},
};

const TRAY_STATUS_ID: &str = "agent-status";
const TRAY_AGENT_START_ID: &str = "agent-start";
const TRAY_AGENT_PAUSE_ID: &str = "agent-pause";
const TRAY_AGENT_RESUME_ID: &str = "agent-resume";
const TRAY_AGENT_STOP_ID: &str = "agent-stop";
const TRAY_SHOW_ID: &str = "show-main-window";
const TRAY_HIDE_ID: &str = "hide-main-window";
const TRAY_EXIT_ID: &str = "exit-wuji";
const STATUS_REFRESH_INTERVAL: Duration = Duration::from_secs(4);

#[derive(Clone)]
struct AgentTrayMenu {
    status: MenuItem<Wry>,
    start: MenuItem<Wry>,
    pause: MenuItem<Wry>,
    resume: MenuItem<Wry>,
    stop: MenuItem<Wry>,
    busy: Arc<AtomicBool>,
}

impl AgentTrayMenu {
    fn apply_status(&self, status: &AgentStatus) {
        let presentation = present_agent_state(&status.actual_state, status.is_running);
        let _ = self.status.set_text(presentation.label);
        let _ = self.start.set_enabled(presentation.can_start);
        let _ = self.pause.set_enabled(presentation.can_pause);
        let _ = self.resume.set_enabled(presentation.can_resume);
        let _ = self.stop.set_enabled(presentation.can_stop);
    }

    fn set_connecting(&self) {
        let _ = self.status.set_text("Agent：正在连接…");
        self.disable_actions();
    }

    fn set_unavailable(&self) {
        let _ = self.status.set_text("Agent：服务不可用");
        self.disable_actions();
    }

    fn set_busy(&self) {
        let _ = self.status.set_text("Agent：正在处理…");
        self.disable_actions();
    }

    fn disable_actions(&self) {
        let _ = self.start.set_enabled(false);
        let _ = self.pause.set_enabled(false);
        let _ = self.resume.set_enabled(false);
        let _ = self.stop.set_enabled(false);
    }
}

#[derive(Clone)]
struct WindowTrayMenu {
    show: MenuItem<Wry>,
    hide: MenuItem<Wry>,
}

impl WindowTrayMenu {
    fn sync(&self, app: &AppHandle) {
        let visible = app
            .get_webview_window("main")
            .and_then(|window| window.is_visible().ok())
            .unwrap_or(false);
        let _ = self.show.set_enabled(!visible);
        let _ = self.hide.set_enabled(visible);
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
struct AgentMenuPresentation {
    label: &'static str,
    can_start: bool,
    can_pause: bool,
    can_resume: bool,
    can_stop: bool,
}

pub fn setup_tray(app: &App) -> tauri::Result<()> {
    let agent_menu = AgentTrayMenu {
        status: MenuItem::with_id(app, TRAY_STATUS_ID, "Agent：正在连接…", false, None::<&str>)?,
        start: MenuItem::with_id(app, TRAY_AGENT_START_ID, "启动记录", false, None::<&str>)?,
        pause: MenuItem::with_id(app, TRAY_AGENT_PAUSE_ID, "暂停记录", false, None::<&str>)?,
        resume: MenuItem::with_id(app, TRAY_AGENT_RESUME_ID, "继续记录", false, None::<&str>)?,
        stop: MenuItem::with_id(app, TRAY_AGENT_STOP_ID, "停止记录", false, None::<&str>)?,
        busy: Arc::new(AtomicBool::new(false)),
    };
    let window_menu = WindowTrayMenu {
        show: MenuItem::with_id(app, TRAY_SHOW_ID, "显示吾迹", false, None::<&str>)?,
        hide: MenuItem::with_id(app, TRAY_HIDE_ID, "隐藏吾迹", true, None::<&str>)?,
    };
    let exit = MenuItem::with_id(app, TRAY_EXIT_ID, "退出吾迹", true, None::<&str>)?;
    let agent_separator = PredefinedMenuItem::separator(app)?;
    let window_separator = PredefinedMenuItem::separator(app)?;
    let exit_separator = PredefinedMenuItem::separator(app)?;
    let menu = Menu::with_items(
        app,
        &[
            &agent_menu.status,
            &agent_separator,
            &agent_menu.start,
            &agent_menu.pause,
            &agent_menu.resume,
            &agent_menu.stop,
            &window_separator,
            &window_menu.show,
            &window_menu.hide,
            &exit_separator,
            &exit,
        ],
    )?;

    let event_agent_menu = agent_menu.clone();
    let event_window_menu = window_menu.clone();
    let mut tray = TrayIconBuilder::with_id("wuji-main")
        .menu(&menu)
        .tooltip("吾迹 · 开发预览")
        .show_menu_on_left_click(false)
        .on_menu_event(move |app, event| match event.id().as_ref() {
            TRAY_AGENT_START_ID => run_agent_command(app, event_agent_menu.clone(), "agent.start"),
            TRAY_AGENT_PAUSE_ID => run_agent_command(app, event_agent_menu.clone(), "agent.pause"),
            TRAY_AGENT_RESUME_ID => {
                run_agent_command(app, event_agent_menu.clone(), "agent.resume")
            }
            TRAY_AGENT_STOP_ID => run_agent_command(app, event_agent_menu.clone(), "agent.stop"),
            TRAY_SHOW_ID => {
                let _ = show_main_window(app);
                event_window_menu.sync(app);
            }
            TRAY_HIDE_ID => {
                let _ = hide_main_window(app);
                event_window_menu.sync(app);
            }
            TRAY_EXIT_ID => request_exit(app),
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
                let _ = show_main_window(tray.app_handle());
            }
        });
    if let Some(icon) = app.default_window_icon().cloned() {
        tray = tray.icon(icon);
    }
    tray.build(app)?;

    let lifecycle_window_menu = window_menu.clone();
    let lifecycle_app = app.handle().clone();
    app.listen(HOST_LIFECYCLE_EVENT, move |_| {
        lifecycle_window_menu.sync(&lifecycle_app);
    });
    window_menu.sync(app.handle());
    start_status_monitor(app.handle().clone(), agent_menu);
    Ok(())
}

fn start_status_monitor(app: AppHandle, menu: AgentTrayMenu) {
    menu.set_connecting();
    tauri::async_runtime::spawn(async move {
        let mut refresh = interval(STATUS_REFRESH_INTERVAL);
        refresh.set_missed_tick_behavior(MissedTickBehavior::Skip);
        loop {
            refresh.tick().await;
            if menu.busy.load(Ordering::SeqCst) {
                continue;
            }
            refresh_agent_status(&app, &menu).await;
        }
    });
}

fn run_agent_command(app: &AppHandle, menu: AgentTrayMenu, method: &'static str) {
    if menu.busy.swap(true, Ordering::SeqCst) {
        return;
    }
    menu.set_busy();
    let app = app.clone();
    tauri::async_runtime::spawn(async move {
        let supervisor = app.state::<BridgeSupervisor>();
        let _ = supervisor.request::<CommandResult>(method).await;
        menu.busy.store(false, Ordering::SeqCst);
        refresh_agent_status(&app, &menu).await;
    });
}

async fn refresh_agent_status(app: &AppHandle, menu: &AgentTrayMenu) {
    let supervisor = app.state::<BridgeSupervisor>();
    match supervisor.request::<AgentStatus>("agent.getStatus").await {
        Ok(status) => menu.apply_status(&status),
        Err(_) => menu.set_unavailable(),
    }
}

fn present_agent_state(state: &AgentState, is_running: bool) -> AgentMenuPresentation {
    let mut presentation = AgentMenuPresentation {
        label: "Agent：状态未知",
        can_start: false,
        can_pause: false,
        can_resume: false,
        can_stop: false,
    };
    match state {
        AgentState::NotRunning | AgentState::Stopped => {
            presentation.label = "Agent：未运行";
            presentation.can_start = true;
        }
        AgentState::Starting => presentation.label = "Agent：正在启动",
        AgentState::Running => {
            presentation.label = "Agent：正在记录";
            presentation.can_pause = true;
            presentation.can_stop = true;
        }
        AgentState::Pausing => presentation.label = "Agent：正在暂停",
        AgentState::Paused => {
            presentation.label = "Agent：已暂停";
            presentation.can_resume = true;
            presentation.can_stop = true;
        }
        AgentState::Resuming => presentation.label = "Agent：正在恢复",
        AgentState::Stopping => presentation.label = "Agent：正在停止",
        AgentState::Stale => {
            presentation.label = "Agent：状态过期";
            presentation.can_stop = is_running;
        }
        AgentState::Error => {
            presentation.label = "Agent：服务异常";
            presentation.can_stop = is_running;
        }
        AgentState::Maintenance => presentation.label = "Agent：维护中",
    }
    presentation
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn cold_start_states_have_distinct_labels_and_actions() {
        let not_running = present_agent_state(&AgentState::NotRunning, false);
        assert_eq!(not_running.label, "Agent：未运行");
        assert!(not_running.can_start);
        assert!(!not_running.can_stop);

        let running = present_agent_state(&AgentState::Running, true);
        assert_eq!(running.label, "Agent：正在记录");
        assert!(running.can_pause);
        assert!(running.can_stop);

        let paused = present_agent_state(&AgentState::Paused, true);
        assert_eq!(paused.label, "Agent：已暂停");
        assert!(paused.can_resume);
        assert!(paused.can_stop);

        let stale = present_agent_state(&AgentState::Stale, true);
        assert_eq!(stale.label, "Agent：状态过期");
        assert!(stale.can_stop);
        assert!(!stale.can_start);
    }

    #[test]
    fn stale_status_never_enables_stop_for_a_missing_process() {
        let stale = present_agent_state(&AgentState::Stale, false);
        assert!(!stale.can_stop);
    }
}
