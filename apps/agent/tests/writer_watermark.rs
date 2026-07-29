//! R03 回归：满队列 Pause/Stop watermark 与真实生命周期事件映射。

use std::sync::Arc;

use rusqlite::Connection;
use tempfile::TempDir;
use tokio::sync::{mpsc, watch};
use wuji_core::domain::{ActivityState, CaptureQuality, CaptureState};
use wuji_core::dto::RuntimeId;
use wuji_core::pipeline::{FilteredObservation, ProcessorOutput};
use wuji_core::settings::Settings;
use wuji_rebuild_agent::activity::{ActivityEngine, EngineEvent};
use wuji_rebuild_agent::capture_loop::ContinuityState;
use wuji_rebuild_agent::shared::SharedState;
use wuji_rebuild_agent::writer_task::{WriterControl, WriterTask};
use wuji_storage::Writer;

const T0: i64 = 1_784_332_800_000;
const SHANGHAI: &str = "Asia/Shanghai";

fn db_path(dir: &TempDir) -> std::path::PathBuf {
    dir.path().join("wuji-rebuild-v0.1.db")
}

struct Fixture {
    writer: Writer,
    engine: ActivityEngine,
    continuity: Arc<ContinuityState>,
    shared: Arc<SharedState>,
    capture_state_tx: watch::Sender<CaptureState>,
}

