//! Single SQLite Writer：bootstrap、行操作与单事务提交（09 §5.2、§7）。
//!
//! 行为状态机（V01-4）调用本模块的原子行操作；本模块只做存储层不变量，
//! 不做归属/切段决策。

use std::path::{Path, PathBuf};

use rusqlite::{Connection, OptionalExtension, Transaction, TransactionBehavior, params};
use wuji_core::domain::{
    ActivityState, CaptureQuality, CaptureState, GapKind, ProcessState, WriterState,
};
use wuji_core::dto::RuntimeId;
use wuji_core::settings::Settings;

use crate::connection::{open_writer_connection, read_and_verify_schema_meta, verify_wal};
use crate::error::{Result, StorageError};
use crate::models::{
    ALGORITHM_VERSION, GapRow, SchemaMeta, SegmentRow, WorkBlockRow, parse_gap_kind,
    parse_row_status,
};

/// 编译期内嵌的唯一 DDL（09 §7.2）。
pub const SCHEMA_SQL: &str = include_str!("../schema/schema.sql");

/// gap cap 的取值上界：max(3 × 10s, 15s)（09 §5.1 允许值推导）。
pub const MAX_GAP_CAP_MS: i64 = 30_000;

#[derive(Debug)]
pub struct Writer {
    conn: Connection,
    meta: SchemaMeta,
    path: PathBuf,
}

/// Observation 插入结果（09 §7.3：重放命中唯一约束返回已处理，不再累计）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ObservationInsert {
    Inserted(i64),
    AlreadyProcessed,
}

impl Writer {
    /// 09 §7.2 bootstrap：临时文件建库 → 校验 → checkpoint → 原子改名。
    /// 失败只删除临时文件，不碰任何既有数据库；目标已存在时拒绝。
    pub fn bootstrap(db_path: &Path, now_utc_ms: i64) -> Result<Self> {
        let tz_id =
            iana_time_zone::get_timezone().map_err(|_| StorageError::time_zone_unavailable())?;
        Self::bootstrap_with_timezone(db_path, &tz_id, now_utc_ms)
    }

