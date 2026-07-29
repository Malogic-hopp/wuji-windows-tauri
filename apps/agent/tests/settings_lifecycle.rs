//! R04 回归：settings 生效边界（backlog 保持旧 revision）、revision 单调性与自动对账。

use std::sync::Arc;
use std::time::Duration;

use rusqlite::Connection;
use tempfile::TempDir;
use tokio::sync::{mpsc, watch};
use wuji_core::domain::{ActivityState, CaptureQuality, CaptureState};
use wuji_core::dto::RuntimeId;
use wuji_core::error::SafeErrorCode;
use wuji_core::pipeline::{FilteredObservation, ProcessorOutput};
use wuji_core::settings::Settings;
use wuji_rebuild_agent::activity::ActivityEngine;
use wuji_rebuild_agent::capture_coordinator::CaptureCoordinator;
use wuji_rebuild_agent::capture_loop::ContinuityState;
use wuji_rebuild_agent::pipeline_health::PipelineHealth;
use wuji_rebuild_agent::settings_reconciler::run_settings_reconciler_with_interval;
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
    obs_with_rev(sequence, utc_ms, 0)
}

fn obs_with_rev(sequence: u64, utc_ms: i64, revision: i64) -> ProcessorOutput {
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
        settings_revision: revision,
    })
}

fn observation_revisions(dir: &TempDir) -> Vec<(i64, i64)> {
    let conn = Connection::open(db_path(dir)).unwrap();
    let mut stmt = conn
        .prepare("SELECT capture_sequence, settings_revision FROM foreground_observations ORDER BY capture_sequence")
        .unwrap();
    stmt.query_map([], |r| Ok((r.get(0)?, r.get(1)?)))
        .unwrap()
        .collect::<rusqlite::Result<Vec<_>>>()
        .unwrap()
}

/// 生效边界：watermark 之前的 backlog 保持旧 revision，之后采集的样本用新 revision（R04）。
#[tokio::test(start_paused = true)]
async fn backlog_before_watermark_keeps_old_revision() {
    let dir = TempDir::new().unwrap();
    let fixture = fixture(&dir);
    let (data_tx, data_rx) = mpsc::channel(8);
    let (control_tx, control_rx) = mpsc::channel(8);

    // backlog：seq 1..=2 在 settings 变更被接受之前采集（旧 revision 0）。
    data_tx.try_send(obs(1, T0)).unwrap();
    data_tx.try_send(obs(2, T0 + 3_000)).unwrap();
    // S2-04：在 data lane 中注入 SettingsApplied barrier（旧 backlog 之后、新样本之前）。
    let barrier_id = wuji_core::pipeline::BarrierId::new();
    data_tx
        .try_send(ProcessorOutput::Barrier(
            wuji_core::pipeline::BarrierToken {
                id: barrier_id.clone(),
                kind: wuji_core::pipeline::BarrierKind::SettingsApplied,
                expected_revision: 0,
            },
        ))
        .unwrap();
    let new_settings = Settings {
        revision: "1".to_string(),
        ..Settings::default()
    };
    let (ack_tx, ack_rx) = tokio::sync::oneshot::channel();
    control_tx
        .send(WriterControl::SettingsApplied {
            settings: new_settings,
            at_utc_ms: T0 + 6_000,
            barrier_id,
            expected_revision: 0,
            ack: ack_tx,
        })
        .await
        .unwrap();
    // barrier 之后采集的样本（新 revision 1）。
    data_tx.try_send(obs_with_rev(3, T0 + 9_000, 1)).unwrap();

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
        .expect("settings ack")
        .expect("settings applied");
    tokio::time::sleep(Duration::from_millis(50)).await;
    drop(control_tx);
    drop(data_tx);
    let _ = run.await;

    assert_eq!(
        observation_revisions(&dir),
        vec![(1, 0), (2, 0), (3, 1)],
        "backlog 必须保持旧 revision，watermark 之后的样本才用新 revision（R04）"
    );
    assert_eq!(fixture.shared.applied_settings_revision(), 1);
}

