//! 阶段 4.1 复审补修：统一 Settings 持久化协议的确定性集成测试。
//!
//! 覆盖：连续 DB commit 失败、连续降级、连续 digest 冲突（正式槽位不变）、
//! 幂等路径修复缺失备份冗余、前滚成功后 DB 与双槽同时前进。

use std::path::PathBuf;
use std::sync::Arc;

use rusqlite::Connection;
use tempfile::TempDir;
use wuji_core::dto::RuntimeId;
use wuji_core::error::SafeErrorCode;
use wuji_core::settings::Settings;
use wuji_rebuild_agent::activity::ActivityEngine;
use wuji_rebuild_agent::capture_loop::ContinuityState;
use wuji_rebuild_agent::settings_backup::{read_backup_matching, write_backup};
use wuji_rebuild_agent::settings_persist::{SettingsPersistOutcome, apply_settings_persistent};
use wuji_storage::Writer;

const T0: i64 = 1_784_332_800_000;
const SHANGHAI: &str = "Asia/Shanghai";

fn db_path(dir: &TempDir) -> PathBuf {
    dir.path().join("wuji-rebuild-v0.1.db")
}

fn config_dir(dir: &TempDir) -> PathBuf {
    dir.path().join("config")
}

fn saved(revision: u64) -> Settings {
    Settings {
        revision: revision.to_string(),
        ..Settings::default()
    }
}

/// 在 DB 中提交 revision 5 并构造对应引擎（模拟已稳定运行的状态）。
fn committed_engine_and_writer(dir: &TempDir, settings: &Settings) -> (ActivityEngine, Writer) {
    Writer::bootstrap_with_timezone(&db_path(dir), SHANGHAI, T0).unwrap();
    let continuity = Arc::new(ContinuityState::default());
    let runtime_id = RuntimeId::new();
    {
        let mut writer = Writer::open_existing(&db_path(dir)).unwrap();
        let tx = writer.transaction().unwrap();
        tx.insert_runtime(&runtime_id, T0).unwrap();
        tx.ensure_settings_revision(
            settings.revision.parse().unwrap(),
            &settings.content_digest(),
            T0,
        )
        .unwrap();
        tx.commit().unwrap();
    }
    let writer = Writer::open_existing(&db_path(dir)).unwrap();
    let engine = ActivityEngine::new(runtime_id, settings.clone(), continuity).unwrap();
    (engine, writer)
}

fn slot_contents(dir: &TempDir) -> Vec<String> {
    let mut contents = std::fs::read_dir(config_dir(dir))
        .map(|entries| {
            entries
                .filter_map(|e| e.ok())
                .filter(|e| e.file_name().to_string_lossy().ends_with(".json"))
                .map(|e| std::fs::read_to_string(e.path()).unwrap_or_default())
                .collect::<Vec<_>>()
        })
        .unwrap_or_default();
    contents.sort();
    contents
}

/// 复审 P1-02 核心：连续两次 DB commit 失败不得丢失唯一匹配 DB 的 LKG。
#[test]
fn consecutive_db_commit_failures_preserve_db_matching_lkg() {
    let dir = TempDir::new().unwrap();
    let committed = saved(5);
    let (mut engine, mut writer) = committed_engine_and_writer(&dir, &committed);
    let db_digest = committed.content_digest();

    // 布局：A=5（唯一匹配 DB 的 LKG），B=4（旧槽）。
    let config = config_dir(&dir);
    std::fs::create_dir_all(&config).unwrap();
    std::fs::write(
        config.join(wuji_rebuild_agent::settings_backup::SLOT_A),
        committed.canonical_json(),
    )
    .unwrap();
    std::fs::write(
        config.join(wuji_rebuild_agent::settings_backup::SLOT_B),
        saved(4).canonical_json(),
    )
    .unwrap();

    // 第二连接持写锁：DB commit 确定性失败（busy_timeout 750ms 后报 busy）。
    let blocker = Connection::open(db_path(&dir)).unwrap();
    blocker.execute_batch("BEGIN IMMEDIATE").unwrap();

    let newer = saved(6);
    for attempt in 1..=2 {
        let error = apply_settings_persistent(&mut engine, &mut writer, &config, &newer, T0)
            .expect_err(&format!("第 {attempt} 次提交必须失败（写锁持有中）"));
        assert_eq!(error.code, SafeErrorCode::AgentWriterDegraded);
        // 每次失败后：A 槽必须仍是 revision 5 内容。
        let a_raw =
            std::fs::read_to_string(config.join(wuji_rebuild_agent::settings_backup::SLOT_A))
                .unwrap();
        assert!(
            a_raw.contains("\"revision\":\"5\""),
            "第 {attempt} 次失败后唯一 LKG 槽不得被覆盖"
        );
    }
    // B 槽承载了候选（未提交），DB 仍停留在 5。
    let lkg = read_backup_matching(&config, Some(&(5, db_digest.clone())));
    assert_eq!(lkg.expect("LKG 必须仍可恢复").revision, "5");
    blocker.execute_batch("ROLLBACK").unwrap();

    // 释放写锁后同一请求成功：DB 与备份同时前进。
    let outcome = apply_settings_persistent(&mut engine, &mut writer, &config, &newer, T0)
        .expect("释放写锁后必须成功");
    assert_eq!(outcome, SettingsPersistOutcome::Applied(6));
    let matched = read_backup_matching(&config, Some(&(6, newer.content_digest())));
    assert!(matched.is_some(), "提交成功后候选必须可交叉验证");
}

