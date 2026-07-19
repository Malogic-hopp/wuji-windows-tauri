//! V01-4 黄金样本守恒测试（09 §11 V01-4 退出条件、§12.1 领域门禁）。
//!
//! 覆盖：零时长首样本、采样切换不归属、Idle pending、Work break、app switch、
//! restart、clock change、UTC/local/DST、隐私排除、capture_error、Unknown、
//! Pause/Stop/Sleep/Lock、queue drop、重放与 Today/Timeline 守恒交叉验证。

use std::sync::Arc;

use rusqlite::Connection;
use tempfile::TempDir;
use wuji_core::domain::{ActivityState, CaptureQuality};
use wuji_core::dto::RuntimeId;
use wuji_core::pipeline::FilteredObservation;
use wuji_core::settings::Settings;
use wuji_rebuild_agent::activity::{ActivityEngine, EngineEvent};
use wuji_rebuild_agent::capture_loop::ContinuityState;
use wuji_storage::{Reader, Writer};

/// 2026-07-18T00:00:00Z（毫秒）。
const T0: i64 = 1_784_332_800_000;
const SHANGHAI: &str = "Asia/Shanghai";

fn db_path(dir: &TempDir) -> std::path::PathBuf {
    dir.path().join("wuji-rebuild-v0.1.db")
}

fn bootstrap(dir: &TempDir, tz: &str) -> Writer {
    Writer::bootstrap_with_timezone(&db_path(dir), tz, T0).expect("bootstrap 应成功")
}

fn engine() -> (ActivityEngine, Arc<ContinuityState>) {
    engine_with_settings(Settings::default())
}

fn engine_with_settings(settings: Settings) -> (ActivityEngine, Arc<ContinuityState>) {
    let continuity = Arc::new(ContinuityState::default());
    let engine =
        ActivityEngine::new(RuntimeId::new(), settings, continuity.clone()).expect("engine 创建");
    (engine, continuity)
}

/// 每个测试在驱动前必须注册 engine 的 runtime 行（FK 约束）。
fn register_runtime(writer: &mut Writer, engine: &ActivityEngine) {
    let tx = writer.transaction().expect("tx");
    tx.insert_runtime(engine.runtime_id(), T0)
        .expect("insert runtime");
    tx.commit().expect("commit runtime");
}

fn mk_obs(
    sequence: u64,
    epoch: u64,
    name: &str,
    state: ActivityState,
    utc_ms: i64,
    mono_ms: u64,
) -> FilteredObservation {
    FilteredObservation {
        sequence,
        continuity_epoch: epoch,
        captured_at_utc_ms: utc_ms,
        captured_monotonic_ms: mono_ms,
        app_key: format!("proc:{:064x}", name.len() as u64 + 1),
        display_name: name.to_string(),
        normalized_process_name: format!("{}.exe", name.to_lowercase()),
        activity_state: state,
        quality: CaptureQuality::Normal,
    }
}

/// 便捷：同 app 连续采样序列（3 秒间隔，mono 与 utc 同步推进）。
fn obs_series(
    start_sequence: u64,
    epoch: u64,
    name: &str,
    state: ActivityState,
    start_utc_ms: i64,
    count: u64,
) -> Vec<EngineEvent> {
    (0..count)
        .map(|i| {
            let t = start_utc_ms + (i as i64) * 3_000;
            EngineEvent::Observation(mk_obs(
                start_sequence + i,
                epoch,
                name,
                state,
                t,
                (t - T0) as u64,
            ))
        })
        .collect()
}

fn drive(writer: &mut Writer, engine: &mut ActivityEngine, events: &[EngineEvent]) {
    for event in events {
        engine.handle(writer, event.clone()).expect("事件处理");
    }
}

fn conn(dir: &TempDir) -> Connection {
    Connection::open(db_path(dir)).unwrap()
}

fn query_i64(conn: &Connection, sql: &str, params: impl rusqlite::Params) -> i64 {
    conn.query_row(sql, params, |r| r.get(0)).unwrap()
}

#[test]
fn first_observation_creates_zero_duration_segment_only() {
    let dir = TempDir::new().unwrap();
    let mut writer = bootstrap(&dir, SHANGHAI);
    let (mut engine, _) = engine();
    register_runtime(&mut writer, &engine);

    drive(
        &mut writer,
        &mut engine,
        &[EngineEvent::Observation(mk_obs(
            1,
            0,
            "code",
            ActivityState::Active,
            T0,
            0,
        ))],
    );

    let conn = conn(&dir);
    let (duration, status): (i64, String) = conn
        .query_row(
            "SELECT duration_ms, status FROM activity_segments",
            [],
            |r| Ok((r.get(0)?, r.get(1)?)),
        )
        .unwrap();
    assert_eq!(duration, 0, "单条 Observation 只产生零时长 Segment");
    assert_eq!(status, "open");
    assert_eq!(query_i64(&conn, "SELECT COUNT(*) FROM work_blocks", []), 0);
    assert_eq!(
        query_i64(&conn, "SELECT COUNT(*) FROM hourly_app_usage", []),
        0,
        "零时长 Segment 不进入 segment_count"
    );
    assert_eq!(
        query_i64(&conn, "SELECT COUNT(*) FROM daily_app_usage", []),
        0
    );
    assert_eq!(
        query_i64(&conn, "SELECT COUNT(*) FROM foreground_observations", []),
        1
    );
}

