//! 阶段 4.5：session/power 事件消费者与受监督的桥接。
//!
//! 三层监督：
//! - pump（OS 窗口线程）：SessionPowerPumpHandle 持有 JoinHandle
//! - bridge（std thread）：SessionPowerBridge 持有 JoinHandle，shutdown 时 join
//! - consumer（tokio task）：supervisor task 持有 JoinHandle，panic 时 latch monitor_fault；
//!   shutdown 通过 AbortHandle 兜底

use std::sync::Arc;
use std::time::Duration;
#[cfg(windows)]
use std::{future::Future, pin::Pin};
use tokio::sync::{mpsc, watch};

use crate::capture_coordinator::{CaptureCoordinator, SystemLifecycleEvent};
use crate::capture_loop::now_utc_ms;

pub async fn run_session_power_events(
    mut bridge_rx: mpsc::Receiver<wuji_windows::SessionPowerEvent>,
    coordinator: Arc<CaptureCoordinator>,
    mut shutdown_rx: watch::Receiver<bool>,
) {
    loop {
        tokio::select! {
            event = bridge_rx.recv() => {
                match event {
                    Some(raw) => {
                        let at = now_utc_ms();
                        let sys_event = map_session_power_event(raw, at);
                        if let Err(error) = coordinator
                            .apply_system_lifecycle_event(sys_event)
                            .await
                            && error.code
                                != wuji_core::error::SafeErrorCode::AgentWriterFaulted
                            {
                                eprintln!("session/power 事件处理失败: {error}");
                            }
                    }
                    None => {
                        if *shutdown_rx.borrow() {
                            // 正常 shutdown：pump 已 stop，bridge channel 关闭
                        } else {
                            coordinator.latch_monitor_fault();
                            eprintln!("session/power 通道意外关闭（非 shutdown），已锁存 monitor fault");
                        }
                        return;
                    }
                }
            }
            _ = shutdown_rx.changed() => {}
        }
    }
}

/// 持有 session/power consumer 的任务身份并统一解释退出原因。
///
/// panic 表示生产监听链路已永久失效，必须锁存 monitor fault；shutdown 主动
/// abort 产生的 cancelled 则由 `SessionPowerBridge::shutdown` 负责确认，不重复
/// 记为运行时故障。公开这个窄 helper 是为了让集成测试覆盖生产 supervisor，
/// 而不是直接调用 `latch_monitor_fault()` 模拟结果。
#[doc(hidden)]
pub async fn supervise_session_power_consumer(
    consumer: tokio::task::JoinHandle<()>,
    coordinator: Arc<CaptureCoordinator>,
) {
    match consumer.await {
        Ok(()) => {}
        Err(error) => {
            if error.is_panic() {
                coordinator.latch_monitor_fault();
                eprintln!("session/power consumer panic，已锁存 monitor fault");
            }
            // cancelled（被 shutdown abort）不视为运行时监听故障。
        }
    }
}

/// 生产与测试共用的 session/power 转发 helper。
/// 从 OS 事件泵（std channel）读取 SessionPowerEvent，经 blocking_send 转发到
/// bridge tokio channel。生产 bridge std 线程与 L18 背压测试共用本函数。
pub fn run_session_power_forward(
    events_rx: std::sync::mpsc::Receiver<wuji_windows::SessionPowerEvent>,
    bridge_tx: mpsc::Sender<wuji_windows::SessionPowerEvent>,
) {
    run_session_power_forward_inner(events_rx, bridge_tx, |_| {});
}

fn run_session_power_forward_inner(
    events_rx: std::sync::mpsc::Receiver<wuji_windows::SessionPowerEvent>,
    bridge_tx: mpsc::Sender<wuji_windows::SessionPowerEvent>,
    mut before_send: impl FnMut(wuji_windows::SessionPowerEvent),
) {
    while let Ok(event) = events_rx.recv() {
        before_send(event);
        if bridge_tx.blocking_send(event).is_err() {
            break;
        }
    }
}

/// 集成测试 rendezvous：与生产转发共用同一循环，只在每次 blocking_send 前报告
/// Attempt(event)，用于确定性证明容量满时确实阻塞。
#[doc(hidden)]
pub fn run_session_power_forward_observed(
    events_rx: std::sync::mpsc::Receiver<wuji_windows::SessionPowerEvent>,
    bridge_tx: mpsc::Sender<wuji_windows::SessionPowerEvent>,
    attempt_tx: std::sync::mpsc::SyncSender<wuji_windows::SessionPowerEvent>,
) {
    run_session_power_forward_inner(events_rx, bridge_tx, move |event| {
        let _ = attempt_tx.send(event);
    });
}

