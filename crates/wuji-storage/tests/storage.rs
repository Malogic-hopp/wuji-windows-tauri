//! V01-2 临时库测试（09 §11 退出条件）：
//! Schema 原样执行、空库 bootstrap、FK/CHECK、事务回滚、只读 reader、
//! 并发 WAL、触及桶重算幂等。

use rusqlite::Connection;
use tempfile::TempDir;
use wuji_core::domain::{ActivityState, CaptureQuality, GapKind};
use wuji_core::dto::{LocalDate, RuntimeId, TimelineCursor};
use wuji_core::error::SafeErrorCode;
use wuji_core::settings::Settings;
use wuji_storage::connection::open_reader_connection;
use wuji_storage::writer::SCHEMA_SQL;
use wuji_storage::{ObservationInsert, Reader, Writer};

/// 2026-07-18T00:00:00Z（毫秒）。
const T0: i64 = 1_784_332_800_000;
const SHANGHAI: &str = "Asia/Shanghai";
const GAP_CAP_MS: i64 = 15_000;

fn db_path(dir: &TempDir) -> std::path::PathBuf {
    dir.path().join("wuji-rebuild-v0.1.db")
}

fn bootstrap(dir: &TempDir) -> Writer {
    Writer::bootstrap_with_timezone(&db_path(dir), SHANGHAI, T0).expect("bootstrap 应成功")
}

fn seed_app(tx: &wuji_storage::writer::StorageTransaction<'_>, name: &str, seen_at: i64) -> i64 {
    // 每个名字生成确定且互不相同的 app_key（仅测试用，不模拟真实 sha256）。
    let hash = name.bytes().fold(0xcbf29ce484222325_u64, |h, b| {
        (h ^ u64::from(b)).wrapping_mul(0x100000001b3)
    });
    let app_key = format!("proc:{hash:064x}");
    tx.upsert_app_identity(&app_key, name, &format!("{name}.exe"), seen_at)
        .expect("app upsert")
}

fn insert_obs(
    tx: &wuji_storage::writer::StorageTransaction<'_>,
    runtime: &RuntimeId,
    sequence: i64,
    captured: i64,
    app_id: i64,
    state: ActivityState,
) -> i64 {
    match tx.insert_observation(
        runtime,
        sequence,
        0,
        captured,
        captured - T0,
        app_id,
        state,
        CaptureQuality::Normal,
        0,
    ) {
        Ok(ObservationInsert::Inserted(id)) => id,
        other => panic!("observation 插入失败: {other:?}"),
    }
}

#[test]
fn bootstrap_creates_valid_database() {
    let dir = TempDir::new().unwrap();
    let writer = bootstrap(&dir);

    let meta = writer.schema_meta();
    assert_eq!(meta.schema_version, 2);
    assert_eq!(meta.algorithm_version, "rebuild-v0.1");
    assert_eq!(meta.reporting_time_zone_id, SHANGHAI);

    let conn = Connection::open(db_path(&dir)).unwrap();
    let digest: String = conn
        .query_row(
            "SELECT content_digest FROM settings_revisions WHERE revision = 0",
            [],
            |r| r.get(0),
        )
        .unwrap();
    assert_eq!(digest, Settings::default().content_digest());

    let runtime_count: i64 = conn
        .query_row("SELECT COUNT(*) FROM agent_runtime", [], |r| r.get(0))
        .unwrap();
    assert_eq!(runtime_count, 1);

    let journal: String = conn
        .query_row("PRAGMA journal_mode", [], |r| r.get(0))
        .unwrap();
    assert_eq!(journal, "wal");

    let table_count: i64 = conn
        .query_row(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'",
            [],
            |r| r.get(0),
        )
        .unwrap();
    assert_eq!(table_count, 11);

    // schema.sql 与内嵌常量一致（原样执行的证据）。
    assert!(SCHEMA_SQL.contains("STRICT"));
    assert!(SCHEMA_SQL.contains("WITHOUT ROWID"));
}

#[test]
fn bootstrap_refuses_existing_and_bad_timezone_cleans_up() {
    let dir = TempDir::new().unwrap();
    let _writer = bootstrap(&dir);
    let err = Writer::bootstrap_with_timezone(&db_path(&dir), SHANGHAI, T0).unwrap_err();
    assert_eq!(err.code, SafeErrorCode::DbUnavailable);

    let other = TempDir::new().unwrap();
    let err = Writer::bootstrap_with_timezone(&db_path(&other), "Not/AZone", T0).unwrap_err();
    assert_eq!(err.code, SafeErrorCode::TimeZoneUnavailable);
    assert!(std::fs::read_dir(other.path()).unwrap().next().is_none());
}

#[test]
fn open_existing_rejects_non_v01_database() {
    let dir = TempDir::new().unwrap();
    {
        let conn = Connection::open(db_path(&dir)).unwrap();
        conn.execute_batch("CREATE TABLE something (id INTEGER)")
            .unwrap();
    }
    let err = Writer::open_existing(&db_path(&dir)).unwrap_err();
    assert_eq!(err.code, SafeErrorCode::DbSchemaUnsupported);

    let err = Reader::open(&db_path(&dir)).unwrap_err();
    assert_eq!(err.code, SafeErrorCode::DbSchemaUnsupported);
}

#[test]
fn reader_is_read_only_and_missing_db_is_db_unavailable() {
    let dir = TempDir::new().unwrap();
    let _writer = bootstrap(&dir);

    let reader_conn = open_reader_connection(&db_path(&dir)).expect("reader 应能打开");
    let write_attempt = reader_conn.execute(
        "INSERT INTO settings_revisions (revision, content_digest, applied_at_utc_ms)
         VALUES (99, 'x', 0)",
        [],
    );
    assert!(write_attempt.is_err(), "只读 reader 不得写入");

    let missing = TempDir::new().unwrap();
    let err = Reader::open(&db_path(&missing)).unwrap_err();
    assert_eq!(err.code, SafeErrorCode::DbUnavailable);
}

#[test]
fn foreign_keys_and_single_open_row_are_enforced() {
    let dir = TempDir::new().unwrap();
    let mut writer = bootstrap(&dir);
    let runtime = RuntimeId::new();

    let tx = writer.transaction().unwrap();
    tx.insert_runtime(&runtime, T0).unwrap();
    tx.commit().unwrap();

    let tx = writer.transaction().unwrap();
    let fk_violation = tx.insert_observation(
        &runtime,
        1,
        0,
        T0,
        0,
        777,
        ActivityState::Active,
        CaptureQuality::Normal,
        0,
    );
    assert!(fk_violation.is_err(), "未知 app_id 必须触发 FK 失败");
    drop(tx);

    let tx = writer.transaction().unwrap();
    let app = seed_app(&tx, "notepad", T0);
    let obs = insert_obs(&tx, &runtime, 2, T0, app, ActivityState::Active);
    tx.open_segment(&runtime, 0, app, ActivityState::Active, T0, obs)
        .unwrap();
    let second_open = tx.open_segment(&runtime, 0, app, ActivityState::Active, T0 + 1, obs);
    assert!(second_open.is_err(), "第二个 open Segment 必须被拒绝");
}