#[test]
fn same_app_attribution_extends_segment_and_opens_work() {
    let dir = TempDir::new().unwrap();
    let mut writer = bootstrap(&dir, SHANGHAI);
    let (mut engine, _) = engine();
    register_runtime(&mut writer, &engine);

    drive(
        &mut writer,
        &mut engine,
        &obs_series(1, 0, "code", ActivityState::Active, T0, 2),
    );

    let conn = conn(&dir);
    let duration: i64 = conn
        .query_row("SELECT duration_ms FROM activity_segments", [], |r| {
            r.get(0)
        })
        .unwrap();
    assert_eq!(duration, 3_000);
    let (active, status): (i64, String) = conn
        .query_row(
            "SELECT active_duration_ms, status FROM work_blocks",
            [],
            |r| Ok((r.get(0)?, r.get(1)?)),
        )
        .unwrap();
    assert_eq!(active, 3_000);
    assert_eq!(status, "open");
    assert_eq!(
        query_i64(
            &conn,
            "SELECT active_duration_ms FROM hourly_app_usage WHERE utc_hour_start_ms = ?1",
            [T0]
        ),
        3_000
    );
}

#[test]
fn app_switch_records_sampling_transition_and_keeps_work_block() {
    let dir = TempDir::new().unwrap();
    let mut writer = bootstrap(&dir, SHANGHAI);
    let (mut engine, _) = engine();
    register_runtime(&mut writer, &engine);

    drive(
        &mut writer,
        &mut engine,
        &obs_series(1, 0, "code", ActivityState::Active, T0, 2),
    );
    drive(
        &mut writer,
        &mut engine,
        &[EngineEvent::Observation(mk_obs(
            3,
            0,
            "notepad",
            ActivityState::Active,
            T0 + 6_000,
            6_000,
        ))],
    );

    let conn = conn(&dir);
    let (end, reason, status): (i64, String, String) = conn
        .query_row(
            "SELECT end_at_utc_ms, close_reason, status FROM activity_segments ORDER BY segment_id LIMIT 1",
            [],
            |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?)),
        )
        .unwrap();
    assert_eq!(end, T0 + 3_000, "旧段在上一条 Observation 时刻关闭");
    assert_eq!(reason, "app_changed");
    assert_eq!(status, "closed");

    let (kind, gs, ge, gstatus): (String, i64, i64, String) = conn
        .query_row(
            "SELECT kind, start_at_utc_ms, end_at_utc_ms, status FROM capture_gaps",
            [],
            |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?, r.get(3)?)),
        )
        .unwrap();
    assert_eq!(kind, "sampling_transition");
    assert_eq!((gs, ge), (T0 + 3_000, T0 + 6_000));
    assert_eq!(gstatus, "closed");

    let work_status: String = conn
        .query_row("SELECT status FROM work_blocks", [], |r| r.get(0))
        .unwrap();
    assert_eq!(work_status, "open", "App 切换不结束 Work Block");

    assert_eq!(
        query_i64(
            &conn,
            "SELECT raw_app_switch_count FROM daily_work_metrics WHERE local_date = '2026-07-18'",
            []
        ),
        1
    );
}

#[test]
fn idle_pending_then_resume_counts_short_idle() {
    let dir = TempDir::new().unwrap();
    let mut writer = bootstrap(&dir, SHANGHAI);
    let settings = Settings {
        work_break_idle_seconds: 120,
        ..Settings::default()
    };
    let (mut engine, _) = engine_with_settings(settings);
    register_runtime(&mut writer, &engine);

    drive(
        &mut writer,
        &mut engine,
        &obs_series(1, 0, "code", ActivityState::Active, T0, 2),
    );
    drive(
        &mut writer,
        &mut engine,
        &obs_series(3, 0, "code", ActivityState::Idle, T0 + 6_000, 3),
    );
    drive(
        &mut writer,
        &mut engine,
        &[EngineEvent::Observation(mk_obs(
            6,
            0,
            "code",
            ActivityState::Active,
            T0 + 15_000,
            15_000,
        ))],
    );

    let conn = conn(&dir);
    let (active, short_idle, status): (i64, i64, String) = conn
        .query_row(
            "SELECT active_duration_ms, short_idle_duration_ms, status FROM work_blocks",
            [],
            |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?)),
        )
        .unwrap();
    assert_eq!(active, 3_000);
    assert_eq!(
        short_idle, 6_000,
        "阈值前恢复 Active，已归属 idle 计为 short idle"
    );
    assert_eq!(status, "open");

    let idle_duration: i64 = conn
        .query_row(
            "SELECT duration_ms FROM activity_segments WHERE activity_state = 'idle'",
            [],
            |r| r.get(0),
        )
        .unwrap();
    assert_eq!(idle_duration, 6_000);
}

