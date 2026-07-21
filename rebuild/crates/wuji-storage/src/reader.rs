//! 只读 Query：Today、Timeline、Agent 状态快照（09 §7.3、§8.4）。
//!
//! Reader 以 read-only + query_only 打开并验证 schema_version（09 §7.3）；
//! 所有时长/ID/毫秒以 Int64String 返回。

use std::path::Path;

use rusqlite::{Connection, OptionalExtension, params};
use wuji_core::dto::{
    AgentStatusDto, AppDto, Int64String, LocalDate, RuntimeId, TimelineCursor, TimelineGapDto,
    TimelineItem, TimelineItemKind, TimelinePageDto, TimelineSegmentDto, TodayDto, TodayQualityDto,
    TopAppDto,
};

use crate::connection::{open_reader_connection, read_and_verify_schema_meta};
use crate::error::{Result, StorageError};
use crate::models::{
    RuntimeRow, SchemaMeta, parse_activity_state, parse_capture_state, parse_gap_kind,
    parse_process_state, parse_row_status, parse_writer_state,
};
use crate::timeutil::{local_date_of, local_day_range_utc_ms};

#[derive(Debug)]
pub struct Reader {
    conn: Connection,
    meta: SchemaMeta,
}

impl Reader {
    pub fn open(db_path: &Path) -> Result<Self> {
        let conn = open_reader_connection(db_path)?;
        let meta = read_and_verify_schema_meta(&conn)?;
        Ok(Self { conn, meta })
    }

    pub fn schema_meta(&self) -> &SchemaMeta {
        &self.meta
    }

    /// Today：daily 读模型 + open/closed Segment 的 current/last app（09 §8.4 字段口径）。
    pub fn today(&self, date: &LocalDate) -> Result<TodayDto> {
        let tz = self.meta.reporting_tz()?;
        let date_str = date.as_str().to_string();

        let work = self
            .conn
            .query_row(
                "SELECT active_duration_ms, short_idle_duration_ms, work_block_count,
                        longest_work_block_active_ms, raw_app_switch_count, data_gap_count
                 FROM daily_work_metrics WHERE local_date = ?1",
                params![date_str],
                |row| {
                    Ok((
                        row.get::<_, i64>(0)?,
                        row.get::<_, i64>(1)?,
                        row.get::<_, i64>(2)?,
                        row.get::<_, i64>(3)?,
                        row.get::<_, i64>(4)?,
                        row.get::<_, i64>(5)?,
                    ))
                },
            )
            .optional()?;
        let (_work_active, _work_idle, block_count, longest, raw_switches, gap_count) =
            work.unwrap_or((0, 0, 0, 0, 0, 0));

        let mut app_stmt = self.conn.prepare(
            "SELECT u.app_id, a.display_name, u.active_duration_ms
             FROM daily_app_usage u
             JOIN app_identities a ON a.app_id = u.app_id
             WHERE u.local_date = ?1
             ORDER BY u.active_duration_ms DESC, u.app_id ASC
             LIMIT 20",
        )?;
        let app_rows = app_stmt.query_map(params![date_str], |row| {
            Ok((
                row.get::<_, i64>(0)?,
                row.get::<_, String>(1)?,
                row.get::<_, i64>(2)?,
            ))
        })?;
        let mut top_apps = Vec::new();
        let mut total_active = 0_i64;
        for app in app_rows {
            let (app_id, display_name, active) = app?;
            total_active += active;
            top_apps.push(TopAppDto {
                app: AppDto {
                    app_id: Int64String(app_id),
                    display_name,
                },
                active_duration_ms: Int64String(active),
            });
        }

        let current_app = self.app_of_segment("open")?;
        let last_app = self
            .conn
            .query_row(
                "SELECT s.app_id, a.display_name
                 FROM activity_segments s
                 JOIN app_identities a ON a.app_id = s.app_id
                 WHERE s.status = 'closed'
                 ORDER BY s.end_at_utc_ms DESC, s.segment_id DESC
                 LIMIT 1",
                [],
                |row| {
                    Ok(AppDto {
                        app_id: Int64String(row.get(0)?),
                        display_name: row.get(1)?,
                    })
                },
            )
            .optional()?;

        let dropped_count = self.dropped_count_of_date(&tz, date, &date_str)?;
        let is_complete = gap_count == 0 && dropped_count == 0;

        Ok(TodayDto {
            local_date: date.clone(),
            reporting_time_zone_id: self.meta.reporting_time_zone_id.clone(),
            active_duration_ms: Int64String(total_active),
            current_app,
            last_app,
            longest_work_block_active_ms: Int64String(longest),
            work_block_count: Int64String(block_count),
            raw_app_switch_count: Int64String(raw_switches),
            top_apps,
            quality: TodayQualityDto {
                is_complete,
                gap_count: Int64String(gap_count),
                dropped_count: Int64String(dropped_count),
            },
        })
    }

