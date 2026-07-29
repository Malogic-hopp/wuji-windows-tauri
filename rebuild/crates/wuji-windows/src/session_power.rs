//! Windows session/power 事件泵（阶段 4.5：隐藏顶层窗口 + 自定义 WndProc +
//! 可停止的 pump handle）。
//!
//! 通过一个不显示的顶层窗口 + 自定义窗口过程接收：
//! - Lock/Unlock：WTSRegisterSessionNotification → WM_WTSSESSION_CHANGE
//! - Sleep/Resume：WM_POWERBROADCAST（sent message，直接在 WndProc 中处理）
//!
//! 提供 `SessionPowerPumpHandle` 支持可证明的正常关闭：
//! `request_stop()` → WM_APP_SHUTDOWN → DestroyWindow → PostQuitMessage
//! → 消息循环退出 → sender drop → 所有 receiver 感知关闭。

#[cfg(windows)]
use std::io;

/// 会话/电源事件。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SessionPowerEvent {
    Sleep,
    Resume,
    Lock,
    Unlock,
}

/// 可停止的事件泵 handle。
#[cfg(windows)]
pub struct SessionPowerPumpHandle {
    hwnd: isize,
    thread_id: u32,
    thread: Option<std::thread::JoinHandle<()>>,
    /// 泵线程退出信号（tokio oneshot）。线程退出前发送，调用方 await。
    exited_rx: Option<tokio::sync::oneshot::Receiver<()>>,
}

#[cfg(windows)]
impl SessionPowerPumpHandle {
    /// 请求停止泵：向隐藏窗口投递 WM_APP_SHUTDOWN。
    pub fn request_stop(&self) -> std::io::Result<()> {
        use windows_sys::Win32::Foundation::HWND;
        use windows_sys::Win32::UI::WindowsAndMessaging::PostMessageW;
        const WM_APP_SHUTDOWN: u32 = 0x8000;
        let ret = unsafe { PostMessageW(self.hwnd as HWND, WM_APP_SHUTDOWN, 0, 0) };
        if ret == 0 {
            Err(std::io::Error::last_os_error())
        } else {
            Ok(())
        }
    }

    /// `PostMessageW(hwnd, ...)` 失败或窗口已损坏时的有界兜底：直接向泵线程
    /// 投递 `WM_QUIT`。`GetMessageW` 即使带 HWND filter 也会接收 WM_QUIT。
    pub fn request_quit_fallback(&self) -> std::io::Result<()> {
        use windows_sys::Win32::UI::WindowsAndMessaging::PostThreadMessageW;
        const WM_QUIT: u32 = 0x0012;
        let ret = unsafe { PostThreadMessageW(self.thread_id, WM_QUIT, 0, 0) };
        if ret == 0 {
            Err(std::io::Error::last_os_error())
        } else {
            Ok(())
        }
    }

    /// 泵线程退出信号。借用而不转移所有权，timeout 后调用方仍可重试等待。
    pub fn exited_rx_mut(&mut self) -> Option<&mut tokio::sync::oneshot::Receiver<()>> {
        self.exited_rx.as_mut()
    }

    pub fn is_finished(&self) -> bool {
        self.thread
            .as_ref()
            .is_none_or(std::thread::JoinHandle::is_finished)
    }

    /// 只在已确认 finished 时回收线程；未结束时保留 JoinHandle，不 detach。
    pub fn join_if_finished(&mut self) -> std::io::Result<bool> {
        if !self.is_finished() {
            return Ok(false);
        }
        if let Some(handle) = self.thread.take() {
            handle
                .join()
                .map_err(|_| std::io::Error::other("session/power pump 线程 panic"))?;
        }
        Ok(true)
    }

    /// 启动装配失败时使用的同步、有界回滚。
    ///
    /// 正常 Agent shutdown 仍走异步三层状态机；本方法只解决“pump 已创建、但
    /// bridge 尚未登记”这一同步构造窗口。任何同步 join 都只在
    /// `is_finished()==true` 后执行。
    pub fn shutdown_bounded(&mut self, timeout: std::time::Duration) -> std::io::Result<()> {
        let mut fallback_used = false;
        let mut diagnostics = Vec::new();
        if let Err(error) = self.request_stop() {
            diagnostics.push(format!("PostMessageW stop 失败: {error}"));
            fallback_used = true;
            if let Err(fallback) = self.request_quit_fallback() {
                diagnostics.push(format!("PostThreadMessageW fallback 失败: {fallback}"));
            }
        }

        if self.wait_finished_until(std::time::Instant::now() + timeout)? {
            return self.join_if_finished().and_then(|joined| {
                if joined {
                    Ok(())
                } else {
                    Err(std::io::Error::other("pump 已退出但未能回收线程"))
                }
            });
        }

        if !fallback_used {
            fallback_used = true;
            if let Err(error) = self.request_quit_fallback() {
                diagnostics.push(format!("pump timeout 后 fallback 失败: {error}"));
            }
        }
        debug_assert!(fallback_used);

        if self.wait_finished_until(std::time::Instant::now() + timeout)? {
            return self.join_if_finished().and_then(|joined| {
                if joined {
                    Ok(())
                } else {
                    Err(std::io::Error::other("pump fallback 后未能回收线程"))
                }
            });
        }

        Err(std::io::Error::new(
            std::io::ErrorKind::TimedOut,
            format!(
                "pump 启动回滚超时{}",
                if diagnostics.is_empty() {
                    String::new()
                } else {
                    format!("：{}", diagnostics.join("；"))
                }
            ),
        ))
    }

