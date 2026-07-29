//! 第二次复审 P1：PipelineHealth 状态机与"首次 poll 前 abort"的失败闭环。
//!
//! 关键时序（current_thread 测试运行时）：`spawn` 后不 yield、立即 `abort`，
//! 被 abort 的 future 从未被 poll——健康状态仍必须变为 Dead，因为 guard 在
//! `tokio::spawn` 之前已同步创建并被 future 捕获。

use std::sync::Arc;

use tempfile::TempDir;
use tokio::sync::{mpsc, watch};
use wuji_core::domain::{CaptureState, ProcessState, WriterState};
use wuji_core::dto::RuntimeId;
use wuji_core::error::SafeErrorCode;
use wuji_core::pipeline::IdleReading;
use wuji_core::settings::Settings;
use wuji_rebuild_agent::activity::ActivityEngine;
use wuji_rebuild_agent::capture_coordinator::CaptureCoordinator;
use wuji_rebuild_agent::capture_loop::{
    CaptureLoopConfig, CaptureSource, ContinuityState, RawSample, spawn_capture_loop,
};
use wuji_rebuild_agent::pipeline_health::{PipelineHealth, PipelineTask, TaskLifecycle};
use wuji_rebuild_agent::processor_task::spawn_observation_processor;
use wuji_rebuild_agent::shared::SharedState;
use wuji_rebuild_agent::writer_task::WriterTask;
use wuji_storage::Writer;

const T0: i64 = 1_784_332_800_000;
const SHANGHAI: &str = "Asia/Shanghai";

struct NullSource;

impl CaptureSource for NullSource {
    fn capture(&self) -> RawSample {
        RawSample {
            process_file_name: None,
            idle: IdleReading::Unavailable,
        }
    }
}

/// Capture task：注册发生在 spawn 前（立即可见 Alive）；spawn 后不 yield、
/// 立即 abort（首次 poll 前）→ 状态必须变为 Dead。
#[tokio::test(flavor = "current_thread")]
async fn capture_abort_before_first_poll_marks_dead() {
    let health = PipelineHealth::new();
    assert_eq!(health.capture_state(), TaskLifecycle::NotStarted);

    let (_settings_tx, settings_rx) = watch::channel(Settings::default());
    let (_state_tx, state_rx) = watch::channel(CaptureState::Stopped);
    let (_barrier_tx, barrier_rx) = wuji_rebuild_agent::barrier::barrier_request_channel(4);
    let (pipeline_rx, handle) = spawn_capture_loop(
        NullSource,
        settings_rx,
        state_rx,
        Arc::new(ContinuityState::default()),
        CaptureLoopConfig::default(),
        barrier_rx,
        &health,
    );
    // 注册在 spawn 前同步完成（第二次复审 P1）。
    assert_eq!(health.capture_state(), TaskLifecycle::Alive);

    handle.abort(); // 不 yield：首次 poll 前 abort。
    let outcome = handle.await;
    assert!(outcome.is_err(), "abort 的任务不得正常返回");
    assert_eq!(
        health.capture_state(),
        TaskLifecycle::Dead,
        "首次 poll 前 abort 必须经 future 捕获的 guard 标记 Dead"
    );
    assert!(!health.all_alive());
    drop(pipeline_rx);
}

/// Processor task：同样的 spawn 前注册 + 首次 poll 前 abort → Dead。
#[tokio::test(flavor = "current_thread")]
async fn processor_abort_before_first_poll_marks_dead() {
    let health = PipelineHealth::new();
    assert_eq!(health.processor_state(), TaskLifecycle::NotStarted);

    let (pipeline_tx, pipeline_rx) = mpsc::channel::<wuji_core::pipeline::CapturePipelineItem>(8);
    let (_settings_tx, settings_rx) = watch::channel(Settings::default());
    let (data_rx, handle) = spawn_observation_processor(
        pipeline_rx,
        settings_rx,
        Arc::new(ContinuityState::default()),
        &health,
    );
    assert_eq!(health.processor_state(), TaskLifecycle::Alive);

    handle.abort(); // 不 yield：首次 poll 前 abort。
    let outcome = handle.await;
    assert!(outcome.is_err());
    assert_eq!(
        health.processor_state(),
        TaskLifecycle::Dead,
        "首次 poll 前 abort 必须经 future 捕获的 guard 标记 Dead"
    );
    drop((pipeline_tx, data_rx));
}

