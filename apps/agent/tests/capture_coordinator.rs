//! 阶段 4.3：CaptureCoordinator 统一控制入口的确定性测试（含复审 P1-01/P1-02 补修）。
//!
//! 覆盖必测场景：并发 transition 严格串行、Barrier 注入失败时 control 不悬挂、
//! Writer ack 失败后 shared/watch/DTO 一致并保持安全冻结、settings 失败保持
//! last-known-good、系统事件等待双 ack；
//! 复审 P1-01：Writer fatal 不可由 capture_start/resume 复活；
//! 复审 P1-02：任务/channel 退出后不得虚假 Running（健康检查 + publish Result）。

use std::sync::Arc;
use std::time::Duration;

use tokio::sync::{mpsc, watch};
use wuji_core::domain::{CaptureState, ProcessState, WriterState};
use wuji_core::dto::RuntimeId;
use wuji_core::error::{ErrorSource, SafeErrorCode};
use wuji_core::pipeline::BarrierKind;
use wuji_core::settings::Settings;
use wuji_rebuild_agent::activity::EngineEvent;
use wuji_rebuild_agent::barrier::{BarrierInjectError, BarrierRequest};
use wuji_rebuild_agent::capture_coordinator::CaptureCoordinator;
use wuji_rebuild_agent::capture_coordinator::SystemLifecycleEvent;
use wuji_rebuild_agent::pipeline_health::{PipelineHealth, TaskHealthGuard, TaskLifecycle};
use wuji_rebuild_agent::shared::SharedState;
use wuji_rebuild_agent::writer_task::WriterControl;
use wuji_storage::error::StorageError;

const T0: i64 = 1_784_332_800_000;

struct Harness {
    coordinator: Arc<CaptureCoordinator>,
    shared: Arc<SharedState>,
    health: Arc<PipelineHealth>,
    /// 三任务的 RAII 守卫（capture/processor/writer）：harness 在构造时同步注册，
    /// 模拟生产任务已启动；需要模拟任务死亡的测试 drop 掉对应守卫。
    health_guards: (TaskHealthGuard, TaskHealthGuard, TaskHealthGuard),
    /// mark_fatal 模拟写入点（与 WriterTask 持有的 watch 发送端同通道）。
    capture_tx: watch::Sender<CaptureState>,
    barrier_rx: mpsc::Receiver<BarrierRequest>,
    control_rx: mpsc::Receiver<WriterControl>,
    capture_rx: watch::Receiver<CaptureState>,
    settings_rx: watch::Receiver<Settings>,
}

fn harness(initial: CaptureState) -> Harness {
    let shared = Arc::new(SharedState::new("0.1.0".to_string(), RuntimeId::new()));
    shared.set_capture_state(initial);
    let health = PipelineHealth::new();
    // 与生产一致：任务注册在 spawn 前同步完成（第二次复审 P1 状态机）。
    let guards = (
        health.register_capture(),
        health.register_processor(),
        health.register_writer(),
    );
    assert!(health.all_alive());
    let (barrier_tx, barrier_rx) = wuji_rebuild_agent::barrier::barrier_request_channel(8);
    let (capture_state_tx, capture_rx) = watch::channel(initial);
    let (control_tx, control_rx) = mpsc::channel(8);
    let (settings_tx, settings_rx) = watch::channel(Settings::default());
    let coordinator = Arc::new(CaptureCoordinator::new(
        barrier_tx,
        capture_state_tx.clone(),
        control_tx,
        shared.clone(),
        settings_tx,
        initial,
        health.clone(),
    ));
    Harness {
        coordinator,
        shared,
        health,
        health_guards: guards,
        capture_tx: capture_state_tx,
        barrier_rx,
        control_rx,
        capture_rx,
        settings_rx,
    }
}

/// 与 WriterTask::mark_fatal 相同的对外写入（watch + shared + writer/process 状态）。
fn simulate_mark_fatal(h: &Harness) {
    h.capture_tx.send_replace(CaptureState::Stopped);
    h.shared.set_writer_state(WriterState::Faulted);
    h.shared.set_process_state(ProcessState::Faulted);
    h.shared.set_capture_state(CaptureState::Stopped);
}

/// 五处状态全部 Stopped（watch/shared/DTO/desired/effective）。
/// 按字段传入：允许调用方已 drop 掉其他字段（partial move）。
fn assert_stopped_everywhere(
    coordinator: &CaptureCoordinator,
    shared: &SharedState,
    capture_rx: &watch::Receiver<CaptureState>,
) {
    assert_eq!(coordinator.desired_state(), CaptureState::Stopped);
    assert_eq!(coordinator.effective_state(), CaptureState::Stopped);
    assert_eq!(*capture_rx.borrow(), CaptureState::Stopped);
    assert_eq!(shared.capture_state(), CaptureState::Stopped);
    assert_eq!(shared.status_dto().capture_state, CaptureState::Stopped);
}

/// 完成 control lane 上等待的一条 control（按变体 ack）。
async fn ack_next_control(control_rx: &mut mpsc::Receiver<WriterControl>) {
    match control_rx.recv().await.expect("control 必须到达") {
        WriterControl::Lifecycle { ack, .. } => ack.send(Ok(())).unwrap(),
        WriterControl::SettingsApplied { ack, .. } => ack.send(Ok(1)).unwrap(),
        _other => panic!("不应出现其他 control"),
    }
}

/// 等待并取走一条 Lifecycle control 的 ack。
async fn recv_lifecycle_ack(
    control_rx: &mut mpsc::Receiver<WriterControl>,
) -> tokio::sync::oneshot::Sender<wuji_storage::error::Result<()>> {
    match control_rx.recv().await {
        Some(WriterControl::Lifecycle { ack, .. }) => ack,
        _other => panic!("应为 Lifecycle control"),
    }
}

/// pause：冻结先于 injected ack，双 ack 都等待，全程 watch/shared/DTO 一致。
#[tokio::test]
async fn pause_waits_for_both_acks_and_publishes_consistently() {
    let mut h = harness(CaptureState::Running);
    let cmd = tokio::spawn({
        let coordinator = h.coordinator.clone();
        async move { coordinator.apply_capture_command("capture_pause", T0).await }
    });

    // 冻结发生在 injected ack 之前：watch 与 shared 已一致进入 Paused。
    let request = h.barrier_rx.recv().await.expect("barrier 请求必须到达");
    assert_eq!(request.token.kind, BarrierKind::Lifecycle);
    assert_eq!(request.token.expected_revision, 0);
    assert_eq!(*h.capture_rx.borrow(), CaptureState::Paused);
    assert_eq!(h.shared.capture_state(), CaptureState::Paused);
    // injected ack 之前绝不发送 control（无悬挂等待）。
    assert!(
        h.control_rx.try_recv().is_err(),
        "injected ack 前不得出现 control"
    );

    let barrier_id = request.token.id.clone();
    request.injected_ack.send(Ok(())).unwrap();
    let ack = match h.control_rx.recv().await {
        Some(WriterControl::Lifecycle {
            barrier_id: control_id,
            ack,
            ..
        }) => {
            assert_eq!(control_id, barrier_id, "control 必须与注入同 ID");
            ack
        }
        _other => panic!("应为 Lifecycle control"),
    };
    ack.send(Ok(())).unwrap();

    let result = cmd.await.expect("任务不 panic");
    assert_eq!(result, Ok(CaptureState::Paused));
    assert_eq!(*h.capture_rx.borrow(), CaptureState::Paused);
    assert_eq!(h.shared.capture_state(), CaptureState::Paused);
    assert_eq!(h.shared.status_dto().capture_state, CaptureState::Paused);
    assert_eq!(h.coordinator.desired_state(), CaptureState::Paused);
    assert_eq!(h.coordinator.effective_state(), CaptureState::Paused);
}

