//! 阶段 4.2：可靠 BarrierRequest/injected ack 与 Writer pending 的确定性测试。
//!
//! 覆盖：Barrier 先于 control、同 kind 不同 ID、同 ID 冲突、request/FIFO closed、
//! pending TTL/overflow/重复、Processor/Writer 退出、queue 满时 Barrier 可达、
//! timeout 不提交边界。

use std::sync::Arc;
use std::time::Duration;

use rusqlite::Connection;
use tempfile::TempDir;
use tokio::sync::{mpsc, oneshot, watch};
use wuji_core::domain::{ActivityState, CaptureQuality, CaptureState, ProcessState, WriterState};
use wuji_core::dto::RuntimeId;
use wuji_core::error::{ErrorSource, SafeErrorCode};
use wuji_core::pipeline::IdleReading;
use wuji_core::pipeline::{
    BarrierId, BarrierKind, BarrierToken, FilteredObservation, ProcessorOutput,
};
use wuji_core::settings::Settings;
use wuji_rebuild_agent::activity::{ActivityEngine, EngineEvent};
use wuji_rebuild_agent::barrier::{BarrierInjectError, BarrierRequest, inject_barrier};
use wuji_rebuild_agent::capture_coordinator::CaptureCoordinator;
use wuji_rebuild_agent::capture_loop::{
    CaptureLoopConfig, CaptureSource, ContinuityState, RawSample, spawn_capture_loop,
};
use wuji_rebuild_agent::pipeline_health::{PipelineHealth, TaskLifecycle};
use wuji_rebuild_agent::processor_task::spawn_observation_processor;
use wuji_rebuild_agent::shared::SharedState;
use wuji_rebuild_agent::writer_task::{
    PENDING_BARRIER_CAPACITY, PendingBarriers, PendingRegister, PendingSummary, WriterControl,
    WriterTask,
};
use wuji_storage::Writer;

const T0: i64 = 1_784_332_800_000;
const SHANGHAI: &str = "Asia/Shanghai";

fn db_path(dir: &TempDir) -> std::path::PathBuf {
    dir.path().join("wuji-rebuild-v0.1.db")
}

fn token(kind: BarrierKind) -> BarrierToken {
    BarrierToken {
        id: BarrierId::new(),
        kind,
        expected_revision: 0,
    }
}

struct Fixture {
    writer: Writer,
    engine: ActivityEngine,
    continuity: Arc<ContinuityState>,
    shared: Arc<SharedState>,
    capture_state_tx: watch::Sender<CaptureState>,
}

fn fixture(dir: &TempDir) -> Fixture {
    Writer::bootstrap_with_timezone(&db_path(dir), SHANGHAI, T0).unwrap();
    let continuity = Arc::new(ContinuityState::default());
    let runtime_id = RuntimeId::new();
    {
        let mut w = Writer::open_existing(&db_path(dir)).unwrap();
        let engine =
            ActivityEngine::new(runtime_id.clone(), Settings::default(), continuity.clone())
                .unwrap();
        let tx = w.transaction().unwrap();
        tx.insert_runtime(&runtime_id, T0).unwrap();
        tx.commit().unwrap();
        drop(engine);
    }
    let writer = Writer::open_existing(&db_path(dir)).unwrap();
    let engine =
        ActivityEngine::new(runtime_id.clone(), Settings::default(), continuity.clone()).unwrap();
    let shared = Arc::new(SharedState::new("0.1.0".to_string(), runtime_id));
    let (capture_state_tx, _) = watch::channel(CaptureState::Running);
    Fixture {
        writer,
        engine,
        continuity,
        shared,
        capture_state_tx,
    }
}

fn obs(sequence: u64, utc_ms: i64) -> ProcessorOutput {
    ProcessorOutput::Observation(FilteredObservation {
        sequence,
        continuity_epoch: 0,
        captured_at_utc_ms: utc_ms,
        captured_monotonic_ms: (utc_ms - T0) as u64,
        app_key: format!("proc:{sequence:032x}"),
        display_name: format!("app{sequence}"),
        normalized_process_name: format!("app{sequence}.exe"),
        activity_state: ActivityState::Active,
        quality: CaptureQuality::Normal,
        settings_revision: 0,
    })
}

fn gap_count(dir: &TempDir) -> i64 {
    Connection::open(db_path(dir))
        .unwrap()
        .query_row("SELECT COUNT(*) FROM capture_gaps", [], |r| r.get(0))
        .unwrap()
}

/// Barrier 先于 control 到达：登记 pending，随后 control 精确消费并提交边界。
#[tokio::test(start_paused = true)]
async fn barrier_before_control_is_pending_then_consumed() {
    let dir = TempDir::new().unwrap();
    let fixture = fixture(&dir);
    let (data_tx, data_rx) = mpsc::channel(8);
    let (control_tx, control_rx) = mpsc::channel(8);

    let barrier = token(BarrierKind::Lifecycle);
    data_tx
        .send(ProcessorOutput::Barrier(barrier.clone()))
        .await
        .unwrap();

    let task = WriterTask::new(
        fixture.writer,
        fixture.engine,
        fixture.shared.clone(),
        fixture.capture_state_tx,
        fixture.continuity.clone(),
        dir.path().join("config"),
        wuji_rebuild_agent::pipeline_health::PipelineHealth::new(),
    );
    let run = tokio::spawn(async move { task.run(data_rx, control_rx).await });

    // control 后到：pending 必须已登记，drain 直接命中。
    let (ack_tx, ack_rx) = tokio::sync::oneshot::channel();
    control_tx
        .send(WriterControl::Lifecycle {
            event: EngineEvent::CapturePaused {
                at_utc_ms: T0 + 3_000,
            },
            barrier_id: barrier.id.clone(),
            expected_revision: 0,
            ack: ack_tx,
        })
        .await
        .unwrap();
    ack_rx
        .await
        .expect("ack")
        .expect("先到的 Barrier 必须被 control 消费");
    assert_eq!(gap_count(&dir), 1, "边界必须提交一次");

    drop(control_tx);
    drop(data_tx);
    let _ = run.await;
}

/// 同 kind 不同 ID：control 超时且不提交边界；正确的 ID 到达后才提交。
#[tokio::test(start_paused = true)]
async fn same_kind_different_id_times_out_without_committing() {
    let dir = TempDir::new().unwrap();
    let fixture = fixture(&dir);
    let (data_tx, data_rx) = mpsc::channel(8);
    let (control_tx, control_rx) = mpsc::channel(8);

    let task = WriterTask::new(
        fixture.writer,
        fixture.engine,
        fixture.shared.clone(),
        fixture.capture_state_tx,
        fixture.continuity.clone(),
        dir.path().join("config"),
        wuji_rebuild_agent::pipeline_health::PipelineHealth::new(),
    );
    let run = tokio::spawn(async move { task.run(data_rx, control_rx).await });

    let ghost_id = BarrierId::new();
    let (ack_tx, ack_rx) = tokio::sync::oneshot::channel();
    control_tx
        .send(WriterControl::Lifecycle {
            event: EngineEvent::CapturePaused {
                at_utc_ms: T0 + 3_000,
            },
            barrier_id: ghost_id,
            expected_revision: 0,
            ack: ack_tx,
        })
        .await
        .unwrap();
    let error = ack_rx
        .await
        .expect("ack")
        .expect_err("无匹配 Barrier 必须超时");
    assert_eq!(error.code, SafeErrorCode::InternalSafeError);
    assert_eq!(gap_count(&dir), 0, "timeout 不得提交边界（阶段 4.2）");

    drop(control_tx);
    drop(data_tx);
    let _ = run.await;
}