#[test]
fn idle_break_closes_work_retroactively_at_idle_start() {
    let dir = TempDir::new().unwrap();
    let mut writer = bootstrap(&dir, SHANGHAI);
    let settings = Settings {
        work_break_idle_seconds: 120,
        ..Settings::default()
    };
    let (mut engine, _) = engine_with_settings(settings);
    register_runtime(&mut writer, &engine);

    drive(
        &mut writer,
        &mut engine,
        &obs_series(1, 0, "code", ActivityState::Active, T0, 2),
    );
    // 41 条 idle：pending 累计 40×3s = 120s，达到 break 阈值。
    drive(
        &mut writer,
        &mut engine,
        &obs_series(3, 0, "code", ActivityState::Idle, T0 + 6_000, 41),
    );

    let conn = conn(&dir);
    let (end, reason, status, short_idle, active): (i64, String, String, i64, i64) = conn
        .query_row(
            "SELECT end_at_utc_ms, close_reason, status, short_idle_duration_ms, active_duration_ms FROM work_blocks",
            [],
            |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?, r.get(3)?, r.get(4)?)),
        )
        .unwrap();
    assert_eq!(reason, "idle_break");
    assert_eq!(status, "closed");
    assert_eq!(end, T0 + 6_000, "Work Block 回溯结束于 Idle 开始");
    assert_eq!(short_idle, 0, "整段 Idle 不进入该 Work Block");
    assert_eq!(active, 3_000);

    // idle segment 继续独立延伸。
    let idle_status: String = conn
        .query_row(
            "SELECT status FROM activity_segments WHERE activity_state = 'idle'",
            [],
            |r| r.get(0),
        )
        .unwrap();
    assert_eq!(idle_status, "open");
}

#[test]
fn capture_delayed_over_gap_cap_closes_continuity() {
    let dir = TempDir::new().unwrap();
    let mut writer = bootstrap(&dir, SHANGHAI);
    let (mut engine, _) = engine();
    register_runtime(&mut writer, &engine);

    drive(
        &mut writer,
        &mut engine,
        &obs_series(1, 0, "code", ActivityState::Active, T0, 1),
    );
    drive(
        &mut writer,
        &mut engine,
        &[EngineEvent::Observation(mk_obs(
            2,
            0,
            "code",
            ActivityState::Active,
            T0 + 20_000,
            20_000,
        ))],
    );

    let conn = conn(&dir);
    let reason: String = conn
        .query_row(
            "SELECT close_reason FROM activity_segments ORDER BY segment_id LIMIT 1",
            [],
            |r| r.get(0),
        )
        .unwrap();
    assert_eq!(reason, "capture_delayed");
    let (kind, gs, ge): (String, i64, i64) = conn
        .query_row(
            "SELECT kind, start_at_utc_ms, end_at_utc_ms FROM capture_gaps",
            [],
            |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?)),
        )
        .unwrap();
    assert_eq!(kind, "capture_delayed");
    assert_eq!((gs, ge), (T0, T0 + 20_000));
    assert_eq!(
        query_i64(&conn, "SELECT COUNT(*) FROM work_blocks", []),
        0,
        "缺口不补算，无正 active 则不建块"
    );
}

#[test]
fn queue_drop_epoch_change_closes_continuity_with_gap() {
    let dir = TempDir::new().unwrap();
    let mut writer = bootstrap(&dir, SHANGHAI);
    let (mut engine, continuity) = engine();
    register_runtime(&mut writer, &engine);

    drive(
        &mut writer,
        &mut engine,
        &obs_series(1, 0, "code", ActivityState::Active, T0, 2),
    );
    continuity.note_capture_drop();
    drive(
        &mut writer,
        &mut engine,
        &[EngineEvent::Observation(mk_obs(
            3,
            1,
            "code",
            ActivityState::Active,
            T0 + 6_000,
            6_000,
        ))],
    );

    let conn = conn(&dir);
    let (seg_reason, work_reason): (String, String) = conn
        .query_row(
            "SELECT (SELECT close_reason FROM activity_segments ORDER BY segment_id LIMIT 1),
                    (SELECT close_reason FROM work_blocks ORDER BY work_block_id LIMIT 1)",
            [],
            |r| Ok((r.get(0)?, r.get(1)?)),
        )
        .unwrap();
    assert_eq!(seg_reason, "queue_drop");
    assert_eq!(work_reason, "queue_drop");
    let (kind, gs, ge): (String, i64, i64) = conn
        .query_row(
            "SELECT kind, start_at_utc_ms, end_at_utc_ms FROM capture_gaps",
            [],
            |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?)),
        )
        .unwrap();
    assert_eq!(kind, "capture_queue_drop");
    assert_eq!((gs, ge), (T0 + 3_000, T0 + 6_000));
    let new_epoch: i64 = conn
        .query_row(
            "SELECT continuity_epoch FROM activity_segments ORDER BY segment_id DESC LIMIT 1",
            [],
            |r| r.get(0),
        )
        .unwrap();
    assert_eq!(new_epoch, 1);
}