#[test]
fn transaction_rollback_leaves_no_partial_batch() {
    let dir = TempDir::new().unwrap();
    let mut writer = bootstrap(&dir);
    let runtime = RuntimeId::new();

    {
        let tx = writer.transaction().unwrap();
        tx.insert_runtime(&runtime, T0).unwrap();
        let app = seed_app(&tx, "notepad", T0);
        insert_obs(&tx, &runtime, 1, T0, app, ActivityState::Active);
        // 强制一个失败（未知 runtime 的 FK 违规），整个事务不落盘。
        let boom = tx.insert_observation(
            &RuntimeId::new(),
            1,
            0,
            T0,
            0,
            app,
            ActivityState::Active,
            CaptureQuality::Normal,
            0,
        );
        assert!(boom.is_err());
        drop(tx);
    }

    let conn = Connection::open(db_path(&dir)).unwrap();
    let obs_count: i64 = conn
        .query_row("SELECT COUNT(*) FROM foreground_observations", [], |r| {
            r.get(0)
        })
        .unwrap();
    let app_count: i64 = conn
        .query_row("SELECT COUNT(*) FROM app_identities", [], |r| r.get(0))
        .unwrap();
    assert_eq!(obs_count, 0);
    assert_eq!(app_count, 0);
}

#[test]
fn observation_replay_is_idempotent() {
    let dir = TempDir::new().unwrap();
    let mut writer = bootstrap(&dir);
    let runtime = RuntimeId::new();

    let tx = writer.transaction().unwrap();
    tx.insert_runtime(&runtime, T0).unwrap();
    let app = seed_app(&tx, "notepad", T0);
    let first = tx
        .insert_observation(
            &runtime,
            7,
            0,
            T0,
            0,
            app,
            ActivityState::Active,
            CaptureQuality::Normal,
            0,
        )
        .unwrap();
    assert!(matches!(first, ObservationInsert::Inserted(_)));
    let replay = tx
        .insert_observation(
            &runtime,
            7,
            0,
            T0,
            0,
            app,
            ActivityState::Active,
            CaptureQuality::Normal,
            0,
        )
        .unwrap();
    assert_eq!(replay, ObservationInsert::AlreadyProcessed);
    tx.commit().unwrap();

    let conn = Connection::open(db_path(&dir)).unwrap();
    let count: i64 = conn
        .query_row("SELECT COUNT(*) FROM foreground_observations", [], |r| {
            r.get(0)
        })
        .unwrap();
    assert_eq!(count, 1);
}

struct DailyFixture {
    app_a: i64,
    app_b: i64,
}

/// 构造：app A active [01:30, 02:30)（跨两个 UTC 小时，含一次 A→B 合法 switch）、
/// app B active [02:30:03, 02:36)、B idle [03:00, 03:10)、一个覆盖 A 的 Work Block、
/// 当日一条 capture_paused gap。
fn seed_daily_fixture(writer: &mut Writer) -> DailyFixture {
    let runtime = RuntimeId::new();
    let tx = writer.transaction().unwrap();
    tx.insert_runtime(&runtime, T0).unwrap();
    let app_a = seed_app(&tx, "code", T0);
    let app_b = seed_app(&tx, "notepad", T0);

    let mut seq = 0_i64;
    let mut obs = |tx: &wuji_storage::writer::StorageTransaction<'_>,
                   at: i64,
                   app: i64,
                   state: ActivityState| {
        seq += 1;
        insert_obs(tx, &runtime, seq, at, app, state)
    };

    let a1 = obs(&tx, T0 + 5_400_000, app_a, ActivityState::Active);
    let _a2 = obs(&tx, T0 + 7_200_000, app_a, ActivityState::Active);
    let a3 = obs(&tx, T0 + 9_000_000, app_a, ActivityState::Active);
    let b1 = obs(&tx, T0 + 9_003_000, app_b, ActivityState::Active);
    let b2 = obs(&tx, T0 + 10_800_000, app_b, ActivityState::Idle);
    let b3 = obs(&tx, T0 + 11_400_000, app_b, ActivityState::Idle);

    let seg_a = tx
        .open_segment(
            &runtime,
            0,
            app_a,
            ActivityState::Active,
            T0 + 5_400_000,
            a1,
        )
        .unwrap();
    tx.update_open_segment(seg_a, T0 + 9_000_000, a3).unwrap();
    tx.close_open_segment("app_changed").unwrap();

    let work_block_id = tx.open_work_block(&runtime, T0 + 5_400_000, seg_a).unwrap();
    tx.update_open_work_block(work_block_id, T0 + 9_000_000, 3_600_000, 0, seg_a)
        .unwrap();
    tx.close_open_work_block("capture_stopped").unwrap();

    let seg_b_active = tx
        .open_segment(
            &runtime,
            0,
            app_b,
            ActivityState::Active,
            T0 + 9_003_000,
            b1,
        )
        .unwrap();
    tx.update_open_segment(seg_b_active, T0 + 9_360_000, b1)
        .unwrap();
    tx.close_open_segment("state_changed").unwrap();

    let seg_b_idle = tx
        .open_segment(&runtime, 0, app_b, ActivityState::Idle, T0 + 10_800_000, b2)
        .unwrap();
    tx.update_open_segment(seg_b_idle, T0 + 11_400_000, b3)
        .unwrap();
    tx.close_open_segment("capture_paused").unwrap();

    tx.open_gap(&runtime, GapKind::CapturePaused, T0 + 11_400_000)
        .unwrap();
    tx.close_open_gap(T0 + 12_000_000).unwrap();
    tx.commit().unwrap();

    DailyFixture { app_a, app_b }
}