    /// 可注入时区的 bootstrap（测试与 TIME_ZONE_UNAVAILABLE 路径）。
    pub fn bootstrap_with_timezone(db_path: &Path, tz_id: &str, now_utc_ms: i64) -> Result<Self> {
        let tz: chrono_tz::Tz = tz_id
            .parse()
            .map_err(|_| StorageError::time_zone_unavailable())?;
        let _ = tz;
        if db_path.exists() {
            return Err(StorageError::db_unavailable(
                "数据库已存在；bootstrap 只创建新库，请用 open_existing",
            ));
        }
        let dir = db_path
            .parent()
            .ok_or_else(|| StorageError::db_unavailable("数据库路径无效"))?;
        std::fs::create_dir_all(dir).map_err(|e| {
            StorageError::db_unavailable("无法创建数据目录").with_detail(e.to_string())
        })?;

        let file_name = db_path
            .file_name()
            .and_then(|n| n.to_str())
            .ok_or_else(|| StorageError::db_unavailable("数据库文件名无效"))?;
        let temp = dir.join(format!(
            "{file_name}.bootstrap-{}.tmp",
            ulid::Ulid::generate()
        ));

        let outcome = (|| -> Result<()> {
            {
                let mut conn = open_writer_connection(&temp)?;
                conn.execute_batch(SCHEMA_SQL)
                    .map_err(StorageError::from_sqlite)?;

                conn.execute(
                    "INSERT INTO schema_meta
                     (singleton_id, schema_version, algorithm_version, created_at_utc_ms, reporting_time_zone_id)
                     VALUES (1, 1, ?1, ?2, ?3)",
                    params![ALGORITHM_VERSION, now_utc_ms, tz_id],
                )
                .map_err(StorageError::from_sqlite)?;

                let digest = Settings::default().content_digest();
                conn.execute(
                    "INSERT INTO settings_revisions (revision, content_digest, applied_at_utc_ms)
                     VALUES (0, ?1, ?2)",
                    params![digest, now_utc_ms],
                )
                .map_err(StorageError::from_sqlite)?;

                let runtime_id = RuntimeId::new();
                conn.execute(
                    "INSERT INTO agent_runtime
                     (runtime_id, process_state, capture_state, writer_state,
                      started_at_utc_ms, heartbeat_at_utc_ms)
                     VALUES (?1, 'starting', 'stopped', 'healthy', ?2, ?2)",
                    params![runtime_id.as_str(), now_utc_ms],
                )
                .map_err(StorageError::from_sqlite)?;

                // 最小事务回滚自检：CHECK 拒绝 + rollback 后行数不变。
                {
                    let tx = conn.transaction().map_err(StorageError::from_sqlite)?;
                    let violated = tx.execute(
                        "INSERT INTO schema_meta
                         (singleton_id, schema_version, algorithm_version, created_at_utc_ms, reporting_time_zone_id)
                         VALUES (2, 1, 'rollback-probe', 0, 'x')",
                        [],
                    );
                    if violated.is_ok() {
                        return Err(StorageError::internal(
                            "bootstrap 自检失败：schema_meta 单例 CHECK 未生效",
                        ));
                    }
                    drop(tx);
                }
                let meta_count: i64 = conn
                    .query_row("SELECT COUNT(*) FROM schema_meta", [], |r| r.get(0))
                    .map_err(StorageError::from_sqlite)?;
                if meta_count != 1 {
                    return Err(StorageError::internal("bootstrap 自检失败：事务回滚未生效"));
                }

                let fk_violations: i64 = conn
                    .query_row("SELECT COUNT(*) FROM pragma_foreign_key_check", [], |r| {
                        r.get(0)
                    })
                    .map_err(StorageError::from_sqlite)?;
                if fk_violations != 0 {
                    return Err(StorageError::internal(
                        "bootstrap 自检失败：foreign_key_check 存在违规",
                    ));
                }
                let quick: String = conn
                    .query_row("PRAGMA quick_check", [], |r| r.get(0))
                    .map_err(StorageError::from_sqlite)?;
                if quick != "ok" {
                    return Err(StorageError::internal(
                        "bootstrap 自检失败：quick_check 未通过",
                    ));
                }

                conn.execute_batch("PRAGMA wal_checkpoint(TRUNCATE)")
                    .map_err(StorageError::from_sqlite)?;
            }
            std::fs::rename(&temp, db_path).map_err(|e| {
                StorageError::db_unavailable("数据库文件原子改名失败").with_detail(e.to_string())
            })?;
            Ok(())
        })();

        if let Err(error) = outcome {
            let _ = std::fs::remove_file(&temp);
            let _ = std::fs::remove_file(temp.with_extension("tmp-wal"));
            let _ = std::fs::remove_file(temp.with_extension("tmp-shm"));
            return Err(error);
        }

        Self::open_existing(db_path)
    }

    /// 打开既有 v0.1 库：连接 bootstrap + schema_meta/WAL 校验。
    pub fn open_existing(db_path: &Path) -> Result<Self> {
        let conn = open_writer_connection(db_path)?;
        let meta = read_and_verify_schema_meta(&conn)?;
        verify_wal(&conn)?;
        Ok(Self {
            conn,
            meta,
            path: db_path.to_path_buf(),
        })
    }

    pub fn schema_meta(&self) -> &SchemaMeta {
        &self.meta
    }

    pub fn path(&self) -> &Path {
        &self.path
    }