#[test]
fn clock_backward_produces_zero_length_gap_and_no_negative_time() {
    let dir = TempDir::new().unwrap();
    let mut writer = bootstrap(&dir, SHANGHAI);
    let (mut engine, continuity) = engine();
    register_runtime(&mut writer, &engine);

    drive(
        &mut writer,
        &mut engine,
        &obs_series(1, 0, "code", ActivityState::Active, T0, 2),
    );
    // UTC 回拨 3 秒：utc 倒退，mono 正常向前。
    drive(
        &mut writer,
        &mut engine,
        &[EngineEvent::Observation(mk_obs(
            3,
            0,
            "code",
            ActivityState::Active,
            T0,
            6_000,
        ))],
    );

    assert_eq!(continuity.current_epoch(), 1, "时钟异常必须增加 epoch");
    let conn = conn(&dir);
    let (kind, gs, ge): (String, i64, i64) = conn
        .query_row(
            "SELECT kind, start_at_utc_ms, end_at_utc_ms FROM capture_gaps",
            [],
            |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?)),
        )
        .unwrap();
    assert_eq!(kind, "clock_changed");
    assert_eq!(
        (gs, ge),
        (T0 + 3_000, T0 + 3_000),
        "回拨保存旧端点零长度 gap"
    );
    assert_eq!(
        query_i64(
            &conn,
            "SELECT COUNT(*) FROM activity_segments WHERE duration_ms < 0",
            []
        ),
        0,
        "不允许负时长"
    );
    let reason: String = conn
        .query_row(
            "SELECT close_reason FROM activity_segments ORDER BY segment_id LIMIT 1",
            [],
            |r| r.get(0),
        )
        .unwrap();
    assert_eq!(reason, "clock_changed");
}

#[test]
fn clock_skew_between_utc_and_monotonic_is_detected() {
    let dir = TempDir::new().unwrap();
    let mut writer = bootstrap(&dir, SHANGHAI);
    let (mut engine, _) = engine();
    register_runtime(&mut writer, &engine);

    drive(
        &mut writer,
        &mut engine,
        &[EngineEvent::Observation(mk_obs(
            1,
            0,
            "code",
            ActivityState::Active,
            T0,
            0,
        ))],
    );
    // utc +10s 但 mono 只 +3s：偏差 7s > 2s 容差。
    drive(
        &mut writer,
        &mut engine,
        &[EngineEvent::Observation(mk_obs(
            2,
            0,
            "code",
            ActivityState::Active,
            T0 + 10_000,
            3_000,
        ))],
    );

    let conn = conn(&dir);
    let kind: String = conn
        .query_row("SELECT kind FROM capture_gaps", [], |r| r.get(0))
        .unwrap();
    assert_eq!(kind, "clock_changed");
}

#[test]
fn privacy_excluded_merges_events_and_resumes_cleanly() {
    let dir = TempDir::new().unwrap();
    let mut writer = bootstrap(&dir, SHANGHAI);
    let (mut engine, _) = engine();
    register_runtime(&mut writer, &engine);

    drive(
        &mut writer,
        &mut engine,
        &obs_series(1, 0, "code", ActivityState::Active, T0, 2),
    );
    drive(
        &mut writer,
        &mut engine,
        &[
            EngineEvent::PrivacyExcluded {
                captured_at_utc_ms: T0 + 6_000,
            },
            EngineEvent::PrivacyExcluded {
                captured_at_utc_ms: T0 + 9_000,
            },
        ],
    );
    drive(
        &mut writer,
        &mut engine,
        &[EngineEvent::Observation(mk_obs(
            3,
            0,
            "code",
            ActivityState::Active,
            T0 + 12_000,
            12_000,
        ))],
    );

    let conn = conn(&dir);
    let (kind, count, gs, ge, status): (String, i64, i64, i64, String) = conn
        .query_row(
            "SELECT kind, event_count, start_at_utc_ms, end_at_utc_ms, status FROM capture_gaps",
            [],
            |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?, r.get(3)?, r.get(4)?)),
        )
        .unwrap();
    assert_eq!(kind, "privacy_excluded");
    assert_eq!(count, 2, "同类相邻 gap 合并累计");
    assert_eq!(gs, T0 + 6_000);
    assert_eq!(ge, T0 + 12_000);
    assert_eq!(status, "closed");
    assert_eq!(
        query_i64(&conn, "SELECT COUNT(*) FROM foreground_observations", []),
        3,
        "排除 App 不产生 Observation"
    );
    let work_reason: String = conn
        .query_row("SELECT close_reason FROM work_blocks", [], |r| r.get(0))
        .unwrap();
    assert_eq!(work_reason, "privacy_excluded");
}