pub fn map_session_power_event(
    raw: wuji_windows::SessionPowerEvent,
    at_utc_ms: i64,
) -> SystemLifecycleEvent {
    match raw {
        wuji_windows::SessionPowerEvent::Lock => SystemLifecycleEvent::Lock { at_utc_ms },
        wuji_windows::SessionPowerEvent::Unlock => SystemLifecycleEvent::Unlock { at_utc_ms },
        wuji_windows::SessionPowerEvent::Sleep => SystemLifecycleEvent::Sleep { at_utc_ms },
        wuji_windows::SessionPowerEvent::Resume => SystemLifecycleEvent::Resume { at_utc_ms },
    }
}

const SHUTDOWN_JOIN_TIMEOUT: Duration = Duration::from_secs(5);
const CONSUMER_DRAIN_TIMEOUT: Duration = Duration::from_secs(3);
const ABORT_CONFIRM_TIMEOUT: Duration = Duration::from_secs(1);
const THREAD_FINISH_TIMEOUT: Duration = Duration::from_secs(1);

#[cfg(windows)]
#[derive(Clone, Copy)]
struct ShutdownTimeouts {
    consumer_drain: Duration,
    abort_confirm: Duration,
    component_exit: Duration,
    thread_finish: Duration,
}

#[cfg(windows)]
impl ShutdownTimeouts {
    const PRODUCTION: Self = Self {
        consumer_drain: CONSUMER_DRAIN_TIMEOUT,
        abort_confirm: ABORT_CONFIRM_TIMEOUT,
        component_exit: SHUTDOWN_JOIN_TIMEOUT,
        thread_finish: THREAD_FINISH_TIMEOUT,
    };
}

#[cfg(windows)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum ConsumerExit {
    Completed,
    Cancelled,
}

/// shutdown 编排只依赖这个窄接口。生产实现持有真实 Tokio/std/Win32 句柄；
/// 单元测试实现提供确定性的 stop 失败、永久 pending 和 panic 结果。这样有界
/// 退出的证据覆盖的就是生产状态机，而不是一份复制的测试流程。
#[cfg(windows)]
trait SessionPowerShutdownOps {
    fn signal_shutdown(&mut self);
    fn request_pump_stop(&mut self) -> std::io::Result<()>;
    fn request_pump_fallback(&mut self) -> std::io::Result<()>;
    fn wait_consumer(&mut self)
    -> Pin<Box<dyn Future<Output = Result<ConsumerExit, String>> + '_>>;
    fn abort_consumer(&mut self);
    fn reap_consumer(&mut self);
    fn wait_pump_exit(&mut self) -> Pin<Box<dyn Future<Output = Result<(), String>> + '_>>;
    fn pump_is_finished(&self) -> bool;
    fn join_pump(&mut self) -> Result<bool, String>;
    fn wait_bridge_exit(&mut self) -> Pin<Box<dyn Future<Output = Result<(), String>> + '_>>;
    fn bridge_is_finished(&self) -> bool;
    fn join_bridge(&mut self) -> Result<bool, String>;
}

/// shutdown 结果报告。
#[derive(Debug)]
pub struct ShutdownReport {
    pub consumer_exited: bool,
    pub pump_exited: bool,
    pub pump_joined: bool,
    pub bridge_exited: bool,
    pub bridge_joined: bool,
    pub fallback_used: bool,
    pub errors: Vec<String>,
}

impl ShutdownReport {
    pub fn is_complete(&self) -> bool {
        self.consumer_exited
            && self.pump_exited
            && self.pump_joined
            && self.bridge_exited
            && self.bridge_joined
    }
}

#[cfg(windows)]
struct BridgeExitSignal(Option<tokio::sync::oneshot::Sender<()>>);

#[cfg(windows)]
impl Drop for BridgeExitSignal {
    fn drop(&mut self) {
        if let Some(tx) = self.0.take() {
            let _ = tx.send(());
        }
    }
}

