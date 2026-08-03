//! 托盘（09 §3.1：窗口、托盘、单实例为 Desktop 基本能力）。
//!
//! 菜单布局对齐旧 Win-ui WPF：Agent 状态 → 启动/暂停/恢复/停止 → 显示/隐藏 → 退出。
//! 控制操作与顶栏共用 `ControlService`（09 §8.3/§9.3）：解析 `ok=false`、
//! 等待停止终态、in-flight 互斥；托盘不复制 Bridge 时代的 agent.* 语义。
//! “停止 Agent”关闭 Agent 进程，与“暂停记录”（只暂停 Capture）语义分开。

use tauri::{
    App, AppHandle, Manager, Wry,
    menu::{Menu, MenuItem, PredefinedMenuItem},
    tray::{MouseButton, TrayIconBuilder, TrayIconEvent},
};
use tokio::time::{Duration, MissedTickBehavior, interval};
use wuji_core::domain::{CaptureState, ProcessState};
use wuji_core::dto::AgentStatusDto;

use crate::commands::{AppServices, AutoStartOutcome};

const TRAY_STATUS_ID: &str = "agent-status";
const TRAY_START_ID: &str = "agent-start";
const TRAY_PAUSE_ID: &str = "agent-pause";
const TRAY_RESUME_ID: &str = "agent-resume";
const TRAY_STOP_ID: &str = "agent-stop";
const TRAY_SHOW_ID: &str = "show-main-window";
const TRAY_HIDE_ID: &str = "hide-main-window";
const TRAY_EXIT_ID: &str = "exit-wuji";
const STATUS_REFRESH_INTERVAL: Duration = Duration::from_secs(5);

#[derive(Clone)]
pub(crate) struct TrayMenu {
    pub(crate) status: MenuItem<Wry>,
    pub(crate) start: MenuItem<Wry>,
    pub(crate) pause: MenuItem<Wry>,
    pub(crate) resume: MenuItem<Wry>,
    pub(crate) stop: MenuItem<Wry>,
    pub(crate) show: MenuItem<Wry>,
    pub(crate) hide: MenuItem<Wry>,
}

impl TrayMenu {
    /// 根据窗口可见状态互斥启用：可见时隐藏可用，隐藏时显示可用。
    pub(crate) fn sync_visibility(&self, app: &AppHandle) {
        let visible = app
            .get_webview_window("main")
            .map(|w| w.is_visible().unwrap_or(false))
            .unwrap_or(false);
        let (show_enabled, hide_enabled) = visibility_flags(visible);
        let _ = self.show.set_enabled(show_enabled);
        let _ = self.hide.set_enabled(hide_enabled);
    }

    /// 按完整 process+capture 视图更新菜单项文案与可用性。
    pub(crate) fn apply_status(&self, view: TrayStatusView) {
        let (label, start, pause, resume, stop) = view.into();
        let _ = self.status.set_text(label);
        let _ = self.start.set_enabled(start);
        let _ = self.pause.set_enabled(pause);
        let _ = self.resume.set_enabled(resume);
        let _ = self.stop.set_enabled(stop);
    }
}

/// 窗口可见性 → 显示/隐藏菜单项互斥（纯函数，供测试）。
fn visibility_flags(visible: bool) -> (bool, bool) {
    (!visible, visible)
}

/// 托盘菜单状态视图：由完整 AgentStatusDto 推导（纯函数，供测试与监视器共用）。
///
/// 进程与采集状态分开建模：Agent 在线但 Capture stopped 是“未记录”，
/// 不是“未启动”；停止 Agent（关闭进程）只在进程在线时可用。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) struct TrayStatusView {
    pub(crate) label: &'static str,
    pub(crate) start_enabled: bool,
    pub(crate) pause_enabled: bool,
    pub(crate) resume_enabled: bool,
    pub(crate) stop_enabled: bool,
}

impl From<TrayStatusView> for (&'static str, bool, bool, bool, bool) {
    fn from(view: TrayStatusView) -> Self {
        (
            view.label,
            view.start_enabled,
            view.pause_enabled,
            view.resume_enabled,
            view.stop_enabled,
        )
    }
}