#[test]
fn capture_error_writes_gap_without_process_info() {
    let dir = TempDir::new().unwrap();
    let mut writer = bootstrap(&dir, SHANGHAI);
    let (mut engine, _) = engine();
    register_runtime(&mut writer, &engine);

    drive(
        &mut writer,
        &mut engine,
        &obs_series(1, 0, "code", ActivityState::Active, T0, 1),
    );
    drive(
        &mut writer,
        &mut engine,
        &[EngineEvent::CaptureError {
            captured_at_utc_ms: T0 + 3_000,
        }],
    );
    drive(
        &mut writer,
        &mut engine,
        &[EngineEvent::Observation(mk_obs(
            2,
            0,
            "notepad",
            ActivityState::Active,
            T0 + 6_000,
            6_000,
        ))],
    );

    let conn = conn(&dir);
    let (kind, status): (String, String) = conn
        .query_row("SELECT kind, status FROM capture_gaps", [], |r| {
            Ok((r.get(0)?, r.get(1)?))
        })
        .unwrap();
    assert_eq!(kind, "capture_error");
    assert_eq!(status, "closed");
    let seg_reason: String = conn
        .query_row(
            "SELECT close_reason FROM activity_segments ORDER BY segment_id LIMIT 1",
            [],
            |r| r.get(0),
        )
        .unwrap();
    assert_eq!(seg_reason, "capture_error");
}

#[test]
fn unknown_observation_closes_work_block_only() {
    let dir = TempDir::new().unwrap();
    let mut writer = bootstrap(&dir, SHANGHAI);
    let (mut engine, _) = engine();
    register_runtime(&mut writer, &engine);

    drive(
        &mut writer,
        &mut engine,
        &obs_series(1, 0, "code", ActivityState::Active, T0, 2),
    );
    drive(
        &mut writer,
        &mut engine,
        &obs_series(3, 0, "code", ActivityState::Unknown, T0 + 6_000, 2),
    );
    drive(
        &mut writer,
        &mut engine,
        &[EngineEvent::Observation(mk_obs(
            5,
            0,
            "code",
            ActivityState::Active,
            T0 + 12_000,
            12_000,
        ))],
    );

    let conn = conn(&dir);
    let reason: String = conn
        .query_row("SELECT close_reason FROM work_blocks", [], |r| r.get(0))
        .unwrap();
    assert_eq!(reason, "unknown");
    let unknown_duration: i64 = conn
        .query_row(
            "SELECT duration_ms FROM activity_segments WHERE activity_state = 'unknown'",
            [],
            |r| r.get(0),
        )
        .unwrap();
    assert_eq!(unknown_duration, 3_000);
    assert_eq!(
        query_i64(
            &conn,
            "SELECT unknown_duration_ms FROM daily_app_usage WHERE local_date = '2026-07-18'",
            []
        ),
        3_000
    );
}

#[test]
fn pause_gap_closes_at_first_observation_after_resume() {
    let dir = TempDir::new().unwrap();
    let mut writer = bootstrap(&dir, SHANGHAI);
    let (mut engine, _) = engine();
    register_runtime(&mut writer, &engine);

    drive(
        &mut writer,
        &mut engine,
        &obs_series(1, 0, "code", ActivityState::Active, T0, 2),
    );
    drive(
        &mut writer,
        &mut engine,
        &[EngineEvent::CapturePaused {
            at_utc_ms: T0 + 6_000,
        }],
    );
    drive(
        &mut writer,
        &mut engine,
        &[EngineEvent::Observation(mk_obs(
            3,
            0,
            "code",
            ActivityState::Active,
            T0 + 60_000,
            60_000,
        ))],
    );

    let conn = conn(&dir);
    let (kind, gs, ge, status): (String, i64, i64, String) = conn
        .query_row(
            "SELECT kind, start_at_utc_ms, end_at_utc_ms, status FROM capture_gaps",
            [],
            |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?, r.get(3)?)),
        )
        .unwrap();
    assert_eq!(kind, "capture_paused");
    assert_eq!((gs, ge), (T0 + 6_000, T0 + 60_000));
    assert_eq!(status, "closed");
    let work_reason: String = conn
        .query_row("SELECT close_reason FROM work_blocks", [], |r| r.get(0))
        .unwrap();
    assert_eq!(work_reason, "capture_paused");
}