#[cfg(windows)]
pub struct SessionPowerBridge {
    /// consumer 的 AbortHandle。shutdown 时，先等待 consumer 自然结束；
    /// timeout 后才 abort。
    consumer_abort: tokio::task::AbortHandle,
    /// supervisor task（持有 consumer JoinHandle，panic 时 latch monitor fault）
    supervisor: Option<tokio::task::JoinHandle<()>>,
    pub pump: wuji_windows::SessionPowerPumpHandle,
    pub shutdown_tx: watch::Sender<bool>,
    bridge_thread: Option<std::thread::JoinHandle<()>>,
    bridge_exited_rx: Option<tokio::sync::oneshot::Receiver<()>>,
    coordinator: Arc<CaptureCoordinator>,
}

#[cfg(windows)]
impl SessionPowerShutdownOps for SessionPowerBridge {
    fn signal_shutdown(&mut self) {
        let _ = self.shutdown_tx.send(true);
    }

    fn request_pump_stop(&mut self) -> std::io::Result<()> {
        self.pump.request_stop()
    }

    fn request_pump_fallback(&mut self) -> std::io::Result<()> {
        self.pump.request_quit_fallback()
    }

    fn wait_consumer(
        &mut self,
    ) -> Pin<Box<dyn Future<Output = Result<ConsumerExit, String>> + '_>> {
        Box::pin(async move {
            let Some(supervisor) = self.supervisor.as_mut() else {
                return Ok(ConsumerExit::Completed);
            };
            match supervisor.await {
                Ok(()) => Ok(ConsumerExit::Completed),
                Err(error) if error.is_cancelled() => Ok(ConsumerExit::Cancelled),
                Err(error) => Err(error.to_string()),
            }
        })
    }

    fn abort_consumer(&mut self) {
        self.consumer_abort.abort();
    }

    fn reap_consumer(&mut self) {
        if self
            .supervisor
            .as_ref()
            .is_some_and(tokio::task::JoinHandle::is_finished)
        {
            self.supervisor.take();
        }
    }

    fn wait_pump_exit(&mut self) -> Pin<Box<dyn Future<Output = Result<(), String>> + '_>> {
        Box::pin(async move {
            if self.pump.exited_rx_mut().is_none() {
                return if self.pump.is_finished() {
                    Ok(())
                } else {
                    Err("session/power pump 缺少 exited receiver".to_string())
                };
            }
            self.pump
                .exited_rx_mut()
                .expect("receiver existence checked")
                .await
                .map_err(|_| "session/power pump exited 信号被取消".to_string())
        })
    }

    fn pump_is_finished(&self) -> bool {
        self.pump.is_finished()
    }

    fn join_pump(&mut self) -> Result<bool, String> {
        self.pump.join_if_finished().map_err(|e| e.to_string())
    }

    fn wait_bridge_exit(&mut self) -> Pin<Box<dyn Future<Output = Result<(), String>> + '_>> {
        Box::pin(async move {
            if self.bridge_exited_rx.is_none() {
                return if self.bridge_is_finished() {
                    Ok(())
                } else {
                    Err("session/power bridge 缺少 exited receiver".to_string())
                };
            }
            self.bridge_exited_rx
                .as_mut()
                .expect("receiver existence checked")
                .await
                .map_err(|_| "session/power bridge exited 信号被取消".to_string())
        })
    }

    fn bridge_is_finished(&self) -> bool {
        self.bridge_thread
            .as_ref()
            .is_none_or(std::thread::JoinHandle::is_finished)
    }

    fn join_bridge(&mut self) -> Result<bool, String> {
        if !self.bridge_is_finished() {
            return Ok(false);
        }
        let Some(handle) = self.bridge_thread.take() else {
            return Ok(true);
        };
        handle
            .join()
            .map(|()| true)
            .map_err(|_| "session/power bridge 线程 panic".to_string())
    }
}