/// 同 ID 不同 kind/revision：冲突检测，绝不应用。
#[tokio::test(start_paused = true)]
async fn same_id_conflicting_kind_or_revision_is_rejected() {
    let dir = TempDir::new().unwrap();
    let fixture = fixture(&dir);
    let (data_tx, data_rx) = mpsc::channel(8);
    let (control_tx, control_rx) = mpsc::channel(8);

    // 同 ID、不同 kind 的两个 Barrier 经 data lane 进入。
    let id = BarrierId::new();
    let first = BarrierToken {
        id: id.clone(),
        kind: BarrierKind::Lifecycle,
        expected_revision: 0,
    };
    let conflicting = BarrierToken {
        id: id.clone(),
        kind: BarrierKind::SettingsApplied,
        expected_revision: 0,
    };
    data_tx.send(ProcessorOutput::Barrier(first)).await.unwrap();
    data_tx
        .send(ProcessorOutput::Barrier(conflicting))
        .await
        .unwrap();

    let task = WriterTask::new(
        fixture.writer,
        fixture.engine,
        fixture.shared.clone(),
        fixture.capture_state_tx,
        fixture.continuity.clone(),
        dir.path().join("config"),
        wuji_rebuild_agent::pipeline_health::PipelineHealth::new(),
    );
    let run = tokio::spawn(async move { task.run(data_rx, control_rx).await });

    // 给两个 Barrier 处理时间（paused 时间自动推进）。
    tokio::time::sleep(Duration::from_millis(50)).await;
    assert!(
        fixture.shared.errors().contains_key(&ErrorSource::Writer),
        "同 ID 冲突必须上报诊断"
    );

    // revision 冲突：control 期望 1，FIFO 中 Barrier 期望 0 → 拒绝应用。
    let revision_conflict = BarrierToken {
        id: id.clone(),
        kind: BarrierKind::Lifecycle,
        expected_revision: 0,
    };
    data_tx
        .send(ProcessorOutput::Barrier(revision_conflict))
        .await
        .unwrap();
    let (ack_tx, ack_rx) = tokio::sync::oneshot::channel();
    control_tx
        .send(WriterControl::Lifecycle {
            event: EngineEvent::CapturePaused {
                at_utc_ms: T0 + 3_000,
            },
            barrier_id: id,
            expected_revision: 1,
            ack: ack_tx,
        })
        .await
        .unwrap();
    let error = ack_rx
        .await
        .expect("ack")
        .expect_err("revision 不符必须拒绝（阶段 4.2 第 7 条）");
    assert_eq!(error.code, SafeErrorCode::SettingsConflict);
    assert_eq!(gap_count(&dir), 0, "冲突边界不得提交");

    drop(control_tx);
    drop(data_tx);
    let _ = run.await;
}

/// 注入请求 channel 关闭：稳定返回 RequestClosed。
#[tokio::test]
async fn request_channel_closed_fails_injection() {
    let (tx, rx) = wuji_rebuild_agent::barrier::barrier_request_channel(1);
    drop(rx);
    let error = inject_barrier(&tx, token(BarrierKind::Lifecycle))
        .await
        .expect_err("channel 关闭必须稳定失败");
    assert_eq!(error, BarrierInjectError::RequestClosed);
}

struct NullSource;
impl CaptureSource for NullSource {
    fn capture(&self) -> RawSample {
        RawSample {
            process_file_name: None,
            idle: IdleReading::Unavailable,
        }
    }
}

/// FIFO 关闭（Writer/Processor 退出）时注入返回 Closed。
#[tokio::test(start_paused = true)]
async fn fifo_closed_fails_injection() {
    let continuity = Arc::new(ContinuityState::default());
    let (request_tx, request_rx) = wuji_rebuild_agent::barrier::barrier_request_channel(4);
    let (_settings_tx, settings_rx) = watch::channel(Settings::default());
    let (_state_tx, state_rx) = watch::channel(CaptureState::Stopped);
    let (pipeline_rx, _handle) = spawn_capture_loop(
        NullSource,
        settings_rx,
        state_rx,
        continuity,
        CaptureLoopConfig::default(),
        request_rx,
        &PipelineHealth::new(),
    );
    // Processor/Writer 退出：丢弃 FIFO 接收端。
    drop(pipeline_rx);

    let error = inject_barrier(&request_tx, token(BarrierKind::Lifecycle))
        .await
        .expect_err("FIFO 关闭必须稳定失败");
    assert_eq!(error, BarrierInjectError::Closed);
}

/// queue 满时 Barrier 仍按序可达（不被丢弃、不超车）。
/// 阶段 4.3.1 修正：真实满载（此前默认 3s 采样间隔 + 容量 2 + 推进 2.5s，
/// 实际只有 1 条 Sample，从未满载——假满载结论更正见阶段4.3.1完成说明）。
#[tokio::test(start_paused = true)]
async fn barrier_reaches_fifo_in_order_when_sample_queue_full() {
    let continuity = Arc::new(ContinuityState::default());
    let (request_tx, request_rx) = wuji_rebuild_agent::barrier::barrier_request_channel(4);
    // 显式 1 秒采样间隔（不得依赖默认值）。
    let (_settings_tx, settings_rx) = watch::channel(Settings {
        sampling_interval_seconds: 1,
        ..Settings::default()
    });
    let (_state_tx, state_rx) = watch::channel(CaptureState::Running);
    let (mut pipeline_rx, handle) = spawn_capture_loop(
        NullSource,
        settings_rx,
        state_rx,
        continuity.clone(),
        CaptureLoopConfig {
            wake_interval: Duration::from_millis(10),
            queue_capacity: 2,
            offload_capture: false,
            ..CaptureLoopConfig::default()
        },
        request_rx,
        &PipelineHealth::new(),
    );

    // 先 yield 让 Capture Loop 启动并完成首次采样（paused 时钟下新 spawn 任务的
    // 计时器在首次 poll 前对 advance 不可见），再推进到第二条样本并 yield 驱动。
    tokio::task::yield_now().await;
    tokio::time::advance(Duration::from_millis(1_100)).await;
    tokio::task::yield_now().await;
    // 确定性断言 FIFO 已满：capture queue depth == 2（入队计数，非 sleep 猜测）。
    assert_eq!(
        continuity.capture_queue_depth(),
        2,
        "注入前 FIFO 必须真实装满（2/2）"
    );

    // FIFO 满后启动注入：Capture Loop 取得请求后将阻塞在 FIFO 写入。
    let mut inject =
        tokio::spawn(
            async move { inject_barrier(&request_tx, token(BarrierKind::Lifecycle)).await },
        );
    // 让 Capture Loop 取得请求并阻塞在满载 FIFO 写入上。
    tokio::task::yield_now().await;
    tokio::task::yield_now().await;
    // 腾出容量前：injected ack 尚未返回（注入任务未完成）。
    assert!(
        tokio::time::timeout(Duration::from_millis(100), &mut inject)
            .await
            .is_err(),
        "腾出容量前注入任务不得完成"
    );
    assert_eq!(
        continuity.capture_queue_depth(),
        2,
        "Barrier 未写入前 FIFO 内容不变"
    );

    // 取走一条旧 Sample 腾出容量：注入必须成功。
    let first = pipeline_rx.recv().await.expect("FIFO 首条");
    assert!(
        matches!(first, wuji_core::pipeline::CapturePipelineItem::Sample(_)),
        "首条必须是旧 Sample"
    );
    inject
        .await
        .expect("注入任务不 panic")
        .expect("腾槽后 Barrier 必须注入成功");

    // FIFO 顺序：剩余旧 Sample 在前，Barrier 在后（不超车）。
    let second = pipeline_rx.recv().await.expect("FIFO 第二条");
    assert!(
        matches!(second, wuji_core::pipeline::CapturePipelineItem::Sample(_)),
        "第二条必须是剩余旧 Sample"
    );
    let third = pipeline_rx.recv().await.expect("FIFO 第三条");
    assert!(
        matches!(third, wuji_core::pipeline::CapturePipelineItem::Barrier(_)),
        "第三条必须是 Barrier（旧 Sample 全部在前）"
    );
    drop(pipeline_rx);
    handle.await.unwrap();
}