/// revision 单调性：低于已应用值的 SettingsApplied 被拒绝且不更新内存（R04）。
#[tokio::test(start_paused = true)]
async fn downgrade_settings_applied_is_rejected() {
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

    // S2-04 返修：注入 matching barrier 到 data lane。
    let rev3_id = wuji_core::pipeline::BarrierId::new();
    let _ = data_tx
        .send(ProcessorOutput::Barrier(
            wuji_core::pipeline::BarrierToken {
                id: rev3_id.clone(),
                kind: wuji_core::pipeline::BarrierKind::SettingsApplied,
                expected_revision: 0,
            },
        ))
        .await;
    let (ack_tx, ack_rx) = tokio::sync::oneshot::channel();
    control_tx
        .send(WriterControl::SettingsApplied {
            settings: Settings {
                revision: "3".to_string(),
                ..Settings::default()
            },
            at_utc_ms: T0,
            barrier_id: rev3_id,
            expected_revision: 0,
            ack: ack_tx,
        })
        .await
        .unwrap();
    ack_rx.await.expect("ack").expect("revision 3 applied");
    assert_eq!(fixture.shared.applied_settings_revision(), 3);

    // 降级到 revision 2：必须拒绝。
    let rev2_id = wuji_core::pipeline::BarrierId::new();
    let _ = data_tx
        .send(ProcessorOutput::Barrier(
            wuji_core::pipeline::BarrierToken {
                id: rev2_id.clone(),
                kind: wuji_core::pipeline::BarrierKind::SettingsApplied,
                expected_revision: 3,
            },
        ))
        .await;
    let (ack_tx, ack_rx) = tokio::sync::oneshot::channel();
    control_tx
        .send(WriterControl::SettingsApplied {
            settings: Settings {
                revision: "2".to_string(),
                ..Settings::default()
            },
            at_utc_ms: T0 + 1_000,
            barrier_id: rev2_id,
            expected_revision: 3,
            ack: ack_tx,
        })
        .await
        .unwrap();
    let error = ack_rx.await.expect("ack").expect_err("降级必须被拒绝");
    assert_eq!(error.code, SafeErrorCode::SettingsConflict);
    assert_eq!(
        fixture.shared.applied_settings_revision(),
        3,
        "降级不得更新已应用 revision"
    );

    drop(control_tx);
    drop(data_tx);
    let _ = run.await;
}

/// 自动对账：文件 revision 高于已应用值时，后台 reconciler 自动应用（R04 saved-not-applied 重试）。
/// 阶段 4.3：reconciler 与 IPC settings_reload 共用唯一 CaptureCoordinator。
#[tokio::test]
async fn reconciler_applies_newer_saved_file() {
    let dir = TempDir::new().unwrap();
    let fixture = fixture(&dir);
    let settings_path = dir.path().join("settings.json");
    let saved = Settings {
        revision: "1".to_string(),
        idle_threshold_seconds: 90,
        ..Settings::default()
    };
    std::fs::write(&settings_path, saved.canonical_json()).unwrap();

    let (data_tx, data_rx) = mpsc::channel(8);
    let (control_tx, control_rx) = mpsc::channel(8);
    let (settings_tx, settings_rx) = watch::channel(Settings::default());

    let (barrier_tx, mut barrier_rx) = wuji_rebuild_agent::barrier::barrier_request_channel(16);
    // 模拟 Capture Loop：收到 BarrierRequest 后写入 FIFO 并确认（阶段 4.2 真实语义）。
    let fwd_data_tx = data_tx.clone();
    tokio::spawn(async move {
        while let Some(request) = barrier_rx.recv().await {
            let failed = fwd_data_tx
                .send(ProcessorOutput::Barrier(request.token))
                .await
                .is_err();
            let _ = request.injected_ack.send(if failed {
                Err(wuji_rebuild_agent::barrier::BarrierInjectError::Closed)
            } else {
                Ok(())
            });
            if failed {
                break;
            }
        }
    });
    // watch 接收端必须存活（复审 P1-02：无消费者的发布会失败）。
    let _capture_rx = fixture.capture_state_tx.subscribe();
    // 第二次复审 P1：同步注册三任务健康（模拟生产任务已启动）。
    let health = PipelineHealth::new();
    let _guards = (
        health.register_capture(),
        health.register_processor(),
        health.register_writer(),
    );
    let coordinator = Arc::new(CaptureCoordinator::new(
        barrier_tx,
        fixture.capture_state_tx.clone(),
        control_tx.clone(),
        fixture.shared.clone(),
        settings_tx,
        CaptureState::Running,
        health,
    ));
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
    let reconciler = tokio::spawn(run_settings_reconciler_with_interval(
        settings_path,
        fixture.shared.clone(),
        coordinator,
        Duration::from_millis(20),
    ));

    // 等待自动对账生效（最多 2 秒真实时间）。
    let deadline = std::time::Instant::now() + Duration::from_secs(2);
    while fixture.shared.applied_settings_revision() != 1 {
        assert!(std::time::Instant::now() < deadline, "自动对账超时未生效");
        tokio::time::sleep(Duration::from_millis(10)).await;
    }
    // Coordinator 在 Writer ack 后更新 settings watch（先 watch 后解冻）。
    let watch_deadline = std::time::Instant::now() + Duration::from_secs(2);
    while settings_rx.borrow().revision != "1" {
        assert!(
            std::time::Instant::now() < watch_deadline,
            "settings watch 未更新"
        );
        tokio::time::sleep(Duration::from_millis(10)).await;
    }

    reconciler.abort();
    drop(control_tx);
    drop(data_tx);
    let _ = run.await;

    let conn = Connection::open(db_path(&dir)).unwrap();
    let max_revision: i64 = conn
        .query_row("SELECT MAX(revision) FROM settings_revisions", [], |r| {
            r.get(0)
        })
        .unwrap();
    assert_eq!(max_revision, 1, "reconciler 必须持久化新 revision");
}