    fn app_of_segment(&self, status: &str) -> Result<Option<AppDto>> {
        self.conn
            .query_row(
                "SELECT s.app_id, a.display_name
                 FROM activity_segments s
                 JOIN app_identities a ON a.app_id = s.app_id
                 WHERE s.status = ?1",
                params![status],
                |row| {
                    Ok(AppDto {
                        app_id: Int64String(row.get(0)?),
                        display_name: row.get(1)?,
                    })
                },
            )
            .optional()
            .map_err(StorageError::from)
    }

    /// 当日 queue drop 类 gap 的事件计数（丢弃事件次数，非被丢 Observation 数）。
    fn dropped_count_of_date(
        &self,
        tz: &chrono_tz::Tz,
        date: &LocalDate,
        date_str: &str,
    ) -> Result<i64> {
        let (day_start, day_end) = local_day_range_utc_ms(tz, date)?;
        let mut stmt = self.conn.prepare(
            "SELECT start_at_utc_ms FROM capture_gaps
             WHERE kind IN ('capture_queue_drop', 'writer_queue_drop')
               AND start_at_utc_ms >= ?1 AND start_at_utc_ms < ?2",
        )?;
        let rows = stmt.query_map(params![day_start, day_end], |row| row.get::<_, i64>(0))?;
        let mut count = 0_i64;
        for start in rows {
            let start = start?;
            if local_date_of(tz, start)? == date_str {
                count += 1;
            }
        }
        Ok(count)
    }

    /// Timeline：Segment/Gap 混合按 `(start_at_utc_ms, kind, id)` 升序分页（09 §8.4）。
    pub fn timeline(
        &self,
        date: &LocalDate,
        cursor: Option<TimelineCursor>,
        limit: u32,
    ) -> Result<TimelinePageDto> {
        let tz = self.meta.reporting_tz()?;
        let (day_start, day_end) = local_day_range_utc_ms(&tz, date)?;
        let (cursor_start, cursor_kind, cursor_id) = cursor
            .map(|c| {
                (
                    c.start_at_utc_ms,
                    match c.item_kind {
                        TimelineItemKind::Segment => 0_i64,
                        TimelineItemKind::Gap => 1_i64,
                    },
                    c.id,
                )
            })
            .unwrap_or((i64::MIN, 0, i64::MIN));
        let fetch_limit = i64::from(limit.max(1)) + 1;

        let mut stmt = self.conn.prepare(
            "SELECT kind_rank, kind, id, start_at, end_at, app_id, display_name,
                    activity_state, status, gap_kind, event_count
             FROM (
               SELECT 0 AS kind_rank, 'segment' AS kind, s.segment_id AS id,
                      s.start_at_utc_ms AS start_at, s.end_at_utc_ms AS end_at,
                      s.app_id AS app_id, a.display_name AS display_name,
                      s.activity_state AS activity_state, s.status AS status,
                      NULL AS gap_kind, NULL AS event_count
               FROM activity_segments s
               JOIN app_identities a ON a.app_id = s.app_id
               WHERE s.end_at_utc_ms >= ?1 AND s.start_at_utc_ms < ?2
               UNION ALL
               SELECT 1, 'gap', g.gap_id, g.start_at_utc_ms, g.end_at_utc_ms,
                      NULL, NULL, NULL, g.status, g.kind, g.event_count
               FROM capture_gaps g
               WHERE g.start_at_utc_ms < ?2
                 AND (g.end_at_utc_ms IS NULL OR g.end_at_utc_ms >= ?1)
             )
             WHERE start_at > ?3
                OR (start_at = ?3 AND kind_rank > ?4)
                OR (start_at = ?3 AND kind_rank = ?4 AND id > ?5)
             ORDER BY start_at, kind_rank, id
             LIMIT ?6",
        )?;

        let rows = stmt.query_map(
            params![
                day_start,
                day_end,
                cursor_start,
                cursor_kind,
                cursor_id,
                fetch_limit
            ],
            |row| {
                Ok((
                    row.get::<_, i64>(0)?,
                    row.get::<_, String>(1)?,
                    row.get::<_, i64>(2)?,
                    row.get::<_, i64>(3)?,
                    row.get::<_, Option<i64>>(4)?,
                    row.get::<_, Option<i64>>(5)?,
                    row.get::<_, Option<String>>(6)?,
                    row.get::<_, Option<String>>(7)?,
                    row.get::<_, String>(8)?,
                    row.get::<_, Option<String>>(9)?,
                    row.get::<_, Option<i64>>(10)?,
                ))
            },
        )?;

        let mut keyed: Vec<((i64, TimelineItemKind, i64), TimelineItem)> = Vec::new();
        for row in rows {
            let (
                _rank,
                kind,
                id,
                start,
                end,
                app_id,
                display_name,
                activity_state,
                status,
                gap_kind,
                event_count,
            ) = row?;
            let (item, item_kind) = match kind.as_str() {
                "segment" => {
                    let state =
                        parse_activity_state(activity_state.as_deref().unwrap_or("unknown"))?;
                    let end = end.unwrap_or(start);
                    (
                        TimelineItem::Segment(TimelineSegmentDto {
                            segment_id: Int64String(id),
                            app: AppDto {
                                app_id: Int64String(app_id.unwrap_or(0)),
                                display_name: display_name.unwrap_or_default(),
                            },
                            activity_state: state,
                            start_at_utc_ms: Int64String(start),
                            end_at_utc_ms: Int64String(end),
                            duration_ms: Int64String(end - start),
                            status: parse_row_status(&status)?,
                        }),
                        TimelineItemKind::Segment,
                    )
                }
                "gap" => (
                    TimelineItem::Gap(TimelineGapDto {
                        gap_id: Int64String(id),
                        gap_kind: parse_gap_kind(gap_kind.as_deref().unwrap_or("capture_error"))?,
                        start_at_utc_ms: Int64String(start),
                        end_at_utc_ms: end.map(Int64String),
                        status: parse_row_status(&status)?,
                        event_count: event_count.unwrap_or(1) as u32,
                    }),
                    TimelineItemKind::Gap,
                ),
                _ => return Err(StorageError::internal("Timeline 查询返回未知条目类型")),
            };
            keyed.push(((start, item_kind, id), item));
            if keyed.len() as i64 == fetch_limit {
                break;
            }
        }

        let has_more = keyed.len() as i64 == fetch_limit;
        if has_more {
            keyed.pop();
        }
        let next_cursor = if has_more {
            keyed.last().map(|((start, kind, id), _)| {
                TimelineCursor {
                    start_at_utc_ms: *start,
                    item_kind: *kind,
                    id: *id,
                }
                .encode()
            })
        } else {
            None
        };
        let items = keyed.into_iter().map(|(_, item)| item).collect();

        Ok(TimelinePageDto {
            local_date: date.clone(),
            reporting_time_zone_id: self.meta.reporting_time_zone_id.clone(),
            items,
            next_cursor,
        })
    }