/// 阶段 4.3.1 §二B：Capture Loop 在 FIFO 满载、已取得 BarrierRequest 但阻塞写入时
/// 被 abort——injected ack 返回稳定 Closed，不发送 WriterControl，health 转 Dead。
#[tokio::test(start_paused = true)]
async fn capture_loop_exit_while_barrier_blocked_returns_closed() {
    let health = PipelineHealth::new();
    let shared = Arc::new(SharedState::new("0.1.0".to_string(), RuntimeId::new()));
    shared.set_capture_state(CaptureState::Running);
    let continuity = Arc::new(ContinuityState::default());
    let (barrier_tx, barrier_rx) = wuji_rebuild_agent::barrier::barrier_request_channel(4);
    let barrier_probe = barrier_tx.clone();
    let (capture_state_tx, capture_state_rx) = watch::channel(CaptureState::Running);
    let frozen_rx = capture_state_tx.subscribe();
    let (control_tx, mut control_rx) = mpsc::channel(8);
    let (settings_tx, settings_rx) = watch::channel(Settings {
        sampling_interval_seconds: 1,
        ..Settings::default()
    });
    let coordinator = Arc::new(CaptureCoordinator::new(
        barrier_tx,
        capture_state_tx,
        control_tx,
        shared.clone(),
        settings_tx,
        CaptureState::Running,
        health.clone(),
    ));
    let (pipeline_rx, capture_handle) = spawn_capture_loop(
        NullSource,
        settings_rx,
        capture_state_rx,
        continuity.clone(),
        CaptureLoopConfig {
            wake_interval: Duration::from_millis(10),
            queue_capacity: 2,
            offload_capture: false,
            ..CaptureLoopConfig::default()
        },
        barrier_rx,
        &health,
    );
    // processor/writer 登记保持存活（本测试的焦点是 capture 退出）。
    let _processor_guard = health.register_processor();
    let _writer_guard = health.register_writer();

    // 填满 FIFO（确定性事实：depth==2，非 sleep 猜测）。
    tokio::task::yield_now().await;
    tokio::time::advance(Duration::from_millis(1_100)).await;
    tokio::task::yield_now().await;
    assert_eq!(continuity.capture_queue_depth(), 2, "FIFO 必须真实满载");

    // 发起 pause：冻结发布后，inject 将被 Capture Loop 阻塞在满载 FIFO 写入上。
    let mut cmd = tokio::spawn({
        let coordinator = coordinator.clone();
        async move { coordinator.apply_capture_command("capture_pause", T0).await }
    });
    // rendezvous：等待冻结发布到 watch。
    for _ in 0..1_000 {
        if *frozen_rx.borrow() == CaptureState::Paused {
            break;
        }
        tokio::task::yield_now().await;
    }
    assert_eq!(
        *frozen_rx.borrow(),
        CaptureState::Paused,
        "transition 必须先冻结"
    );
    // 确定性 rendezvous：容量从 3 恢复为 4 只可能发生在 Capture Loop 已从
    // request channel 取走请求之后；此时 FIFO 仍满，所以请求被任务持有并阻塞。
    for _ in 0..1_000 {
        if barrier_probe.capacity() == 4 {
            break;
        }
        tokio::task::yield_now().await;
    }
    assert_eq!(
        barrier_probe.capacity(),
        4,
        "Capture Loop 必须已取得 BarrierRequest"
    );
    // 确认 inject_barrier 尚未完成。
    assert!(
        tokio::time::timeout(Duration::from_millis(100), &mut cmd)
            .await
            .is_err(),
        "abort 前 transition 不得完成"
    );

    // abort Capture Loop：被阻塞持有的 BarrierRequest 随任务销毁
    // （ack sender 断开 → inject 返回稳定 Closed）；guard Drop → health 转 Dead。
    capture_handle.abort();
    let _ = capture_handle.await;
    assert_eq!(health.capture_state(), TaskLifecycle::Dead);

    let error = cmd
        .await
        .expect("任务不 panic")
        .expect_err("Capture Loop 退出必须返回稳定失败");
    assert_eq!(error.code, SafeErrorCode::InternalSafeError);
    assert!(
        error.message.contains("Closed"),
        "必须证明是已取得请求的 ack Closed，而非 send RequestClosed: {}",
        error.message
    );
    // 不得发送对应 WriterControl。
    assert!(
        control_rx.try_recv().is_err(),
        "注入失败绝不发送 WriterControl"
    );
    // 不提交边界、不虚假 Running：fail-closed 一致冻结。
    assert_eq!(shared.capture_state(), CaptureState::Paused);
    assert_eq!(shared.status_dto().capture_state, CaptureState::Paused);
    assert_eq!(coordinator.desired_state(), CaptureState::Paused);
    drop(pipeline_rx);
}