    fn wait_finished_until(&mut self, deadline: std::time::Instant) -> std::io::Result<bool> {
        loop {
            if self.is_finished() {
                return Ok(true);
            }
            if let Some(exited) = self.exited_rx.as_mut() {
                match exited.try_recv() {
                    Ok(()) => {
                        // Receiver 已完成，后续只等待 JoinHandle 的 finished 状态。
                        self.exited_rx.take();
                    }
                    Err(tokio::sync::oneshot::error::TryRecvError::Closed) => {
                        self.exited_rx.take();
                    }
                    Err(tokio::sync::oneshot::error::TryRecvError::Empty) => {}
                }
            }
            if std::time::Instant::now() >= deadline {
                return Ok(false);
            }
            std::thread::yield_now();
        }
    }
}

#[cfg(windows)]
struct PumpExitSignal(Option<tokio::sync::oneshot::Sender<()>>);

#[cfg(windows)]
impl Drop for PumpExitSignal {
    fn drop(&mut self) {
        if let Some(tx) = self.0.take() {
            let _ = tx.send(());
        }
    }
}

/// 启动事件泵（隐藏顶层窗口 + 自定义 WndProc + 专用线程）。
/// 返回 `(events_rx, pump_handle)`：events_rx 供 bridge 线程读取，
/// pump_handle 提供 `request_stop()` 和 `join()`。
#[cfg(windows)]
pub fn start_event_pump() -> io::Result<(
    std::sync::mpsc::Receiver<SessionPowerEvent>,
    SessionPowerPumpHandle,
)> {
    use windows_sys::Win32::Foundation::{
        GetLastError, HWND, LPARAM, LRESULT, SetLastError, WPARAM,
    };
    use windows_sys::Win32::System::RemoteDesktop::{
        WTSRegisterSessionNotification, WTSUnRegisterSessionNotification,
    };
    use windows_sys::Win32::System::Threading::GetCurrentThreadId;
    use windows_sys::Win32::UI::WindowsAndMessaging::{
        CREATESTRUCTW, CW_USEDEFAULT, CreateWindowExW, DefWindowProcW, DestroyWindow,
        DispatchMessageW, GWLP_USERDATA, GetMessageW, GetWindowLongPtrW, MSG, PostQuitMessage,
        RegisterClassW, SetWindowLongPtrW, TranslateMessage, UnregisterClassW, WM_NCCREATE,
        WM_NCDESTROY, WNDCLASSW, WTS_SESSION_LOCK, WTS_SESSION_UNLOCK,
    };

    const WM_WTSSESSION_CHANGE: u32 = 0x02B1;
    const WM_POWERBROADCAST: u32 = 0x0218;
    const WM_APP_SHUTDOWN: u32 = 0x8000;
    const PBT_APMSUSPEND: usize = 0x0004;
    const PBT_APMRESUME_AUTOMATIC: usize = 0x0012;

    type Sender = std::sync::mpsc::Sender<SessionPowerEvent>;

    struct WindowContext {
        sender: Sender,
    }

    unsafe extern "system" fn wnd_proc(
        hwnd: HWND,
        msg: u32,
        wparam: WPARAM,
        lparam: LPARAM,
    ) -> LRESULT {
        unsafe {
            if msg == WM_NCCREATE {
                let create = lparam as *const CREATESTRUCTW;
                if create.is_null() {
                    return 0;
                }
                let context = (*create).lpCreateParams as isize;
                if context == 0 {
                    return 0;
                }
                SetLastError(0);
                let previous = SetWindowLongPtrW(hwnd, GWLP_USERDATA, context);
                if previous == 0 && GetLastError() != 0 {
                    return 0;
                }
            }

            if msg == WM_APP_SHUTDOWN {
                // request_stop：销毁窗口 → WM_DESTROY → 调用方应在 WM_DESTROY 中
                // PostQuitMessage。这里直接 PostQuitMessage 后 DestroyWindow。
                PostQuitMessage(0);
                DestroyWindow(hwnd);
                return 0;
            }

            if msg == WM_NCDESTROY {
                SetWindowLongPtrW(hwnd, GWLP_USERDATA, 0);
                return DefWindowProcW(hwnd, msg, wparam, lparam);
            }

            let event = match msg {
                WM_POWERBROADCAST => match wparam {
                    PBT_APMSUSPEND => Some(SessionPowerEvent::Sleep),
                    PBT_APMRESUME_AUTOMATIC => Some(SessionPowerEvent::Resume),
                    _ => None,
                },
                WM_WTSSESSION_CHANGE => match wparam {
                    x if x == WTS_SESSION_LOCK as usize => Some(SessionPowerEvent::Lock),
                    x if x == WTS_SESSION_UNLOCK as usize => Some(SessionPowerEvent::Unlock),
                    _ => None,
                },
                _ => None,
            };

            if let Some(event) = event {
                // 使用 GetWindowLongPtrW 读取上下文（SetWindowLongPtrW(..., 0) 会清空）。
                let ptr = GetWindowLongPtrW(hwnd, GWLP_USERDATA) as *const WindowContext;
                if !ptr.is_null() {
                    let ctx = &*ptr;
                    let _ = ctx.sender.send(event);
                }
            }

            DefWindowProcW(hwnd, msg, wparam, lparam)
        }
    }

    let (sender, receiver) = std::sync::mpsc::channel::<SessionPowerEvent>();
    let (ready_tx, ready_rx) = std::sync::mpsc::channel::<io::Result<(isize, u32)>>();
    let (exited_tx, exited_rx) = tokio::sync::oneshot::channel::<()>();

    let thread = std::thread::Builder::new()
        .name("wuji-session-power-pump".to_string())
        .spawn(move || {
            // 最先声明、最后析构：确保 WindowContext 和全部 Sender clone 已释放后，
            // 才向等待方发布 exited。
            let _exit_signal = PumpExitSignal(Some(exited_tx));
            let thread_id = unsafe { GetCurrentThreadId() };
            let class_name: Vec<u16> = "WUJI.SessionPowerPump"
                .encode_utf16()
                .chain(std::iter::once(0))
                .collect();

            let ctx = WindowContext {
                sender: sender.clone(),
            };

            unsafe {
                let mut class: WNDCLASSW = std::mem::zeroed();
                class.lpfnWndProc = Some(wnd_proc);
                class.lpszClassName = class_name.as_ptr();
                class.hInstance = std::ptr::null_mut();
                if RegisterClassW(&class) == 0 {
                    let _ = ready_tx.send(Err(io::Error::last_os_error()));
                    return;
                }

                let hwnd: HWND = CreateWindowExW(
                    0,
                    class_name.as_ptr(),
                    class_name.as_ptr(),
                    0,
                    CW_USEDEFAULT,
                    CW_USEDEFAULT,
                    CW_USEDEFAULT,
                    CW_USEDEFAULT,
                    std::ptr::null_mut(),
                    std::ptr::null_mut(),
                    std::ptr::null_mut(),
                    &ctx as *const WindowContext as *const std::ffi::c_void,
                );
                if hwnd.is_null() {
                    let error = io::Error::last_os_error();
                    let _ = UnregisterClassW(class_name.as_ptr(), std::ptr::null_mut());
                    let _ = ready_tx.send(Err(error));
                    return;
                }
                if WTSRegisterSessionNotification(hwnd, 0) == 0 {
                    let error = io::Error::last_os_error();
                    DestroyWindow(hwnd);
                    let _ = UnregisterClassW(class_name.as_ptr(), std::ptr::null_mut());
                    let _ = ready_tx.send(Err(error));
                    return;
                }
                let _ = ready_tx.send(Ok((hwnd as isize, thread_id)));

                let mut message: MSG = std::mem::zeroed();
                while GetMessageW(&mut message, hwnd, 0, 0) > 0 {
                    let _ = TranslateMessage(&message);
                    DispatchMessageW(&message);
                }

                // 消息循环退出 → 清理。
                let _ = WTSUnRegisterSessionNotification(hwnd);
                SetWindowLongPtrW(hwnd, GWLP_USERDATA, 0);
                let _ = DestroyWindow(hwnd);
                let _ = UnregisterClassW(class_name.as_ptr(), std::ptr::null_mut());
                drop(ctx);
                drop(sender);
            }
        })?;

    let (hwnd_isize, thread_id) = ready_rx
        .recv()
        .map_err(|_| io::Error::other("事件泵线程启动失败"))??;

    Ok((
        receiver,
        SessionPowerPumpHandle {
            hwnd: hwnd_isize,
            thread_id,
            thread: Some(thread),
            exited_rx: Some(exited_rx),
        },
    ))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    #[cfg(windows)]
    fn pump_starts_stops_and_joins_without_hanging() {
        let (_events_rx, mut pump) = start_event_pump().expect("事件泵必须能启动");
        pump.request_stop().expect("PostMessageW 必须成功");
        let deadline = std::time::Instant::now() + std::time::Duration::from_secs(2);
        let exited = loop {
            let result = pump.exited_rx_mut().expect("exited receiver").try_recv();
            match result {
                Ok(()) => break true,
                Err(tokio::sync::oneshot::error::TryRecvError::Closed) => {
                    panic!("pump exited signal 被取消")
                }
                Err(tokio::sync::oneshot::error::TryRecvError::Empty) => {
                    assert!(std::time::Instant::now() < deadline, "pump 退出超时");
                    std::thread::yield_now();
                }
            }
        };
        assert!(exited);
        while !pump.is_finished() {
            assert!(std::time::Instant::now() < deadline, "pump finished 超时");
            std::thread::yield_now();
        }
        assert!(pump.join_if_finished().expect("join pump"));
    }
}