/// Barrier 注入失败：control 不悬挂（从未发送），fail-closed 安全冻结，
/// shared/watch/DTO 一致；采集任务已死亡时 resume 不得虚假 Running（复审 P1-02）。
#[tokio::test]
async fn injection_failure_fails_closed_and_never_sends_control() {
    let mut h = harness(CaptureState::Running);
    drop(h.barrier_rx); // Capture Loop 不存在 → RequestClosed

    let error = h
        .coordinator
        .apply_capture_command("capture_pause", T0)
        .await
        .expect_err("注入失败必须返回错误");
    assert_eq!(error.code, SafeErrorCode::InternalSafeError);
    assert!(
        h.control_rx.try_recv().is_err(),
        "注入失败绝不发送 control（无悬挂等待）"
    );

    // fail-closed：watch/shared/DTO 一致冻结在 Paused，desired 同步。
    assert_eq!(*h.capture_rx.borrow(), CaptureState::Paused);
    assert_eq!(h.shared.capture_state(), CaptureState::Paused);
    assert_eq!(h.shared.status_dto().capture_state, CaptureState::Paused);
    assert_eq!(h.coordinator.desired_state(), CaptureState::Paused);
    assert_eq!(
        h.shared.errors().get(&ErrorSource::Writer),
        Some(&SafeErrorCode::InternalSafeError),
        "失败必须留下来源明确的诊断"
    );

    // 幂等 pause：已处于 Paused，不再产生任何 barrier。
    let again = h
        .coordinator
        .apply_capture_command("capture_pause", T0)
        .await;
    assert_eq!(again, Ok(CaptureState::Paused));

    // 复审 P1-02：采集任务已死亡（barrier channel 关闭），resume 发布 Running
    // 必须被拒绝并回退到 Stopped，绝不虚假 Running。
    let error = h
        .coordinator
        .apply_capture_command("capture_resume", T0)
        .await
        .expect_err("采集任务死亡时 resume 不得返回 Running");
    assert_eq!(error.code, SafeErrorCode::InternalSafeError);
    assert_stopped_everywhere(&h.coordinator, &h.shared, &h.capture_rx);
}

/// Writer ack 返回错误：shared/watch/DTO 一致并保持安全冻结。
#[tokio::test]
async fn writer_ack_failure_keeps_consistent_frozen_state() {
    let mut h = harness(CaptureState::Running);
    let cmd = tokio::spawn({
        let coordinator = h.coordinator.clone();
        async move { coordinator.apply_capture_command("capture_pause", T0).await }
    });
    let request = h.barrier_rx.recv().await.expect("barrier 请求必须到达");
    request.injected_ack.send(Ok(())).unwrap();
    let ack = recv_lifecycle_ack(&mut h.control_rx).await;
    ack.send(Err(StorageError::new(
        SafeErrorCode::SettingsConflict,
        "模拟提交失败",
    )))
    .unwrap();

    let error = cmd
        .await
        .expect("任务不 panic")
        .expect_err("ack 失败必须返回错误");
    assert_eq!(error.code, SafeErrorCode::SettingsConflict);
    // fail-closed：状态一致冻结，绝不回滚成 watch Paused/shared Running 的分裂。
    assert_eq!(*h.capture_rx.borrow(), CaptureState::Paused);
    assert_eq!(h.shared.capture_state(), CaptureState::Paused);
    assert_eq!(h.shared.status_dto().capture_state, CaptureState::Paused);
    assert_eq!(h.coordinator.desired_state(), CaptureState::Paused);
}

/// 并发 Pause + Settings：唯一 transition lock 严格串行——第一个 transition
/// 未完成（双 ack 未齐）前，第二个不得发出任何 BarrierRequest。
#[tokio::test]
async fn concurrent_pause_and_settings_are_strictly_serialized() {
    let mut h = harness(CaptureState::Running);
    let settings = Settings {
        revision: "1".to_string(),
        ..Settings::default()
    };
    let pause = tokio::spawn({
        let coordinator = h.coordinator.clone();
        async move { coordinator.apply_capture_command("capture_pause", T0).await }
    });
    let apply = tokio::spawn({
        let coordinator = h.coordinator.clone();
        async move { coordinator.apply_settings(settings, T0).await }
    });

    let first = h.barrier_rx.recv().await.expect("第一个 barrier 请求");
    let leaked = tokio::time::timeout(Duration::from_millis(100), h.barrier_rx.recv()).await;
    assert!(
        leaked.is_err(),
        "同一时刻只允许一个 transition（唯一 transition lock）"
    );
    let first_kind = first.token.kind;
    first.injected_ack.send(Ok(())).unwrap();
    ack_next_control(&mut h.control_rx).await;

    // 第一个完成后第二个才到达。
    let second = tokio::time::timeout(Duration::from_secs(1), h.barrier_rx.recv())
        .await
        .expect("第二个 barrier 请求必须到达")
        .expect("通道不得关闭");
    assert_ne!(second.token.kind, first_kind, "两个请求应分属两条路径");
    second.injected_ack.send(Ok(())).unwrap();
    ack_next_control(&mut h.control_rx).await;

    let pause_result = pause.await.expect("pause 不 panic");
    let apply_result = apply.await.expect("settings 不 panic");
    assert!(pause_result.is_ok(), "pause 必须成功: {pause_result:?}");
    assert_eq!(apply_result, Ok(1));
    // 终态一致：paused + settings watch 已更新。
    assert_eq!(*h.capture_rx.borrow(), CaptureState::Paused);
    assert_eq!(h.shared.capture_state(), CaptureState::Paused);
    assert_eq!(h.settings_rx.borrow().revision, "1");
}

/// Settings 注入失败（管线仍存活）：保持 last-known-good，采集不冻结，
/// settings watch/applied revision 不变，诊断来源明确。
#[tokio::test]
async fn settings_injection_failure_keeps_last_known_good() {
    let mut h = harness(CaptureState::Running);
    let settings = Settings {
        revision: "1".to_string(),
        ..Settings::default()
    };
    let apply = tokio::spawn({
        let coordinator = h.coordinator.clone();
        async move { coordinator.apply_settings(settings, T0).await }
    });

    let request = h.barrier_rx.recv().await.expect("barrier 请求必须到达");
    assert_eq!(request.token.kind, BarrierKind::SettingsApplied);
    // transition 骨架：gate 已冻结，desired 仍是 Running。
    assert_eq!(*h.capture_rx.borrow(), CaptureState::Paused);
    assert_eq!(h.coordinator.desired_state(), CaptureState::Running);

    request
        .injected_ack
        .send(Err(BarrierInjectError::Closed))
        .unwrap();
    let error = apply.await.expect("任务不 panic").expect_err("必须失败");
    assert_eq!(error.code, SafeErrorCode::InternalSafeError);

    // last-known-good：采集恢复 Running，settings watch/applied 不变，无 control。
    assert_eq!(*h.capture_rx.borrow(), CaptureState::Running);
    assert_eq!(h.shared.capture_state(), CaptureState::Running);
    assert_eq!(h.settings_rx.borrow().revision, "0");
    assert_eq!(h.shared.applied_settings_revision(), 0);
    assert!(h.control_rx.try_recv().is_err());
    assert_eq!(
        h.shared.errors().get(&ErrorSource::Settings),
        Some(&SafeErrorCode::SettingsSavedNotApplied)
    );
}

/// Settings 成功：Writer ack 之前 settings watch 不更新；ack 后先交付 watch
/// 再解冻 gate。
#[tokio::test]
async fn settings_success_updates_watch_before_unfreeze() {
    let mut h = harness(CaptureState::Running);
    let settings = Settings {
        revision: "1".to_string(),
        ..Settings::default()
    };
    let apply = tokio::spawn({
        let coordinator = h.coordinator.clone();
        async move { coordinator.apply_settings(settings, T0).await }
    });

    let request = h.barrier_rx.recv().await.expect("barrier 请求必须到达");
    assert_eq!(request.token.kind, BarrierKind::SettingsApplied);
    assert_eq!(request.token.expected_revision, 0);
    request.injected_ack.send(Ok(())).unwrap();
    let ack = match h.control_rx.recv().await {
        Some(WriterControl::SettingsApplied { ack, .. }) => ack,
        _other => panic!("应为 SettingsApplied control"),
    };
    // Writer ack 之前：settings watch 不得更新，gate 保持冻结。
    assert_eq!(h.settings_rx.borrow().revision, "0");
    assert_eq!(*h.capture_rx.borrow(), CaptureState::Paused);

    ack.send(Ok(1)).unwrap();
    let applied = apply.await.expect("任务不 panic").expect("apply 必须成功");
    assert_eq!(applied, 1);
    assert_eq!(h.settings_rx.borrow().revision, "1");
    assert_eq!(*h.capture_rx.borrow(), CaptureState::Running);
    assert_eq!(h.shared.capture_state(), CaptureState::Running);
}