/// 进程不在运行态时按状态分别建模（与顶栏同口径：非 `stopped` 视为在线异常，
/// 不能误判为“未运行”可重新启动）：故障态（Degraded/Faulted）仍可能 IPC
/// 可达，保留“停止 Agent”以便重置；瞬态（Starting/ShuttingDown）等待终态，
/// 不提供任何动作。
fn menu_view(status: Option<&AgentStatusDto>) -> TrayStatusView {
    let offline = TrayStatusView {
        label: "— Agent 未运行",
        start_enabled: true,
        pause_enabled: false,
        resume_enabled: false,
        stop_enabled: false,
    };
    let Some(status) = status else {
        return offline;
    };
    match status.process_state {
        ProcessState::Running => match status.capture_state {
            CaptureState::Running => TrayStatusView {
                label: "● 记录中",
                start_enabled: false,
                pause_enabled: true,
                resume_enabled: false,
                stop_enabled: true,
            },
            CaptureState::Paused => TrayStatusView {
                label: "◐ 已暂停",
                start_enabled: false,
                pause_enabled: false,
                resume_enabled: true,
                stop_enabled: true,
            },
            CaptureState::Stopped => TrayStatusView {
                label: "— 未记录",
                start_enabled: true,
                pause_enabled: false,
                resume_enabled: false,
                stop_enabled: true,
            },
        },
        ProcessState::Stopped => offline,
        // 瞬态：启动中/退出中，等待终态；不提供动作避免误操作。
        ProcessState::Starting => TrayStatusView {
            label: "… Agent 启动中",
            start_enabled: false,
            pause_enabled: false,
            resume_enabled: false,
            stop_enabled: false,
        },
        ProcessState::ShuttingDown => TrayStatusView {
            label: "… Agent 正在退出",
            start_enabled: false,
            pause_enabled: false,
            resume_enabled: false,
            stop_enabled: false,
        },
        // 故障态可能仍在线：显示异常并保留停止能力，不显示“未运行/可重启”。
        ProcessState::Degraded => TrayStatusView {
            label: "⚠ Agent 异常（降级）",
            start_enabled: false,
            pause_enabled: false,
            resume_enabled: false,
            stop_enabled: true,
        },
        ProcessState::Faulted => TrayStatusView {
            label: "⚠ Agent 异常（故障）",
            start_enabled: false,
            pause_enabled: false,
            resume_enabled: false,
            stop_enabled: true,
        },
    }
}

fn show_main_window(app: &AppHandle) {
    if let Some(window) = app.get_webview_window("main") {
        let _ = window.show();
        let _ = window.unminimize();
        let _ = window.set_focus();
    }
}

fn hide_main_window(app: &AppHandle) {
    if let Some(window) = app.get_webview_window("main") {
        let _ = window.hide();
    }
}

/// 托盘控制动作：全部走 ControlService 语义（ok=false 解析、停止终态等待、
/// in-flight 互斥），错误在 stderr 记录并保持菜单状态不变。
#[derive(Clone, Copy)]
enum TrayAction {
    Start,
    Pause,
    Resume,
    Stop,
}

fn reconcile_auto_start_after_tray_action(
    outcome: &AutoStartOutcome,
    action: TrayAction,
    status: &AgentStatusDto,
) {
    let expected = match action {
        TrayAction::Start | TrayAction::Resume => Some(CaptureState::Running),
        TrayAction::Pause => Some(CaptureState::Paused),
        TrayAction::Stop => None,
    };
    if let Some(expected) = expected {
        outcome.reconcile_manual_control(status, expected);
    }
}

async fn run_action(app: AppHandle, action: TrayAction) {
    let services = app.state::<AppServices>();
    let result = match action {
        TrayAction::Start => services.control.capture_start().await,
        TrayAction::Pause => services.control.capture_pause().await,
        TrayAction::Resume => services.control.capture_resume().await,
        TrayAction::Stop => services.control.stop_agent(&services.query).await,
    };
    match result {
        Ok(status) => reconcile_auto_start_after_tray_action(&services.auto_start, action, &status),
        Err(error) => eprintln!("[tray] 控制操作失败: {}", error.message),
    }
}

