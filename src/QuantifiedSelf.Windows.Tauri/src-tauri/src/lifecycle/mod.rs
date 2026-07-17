use std::sync::Mutex;

use serde::Serialize;
use tauri::{AppHandle, Emitter, Manager, Runtime, WebviewWindow, Window, WindowEvent};

use crate::bridge::BridgeSupervisor;

pub const HOST_LIFECYCLE_EVENT: &str = "host://lifecycle";
pub const HOST_CLOSE_REQUESTED_EVENT: &str = "host://close-requested";
const MAIN_WINDOW_LABEL: &str = "main";

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum HostLifecycleState {
    Visible,
    HiddenToTray,
    ExitConfirmationPending,
    ShuttingDown,
    Exited,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum CloseIntent {
    Hide,
    Exit,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum HostAction {
    None,
    Hide,
    Confirm(CloseIntent),
    Shutdown,
}

#[derive(Debug, Clone, Copy)]
struct LifecycleData {
    state: HostLifecycleState,
    has_unsaved_changes: bool,
    pending_intent: Option<CloseIntent>,
}

impl Default for LifecycleData {
    fn default() -> Self {
        Self {
            state: HostLifecycleState::Visible,
            has_unsaved_changes: false,
            pending_intent: None,
        }
    }
}

pub struct HostLifecycle {
    data: Mutex<LifecycleData>,
}

impl Default for HostLifecycle {
    fn default() -> Self {
        Self {
            data: Mutex::new(LifecycleData::default()),
        }
    }
}

impl HostLifecycle {
    pub fn state(&self) -> HostLifecycleState {
        self.lock().state
    }

    pub fn set_unsaved_changes(&self, value: bool) {
        self.lock().has_unsaved_changes = value;
    }

    fn request(&self, intent: CloseIntent) -> HostAction {
        let mut data = self.lock();
        if matches!(
            data.state,
            HostLifecycleState::ShuttingDown | HostLifecycleState::Exited
        ) {
            return HostAction::None;
        }
        if data.has_unsaved_changes {
            data.state = HostLifecycleState::ExitConfirmationPending;
            data.pending_intent = Some(intent);
            return HostAction::Confirm(intent);
        }

        data.pending_intent = None;
        match intent {
            CloseIntent::Hide => {
                data.state = HostLifecycleState::HiddenToTray;
                HostAction::Hide
            }
            CloseIntent::Exit => {
                data.state = HostLifecycleState::ShuttingDown;
                HostAction::Shutdown
            }
        }
    }

    fn request_minimize_to_tray(&self) -> HostAction {
        let mut data = self.lock();
        if matches!(
            data.state,
            HostLifecycleState::ShuttingDown | HostLifecycleState::Exited
        ) {
            return HostAction::None;
        }
        if data.state == HostLifecycleState::ExitConfirmationPending {
            // Hide the minimized window but preserve the active confirmation
            // and its intent so restoring from the tray can continue safely.
            return HostAction::Hide;
        }

        // Match the WPF dev default: minimizing hides the window without
        // discarding dirty Settings state or asking for close confirmation.
        data.state = HostLifecycleState::HiddenToTray;
        data.pending_intent = None;
        HostAction::Hide
    }

    fn confirm(&self, intent: CloseIntent) -> Result<HostAction, LifecycleCommandError> {
        let mut data = self.lock();
        if data.state != HostLifecycleState::ExitConfirmationPending
            || data.pending_intent != Some(intent)
        {
            return Err(LifecycleCommandError::invalid_transition());
        }
        data.has_unsaved_changes = false;
        data.pending_intent = None;
        Ok(match intent {
            CloseIntent::Hide => {
                data.state = HostLifecycleState::HiddenToTray;
                HostAction::Hide
            }
            CloseIntent::Exit => {
                data.state = HostLifecycleState::ShuttingDown;
                HostAction::Shutdown
            }
        })
    }

    pub fn cancel_close(&self) {
        let mut data = self.lock();
        if data.state == HostLifecycleState::ExitConfirmationPending {
            data.state = HostLifecycleState::Visible;
            data.pending_intent = None;
        }
    }

    fn mark_visible(&self) {
        let mut data = self.lock();
        if data.state == HostLifecycleState::HiddenToTray {
            data.state = HostLifecycleState::Visible;
        }
    }

    fn mark_exited(&self) {
        let mut data = self.lock();
        data.state = HostLifecycleState::Exited;
        data.pending_intent = None;
    }

    fn lock(&self) -> std::sync::MutexGuard<'_, LifecycleData> {
        self.data.lock().unwrap_or_else(|error| error.into_inner())
    }
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct LifecycleCommandError {
    code: &'static str,
    message: &'static str,
    retryable: bool,
}

impl LifecycleCommandError {
    fn invalid_transition() -> Self {
        Self {
            code: "invalid_lifecycle_transition",
            message: "当前窗口状态已变化，请重试。",
            retryable: true,
        }
    }

    fn window_unavailable() -> Self {
        Self {
            code: "window_unavailable",
            message: "主窗口暂时不可用，请从托盘重新打开。",
            retryable: true,
        }
    }
}

#[derive(Debug, Clone, Copy, Serialize)]
#[serde(rename_all = "camelCase")]
struct LifecycleEvent {
    state: HostLifecycleState,
}

#[derive(Debug, Clone, Copy, Serialize)]
#[serde(rename_all = "camelCase")]
struct CloseRequestedEvent {
    intent: CloseIntent,
}

pub fn handle_window_event<R: Runtime>(window: &Window<R>, event: &WindowEvent) {
    if window.label() != MAIN_WINDOW_LABEL {
        return;
    }
    if let WindowEvent::CloseRequested { api, .. } = event {
        api.prevent_close();
        let app = window.app_handle();
        let lifecycle = app.state::<HostLifecycle>();
        execute_action(app, lifecycle.request(CloseIntent::Hide));
    } else if matches!(event, WindowEvent::Resized(_)) && window.is_minimized().unwrap_or(false) {
        let app = window.app_handle();
        let lifecycle = app.state::<HostLifecycle>();
        execute_action(app, lifecycle.request_minimize_to_tray());
    } else if matches!(event, WindowEvent::Focused(true)) {
        let app = window.app_handle();
        let lifecycle = app.state::<HostLifecycle>();
        lifecycle.mark_visible();
        publish_lifecycle(app, lifecycle.state());
    }
}

pub fn set_unsaved_changes<R: Runtime>(app: &AppHandle<R>, value: bool) {
    app.state::<HostLifecycle>().set_unsaved_changes(value);
}

pub fn show_main_window<R: Runtime>(app: &AppHandle<R>) -> Result<(), LifecycleCommandError> {
    let window = main_window(app)?;
    window
        .show()
        .map_err(|_| LifecycleCommandError::window_unavailable())?;
    let _ = window.unminimize();
    let _ = window.set_focus();
    let lifecycle = app.state::<HostLifecycle>();
    lifecycle.mark_visible();
    publish_lifecycle(app, lifecycle.state());
    Ok(())
}

pub fn hide_main_window<R: Runtime>(app: &AppHandle<R>) -> Result<(), LifecycleCommandError> {
    let lifecycle = app.state::<HostLifecycle>();
    let action = if lifecycle.state() == HostLifecycleState::ExitConfirmationPending {
        lifecycle.confirm(CloseIntent::Hide)?
    } else {
        lifecycle.request(CloseIntent::Hide)
    };
    execute_action(app, action);
    Ok(())
}

pub fn request_exit<R: Runtime>(app: &AppHandle<R>) {
    let lifecycle = app.state::<HostLifecycle>();
    let action = if lifecycle.state() == HostLifecycleState::ExitConfirmationPending {
        lifecycle
            .confirm(CloseIntent::Exit)
            .unwrap_or(HostAction::None)
    } else {
        lifecycle.request(CloseIntent::Exit)
    };
    execute_action(app, action);
}

pub fn cancel_close<R: Runtime>(app: &AppHandle<R>) {
    let lifecycle = app.state::<HostLifecycle>();
    lifecycle.cancel_close();
    publish_lifecycle(app, lifecycle.state());
}

pub fn permits_exit<R: Runtime>(app: &AppHandle<R>) -> bool {
    matches!(
        app.state::<HostLifecycle>().state(),
        HostLifecycleState::ShuttingDown | HostLifecycleState::Exited
    )
}

fn execute_action<R: Runtime>(app: &AppHandle<R>, action: HostAction) {
    let lifecycle = app.state::<HostLifecycle>();
    match action {
        HostAction::None => {}
        HostAction::Hide => {
            if let Ok(window) = main_window(app) {
                let _ = window.hide();
            }
            publish_lifecycle(app, lifecycle.state());
        }
        HostAction::Confirm(intent) => {
            publish_lifecycle(app, lifecycle.state());
            let _ = app.emit(HOST_CLOSE_REQUESTED_EVENT, CloseRequestedEvent { intent });
        }
        HostAction::Shutdown => {
            publish_lifecycle(app, lifecycle.state());
            let app = app.clone();
            tauri::async_runtime::spawn(async move {
                // Host exit intentionally shuts down only the Bridge. Agent lifecycle is independent.
                let supervisor = app.state::<BridgeSupervisor>();
                let _ = supervisor.shutdown().await;
                let lifecycle = app.state::<HostLifecycle>();
                lifecycle.mark_exited();
                publish_lifecycle(&app, lifecycle.state());
                app.exit(0);
            });
        }
    }
}

fn main_window<R: Runtime>(app: &AppHandle<R>) -> Result<WebviewWindow<R>, LifecycleCommandError> {
    app.get_webview_window(MAIN_WINDOW_LABEL)
        .ok_or_else(LifecycleCommandError::window_unavailable)
}

fn publish_lifecycle<R: Runtime>(app: &AppHandle<R>, state: HostLifecycleState) {
    let _ = app.emit(HOST_LIFECYCLE_EVENT, LifecycleEvent { state });
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn clean_close_hides_without_entering_shutdown() {
        let lifecycle = HostLifecycle::default();

        assert_eq!(lifecycle.request(CloseIntent::Hide), HostAction::Hide);
        assert_eq!(lifecycle.state(), HostLifecycleState::HiddenToTray);
    }

    #[test]
    fn dirty_close_waits_for_explicit_hide_confirmation() {
        let lifecycle = HostLifecycle::default();
        lifecycle.set_unsaved_changes(true);

        assert_eq!(
            lifecycle.request(CloseIntent::Hide),
            HostAction::Confirm(CloseIntent::Hide)
        );
        assert_eq!(
            lifecycle.state(),
            HostLifecycleState::ExitConfirmationPending
        );
        assert_eq!(
            lifecycle.confirm(CloseIntent::Hide).unwrap(),
            HostAction::Hide
        );
        assert_eq!(lifecycle.state(), HostLifecycleState::HiddenToTray);
    }

    #[test]
    fn dirty_exit_can_be_cancelled_without_losing_dirty_state() {
        let lifecycle = HostLifecycle::default();
        lifecycle.set_unsaved_changes(true);
        assert_eq!(
            lifecycle.request(CloseIntent::Exit),
            HostAction::Confirm(CloseIntent::Exit)
        );

        lifecycle.cancel_close();

        assert_eq!(lifecycle.state(), HostLifecycleState::Visible);
        assert_eq!(
            lifecycle.request(CloseIntent::Exit),
            HostAction::Confirm(CloseIntent::Exit)
        );
    }

    #[test]
    fn confirmed_exit_enters_shutdown_and_is_idempotent() {
        let lifecycle = HostLifecycle::default();
        lifecycle.set_unsaved_changes(true);
        lifecycle.request(CloseIntent::Exit);

        assert_eq!(
            lifecycle.confirm(CloseIntent::Exit).unwrap(),
            HostAction::Shutdown
        );
        assert_eq!(lifecycle.state(), HostLifecycleState::ShuttingDown);
        assert_eq!(lifecycle.request(CloseIntent::Exit), HostAction::None);
    }

    #[test]
    fn wrong_confirmation_cannot_change_the_pending_intent() {
        let lifecycle = HostLifecycle::default();
        lifecycle.set_unsaved_changes(true);
        lifecycle.request(CloseIntent::Hide);

        assert!(lifecycle.confirm(CloseIntent::Exit).is_err());
        assert_eq!(
            lifecycle.state(),
            HostLifecycleState::ExitConfirmationPending
        );
    }

    #[test]
    fn minimize_to_tray_preserves_dirty_state_without_confirmation() {
        let lifecycle = HostLifecycle::default();
        lifecycle.set_unsaved_changes(true);

        assert_eq!(lifecycle.request_minimize_to_tray(), HostAction::Hide);
        assert_eq!(lifecycle.state(), HostLifecycleState::HiddenToTray);
        assert_eq!(
            lifecycle.request(CloseIntent::Exit),
            HostAction::Confirm(CloseIntent::Exit)
        );
    }

    #[test]
    fn minimize_does_not_bypass_an_active_close_confirmation() {
        let lifecycle = HostLifecycle::default();
        lifecycle.set_unsaved_changes(true);
        lifecycle.request(CloseIntent::Hide);

        assert_eq!(lifecycle.request_minimize_to_tray(), HostAction::Hide);
        assert_eq!(
            lifecycle.state(),
            HostLifecycleState::ExitConfirmationPending
        );
    }
}