/// 系统事件（Sleep/Lock）：经同一 Coordinator，等待双 ack；
/// 本阶段不叠加 suppression（4.5），gate 完成后恢复打开。
#[tokio::test]
async fn system_event_waits_both_acks_and_keeps_desired() {
    let mut h = harness(CaptureState::Running);
    let event_task = tokio::spawn({
        let coordinator = h.coordinator.clone();
        async move {
            coordinator
                .apply_system_lifecycle_event(SystemLifecycleEvent::Sleep { at_utc_ms: T0 })
                .await
        }
    });

    let request = h.barrier_rx.recv().await.expect("barrier 请求必须到达");
    assert_eq!(request.token.kind, BarrierKind::Lifecycle);
    request.injected_ack.send(Ok(())).unwrap();
    let ack = match h.control_rx.recv().await {
        Some(WriterControl::Lifecycle { event, ack, .. }) => {
            assert!(
                matches!(event, EngineEvent::SystemSleep { .. }),
                "必须透传系统事件"
            );
            ack
        }
        _other => panic!("应为 Lifecycle control"),
    };
    ack.send(Ok(())).unwrap();
    event_task
        .await
        .expect("任务不 panic")
        .expect("系统事件必须成功");

    // 不改变 desired；Sleep suppression active → effective Paused。
    assert_eq!(h.coordinator.desired_state(), CaptureState::Running);
    assert_eq!(*h.capture_rx.borrow(), CaptureState::Paused);
}

/// 系统事件注入失败且采集任务已死亡：LifecyclePump 来源诊断，安全停止，
/// 绝不虚假 Running（复审 P1-02）。
#[tokio::test]
async fn system_event_injection_failure_reports_pump_source() {
    let h = harness(CaptureState::Running);
    drop(h.barrier_rx);

    let error = h
        .coordinator
        .apply_system_lifecycle_event(SystemLifecycleEvent::Lock { at_utc_ms: T0 })
        .await
        .expect_err("注入失败必须返回错误");
    assert_eq!(error.code, SafeErrorCode::InternalSafeError);
    assert_eq!(
        h.shared.errors().get(&ErrorSource::LifecyclePump),
        Some(&SafeErrorCode::InternalSafeError)
    );
    // Lock suppression active → effective Paused（非 Running）
    assert_eq!(*h.capture_rx.borrow(), CaptureState::Paused);
    assert_eq!(h.coordinator.desired_state(), CaptureState::Running);
}

/// 非法转换与幂等转换都不产生 barrier/control。
#[tokio::test]
async fn invalid_and_idempotent_commands_issue_no_barrier() {
    let h = harness(CaptureState::Stopped);
    let error = h
        .coordinator
        .apply_capture_command("capture_pause", T0)
        .await
        .expect_err("stopped 不能 pause");
    assert_eq!(error.code, SafeErrorCode::CaptureInvalidState);
    let idempotent = h
        .coordinator
        .apply_capture_command("capture_stop", T0)
        .await;
    assert_eq!(idempotent, Ok(CaptureState::Stopped), "stop 幂等成功");
    assert!(h.barrier_rx.is_empty());
    assert!(h.control_rx.is_empty());
}

/// 启动对账禁止采集（capture_blocked）：capture_start 拒绝且不产生 barrier。
#[tokio::test]
async fn capture_start_blocked_when_settings_unrecoverable() {
    let h = harness(CaptureState::Stopped);
    h.shared.set_capture_blocked(true);
    let error = h
        .coordinator
        .apply_capture_command("capture_start", T0)
        .await
        .expect_err("blocked 必须拒绝");
    assert_eq!(error.code, SafeErrorCode::SettingsInvalid);
    assert_eq!(*h.capture_rx.borrow(), CaptureState::Stopped);
    assert!(h.barrier_rx.is_empty());
    assert!(h.control_rx.is_empty());
}

/// EnsureRecording（09 §9.3）：settings 不可恢复时同样拒绝，零副作用——
/// desired/watch/shared 保持 Stopped，无 barrier/control。
#[tokio::test]
async fn ensure_recording_blocked_when_settings_unrecoverable() {
    let h = harness(CaptureState::Stopped);
    h.shared.set_capture_blocked(true);
    let error = h
        .coordinator
        .apply_capture_command("capture_ensure_recording", T0)
        .await
        .expect_err("blocked 必须拒绝");
    assert_eq!(error.code, SafeErrorCode::SettingsInvalid);
    assert_eq!(*h.capture_rx.borrow(), CaptureState::Stopped);
    assert!(h.barrier_rx.is_empty());
    assert!(h.control_rx.is_empty());
}

/// EnsureRecording（09 §9.3）：writer fatal 并入后拒绝（与 start/resume 同 fence），
/// 零副作用——五处状态全部 Stopped，无 barrier/control。
#[tokio::test]
async fn ensure_recording_rejected_when_writer_faulted() {
    let h = harness(CaptureState::Stopped);
    simulate_mark_fatal(&h);
    let error = h
        .coordinator
        .apply_capture_command("capture_ensure_recording", T0)
        .await
        .expect_err("fatal 后 ensure 必须拒绝");
    assert_eq!(error.code, SafeErrorCode::AgentWriterFaulted);
    assert!(h.barrier_rx.is_empty());
    assert!(h.control_rx.is_empty());
    assert_stopped_everywhere(&h.coordinator, &h.shared, &h.capture_rx);
}

/// revision 降级在 transition lock 内拒绝，不产生任何 barrier/control。
#[tokio::test]
async fn settings_downgrade_is_rejected_under_lock() {
    let h = harness(CaptureState::Running);
    h.shared.set_applied_settings_revision(5);
    let error = h
        .coordinator
        .apply_settings(
            Settings {
                revision: "4".to_string(),
                ..Settings::default()
            },
            T0,
        )
        .await
        .expect_err("降级必须拒绝");
    assert_eq!(error.code, SafeErrorCode::SettingsConflict);
    assert!(h.barrier_rx.is_empty());
    assert!(h.control_rx.is_empty());
    assert_eq!(h.shared.applied_settings_revision(), 5);
}

/// 复审 P1-01：Writer fatal 被并入后，start/resume 一律 AGENT_WRITER_FAULTED 拒绝，
/// 零副作用（无 barrier/control），五处状态全部 Stopped，且不可由任何命令解除。
#[tokio::test]
async fn writer_fatal_blocks_start_and_resume_forever() {
    let h = harness(CaptureState::Running);
    simulate_mark_fatal(&h);

    // Coordinator 在下一次 transition 并入 fatal。
    let error = h
        .coordinator
        .apply_capture_command("capture_start", T0)
        .await
        .expect_err("fatal 后 start 必须拒绝");
    assert_eq!(error.code, SafeErrorCode::AgentWriterFaulted);
    let error = h
        .coordinator
        .apply_capture_command("capture_resume", T0)
        .await
        .expect_err("fatal 后 resume 必须拒绝");
    assert_eq!(error.code, SafeErrorCode::AgentWriterFaulted);

    // 零副作用：无 barrier、无 control。
    assert!(h.barrier_rx.is_empty());
    assert!(h.control_rx.is_empty());
    assert_stopped_everywhere(&h.coordinator, &h.shared, &h.capture_rx);

    // 其他命令不清除 writer_fault：stop 幂等成功后 start 仍拒绝。
    let stopped = h
        .coordinator
        .apply_capture_command("capture_stop", T0)
        .await;
    assert_eq!(stopped, Ok(CaptureState::Stopped));
    let error = h
        .coordinator
        .apply_capture_command("capture_start", T0)
        .await
        .expect_err("writer_fault 不可由用户命令解除");
    assert_eq!(error.code, SafeErrorCode::AgentWriterFaulted);
    assert_stopped_everywhere(&h.coordinator, &h.shared, &h.capture_rx);
}