    /// 单事务批：Observation/Segment/Work/gap/projection 同一事务提交（09 §7.3）。
    pub fn transaction(&mut self) -> Result<StorageTransaction<'_>> {
        let tx = self
            .conn
            .transaction_with_behavior(TransactionBehavior::Immediate)
            .map_err(StorageError::from_sqlite)?;
        Ok(StorageTransaction { tx })
    }

    /// 启动恢复读取：当前 open 行（至多一行，由部分唯一索引兜底）。
    pub fn find_open_segment(&self) -> Result<Option<SegmentRow>> {
        self.conn
            .query_row(
                "SELECT s.segment_id, s.runtime_id, s.continuity_epoch, s.app_id,
                        a.display_name, s.activity_state, s.start_at_utc_ms, s.end_at_utc_ms,
                        s.duration_ms, s.status
                 FROM activity_segments s
                 JOIN app_identities a ON a.app_id = s.app_id
                 WHERE s.status = 'open'",
                [],
                |row| {
                    Ok((
                        row.get::<_, i64>(0)?,
                        row.get::<_, String>(1)?,
                        row.get::<_, i64>(2)?,
                        row.get::<_, i64>(3)?,
                        row.get::<_, String>(4)?,
                        row.get::<_, String>(5)?,
                        row.get::<_, i64>(6)?,
                        row.get::<_, i64>(7)?,
                        row.get::<_, i64>(8)?,
                        row.get::<_, String>(9)?,
                    ))
                },
            )
            .optional()
            .map_err(StorageError::from_sqlite)?
            .map(
                |(id, rt, epoch, app, name, state, start, end, dur, status)| {
                    Ok(SegmentRow {
                        segment_id: id,
                        runtime_id: rt,
                        continuity_epoch: epoch,
                        app_id: app,
                        app_display_name: name,
                        activity_state: crate::models::parse_activity_state(&state)?,
                        start_at_utc_ms: start,
                        end_at_utc_ms: end,
                        duration_ms: dur,
                        status: parse_row_status(&status)?,
                    })
                },
            )
            .transpose()
    }

    pub fn find_open_work_block(&self) -> Result<Option<WorkBlockRow>> {
        self.conn
            .query_row(
                "SELECT work_block_id, start_at_utc_ms, end_at_utc_ms,
                        active_duration_ms, short_idle_duration_ms, status
                 FROM work_blocks WHERE status = 'open'",
                [],
                |row| {
                    Ok((
                        row.get::<_, i64>(0)?,
                        row.get::<_, i64>(1)?,
                        row.get::<_, i64>(2)?,
                        row.get::<_, i64>(3)?,
                        row.get::<_, i64>(4)?,
                        row.get::<_, String>(5)?,
                    ))
                },
            )
            .optional()
            .map_err(StorageError::from_sqlite)?
            .map(|(id, start, end, active, idle, status)| {
                Ok(WorkBlockRow {
                    work_block_id: id,
                    start_at_utc_ms: start,
                    end_at_utc_ms: end,
                    active_duration_ms: active,
                    short_idle_duration_ms: idle,
                    status: parse_row_status(&status)?,
                })
            })
            .transpose()
    }

    pub fn find_open_gap(&self) -> Result<Option<GapRow>> {
        self.conn
            .query_row(
                "SELECT gap_id, kind, start_at_utc_ms, end_at_utc_ms, status, event_count
                 FROM capture_gaps WHERE status = 'open'",
                [],
                |row| {
                    Ok((
                        row.get::<_, i64>(0)?,
                        row.get::<_, String>(1)?,
                        row.get::<_, i64>(2)?,
                        row.get::<_, Option<i64>>(3)?,
                        row.get::<_, String>(4)?,
                        row.get::<_, i64>(5)?,
                    ))
                },
            )
            .optional()
            .map_err(StorageError::from_sqlite)?
            .map(|(id, kind, start, end, status, count)| {
                Ok(GapRow {
                    gap_id: id,
                    kind: parse_gap_kind(&kind)?,
                    start_at_utc_ms: start,
                    end_at_utc_ms: end,
                    status: parse_row_status(&status)?,
                    event_count: count,
                })
            })
            .transpose()
    }

    /// 最近一次 runtime 行（启动恢复用，09 §6.7）。
    pub fn latest_runtime(&self) -> Result<Option<crate::models::RuntimeRow>> {
        self.conn
            .query_row(
                "SELECT runtime_id, process_state, capture_state, writer_state,
                        started_at_utc_ms, ended_at_utc_ms, heartbeat_at_utc_ms,
                        last_observation_at_utc_ms, last_write_at_utc_ms,
                        capture_queue_depth, writer_queue_depth,
                        dropped_capture_count, dropped_writer_count,
                        continuity_epoch, safe_error_code
                 FROM agent_runtime
                 ORDER BY started_at_utc_ms DESC, runtime_id DESC
                 LIMIT 1",
                [],
                |row| {
                    Ok((
                        row.get::<_, String>(0)?,
                        row.get::<_, String>(1)?,
                        row.get::<_, String>(2)?,
                        row.get::<_, String>(3)?,
                        row.get::<_, i64>(4)?,
                        row.get::<_, Option<i64>>(5)?,
                        row.get::<_, i64>(6)?,
                        row.get::<_, Option<i64>>(7)?,
                        row.get::<_, Option<i64>>(8)?,
                        row.get::<_, i64>(9)?,
                        row.get::<_, i64>(10)?,
                        row.get::<_, i64>(11)?,
                        row.get::<_, i64>(12)?,
                        row.get::<_, i64>(13)?,
                        row.get::<_, Option<String>>(14)?,
                    ))
                },
            )
            .optional()
            .map_err(StorageError::from_sqlite)?
            .map(
                |(
                    runtime_id,
                    process_state,
                    capture_state,
                    writer_state,
                    started,
                    ended,
                    heartbeat,
                    last_obs,
                    last_write,
                    cq,
                    wq,
                    dc,
                    dw,
                    epoch,
                    safe,
                )| {
                    Ok(crate::models::RuntimeRow {
                        runtime_id,
                        process_state: crate::models::parse_process_state(&process_state)?,
                        capture_state: crate::models::parse_capture_state(&capture_state)?,
                        writer_state: crate::models::parse_writer_state(&writer_state)?,
                        started_at_utc_ms: started,
                        ended_at_utc_ms: ended,
                        heartbeat_at_utc_ms: heartbeat,
                        last_observation_at_utc_ms: last_obs,
                        last_write_at_utc_ms: last_write,
                        capture_queue_depth: cq,
                        writer_queue_depth: wq,
                        dropped_capture_count: dc,
                        dropped_writer_count: dw,
                        continuity_epoch: epoch,
                        safe_error_code: safe,
                    })
                },
            )
            .transpose()
    }
}