/// 阶段 4.3.1 §二C：真实 WriterTask 在 Coordinator transition 进行中 abort——
/// 稳定失败（非永久等待）、shared/watch/DTO 安全停止、SQLite 无目标边界提交、
/// writer health 转 Dead、Capture/Processor 保持存活。
/// 时序由写锁（BEGIN IMMEDIATE）与 watch/writer_state rendezvous 确定性驱动，
/// 不使用随机 sleep。
#[tokio::test(start_paused = true)]
async fn writer_task_exit_during_transition_fails_closed() {
    let dir = TempDir::new().unwrap();
    let fixture = fixture(&dir);
    // 第二连接持写锁：真实 Writer 的第一个 Observation 必然 busy
    // （busy_timeout 750ms 真实阻塞一次后进入 Degraded + backoff）。
    let blocker = Connection::open(db_path(&dir)).unwrap();
    blocker.execute_batch("BEGIN IMMEDIATE").unwrap();

    let health = PipelineHealth::new();
    let continuity = fixture.continuity.clone();
    let shared = fixture.shared.clone();
    let (barrier_tx, barrier_rx) = wuji_rebuild_agent::barrier::barrier_request_channel(8);
    let (capture_state_tx, capture_state_rx) = watch::channel(CaptureState::Stopped);
    let frozen_rx = capture_state_tx.subscribe();
    let (control_tx, control_rx) = mpsc::channel(8);
    let control_probe = control_tx.clone();
    let (settings_tx, settings_rx) = watch::channel(Settings {
        sampling_interval_seconds: 1,
        ..Settings::default()
    });
    let coordinator = Arc::new(CaptureCoordinator::new(
        barrier_tx,
        capture_state_tx.clone(),
        control_tx,
        shared.clone(),
        settings_tx,
        CaptureState::Stopped,
        health.clone(),
    ));
    let (pipeline_rx, capture_handle) = spawn_capture_loop(
        NullSource,
        settings_rx.clone(),
        capture_state_rx,
        continuity.clone(),
        CaptureLoopConfig {
            wake_interval: Duration::from_millis(10),
            queue_capacity: 64,
            offload_capture: false,
            ..CaptureLoopConfig::default()
        },
        barrier_rx,
        &health,
    );
    let (processor_rx, processor_handle) =
        spawn_observation_processor(pipeline_rx, settings_rx, continuity.clone(), &health);
    let writer_task = WriterTask::new(
        fixture.writer,
        fixture.engine,
        shared.clone(),
        capture_state_tx,
        continuity.clone(),
        dir.path().join("config"),
        health.clone(),
    );
    // 真实 WriterTask 启动并登记 PipelineHealth。
    let writer_handle = tokio::spawn(writer_task.into_run_future(processor_rx, control_rx));
    assert!(health.all_alive());

    // start：Coordinator 发布 Running（健康/channel/watch 均通过）。
    coordinator
        .apply_capture_command("capture_start", T0)
        .await
        .expect("start 必须成功");
    // rendezvous：样本流入真实 Writer，写锁使其必然 busy → Degraded。
    tokio::time::advance(Duration::from_millis(100)).await;
    for _ in 0..1_000 {
        if shared.writer_state() == WriterState::Degraded {
            break;
        }
        tokio::task::yield_now().await;
    }
    assert_eq!(
        shared.writer_state(),
        WriterState::Degraded,
        "写锁必须让真实 Writer 进入 busy Degraded（busy_timeout 后）"
    );

    // 发起 pause：barrier/control 流程进行中，Writer 停在 backoff（paused 不推进）。
    let cmd = tokio::spawn({
        let coordinator = coordinator.clone();
        async move { coordinator.apply_capture_command("capture_pause", T0).await }
    });
    // rendezvous：冻结发布到 watch。
    for _ in 0..1_000 {
        if *frozen_rx.borrow() == CaptureState::Paused {
            break;
        }
        tokio::task::yield_now().await;
    }
    assert_eq!(*frozen_rx.borrow(), CaptureState::Paused);
    // 确定性 rendezvous：Writer 仍卡在 paused busy backoff；capacity 8→7
    // 证明 Coordinator 的 transition control 已进入真实 Writer control lane。
    for _ in 0..1_000 {
        if control_probe.capacity() == 7 {
            break;
        }
        tokio::task::yield_now().await;
    }
    assert_eq!(
        control_probe.capacity(),
        7,
        "abort 前 transition control 必须已缓冲到 Writer lane"
    );

    // 真实 WriterTask abort：缓冲中的 control（连同 ack sender）随之销毁。
    writer_handle.abort();
    let _ = writer_handle.await;
    assert_eq!(health.writer_state(), TaskLifecycle::Dead);

    // Coordinator 必须得到稳定失败（ack sender 断开），不得永久等待。
    let error = cmd
        .await
        .expect("任务不 panic")
        .expect_err("Writer 退出必须返回稳定失败");
    assert_eq!(error.code, SafeErrorCode::AgentWriterFaulted);
    assert!(error.message.contains("结果未知"));
    // control 已入队后 ack Closed 属于 unknown：shared/watch/DTO/desired
    // 一致 Stopped，writer/process fatal 锁存。
    assert_eq!(shared.capture_state(), CaptureState::Stopped);
    assert_eq!(shared.status_dto().capture_state, CaptureState::Stopped);
    assert_eq!(*frozen_rx.borrow(), CaptureState::Stopped);
    assert_eq!(coordinator.desired_state(), CaptureState::Stopped);
    assert_eq!(shared.writer_state(), WriterState::Faulted);
    assert_eq!(shared.process_state(), ProcessState::Faulted);
    // Capture Loop 与 Processor 保持存活。
    assert!(health.capture_alive());
    assert!(health.processor_alive());
    // SQLite 不得出现目标边界提交；busy 期间 Observation 也不得落库。
    let gaps: i64 = Connection::open(db_path(&dir))
        .unwrap()
        .query_row(
            "SELECT COUNT(*) FROM capture_gaps WHERE kind = 'capture_paused'",
            [],
            |r| r.get(0),
        )
        .unwrap();
    assert_eq!(gaps, 0, "不得提交 capture_paused 边界");
    let observations: i64 = Connection::open(db_path(&dir))
        .unwrap()
        .query_row("SELECT COUNT(*) FROM foreground_observations", [], |r| {
            r.get(0)
        })
        .unwrap();
    assert_eq!(observations, 0, "busy 期间不得有已提交 Observation");

    blocker.execute_batch("ROLLBACK").unwrap();
    capture_handle.abort();
    processor_handle.abort();
}

/// 阶段 4.3.1 §二D：pending Barrier 在 Shutdown drain 中不被吞掉——
/// 遗留 pending 产生来源明确的安全诊断，Shutdown ack 返回，WriterTask 正常退出。
#[tokio::test(start_paused = true)]
async fn shutdown_reports_leftover_pending_and_exits_cleanly() {
    let dir = TempDir::new().unwrap();
    let fixture = fixture(&dir);
    let (data_tx, data_rx) = mpsc::channel(8);
    let (control_tx, control_rx) = mpsc::channel(8);

    let task = WriterTask::new(
        fixture.writer,
        fixture.engine,
        fixture.shared.clone(),
        fixture.capture_state_tx,
        fixture.continuity.clone(),
        dir.path().join("config"),
        PipelineHealth::new(),
    );
    let run = tokio::spawn(async move { task.run(data_rx, control_rx).await });

    // Barrier 与 Shutdown 背靠背发送（中间不 yield）：biased select 先消费
    // Shutdown，其 drain_all 必须把 Barrier 登记 pending 而不是吞掉。
    data_tx
        .send(ProcessorOutput::Barrier(token(BarrierKind::Lifecycle)))
        .await
        .unwrap();
    let (ack_tx, ack_rx) = oneshot::channel();
    control_tx
        .send(WriterControl::Shutdown { ack: ack_tx })
        .await
        .unwrap();
    // drain 前不得有 Writer 诊断（pending 登记本身不产生错误）。
    assert_eq!(fixture.shared.errors().get(&ErrorSource::Writer), None);

    // Shutdown ack 返回且 WriterTask 正常退出。
    ack_rx.await.expect("Shutdown ack 必须返回");
    let _ = run.await.expect("WriterTask 必须正常退出");

    // 证据（不只日志字符串）：遗留 pending 产生来源明确的安全诊断。
    assert_eq!(
        fixture.shared.errors().get(&ErrorSource::Writer),
        Some(&SafeErrorCode::InternalSafeError),
        "Shutdown 遗留 pending 必须产生 Writer 来源诊断"
    );
    // 未提交任何边界。
    let gaps: i64 = Connection::open(db_path(&dir))
        .unwrap()
        .query_row("SELECT COUNT(*) FROM capture_gaps", [], |r| r.get(0))
        .unwrap();
    assert_eq!(gaps, 0, "未匹配的 Barrier 不得提交任何边界");
}

/// 阶段 4.3.1 §二D：`PendingBarriers::summary` 纯函数——pending/poisoned 分项、
/// 饱和标志与总容量约束（不依赖日志字符串）。
#[tokio::test(start_paused = true)]
async fn pending_summary_reports_partitioned_counts_within_capacity() {
    let mut pending = PendingBarriers::default();
    for _ in 0..3 {
        assert_eq!(
            pending.register(token(BarrierKind::Lifecycle)),
            PendingRegister::Registered
        );
    }
    // 同 ID 不同 kind：冲突 → poisoned。
    let conflict_id = BarrierId::new();
    pending.register(BarrierToken {
        id: conflict_id.clone(),
        kind: BarrierKind::Lifecycle,
        expected_revision: 0,
    });
    assert_eq!(
        pending.register(BarrierToken {
            id: conflict_id,
            kind: BarrierKind::SettingsApplied,
            expected_revision: 0,
        }),
        PendingRegister::Conflict
    );

    let summary = pending.summary();
    assert_eq!(
        summary,
        PendingSummary {
            pending: 3,
            poisoned: 1,
            saturated: false,
        }
    );
    assert!(
        pending.len() <= PENDING_BARRIER_CAPACITY,
        "总状态数不得突破容量约束"
    );

    // 填满总容量后毒化表外 ghost ID：不得突破 64，而应进入全局饱和；
    // summary 必须把 saturated=true 暴露给 Shutdown 诊断。
    while pending.len() < PENDING_BARRIER_CAPACITY {
        assert_eq!(
            pending.register(token(BarrierKind::Lifecycle)),
            PendingRegister::Registered
        );
    }
    let ghost = BarrierId::new();
    assert!(!pending.poison(&ghost), "满容量表外毒化必须走饱和降级");
    assert_eq!(pending.len(), PENDING_BARRIER_CAPACITY);
    assert_eq!(
        pending.summary(),
        PendingSummary {
            pending: PENDING_BARRIER_CAPACITY - 1,
            poisoned: 1,
            saturated: true,
        }
    );
}

