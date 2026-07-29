//! 自动门禁（审核 §7）：busy/损坏/checkpoint 故障注入与隐私扫描。
//! disk-full 为手工门禁（见 migration-status），不在自动范围。

use std::sync::Arc;
use std::time::{Duration, Instant};

use rusqlite::Connection;
use tempfile::TempDir;
use tokio::sync::{mpsc, watch};
use wuji_core::domain::{ActivityState, CaptureQuality, CaptureState, ProcessState, WriterState};
use wuji_core::dto::RuntimeId;
use wuji_core::error::{ErrorSource, SafeErrorCode};
use wuji_core::pipeline::{FilteredObservation, IdleReading, ProcessorOutput, RawCapture};
use wuji_core::settings::Settings;
use wuji_rebuild_agent::activity::{ActivityEngine, EngineEvent};
use wuji_rebuild_agent::capture_loop::ContinuityState;
use wuji_rebuild_agent::pipeline_health::PipelineHealth;
use wuji_rebuild_agent::processor_task::spawn_observation_processor;
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

fn observation_count(dir: &TempDir) -> i64 {
    Connection::open(db_path(dir))
        .unwrap()
        .query_row("SELECT COUNT(*) FROM foreground_observations", [], |r| {
            r.get(0)
        })
        .unwrap()
}

/// busy：第二连接持写锁约 1.2s，Writer 重试后恢复，Observation 最终落库（09 §5.2）。
#[tokio::test]
async fn busy_lock_degrades_then_recovers() {
    let dir = TempDir::new().unwrap();
    let fixture = fixture(&dir);
    let (data_tx, data_rx) = mpsc::channel(8);
    let (control_tx, control_rx) = mpsc::channel(8);

    // 第二连接持写锁（WAL 模式 BEGIN IMMEDIATE 阻塞其他写者提交）。
    let blocker = Connection::open(db_path(&dir)).unwrap();
    blocker.execute_batch("BEGIN IMMEDIATE").unwrap();

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

    data_tx.send(obs(1, T0)).await.unwrap();
    // 1.2s 后释放写锁（busy_timeout 750ms → 首次尝试失败并重试）。
    let releaser = std::thread::spawn(move || {
        std::thread::sleep(Duration::from_millis(1_200));
        blocker.execute_batch("ROLLBACK").unwrap();
    });

    let deadline = Instant::now() + Duration::from_secs(10);
    while observation_count(&dir) == 0 {
        assert!(
            Instant::now() < deadline,
            "busy 重试后 Observation 必须落库"
        );
        tokio::time::sleep(Duration::from_millis(50)).await;
    }
    releaser.join().unwrap();
    // 恢复后 writer 状态回到 Healthy（无 faulted）。
    assert_ne!(fixture.shared.writer_state(), WriterState::Faulted);

    drop(control_tx);
    drop(data_tx);
    let _ = run.await;
}

/// Coordinator 已因 ack unknown 锁存 fatal 后，Writer 的迟到 busy 重试即使最终
/// 成功，也不得把 writer 改回 Degraded/Healthy 或清除 AGENT_WRITER_FAULTED。
#[tokio::test]
async fn late_busy_recovery_cannot_clear_latched_writer_fault() {
    let dir = TempDir::new().unwrap();
    let fixture = fixture(&dir);
    let (data_tx, data_rx) = mpsc::channel(8);
    let (control_tx, control_rx) = mpsc::channel(8);
    let blocker = Connection::open(db_path(&dir)).unwrap();
    blocker.execute_batch("BEGIN IMMEDIATE").unwrap();

    fixture.shared.set_writer_state(WriterState::Faulted);
    fixture.shared.set_process_state(ProcessState::Faulted);
    fixture
        .shared
        .set_error(ErrorSource::Writer, SafeErrorCode::AgentWriterFaulted);
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
    data_tx.send(obs(1, T0)).await.unwrap();

    let releaser = std::thread::spawn(move || {
        std::thread::sleep(Duration::from_millis(1_200));
        blocker.execute_batch("ROLLBACK").unwrap();
    });
    let deadline = Instant::now() + Duration::from_secs(10);
    while observation_count(&dir) == 0 {
        assert!(Instant::now() < deadline, "迟到 Writer 重试必须最终完成");
        tokio::time::sleep(Duration::from_millis(50)).await;
    }
    releaser.join().unwrap();

    assert_eq!(fixture.shared.writer_state(), WriterState::Faulted);
    assert_eq!(fixture.shared.process_state(), ProcessState::Faulted);
    assert_eq!(
        fixture.shared.errors().get(&ErrorSource::Writer),
        Some(&SafeErrorCode::AgentWriterFaulted)
    );
    drop((data_tx, control_tx));
    let _ = run.await;
}