/// 复审 P1-01：普通 lifecycle fault 可由合法显式命令解除，writer_fault 不可——
/// 两类 fault 不混淆。
#[tokio::test]
async fn lifecycle_fault_is_clearable_but_writer_fault_is_not() {
    let mut h = harness(CaptureState::Running);
    // lifecycle 提交失败 → fail-closed 冻结（管线仍存活）。
    let cmd = tokio::spawn({
        let coordinator = h.coordinator.clone();
        async move { coordinator.apply_capture_command("capture_pause", T0).await }
    });
    let request = h.barrier_rx.recv().await.expect("barrier 请求必须到达");
    request.injected_ack.send(Ok(())).unwrap();
    let ack = recv_lifecycle_ack(&mut h.control_rx).await;
    ack.send(Err(StorageError::new(
        SafeErrorCode::InternalSafeError,
        "模拟提交失败",
    )))
    .unwrap();
    let _ = cmd.await.expect("任务不 panic").expect_err("ack 必须失败");
    assert_eq!(h.coordinator.desired_state(), CaptureState::Paused);

    // 显式 resume 解除普通 fault（控制面健康：channel 都在）。
    let resumed = h
        .coordinator
        .apply_capture_command("capture_resume", T0)
        .await
        .expect("lifecycle fault 可由显式命令解除");
    assert_eq!(resumed, CaptureState::Running);
    assert_eq!(*h.capture_rx.borrow(), CaptureState::Running);

    // writer fatal：不可解除，返回稳定错误 AGENT_WRITER_FAULTED。
    simulate_mark_fatal(&h);
    let error = h
        .coordinator
        .apply_capture_command("capture_start", T0)
        .await
        .expect_err("fatal 后 start 必须拒绝");
    assert_eq!(error.code, SafeErrorCode::AgentWriterFaulted);
    let error = h
        .coordinator
        .apply_capture_command("capture_resume", T0)
        .await
        .expect_err("fatal 后 resume 必须拒绝");
    assert_eq!(error.code, SafeErrorCode::AgentWriterFaulted);
    assert_stopped_everywhere(&h.coordinator, &h.shared, &h.capture_rx);
}

/// 复审 P1-02：barrier channel 关闭（Capture Loop 退出）后 start 拒绝，
/// 五处状态全部 Stopped，不虚假 Running。
#[tokio::test]
async fn start_rejected_when_capture_channel_closed() {
    let mut h = harness(CaptureState::Stopped);
    drop(h.barrier_rx);
    let error = h
        .coordinator
        .apply_capture_command("capture_start", T0)
        .await
        .expect_err("capture channel 关闭必须拒绝");
    assert_eq!(error.code, SafeErrorCode::InternalSafeError);
    assert_stopped_everywhere(&h.coordinator, &h.shared, &h.capture_rx);
    assert!(h.control_rx.try_recv().is_err());
}

/// 复审 P1-02：Writer control channel 关闭后 start/resume 均拒绝。
#[tokio::test]
async fn start_and_resume_rejected_when_control_channel_closed() {
    // start（Stopped → Running）。
    let h = harness(CaptureState::Stopped);
    drop(h.control_rx);
    let error = h
        .coordinator
        .apply_capture_command("capture_start", T0)
        .await
        .expect_err("control channel 关闭必须拒绝 start");
    assert_eq!(error.code, SafeErrorCode::InternalSafeError);
    assert_stopped_everywhere(&h.coordinator, &h.shared, &h.capture_rx);

    // resume（Paused → Running）。
    let h = harness(CaptureState::Paused);
    drop(h.control_rx);
    let error = h
        .coordinator
        .apply_capture_command("capture_resume", T0)
        .await
        .expect_err("control channel 关闭必须拒绝 resume");
    assert_eq!(error.code, SafeErrorCode::InternalSafeError);
    assert_stopped_everywhere(&h.coordinator, &h.shared, &h.capture_rx);
}

/// 复审 P1-02：Processor 独立退出（barrier/control channel 尚未关闭）时
/// 也不得虚假 Running——拒绝来自 RAII 健康状态而非 channel 状态。
#[tokio::test]
async fn start_rejected_when_processor_dead_but_channels_open() {
    let h = harness(CaptureState::Stopped);
    drop(h.health_guards.1); // Processor 任务退出（channel 全部仍开放）。
    assert_eq!(h.health.processor_state(), TaskLifecycle::Dead);
    assert!(h.health.capture_alive() && h.health.writer_alive());

    let error = h
        .coordinator
        .apply_capture_command("capture_start", T0)
        .await
        .expect_err("processor 死亡必须拒绝");
    assert_eq!(error.code, SafeErrorCode::InternalSafeError);
    assert_stopped_everywhere(&h.coordinator, &h.shared, &h.capture_rx);
    // channel 确实仍开放：拒绝只可能来自健康状态。
    assert!(!h.barrier_rx.is_closed());
    assert!(!h.control_rx.is_closed());
}

/// 复审 P1-02：capture watch 无消费者时 publish 不得返回成功；
/// shared/DTO/desired 全部保持 Stopped。
#[tokio::test]
async fn publish_fails_without_capture_watch_receiver() {
    // start：Running 发布被拒绝。
    let h = harness(CaptureState::Stopped);
    drop(h.capture_rx);
    let error = h
        .coordinator
        .apply_capture_command("capture_start", T0)
        .await
        .expect_err("无消费者必须拒绝");
    assert_eq!(error.code, SafeErrorCode::InternalSafeError);
    assert_eq!(h.coordinator.desired_state(), CaptureState::Stopped);
    assert_eq!(h.shared.capture_state(), CaptureState::Stopped);
    assert_eq!(h.shared.status_dto().capture_state, CaptureState::Stopped);

    // pause：冻结发布失败即安全停止，无 barrier/control。
    let h = harness(CaptureState::Running);
    drop(h.capture_rx);
    let error = h
        .coordinator
        .apply_capture_command("capture_pause", T0)
        .await
        .expect_err("无消费者必须拒绝");
    assert_eq!(error.code, SafeErrorCode::InternalSafeError);
    assert_eq!(h.shared.capture_state(), CaptureState::Stopped);
    assert_eq!(h.shared.status_dto().capture_state, CaptureState::Stopped);
    assert!(h.barrier_rx.is_empty());
    assert!(h.control_rx.is_empty());
}

/// 复审二 P2-01：Pause 边界已提交但最终发布失败（Capture Loop 在双 ack 期间退出）
/// → 返回 INTERNAL_SAFE_ERROR，安全停止，五处状态一致。
#[tokio::test]
async fn pause_final_publish_failure_returns_error_and_safe_stops() {
    let mut h = harness(CaptureState::Running);
    let cmd = tokio::spawn({
        let coordinator = h.coordinator.clone();
        async move { coordinator.apply_capture_command("capture_pause", T0).await }
    });
    let request = h.barrier_rx.recv().await.expect("barrier 请求必须到达");
    request.injected_ack.send(Ok(())).unwrap();
    let ack = recv_lifecycle_ack(&mut h.control_rx).await;
    // Capture Loop 在双 ack 期间退出：最终发布必然失败。
    drop(h.capture_rx);
    ack.send(Ok(())).unwrap(); // 生命周期边界已提交（不回滚）。

    let error = cmd
        .await
        .expect("任务不 panic")
        .expect_err("最终发布失败不得静默成功");
    assert_eq!(error.code, SafeErrorCode::InternalSafeError);
    assert!(
        error.message.contains("边界已提交"),
        "错误必须说明边界已提交: {}",
        error.message
    );
    // 安全停止：desired/effective/shared/DTO 一致且非 Running（watch 已无消费者）。
    assert_eq!(h.coordinator.desired_state(), CaptureState::Stopped);
    assert_eq!(h.coordinator.effective_state(), CaptureState::Stopped);
    assert_eq!(h.shared.capture_state(), CaptureState::Stopped);
    assert_eq!(h.shared.status_dto().capture_state, CaptureState::Stopped);
    assert_eq!(
        h.shared.errors().get(&ErrorSource::Writer),
        Some(&SafeErrorCode::InternalSafeError),
        "必须留下来源明确的诊断"
    );
}