#[test]
fn sleep_during_pause_keeps_original_gap() {
    let dir = TempDir::new().unwrap();
    let mut writer = bootstrap(&dir, SHANGHAI);
    let (mut engine, _) = engine();
    register_runtime(&mut writer, &engine);

    drive(
        &mut writer,
        &mut engine,
        &obs_series(1, 0, "code", ActivityState::Active, T0, 2),
    );
    drive(
        &mut writer,
        &mut engine,
        &[EngineEvent::CapturePaused {
            at_utc_ms: T0 + 6_000,
        }],
    );
    drive(
        &mut writer,
        &mut engine,
        &[EngineEvent::SystemSleep {
            at_utc_ms: T0 + 60_000,
        }],
    );
    drive(
        &mut writer,
        &mut engine,
        &[EngineEvent::Observation(mk_obs(
            3,
            0,
            "code",
            ActivityState::Active,
            T0 + 90_000,
            90_000,
        ))],
    );

    let conn = conn(&dir);
    assert_eq!(
        query_i64(&conn, "SELECT COUNT(*) FROM capture_gaps", []),
        1,
        "paused 期间的 sleep 不产生第二个 gap"
    );
    let (kind, ge): (String, i64) = conn
        .query_row("SELECT kind, end_at_utc_ms FROM capture_gaps", [], |r| {
            Ok((r.get(0)?, r.get(1)?))
        })
        .unwrap();
    assert_eq!(kind, "capture_paused");
    assert_eq!(ge, T0 + 90_000);
}

#[test]
fn startup_recovery_closes_legacy_open_rows() {
    let dir = TempDir::new().unwrap();
    let mut writer = bootstrap(&dir, SHANGHAI);

    {
        let (mut engine1, _) = engine();
        register_runtime(&mut writer, &engine1);
        drive(
            &mut writer,
            &mut engine1,
            &obs_series(1, 0, "code", ActivityState::Active, T0, 2),
        );
        drive(
            &mut writer,
            &mut engine1,
            &[EngineEvent::PrivacyExcluded {
                captured_at_utc_ms: T0 + 6_000,
            }],
        );
        // engine1 直接丢弃：模拟崩溃（open segment/work/gap 遗留）。
    }

    let (mut engine2, _) = engine();
    // engine2 不再预注册：recover_startup 在同一事务内插入新 runtime（09 §6.7）。
    engine2
        .recover_startup(&mut writer, T0 + 600_000)
        .expect("启动恢复");

    let conn = conn(&dir);
    // engine1 的 segment/work 在其 privacy 边界已被自己关闭（recovery 不改动历史行）。
    let (seg_reason, work_reason): (String, String) = conn
        .query_row(
            "SELECT (SELECT close_reason FROM activity_segments ORDER BY segment_id LIMIT 1),
                    (SELECT close_reason FROM work_blocks ORDER BY work_block_id LIMIT 1)",
            [],
            |r| Ok((r.get(0)?, r.get(1)?)),
        )
        .unwrap();
    assert_eq!(seg_reason, "privacy_excluded");
    assert_eq!(work_reason, "privacy_excluded");

    // 遗留 privacy gap 按原 kind 关闭且不早于其 start；agent_restart gap 打开。
    let mut stmt = conn
        .prepare(
            "SELECT kind, status, end_at_utc_ms, start_at_utc_ms FROM capture_gaps ORDER BY gap_id",
        )
        .unwrap();
    let gaps: Vec<(String, String, Option<i64>, i64)> = stmt
        .query_map([], |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?, r.get(3)?)))
        .unwrap()
        .collect::<rusqlite::Result<Vec<_>>>()
        .unwrap();
    assert_eq!(gaps.len(), 2);
    assert_eq!(gaps[0].0, "privacy_excluded");
    assert_eq!(gaps[0].1, "closed");
    assert!(
        gaps[0].2.unwrap() >= gaps[0].3,
        "遗留 gap 不得早于其 start 关闭"
    );
    assert_eq!(gaps[1].0, "agent_restart");
    assert_eq!(gaps[1].1, "open");

    // 新 runtime 首条有效 Observation 关闭 agent_restart gap。
    drive(
        &mut writer,
        &mut engine2,
        &[EngineEvent::Observation(mk_obs(
            10,
            0,
            "code",
            ActivityState::Active,
            T0 + 603_000,
            603_000,
        ))],
    );
    let status: String = conn
        .query_row(
            "SELECT status FROM capture_gaps WHERE kind = 'agent_restart'",
            [],
            |r| r.get(0),
        )
        .unwrap();
    assert_eq!(status, "closed");
    let runtimes: i64 = query_i64(&conn, "SELECT COUNT(*) FROM agent_runtime", []);
    assert_eq!(runtimes, 3, "bootstrap + engine1 + engine2 三个 runtime");
}