/// 损坏注入：外部 DROP TABLE 后写入失败 → faulted、停止采集、不自动修复、控制 lane 仍应答。
#[tokio::test]
async fn corrupt_schema_faults_writer_and_does_not_auto_repair() {
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

    // 第二连接注入损坏（写者空闲时可取得 schema 锁）。
    {
        let injector = Connection::open(db_path(&dir)).unwrap();
        injector
            .execute_batch("DROP TABLE foreground_observations")
            .unwrap();
    }

    data_tx.send(obs(1, T0)).await.unwrap();
    let deadline = Instant::now() + Duration::from_secs(5);
    while fixture.shared.writer_state() != WriterState::Faulted {
        assert!(Instant::now() < deadline, "损坏写入必须使 writer faulted");
        tokio::time::sleep(Duration::from_millis(20)).await;
    }
    assert_eq!(fixture.shared.capture_state(), CaptureState::Stopped);
    assert!(fixture.shared.safe_error_code().is_some());

    // 不自动修复：再发一条仍然失败，writer 不自行重建 schema。
    data_tx.send(obs(2, T0 + 3_000)).await.unwrap();
    tokio::time::sleep(Duration::from_millis(300)).await;
    assert_eq!(fixture.shared.writer_state(), WriterState::Faulted);

    // IPC/控制 lane 保持在线：Lifecycle 仍被处理并给出 ack。
    // S2-04 返修：注入 matching Barrier 到 data lane。
    let life_id = wuji_core::pipeline::BarrierId::new();
    let _ = data_tx
        .send(ProcessorOutput::Barrier(
            wuji_core::pipeline::BarrierToken {
                id: life_id.clone(),
                kind: wuji_core::pipeline::BarrierKind::Lifecycle,
                expected_revision: 0,
            },
        ))
        .await;
    let (ack_tx, ack_rx) = tokio::sync::oneshot::channel();
    control_tx
        .send(WriterControl::Lifecycle {
            event: EngineEvent::CaptureStopped {
                at_utc_ms: T0 + 6_000,
            },
            barrier_id: life_id,
            expected_revision: 0,
            ack: ack_tx,
        })
        .await
        .unwrap();
    let _ = ack_rx.await.expect("faulted 后控制 lane 必须仍应答");

    drop(control_tx);
    drop(data_tx);
    let _ = run.await;
}

/// checkpoint busy：外部读事务挡住 TRUNCATE → 仅记录诊断，写入不阻断（09 §5.2）。
#[tokio::test]
async fn checkpoint_busy_only_records_diagnostic() {
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

    // 先写入一条 Observation（WAL 有未 checkpoint 的帧）。
    data_tx.send(obs(1, T0)).await.unwrap();
    let deadline = Instant::now() + Duration::from_secs(5);
    while observation_count(&dir) == 0 {
        assert!(Instant::now() < deadline, "前置写入必须落库");
        tokio::time::sleep(Duration::from_millis(50)).await;
    }

    // 外部读事务持有 read mark（读取 WAL 中的新页），TRUNCATE checkpoint 必然 busy。
    let reader = Connection::open(db_path(&dir)).unwrap();
    reader.execute_batch("BEGIN").unwrap();
    reader
        .query_row("SELECT COUNT(*) FROM foreground_observations", [], |r| {
            r.get::<_, i64>(0)
        })
        .unwrap();

    control_tx.send(WriterControl::Checkpoint).await.unwrap();
    tokio::time::sleep(Duration::from_millis(500)).await;
    assert_eq!(
        fixture.shared.safe_error_code(),
        Some(wuji_core::error::SafeErrorCode::AgentWriterDegraded),
        "checkpoint busy 必须留下安全诊断"
    );
    assert_ne!(
        fixture.shared.writer_state(),
        WriterState::Faulted,
        "checkpoint busy 不得使 writer faulted"
    );

    // 写入不受 checkpoint busy 影响。
    data_tx.send(obs(2, T0 + 3_000)).await.unwrap();
    let deadline = Instant::now() + Duration::from_secs(5);
    while observation_count(&dir) < 2 {
        assert!(
            Instant::now() < deadline,
            "checkpoint busy 不得阻断正常写入"
        );
        tokio::time::sleep(Duration::from_millis(50)).await;
    }
    reader.execute_batch("ROLLBACK").unwrap();

    drop(control_tx);
    drop(data_tx);
    let _ = run.await;
}