/// 复审二 P2-01：Stop 边界已提交但最终发布失败 → 与 Pause 同一规则。
#[tokio::test]
async fn stop_final_publish_failure_returns_error_and_safe_stops() {
    let mut h = harness(CaptureState::Running);
    let cmd = tokio::spawn({
        let coordinator = h.coordinator.clone();
        async move { coordinator.apply_capture_command("capture_stop", T0).await }
    });
    let request = h.barrier_rx.recv().await.expect("barrier 请求必须到达");
    request.injected_ack.send(Ok(())).unwrap();
    let ack = recv_lifecycle_ack(&mut h.control_rx).await;
    drop(h.capture_rx);
    ack.send(Ok(())).unwrap(); // 边界已提交。

    let error = cmd
        .await
        .expect("任务不 panic")
        .expect_err("最终发布失败不得静默成功");
    assert_eq!(error.code, SafeErrorCode::InternalSafeError);
    assert!(error.message.contains("边界已提交"));
    assert_eq!(h.coordinator.desired_state(), CaptureState::Stopped);
    assert_eq!(h.coordinator.effective_state(), CaptureState::Stopped);
    assert_eq!(h.shared.capture_state(), CaptureState::Stopped);
    assert_eq!(h.shared.status_dto().capture_state, CaptureState::Stopped);
    assert_eq!(
        h.shared.errors().get(&ErrorSource::Writer),
        Some(&SafeErrorCode::InternalSafeError)
    );
}

/// 复审 P1-02 / 复审二 P2-02：settings watch 无消费者时，Writer ack 成功也不得
/// 报告完整成功；安全停止、保留真实 applied revision、留下 Settings/任务退出诊断。
#[tokio::test]
async fn settings_watch_undeliverable_after_commit_is_not_success() {
    let mut h = harness(CaptureState::Running);
    drop(h.settings_rx);
    let settings = Settings {
        revision: "1".to_string(),
        ..Settings::default()
    };
    let apply = tokio::spawn({
        let coordinator = h.coordinator.clone();
        async move { coordinator.apply_settings(settings, T0).await }
    });

    let request = h.barrier_rx.recv().await.expect("barrier 请求必须到达");
    request.injected_ack.send(Ok(())).unwrap();
    let ack = match h.control_rx.recv().await {
        Some(WriterControl::SettingsApplied { ack, .. }) => ack,
        _other => panic!("应为 SettingsApplied control"),
    };
    // 按真实 Writer 顺序（复审二 P2-02）：提交成功后先更新 applied revision，
    // 再发送成功 ack——模拟"DB 已提交但运行时消费者不可用"。
    h.shared.set_applied_settings_revision(1);
    ack.send(Ok(1)).unwrap();

    let error = apply
        .await
        .expect("任务不 panic")
        .expect_err("不得伪装完整成功");
    assert_eq!(error.code, SafeErrorCode::InternalSafeError);
    // 安全停止 + Settings 诊断；applied revision 保留（不得回退为 0）。
    assert_stopped_everywhere(&h.coordinator, &h.shared, &h.capture_rx);
    assert_eq!(
        h.shared.applied_settings_revision(),
        1,
        "DB 已提交的 applied revision 必须保留"
    );
    assert_eq!(
        h.shared.errors().get(&ErrorSource::Settings),
        Some(&SafeErrorCode::InternalSafeError)
    );
}

/// 复审 P1-02：control channel 关闭时，lifecycle/settings/system-event 三条路径的
/// control send 均稳定失败，且失败语义与路径一致。
#[tokio::test]
async fn control_send_failure_is_stable_on_all_paths() {
    // lifecycle（pause）：fail-closed 冻结在 Paused。
    let mut h = harness(CaptureState::Running);
    let cmd = tokio::spawn({
        let coordinator = h.coordinator.clone();
        async move { coordinator.apply_capture_command("capture_pause", T0).await }
    });
    let request = h.barrier_rx.recv().await.expect("barrier 请求必须到达");
    request.injected_ack.send(Ok(())).unwrap();
    drop(h.control_rx);
    let error = cmd.await.expect("任务不 panic").expect_err("send 必须失败");
    assert_eq!(error.code, SafeErrorCode::InternalSafeError);
    assert_eq!(*h.capture_rx.borrow(), CaptureState::Paused);
    assert_eq!(h.shared.capture_state(), CaptureState::Paused);
    assert_eq!(h.coordinator.desired_state(), CaptureState::Paused);

    // settings：writer 死亡 → 安全停止（不虚假 Running），last-known-good 不变。
    let mut h = harness(CaptureState::Running);
    let apply = tokio::spawn({
        let coordinator = h.coordinator.clone();
        async move {
            coordinator
                .apply_settings(
                    Settings {
                        revision: "1".to_string(),
                        ..Settings::default()
                    },
                    T0,
                )
                .await
        }
    });
    let request = h.barrier_rx.recv().await.expect("barrier 请求必须到达");
    request.injected_ack.send(Ok(())).unwrap();
    drop(h.control_rx);
    let error = apply
        .await
        .expect("任务不 panic")
        .expect_err("send 必须失败");
    assert_eq!(error.code, SafeErrorCode::InternalSafeError);
    assert_stopped_everywhere(&h.coordinator, &h.shared, &h.capture_rx);
    assert_eq!(h.settings_rx.borrow().revision, "0");
    assert_eq!(
        h.shared.errors().get(&ErrorSource::Settings),
        Some(&SafeErrorCode::SettingsSavedNotApplied)
    );

    // system event：Sleep suppression active → effective Paused（保持安全冻结）。
    let mut h = harness(CaptureState::Running);
    let ev = tokio::spawn({
        let coordinator = h.coordinator.clone();
        async move {
            coordinator
                .apply_system_lifecycle_event(SystemLifecycleEvent::Sleep { at_utc_ms: T0 })
                .await
        }
    });
    let request = h.barrier_rx.recv().await.expect("barrier 请求必须到达");
    request.injected_ack.send(Ok(())).unwrap();
    drop(h.control_rx);
    let error = ev.await.expect("任务不 panic").expect_err("send 必须失败");
    assert_eq!(error.code, SafeErrorCode::InternalSafeError);
    // Sleep suppression active → effective Paused；desired 保持 Running 用于重试
    assert_eq!(*h.capture_rx.borrow(), CaptureState::Paused);
    assert_eq!(h.coordinator.desired_state(), CaptureState::Running);
    assert_eq!(
        h.shared.errors().get(&ErrorSource::LifecyclePump),
        Some(&SafeErrorCode::InternalSafeError)
    );
}

/// control 已入队后 ack sender 被丢弃不能证明“未提交”：Writer 的真实顺序是
/// 先提交再 ack。三条路径都必须按结果未知锁存 writer fault，不得恢复 Running。
#[tokio::test]
async fn writer_ack_drop_fences_all_paths_as_unknown_outcome() {
    // lifecycle：结果未知，锁存 fatal 而不是普通 Paused 失败。
    let mut h = harness(CaptureState::Running);
    let cmd = tokio::spawn({
        let coordinator = h.coordinator.clone();
        async move { coordinator.apply_capture_command("capture_pause", T0).await }
    });
    let request = h.barrier_rx.recv().await.expect("barrier 请求必须到达");
    request.injected_ack.send(Ok(())).unwrap();
    let ack = recv_lifecycle_ack(&mut h.control_rx).await;
    drop(ack);
    let error = cmd
        .await
        .expect("任务不 panic")
        .expect_err("ack 断必须失败");
    assert_eq!(error.code, SafeErrorCode::AgentWriterFaulted);
    assert!(error.message.contains("结果未知"));
    assert_stopped_everywhere(&h.coordinator, &h.shared, &h.capture_rx);
    assert_eq!(h.shared.writer_state(), WriterState::Faulted);
    assert_eq!(h.shared.process_state(), ProcessState::Faulted);

    // settings：同样不得按“未提交”解冻恢复 Running。
    let mut h = harness(CaptureState::Running);
    let apply = tokio::spawn({
        let coordinator = h.coordinator.clone();
        async move {
            coordinator
                .apply_settings(
                    Settings {
                        revision: "1".to_string(),
                        ..Settings::default()
                    },
                    T0,
                )
                .await
        }
    });
    let request = h.barrier_rx.recv().await.expect("barrier 请求必须到达");
    request.injected_ack.send(Ok(())).unwrap();
    let ack = match h.control_rx.recv().await {
        Some(WriterControl::SettingsApplied { ack, .. }) => ack,
        _other => panic!("应为 SettingsApplied control"),
    };
    drop(ack);
    let error = apply
        .await
        .expect("任务不 panic")
        .expect_err("ack 断必须失败");
    assert_eq!(error.code, SafeErrorCode::AgentWriterFaulted);
    assert_stopped_everywhere(&h.coordinator, &h.shared, &h.capture_rx);
    assert_eq!(h.settings_rx.borrow().revision, "0");
    assert_eq!(
        h.shared.errors().get(&ErrorSource::Writer),
        Some(&SafeErrorCode::AgentWriterFaulted)
    );
    assert_eq!(h.shared.writer_state(), WriterState::Faulted);
    assert_eq!(h.shared.process_state(), ProcessState::Faulted);

    // system event：不能声称边界未提交，也不能恢复 Running。
    let mut h = harness(CaptureState::Running);
    let ev = tokio::spawn({
        let coordinator = h.coordinator.clone();
        async move {
            coordinator
                .apply_system_lifecycle_event(SystemLifecycleEvent::Sleep { at_utc_ms: T0 })
                .await
        }
    });
    let request = h.barrier_rx.recv().await.expect("barrier 请求必须到达");
    request.injected_ack.send(Ok(())).unwrap();
    let ack = recv_lifecycle_ack(&mut h.control_rx).await;
    drop(ack);
    let error = ev.await.expect("任务不 panic").expect_err("ack 断必须失败");
    assert_eq!(error.code, SafeErrorCode::AgentWriterFaulted);
    assert_stopped_everywhere(&h.coordinator, &h.shared, &h.capture_rx);
    assert_eq!(
        h.shared.errors().get(&ErrorSource::Writer),
        Some(&SafeErrorCode::AgentWriterFaulted)
    );
    assert_eq!(h.shared.writer_state(), WriterState::Faulted);
    assert_eq!(h.shared.process_state(), ProcessState::Faulted);
}