pub fn setup_tray(app: &App) -> tauri::Result<()> {
    // 手工创建的 TrayIconBuilder 不会自动继承 bundle/default window icon。
    // 显式复用 Tauri 在 dev 与 package 构建时嵌入的同一份图标，避免 Windows
    // 托盘区域只出现透明占位。
    let icon = app
        .default_window_icon()
        .cloned()
        .ok_or_else(|| tauri::Error::AssetNotFound("icons/icon.ico".to_owned()))?;

    let menu = TrayMenu {
        status: MenuItem::with_id(app, TRAY_STATUS_ID, "— Agent 未运行", false, None::<&str>)?,
        start: MenuItem::with_id(app, TRAY_START_ID, "启动记录", true, None::<&str>)?,
        pause: MenuItem::with_id(app, TRAY_PAUSE_ID, "暂停记录", false, None::<&str>)?,
        resume: MenuItem::with_id(app, TRAY_RESUME_ID, "继续记录", false, None::<&str>)?,
        stop: MenuItem::with_id(app, TRAY_STOP_ID, "停止 Agent", false, None::<&str>)?,
        show: MenuItem::with_id(app, TRAY_SHOW_ID, "显示吾迹", true, None::<&str>)?,
        hide: MenuItem::with_id(app, TRAY_HIDE_ID, "隐藏吾迹", true, None::<&str>)?,
    };
    let exit = MenuItem::with_id(app, TRAY_EXIT_ID, "退出吾迹", true, None::<&str>)?;
    let sep1 = PredefinedMenuItem::separator(app)?;
    let sep2 = PredefinedMenuItem::separator(app)?;
    let sep3 = PredefinedMenuItem::separator(app)?;
    let tray_menu = Menu::with_items(
        app,
        &[
            &menu.status,
            &sep1,
            &menu.start,
            &menu.pause,
            &menu.resume,
            &menu.stop,
            &sep2,
            &menu.show,
            &menu.hide,
            &sep3,
            &exit,
        ],
    )?;

    let app_handle = app.handle().clone();
    let menu_clone = menu.clone();
    let menu_for_events = menu.clone();

    TrayIconBuilder::with_id("wuji-main")
        .icon(icon)
        .menu(&tray_menu)
        .tooltip("吾迹 Rebuild v0.1（开发）")
        .show_menu_on_left_click(false)
        .on_menu_event(move |app, event| {
            let id = event.id().as_ref();
            match id {
                TRAY_START_ID => {
                    tauri::async_runtime::spawn(run_action(app.clone(), TrayAction::Start));
                }
                TRAY_PAUSE_ID => {
                    tauri::async_runtime::spawn(run_action(app.clone(), TrayAction::Pause));
                }
                TRAY_RESUME_ID => {
                    tauri::async_runtime::spawn(run_action(app.clone(), TrayAction::Resume));
                }
                TRAY_STOP_ID => {
                    tauri::async_runtime::spawn(run_action(app.clone(), TrayAction::Stop));
                }
                TRAY_SHOW_ID => {
                    show_main_window(app);
                    menu_for_events.sync_visibility(app);
                }
                TRAY_HIDE_ID => {
                    hide_main_window(app);
                    menu_for_events.sync_visibility(app);
                }
                TRAY_EXIT_ID => app.exit(0),
                _ => {}
            }
        })
        .on_tray_icon_event(|tray, event| {
            if matches!(
                event,
                TrayIconEvent::Click {
                    button: MouseButton::Left,
                    ..
                }
            ) {
                let app = tray.app_handle();
                show_main_window(app);
                if let Some(saved) = app.try_state::<TrayMenuState>() {
                    saved.menu.sync_visibility(app);
                }
            }
        })
        .build(app)?;

    menu_clone.sync_visibility(&app_handle);
    app_handle.manage(TrayMenuState { menu: menu_clone });

    start_status_monitor(app_handle.clone(), menu);
    Ok(())
}

/// 托管到 Tauri state，供托盘事件闭包（on_tray_icon_event）内取回 menu 引用做同步。
/// lib.rs 的窗口事件处理也需要访问此状态以同步显示/隐藏互斥。
pub(crate) struct TrayMenuState {
    pub(crate) menu: TrayMenu,
}