/// 单事务批操作。所有方法保持存储层不变量；归属决策属于 V01-4。
pub struct StorageTransaction<'w> {
    tx: Transaction<'w>,
}

impl StorageTransaction<'_> {
    pub fn commit(self) -> Result<()> {
        self.tx.commit().map_err(StorageError::from_sqlite)
    }

    /// App Identity upsert：first/last seen 只按 MIN/MAX 更新（09 §7.3，防时钟回拨破坏）。
    pub fn upsert_app_identity(
        &self,
        app_key: &str,
        display_name: &str,
        normalized_process_name: &str,
        seen_at_utc_ms: i64,
    ) -> Result<i64> {
        self.tx
            .query_row(
                "INSERT INTO app_identities
                 (app_key, display_name, normalized_process_name, first_seen_at_utc_ms, last_seen_at_utc_ms)
                 VALUES (?1, ?2, ?3, ?4, ?4)
                 ON CONFLICT(app_key) DO UPDATE SET
                   first_seen_at_utc_ms = MIN(first_seen_at_utc_ms, excluded.first_seen_at_utc_ms),
                   last_seen_at_utc_ms  = MAX(last_seen_at_utc_ms,  excluded.last_seen_at_utc_ms)
                 RETURNING app_id",
                params![app_key, display_name, normalized_process_name, seen_at_utc_ms],
                |row| row.get(0),
            )
            .map_err(StorageError::from_sqlite)
    }

    pub fn insert_settings_revision(
        &self,
        revision: i64,
        content_digest: &str,
        applied_at_utc_ms: i64,
    ) -> Result<()> {
        self.tx
            .execute(
                "INSERT INTO settings_revisions (revision, content_digest, applied_at_utc_ms)
                 VALUES (?1, ?2, ?3)",
                params![revision, content_digest, applied_at_utc_ms],
            )
            .map_err(StorageError::from_sqlite)?;
        Ok(())
    }

    pub fn insert_runtime(&self, runtime_id: &RuntimeId, started_at_utc_ms: i64) -> Result<()> {
        self.tx
            .execute(
                "INSERT INTO agent_runtime
                 (runtime_id, process_state, capture_state, writer_state,
                  started_at_utc_ms, heartbeat_at_utc_ms)
                 VALUES (?1, 'starting', 'stopped', 'healthy', ?2, ?2)",
                params![runtime_id.as_str(), started_at_utc_ms],
            )
            .map_err(StorageError::from_sqlite)?;
        Ok(())
    }

    #[allow(clippy::too_many_arguments)]
    pub fn update_runtime_heartbeat(
        &self,
        runtime_id: &RuntimeId,
        heartbeat_at_utc_ms: i64,
        last_observation_at_utc_ms: Option<i64>,
        last_write_at_utc_ms: Option<i64>,
        capture_queue_depth: i64,
        writer_queue_depth: i64,
        dropped_capture_count: i64,
        dropped_writer_count: i64,
        continuity_epoch: i64,
        process_state: ProcessState,
        capture_state: CaptureState,
        writer_state: WriterState,
        safe_error_code: Option<&str>,
    ) -> Result<()> {
        let process = serde_process_state(process_state);
        let capture = serde_capture_state(capture_state);
        let writer = serde_writer_state(writer_state);
        self.tx
            .execute(
                "UPDATE agent_runtime SET
                   heartbeat_at_utc_ms = ?2,
                   last_observation_at_utc_ms = COALESCE(?3, last_observation_at_utc_ms),
                   last_write_at_utc_ms = COALESCE(?4, last_write_at_utc_ms),
                   capture_queue_depth = ?5,
                   writer_queue_depth = ?6,
                   dropped_capture_count = ?7,
                   dropped_writer_count = ?8,
                   continuity_epoch = ?9,
                   process_state = ?10,
                   capture_state = ?11,
                   writer_state = ?12,
                   safe_error_code = ?13
                 WHERE runtime_id = ?1",
                params![
                    runtime_id.as_str(),
                    heartbeat_at_utc_ms,
                    last_observation_at_utc_ms,
                    last_write_at_utc_ms,
                    capture_queue_depth,
                    writer_queue_depth,
                    dropped_capture_count,
                    dropped_writer_count,
                    continuity_epoch,
                    process,
                    capture,
                    writer,
                    safe_error_code,
                ],
            )
            .map_err(StorageError::from_sqlite)?;
        Ok(())
    }

    pub fn mark_runtime_ended(&self, runtime_id: &RuntimeId, ended_at_utc_ms: i64) -> Result<()> {
        self.tx
            .execute(
                "UPDATE agent_runtime SET ended_at_utc_ms = ?2, process_state = 'stopped'
                 WHERE runtime_id = ?1",
                params![runtime_id.as_str(), ended_at_utc_ms],
            )
            .map_err(StorageError::from_sqlite)?;
        Ok(())
    }

    /// 重放安全：命中 UNIQUE(runtime_id, capture_sequence) 时返回 AlreadyProcessed（09 §7.3）。
    #[allow(clippy::too_many_arguments)]
    pub fn insert_observation(
        &self,
        runtime_id: &RuntimeId,
        capture_sequence: i64,
        continuity_epoch: i64,
        captured_at_utc_ms: i64,
        captured_monotonic_ms: i64,
        app_id: i64,
        activity_state: ActivityState,
        quality: CaptureQuality,
        settings_revision: i64,
    ) -> Result<ObservationInsert> {
        let affected = self
            .tx
            .execute(
                "INSERT OR IGNORE INTO foreground_observations
                 (runtime_id, capture_sequence, continuity_epoch, captured_at_utc_ms,
                  captured_monotonic_ms, app_id, activity_state, quality, settings_revision)
                 VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9)",
                params![
                    runtime_id.as_str(),
                    capture_sequence,
                    continuity_epoch,
                    captured_at_utc_ms,
                    captured_monotonic_ms,
                    app_id,
                    serde_activity_state(activity_state),
                    serde_quality(quality),
                    settings_revision,
                ],
            )
            .map_err(StorageError::from_sqlite)?;
        if affected == 0 {
            return Ok(ObservationInsert::AlreadyProcessed);
        }
        Ok(ObservationInsert::Inserted(self.tx.last_insert_rowid()))
    }

    pub fn open_segment(
        &self,
        runtime_id: &RuntimeId,
        continuity_epoch: i64,
        app_id: i64,
        activity_state: ActivityState,
        at_utc_ms: i64,
        first_observation_id: i64,
    ) -> Result<i64> {
        self.tx
            .query_row(
                "INSERT INTO activity_segments
                 (runtime_id, continuity_epoch, app_id, activity_state,
                  start_at_utc_ms, end_at_utc_ms, duration_ms,
                  first_observation_id, last_observation_id, status, close_reason)
                 VALUES (?1, ?2, ?3, ?4, ?5, ?5, 0, ?6, ?6, 'open', NULL)
                 RETURNING segment_id",
                params![
                    runtime_id.as_str(),
                    continuity_epoch,
                    app_id,
                    serde_activity_state(activity_state),
                    at_utc_ms,
                    first_observation_id,
                ],
                |row| row.get(0),
            )
            .map_err(StorageError::from_sqlite)
    }

    /// open Segment 只向前更新（09 §7.3）：新 end 不得早于当前 end。
    pub fn update_open_segment(
        &self,
        segment_id: i64,
        new_end_at_utc_ms: i64,
        last_observation_id: i64,
    ) -> Result<()> {
        let changed = self
            .tx
            .execute(
                "UPDATE activity_segments
                 SET end_at_utc_ms = ?2,
                     duration_ms = ?2 - start_at_utc_ms,
                     last_observation_id = ?3
                 WHERE segment_id = ?1 AND status = 'open' AND end_at_utc_ms <= ?2",
                params![segment_id, new_end_at_utc_ms, last_observation_id],
            )
            .map_err(StorageError::from_sqlite)?;
        if changed != 1 {
            return Err(StorageError::internal(
                "open Segment 更新被拒绝（不存在、已关闭或时间倒退）",
            ));
        }
        Ok(())
    }

    /// 按原因关闭当前 open Segment；end 保持最后已归属时刻（09 §6.5）。
    pub fn close_open_segment(&self, close_reason: &str) -> Result<()> {
        let changed = self
            .tx
            .execute(
                "UPDATE activity_segments SET status = 'closed', close_reason = ?1
                 WHERE status = 'open'",
                params![close_reason],
            )
            .map_err(StorageError::from_sqlite)?;
        if changed > 1 {
            return Err(StorageError::internal(
                "存在多个 open Segment，违反单例约束",
            ));
        }
        Ok(())
    }

    pub fn open_work_block(
        &self,
        runtime_id: &RuntimeId,
        at_utc_ms: i64,
        first_segment_id: i64,
    ) -> Result<i64> {
        self.tx
            .query_row(
                "INSERT INTO work_blocks
                 (runtime_id, start_at_utc_ms, end_at_utc_ms, active_duration_ms,
                  short_idle_duration_ms, first_activity_segment_id, last_activity_segment_id,
                  status, close_reason)
                 VALUES (?1, ?2, ?2, 0, 0, ?3, ?3, 'open', NULL)
                 RETURNING work_block_id",
                params![runtime_id.as_str(), at_utc_ms, first_segment_id],
                |row| row.get(0),
            )
            .map_err(StorageError::from_sqlite)
    }

    /// open Work Block 绝对值更新（V01-4 状态机持运行合计，写确定值）。
    pub fn update_open_work_block(
        &self,
        work_block_id: i64,
        new_end_at_utc_ms: i64,
        active_duration_ms: i64,
        short_idle_duration_ms: i64,
        last_segment_id: i64,
    ) -> Result<()> {
        let changed = self
            .tx
            .execute(
                "UPDATE work_blocks
                 SET end_at_utc_ms = ?2,
                     active_duration_ms = ?3,
                     short_idle_duration_ms = ?4,
                     last_activity_segment_id = ?5
                 WHERE work_block_id = ?1 AND status = 'open' AND end_at_utc_ms <= ?2",
                params![
                    work_block_id,
                    new_end_at_utc_ms,
                    active_duration_ms,
                    short_idle_duration_ms,
                    last_segment_id,
                ],
            )
            .map_err(StorageError::from_sqlite)?;
        if changed != 1 {
            return Err(StorageError::internal(
                "open Work Block 更新被拒绝（不存在、已关闭或时间倒退）",
            ));
        }
        Ok(())
    }

    pub fn close_open_work_block(&self, close_reason: &str) -> Result<()> {
        let changed = self
            .tx
            .execute(
                "UPDATE work_blocks SET status = 'closed', close_reason = ?1 WHERE status = 'open'",
                params![close_reason],
            )
            .map_err(StorageError::from_sqlite)?;
        if changed > 1 {
            return Err(StorageError::internal(
                "存在多个 open Work Block，违反单例约束",
            ));
        }
        Ok(())
    }

    pub fn open_gap(
        &self,
        runtime_id: &RuntimeId,
        kind: GapKind,
        start_at_utc_ms: i64,
    ) -> Result<i64> {
        self.tx
            .query_row(
                "INSERT INTO capture_gaps
                 (runtime_id, start_at_utc_ms, end_at_utc_ms, kind, status, event_count)
                 VALUES (?1, ?2, NULL, ?3, 'open', 1)
                 RETURNING gap_id",
                params![runtime_id.as_str(), start_at_utc_ms, serde_gap_kind(kind)],
                |row| row.get(0),
            )
            .map_err(StorageError::from_sqlite)
    }

    /// 同类相邻 gap 事件合并：event_count + 1（09 §6.7 叠加规则）。
    pub fn extend_open_gap(&self) -> Result<()> {
        let changed = self
            .tx
            .execute(
                "UPDATE capture_gaps SET event_count = event_count + 1 WHERE status = 'open'",
                [],
            )
            .map_err(StorageError::from_sqlite)?;
        if changed != 1 {
            return Err(StorageError::internal("没有可延伸的 open gap"));
        }
        Ok(())
    }

    pub fn close_open_gap(&self, end_at_utc_ms: i64) -> Result<()> {
        let changed = self
            .tx
            .execute(
                "UPDATE capture_gaps SET status = 'closed', end_at_utc_ms = ?1
                 WHERE status = 'open'",
                params![end_at_utc_ms],
            )
            .map_err(StorageError::from_sqlite)?;
        if changed > 1 {
            return Err(StorageError::internal("存在多个 open gap，违反单例约束"));
        }
        Ok(())
    }

    /// 写入已闭合 gap（sampling_transition、clock_changed、capture_delayed 等区间已知的边界）。
    pub fn insert_closed_gap(
        &self,
        runtime_id: &RuntimeId,
        kind: GapKind,
        start_at_utc_ms: i64,
        end_at_utc_ms: i64,
    ) -> Result<i64> {
        self.tx
            .query_row(
                "INSERT INTO capture_gaps
                 (runtime_id, start_at_utc_ms, end_at_utc_ms, kind, status, event_count)
                 VALUES (?1, ?2, ?3, ?4, 'closed', 1)
                 RETURNING gap_id",
                params![
                    runtime_id.as_str(),
                    start_at_utc_ms,
                    end_at_utc_ms,
                    serde_gap_kind(kind)
                ],
                |row| row.get(0),
            )
            .map_err(StorageError::from_sqlite)
    }

    /// idle_break 专用：把 Work Block 回溯结束于 Idle 起点（09 §6.6），
    /// 允许 end 早于当前 end（这是 7.3 前向更新规则的唯一受控例外）。
    pub fn close_open_work_block_with_end(
        &self,
        close_reason: &str,
        end_at_utc_ms: i64,
    ) -> Result<()> {
        let changed = self
            .tx
            .execute(
                "UPDATE work_blocks
                 SET status = 'closed', close_reason = ?1, end_at_utc_ms = ?2
                 WHERE status = 'open'",
                params![close_reason, end_at_utc_ms],
            )
            .map_err(StorageError::from_sqlite)?;
        if changed > 1 {
            return Err(StorageError::internal(
                "存在多个 open Work Block，违反单例约束",
            ));
        }
        Ok(())
    }

    pub(crate) fn raw_tx(&self) -> &Transaction<'_> {
        &self.tx
    }
}