/// 并发 Stop + Settings：与 Pause 版本同一组不变量（阶段 4.3.1 §三A）。
#[tokio::test]
async fn concurrent_stop_and_settings_are_strictly_serialized() {
    let mut h = harness(CaptureState::Running);
    let settings = Settings {
        revision: "1".to_string(),
        ..Settings::default()
    };
    let stop = tokio::spawn({
        let coordinator = h.coordinator.clone();
        async move { coordinator.apply_capture_command("capture_stop", T0).await }
    });
    let apply = tokio::spawn({
        let coordinator = h.coordinator.clone();
        async move { coordinator.apply_settings(settings, T0).await }
    });

    let first = h.barrier_rx.recv().await.expect("第一个 barrier 请求");
    let leaked = tokio::time::timeout(Duration::from_millis(100), h.barrier_rx.recv()).await;
    assert!(leaked.is_err(), "同一时刻只允许一个 transition");
    let first_id = first.token.id.clone();
    first.injected_ack.send(Ok(())).unwrap();
    ack_next_control(&mut h.control_rx).await;

    let second = tokio::time::timeout(Duration::from_secs(1), h.barrier_rx.recv())
        .await
        .expect("第二个 barrier 请求必须到达")
        .expect("通道不得关闭");
    assert_ne!(second.token.id, first_id, "BarrierId 不得复用");
    second.injected_ack.send(Ok(())).unwrap();
    ack_next_control(&mut h.control_rx).await;

    let stop_result = stop.await.expect("stop 不 panic");
    let apply_result = apply.await.expect("settings 不 panic");
    assert!(stop_result.is_ok(), "stop 必须成功: {stop_result:?}");
    assert_eq!(apply_result, Ok(1));
    assert_eq!(h.shared.capture_state(), CaptureState::Stopped);
    assert_eq!(h.settings_rx.borrow().revision, "1");
}

/// 并发 System event + Pause：同一 transition lock 严格串行（阶段 4.3.1 §三A；
/// 两条都是 Lifecycle kind，以事件变体与 BarrierId 区分）。
#[tokio::test]
async fn concurrent_system_event_and_pause_are_strictly_serialized() {
    let mut h = harness(CaptureState::Running);
    let sleep = tokio::spawn({
        let coordinator = h.coordinator.clone();
        async move {
            coordinator
                .apply_system_lifecycle_event(SystemLifecycleEvent::Sleep { at_utc_ms: T0 })
                .await
        }
    });
    let pause = tokio::spawn({
        let coordinator = h.coordinator.clone();
        async move { coordinator.apply_capture_command("capture_pause", T0).await }
    });

    let first = h.barrier_rx.recv().await.expect("第一个 barrier 请求");
    let leaked = tokio::time::timeout(Duration::from_millis(100), h.barrier_rx.recv()).await;
    assert!(leaked.is_err(), "同一时刻只允许一个 transition");
    let first_id = first.token.id.clone();
    first.injected_ack.send(Ok(())).unwrap();
    ack_next_control(&mut h.control_rx).await;

    let second = tokio::time::timeout(Duration::from_secs(1), h.barrier_rx.recv())
        .await
        .expect("第二个 barrier 请求必须到达")
        .expect("通道不得关闭");
    assert_ne!(second.token.id, first_id, "BarrierId 不得复用");
    second.injected_ack.send(Ok(())).unwrap();
    ack_next_control(&mut h.control_rx).await;

    let sleep_result = sleep.await.expect("system event 不 panic");
    let pause_result = pause.await.expect("pause 不 panic");
    assert!(
        sleep_result.is_ok(),
        "system event 必须成功: {sleep_result:?}"
    );
    assert!(pause_result.is_ok(), "pause 必须成功: {pause_result:?}");
    assert_eq!(h.shared.capture_state(), CaptureState::Paused);
}

/// 明确强制 rev1 先取得 transition lock：rev2 必须在 rev1 提交后读取 expected=1。
#[tokio::test]
async fn concurrent_settings_rev1_first_reads_revision_under_lock() {
    let mut h = harness(CaptureState::Running);
    let rev1 = Settings {
        revision: "1".to_string(),
        ..Settings::default()
    };
    let rev2 = Settings {
        revision: "2".to_string(),
        ..Settings::default()
    };
    let apply1 = tokio::spawn({
        let coordinator = h.coordinator.clone();
        async move { coordinator.apply_settings(rev1, T0).await }
    });
    let first = h.barrier_rx.recv().await.expect("rev1 barrier 必须到达");
    assert_eq!(first.token.expected_revision, 0);

    // rev1 已持锁后才启动 rev2，消除调度偶然性。
    let apply2 = tokio::spawn({
        let coordinator = h.coordinator.clone();
        async move { coordinator.apply_settings(rev2, T0).await }
    });
    assert!(
        tokio::time::timeout(Duration::from_millis(100), h.barrier_rx.recv())
            .await
            .is_err(),
        "rev1 ack 前 rev2 不得进入 transition"
    );

    let first_id = first.token.id.clone();
    first.injected_ack.send(Ok(())).unwrap();
    let ack = match h.control_rx.recv().await {
        Some(WriterControl::SettingsApplied { settings, ack, .. }) => {
            assert_eq!(settings.revision, "1");
            ack
        }
        _other => panic!("应为 SettingsApplied control"),
    };
    h.shared.set_applied_settings_revision(1);
    ack.send(Ok(1)).unwrap();

    let second = h.barrier_rx.recv().await.expect("rev2 barrier 必须到达");
    assert_ne!(second.token.id, first_id, "BarrierId 不得复用");
    assert_eq!(second.token.expected_revision, 1);
    second.injected_ack.send(Ok(())).unwrap();
    let ack = match h.control_rx.recv().await {
        Some(WriterControl::SettingsApplied { settings, ack, .. }) => {
            assert_eq!(settings.revision, "2");
            ack
        }
        _other => panic!("应为 SettingsApplied control"),
    };
    h.shared.set_applied_settings_revision(2);
    ack.send(Ok(2)).unwrap();
    assert_eq!(apply1.await.expect("apply1 不 panic"), Ok(1));
    assert_eq!(apply2.await.expect("apply2 不 panic"), Ok(2));
}