/// Shutdown 真实分支必须报告 saturated，而不只覆盖普通 pending：64 个其他
/// Barrier 填满表后，一个匹配但 revision 过期的全新 ID 触发表外 poison，随后
/// Shutdown 以可观测 SharedState 诊断收敛。
#[tokio::test(start_paused = true)]
async fn shutdown_reports_saturated_pending_state() {
    let dir = TempDir::new().unwrap();
    let fixture = fixture(&dir);
    let (data_tx, data_rx) = mpsc::channel(128);
    let (control_tx, control_rx) = mpsc::channel(8);

    for _ in 0..PENDING_BARRIER_CAPACITY {
        data_tx
            .send(ProcessorOutput::Barrier(token(BarrierKind::Lifecycle)))
            .await
            .unwrap();
    }
    let stale = BarrierToken {
        id: BarrierId::new(),
        kind: BarrierKind::Lifecycle,
        expected_revision: 1, // Engine 仍为 revision 0。
    };
    data_tx
        .send(ProcessorOutput::Barrier(stale.clone()))
        .await
        .unwrap();
    let (transition_ack_tx, transition_ack_rx) = oneshot::channel();
    control_tx
        .send(WriterControl::Lifecycle {
            event: EngineEvent::CapturePaused { at_utc_ms: T0 },
            barrier_id: stale.id,
            expected_revision: 1,
            ack: transition_ack_tx,
        })
        .await
        .unwrap();

    let task = WriterTask::new(
        fixture.writer,
        fixture.engine,
        fixture.shared.clone(),
        fixture.capture_state_tx,
        fixture.continuity,
        dir.path().join("config"),
        PipelineHealth::new(),
    );
    let run = tokio::spawn(async move { task.run(data_rx, control_rx).await });
    let transition_error = transition_ack_rx
        .await
        .expect("transition ack 必须返回")
        .expect_err("过期 revision 必须拒绝");
    assert_eq!(transition_error.code, SafeErrorCode::SettingsConflict);

    let (shutdown_ack_tx, shutdown_ack_rx) = oneshot::channel();
    control_tx
        .send(WriterControl::Shutdown {
            ack: shutdown_ack_tx,
        })
        .await
        .unwrap();
    shutdown_ack_rx.await.expect("Shutdown ack 必须返回");
    let _ = run.await.expect("WriterTask 必须正常退出");
    assert_eq!(
        fixture.shared.errors().get(&ErrorSource::Writer),
        Some(&SafeErrorCode::InternalSafeError),
        "saturated Shutdown 必须留下 Writer 诊断"
    );
}

/// pending overflow：登记满后拒绝并上报诊断（不静默丢弃）。
#[tokio::test(start_paused = true)]
async fn pending_overflow_is_refused_and_diagnosed() {
    let dir = TempDir::new().unwrap();
    let fixture = fixture(&dir);
    let (data_tx, data_rx) = mpsc::channel(128);
    let (control_tx, control_rx) = mpsc::channel(8);

    // data lane 灌入 CAPACITY+1 个不同 ID 的 Barrier。
    for _ in 0..(PENDING_BARRIER_CAPACITY + 1) {
        data_tx
            .send(ProcessorOutput::Barrier(token(BarrierKind::Lifecycle)))
            .await
            .unwrap();
    }

    let task = WriterTask::new(
        fixture.writer,
        fixture.engine,
        fixture.shared.clone(),
        fixture.capture_state_tx,
        fixture.continuity.clone(),
        dir.path().join("config"),
        wuji_rebuild_agent::pipeline_health::PipelineHealth::new(),
    );
    let run = tokio::spawn(async move { task.run(data_rx, control_rx).await });
    tokio::time::sleep(Duration::from_millis(200)).await;
    assert!(
        fixture.shared.errors().contains_key(&ErrorSource::Writer),
        "overflow 必须上报诊断（阶段 4.2 第 6 条）"
    );

    drop(control_tx);
    drop(data_tx);
    let _ = run.await;
}

/// pending TTL：过期条目被清除，对应 control 超时失败、不提交边界。
#[tokio::test(start_paused = true)]
async fn pending_ttl_expires_entry() {
    let mut pending = PendingBarriers::default();
    let barrier = token(BarrierKind::Lifecycle);
    assert_eq!(
        pending.register(barrier.clone()),
        wuji_rebuild_agent::writer_task::PendingRegister::Registered
    );

    // 未过期时可消费。
    tokio::time::advance(Duration::from_secs(31)).await;
    assert_eq!(
        pending.take_if_matches(&barrier.id, BarrierKind::Lifecycle, 0),
        wuji_rebuild_agent::writer_task::PendingTake::Absent,
        "超过 TTL 的 pending 必须被清除"
    );
}

/// pending 重复：完全相同的 Barrier 重复登记保留首条、只消费一次。
#[tokio::test(start_paused = true)]
async fn pending_duplicate_keeps_first_and_consumes_once() {
    let mut pending = PendingBarriers::default();
    let barrier = token(BarrierKind::Lifecycle);
    assert_eq!(
        pending.register(barrier.clone()),
        wuji_rebuild_agent::writer_task::PendingRegister::Registered
    );
    assert_eq!(
        pending.register(barrier.clone()),
        wuji_rebuild_agent::writer_task::PendingRegister::Duplicate
    );
    assert_eq!(
        pending.take_if_matches(&barrier.id, BarrierKind::Lifecycle, 0),
        wuji_rebuild_agent::writer_task::PendingTake::Matched
    );
    assert_eq!(
        pending.take_if_matches(&barrier.id, BarrierKind::Lifecycle, 0),
        wuji_rebuild_agent::writer_task::PendingTake::Absent,
        "同一 Barrier 只能被消费一次"
    );
}

/// data lane 断开：drain 返回稳定错误而不是悬挂。
#[tokio::test(start_paused = true)]
async fn data_lane_disconnect_fails_drain() {
    let dir = TempDir::new().unwrap();
    let fixture = fixture(&dir);
    let (data_tx, data_rx) = mpsc::channel::<ProcessorOutput>(8);
    let (control_tx, control_rx) = mpsc::channel(8);

    let task = WriterTask::new(
        fixture.writer,
        fixture.engine,
        fixture.shared.clone(),
        fixture.capture_state_tx,
        fixture.continuity.clone(),
        dir.path().join("config"),
        wuji_rebuild_agent::pipeline_health::PipelineHealth::new(),
    );
    let run = tokio::spawn(async move { task.run(data_rx, control_rx).await });
    // Processor 退出：data lane 关闭。
    drop(data_tx);

    let (ack_tx, ack_rx) = tokio::sync::oneshot::channel();
    control_tx
        .send(WriterControl::Lifecycle {
            event: EngineEvent::CapturePaused {
                at_utc_ms: T0 + 3_000,
            },
            barrier_id: BarrierId::new(),
            expected_revision: 0,
            ack: ack_tx,
        })
        .await
        .unwrap();
    let error = ack_rx
        .await
        .expect("ack")
        .expect_err("data lane 断开必须稳定失败");
    assert_eq!(error.code, SafeErrorCode::InternalSafeError);
    assert_eq!(gap_count(&dir), 0, "失败路径不得提交边界");

    drop(control_tx);
    let _ = run.await;
}