fn start_status_monitor(app: AppHandle, menu: TrayMenu) {
    tauri::async_runtime::spawn(async move {
        let mut refresh = interval(STATUS_REFRESH_INTERVAL);
        refresh.set_missed_tick_behavior(MissedTickBehavior::Delay);
        loop {
            refresh.tick().await;
            let services = app.state::<AppServices>();
            // status 失败（Agent 离线）→ None，按“未运行”展示；
            // ok 但字段缺失的情况由 DTO 反序列化失败同样落为 None。
            let status = services.control.status().await.ok();
            menu.apply_status(menu_view(status.as_ref()));
        }
    });
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    fn dto(process: &str, capture: &str) -> AgentStatusDto {
        serde_json::from_value(json!({
            "agentVersion": "0.1.0",
            "protocolVersion": 1,
            "schemaVersion": 1,
            "processState": process,
            "captureState": capture,
            "writerState": "healthy",
            "runtimeId": "01JX0000000000000000000000",
            "heartbeatAtUtcMs": null,
            "lastObservationAtUtcMs": null,
            "lastWriteAtUtcMs": null,
            "captureQueueDepth": 0,
            "writerQueueDepth": 0,
            "droppedCaptureCount": "0",
            "droppedWriterCount": "0",
            "safeErrorCode": null,
        }))
        .expect("合法 AgentStatusDto")
    }

    #[test]
    fn offline_is_not_confused_with_online_but_stopped() {
        // 无状态（IPC 失败）：只有启动可用，停止 Agent 不可用。
        let view = menu_view(None);
        assert_eq!(view.label, "— Agent 未运行");
        assert!(view.start_enabled && !view.stop_enabled);

        // Agent 在线但 Capture stopped：允许“启动记录”和“停止 Agent”，
        // 不等于“Agent 未运行”。
        let view = menu_view(Some(&dto("running", "stopped")));
        assert_eq!(view.label, "— 未记录");
        assert!(view.start_enabled && view.stop_enabled);
    }

    #[test]
    fn running_paused_views_enable_only_relevant_actions() {
        let running = menu_view(Some(&dto("running", "running")));
        assert_eq!(running.label, "● 记录中");
        assert!(running.pause_enabled && running.stop_enabled);
        assert!(!running.start_enabled && !running.resume_enabled);

        let paused = menu_view(Some(&dto("running", "paused")));
        assert_eq!(paused.label, "◐ 已暂停");
        assert!(paused.resume_enabled && paused.stop_enabled);
        assert!(!paused.start_enabled && !paused.pause_enabled);
    }

    #[test]
    fn stopped_process_state_is_offline_and_restartable() {
        // 进程终态 stopped（或 IPC 失败无状态）：只有“启动记录”可用。
        let view = menu_view(Some(&dto("stopped", "running")));
        assert_eq!(view.label, "— Agent 未运行");
        assert!(view.start_enabled && !view.stop_enabled);
    }

    #[test]
    fn transient_states_offer_no_actions() {
        // Starting/ShuttingDown 是瞬态：等待终态，任何动作都禁用，
        // 不允许“启动记录”造成双开或误重启。
        for (process, capture, label) in [
            ("starting", "paused", "… Agent 启动中"),
            ("shutting_down", "running", "… Agent 正在退出"),
        ] {
            let view = menu_view(Some(&dto(process, capture)));
            assert_eq!(view.label, label, "{process}/{capture}");
            assert!(
                !(view.start_enabled
                    || view.pause_enabled
                    || view.resume_enabled
                    || view.stop_enabled),
                "{process}/{capture}"
            );
        }
    }

    #[test]
    fn fault_states_show_abnormal_and_keep_stop() {
        // Degraded/Faulted 仍可能 IPC 可达（与顶栏同口径在线异常）：
        // 显示异常并保留“停止 Agent”用于重置，不显示“未运行/可重启”。
        let degraded = menu_view(Some(&dto("degraded", "running")));
        assert_eq!(degraded.label, "⚠ Agent 异常（降级）");
        assert!(degraded.stop_enabled && !degraded.start_enabled);

        let faulted = menu_view(Some(&dto("faulted", "stopped")));
        assert_eq!(faulted.label, "⚠ Agent 异常（故障）");
        assert!(faulted.stop_enabled && !faulted.start_enabled);
    }

    #[test]
    fn visibility_flags_are_mutually_exclusive() {
        assert_eq!(visibility_flags(true), (false, true));
        assert_eq!(visibility_flags(false), (true, false));
    }

    #[test]
    fn tray_manual_retry_reconciles_auto_start_without_hiding_permanent_fault() {
        let outcome = AutoStartOutcome::default();
        outcome.mark_failed(wuji_core::error::SafeError::new(
            wuji_core::error::SafeErrorCode::AgentWriterFaulted,
            "自动开始记录失败",
        ));
        reconcile_auto_start_after_tray_action(
            &outcome,
            TrayAction::Start,
            &dto("running", "running"),
        );
        assert_eq!(
            outcome.snapshot().state,
            crate::commands::AutoStartState::Idle,
            "托盘成功启动后必须清除旧失败"
        );

        outcome.mark_failed(wuji_core::error::SafeError::new(
            wuji_core::error::SafeErrorCode::InternalSafeError,
            "事件监视已永久失效，无法开始采集",
        ));
        let mut permanently_paused = dto("running", "paused");
        permanently_paused.safe_error_code =
            Some(wuji_core::error::SafeErrorCode::InternalSafeError);
        reconcile_auto_start_after_tray_action(&outcome, TrayAction::Resume, &permanently_paused);
        assert_eq!(
            outcome.snapshot().state,
            crate::commands::AutoStartState::Failed,
            "永久故障返回 Ok(Paused) 时不得清除失败"
        );
    }
}