/// 明确强制 rev2 先取得锁：rev1 必须在锁内复检为降级并拒绝，且不得产生
/// 第二条 barrier/control。该分支过去的断言写反且从未被调度触发。
#[tokio::test]
async fn concurrent_settings_rev2_first_rejects_rev1_deterministically() {
    let mut h = harness(CaptureState::Running);
    let rev2 = Settings {
        revision: "2".to_string(),
        ..Settings::default()
    };
    let apply2 = tokio::spawn({
        let coordinator = h.coordinator.clone();
        async move { coordinator.apply_settings(rev2, T0).await }
    });
    let first = h.barrier_rx.recv().await.expect("rev2 barrier 必须到达");
    assert_eq!(first.token.expected_revision, 0);

    let rev1 = Settings {
        revision: "1".to_string(),
        ..Settings::default()
    };
    let apply1 = tokio::spawn({
        let coordinator = h.coordinator.clone();
        async move { coordinator.apply_settings(rev1, T0).await }
    });
    first.injected_ack.send(Ok(())).unwrap();
    let ack = match h.control_rx.recv().await {
        Some(WriterControl::SettingsApplied { settings, ack, .. }) => {
            assert_eq!(settings.revision, "2");
            ack
        }
        _other => panic!("应为 SettingsApplied control"),
    };
    h.shared.set_applied_settings_revision(2);
    ack.send(Ok(2)).unwrap();

    assert_eq!(apply2.await.expect("apply2 不 panic"), Ok(2));
    let conflict = apply1
        .await
        .expect("apply1 不 panic")
        .expect_err("rev1 必须按降级拒绝");
    assert_eq!(conflict.code, SafeErrorCode::SettingsConflict);
    assert!(h.barrier_rx.try_recv().is_err());
    assert!(h.control_rx.try_recv().is_err());
    assert_eq!(h.shared.applied_settings_revision(), 2);
}

/// 阶段 4.3.1 §三B：SystemSleep 边界已提交但最终 publish 失败——
/// 返回 INTERNAL_SAFE_ERROR（说明边界已提交）、shared/DTO/effective 安全停止。
#[tokio::test]
async fn system_event_final_publish_failure_returns_error_and_safe_stops() {
    let mut h = harness(CaptureState::Running);
    let event_task = tokio::spawn({
        let coordinator = h.coordinator.clone();
        async move {
            coordinator
                .apply_system_lifecycle_event(SystemLifecycleEvent::Sleep { at_utc_ms: T0 })
                .await
        }
    });
    let request = h.barrier_rx.recv().await.expect("barrier 请求必须到达");
    request.injected_ack.send(Ok(())).unwrap();
    let ack = recv_lifecycle_ack(&mut h.control_rx).await;
    // Capture consumer 在最终 publish 前退出。
    drop(h.capture_rx);
    ack.send(Ok(())).unwrap(); // 边界已提交（不回滚）。

    let error = event_task
        .await
        .expect("任务不 panic")
        .expect_err("最终发布失败不得静默成功");
    assert_eq!(error.code, SafeErrorCode::InternalSafeError);
    assert!(
        error.message.contains("已提交"),
        "错误必须说明边界已提交但流水线不可用: {}",
        error.message
    );
    assert_eq!(h.coordinator.desired_state(), CaptureState::Stopped);
    assert_eq!(h.coordinator.effective_state(), CaptureState::Stopped);
    assert_eq!(h.shared.capture_state(), CaptureState::Stopped);
    assert_eq!(h.shared.status_dto().capture_state, CaptureState::Stopped);
    assert_eq!(
        h.shared.errors().get(&ErrorSource::LifecyclePump),
        Some(&SafeErrorCode::InternalSafeError)
    );
}

/// 阶段 4.3.1 §三B：Settings 已提交（DB/applied 前进、settings watch 有新 revision），
/// 但最终解除 transition 的 Running 发布失败——INTERNAL_SAFE_ERROR、applied 保留、
/// settings watch 保持新 revision、capture/shared/DTO 安全停止。
#[tokio::test]
async fn settings_final_publish_failure_keeps_committed_revision_and_safe_stops() {
    let mut h = harness(CaptureState::Running);
    let settings = Settings {
        revision: "1".to_string(),
        ..Settings::default()
    };
    let apply = tokio::spawn({
        let coordinator = h.coordinator.clone();
        async move { coordinator.apply_settings(settings, T0).await }
    });
    let request = h.barrier_rx.recv().await.expect("barrier 请求必须到达");
    request.injected_ack.send(Ok(())).unwrap();
    let ack = match h.control_rx.recv().await {
        Some(WriterControl::SettingsApplied { ack, .. }) => ack,
        _other => panic!("应为 SettingsApplied control"),
    };
    // Capture consumer 在最终 publish 前退出；settings watch 仍有消费者。
    drop(h.capture_rx);
    // 按真实 Writer 顺序：先更新 applied，再发送成功 ack。
    h.shared.set_applied_settings_revision(1);
    ack.send(Ok(1)).unwrap();

    let error = apply
        .await
        .expect("任务不 panic")
        .expect_err("最终发布失败不得静默成功");
    assert_eq!(error.code, SafeErrorCode::InternalSafeError);
    // applied revision 保持已提交值，不得回退。
    assert_eq!(h.shared.applied_settings_revision(), 1);
    // settings watch 保持新 revision（已交付）。
    assert_eq!(h.settings_rx.borrow().revision, "1");
    // capture/shared/DTO/desired/effective 安全停止。
    assert_eq!(h.coordinator.desired_state(), CaptureState::Stopped);
    assert_eq!(h.coordinator.effective_state(), CaptureState::Stopped);
    assert_eq!(h.shared.capture_state(), CaptureState::Stopped);
    assert_eq!(h.shared.status_dto().capture_state, CaptureState::Stopped);
    assert_eq!(
        h.shared.errors().get(&ErrorSource::Settings),
        Some(&SafeErrorCode::InternalSafeError)
    );
}

/// 阶段 4.3.1 §四C：Writer ack 超过 operation deadline（paused 8s）——
/// 提交结果未知：锁存 writer_fault、强制 Stopped、后续全部 transition 被拒绝、
/// Writer 迟到完成也不解除。
#[tokio::test(start_paused = true)]
async fn writer_ack_timeout_fences_all_transitions() {
    let mut h = harness(CaptureState::Running);
    let cmd = tokio::spawn({
        let coordinator = h.coordinator.clone();
        async move { coordinator.apply_capture_command("capture_pause", T0).await }
    });
    let request = h.barrier_rx.recv().await.expect("barrier 请求必须到达");
    request.injected_ack.send(Ok(())).unwrap();
    let ack = recv_lifecycle_ack(&mut h.control_rx).await;

    // 不 ack：paused 时钟推进超过 8s operation deadline。
    let error = cmd.await.expect("任务不 panic").expect_err("必须超时失败");
    assert_eq!(error.code, SafeErrorCode::AgentWriterFaulted);
    assert!(
        error.message.contains("结果未知"),
        "必须是提交结果未知语义: {}",
        error.message
    );
    assert_stopped_everywhere(&h.coordinator, &h.shared, &h.capture_rx);
    assert_eq!(
        h.shared.errors().get(&ErrorSource::Writer),
        Some(&SafeErrorCode::AgentWriterFaulted)
    );
    assert_eq!(h.shared.writer_state(), WriterState::Faulted);
    assert_eq!(h.shared.process_state(), ProcessState::Faulted);

    // fencing：start/resume/settings/system-event 全部拒绝，零副作用。
    let error = h
        .coordinator
        .apply_capture_command("capture_start", T0)
        .await
        .expect_err("start 必须被拒绝");
    assert_eq!(error.code, SafeErrorCode::AgentWriterFaulted);
    let error = h
        .coordinator
        .apply_settings(
            Settings {
                revision: "1".to_string(),
                ..Settings::default()
            },
            T0,
        )
        .await
        .expect_err("settings 必须被拒绝");
    assert_eq!(error.code, SafeErrorCode::AgentWriterFaulted);
    let error = h
        .coordinator
        .apply_system_lifecycle_event(SystemLifecycleEvent::Sleep { at_utc_ms: T0 })
        .await
        .expect_err("system event 必须被拒绝");
    assert_eq!(error.code, SafeErrorCode::AgentWriterFaulted);
    assert!(h.barrier_rx.is_empty(), "fencing 后不得产生新 barrier");
    assert!(h.control_rx.is_empty(), "fencing 后不得产生新 control");

    // Writer 迟到完成：不得解除 writer_fault，也不允许新控制与其交错。
    let _ = ack.send(Ok(()));
    let error = h
        .coordinator
        .apply_capture_command("capture_start", T0)
        .await
        .expect_err("迟到完成不得解除 fencing");
    assert_eq!(error.code, SafeErrorCode::AgentWriterFaulted);
    assert_stopped_everywhere(&h.coordinator, &h.shared, &h.capture_rx);
}