/// 复审 P1-02：连续降级请求在触碰任何槽位之前被拒绝。
#[test]
fn consecutive_downgrades_do_not_touch_slots() {
    let dir = TempDir::new().unwrap();
    let committed = saved(5);
    let (mut engine, mut writer) = committed_engine_and_writer(&dir, &committed);
    let config = config_dir(&dir);
    std::fs::create_dir_all(&config).unwrap();
    std::fs::write(
        config.join(wuji_rebuild_agent::settings_backup::SLOT_A),
        committed.canonical_json(),
    )
    .unwrap();
    std::fs::write(
        config.join(wuji_rebuild_agent::settings_backup::SLOT_B),
        saved(4).canonical_json(),
    )
    .unwrap();
    let before = slot_contents(&dir);

    let downgrade = saved(3);
    for attempt in 1..=2 {
        let error = apply_settings_persistent(&mut engine, &mut writer, &config, &downgrade, T0)
            .expect_err(&format!("第 {attempt} 次降级必须被拒绝"));
        assert_eq!(error.code, SafeErrorCode::SettingsConflict);
    }
    assert_eq!(slot_contents(&dir), before, "降级不得修改任何正式槽位");
}

/// 复审 P1-02：连续 digest 冲突在触碰任何槽位之前被拒绝。
#[test]
fn consecutive_digest_conflicts_do_not_touch_slots() {
    let dir = TempDir::new().unwrap();
    let committed = saved(5);
    let (mut engine, mut writer) = committed_engine_and_writer(&dir, &committed);
    let config = config_dir(&dir);
    std::fs::create_dir_all(&config).unwrap();
    std::fs::write(
        config.join(wuji_rebuild_agent::settings_backup::SLOT_A),
        committed.canonical_json(),
    )
    .unwrap();
    let before = slot_contents(&dir);

    let mut conflicting = saved(5);
    conflicting.idle_threshold_seconds = 90;
    for attempt in 1..=2 {
        let error = apply_settings_persistent(&mut engine, &mut writer, &config, &conflicting, T0)
            .expect_err(&format!("第 {attempt} 次 digest 冲突必须被拒绝"));
        assert_eq!(error.code, SafeErrorCode::SettingsConflict);
    }
    assert_eq!(
        slot_contents(&dir),
        before,
        "digest 冲突不得修改任何正式槽位"
    );
}

/// 复审 P1-01：内容与 DB 匹配但备份缺失时，幂等路径必须修复备份冗余。
#[test]
fn idempotent_apply_repairs_missing_backup() {
    let dir = TempDir::new().unwrap();
    let committed = saved(5);
    let (mut engine, mut writer) = committed_engine_and_writer(&dir, &committed);
    let config = config_dir(&dir);
    // 无任何槽位文件。
    assert!(read_backup_matching(&config, Some(&(5, committed.content_digest()))).is_none());

    let outcome = apply_settings_persistent(&mut engine, &mut writer, &config, &committed, T0)
        .expect("幂等应用必须成功");
    assert_eq!(outcome, SettingsPersistOutcome::Idempotent(5));
    let repaired = read_backup_matching(&config, Some(&(5, committed.content_digest())))
        .expect("幂等路径必须补建备份冗余");
    assert_eq!(repaired.revision, "5");
}

/// 复审 P1-01：前滚成功后 DB 与双槽同时前进（统一协议应用于启动与运行时）。
#[test]
fn forward_apply_advances_db_and_backup_together() {
    let dir = TempDir::new().unwrap();
    let committed = saved(5);
    let (mut engine, mut writer) = committed_engine_and_writer(&dir, &committed);
    let config = config_dir(&dir);
    write_backup(&config, &committed, None).unwrap();

    let newer = saved(6);
    let outcome = apply_settings_persistent(&mut engine, &mut writer, &config, &newer, T0)
        .expect("前滚必须成功");
    assert_eq!(outcome, SettingsPersistOutcome::Applied(6));
    let (db_revision, _) = writer
        .latest_settings_revision_digest()
        .unwrap()
        .expect("DB 必有 revision");
    assert_eq!(db_revision, 6);
    assert!(read_backup_matching(&config, Some(&(6, newer.content_digest()))).is_some());
}