#[cfg(windows)]
async fn shutdown_session_power<O: SessionPowerShutdownOps>(
    ops: &mut O,
    coordinator: &CaptureCoordinator,
    timeouts: ShutdownTimeouts,
) -> ShutdownReport {
    ops.signal_shutdown();
    let mut errors = Vec::new();
    let mut fallback_used = false;
    if let Err(error) = ops.request_pump_stop() {
        errors.push(format!("pump PostMessageW stop 失败: {error}"));
        fallback_used = true;
        if let Err(fallback) = ops.request_pump_fallback() {
            errors.push(format!("pump PostThreadMessageW fallback 失败: {fallback}"));
        }
    }

    let mut consumer_exited = false;
    match tokio::time::timeout(timeouts.consumer_drain, ops.wait_consumer()).await {
        Ok(Ok(ConsumerExit::Completed)) => consumer_exited = true,
        Ok(Ok(ConsumerExit::Cancelled)) => {
            consumer_exited = true;
            errors.push("session/power consumer 在 shutdown abort 前已 cancelled".to_string());
        }
        Ok(Err(error)) => {
            consumer_exited = true;
            errors.push(format!("session/power supervisor 异常: {error}"));
        }
        Err(_) => {
            errors.push("session/power consumer 自然退出超时，已请求 abort".to_string());
            ops.abort_consumer();
            match tokio::time::timeout(timeouts.abort_confirm, ops.wait_consumer()).await {
                Ok(Ok(ConsumerExit::Completed | ConsumerExit::Cancelled)) => consumer_exited = true,
                Ok(Err(error)) => {
                    errors.push(format!("session/power consumer abort 后异常: {error}"));
                }
                Err(_) => errors.push("session/power consumer abort 后仍未退出".to_string()),
            }
        }
    }
    if consumer_exited {
        ops.reap_consumer();
    }

    let mut pump_exited = false;
    match tokio::time::timeout(timeouts.component_exit, ops.wait_pump_exit()).await {
        Ok(Ok(())) => pump_exited = true,
        Ok(Err(error)) => {
            pump_exited = ops.pump_is_finished();
            errors.push(error);
        }
        Err(_) => errors.push("session/power pump 退出超时".to_string()),
    }

    // 正常 stop 已投递但 pump 未响应时，再尝试线程级 WM_QUIT 兜底。
    if !pump_exited && !fallback_used {
        fallback_used = true;
        match ops.request_pump_fallback() {
            Ok(()) => {
                match tokio::time::timeout(timeouts.component_exit, ops.wait_pump_exit()).await {
                    Ok(Ok(())) => pump_exited = true,
                    Ok(Err(error)) => {
                        pump_exited = ops.pump_is_finished();
                        errors.push(format!("session/power pump fallback 后: {error}"));
                    }
                    Err(_) => errors.push("session/power pump fallback 后仍未退出".to_string()),
                }
            }
            Err(error) => errors.push(format!(
                "session/power pump timeout 后 PostThreadMessageW fallback 失败: {error}"
            )),
        }
    }

    let mut pump_joined = false;
    if pump_exited {
        let finished = tokio::time::timeout(timeouts.thread_finish, async {
            while !ops.pump_is_finished() {
                tokio::task::yield_now().await;
            }
        })
        .await
        .is_ok();
        if finished {
            match ops.join_pump() {
                Ok(joined) => pump_joined = joined,
                Err(error) => errors.push(format!("session/power pump join 失败: {error}")),
            }
        } else {
            errors.push("session/power pump 已发 exited 但线程未 finished".to_string());
        }
    }

    let mut bridge_exited = false;
    match tokio::time::timeout(timeouts.component_exit, ops.wait_bridge_exit()).await {
        Ok(Ok(())) => bridge_exited = true,
        Ok(Err(error)) => {
            bridge_exited = ops.bridge_is_finished();
            errors.push(error);
        }
        Err(_) => errors.push("session/power bridge 退出超时".to_string()),
    }

    let mut bridge_joined = false;
    if bridge_exited {
        let finished = tokio::time::timeout(timeouts.thread_finish, async {
            while !ops.bridge_is_finished() {
                tokio::task::yield_now().await;
            }
        })
        .await
        .is_ok();
        if finished {
            match ops.join_bridge() {
                Ok(joined) => bridge_joined = joined,
                Err(error) => errors.push(error),
            }
        } else {
            errors.push("session/power bridge 已发 exited 但线程未 finished".to_string());
        }
    }

    let report = ShutdownReport {
        consumer_exited,
        pump_exited,
        pump_joined,
        bridge_exited,
        bridge_joined,
        fallback_used,
        errors,
    };
    if !report.is_complete() || !report.errors.is_empty() {
        coordinator.latch_monitor_fault();
    }
    report
}

#[cfg(windows)]
impl SessionPowerBridge {
    /// 三层有界关闭。任何 timeout 后，未结束的真实句柄仍保留在 `self` 中；同步
    /// join 只在 exited + is_finished 双重确认后执行，绝不承担等待。
    pub async fn shutdown(&mut self) -> ShutdownReport {
        let coordinator = self.coordinator.clone();
        shutdown_session_power(self, &coordinator, ShutdownTimeouts::PRODUCTION).await
    }
}