/// control 已确定入队后，Coordinator future 被取消仍可能有迟到提交；RAII phase
/// guard 必须在 abort 展开时把结果标为 unknown 并锁存 fatal。
#[tokio::test]
async fn abort_after_control_acceptance_fences_late_side_effect() {
    let mut h = harness(CaptureState::Running);
    let command = tokio::spawn({
        let coordinator = h.coordinator.clone();
        async move { coordinator.apply_capture_command("capture_pause", T0).await }
    });
    let request = h.barrier_rx.recv().await.expect("barrier 请求必须到达");
    request.injected_ack.send(Ok(())).unwrap();
    // 从真实 control receiver 取到消息，建立“Writer lane 已接受”的 rendezvous。
    let ack = recv_lifecycle_ack(&mut h.control_rx).await;

    command.abort();
    let outcome = command.await;
    assert!(outcome.is_err(), "取消后的 transition 不得正常返回");
    assert_stopped_everywhere(&h.coordinator, &h.shared, &h.capture_rx);
    assert_eq!(h.shared.writer_state(), WriterState::Faulted);
    assert_eq!(h.shared.process_state(), ProcessState::Faulted);
    assert_eq!(
        h.shared.errors().get(&ErrorSource::Writer),
        Some(&SafeErrorCode::AgentWriterFaulted)
    );

    // Writer 迟到提交/ack 不能解除 fencing。
    let _ = ack.send(Ok(()));
    let error = h
        .coordinator
        .apply_capture_command("capture_start", T0)
        .await
        .expect_err("迟到 ack 后仍必须拒绝");
    assert_eq!(error.code, SafeErrorCode::AgentWriterFaulted);
}

/// 阶段 4.3.1 §四B：injected ack 超过 deadline（paused 3s，Capture Loop 持有请求
/// 但不 ack）——释放 transition lock、不发送 control、来源明确诊断；
/// 管线恢复后命令仍可执行（证明锁已释放）。
#[tokio::test(start_paused = true)]
async fn inject_ack_timeout_releases_lock_without_control() {
    let mut h = harness(CaptureState::Running);
    // Capture Loop 持有请求但暂不 ack（消费但不确认）。
    let (hold_tx, mut hold_rx) = mpsc::channel(8);
    let mut barrier_rx = h.barrier_rx;
    let forward = tokio::spawn(async move {
        while let Some(request) = barrier_rx.recv().await {
            if hold_tx.send(request).await.is_err() {
                break;
            }
        }
    });

    let error = h
        .coordinator
        .apply_capture_command("capture_pause", T0)
        .await
        .expect_err("ack 超时必须失败");
    assert_eq!(error.code, SafeErrorCode::InternalSafeError);
    assert!(
        error.message.contains("AckTimeout"),
        "必须是 injected ack timeout: {}",
        error.message
    );
    // 不发送 control；fail-closed 一致冻结。
    assert!(h.control_rx.try_recv().is_err());
    assert_eq!(h.shared.capture_state(), CaptureState::Paused);
    assert_eq!(*h.capture_rx.borrow(), CaptureState::Paused);

    // transition lock 已释放（AckTimeout 后 ack receiver 已按协议销毁，
    // 暂存的请求无法再被迟到确认——这正是"不声称未注入"语义）。
    let parked = hold_rx.recv().await.expect("暂存的请求");
    assert!(
        parked.injected_ack.send(Ok(())).is_err(),
        "AckTimeout 后 ack receiver 必须已销毁"
    );
    let resumed = h
        .coordinator
        .apply_capture_command("capture_resume", T0)
        .await
        .expect("lock 已释放，resume 必须成功");
    assert_eq!(resumed, CaptureState::Running);
    forward.abort();
}

/// 阶段 4.3.1 §四A：request channel 满且消费端不前进时，send 在服务端期限内
/// 稳定失败（SendTimeout，paused 2s），请求保证未进入 channel，不发送 control。
#[tokio::test(start_paused = true)]
async fn barrier_send_timeout_when_channel_full_and_consumer_stuck() {
    let h = harness(CaptureState::Running);
    // 容量 1 的请求 channel：第一个请求占住且永远不被消费。
    let (tiny_tx, mut tiny_rx) = wuji_rebuild_agent::barrier::barrier_request_channel(1);
    let (control_tx, _control_rx) = mpsc::channel(8);
    let (settings_tx, _settings_rx) = watch::channel(Settings::default());
    // control/settings/watch/health 复用 harness；仅 barrier channel 换为容量 1。
    let coordinator = Arc::new(CaptureCoordinator::new(
        tiny_tx.clone(),
        h.capture_tx.clone(),
        control_tx,
        h.shared.clone(),
        settings_tx,
        CaptureState::Running,
        h.health.clone(),
    ));
    // 占满容量 1（直接发送一个裸请求，不消费）。
    let (ack_tx, _ack_rx) = tokio::sync::oneshot::channel();
    tiny_tx
        .send(BarrierRequest {
            token: wuji_core::pipeline::BarrierToken {
                id: wuji_core::pipeline::BarrierId::new(),
                kind: BarrierKind::Lifecycle,
                expected_revision: 0,
            },
            injected_ack: ack_tx,
        })
        .await
        .unwrap();

    // paused 2s 后 send 超时：请求未进入 channel（容量仍被第一个占住）。
    let error = coordinator
        .apply_capture_command("capture_pause", T0)
        .await
        .expect_err("send 超时必须失败");
    assert_eq!(error.code, SafeErrorCode::InternalSafeError);
    assert!(
        error.message.contains("SendTimeout"),
        "必须是 request send timeout: {}",
        error.message
    );
    // channel 中只有第一个请求（证明超时的请求未进入）。
    let first = tiny_rx.recv().await.expect("第一个请求必须仍在");
    assert!(matches!(first.token.kind, BarrierKind::Lifecycle));
    assert!(
        tiny_rx.try_recv().is_err(),
        "channel 中不得有第二个（超时的）请求"
    );
}

/// Writer control lane 被 Maintenance 占满且 Writer 不前进时，Coordinator 必须
/// 在 2s send deadline 内返回；permit 未取得，所以 transition control 未入队，
/// 不能等到 8s ack deadline，更不能永久占用 transition lock。
#[tokio::test(start_paused = true)]
async fn full_stuck_control_lane_times_out_before_enqueue() {
    let shared = Arc::new(SharedState::new("0.1.0".to_string(), RuntimeId::new()));
    shared.set_capture_state(CaptureState::Running);
    let health = PipelineHealth::new();
    let _guards = (
        health.register_capture(),
        health.register_processor(),
        health.register_writer(),
    );
    let (barrier_tx, mut barrier_rx) = wuji_rebuild_agent::barrier::barrier_request_channel(1);
    let (capture_tx, capture_rx) = watch::channel(CaptureState::Running);
    let (control_tx, mut control_rx) = mpsc::channel(1);
    let (settings_tx, _settings_rx) = watch::channel(Settings::default());
    let coordinator = Arc::new(CaptureCoordinator::new(
        barrier_tx,
        capture_tx,
        control_tx.clone(),
        shared.clone(),
        settings_tx,
        CaptureState::Running,
        health,
    ));

    // Maintenance 先占满唯一容量，接收端保持存活但永不排空。
    control_tx.send(WriterControl::Checkpoint).await.unwrap();
    let command = tokio::spawn({
        let coordinator = coordinator.clone();
        async move { coordinator.apply_capture_command("capture_pause", T0).await }
    });
    let request = barrier_rx.recv().await.expect("barrier 必须先注入");
    request.injected_ack.send(Ok(())).unwrap();

    let error = command
        .await
        .expect("任务不 panic")
        .expect_err("control reserve 必须超时");
    assert_eq!(error.code, SafeErrorCode::InternalSafeError);
    assert!(
        error.message.contains("Timeout"),
        "必须区分未入队的 send timeout: {}",
        error.message
    );
    assert!(matches!(
        control_rx.recv().await,
        Some(WriterControl::Checkpoint)
    ));
    assert!(control_rx.try_recv().is_err(), "Lifecycle control 不得入队");
    assert_eq!(*capture_rx.borrow(), CaptureState::Paused);
    assert_eq!(shared.capture_state(), CaptureState::Paused);
    assert_ne!(shared.writer_state(), WriterState::Faulted);
}