fn fixture(dir: &TempDir) -> Fixture {
    // bootstrap 建库后立即释放连接，再以 Single Writer 身份重新打开。
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

fn obs(sequence: u64, name: &str, utc_ms: i64) -> ProcessorOutput {
    obs_named(sequence, &format!("{name}{sequence}.exe"), name, utc_ms)
}

/// 指定规范进程名（同名的 Observation 会聚合成同一 App）。
fn obs_named(sequence: u64, normalized_name: &str, display: &str, utc_ms: i64) -> ProcessorOutput {
    ProcessorOutput::Observation(FilteredObservation {
        sequence,
        continuity_epoch: 0,
        captured_at_utc_ms: utc_ms,
        captured_monotonic_ms: (utc_ms - T0) as u64,
        app_key: format!(
            "proc:{}",
            wuji_core::settings::sha256_hex(normalized_name.as_bytes())
        ),
        display_name: display.to_string(),
        normalized_process_name: normalized_name.to_string(),
        activity_state: ActivityState::Active,
        quality: CaptureQuality::Normal,
        settings_revision: 0,
    })
}

fn conn(dir: &TempDir) -> Connection {
    Connection::open(db_path(dir)).unwrap()
}

#[tokio::test(start_paused = true)]
async fn full_queue_pause_drains_backlog_before_boundary() {
    let dir = TempDir::new().unwrap();
    let fixture = fixture(&dir);
    let (data_tx, data_rx) = mpsc::channel(8);
    let (control_tx, control_rx) = mpsc::channel(4);

    // data lane 填满 4 条（seq 1..4），随后注入 Lifecycle barrier + 下达 Lifecycle。
    for sequence in 1..=4_u64 {
        data_tx
            .try_send(obs(sequence, "code", T0 + sequence as i64 * 3_000))
            .unwrap();
    }
    // S2-04：注入 barrier 到 data lane（旧 backlog 之后）。
    let barrier_id = wuji_core::pipeline::BarrierId::new();
    data_tx
        .try_send(ProcessorOutput::Barrier(
            wuji_core::pipeline::BarrierToken {
                id: barrier_id.clone(),
                kind: wuji_core::pipeline::BarrierKind::Lifecycle,
                expected_revision: 0,
            },
        ))
        .unwrap();
    let (ack_tx, ack_rx) = tokio::sync::oneshot::channel();
    control_tx
        .send(WriterControl::Lifecycle {
            event: EngineEvent::CapturePaused {
                at_utc_ms: T0 + 60_000,
            },
            barrier_id,
            expected_revision: 0,
            ack: ack_tx,
        })
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
    ack_rx
        .await
        .expect("lifecycle ack")
        .expect("lifecycle applied");
    drop(control_tx);
    drop(data_tx);
    let _ = run.await;

    let conn = conn(&dir);
    let observations: i64 = conn
        .query_row("SELECT COUNT(*) FROM foreground_observations", [], |r| {
            r.get(0)
        })
        .unwrap();
    assert_eq!(
        observations, 4,
        "满队列 backlog 必须先于边界全部提交（R03）"
    );
    let (kind, status): (String, String) = conn
        .query_row(
            "SELECT kind, status FROM capture_gaps WHERE kind = 'capture_paused'",
            [],
            |r| Ok((r.get(0)?, r.get(1)?)),
        )
        .unwrap();
    assert_eq!(kind, "capture_paused");
    assert_eq!(
        status, "open",
        "pause gap 打开，等待第一条恢复后的有效 Observation"
    );
    // 所有 backlog Observation 都早于 pause 边界被处理：open segment 已在边界关闭。
    let open_segments: i64 = conn
        .query_row(
            "SELECT COUNT(*) FROM activity_segments WHERE status = 'open'",
            [],
            |r| r.get(0),
        )
        .unwrap();
    assert_eq!(open_segments, 0);
}

#[tokio::test(start_paused = true)]
async fn straggler_after_watermark_is_post_boundary_observation() {
    let dir = TempDir::new().unwrap();
    let fixture = fixture(&dir);

    // 先有一条活跃采样形成 open segment，然后 Pause（watermark=1），
    // 再有 seq=2 的迟到样本（seq > watermark）：它属于边界之后。
    let (data_tx, data_rx) = mpsc::channel(4);
    let (control_tx, control_rx) = mpsc::channel(4);
    data_tx.try_send(obs(1, "code", T0)).unwrap();
    // S2-04：注入 barrier 到 data lane（seq 1 之后，seq 2 之前）。
    let barrier_id_2 = wuji_core::pipeline::BarrierId::new();
    data_tx
        .try_send(ProcessorOutput::Barrier(
            wuji_core::pipeline::BarrierToken {
                id: barrier_id_2.clone(),
                kind: wuji_core::pipeline::BarrierKind::Lifecycle,
                expected_revision: 0,
            },
        ))
        .unwrap();
    let (ack_tx, ack_rx) = tokio::sync::oneshot::channel();
    control_tx
        .send(WriterControl::Lifecycle {
            event: EngineEvent::CapturePaused {
                at_utc_ms: T0 + 3_000,
            },
            barrier_id: barrier_id_2,
            expected_revision: 0,
            ack: ack_tx,
        })
        .await
        .unwrap();
    // 注意：pause 控制先到，迟到样本在 ack 前已经入队（模拟排空期间新到）。
    tokio::time::sleep(std::time::Duration::from_millis(10)).await;
    data_tx.try_send(obs(2, "code", T0 + 6_000)).unwrap();

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
    ack_rx
        .await
        .expect("lifecycle ack")
        .expect("lifecycle applied");
    // 给迟到样本一点处理时间。
    tokio::time::sleep(std::time::Duration::from_millis(50)).await;
    drop(control_tx);
    drop(data_tx);
    let _ = run.await;

    let conn = conn(&dir);
    let gap_status: String = conn
        .query_row(
            "SELECT status FROM capture_gaps WHERE kind = 'capture_paused'",
            [],
            |r| r.get(0),
        )
        .unwrap();
    assert_eq!(
        gap_status, "closed",
        "迟到样本按 09 §6.7 在边界后关闭 pause gap，而不是写入边界之前"
    );
    let segment_count: i64 = conn
        .query_row("SELECT COUNT(*) FROM activity_segments", [], |r| r.get(0))
        .unwrap();
    assert_eq!(segment_count, 2, "边界前一段 + 迟到样本的新段");
}

#[tokio::test(start_paused = true)]
async fn sleep_and_lock_events_close_rows_with_matching_kinds() {
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

    data_tx
        .try_send(obs_named(1, "code-a.exe", "code", T0))
        .unwrap();
    data_tx
        .try_send(obs_named(2, "code-a.exe", "code", T0 + 3_000))
        .unwrap();
    let sleep_barrier_id = wuji_core::pipeline::BarrierId::new();
    let _ = data_tx
        .send(ProcessorOutput::Barrier(
            wuji_core::pipeline::BarrierToken {
                id: sleep_barrier_id.clone(),
                kind: wuji_core::pipeline::BarrierKind::Lifecycle,
                expected_revision: 0,
            },
        ))
        .await;
    let (sleep_ack, sleep_ack_rx) = tokio::sync::oneshot::channel();
    control_tx
        .send(WriterControl::Lifecycle {
            event: EngineEvent::SystemSleep {
                at_utc_ms: T0 + 6_000,
            },
            barrier_id: sleep_barrier_id,
            expected_revision: 0,
            ack: sleep_ack,
        })
        .await
        .unwrap();
    sleep_ack_rx
        .await
        .expect("sleep ack")
        .expect("sleep applied");

    // Sleep gap 打开期间恢复采样 → gap 关闭；随后 Lock。
    data_tx
        .try_send(obs_named(3, "code-b.exe", "code", T0 + 120_000))
        .unwrap();
    let lock_barrier_id = wuji_core::pipeline::BarrierId::new();
    let _ = data_tx
        .send(ProcessorOutput::Barrier(
            wuji_core::pipeline::BarrierToken {
                id: lock_barrier_id.clone(),
                kind: wuji_core::pipeline::BarrierKind::Lifecycle,
                expected_revision: 0,
            },
        ))
        .await;
    let (lock_ack, lock_ack_rx) = tokio::sync::oneshot::channel();
    control_tx
        .send(WriterControl::Lifecycle {
            event: EngineEvent::SessionLocked {
                at_utc_ms: T0 + 180_000,
            },
            barrier_id: lock_barrier_id,
            expected_revision: 0,
            ack: lock_ack,
        })
        .await
        .unwrap();
    lock_ack_rx.await.expect("lock ack").expect("lock applied");

    drop(control_tx);
    drop(data_tx);
    let _ = run.await;

    let conn = conn(&dir);
    let kinds: Vec<(String, String)> = {
        let mut stmt = conn
            .prepare("SELECT kind, status FROM capture_gaps ORDER BY gap_id")
            .unwrap();
        stmt.query_map([], |r| Ok((r.get(0)?, r.get(1)?)))
            .unwrap()
            .collect::<rusqlite::Result<Vec<_>>>()
            .unwrap()
    };
    assert_eq!(
        kinds,
        vec![
            ("system_sleep".to_string(), "closed".to_string()),
            ("session_locked".to_string(), "open".to_string()),
        ],
        "Sleep/Lock 事件必须产生对应 kind 的边界与 gap（R03）"
    );
    let work_reasons: Vec<String> = {
        let mut stmt = conn
            .prepare("SELECT close_reason FROM work_blocks ORDER BY work_block_id")
            .unwrap();
        stmt.query_map([], |r| r.get(0))
            .unwrap()
            .collect::<rusqlite::Result<Vec<_>>>()
            .unwrap()
    };
    assert!(work_reasons.contains(&"system_sleep".to_string()));
}

/// S2-04 返修：Writer 等待不存在的 Barrier 超时必须返回错误（不放行）。
#[tokio::test(start_paused = true)]
async fn barrier_timeout_returns_error_and_keeps_frozen() {
    let dir = TempDir::new().unwrap();
    let fixture = fixture(&dir);
    let (data_tx, data_rx) = mpsc::channel(4);
    let (control_tx, control_rx) = mpsc::channel(4);

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

    // 发送不存在的 barrier_id：Writer 等待 5s 后超时。
    let (ack_tx, ack_rx) = tokio::sync::oneshot::channel();
    let ghost_id = wuji_core::pipeline::BarrierId::new();
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

    // 5s 超时必须返回错误。
    let result = tokio::time::timeout(std::time::Duration::from_secs(6), ack_rx)
        .await
        .expect("ack 必须返回")
        .expect("ack 通道未关闭");
    assert!(
        result.is_err(),
        "不存在的 barrier 超时必须返回错误，实际: {result:?}"
    );

    // 超时后 Writer 仍可处理后续消息：pipeline 未因超时崩溃。
    drop(data_tx);
    drop(control_tx);
    let _ = run.await;
}