#[test]
fn recompute_is_deterministic_and_covers_hours_and_dates() {
    let dir = TempDir::new().unwrap();
    let mut writer = bootstrap(&dir);
    let fixture = seed_daily_fixture(&mut writer);

    let tz = writer.schema_meta().reporting_tz().unwrap();
    let hour_01 = T0 + 3_600_000;
    let hour_02 = T0 + 7_200_000;
    let hour_03 = T0 + 10_800_000;
    let date = LocalDate::parse("2026-07-18").unwrap();

    let tx = writer.transaction().unwrap();
    tx.recompute_hours(&tz, &[hour_01, hour_02, hour_03])
        .unwrap();
    tx.recompute_dates(&tz, std::slice::from_ref(&date), GAP_CAP_MS)
        .unwrap();
    tx.commit().unwrap();

    let conn = Connection::open(db_path(&dir)).unwrap();

    // hourly：A active [01:30,02:30) → 01 桶 1800s、02 桶 1800s；B idle [03:00,03:10) → 03 桶 600s。
    let h1: (i64, i64, i64) = conn
        .query_row(
            "SELECT local_hour, local_utc_offset_minutes, active_duration_ms
             FROM hourly_app_usage WHERE utc_hour_start_ms = ?1 AND app_id = ?2",
            [hour_01, fixture.app_a],
            |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?)),
        )
        .unwrap();
    assert_eq!(h1, (9, 480, 1_800_000));
    let h2: i64 = conn
        .query_row(
            "SELECT active_duration_ms FROM hourly_app_usage WHERE utc_hour_start_ms = ?1 AND app_id = ?2",
            [hour_02, fixture.app_a],
            |r| r.get(0),
        )
        .unwrap();
    assert_eq!(h2, 1_800_000);
    let h3: i64 = conn
        .query_row(
            "SELECT idle_duration_ms FROM hourly_app_usage WHERE utc_hour_start_ms = ?1 AND app_id = ?2",
            [hour_03, fixture.app_b],
            |r| r.get(0),
        )
        .unwrap();
    assert_eq!(h3, 600_000);

    // daily_app_usage：A active 3600s；B active 357s + idle 600s。
    let daily_a: i64 = conn
        .query_row(
            "SELECT active_duration_ms FROM daily_app_usage WHERE local_date = '2026-07-18' AND app_id = ?1",
            [fixture.app_a],
            |r| r.get(0),
        )
        .unwrap();
    assert_eq!(daily_a, 3_600_000);
    let daily_b: (i64, i64) = conn
        .query_row(
            "SELECT active_duration_ms, idle_duration_ms FROM daily_app_usage WHERE local_date = '2026-07-18' AND app_id = ?1",
            [fixture.app_b],
            |r| Ok((r.get(0)?, r.get(1)?)),
        )
        .unwrap();
    assert_eq!(daily_b, (357_000, 600_000));

    // daily_work_metrics：一个 Work Block 3600s active，1 个非 transition gap，1 次合法 switch。
    let metrics: (i64, i64, i64, i64, i64) = conn
        .query_row(
            "SELECT active_duration_ms, work_block_count, longest_work_block_active_ms,
                    raw_app_switch_count, data_gap_count
             FROM daily_work_metrics WHERE local_date = '2026-07-18'",
            [],
            |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?, r.get(3)?, r.get(4)?)),
        )
        .unwrap();
    assert_eq!(metrics, (3_600_000, 1, 3_600_000, 1, 1));

    // 幂等：再重算一次，所有读模型行不变。
    let dump = |conn: &Connection| -> Vec<String> {
        let mut stmt = conn
            .prepare(
                "SELECT 'h' || utc_hour_start_ms || ':' || app_id || '=' || active_duration_ms || ',' || idle_duration_ms
                 FROM hourly_app_usage
                 UNION ALL
                 SELECT 'd' || local_date || ':' || app_id || '=' || active_duration_ms || ',' || idle_duration_ms
                 FROM daily_app_usage
                 UNION ALL
                 SELECT 'w' || local_date || '=' || active_duration_ms || ',' || work_block_count || ',' || raw_app_switch_count || ',' || data_gap_count
                 FROM daily_work_metrics
                 ORDER BY 1",
            )
            .unwrap();
        stmt.query_map([], |r| r.get(0))
            .unwrap()
            .collect::<rusqlite::Result<Vec<String>>>()
            .unwrap()
    };
    let before = dump(&conn);
    let tx = writer.transaction().unwrap();
    tx.recompute_hours(&tz, &[hour_01, hour_02, hour_03])
        .unwrap();
    tx.recompute_dates(&tz, std::slice::from_ref(&date), GAP_CAP_MS)
        .unwrap();
    tx.commit().unwrap();
    let after = dump(&conn);
    assert_eq!(before, after);

    // 来源删除后旧桶行同事务清除：用独立的调试连接删掉 idle 段来源，再重算。
    // B 的 idle 行消失；B 的 active 段仍在，故日行保留且 idle 归零、active 不变。
    {
        let conn = Connection::open(db_path(&dir)).unwrap();
        conn.execute(
            "DELETE FROM activity_segments WHERE app_id = ?1 AND activity_state = 'idle'",
            [fixture.app_b],
        )
        .unwrap();
    }
    {
        let tx = writer.transaction().unwrap();
        tx.recompute_hours(&tz, &[hour_03]).unwrap();
        tx.recompute_dates(&tz, std::slice::from_ref(&date), GAP_CAP_MS)
            .unwrap();
        tx.commit().unwrap();
    }
    let stale_hourly: i64 = conn
        .query_row(
            "SELECT COUNT(*) FROM hourly_app_usage WHERE utc_hour_start_ms = ?1 AND app_id = ?2",
            [hour_03, fixture.app_b],
            |r| r.get(0),
        )
        .unwrap();
    assert_eq!(stale_hourly, 0, "idle 来源已删，03 桶 B 行必须被清除");
    let remaining_daily: (i64, i64) = conn
        .query_row(
            "SELECT active_duration_ms, idle_duration_ms FROM daily_app_usage WHERE local_date = '2026-07-18' AND app_id = ?1",
            [fixture.app_b],
            |r| Ok((r.get(0)?, r.get(1)?)),
        )
        .unwrap();
    assert_eq!(
        remaining_daily,
        (357_000, 0),
        "B 日行保留 active，idle 重算为 0"
    );
}