/// 隐私扫描（审核 §7）：排除进程名、用户名不得出现在 DB/WAL 字节与 DTO 中。
#[tokio::test]
async fn privacy_canary_never_persists_to_db_wal_or_dto() {
    const EXCLUDED: &str = "keepass-canary.exe";
    let dir = TempDir::new().unwrap();
    let fixture = fixture(&dir);

    let settings = Settings {
        excluded_process_names: vec![EXCLUDED.to_string()],
        ..Settings::default()
    };
    let (_settings_tx, settings_rx) = watch::channel(settings);
    let (capture_tx, capture_rx) = mpsc::channel::<wuji_core::pipeline::CapturePipelineItem>(8);
    // S2-04 返修：Processor 从 CapturePipelineItem FIFO 读取。
    // fault_injection 测试通过 capture_tx 注入 RawCapture（包装为 Sample）给 processor。
    let (data_rx, processor) = spawn_observation_processor(
        capture_rx,
        settings_rx,
        fixture.continuity.clone(),
        &PipelineHealth::new(),
    );
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

    let raw = |sequence: u64, name: Option<&str>, utc_ms: i64| RawCapture {
        sequence,
        continuity_epoch: 0,
        captured_at_utc_ms: utc_ms,
        captured_monotonic_ms: (utc_ms - T0) as u64,
        process_file_name: name.map(str::to_string),
        idle: IdleReading::Seconds(1),
        settings_revision: 0,
    };
    let sample = |sequence: u64, name: Option<&str>, utc_ms: i64| {
        wuji_core::pipeline::CapturePipelineItem::Sample(raw(sequence, name, utc_ms))
    };
    capture_tx
        .send(sample(1, Some("code.exe"), T0))
        .await
        .unwrap();
    capture_tx
        .send(sample(2, Some(EXCLUDED), T0 + 3_000))
        .await
        .unwrap();
    capture_tx
        .send(sample(3, Some("code.exe"), T0 + 6_000))
        .await
        .unwrap();
    drop(capture_tx);
    processor.await.unwrap();

    let (ack_tx, ack_rx) = tokio::sync::oneshot::channel();
    control_tx
        .send(WriterControl::Shutdown { ack: ack_tx })
        .await
        .unwrap();
    ack_rx.await.unwrap();
    let _ = run.await;

    // 字节级扫描：DB 与 WAL（shutdown checkpoint 后 WAL 可能已截断，两者都扫）。
    let db_bytes = std::fs::read(db_path(&dir)).unwrap();
    let wal_path = db_path(&dir).with_extension("db-wal");
    let wal_bytes = std::fs::read(&wal_path).unwrap_or_default();
    let username = std::env::var("USERNAME").unwrap_or_default();
    for needle in [EXCLUDED, username.as_str()] {
        if needle.is_empty() {
            continue;
        }
        assert!(
            !db_bytes
                .windows(needle.len())
                .any(|w| w == needle.as_bytes()),
            "DB 字节中不得出现 {needle}"
        );
        assert!(
            !wal_bytes
                .windows(needle.len())
                .any(|w| w == needle.as_bytes()),
            "WAL 字节中不得出现 {needle}"
        );
    }
    assert!(
        db_bytes.windows(8).any(|w| w == b"code.exe"),
        "正常应用名应落库"
    );

    // DTO 扫描：Today/Status 均不得包含排除名。
    let reader = wuji_storage::Reader::open(&db_path(&dir)).unwrap();
    let today = reader
        .today(&wuji_core::dto::LocalDate::parse("2026-07-18").unwrap())
        .unwrap();
    let dto_json = serde_json::to_string(&today).unwrap();
    assert!(!dto_json.contains(EXCLUDED), "DTO 不得包含排除进程名");
}