/// Writer task：`into_run_future` 在返回 future 前同步注册；spawn 后不 yield、
/// 立即 abort（首次 poll 前）→ 状态必须变为 Dead。
#[tokio::test(flavor = "current_thread")]
async fn writer_abort_before_first_poll_marks_dead() {
    let health = PipelineHealth::new();
    assert_eq!(health.writer_state(), TaskLifecycle::NotStarted);

    let dir = TempDir::new().unwrap();
    let db_path = dir.path().join("wuji-rebuild-v0.1.db");
    Writer::bootstrap_with_timezone(&db_path, SHANGHAI, T0).unwrap();
    let continuity = Arc::new(ContinuityState::default());
    let runtime_id = RuntimeId::new();
    let writer = Writer::open_existing(&db_path).unwrap();
    let engine =
        ActivityEngine::new(runtime_id.clone(), Settings::default(), continuity.clone()).unwrap();
    let shared = Arc::new(SharedState::new("0.1.0".to_string(), runtime_id));
    let (capture_state_tx, _capture_rx) = watch::channel(CaptureState::Stopped);
    let task = WriterTask::new(
        writer,
        engine,
        shared,
        capture_state_tx,
        continuity,
        dir.path().join("config"),
        health.clone(),
    );
    let (_data_tx, data_rx) = mpsc::channel(8);
    let (_control_tx, control_rx) = mpsc::channel(8);

    // into_run_future 在返回 future 前同步注册（尚未 spawn 已 Alive）。
    let future = task.into_run_future(data_rx, control_rx);
    assert_eq!(health.writer_state(), TaskLifecycle::Alive);

    let handle = tokio::spawn(future);
    handle.abort(); // 不 yield：首次 poll 前 abort。
    let outcome = handle.await;
    assert!(outcome.is_err());
    assert_eq!(
        health.writer_state(),
        TaskLifecycle::Dead,
        "首次 poll 前 abort 必须经 future 捕获的 guard 标记 Dead"
    );
}

/// assemble() 返回后、三个任务尚未注册时，Coordinator 不得允许 Running
/// （NotStarted 不是健康状态），状态保持 Stopped。
#[tokio::test]
async fn assemble_without_registered_tasks_rejects_start() {
    let shared = Arc::new(SharedState::new("0.1.0".to_string(), RuntimeId::new()));
    let plane = wuji_rebuild_agent::control_plane::assemble(
        shared.clone(),
        Settings::default(),
        CaptureState::Stopped,
    );
    assert!(!plane.health.all_alive(), "未注册时不得全部健康");

    let error = plane
        .coordinator
        .apply_capture_command("capture_start", T0)
        .await
        .expect_err("任务未注册必须拒绝 start");
    assert_eq!(error.code, SafeErrorCode::InternalSafeError);
    assert_eq!(shared.capture_state(), CaptureState::Stopped);
    assert_eq!(shared.status_dto().capture_state, CaptureState::Stopped);
    assert_eq!(plane.coordinator.desired_state(), CaptureState::Stopped);
    assert_eq!(plane.coordinator.effective_state(), CaptureState::Stopped);

    // resume 同样拒绝（desired 为 Paused 才能进入转换；健康检查在发布前拦截）。
    let shared2 = Arc::new(SharedState::new("0.1.0".to_string(), RuntimeId::new()));
    shared2.set_capture_state(CaptureState::Paused);
    let plane2 = wuji_rebuild_agent::control_plane::assemble(
        shared2.clone(),
        Settings::default(),
        CaptureState::Paused,
    );
    let error = plane2
        .coordinator
        .apply_capture_command("capture_resume", T0)
        .await
        .expect_err("任务未注册必须拒绝 resume");
    assert_eq!(error.code, SafeErrorCode::InternalSafeError);
    assert_eq!(shared2.capture_state(), CaptureState::Stopped);
}