#[test]
fn recompute_raw_switch_count_respects_definition() {
    let dir = TempDir::new().unwrap();
    let mut writer = bootstrap(&dir);
    let runtime = RuntimeId::new();

    {
        let tx = writer.transaction().unwrap();
        tx.insert_runtime(&runtime, T0).unwrap();
        let app_a = seed_app(&tx, "code", T0);
        let app_b = seed_app(&tx, "notepad", T0);
        insert_obs(&tx, &runtime, 1, T0 + 3_000, app_a, ActivityState::Active);
        // A→B，delta 3s：合法 switch（1）
        insert_obs(&tx, &runtime, 2, T0 + 6_000, app_b, ActivityState::Active);
        // B→B：同 app 不计
        insert_obs(&tx, &runtime, 3, T0 + 9_000, app_b, ActivityState::Active);
        // B(unknown)→A：prev unknown 不计
        insert_obs(&tx, &runtime, 4, T0 + 12_000, app_b, ActivityState::Unknown);
        insert_obs(&tx, &runtime, 5, T0 + 15_000, app_a, ActivityState::Active);
        // A→B，delta 3s：合法 switch（2）
        insert_obs(&tx, &runtime, 6, T0 + 18_000, app_b, ActivityState::Active);
        // B→A，delta 42s > cap：不计
        insert_obs(&tx, &runtime, 7, T0 + 60_000, app_a, ActivityState::Active);
        // A→B，delta 3s 但中间有 capture_paused gap：不计
        insert_obs(&tx, &runtime, 8, T0 + 80_000, app_a, ActivityState::Active);
        tx.open_gap(&runtime, GapKind::CapturePaused, T0 + 81_000)
            .unwrap();
        tx.close_open_gap(T0 + 82_000).unwrap();
        insert_obs(&tx, &runtime, 9, T0 + 83_000, app_b, ActivityState::Active);
        tx.commit().unwrap();
    }

    let tz = writer.schema_meta().reporting_tz().unwrap();
    let date = LocalDate::parse("2026-07-18").unwrap();
    let tx = writer.transaction().unwrap();
    tx.recompute_dates(&tz, std::slice::from_ref(&date), GAP_CAP_MS)
        .unwrap();
    tx.commit().unwrap();

    let conn = Connection::open(db_path(&dir)).unwrap();
    let switches: i64 = conn
        .query_row(
            "SELECT raw_app_switch_count FROM daily_work_metrics WHERE local_date = '2026-07-18'",
            [],
            |r| r.get(0),
        )
        .unwrap();
    assert_eq!(switches, 2);
}

#[test]
fn dst_fall_back_keeps_two_same_named_local_hours() {
    let dir = TempDir::new().unwrap();
    let mut writer =
        Writer::bootstrap_with_timezone(&db_path(&dir), "America/New_York", T0).unwrap();
    let runtime = RuntimeId::new();

    // 2026-11-01 fallback：UTC 05:00 = 1:00 EDT(-240)，UTC 06:00 = 1:00 EST(-300)。
    let h05 = 1_793_509_200_000_i64;
    let h06 = 1_793_512_800_000_i64;
    {
        let tz = writer.schema_meta().reporting_tz().unwrap();
        let tx = writer.transaction().unwrap();
        tx.insert_runtime(&runtime, T0).unwrap();
        let app = seed_app(&tx, "code", T0);
        let o1 = insert_obs(&tx, &runtime, 1, h05 + 600_000, app, ActivityState::Active);
        let o2 = insert_obs(
            &tx,
            &runtime,
            2,
            h05 + 1_200_000,
            app,
            ActivityState::Active,
        );
        let seg1 = tx
            .open_segment(&runtime, 0, app, ActivityState::Active, h05 + 600_000, o1)
            .unwrap();
        tx.update_open_segment(seg1, h05 + 1_200_000, o2).unwrap();
        tx.close_open_segment("app_changed").unwrap();
        let o3 = insert_obs(&tx, &runtime, 3, h06 + 600_000, app, ActivityState::Active);
        let o4 = insert_obs(
            &tx,
            &runtime,
            4,
            h06 + 1_200_000,
            app,
            ActivityState::Active,
        );
        let seg2 = tx
            .open_segment(&runtime, 0, app, ActivityState::Active, h06 + 600_000, o3)
            .unwrap();
        tx.update_open_segment(seg2, h06 + 1_200_000, o4).unwrap();
        tx.close_open_segment("app_changed").unwrap();
        tx.recompute_hours(&tz, &[h05, h06]).unwrap();
        tx.commit().unwrap();
    }

    let conn = Connection::open(db_path(&dir)).unwrap();
    let mut stmt = conn
        .prepare(
            "SELECT utc_hour_start_ms, local_date, local_hour, local_utc_offset_minutes, active_duration_ms
             FROM hourly_app_usage ORDER BY utc_hour_start_ms",
        )
        .unwrap();
    let rows: Vec<(i64, String, i64, i64, i64)> = stmt
        .query_map([], |r| {
            Ok((r.get(0)?, r.get(1)?, r.get(2)?, r.get(3)?, r.get(4)?))
        })
        .unwrap()
        .collect::<rusqlite::Result<Vec<_>>>()
        .unwrap();
    assert_eq!(rows.len(), 2);
    assert_eq!(rows[0].1, "2026-11-01");
    assert_eq!(rows[0].2, 1);
    assert_eq!(rows[0].3, -240);
    assert_eq!(rows[0].4, 600_000);
    assert_eq!(rows[1].1, "2026-11-01");
    assert_eq!(rows[1].2, 1);
    assert_eq!(rows[1].3, -300);
    assert_eq!(rows[1].4, 600_000);
}

#[test]
fn timeline_paginates_segments_and_gaps_with_mixed_cursor() {
    let dir = TempDir::new().unwrap();
    let mut writer = bootstrap(&dir);
    let runtime = RuntimeId::new();

    {
        let tx = writer.transaction().unwrap();
        tx.insert_runtime(&runtime, T0).unwrap();
        let app = seed_app(&tx, "code", T0);
        for i in 0..5_i64 {
            let start = T0 + i * 600_000;
            let end = start + 300_000;
            let o1 = insert_obs(&tx, &runtime, i * 2 + 1, start, app, ActivityState::Active);
            let o2 = insert_obs(&tx, &runtime, i * 2 + 2, end, app, ActivityState::Active);
            let seg = tx
                .open_segment(&runtime, 0, app, ActivityState::Active, start, o1)
                .unwrap();
            tx.update_open_segment(seg, end, o2).unwrap();
            tx.close_open_segment("app_changed").unwrap();
            tx.open_gap(&runtime, GapKind::SamplingTransition, end)
                .unwrap();
            tx.close_open_gap(end + 3_000).unwrap();
        }
        tx.commit().unwrap();
    }

    let reader = Reader::open(&db_path(&dir)).unwrap();
    let date = LocalDate::parse("2026-07-18").unwrap();

    let mut seen: Vec<(String, i64)> = Vec::new();
    let mut cursor: Option<TimelineCursor> = None;
    loop {
        let page = reader.timeline(&date, cursor, 3).expect("timeline 查询");
        for item in page.items {
            match item {
                wuji_core::dto::TimelineItem::Segment(s) => {
                    seen.push(("segment".to_string(), s.start_at_utc_ms.0))
                }
                wuji_core::dto::TimelineItem::Gap(g) => {
                    seen.push(("gap".to_string(), g.start_at_utc_ms.0))
                }
            }
        }
        match page.next_cursor {
            Some(raw) => {
                cursor = Some(TimelineCursor::decode(&raw).expect("cursor 必须可解码"));
            }
            None => break,
        }
    }

    // 5 个 segment + 5 个 gap，按时间严格交错且无重复。
    assert_eq!(seen.len(), 10);
    let mut sorted = seen.clone();
    sorted.sort_by_key(|(_, start)| *start);
    assert_eq!(seen, sorted, "分页结果必须按 start 升序且无遗漏");
    assert_eq!(seen[0].0, "segment");
    assert_eq!(seen[1].0, "gap");
}