    /// 已记录的最大 settings revision（无记录时为 None）。
    pub fn max_settings_revision(&self) -> Result<Option<i64>> {
        self.conn
            .query_row("SELECT MAX(revision) FROM settings_revisions", [], |row| {
                row.get(0)
            })
            .map_err(StorageError::from_sqlite)
    }

    /// 最近一次 Agent runtime 快照（09 §8.4 AgentStatusDto 的 DB 部分）。
    pub fn latest_runtime(&self) -> Result<Option<RuntimeRow>> {
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
                    Ok(RuntimeRow {
                        runtime_id,
                        process_state: parse_process_state(&process_state)?,
                        capture_state: parse_capture_state(&capture_state)?,
                        writer_state: parse_writer_state(&writer_state)?,
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

/// 由 runtime 快照与版本常量组装 AgentStatusDto（V01-6 用；agent_version 由调用方补）。
pub fn status_dto_from_runtime(
    row: &RuntimeRow,
    agent_version: String,
    runtime_id: &RuntimeId,
) -> AgentStatusDto {
    AgentStatusDto {
        agent_version,
        protocol_version: 1,
        schema_version: 1,
        process_state: row.process_state,
        capture_state: row.capture_state,
        writer_state: row.writer_state,
        runtime_id: runtime_id.clone(),
        heartbeat_at_utc_ms: Some(Int64String(row.heartbeat_at_utc_ms)),
        last_observation_at_utc_ms: row.last_observation_at_utc_ms.map(Int64String),
        last_write_at_utc_ms: row.last_write_at_utc_ms.map(Int64String),
        capture_queue_depth: row.capture_queue_depth as u32,
        writer_queue_depth: row.writer_queue_depth as u32,
        dropped_capture_count: Int64String(row.dropped_capture_count),
        dropped_writer_count: Int64String(row.dropped_writer_count),
        safe_error_code: None,
    }
}