/// Processor 首次 poll 前 abort，但 barrier/control channel 与 capture watch
/// 仍全部开放时，`capture_start` 必须被拒绝且保持 Stopped——证明拒绝来自
/// RAII 健康状态而非 channel 关闭。
#[tokio::test(flavor = "current_thread")]
async fn processor_abort_before_first_poll_blocks_capture_start() {
    let health = PipelineHealth::new();
    let shared = Arc::new(SharedState::new("0.1.0".to_string(), RuntimeId::new()));
    let (barrier_tx, mut barrier_rx) = wuji_rebuild_agent::barrier::barrier_request_channel(8);
    let (capture_state_tx, capture_rx) = watch::channel(CaptureState::Stopped);
    let (control_tx, _control_rx) = mpsc::channel(8);
    let (settings_tx, settings_rx) = watch::channel(Settings::default());
    // capture/writer 正常注册存活。
    let _capture_guard = health.register_capture();
    let _writer_guard = health.register_writer();

    // 模拟 Capture Loop 的 barrier 应答（channel 保持开放）。
    tokio::spawn(async move {
        while let Some(request) = barrier_rx.recv().await {
            let _ = request.injected_ack.send(Ok(()));
        }
    });

    let coordinator = CaptureCoordinator::new(
        barrier_tx,
        capture_state_tx,
        control_tx,
        shared.clone(),
        settings_tx,
        CaptureState::Stopped,
        health.clone(),
    );

    // Processor spawn 后不 yield、立即 abort（首次 poll 前）。
    let (pipeline_tx, pipeline_rx) = mpsc::channel::<wuji_core::pipeline::CapturePipelineItem>(8);
    let (data_rx, processor_handle) = spawn_observation_processor(
        pipeline_rx,
        settings_rx,
        Arc::new(ContinuityState::default()),
        &health,
    );
    assert_eq!(health.processor_state(), TaskLifecycle::Alive);
    processor_handle.abort();
    let _ = processor_handle.await;
    assert_eq!(health.processor_state(), TaskLifecycle::Dead);

    let error = coordinator
        .apply_capture_command("capture_start", T0)
        .await
        .expect_err("processor 死亡必须拒绝 start");
    assert_eq!(error.code, SafeErrorCode::InternalSafeError);

    // 五处状态全部 Stopped；channel 仍然开放（拒绝来自健康状态）。
    assert_eq!(coordinator.desired_state(), CaptureState::Stopped);
    assert_eq!(coordinator.effective_state(), CaptureState::Stopped);
    assert_eq!(*capture_rx.borrow(), CaptureState::Stopped);
    assert_eq!(shared.capture_state(), CaptureState::Stopped);
    assert_eq!(shared.status_dto().capture_state, CaptureState::Stopped);
    drop((pipeline_tx, data_rx));
}

/// 生产装配的退出事件必须主动经 Coordinator 收敛状态；即使任务在 supervisor
/// 首次 poll 前已经退出，事件也已排队，不得继续虚假显示 Running。
async fn assert_supervisor_fails_closed(task: PipelineTask) {
    let shared = Arc::new(SharedState::new("0.1.0".to_string(), RuntimeId::new()));
    shared.set_capture_state(CaptureState::Running);
    shared.set_process_state(ProcessState::Running);
    let plane = wuji_rebuild_agent::control_plane::assemble(
        shared.clone(),
        Settings::default(),
        CaptureState::Running,
    );
    let mut capture_guard = Some(plane.health.register_capture());
    let mut processor_guard = Some(plane.health.register_processor());
    let mut writer_guard = Some(plane.health.register_writer());
    assert!(plane.health.all_alive());
    let capture_rx = plane.capture_state_rx.clone();

    // 先退出、后启动 supervisor：验证生产启动窗口也不会漏事件。
    match task {
        PipelineTask::Capture => drop(capture_guard.take()),
        PipelineTask::Processor => drop(processor_guard.take()),
        PipelineTask::Writer => drop(writer_guard.take()),
    }
    let coordinator = plane.coordinator.clone();
    let supervisor = tokio::spawn(wuji_rebuild_agent::control_plane::supervise_pipeline_exits(
        plane.pipeline_exit_rx,
        coordinator.clone(),
    ));

    tokio::time::timeout(std::time::Duration::from_secs(1), async {
        while shared.process_state() != ProcessState::Faulted {
            tokio::task::yield_now().await;
        }
    })
    .await
    .expect("退出事件必须被 supervisor 消费");

    assert_eq!(*capture_rx.borrow(), CaptureState::Stopped);
    assert_eq!(shared.capture_state(), CaptureState::Stopped);
    assert_eq!(shared.status_dto().capture_state, CaptureState::Stopped);
    assert_eq!(shared.writer_state(), WriterState::Faulted);
    assert_eq!(shared.process_state(), ProcessState::Faulted);
    assert_eq!(coordinator.desired_state(), CaptureState::Stopped);
    assert_eq!(coordinator.effective_state(), CaptureState::Stopped);
    let error = coordinator
        .apply_capture_command("capture_start", T0)
        .await
        .expect_err("pipeline fault 在本进程内不得被 start 复活");
    assert_eq!(error.code, SafeErrorCode::AgentWriterFaulted);
    supervisor.abort();
}

#[tokio::test]
async fn capture_exit_is_actively_failed_closed() {
    assert_supervisor_fails_closed(PipelineTask::Capture).await;
}

#[tokio::test]
async fn processor_exit_is_actively_failed_closed() {
    assert_supervisor_fails_closed(PipelineTask::Processor).await;
}

#[tokio::test]
async fn writer_exit_is_actively_failed_closed() {
    assert_supervisor_fails_closed(PipelineTask::Writer).await;
}