#[test]
fn today_assembles_from_daily_read_models_and_segments() {
    let dir = TempDir::new().unwrap();
    let mut writer = bootstrap(&dir);
    let fixture = seed_daily_fixture(&mut writer);
    let tz = writer.schema_meta().reporting_tz().unwrap();
    let date = LocalDate::parse("2026-07-18").unwrap();
    {
        let tx = writer.transaction().unwrap();
        tx.recompute_hours(&tz, &[T0 + 3_600_000, T0 + 7_200_000, T0 + 10_800_000])
            .unwrap();
        tx.recompute_dates(&tz, std::slice::from_ref(&date), GAP_CAP_MS)
            .unwrap();
        tx.commit().unwrap();
    }

    let reader = Reader::open(&db_path(&dir)).unwrap();
    let today = reader.today(&date).expect("today 查询");

    assert_eq!(today.active_duration_ms.0, 3_600_000 + 357_000);
    assert_eq!(today.work_block_count.0, 1);
    assert_eq!(today.longest_work_block_active_ms.0, 3_600_000);
    assert_eq!(today.top_apps.len(), 2);
    assert_eq!(today.top_apps[0].app.app_id.0, fixture.app_a);
    assert!(!today.quality.is_complete, "当日有 paused gap，不应完整");
    assert_eq!(today.quality.gap_count.0, 1);
    assert!(
        today.current_app.is_none(),
        "无 open Segment 时 currentApp 为空"
    );
    assert_eq!(
        today.last_app.as_ref().map(|a| a.app_id.0),
        Some(fixture.app_b)
    );
}

#[test]
fn reader_queries_work_while_writer_holds_connection() {
    let dir = TempDir::new().unwrap();
    let mut writer = bootstrap(&dir);
    let fixture_runtime = RuntimeId::new();
    {
        let tx = writer.transaction().unwrap();
        tx.insert_runtime(&fixture_runtime, T0).unwrap();
        let app = seed_app(&tx, "code", T0);
        insert_obs(&tx, &fixture_runtime, 1, T0, app, ActivityState::Active);
        tx.commit().unwrap();
    }

    let reader = Reader::open(&db_path(&dir)).expect("writer 持写连接时 reader 仍应可打开");
    let date = LocalDate::parse("2026-07-18").unwrap();
    let before = reader.today(&date).unwrap();
    assert_eq!(before.active_duration_ms.0, 0);

    // writer 再提交一批；reader 的新查询立即可见（WAL 下各自读快照）。
    {
        let tx = writer.transaction().unwrap();
        let app = seed_app(&tx, "code2", T0 + 1);
        insert_obs(
            &tx,
            &fixture_runtime,
            2,
            T0 + 3_000,
            app,
            ActivityState::Active,
        );
        tx.commit().unwrap();
    }
    let runtime = reader.latest_runtime().unwrap().expect("应有 runtime 行");
    assert_eq!(runtime.started_at_utc_ms, T0);
}

#[test]
fn open_rows_are_recoverable_after_reopen() {
    let dir = TempDir::new().unwrap();
    let runtime = RuntimeId::new();
    {
        let mut writer = bootstrap(&dir);
        let tx = writer.transaction().unwrap();
        tx.insert_runtime(&runtime, T0).unwrap();
        let app = seed_app(&tx, "code", T0);
        let obs = insert_obs(&tx, &runtime, 1, T0, app, ActivityState::Active);
        let seg = tx
            .open_segment(&runtime, 0, app, ActivityState::Active, T0, obs)
            .unwrap();
        tx.open_work_block(&runtime, T0, seg).unwrap();
        tx.open_gap(&runtime, GapKind::CapturePaused, T0).unwrap();
        tx.commit().unwrap();
    }

    // 模拟崩溃后重新打开：遗留 open 行必须可读、可关闭（09 §6.7）。
    let mut writer = Writer::open_existing(&db_path(&dir)).unwrap();
    assert!(writer.find_open_segment().unwrap().is_some());
    assert!(writer.find_open_work_block().unwrap().is_some());
    assert!(writer.find_open_gap().unwrap().is_some());

    let tx = writer.transaction().unwrap();
    tx.close_open_gap(T0).unwrap();
    tx.close_open_segment("agent_restart").unwrap();
    tx.close_open_work_block("agent_restart").unwrap();
    tx.commit().unwrap();

    assert!(writer.find_open_segment().unwrap().is_none());
    assert!(writer.find_open_work_block().unwrap().is_none());
    assert!(writer.find_open_gap().unwrap().is_none());
}

// ---------- R02 回归：Today 聚合截断与 drop event_count ----------

/// 21 个应用各 30s 活跃：Today.activeDurationMs 必须包含 Top 20 之外的应用（R02）。
#[test]
fn today_active_total_is_not_truncated_by_top20() {
    let dir = TempDir::new().unwrap();
    let mut writer = bootstrap(&dir);
    let runtime = RuntimeId::new();
    {
        let tz = writer_tz(writer.schema_meta());
        let tx = writer.transaction().unwrap();
        tx.insert_runtime(&runtime, T0).unwrap();
        let mut seq = 0_i64;
        for i in 0..21_i64 {
            let name = format!("app{i:02}");
            let app = seed_app(&tx, &name, T0);
            let start = T0 + i * 60_000;
            let end = start + 30_000;
            seq += 1;
            let o1 = insert_obs(&tx, &runtime, seq, start, app, ActivityState::Active);
            seq += 1;
            let o2 = insert_obs(&tx, &runtime, seq, end, app, ActivityState::Active);
            let seg = tx
                .open_segment(&runtime, 0, app, ActivityState::Active, start, o1)
                .unwrap();
            tx.update_open_segment(seg, end, o2).unwrap();
            tx.close_open_segment("app_changed").unwrap();
        }
        tx.recompute_hours(&tz, &[T0, T0 + 3_600_000]).unwrap();
        let date = LocalDate::parse("2026-07-18").unwrap();
        tx.recompute_dates(&tz, std::slice::from_ref(&date), GAP_CAP_MS)
            .unwrap();
        tx.commit().unwrap();
    }

    let reader = Reader::open(&db_path(&dir)).unwrap();
    let today = reader
        .today(&LocalDate::parse("2026-07-18").unwrap())
        .unwrap();
    assert_eq!(today.top_apps.len(), 20, "Top Apps 保持 LIMIT 20");
    assert_eq!(
        today.active_duration_ms.0,
        21 * 30_000,
        "Today 活跃时长必须包含 Top 20 之外的应用（R02）"
    );
    // 守恒：Today.active == 当日 Segment active 交集总和。
    let conn = Connection::open(db_path(&dir)).unwrap();
    let segment_sum: i64 = conn
        .query_row(
            "SELECT COALESCE(SUM(duration_ms), 0) FROM activity_segments WHERE activity_state = 'active'",
            [],
            |r| r.get(0),
        )
        .unwrap();
    assert_eq!(today.active_duration_ms.0, segment_sum);
}