#[cfg(windows)]
pub fn start_session_power_bridge(
    coordinator: Arc<CaptureCoordinator>,
) -> Result<SessionPowerBridge, String> {
    start_session_power_bridge_with(coordinator, wuji_windows::start_event_pump, |runner| {
        std::thread::Builder::new()
            .name("wuji-session-power-bridge".to_string())
            .spawn(runner)
    })
}

#[cfg(windows)]
type BridgeRunner = Box<dyn FnOnce() + Send + 'static>;

#[cfg(windows)]
fn start_session_power_bridge_with<StartPump, SpawnBridge>(
    coordinator: Arc<CaptureCoordinator>,
    start_pump: StartPump,
    spawn_bridge: SpawnBridge,
) -> Result<SessionPowerBridge, String>
where
    StartPump: FnOnce() -> std::io::Result<(
        std::sync::mpsc::Receiver<wuji_windows::SessionPowerEvent>,
        wuji_windows::SessionPowerPumpHandle,
    )>,
    SpawnBridge: FnOnce(BridgeRunner) -> std::io::Result<std::thread::JoinHandle<()>>,
{
    const STARTUP_ROLLBACK_TIMEOUT: Duration = Duration::from_secs(2);
    let (shutdown_tx, shutdown_rx) = watch::channel(false);

    match start_pump() {
        Ok((events_rx, mut pump)) => {
            let (bridge_tx, bridge_rx) = mpsc::channel::<wuji_windows::SessionPowerEvent>(8);
            let (bridge_exited_tx, bridge_exited_rx) = tokio::sync::oneshot::channel();
            let runner: BridgeRunner = Box::new(move || {
                let _exit_signal = BridgeExitSignal(Some(bridge_exited_tx));
                run_session_power_forward(events_rx, bridge_tx);
            });
            let bridge_thread = match spawn_bridge(runner) {
                Ok(thread) => thread,
                Err(error) => {
                    coordinator.latch_monitor_fault();
                    let cleanup = pump.shutdown_bounded(STARTUP_ROLLBACK_TIMEOUT);
                    return Err(match cleanup {
                        Ok(()) => format!("启动 session/power 桥接线程失败: {error}；pump 已回滚"),
                        Err(cleanup_error) => format!(
                            "启动 session/power 桥接线程失败: {error}；pump 回滚失败: {cleanup_error}"
                        ),
                    });
                }
            };

            let consumer_coordinator = coordinator.clone();
            let consumer_shutdown_rx = shutdown_rx.clone();
            let consumer = tokio::spawn(async move {
                run_session_power_events(bridge_rx, consumer_coordinator, consumer_shutdown_rx)
                    .await;
            });

            let consumer_abort = consumer.abort_handle();

            // supervisor：持有 consumer JoinHandle，panic 时锁存 monitor fault；
            // cancelled（shutdown abort）由 shutdown 报告确认，不视为运行时故障。
            let supervisor = tokio::spawn(supervise_session_power_consumer(
                consumer,
                coordinator.clone(),
            ));

            Ok(SessionPowerBridge {
                consumer_abort,
                supervisor: Some(supervisor),
                pump,
                shutdown_tx,
                bridge_thread: Some(bridge_thread),
                bridge_exited_rx: Some(bridge_exited_rx),
                coordinator,
            })
        }
        Err(e) => {
            coordinator.latch_monitor_fault();
            Err(format!("启动 session/power 事件泵失败: {e}"))
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn map_all_four_events_uses_production_function() {
        let cases = [
            (wuji_windows::SessionPowerEvent::Lock, true, false),
            (wuji_windows::SessionPowerEvent::Unlock, false, true),
            (wuji_windows::SessionPowerEvent::Sleep, true, false),
            (wuji_windows::SessionPowerEvent::Resume, false, true),
        ];
        for (raw, expect_enter, expect_release) in cases {
            let event = map_session_power_event(raw, 0);
            assert_eq!(event.is_enter(), expect_enter, "{raw:?}");
            assert_eq!(event.is_release(), expect_release, "{raw:?}");
        }
    }

    #[cfg(windows)]
    #[derive(Default)]
    struct FakeShutdownOps {
        stop_fails: bool,
        fallback_fails: bool,
        consumer_pending_until_abort: bool,
        consumer_error: Option<String>,
        pump_pending: bool,
        pump_exits_after_fallback: bool,
        pump_error: Option<String>,
        bridge_pending: bool,
        bridge_error: Option<String>,
        pump_join_error: bool,
        bridge_join_error: bool,
        signalled: bool,
        aborted: bool,
        fallback_calls: usize,
        pump_join_calls: usize,
        bridge_join_calls: usize,
    }

    #[cfg(windows)]
    impl SessionPowerShutdownOps for FakeShutdownOps {
        fn signal_shutdown(&mut self) {
            self.signalled = true;
        }

        fn request_pump_stop(&mut self) -> std::io::Result<()> {
            if self.stop_fails {
                Err(std::io::Error::other("injected stop failure"))
            } else {
                Ok(())
            }
        }

        fn request_pump_fallback(&mut self) -> std::io::Result<()> {
            self.fallback_calls += 1;
            if self.fallback_fails {
                Err(std::io::Error::other("injected fallback failure"))
            } else {
                Ok(())
            }
        }

        fn wait_consumer(
            &mut self,
        ) -> Pin<Box<dyn Future<Output = Result<ConsumerExit, String>> + '_>> {
            if let Some(error) = self.consumer_error.take() {
                return Box::pin(async move { Err(error) });
            }
            if self.consumer_pending_until_abort && !self.aborted {
                return Box::pin(std::future::pending());
            }
            let exit = if self.aborted {
                ConsumerExit::Cancelled
            } else {
                ConsumerExit::Completed
            };
            Box::pin(async move { Ok(exit) })
        }

        fn abort_consumer(&mut self) {
            self.aborted = true;
        }

        fn reap_consumer(&mut self) {}

        fn wait_pump_exit(&mut self) -> Pin<Box<dyn Future<Output = Result<(), String>> + '_>> {
            if let Some(error) = self.pump_error.take() {
                return Box::pin(async move { Err(error) });
            }
            if self.pump_pending && !(self.pump_exits_after_fallback && self.fallback_calls > 0) {
                return Box::pin(std::future::pending());
            }
            Box::pin(async { Ok(()) })
        }

        fn pump_is_finished(&self) -> bool {
            !self.pump_pending || (self.pump_exits_after_fallback && self.fallback_calls > 0)
        }

        fn join_pump(&mut self) -> Result<bool, String> {
            self.pump_join_calls += 1;
            if self.pump_join_error {
                Err("injected pump panic".to_string())
            } else {
                Ok(true)
            }
        }

        fn wait_bridge_exit(&mut self) -> Pin<Box<dyn Future<Output = Result<(), String>> + '_>> {
            if let Some(error) = self.bridge_error.take() {
                return Box::pin(async move { Err(error) });
            }
            if self.bridge_pending {
                return Box::pin(std::future::pending());
            }
            Box::pin(async { Ok(()) })
        }

        fn bridge_is_finished(&self) -> bool {
            !self.bridge_pending
        }

        fn join_bridge(&mut self) -> Result<bool, String> {
            self.bridge_join_calls += 1;
            if self.bridge_join_error {
                Err("injected bridge panic".to_string())
            } else {
                Ok(true)
            }
        }
    }

    #[cfg(windows)]
    fn shutdown_test_coordinator() -> (
        Arc<CaptureCoordinator>,
        Arc<crate::shared::SharedState>,
        watch::Receiver<wuji_core::domain::CaptureState>,
    ) {
        use crate::pipeline_health::PipelineHealth;
        use crate::shared::SharedState;
        use wuji_core::domain::CaptureState;
        use wuji_core::dto::RuntimeId;
        use wuji_core::settings::Settings;

        let shared = Arc::new(SharedState::new("0.1.0".to_string(), RuntimeId::new()));
        shared.set_capture_state(CaptureState::Running);
        let (settings_tx, _) = watch::channel(Settings::default());
        let (capture_tx, capture_rx) = watch::channel(CaptureState::Running);
        let (control_tx, _) = mpsc::channel(1);
        let (barrier_tx, _) = crate::barrier::barrier_request_channel(1);
        let coordinator = Arc::new(CaptureCoordinator::new(
            barrier_tx,
            capture_tx,
            control_tx,
            shared.clone(),
            settings_tx,
            CaptureState::Running,
            PipelineHealth::new(),
        ));
        (coordinator, shared, capture_rx)
    }

    #[cfg(windows)]
    const TEST_TIMEOUTS: ShutdownTimeouts = ShutdownTimeouts {
        consumer_drain: Duration::from_secs(1),
        abort_confirm: Duration::from_secs(1),
        component_exit: Duration::from_secs(1),
        thread_finish: Duration::from_secs(1),
    };

    #[cfg(windows)]
    #[tokio::test(start_paused = true)]
    async fn shutdown_state_machine_normal_path_reaps_all_layers() {
        let (coordinator, shared, _capture_rx) = shutdown_test_coordinator();
        let mut ops = FakeShutdownOps::default();
        let report = shutdown_session_power(&mut ops, &coordinator, TEST_TIMEOUTS).await;
        assert!(report.is_complete());
        assert!(report.errors.is_empty());
        assert!(!report.fallback_used);
        assert!(ops.signalled);
        assert_eq!(ops.pump_join_calls, 1);
        assert_eq!(ops.bridge_join_calls, 1);
        assert!(
            !shared
                .errors()
                .contains_key(&wuji_core::error::ErrorSource::LifecyclePump)
        );
    }

    #[cfg(windows)]
    #[tokio::test(start_paused = true)]
    async fn shutdown_state_machine_stop_failure_uses_fallback_and_reports_fault() {
        let (coordinator, shared, _capture_rx) = shutdown_test_coordinator();
        let mut ops = FakeShutdownOps {
            stop_fails: true,
            ..FakeShutdownOps::default()
        };
        let report = shutdown_session_power(&mut ops, &coordinator, TEST_TIMEOUTS).await;
        assert!(report.is_complete());
        assert!(report.fallback_used);
        assert_eq!(ops.fallback_calls, 1);
        assert!(report.errors.iter().any(|e| e.contains("stop 失败")));
        assert!(
            shared
                .errors()
                .contains_key(&wuji_core::error::ErrorSource::LifecyclePump)
        );
    }

    #[cfg(windows)]
    #[tokio::test(start_paused = true)]
    async fn shutdown_state_machine_pump_timeout_returns_without_joining_pending_threads() {
        let (coordinator, shared, _capture_rx) = shutdown_test_coordinator();
        let mut ops = FakeShutdownOps {
            pump_pending: true,
            bridge_pending: true,
            ..FakeShutdownOps::default()
        };
        let report = shutdown_session_power(&mut ops, &coordinator, TEST_TIMEOUTS).await;
        assert!(!report.is_complete());
        assert!(report.fallback_used);
        assert_eq!(ops.pump_join_calls, 0, "pending pump 禁止同步 join");
        assert_eq!(ops.bridge_join_calls, 0, "pending bridge 禁止同步 join");
        assert!(report.errors.iter().any(|e| e.contains("pump 退出超时")));
        assert!(report.errors.iter().any(|e| e.contains("bridge 退出超时")));
        assert!(
            shared
                .errors()
                .contains_key(&wuji_core::error::ErrorSource::LifecyclePump)
        );
    }

    #[cfg(windows)]
    #[tokio::test(start_paused = true)]
    async fn shutdown_state_machine_consumer_timeout_aborts_then_confirms_exit() {
        let (coordinator, shared, _capture_rx) = shutdown_test_coordinator();
        let mut ops = FakeShutdownOps {
            consumer_pending_until_abort: true,
            ..FakeShutdownOps::default()
        };
        let report = shutdown_session_power(&mut ops, &coordinator, TEST_TIMEOUTS).await;
        assert!(report.is_complete());
        assert!(ops.aborted);
        assert!(report.errors.iter().any(|e| e.contains("自然退出超时")));
        assert!(
            shared
                .errors()
                .contains_key(&wuji_core::error::ErrorSource::LifecyclePump)
        );
    }

    #[cfg(windows)]
    #[tokio::test(start_paused = true)]
    async fn shutdown_state_machine_bridge_timeout_and_thread_panics_are_incomplete() {
        let (coordinator, shared, _capture_rx) = shutdown_test_coordinator();
        let mut bridge_pending = FakeShutdownOps {
            bridge_pending: true,
            ..FakeShutdownOps::default()
        };
        let report = shutdown_session_power(&mut bridge_pending, &coordinator, TEST_TIMEOUTS).await;
        assert!(!report.is_complete());
        assert_eq!(bridge_pending.bridge_join_calls, 0);

        let mut panics = FakeShutdownOps {
            pump_join_error: true,
            bridge_join_error: true,
            ..FakeShutdownOps::default()
        };
        let panic_report = shutdown_session_power(&mut panics, &coordinator, TEST_TIMEOUTS).await;
        assert!(!panic_report.is_complete());
        assert!(panic_report.errors.iter().any(|e| e.contains("pump panic")));
        assert!(
            panic_report
                .errors
                .iter()
                .any(|e| e.contains("bridge panic"))
        );
        assert!(
            shared
                .errors()
                .contains_key(&wuji_core::error::ErrorSource::LifecyclePump)
        );
    }

    #[cfg(windows)]
    #[test]
    fn startup_pump_failure_is_propagated_and_fails_closed_before_bridge_spawn() {
        let (coordinator, shared, capture_rx) = shutdown_test_coordinator();
        let result = start_session_power_bridge_with(
            coordinator,
            || Err(std::io::Error::other("injected pump startup failure")),
            |_runner| panic!("pump 失败后不得尝试创建 bridge"),
        );
        let error = match result {
            Ok(_) => panic!("pump 启动失败必须传播"),
            Err(error) => error,
        };
        assert!(error.contains("injected pump startup failure"));
        assert_eq!(
            shared.capture_state(),
            wuji_core::domain::CaptureState::Stopped
        );
        assert_eq!(
            *capture_rx.borrow(),
            wuji_core::domain::CaptureState::Stopped
        );
        assert_eq!(
            shared.status_dto().capture_state,
            wuji_core::domain::CaptureState::Stopped
        );
        assert_eq!(
            shared.status_dto().safe_error_code,
            Some(wuji_core::error::SafeErrorCode::InternalSafeError)
        );
        assert!(
            shared
                .errors()
                .contains_key(&wuji_core::error::ErrorSource::LifecyclePump)
        );
    }

    #[cfg(windows)]
    #[test]
    fn startup_bridge_spawn_failure_rolls_back_real_pump_before_returning() {
        let (coordinator, shared, capture_rx) = shutdown_test_coordinator();
        let result = start_session_power_bridge_with(
            coordinator,
            wuji_windows::start_event_pump,
            |runner| {
                drop(runner);
                Err(std::io::Error::other("injected bridge spawn failure"))
            },
        );
        let error = match result {
            Ok(_) => panic!("bridge 创建失败必须传播"),
            Err(error) => error,
        };
        assert!(error.contains("injected bridge spawn failure"));
        assert!(error.contains("pump 已回滚"), "必须确认 pump 清理: {error}");
        assert_eq!(
            shared.capture_state(),
            wuji_core::domain::CaptureState::Stopped
        );
        assert_eq!(
            *capture_rx.borrow(),
            wuji_core::domain::CaptureState::Stopped
        );
        assert_eq!(
            shared.status_dto().capture_state,
            wuji_core::domain::CaptureState::Stopped
        );
        assert!(
            shared
                .errors()
                .contains_key(&wuji_core::error::ErrorSource::LifecyclePump)
        );

        // 固定窗口类已在 pump 退出时注销；能够立即再次启动并完整回收，证明失败
        // 返回前没有遗留仍占用窗口类/消息循环的旧 pump。
        let (_events, mut probe) =
            wuji_windows::start_event_pump().expect("回滚后应能立即重建 pump");
        probe
            .shutdown_bounded(Duration::from_secs(2))
            .expect("探针 pump 必须有界回收");
    }

    #[cfg(not(windows))]
    #[tokio::test]
    async fn pump_start_failure_sets_monitor_fault() {
        let shared = Arc::new(SharedState::new("0.1.0".to_string(), RuntimeId::new()));
        shared.set_capture_state(CaptureState::Running);
        let (settings_tx, _) = watch::channel(wuji_core::settings::Settings::default());
        let (capture_state_tx, capture_rx) = watch::channel(CaptureState::Running);
        let (control_tx, _) = mpsc::channel(64);
        let health = PipelineHealth::new();
        let (barrier_tx, _) = crate::barrier::barrier_request_channel(64);
        let coordinator = Arc::new(CaptureCoordinator::new(
            barrier_tx,
            capture_state_tx.clone(),
            control_tx,
            shared,
            settings_tx,
            CaptureState::Running,
            health,
        ));

        let result = start_session_power_bridge(coordinator);
        assert!(result.is_err());
        assert_eq!(*capture_rx.borrow(), CaptureState::Stopped);
    }
}