/// 阶段 4.4（P1-04）：Observation revision 与 Engine 当前 revision 不匹配是
/// 内部协议不变量破坏——零 ActivityEngine/SQLite 副作用，留下来源明确的
/// SETTINGS_CONFLICT，fail-closed 锁存；后续数据消息（即使 revision 匹配）
/// 也一律拒绝，不得重标 revision 或清队列换绿。
#[tokio::test(start_paused = true)]
async fn observation_revision_mismatch_fails_closed_and_latches() {
    let dir = TempDir::new().unwrap();
    let fixture = fixture(&dir);
    let (data_tx, data_rx) = mpsc::channel(8);
    let (control_tx, control_rx) = mpsc::channel(8);
    let capture_rx = fixture.capture_state_tx.subscribe();

    // 先应用 revision 3 settings。
    let barrier_id = wuji_core::pipeline::BarrierId::new();
    let _ = data_tx
        .send(ProcessorOutput::Barrier(
            wuji_core::pipeline::BarrierToken {
                id: barrier_id.clone(),
                kind: wuji_core::pipeline::BarrierKind::SettingsApplied,
                expected_revision: 0,
            },
        ))
        .await;
    let (ack_tx, ack_rx) = tokio::sync::oneshot::channel();
    control_tx
        .send(WriterControl::SettingsApplied {
            settings: Settings {
                revision: "3".to_string(),
                ..Settings::default()
            },
            at_utc_ms: T0,
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
    ack_rx.await.expect("ack").expect("revision 3 applied");

    // 发送 revision 为 0 的 Observation（Engine 现在是 revision 3，协议违例）。
    data_tx.send(obs_with_rev(1, T0 + 3_000, 0)).await.unwrap();
    // 违例锁存后，即使 revision 匹配的 Observation 也一律拒绝。
    data_tx.send(obs_with_rev(2, T0 + 6_000, 3)).await.unwrap();

    tokio::time::sleep(std::time::Duration::from_millis(100)).await;
    let count: i64 = Connection::open(db_path(&dir))
        .unwrap()
        .query_row("SELECT COUNT(*) FROM foreground_observations", [], |r| {
            r.get(0)
        })
        .unwrap();
    assert_eq!(count, 0, "协议违例后任何 Observation 都不得落库");
    let gaps: i64 = Connection::open(db_path(&dir))
        .unwrap()
        .query_row("SELECT COUNT(*) FROM capture_gaps", [], |r| r.get(0))
        .unwrap();
    assert_eq!(gaps, 0, "协议违例不得产生任何 gap（零 SQLite 副作用）");

    // fail-closed：来源明确的 SETTINGS_CONFLICT + writer/process Faulted +
    // watch/shared/DTO 一致 Stopped。
    assert_eq!(
        fixture
            .shared
            .errors()
            .get(&wuji_core::error::ErrorSource::Writer),
        Some(&SafeErrorCode::SettingsConflict),
        "违例必须留下 Writer 来源的 SETTINGS_CONFLICT"
    );
    assert_eq!(
        fixture.shared.writer_state(),
        wuji_core::domain::WriterState::Faulted
    );
    assert_eq!(
        fixture.shared.process_state(),
        wuji_core::domain::ProcessState::Faulted
    );
    assert_eq!(*capture_rx.borrow(), CaptureState::Stopped);
    assert_eq!(fixture.shared.capture_state(), CaptureState::Stopped);
    assert_eq!(
        fixture.shared.status_dto().capture_state,
        CaptureState::Stopped
    );

    drop(control_tx);
    drop(data_tx);
    let _ = run.await;
}

fn max_db_revision(dir: &TempDir) -> i64 {
    Connection::open(db_path(dir))
        .unwrap()
        .query_row("SELECT MAX(revision) FROM settings_revisions", [], |r| {
            r.get(0)
        })
        .unwrap()
}

fn settings_barrier() -> (wuji_core::pipeline::BarrierId, ProcessorOutput) {
    let id = wuji_core::pipeline::BarrierId::new();
    let output = ProcessorOutput::Barrier(wuji_core::pipeline::BarrierToken {
        id: id.clone(),
        kind: wuji_core::pipeline::BarrierKind::SettingsApplied,
        expected_revision: 0,
    });
    (id, output)
}

/// 审核 P1-01 必测场景 8：备份写失败时不得提交 DB revision，也不得返回成功。
#[tokio::test(start_paused = true)]
async fn backup_write_failure_blocks_db_commit() {
    let dir = TempDir::new().unwrap();
    let fixture = fixture(&dir);
    let (data_tx, data_rx) = mpsc::channel(8);
    let (control_tx, control_rx) = mpsc::channel(8);

    // 备份目录不可写：config 路径是已存在的文件（create_dir_all 确定性失败）。
    let blocker = dir.path().join("config");
    std::fs::write(&blocker, b"not a directory").unwrap();

    let task = WriterTask::new(
        fixture.writer,
        fixture.engine,
        fixture.shared.clone(),
        fixture.capture_state_tx,
        fixture.continuity.clone(),
        blocker,
        wuji_rebuild_agent::pipeline_health::PipelineHealth::new(),
    );
    let run = tokio::spawn(async move { task.run(data_rx, control_rx).await });

    let (barrier_id, barrier) = settings_barrier();
    data_tx.send(barrier).await.unwrap();
    let (ack_tx, ack_rx) = tokio::sync::oneshot::channel();
    control_tx
        .send(WriterControl::SettingsApplied {
            settings: Settings {
                revision: "1".to_string(),
                ..Settings::default()
            },
            at_utc_ms: T0,
            barrier_id,
            expected_revision: 0,
            ack: ack_tx,
        })
        .await
        .unwrap();
    let error = ack_rx
        .await
        .expect("ack")
        .expect_err("备份写失败不得返回成功（审核 P1-01）");
    assert_eq!(error.code, SafeErrorCode::SettingsSavedNotApplied);
    assert_eq!(max_db_revision(&dir), 0, "备份失败时 DB revision 不得前进");
    assert_eq!(
        fixture.shared.applied_settings_revision(),
        0,
        "applied revision 不得前进"
    );
    assert_eq!(
        fixture.shared.safe_error_code(),
        Some(SafeErrorCode::SettingsSavedNotApplied)
    );
    assert_ne!(
        fixture.shared.writer_state(),
        wuji_core::domain::WriterState::Faulted,
        "备份失败是可重试错误，writer 不得 faulted"
    );

    drop(control_tx);
    drop(data_tx);
    let _ = run.await;
}

/// 复审 P2-01：Settings 失败只更新 Settings 来源；成功后只清除 Settings 来源。
#[tokio::test(start_paused = true)]
async fn successful_apply_clears_settings_error_source_only() {
    use wuji_core::error::ErrorSource;

    let dir = TempDir::new().unwrap();
    let fixture = fixture(&dir);
    let (data_tx, data_rx) = mpsc::channel(8);
    let (control_tx, control_rx) = mpsc::channel(8);

    // 预置一个其他来源错误（如 checkpoint busy）：它必须全程不受影响。
    fixture
        .shared
        .set_error(ErrorSource::Checkpoint, SafeErrorCode::AgentWriterDegraded);

    // 第一次：备份目录不可写 → 失败，只设置 Settings 来源。
    let blocker = dir.path().join("config");
    std::fs::write(&blocker, b"not a directory").unwrap();
    let task = WriterTask::new(
        fixture.writer,
        fixture.engine,
        fixture.shared.clone(),
        fixture.capture_state_tx,
        fixture.continuity.clone(),
        blocker.clone(),
        wuji_rebuild_agent::pipeline_health::PipelineHealth::new(),
    );
    let run = tokio::spawn(async move { task.run(data_rx, control_rx).await });

    let (barrier_id, barrier) = settings_barrier();
    data_tx.send(barrier).await.unwrap();
    let (ack_tx, ack_rx) = tokio::sync::oneshot::channel();
    control_tx
        .send(WriterControl::SettingsApplied {
            settings: Settings {
                revision: "1".to_string(),
                ..Settings::default()
            },
            at_utc_ms: T0,
            barrier_id,
            expected_revision: 0,
            ack: ack_tx,
        })
        .await
        .unwrap();
    let _ = ack_rx.await.expect("ack").expect_err("第一次必须失败");
    {
        let errors = fixture.shared.errors();
        assert!(
            errors.contains_key(&ErrorSource::Settings),
            "失败必须设置 Settings 来源"
        );
        assert!(
            errors.contains_key(&ErrorSource::Checkpoint),
            "失败不得清除其他来源"
        );
    }

    // 修复备份目录（换成真目录），第二次成功：只清除 Settings 来源。
    std::fs::remove_file(&blocker).unwrap();
    let (barrier_id, barrier) = settings_barrier();
    data_tx.send(barrier).await.unwrap();
    let (ack_tx, ack_rx) = tokio::sync::oneshot::channel();
    control_tx
        .send(WriterControl::SettingsApplied {
            settings: Settings {
                revision: "1".to_string(),
                ..Settings::default()
            },
            at_utc_ms: T0 + 1_000,
            barrier_id,
            expected_revision: 0,
            ack: ack_tx,
        })
        .await
        .unwrap();
    ack_rx.await.expect("ack").expect("第二次必须成功");
    let errors = fixture.shared.errors();
    assert!(
        !errors.contains_key(&ErrorSource::Settings),
        "成功后必须清除 Settings 来源的过期错误"
    );
    assert!(
        errors.contains_key(&ErrorSource::Checkpoint),
        "成功不得清除 Checkpoint 等其他来源"
    );

    drop(control_tx);
    drop(data_tx);
    let _ = run.await;
}

/// 审核 P1-01 必测场景 9：crash-consistent 顺序——成功后 DB revision 与双槽同时前进，
/// 双槽可通过 DB metadata 交叉验证。
#[tokio::test(start_paused = true)]
async fn successful_apply_advances_db_and_backup_together() {
    let dir = TempDir::new().unwrap();
    let fixture = fixture(&dir);
    let (data_tx, data_rx) = mpsc::channel(8);
    let (control_tx, control_rx) = mpsc::channel(8);
    let config_dir = dir.path().join("config");

    let task = WriterTask::new(
        fixture.writer,
        fixture.engine,
        fixture.shared.clone(),
        fixture.capture_state_tx,
        fixture.continuity.clone(),
        config_dir.clone(),
        wuji_rebuild_agent::pipeline_health::PipelineHealth::new(),
    );
    let run = tokio::spawn(async move { task.run(data_rx, control_rx).await });

    let new_settings = Settings {
        revision: "1".to_string(),
        idle_threshold_seconds: 90,
        ..Settings::default()
    };
    let expected_digest = new_settings.content_digest();
    let (barrier_id, barrier) = settings_barrier();
    data_tx.send(barrier).await.unwrap();
    let (ack_tx, ack_rx) = tokio::sync::oneshot::channel();
    control_tx
        .send(WriterControl::SettingsApplied {
            settings: new_settings,
            at_utc_ms: T0,
            barrier_id,
            expected_revision: 0,
            ack: ack_tx,
        })
        .await
        .unwrap();
    ack_rx.await.expect("ack").expect("apply 应成功");

    assert_eq!(max_db_revision(&dir), 1, "DB revision 必须前进");
    let matched = wuji_rebuild_agent::settings_backup::read_backup_matching(
        &config_dir,
        Some(&(1, expected_digest)),
    );
    assert!(
        matched.is_some(),
        "DB 提交后双槽必须能被 DB metadata 交叉验证恢复"
    );
    assert_eq!(matched.unwrap().idle_threshold_seconds, 90);

    drop(control_tx);
    drop(data_tx);
    let _ = run.await;
}