/// 合并 gap 的 event_count 必须全部计入 droppedCount（R02）。
#[test]
fn today_dropped_count_sums_merged_gap_event_count() {
    let dir = TempDir::new().unwrap();
    let mut writer = bootstrap(&dir);
    let runtime = RuntimeId::new();
    {
        let tx = writer.transaction().unwrap();
        tx.insert_runtime(&runtime, T0).unwrap();
        tx.open_gap(&runtime, GapKind::CaptureQueueDrop, T0 + 60_000)
            .unwrap();
        tx.close_open_gap(T0 + 61_000).unwrap();
        // 第二个 gap 连续三次同类事件合并（event_count = 3）。
        tx.open_gap(&runtime, GapKind::WriterQueueDrop, T0 + 120_000)
            .unwrap();
        tx.extend_open_gap().unwrap();
        tx.extend_open_gap().unwrap();
        tx.close_open_gap(T0 + 121_000).unwrap();
        tx.commit().unwrap();
    }

    let reader = Reader::open(&db_path(&dir)).unwrap();
    let today = reader
        .today(&LocalDate::parse("2026-07-18").unwrap())
        .unwrap();
    assert_eq!(
        today.quality.dropped_count.0, 4,
        "droppedCount 必须是 event_count 总和（1 + 3），不是行数（R02）"
    );
    assert!(!today.quality.is_complete);
}

/// 跨本地午夜的 Segment 按 local date 正确拆分（R02 跨午夜聚合）。
#[test]
fn today_cross_midnight_splits_by_local_date() {
    let dir = TempDir::new().unwrap();
    let mut writer = bootstrap(&dir);
    let runtime = RuntimeId::new();
    // Asia/Shanghai：本地 23:50 → 次日 00:10（UTC 15:50 → 16:10）。
    let start = T0 + 57_000_000; // 本地 2026-07-18 23:50 = UTC 15:50
    let end = start + 1_200_000;
    {
        let tz = writer_tz(writer.schema_meta());
        let tx = writer.transaction().unwrap();
        tx.insert_runtime(&runtime, T0).unwrap();
        let app = seed_app(&tx, "code", T0);
        let o1 = insert_obs(&tx, &runtime, 1, start, app, ActivityState::Active);
        let o2 = insert_obs(&tx, &runtime, 2, end, app, ActivityState::Active);
        let seg = tx
            .open_segment(&runtime, 0, app, ActivityState::Active, start, o1)
            .unwrap();
        tx.update_open_segment(seg, end, o2).unwrap();
        tx.close_open_segment("capture_stopped").unwrap();
        let hour1 = start - start.rem_euclid(3_600_000);
        let hour2 = end - end.rem_euclid(3_600_000);
        tx.recompute_hours(&tz, &[hour1, hour2]).unwrap();
        let d1 = LocalDate::parse("2026-07-18").unwrap();
        let d2 = LocalDate::parse("2026-07-19").unwrap();
        tx.recompute_dates(&tz, &[d1, d2], GAP_CAP_MS).unwrap();
        tx.commit().unwrap();
    }

    let reader = Reader::open(&db_path(&dir)).unwrap();
    let d1 = reader
        .today(&LocalDate::parse("2026-07-18").unwrap())
        .unwrap();
    let d2 = reader
        .today(&LocalDate::parse("2026-07-19").unwrap())
        .unwrap();
    assert_eq!(d1.active_duration_ms.0, 600_000, "前一日得 10 分钟");
    assert_eq!(d2.active_duration_ms.0, 600_000, "后一日得 10 分钟");
}

fn writer_tz(meta: &wuji_storage::SchemaMeta) -> chrono_tz::Tz {
    meta.reporting_tz().unwrap()
}

#[test]
fn settings_revision_persists_last_known_good_content() {
    let dir = TempDir::new().unwrap();
    let mut writer = bootstrap(&dir);

    // bootstrap 写入 revision 0 默认 digest（S2-01：不再包含 content_json）。
    {
        let (revision, digest) = writer
            .latest_settings_revision_digest()
            .unwrap()
            .expect("bootstrap 必有 revision 0");
        assert_eq!(revision, 0);
        assert_eq!(
            digest,
            wuji_core::settings::Settings::default().content_digest()
        );
    }

    // 应用 revision 1 后，latest 返回其 revision/digest；幂等重放不重复插入。
    let settings = wuji_core::settings::Settings {
        revision: "1".to_string(),
        idle_threshold_seconds: 90,
        ..wuji_core::settings::Settings::default()
    };
    {
        let tx = writer.transaction().unwrap();
        let outcome = tx
            .ensure_settings_revision(1, &settings.content_digest(), T0)
            .unwrap();
        assert_eq!(
            outcome,
            wuji_storage::writer::SettingsRevisionOutcome::Inserted
        );
        tx.commit().unwrap();
    }
    let (revision, digest1) = writer.latest_settings_revision_digest().unwrap().unwrap();
    assert_eq!(revision, 1);
    assert_eq!(digest1, settings.content_digest());

    // 同 revision 不同 digest → ConflictDigest，不覆盖已持久化内容。
    let mut conflicting = settings.clone();
    conflicting.sampling_interval_seconds = 5;
    {
        let tx = writer.transaction().unwrap();
        let outcome = tx
            .ensure_settings_revision(1, &conflicting.content_digest(), T0)
            .unwrap();
        assert_eq!(
            outcome,
            wuji_storage::writer::SettingsRevisionOutcome::ConflictDigest
        );
        tx.commit().unwrap();
    }
    let (revision, digest) = writer.latest_settings_revision_digest().unwrap().unwrap();
    assert_eq!(revision, 1, "冲突不得改变 revision");
    assert_eq!(digest, settings.content_digest(), "冲突不得覆盖原 digest");
}