pub(crate) fn serde_activity_state(state: ActivityState) -> &'static str {
    match state {
        ActivityState::Active => "active",
        ActivityState::Idle => "idle",
        ActivityState::Unknown => "unknown",
    }
}

pub(crate) fn serde_quality(quality: CaptureQuality) -> &'static str {
    match quality {
        CaptureQuality::Normal => "normal",
        CaptureQuality::ProcessNameFallback => "process_name_fallback",
        CaptureQuality::IdleUnavailable => "idle_unavailable",
    }
}

pub(crate) fn serde_gap_kind(kind: GapKind) -> &'static str {
    match kind {
        GapKind::SamplingTransition => "sampling_transition",
        GapKind::CaptureDelayed => "capture_delayed",
        GapKind::PrivacyExcluded => "privacy_excluded",
        GapKind::CaptureQueueDrop => "capture_queue_drop",
        GapKind::WriterQueueDrop => "writer_queue_drop",
        GapKind::CapturePaused => "capture_paused",
        GapKind::CaptureStopped => "capture_stopped",
        GapKind::SystemSleep => "system_sleep",
        GapKind::SessionLocked => "session_locked",
        GapKind::AgentRestart => "agent_restart",
        GapKind::ClockChanged => "clock_changed",
        GapKind::CaptureError => "capture_error",
    }
}

pub(crate) fn serde_process_state(state: ProcessState) -> &'static str {
    match state {
        ProcessState::Starting => "starting",
        ProcessState::Running => "running",
        ProcessState::Degraded => "degraded",
        ProcessState::Faulted => "faulted",
        ProcessState::ShuttingDown => "shutting_down",
        ProcessState::Stopped => "stopped",
    }
}

pub(crate) fn serde_capture_state(state: CaptureState) -> &'static str {
    match state {
        CaptureState::Stopped => "stopped",
        CaptureState::Running => "running",
        CaptureState::Paused => "paused",
    }
}

pub(crate) fn serde_writer_state(state: WriterState) -> &'static str {
    match state {
        WriterState::Healthy => "healthy",
        WriterState::Degraded => "degraded",
        WriterState::Faulted => "faulted",
    }
}