#[test]
fn dst_fall_back_engine_recompute_keeps_both_hours() {
    let dir = TempDir::new().unwrap();
    let mut writer = bootstrap(&dir, "America/New_York");
    let (mut engine, _) = engine();
    register_runtime(&mut writer, &engine);

    let h05 = 1_793_509_200_000_i64;
    let h06 = 1_793_512_800_000_i64;
    drive(
        &mut writer,
        &mut engine,
        &obs_series(1, 0, "code", ActivityState::Active, h05 + 600_000, 200),
    );
    drive(
        &mut writer,
        &mut engine,
        &obs_series(201, 0, "code", ActivityState::Active, h06 + 600_000, 200),
    );

    let conn = conn(&dir);
    let mut stmt = conn
        .prepare(
            "SELECT local_utc_offset_minutes, active_duration_ms FROM hourly_app_usage
             ORDER BY utc_hour_start_ms",
        )
        .unwrap();
    let rows: Vec<(i64, i64)> = stmt
        .query_map([], |r| Ok((r.get(0)?, r.get(1)?)))
        .unwrap()
        .collect::<rusqlite::Result<Vec<_>>>()
        .unwrap();
    assert_eq!(rows.len(), 2, "DST fallback 两个同名 local hour 不合并");
    assert_eq!(rows[0], (-240, 597_000));
    assert_eq!(rows[1], (-300, 597_000));
}

#[test]
fn conservation_cross_check_today_timeline_and_daily() {
    let dir = TempDir::new().unwrap();
    let mut writer = bootstrap(&dir, SHANGHAI);
    let (mut engine, _) = engine();
    register_runtime(&mut writer, &engine);

    // A active 10 分钟；切到 B 10 分钟；B idle 2 分钟（pending 恢复）；B active 8 分钟；
    // pause 10 分钟；B active 5 分钟；B unknown 3 分钟；A active 9 分钟。
    drive(
        &mut writer,
        &mut engine,
        &obs_series(1, 0, "code", ActivityState::Active, T0, 200),
    );
    drive(
        &mut writer,
        &mut engine,
        &obs_series(201, 0, "notepad", ActivityState::Active, T0 + 600_000, 200),
    );
    drive(
        &mut writer,
        &mut engine,
        &obs_series(401, 0, "notepad", ActivityState::Idle, T0 + 1_200_000, 40),
    );
    drive(
        &mut writer,
        &mut engine,
        &obs_series(
            441,
            0,
            "notepad",
            ActivityState::Active,
            T0 + 1_320_000,
            160,
        ),
    );
    drive(
        &mut writer,
        &mut engine,
        &[EngineEvent::CapturePaused {
            at_utc_ms: T0 + 1_800_000,
        }],
    );
    drive(
        &mut writer,
        &mut engine,
        &obs_series(
            601,
            0,
            "notepad",
            ActivityState::Active,
            T0 + 2_400_000,
            100,
        ),
    );
    drive(
        &mut writer,
        &mut engine,
        &obs_series(
            701,
            0,
            "notepad",
            ActivityState::Unknown,
            T0 + 2_700_000,
            60,
        ),
    );
    drive(
        &mut writer,
        &mut engine,
        &obs_series(761, 0, "code", ActivityState::Active, T0 + 3_060_000, 180),
    );

    let conn = conn(&dir);
    // 各层时长一致：daily_app_usage 总和 == hourly 总和 == Segment 交集总和。
    let (daily_active, daily_idle, daily_unknown): (i64, i64, i64) = conn
        .query_row(
            "SELECT COALESCE(SUM(active_duration_ms),0), COALESCE(SUM(idle_duration_ms),0),
                    COALESCE(SUM(unknown_duration_ms),0)
             FROM daily_app_usage WHERE local_date = '2026-07-18'",
            [],
            |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?)),
        )
        .unwrap();
    let (hourly_active, hourly_idle, hourly_unknown): (i64, i64, i64) = conn
        .query_row(
            "SELECT COALESCE(SUM(active_duration_ms),0), COALESCE(SUM(idle_duration_ms),0),
                    COALESCE(SUM(unknown_duration_ms),0)
             FROM hourly_app_usage",
            [],
            |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?)),
        )
        .unwrap();
    let (seg_active, seg_idle, seg_unknown): (i64, i64, i64) = conn
        .query_row(
            "SELECT COALESCE(SUM(CASE WHEN activity_state='active' THEN duration_ms END),0),
                    COALESCE(SUM(CASE WHEN activity_state='idle' THEN duration_ms END),0),
                    COALESCE(SUM(CASE WHEN activity_state='unknown' THEN duration_ms END),0)
             FROM activity_segments",
            [],
            |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?)),
        )
        .unwrap();
    assert_eq!(daily_active, seg_active);
    assert_eq!(hourly_active, seg_active);
    assert_eq!(daily_idle, seg_idle);
    assert_eq!(hourly_idle, seg_idle);
    assert_eq!(daily_unknown, seg_unknown);
    assert_eq!(hourly_unknown, seg_unknown);

    // 期望值：A active 200×3s-3s（首条零时长）= 597s；B active 200+160+100=460 段同理。
    let expected_a_active = (200_i64 - 1) * 3_000 + (180_i64 - 1) * 3_000;
    let expected_b_active = (200_i64 + 160 + 100 - 3) * 3_000;
    assert_eq!(seg_active, expected_a_active + expected_b_active);
    let expected_idle = (40_i64 - 1) * 3_000;
    assert_eq!(seg_idle, expected_idle);
    let expected_unknown = (60_i64 - 1) * 3_000;
    assert_eq!(seg_unknown, expected_unknown);

    // daily_work_metrics.active == 当日 Work Block active 交集总和 == 全部 active（均在块内）。
    let work_active: i64 = conn
        .query_row(
            "SELECT active_duration_ms FROM daily_work_metrics WHERE local_date = '2026-07-18'",
            [],
            |r| r.get(0),
        )
        .unwrap();
    assert_eq!(work_active, seg_active);

    // Today（读模型组装）与 Timeline（明细交集）守恒。
    let reader = Reader::open(&db_path(&dir)).unwrap();
    let today = reader
        .today(&wuji_core::dto::LocalDate::parse("2026-07-18").unwrap())
        .unwrap();
    assert_eq!(today.active_duration_ms.0, seg_active);
    assert!(!today.quality.is_complete, "有 paused gap，不应完整");
    assert_eq!(
        today.quality.gap_count.0, 2,
        "paused + capture_delayed 两条非 transition gap"
    );
    assert_eq!(today.raw_app_switch_count.0, 1, "仅 A→B 一次合法 switch");
}