#[test]
fn old_v1_fixture_returns_schema_unsupported() {
    use wuji_core::error::SafeErrorCode;
    use wuji_storage::error::StorageError;

    let dir = TempDir::new().unwrap();
    let db_path = dir.path().join("old-v1.db");

    // 手工创建 schema v1 数据库（无 content_json，schema_version = 1）。
    let conn = rusqlite::Connection::open(&db_path).unwrap();
    conn.execute_batch(
        "PRAGMA foreign_keys = ON;
         PRAGMA journal_mode = WAL;
         CREATE TABLE schema_meta (
             singleton_id INTEGER PRIMARY KEY CHECK (singleton_id = 1),
             schema_version INTEGER NOT NULL CHECK (schema_version = 1),
             algorithm_version TEXT NOT NULL CHECK (length(algorithm_version) > 0),
             created_at_utc_ms INTEGER NOT NULL CHECK (created_at_utc_ms >= 0),
             reporting_time_zone_id TEXT NOT NULL CHECK (length(reporting_time_zone_id) > 0)
         ) STRICT;
         CREATE TABLE settings_revisions (
             revision INTEGER PRIMARY KEY CHECK (revision >= 0),
             content_digest TEXT NOT NULL CHECK (length(content_digest) = 64),
             applied_at_utc_ms INTEGER NOT NULL CHECK (applied_at_utc_ms >= 0)
         ) STRICT;
         INSERT INTO schema_meta VALUES (1, 1, 'rebuild-v0.1', 0, 'Asia/Shanghai');
         INSERT INTO settings_revisions VALUES (0, '0000000000000000000000000000000000000000000000000000000000000000', 0);",
    )
    .unwrap();
    conn.execute_batch("PRAGMA wal_checkpoint(TRUNCATE)")
        .unwrap();
    drop(conn);

    let result = wuji_storage::Writer::open_existing(&db_path);
    match result {
        Err(StorageError {
            code: SafeErrorCode::DbSchemaUnsupported,
            ..
        }) => {
            // 预期：旧 schema v1 被拒绝。
        }
        other => panic!("旧 v1 fixture 应返回 DB_SCHEMA_UNSUPPORTED，实际: {other:?}"),
    }
}

#[test]
fn heatmap_aggregates_hourly_projection_and_normalizes_intensity() {
    let dir = TempDir::new().unwrap();
    let mut writer = bootstrap(&dir);
    let runtime = RuntimeId::new();
    let tz = writer.schema_meta().reporting_tz().unwrap();
    let day = 86_400_000_i64;

    {
        let tx = writer.transaction().unwrap();
        tx.insert_runtime(&runtime, T0).unwrap();
        let code = seed_app(&tx, "code", T0);
        let edge = seed_app(&tx, "edge", T0);
        let mut seq = 0_i64;
        // (app, state, 相对 T0 的起点, 时长)。UTC 小时桶起点 = 上海 local 08/09/10/11 时。
        let segments: [(usize, ActivityState, i64, i64); 6] = [
            // 07-18 08 时桶：code active 10 分钟。
            (0, ActivityState::Active, 600_000, 600_000),
            // 07-18 08 时桶：edge active 15 分钟（与 code 聚合 → 1_500_000）。
            (1, ActivityState::Active, 900_000, 900_000),
            // 07-19 08 时桶：code idle 30 分钟（active=0 → 强度 0）。
            (0, ActivityState::Idle, day + 600_000, 1_800_000),
            // 07-19 09 时桶：edge active 满 1 小时 → max=3_600_000，强度 4。
            (1, ActivityState::Active, day + 3_600_000, 3_600_000),
            // 07-19 10 时桶：edge active 15 分钟 = max/4 → 强度 1。
            (1, ActivityState::Active, day + 7_200_000, 900_000),
            // 07-19 11 时桶：edge active 45 分钟 = 3/4 max → 强度 3。
            (1, ActivityState::Active, day + 10_800_000, 2_700_000),
        ];
        let apps = [code, edge];
        for (app_index, state, offset, len) in segments {
            let start = T0 + offset;
            seq += 1;
            let o1 = insert_obs(&tx, &runtime, seq, start, apps[app_index], state);
            let seg = tx
                .open_segment(&runtime, 0, apps[app_index], state, start, o1)
                .unwrap();
            seq += 1;
            let o2 = insert_obs(&tx, &runtime, seq, start + len, apps[app_index], state);
            tx.update_open_segment(seg, start + len, o2).unwrap();
            tx.close_open_segment("app_changed").unwrap();
        }
        // 2026-07-10（7 天窗口外）的 active 段：必须被范围裁剪。
        // 该段早于 T0，insert_obs 的 monotonic 基准（captured - T0）会为负，
        // 直接写 monotonic=0 的夹具值。
        let old = T0 - 8 * day;
        let o1 = match tx.insert_observation(
            &runtime,
            seq + 1,
            0,
            old + 600_000,
            0,
            code,
            ActivityState::Active,
            CaptureQuality::Normal,
            0,
        ) {
            Ok(ObservationInsert::Inserted(id)) => id,
            other => panic!("observation 插入失败: {other:?}"),
        };
        let seg = tx
            .open_segment(&runtime, 0, code, ActivityState::Active, old + 600_000, o1)
            .unwrap();
        let o2 = match tx.insert_observation(
            &runtime,
            seq + 2,
            0,
            old + 1_200_000,
            0,
            code,
            ActivityState::Active,
            CaptureQuality::Normal,
            0,
        ) {
            Ok(ObservationInsert::Inserted(id)) => id,
            other => panic!("observation 插入失败: {other:?}"),
        };
        tx.update_open_segment(seg, old + 1_200_000, o2).unwrap();
        tx.close_open_segment("app_changed").unwrap();

        tx.recompute_hours(
            &tz,
            &[
                T0,
                T0 + day,
                T0 + day + 3_600_000,
                T0 + day + 7_200_000,
                T0 + day + 10_800_000,
                old,
            ],
        )
        .unwrap();
        tx.commit().unwrap();
    }

    let reader = Reader::open(&db_path(&dir)).unwrap();
    let today = LocalDate::parse("2026-07-19").unwrap();
    let heatmap = reader.heatmap(&today, 7, 0).expect("heatmap 查询");
    assert_eq!(heatmap.days, 7);
    assert_eq!(heatmap.today.as_str(), "2026-07-19");
    assert_eq!(heatmap.cells.len(), 5, "窗口外 2026-07-10 必须被裁剪");

    let cell = |date: &str, hour: u32| {
        heatmap
            .cells
            .iter()
            .find(|c| c.local_date == date && c.local_hour == hour)
            .unwrap_or_else(|| panic!("格子 {date} {hour} 必须存在"))
            .clone()
    };

    let aggregated = cell("2026-07-18", 8);
    assert_eq!(
        aggregated.active_duration_ms.0, 1_500_000,
        "多 app 同桶聚合"
    );
    assert_eq!(aggregated.intensity_level, 2, "1.5M/3.6M ≈ 0.42 → 等级 2");

    let idle_only = cell("2026-07-19", 8);
    assert_eq!(idle_only.idle_duration_ms.0, 1_800_000);
    assert_eq!(idle_only.intensity_level, 0, "active 为 0 → 等级 0");

    let busiest = cell("2026-07-19", 9);
    assert_eq!(busiest.active_duration_ms.0, 3_600_000);
    assert_eq!(busiest.intensity_level, 4, "最忙一小时 → 等级 4");

    let quarter = cell("2026-07-19", 10);
    assert_eq!(quarter.intensity_level, 1, "恰为 max/4 → 等级 1");

    let three_quarter = cell("2026-07-19", 11);
    assert_eq!(three_quarter.intensity_level, 3, "恰为 3/4 max → 等级 3");
}