/// 复审 P1-01：token 与 control 携带相同的过期 revision，Engine 已前进时必须拒绝。
/// Settings→Lifecycle 交错：Settings 先把 Engine 推进到 1，Lifecycle（期望 0）不得提交。
#[tokio::test(start_paused = true)]
async fn stale_expected_revision_is_rejected_when_engine_advanced() {
    let dir = TempDir::new().unwrap();
    let fixture = fixture(&dir);
    let (data_tx, data_rx) = mpsc::channel(8);
    let (control_tx, control_rx) = mpsc::channel(8);

    // Lifecycle Barrier 先入队（期望 revision 0）。
    let lifecycle_barrier = token(BarrierKind::Lifecycle);
    data_tx
        .send(ProcessorOutput::Barrier(lifecycle_barrier.clone()))
        .await
        .unwrap();
    // Settings Barrier 随后（期望 revision 0）。
    let settings_barrier = token(BarrierKind::SettingsApplied);
    data_tx
        .send(ProcessorOutput::Barrier(settings_barrier.clone()))
        .await
        .unwrap();

    let task = WriterTask::new(
        fixture.writer,
        fixture.engine,
        fixture.shared.clone(),
        fixture.capture_state_tx,
        fixture.continuity.clone(),
        dir.path().join("config"),
        wuji_rebuild_agent::pipeline_health::PipelineHealth::new(),
    );
    let run = tokio::spawn(async move { task.run(data_rx, control_rx).await });

    // 先执行 Settings control：Engine 0 → 1。
    let (ack_tx, ack_rx) = tokio::sync::oneshot::channel();
    control_tx
        .send(WriterControl::SettingsApplied {
            settings: Settings {
                revision: "1".to_string(),
                ..Settings::default()
            },
            at_utc_ms: T0,
            barrier_id: settings_barrier.id.clone(),
            expected_revision: 0,
            ack: ack_tx,
        })
        .await
        .unwrap();
    ack_rx.await.expect("ack").expect("Settings 必须应用成功");
    assert_eq!(fixture.shared.applied_settings_revision(), 1);

    // Lifecycle control 的 token/control 都携带过期 revision 0：必须拒绝，不得提交边界。
    let (ack_tx, ack_rx) = tokio::sync::oneshot::channel();
    control_tx
        .send(WriterControl::Lifecycle {
            event: EngineEvent::CapturePaused {
                at_utc_ms: T0 + 3_000,
            },
            barrier_id: lifecycle_barrier.id.clone(),
            expected_revision: 0,
            ack: ack_tx,
        })
        .await
        .unwrap();
    let error = ack_rx
        .await
        .expect("ack")
        .expect_err("Engine 已前进，过期 Barrier 必须拒绝（复审 P1-01）");
    assert_eq!(error.code, SafeErrorCode::SettingsConflict);
    assert_eq!(gap_count(&dir), 0, "过期边界不得提交");

    drop(control_tx);
    drop(data_tx);
    let _ = run.await;
}

/// 复审 P1-01：两个并发 Settings control——第二个携带过期 expected_revision 时被拒绝。
#[tokio::test(start_paused = true)]
async fn stale_settings_control_rejected_after_another_settings_applied() {
    let dir = TempDir::new().unwrap();
    let fixture = fixture(&dir);
    let (data_tx, data_rx) = mpsc::channel(8);
    let (control_tx, control_rx) = mpsc::channel(8);

    // 两个 Settings Barrier 都期望 revision 0（模拟两个几乎同时接受的 reload）。
    let first_barrier = token(BarrierKind::SettingsApplied);
    let stale_barrier = token(BarrierKind::SettingsApplied);
    data_tx
        .send(ProcessorOutput::Barrier(first_barrier.clone()))
        .await
        .unwrap();
    data_tx
        .send(ProcessorOutput::Barrier(stale_barrier.clone()))
        .await
        .unwrap();

    let task = WriterTask::new(
        fixture.writer,
        fixture.engine,
        fixture.shared.clone(),
        fixture.capture_state_tx,
        fixture.continuity.clone(),
        dir.path().join("config"),
        wuji_rebuild_agent::pipeline_health::PipelineHealth::new(),
    );
    let run = tokio::spawn(async move { task.run(data_rx, control_rx).await });

    // 第一个 Settings 应用成功（Engine 0 → 1）。
    let (ack_tx, ack_rx) = tokio::sync::oneshot::channel();
    control_tx
        .send(WriterControl::SettingsApplied {
            settings: Settings {
                revision: "1".to_string(),
                ..Settings::default()
            },
            at_utc_ms: T0,
            barrier_id: first_barrier.id.clone(),
            expected_revision: 0,
            ack: ack_tx,
        })
        .await
        .unwrap();
    ack_rx.await.expect("ack").expect("第一个必须成功");

    // 第二个 Settings control 携带过期 expected_revision=0：必须在 apply 前被拒绝。
    let (ack_tx, ack_rx) = tokio::sync::oneshot::channel();
    control_tx
        .send(WriterControl::SettingsApplied {
            settings: Settings {
                revision: "2".to_string(),
                ..Settings::default()
            },
            at_utc_ms: T0 + 1_000,
            barrier_id: stale_barrier.id.clone(),
            expected_revision: 0,
            ack: ack_tx,
        })
        .await
        .unwrap();
    let error = ack_rx
        .await
        .expect("ack")
        .expect_err("过期 expected_revision 必须在 apply 前拒绝");
    assert_eq!(error.code, SafeErrorCode::SettingsConflict);
    assert_eq!(
        fixture.shared.applied_settings_revision(),
        1,
        "过期 control 不得前进 applied revision"
    );

    drop(control_tx);
    drop(data_tx);
    let _ = run.await;
}

/// 复审 P1-01：poisoned tombstone 与 pending 共用严格总容量，连续冲突 ID 不得突破；
/// 第三轮复审：满表 poison 失败 → 释放槽位 → 同 ID 仍不得洗白（全局饱和降级）。
#[tokio::test(start_paused = true)]
async fn tombstones_share_total_capacity() {
    use wuji_rebuild_agent::writer_task::{PendingRegister, PendingTake};

    let mut pending = PendingBarriers::default();

    // 32 个普通 pending（记录第一个 ID，稍后用于 take 释放槽位）。
    let first_normal = token(BarrierKind::Lifecycle);
    assert_eq!(
        pending.register(first_normal.clone()),
        PendingRegister::Registered
    );
    for _ in 0..31 {
        assert_eq!(
            pending.register(token(BarrierKind::Lifecycle)),
            PendingRegister::Registered
        );
    }
    // 32 个冲突对：每个新 ID 先注册 Lifecycle，再用同 ID 注册 Settings → 原地毒化。
    for _ in 0..32 {
        let id = BarrierId::new();
        assert_eq!(
            pending.register(BarrierToken {
                id: id.clone(),
                kind: BarrierKind::Lifecycle,
                expected_revision: 0,
            }),
            PendingRegister::Registered
        );
        assert_eq!(
            pending.register(BarrierToken {
                id: id.clone(),
                kind: BarrierKind::SettingsApplied,
                expected_revision: 0,
            }),
            PendingRegister::Conflict
        );
    }
    assert_eq!(pending.len(), 64, "32 pending + 32 poisoned = 满容量");
    assert_eq!(pending.poisoned_count(), 32);

    // 继续构造新冲突 ID：第一个注册（新插入）就必须被 Overflow 拒绝。
    for _ in 0..32 {
        assert_eq!(
            pending.register(token(BarrierKind::Lifecycle)),
            PendingRegister::Overflow,
            "满容量时新 ID 不得插入"
        );
    }
    assert_eq!(pending.len(), 64, "总状态数始终有界（复审 P1-01）");

    // 对不在表中的 ID 执行毒化：容量满时不新增 tombstone，而是进入全局饱和降级。
    let ghost = BarrierId::new();
    assert!(!pending.poison(&ghost), "满容量不得新增 tombstone");
    assert!(pending.is_saturated(), "未知 ID 冲突必须触发全局饱和降级");
    assert_eq!(pending.len(), 64);

    // 释放一个槽位（take 之前登记的第一个普通 pending）。
    assert_eq!(
        pending.take_if_matches(&first_normal.id, BarrierKind::Lifecycle, 0),
        PendingTake::Matched
    );
    assert_eq!(pending.len(), 63);

    // 第三轮复审 P1-01 核心：释放槽位后 ghost 仍不得洗白（饱和降级持续 TTL）。
    assert_eq!(
        pending.register(BarrierToken {
            id: ghost.clone(),
            kind: BarrierKind::Lifecycle,
            expected_revision: 0,
        }),
        PendingRegister::Saturated,
        "释放槽位后 ghost 仍不得注册（防洗白）"
    );
    assert_eq!(
        pending.register(token(BarrierKind::Lifecycle)),
        PendingRegister::Saturated,
        "饱和期间任何新 ID 均被拒绝"
    );
    assert_eq!(pending.len(), 63, "饱和期间不得插入");

    // TTL 到期：饱和解除，全部释放，恢复正常。
    tokio::time::advance(std::time::Duration::from_secs(31)).await;
    assert!(!pending.is_saturated());
    assert_eq!(
        pending.register(token(BarrierKind::Lifecycle)),
        PendingRegister::Registered,
        "TTL 过期后容量释放"
    );
}