#[test]
fn replayed_observation_is_skipped() {
    let dir = TempDir::new().unwrap();
    let mut writer = bootstrap(&dir, SHANGHAI);
    let (mut engine, _) = engine();
    register_runtime(&mut writer, &engine);

    drive(
        &mut writer,
        &mut engine,
        &obs_series(1, 0, "code", ActivityState::Active, T0, 2),
    );
    // 重放同一条（同 sequence）。
    drive(
        &mut writer,
        &mut engine,
        &[EngineEvent::Observation(mk_obs(
            2,
            0,
            "code",
            ActivityState::Active,
            T0 + 3_000,
            3_000,
        ))],
    );

    let conn = conn(&dir);
    let duration: i64 = conn
        .query_row("SELECT duration_ms FROM activity_segments", [], |r| {
            r.get(0)
        })
        .unwrap();
    assert_eq!(duration, 3_000, "重放不得重复累计");
    assert_eq!(
        query_i64(&conn, "SELECT COUNT(*) FROM foreground_observations", []),
        2
    );
}

#[test]
fn startup_recovery_closes_open_segment_and_work_with_agent_restart() {
    let dir = TempDir::new().unwrap();
    let mut writer = bootstrap(&dir, SHANGHAI);

    {
        let (mut engine1, _) = engine();
        register_runtime(&mut writer, &engine1);
        drive(
            &mut writer,
            &mut engine1,
            &obs_series(1, 0, "code", ActivityState::Active, T0, 2),
        );
        // 崩溃：open segment 与 open work block 遗留，无 gap。
    }

    let (mut engine2, _) = engine();
    engine2
        .recover_startup(&mut writer, T0 + 600_000)
        .expect("启动恢复");

    let conn = conn(&dir);
    let (seg_reason, seg_status): (String, String) = conn
        .query_row(
            "SELECT close_reason, status FROM activity_segments",
            [],
            |r| Ok((r.get(0)?, r.get(1)?)),
        )
        .unwrap();
    assert_eq!(seg_reason, "agent_restart");
    assert_eq!(seg_status, "closed");
    let (work_reason, work_status): (String, String) = conn
        .query_row("SELECT close_reason, status FROM work_blocks", [], |r| {
            Ok((r.get(0)?, r.get(1)?))
        })
        .unwrap();
    assert_eq!(work_reason, "agent_restart");
    assert_eq!(work_status, "closed");

    // agent_restart gap 打开且 start 不早于遗留 segment 的已提交端点。
    let (gap_status, gap_start): (String, i64) = conn
        .query_row(
            "SELECT status, start_at_utc_ms FROM capture_gaps WHERE kind = 'agent_restart'",
            [],
            |r| Ok((r.get(0)?, r.get(1)?)),
        )
        .unwrap();
    assert_eq!(gap_status, "open");
    assert!(gap_start >= T0 + 3_000);
}