#[test]
fn heatmap_empty_range_returns_no_cells() {
    let dir = TempDir::new().unwrap();
    let _writer = bootstrap(&dir);
    let reader = Reader::open(&db_path(&dir)).unwrap();
    let today = LocalDate::parse("2026-07-19").unwrap();
    let heatmap = reader.heatmap(&today, 7, 0).expect("heatmap 查询");
    assert!(heatmap.cells.is_empty());
}

#[test]
fn heatmap_rejects_out_of_range_days() {
    let dir = TempDir::new().unwrap();
    let _writer = bootstrap(&dir);
    let reader = Reader::open(&db_path(&dir)).unwrap();
    let today = LocalDate::parse("2026-07-19").unwrap();
    // days 不变量在 Reader 自身维护，不依赖 QueryService 拦截。
    for days in [0_u32, 32_u32] {
        let err = reader
            .heatmap(&today, days, 0)
            .expect_err("days 越界必须拒绝");
        assert_eq!(err.code, SafeErrorCode::InvalidArgument);
    }
}

#[test]
fn heatmap_week_offset_shifts_anchor_week() {
    let dir = TempDir::new().unwrap();
    let mut writer = bootstrap(&dir);
    let runtime = RuntimeId::new();
    let tz = writer.schema_meta().reporting_tz().unwrap();
    let day = 86_400_000_i64;

    {
        let tx = writer.transaction().unwrap();
        tx.insert_runtime(&runtime, T0).unwrap();
        let code = seed_app(&tx, "code", T0);
        // 2026-07-19（今天）与 2026-07-10（上上周内）各一段 active。
        let o1 = insert_obs(
            &tx,
            &runtime,
            1,
            T0 + day + 600_000,
            code,
            ActivityState::Active,
        );
        let seg = tx
            .open_segment(
                &runtime,
                0,
                code,
                ActivityState::Active,
                T0 + day + 600_000,
                o1,
            )
            .unwrap();
        let o2 = insert_obs(
            &tx,
            &runtime,
            2,
            T0 + day + 1_200_000,
            code,
            ActivityState::Active,
        );
        tx.update_open_segment(seg, T0 + day + 1_200_000, o2)
            .unwrap();
        tx.close_open_segment("app_changed").unwrap();

        let old = T0 - 8 * day;
        let o3 = match tx.insert_observation(
            &runtime,
            3,
            0,
            old + 600_000,
            0,
            code,
            ActivityState::Active,
            CaptureQuality::Normal,
            0,
        ) {
            Ok(ObservationInsert::Inserted(id)) => id,
            other => panic!("observation 插入失败: {other:?}"),
        };
        let seg = tx
            .open_segment(&runtime, 0, code, ActivityState::Active, old + 600_000, o3)
            .unwrap();
        let o4 = match tx.insert_observation(
            &runtime,
            4,
            0,
            old + 1_200_000,
            0,
            code,
            ActivityState::Active,
            CaptureQuality::Normal,
            0,
        ) {
            Ok(ObservationInsert::Inserted(id)) => id,
            other => panic!("observation 插入失败: {other:?}"),
        };
        tx.update_open_segment(seg, old + 1_200_000, o4).unwrap();
        tx.close_open_segment("app_changed").unwrap();

        tx.recompute_hours(&tz, &[T0 + day, old]).unwrap();
        tx.commit().unwrap();
    }

    let reader = Reader::open(&db_path(&dir)).unwrap();
    let today = LocalDate::parse("2026-07-19").unwrap();

    // 本周（offset 0）：只有 07-19 的格子。
    let current = reader.heatmap(&today, 7, 0).expect("heatmap 查询");
    assert_eq!(current.today.as_str(), "2026-07-19");
    assert_eq!(current.range_end_local_date.as_str(), "2026-07-19");
    assert_eq!(current.cells.len(), 1);
    assert_eq!(current.cells[0].local_date, "2026-07-19");

    // 上一周（offset -1）：锚点 2026-07-12，只有 07-10 的格子。
    let previous = reader.heatmap(&today, 7, -1).expect("heatmap 查询");
    assert_eq!(previous.today.as_str(), "2026-07-19");
    assert_eq!(previous.range_end_local_date.as_str(), "2026-07-12");
    assert_eq!(previous.cells.len(), 1);
    assert_eq!(previous.cells[0].local_date, "2026-07-10");
    assert_eq!(previous.cells[0].active_duration_ms.0, 600_000);
    assert_eq!(previous.cells[0].intensity_level, 4, "唯一格子即最忙一小时");

    // 上上周（offset -2）：锚点 2026-07-05，两段都不在范围内。
    let two_weeks = reader.heatmap(&today, 7, -2).expect("heatmap 查询");
    assert_eq!(two_weeks.today.as_str(), "2026-07-19");
    assert_eq!(two_weeks.range_end_local_date.as_str(), "2026-07-05");
    assert!(two_weeks.cells.is_empty());
}

#[test]
fn heatmap_rejects_out_of_range_week_offset() {
    let dir = TempDir::new().unwrap();
    let _writer = bootstrap(&dir);
    let reader = Reader::open(&db_path(&dir)).unwrap();
    let today = LocalDate::parse("2026-07-19").unwrap();
    // 只允许当前周与最多 520 个历史周；未来周和越界历史周必须拒绝。
    for offset in [-521_i32, 1_i32, 521_i32] {
        let err = reader
            .heatmap(&today, 7, offset)
            .expect_err("week_offset 越界必须拒绝");
        assert_eq!(err.code, SafeErrorCode::InvalidArgument);
    }
    assert!(reader.heatmap(&today, 7, 0).is_ok());
    assert!(reader.heatmap(&today, 7, -520).is_ok());
}