/// 复审 P2-01：冲突 ID 进入毒化状态，第三次同 ID 登记不得洗白。
#[tokio::test(start_paused = true)]
async fn conflicted_id_stays_poisoned_and_cannot_be_rehabilitated() {
    use wuji_rebuild_agent::writer_task::{PendingRegister, PendingTake};

    let mut pending = PendingBarriers::default();
    let id = BarrierId::new();
    let first = BarrierToken {
        id: id.clone(),
        kind: BarrierKind::Lifecycle,
        expected_revision: 0,
    };
    let conflicting = BarrierToken {
        id: id.clone(),
        kind: BarrierKind::SettingsApplied,
        expected_revision: 0,
    };
    assert_eq!(pending.register(first.clone()), PendingRegister::Registered);
    assert_eq!(pending.register(conflicting), PendingRegister::Conflict);
    // 第三次同 ID 登记：不得洗白。
    assert_eq!(pending.register(first), PendingRegister::Poisoned);
    // 毒化 ID 不得被消费。
    assert_eq!(
        pending.take_if_matches(&id, BarrierKind::Lifecycle, 0),
        PendingTake::Poisoned
    );
}

/// 复审 P2-01：pending kind 与 control kind 不同立即返回冲突（不再静默等超时）。
#[tokio::test(start_paused = true)]
async fn take_with_kind_mismatch_reports_conflict_immediately() {
    use wuji_rebuild_agent::writer_task::{PendingRegister, PendingTake};

    let mut pending = PendingBarriers::default();
    let id = BarrierId::new();
    assert_eq!(
        pending.register(BarrierToken {
            id: id.clone(),
            kind: BarrierKind::Lifecycle,
            expected_revision: 0,
        }),
        PendingRegister::Registered
    );
    // kind 不符：立即 KindConflict 并毒化（而非 Absent 等待超时）。
    assert_eq!(
        pending.take_if_matches(&id, BarrierKind::SettingsApplied, 0),
        PendingTake::KindConflict
    );
    // 毒化后不得再次登记。
    assert_eq!(
        pending.register(BarrierToken {
            id: id.clone(),
            kind: BarrierKind::Lifecycle,
            expected_revision: 0,
        }),
        PendingRegister::Poisoned
    );
}

struct RepeatSource;
impl CaptureSource for RepeatSource {
    fn capture(&self) -> RawSample {
        RawSample {
            process_file_name: Some("code.exe".to_string()),
            idle: IdleReading::Seconds(0),
        }
    }
}

/// 复审 P2-02：真实 Capture→Processor→Writer 拓扑下，Writer data lane 满载时
/// Barrier 不丢失、不超车，边界只提交一次。
/// 真实时间运行（paused 时钟会让 capture loop 的 UTC/monotonic 产生人为 clock_changed）。
#[tokio::test]
async fn full_writer_lane_barrier_still_reaches_and_commits_once() {
    let dir = TempDir::new().unwrap();
    let fixture = fixture(&dir);
    let continuity = fixture.continuity.clone();
    let (_settings_tx, settings_rx) = watch::channel(Settings {
        sampling_interval_seconds: 1,
        ..Settings::default()
    });
    let (_state_tx, state_rx) = watch::channel(CaptureState::Running);

    // 真实 Capture Loop（FIFO 容量 8）→ 真实 Processor（writer data lane 容量 2）。
    let (request_tx, request_rx) = wuji_rebuild_agent::barrier::barrier_request_channel(4);
    let (pipeline_rx, capture_handle) = spawn_capture_loop(
        RepeatSource,
        settings_rx.clone(),
        state_rx,
        continuity.clone(),
        CaptureLoopConfig {
            wake_interval: Duration::from_millis(20),
            queue_capacity: 8,
            offload_capture: false,
            ..CaptureLoopConfig::default()
        },
        request_rx,
        &PipelineHealth::new(),
    );
    let (data_rx, processor_handle) =
        wuji_rebuild_agent::processor_task::spawn_observation_processor_with_capacity(
            pipeline_rx,
            settings_rx,
            continuity.clone(),
            2,
            &PipelineHealth::new(),
        );

    // Writer 尚未启动：产生 backlog（1s 采样间隔，约 3.5 秒 → 3~4 条样本；
    // 超出 data lane 容量的样本按合同诚实 drop 并记 epoch）。
    tokio::time::sleep(Duration::from_millis(3_500)).await;

    // 注入 Barrier：Capture FIFO 接受并 ack（data lane 仍处于满载/阻塞）。
    let barrier = token(BarrierKind::Lifecycle);
    let barrier_id = barrier.id.clone();
    tokio::time::timeout(Duration::from_secs(2), inject_barrier(&request_tx, barrier))
        .await
        .expect("注入不得悬挂")
        .expect("Capture FIFO 必须接受 Barrier");

    // 现在启动 Writer 并发送 control：Writer 排空 backlog → Processor 最终转发 Barrier。
    let (_control_tx, control_rx) = mpsc::channel(8);
    let task = WriterTask::new(
        fixture.writer,
        fixture.engine,
        fixture.shared.clone(),
        fixture.capture_state_tx,
        fixture.continuity.clone(),
        dir.path().join("config"),
        wuji_rebuild_agent::pipeline_health::PipelineHealth::new(),
    );
    let run = tokio::spawn(async move { task.run(data_rx, control_rx).await });
    let (ack_tx, ack_rx) = tokio::sync::oneshot::channel();
    _control_tx
        .send(WriterControl::Lifecycle {
            event: EngineEvent::CapturePaused {
                at_utc_ms: wuji_rebuild_agent::capture_loop::now_utc_ms() + 60_000,
            },
            barrier_id,
            expected_revision: 0,
            ack: ack_tx,
        })
        .await
        .unwrap();
    tokio::time::timeout(Duration::from_secs(10), ack_rx)
        .await
        .expect("control 不得悬挂")
        .expect("ack")
        .expect("满载 data lane 下 Barrier 必须最终精确匹配");

    let conn = Connection::open(db_path(&dir)).unwrap();
    let pause_gaps: i64 = conn
        .query_row(
            "SELECT COUNT(*) FROM capture_gaps WHERE kind = 'capture_paused'",
            [],
            |r| r.get(0),
        )
        .unwrap();
    assert_eq!(pause_gaps, 1, "Lifecycle 边界必须恰好提交一次");
    let clock_gaps: i64 = conn
        .query_row(
            "SELECT COUNT(*) FROM capture_gaps WHERE kind = 'clock_changed'",
            [],
            |r| r.get(0),
        )
        .unwrap();
    assert_eq!(clock_gaps, 0, "真实时间下不得出现人为 clock_changed");
    let observations: i64 = conn
        .query_row("SELECT COUNT(*) FROM foreground_observations", [], |r| {
            r.get(0)
        })
        .unwrap();
    assert!(
        observations >= 2,
        "存活 backlog 必须先于边界提交: {observations}"
    );

    drop(_control_tx);
    let (writer, _engine) = run.await.expect("writer 退出");
    drop(writer);
    capture_handle.abort();
    processor_handle.abort();
}

/// 第三轮复审 P1-01：全局饱和必须覆盖 control-first 的直接 FIFO 匹配路径。
/// 触发饱和后，全新 matching token/control 不得通过 direct-FIFO 提交边界；
/// TTL 到期后才允许新 ID。
#[tokio::test(start_paused = true)]
async fn saturation_blocks_direct_fifo_matching_for_new_ids() {
    let dir = TempDir::new().unwrap();
    let fixture = fixture(&dir);
    let (data_tx, data_rx) = mpsc::channel(128);
    let (control_tx, control_rx) = mpsc::channel(8);

    // 1. data lane 填满 64 个不同 ID 的 pending。
    for _ in 0..PENDING_BARRIER_CAPACITY {
        data_tx
            .send(ProcessorOutput::Barrier(token(BarrierKind::Lifecycle)))
            .await
            .unwrap();
    }

    let task = WriterTask::new(
        fixture.writer,
        fixture.engine,
        fixture.shared.clone(),
        fixture.capture_state_tx,
        fixture.continuity.clone(),
        dir.path().join("config"),
        wuji_rebuild_agent::pipeline_health::PipelineHealth::new(),
    );
    let run = tokio::spawn(async move { task.run(data_rx, control_rx).await });
    tokio::time::sleep(Duration::from_millis(200)).await;

    // 2. 触发全局饱和：control 期望 Y/Lifecycle，但 FIFO 中是 Y/SettingsApplied
    //    （kind 冲突 → poison(Y)；Y 不在表中且满表 → 全局饱和）。
    let y_id = BarrierId::new();
    data_tx
        .send(ProcessorOutput::Barrier(BarrierToken {
            id: y_id.clone(),
            kind: BarrierKind::SettingsApplied,
            expected_revision: 0,
        }))
        .await
        .unwrap();
    let (ack_tx, ack_rx) = tokio::sync::oneshot::channel();
    control_tx
        .send(WriterControl::Lifecycle {
            event: EngineEvent::CapturePaused {
                at_utc_ms: T0 + 3_000,
            },
            barrier_id: y_id,
            expected_revision: 0,
            ack: ack_tx,
        })
        .await
        .unwrap();
    let _ = ack_rx.await.expect("ack").expect_err("kind 冲突必须拒绝");

    // 3. 全新 ghost token/control 三要素完全匹配：direct-FIFO 路径必须被饱和拒绝。
    let ghost = token(BarrierKind::Lifecycle);
    data_tx
        .send(ProcessorOutput::Barrier(ghost.clone()))
        .await
        .unwrap();
    let (ack_tx, ack_rx) = tokio::sync::oneshot::channel();
    control_tx
        .send(WriterControl::Lifecycle {
            event: EngineEvent::CapturePaused {
                at_utc_ms: T0 + 6_000,
            },
            barrier_id: ghost.id.clone(),
            expected_revision: 0,
            ack: ack_tx,
        })
        .await
        .unwrap();
    let error = ack_rx
        .await
        .expect("ack")
        .expect_err("饱和期间全新 ID 的 direct-FIFO 匹配必须被拒绝（第三轮复审 P1-01）");
    assert_eq!(error.code, SafeErrorCode::SettingsConflict);
    assert_eq!(gap_count(&dir), 0, "饱和期间不得提交边界");

    // 4. TTL 到期后重新注入 ghost：允许提交（已有表内状态不受影响）。
    tokio::time::advance(Duration::from_secs(31)).await;
    data_tx
        .send(ProcessorOutput::Barrier(ghost.clone()))
        .await
        .unwrap();
    let (ack_tx, ack_rx) = tokio::sync::oneshot::channel();
    control_tx
        .send(WriterControl::Lifecycle {
            event: EngineEvent::CapturePaused {
                at_utc_ms: T0 + 9_000,
            },
            barrier_id: ghost.id.clone(),
            expected_revision: 0,
            ack: ack_tx,
        })
        .await
        .unwrap();
    ack_rx
        .await
        .expect("ack")
        .expect("TTL 到期后新 ID 必须允许提交");
    assert_eq!(gap_count(&dir), 1, "TTL 到期后边界提交一次");

    drop(control_tx);
    drop(data_tx);
    let _ = run.await;
}

/// 普通 data 分支的 Barrier 不得被跳过：混入 Sample 流中也被登记并可消费。
#[tokio::test(start_paused = true)]
async fn barrier_mixed_in_sample_stream_is_registered_not_skipped() {
    let dir = TempDir::new().unwrap();
    let fixture = fixture(&dir);
    let (data_tx, data_rx) = mpsc::channel(8);
    let (control_tx, control_rx) = mpsc::channel(8);

    let barrier = token(BarrierKind::Lifecycle);
    data_tx.send(obs(1, T0)).await.unwrap();
    data_tx
        .send(ProcessorOutput::Barrier(barrier.clone()))
        .await
        .unwrap();
    data_tx.send(obs(2, T0 + 3_000)).await.unwrap();

    let task = WriterTask::new(
        fixture.writer,
        fixture.engine,
        fixture.shared.clone(),
        fixture.capture_state_tx,
        fixture.continuity.clone(),
        dir.path().join("config"),
        wuji_rebuild_agent::pipeline_health::PipelineHealth::new(),
    );
    let run = tokio::spawn(async move { task.run(data_rx, control_rx).await });
    tokio::time::sleep(Duration::from_millis(50)).await;

    let (ack_tx, ack_rx) = tokio::sync::oneshot::channel();
    control_tx
        .send(WriterControl::Lifecycle {
            event: EngineEvent::CapturePaused {
                at_utc_ms: T0 + 6_000,
            },
            barrier_id: barrier.id.clone(),
            expected_revision: 0,
            ack: ack_tx,
        })
        .await
        .unwrap();
    ack_rx
        .await
        .expect("ack")
        .expect("混入 Sample 流的 Barrier 必须已登记（不得跳过）");

    drop(control_tx);
    drop(data_tx);
    let _ = run.await;
}

/// 阶段 4.3.1 §四A（barrier 级）：channel 满且消费端不前进 → SendTimeout（paused 2s），
/// 请求保证未进入 channel。
#[tokio::test(start_paused = true)]
async fn send_timeout_when_request_channel_full() {
    let (tx, mut rx) = wuji_rebuild_agent::barrier::barrier_request_channel(1);
    // 占满容量 1（不消费）。
    let (ack_tx, _ack_rx) = oneshot::channel();
    tx.send(BarrierRequest {
        token: token(BarrierKind::Lifecycle),
        injected_ack: ack_tx,
    })
    .await
    .unwrap();

    let error = inject_barrier(&tx, token(BarrierKind::Lifecycle))
        .await
        .expect_err("channel 满且不前进必须稳定失败");
    assert_eq!(error, BarrierInjectError::SendTimeout);
    // 请求保证未进入 channel：只有第一个请求。
    assert!(rx.recv().await.is_some());
    assert!(rx.try_recv().is_err(), "超时的请求不得进入 channel");
}

/// 阶段 4.3.1 §四B（barrier 级）：消费端取得请求但永不 ack → AckTimeout（paused 3s）。
#[tokio::test(start_paused = true)]
async fn ack_timeout_when_consumer_never_acks() {
    let (tx, mut rx) = wuji_rebuild_agent::barrier::barrier_request_channel(4);
    let hold = tokio::spawn(async move {
        let _request = rx.recv().await;
        // 永不 ack：持有请求直到任务被 abort。
        std::future::pending::<()>().await;
    });

    let error = inject_barrier(&tx, token(BarrierKind::Lifecycle))
        .await
        .expect_err("永不 ack 必须稳定失败");
    assert_eq!(error, BarrierInjectError::AckTimeout);
    hold.abort();
}
