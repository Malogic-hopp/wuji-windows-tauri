//! 只读 Query：Today、Timeline、Agent 状态快照（09 §7.3、§8.4）。
//!
//! Reader 以 read-only + query_only 打开并验证 schema_version（09 §7.3）；
//! 所有时长/ID/毫秒以 Int64String 返回。

use std::collections::{BTreeMap, HashMap, HashSet};
use std::path::Path;

use chrono::NaiveDate;
use rusqlite::{Connection, OptionalExtension, params};
use wuji_core::dto::{
    AgentStatusDto, AppDto, HeatmapCellDto, HeatmapDto, Int64String, LocalDate, RuntimeId,
    TimelineCursor, TimelineGapDto, TimelineItem, TimelineItemKind, TimelinePageDto,
    TimelineSegmentDto, TodayDto, TodayQualityDto, TopAppDto,
};
use wuji_core::error::SafeErrorCode;

use crate::connection::{open_reader_connection, read_and_verify_schema_meta};
use crate::error::{Result, StorageError};
use crate::models::{
    RuntimeRow, SchemaMeta, parse_activity_state, parse_capture_state, parse_gap_kind,
    parse_process_state, parse_row_status, parse_writer_state,
};
use crate::timeutil::{
    local_date_of, local_day_range_utc_ms, local_fields, same_moment_cutoff_utc_ms,
};

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

        // Today 活跃时长 = 当日 daily_app_usage 全部应用总和（09 §7.3 守恒：
        // 该总和恒等于当日可靠 Segment 的 active 交集），不受 Top Apps 的 LIMIT 20 截断（R02）。
        let total_active: i64 = self.conn.query_row(
            "SELECT COALESCE(SUM(active_duration_ms), 0) FROM daily_app_usage WHERE local_date = ?1",
            params![date_str],
            |row| row.get(0),
        )?;

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
        for app in app_rows {
            let (app_id, display_name, active) = app?;
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

    /// 当日 queue drop 类 gap 的 event_count 总和（R02：合并 gap 的事件必须全计）。
    /// 语义：丢弃事件次数（每次事件至少丢弃一条 Observation），不是被丢 Observation 数。
    fn dropped_count_of_date(
        &self,
        tz: &chrono_tz::Tz,
        date: &LocalDate,
        date_str: &str,
    ) -> Result<i64> {
        let (day_start, day_end) = local_day_range_utc_ms(tz, date)?;
        let mut stmt = self.conn.prepare(
            "SELECT start_at_utc_ms, event_count FROM capture_gaps
             WHERE kind IN ('capture_queue_drop', 'writer_queue_drop')
               AND start_at_utc_ms >= ?1 AND start_at_utc_ms < ?2",
        )?;
        let rows = stmt.query_map(params![day_start, day_end], |row| {
            Ok((row.get::<_, i64>(0)?, row.get::<_, i64>(1)?))
        })?;
        let mut count = 0_i64;
        for row in rows {
            let (start, event_count) = row?;
            if local_date_of(tz, start)? == date_str {
                count += event_count;
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

    /// Heatmap：以 today 按 week_offset 整周平移后的锚点为终点、最近 days 天 × 24 小时聚合
    /// （hourly 投影；09 §8.4）。cells 稀疏（仅含时长 > 0 的格子）；
    /// 强度按结果集内最大 active 归一化为 0-4。DTO 的 today 始终保持真实今天，
    /// range_end_local_date 承载历史范围终点。
    pub fn heatmap(&self, today: &LocalDate, days: u32, week_offset: i32) -> Result<HeatmapDto> {
        // days/week_offset 不变量由存储层自身维护：QueryService 之外的直接调用同样受约束，
        // 不产生矛盾 DTO；先校验再算术，杜绝大 offset 溢出。
        if days == 0 || days > 31 {
            return Err(StorageError::new(
                SafeErrorCode::InvalidArgument,
                "days 必须在 1 到 31 之间",
            ));
        }
        // 只允许查看当前周及最多 520 个历史周，不查询未来周。
        if !(-520..=0).contains(&week_offset) {
            return Err(StorageError::new(
                SafeErrorCode::InvalidArgument,
                "week_offset 必须在 -520 到 0 之间",
            ));
        }
        let today_naive = NaiveDate::parse_from_str(today.as_str(), "%Y-%m-%d").map_err(|_| {
            StorageError::new(
                SafeErrorCode::InvalidArgument,
                "日期必须使用 YYYY-MM-DD 格式",
            )
        })?;
        // i64 算术（无 u32 乘法溢出）；历法越界显式报错。
        let offset_days = i64::from(week_offset) * 7;
        let anchor = if offset_days < 0 {
            today_naive.checked_sub_days(chrono::Days::new(offset_days.unsigned_abs()))
        } else {
            today_naive.checked_add_days(chrono::Days::new(offset_days as u64))
        }
        .ok_or_else(|| {
            StorageError::new(SafeErrorCode::InvalidArgument, "week_offset 导致日期越界")
        })?;
        let start = anchor
            .checked_sub_days(chrono::Days::new(u64::from(days - 1)))
            .ok_or_else(|| StorageError::internal("日期越界"))?;
        let start_str = start.format("%Y-%m-%d").to_string();
        let anchor_str = anchor.format("%Y-%m-%d").to_string();

        let mut stmt = self.conn.prepare(
            "SELECT local_date, local_hour,
                    COALESCE(SUM(active_duration_ms), 0),
                    COALESCE(SUM(idle_duration_ms), 0),
                    COALESCE(SUM(unknown_duration_ms), 0)
             FROM hourly_app_usage
             WHERE local_date >= ?1 AND local_date <= ?2
             GROUP BY local_date, local_hour
             ORDER BY local_date, local_hour",
        )?;
        let rows = stmt.query_map(params![start_str, anchor_str], |row| {
            Ok((
                row.get::<_, String>(0)?,
                row.get::<_, u32>(1)?,
                row.get::<_, i64>(2)?,
                row.get::<_, i64>(3)?,
                row.get::<_, i64>(4)?,
            ))
        })?;
        let mut raw: Vec<(String, u32, i64, i64, i64)> = Vec::new();
        for row in rows {
            let (date, hour, active, idle, unknown) = row?;
            // 逐字段逻辑或判断：极端数据下不做整数加法，避免 i64 相加溢出。
            if active > 0 || idle > 0 || unknown > 0 {
                raw.push((date, hour, active, idle, unknown));
            }
        }

        let max_active = raw.iter().map(|r| r.2).max().unwrap_or(0);
        let cells = raw
            .into_iter()
            .map(|(date, hour, active, idle, unknown)| HeatmapCellDto {
                local_date: date,
                local_hour: hour,
                active_duration_ms: Int64String(active),
                idle_duration_ms: Int64String(idle),
                unknown_duration_ms: Int64String(unknown),
                intensity_level: heatmap_intensity_level(active, max_active),
            })
            .collect();

        Ok(HeatmapDto {
            today: today.clone(),
            range_end_local_date: LocalDate::parse(&anchor_str)
                .map_err(|e| StorageError::new(e.code, e.message))?,
            reporting_time_zone_id: self.meta.reporting_time_zone_id.clone(),
            days,
            cells,
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

/// Heatmap 强度 0-4：active 为 0 → 0；按结果集最大 active 归一化，
/// ≤1/4 → 1、≤1/2 → 2、≤3/4 → 3、其余 → 4（与旧版 HourActivityHeatmapCalculator 分桶一致；
/// u128 交叉乘比较，避免浮点）。
fn heatmap_intensity_level(active: i64, max_active: i64) -> u32 {
    if active <= 0 || max_active <= 0 {
        return 0;
    }
    let a = active as u128;
    let m = max_active as u128;
    if a * 4 <= m {
        1
    } else if a * 2 <= m {
        2
    } else if a * 4 <= m * 3 {
        3
    } else {
        4
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
        // 离线诊断透传持久化的安全错误码（审核 R09）。
        safe_error_code: row
            .safe_error_code
            .as_deref()
            .and_then(wuji_core::error::SafeErrorCode::from_code),
    }
}

// ===== 统计主页投影原语（10 设计 §5 + 11 实施方案阶段二）=====

/// 读事务快照包装：独占借用 `Reader.conn`（快照存活期间 Reader 本体不可再借用），
/// 保证一次命令的全部统计子查询在同一读事务（WAL 快照）内执行（11 阶段三 3.1）。
/// 统计投影原语全部实现在本类型上，类型层面杜绝"事务开着却绕过事务直查连接"的路径。
pub struct ReaderSnapshot<'a> {
    tx: rusqlite::Transaction<'a>,
    meta: &'a SchemaMeta,
    /// 本快照内 `stats_cutoff_series` 调用次数（命令级单批次回归钩子；Cell 单线程）。
    cutoff_calls: std::cell::Cell<u32>,
}

/// 单日工作统计投影行（`stats_daily_rows` 输出；Query 层映射为 wuji-core 纯输入
/// `DailyMetricSample` 后调用纯函数）。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DayMetric {
    pub local_date: String,
    pub active_duration_ms: i64,
    pub work_block_count: i64,
    pub has_data: bool,
}

/// 单日同时刻截断结果（`stats_cutoff_series` 输出）。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DayAtCutoff {
    pub local_date: String,
    pub active_duration_ms: i64,
    pub work_block_count: i64,
}

/// 周期内应用总量行（`stats_app_totals` 输出；slot 排名输入）。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AppTotalRow {
    pub app_id: i64,
    pub display_name: String,
    pub total_active_ms: i64,
}

/// 逐日逐应用行（`stats_app_rows` 输出；日/周桶聚合输入）。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AppDayRow {
    pub local_date: String,
    pub app_id: i64,
    pub display_name: String,
    pub active_ms: i64,
}

impl Reader {
    /// 在单一读事务快照内执行统计投影（11 阶段三 3.1）：子查询必须通过
    /// `ReaderSnapshot` 执行；闭包返回 Err 时事务自动回滚（rusqlite Transaction
    /// 的 Drop 语义），不影响连接后续使用。
    ///
    /// 快照独占借用 `Reader.conn`，存活期间 Reader 本体不可再借用（编译期保证）：
    ///
    /// ```compile_fail
    /// # use wuji_storage::Reader;
    /// # fn demo(reader: &mut Reader) -> Result<(), wuji_storage::StorageError> {
    /// reader.with_snapshot(|_snap| {
    ///     let _ = reader.schema_meta(); // error[E0502]：快照独占借用 Reader.conn，
    ///                                   // 存活期间无法再以任何方式访问 Reader 本体
    ///     Ok(())
    /// })
    /// # }
    /// ```
    pub fn with_snapshot<T>(
        &mut self,
        f: impl FnOnce(&ReaderSnapshot<'_>) -> Result<T>,
    ) -> Result<T> {
        let tx = self
            .conn
            .unchecked_transaction()
            .map_err(StorageError::from_sqlite)?;
        let snapshot = ReaderSnapshot {
            tx,
            meta: &self.meta,
            cutoff_calls: std::cell::Cell::new(0),
        };
        let result = f(&snapshot);
        if result.is_ok() {
            snapshot.tx.commit().map_err(StorageError::from_sqlite)?;
        }
        result
    }
}

impl ReaderSnapshot<'_> {
    /// reporting 时区（与 Reader 同一 meta）。
    pub fn reporting_tz(&self) -> Result<chrono_tz::Tz> {
        self.meta.reporting_tz()
    }

    /// 每日骨架行（11 阶段二 2.3）：先生成 `[start, end]` 完整本地日期序列，再映射
    /// `daily_work_metrics` 行；SQL 无行的日期生成零值 `has_data = false`——趋势数组
    /// 长度恒等 7/14/30，不依赖数据库是否存在该日行。范围上限 366 天（与 heatmap 的
    /// ≤31 天同一类防御护栏；当前调用方最长为 37 天）。
    pub fn stats_daily_rows(&self, start: &LocalDate, end: &LocalDate) -> Result<Vec<DayMetric>> {
        let start_naive = naive_of(start)?;
        let end_naive = naive_of(end)?;
        if end_naive < start_naive {
            return Err(StorageError::new(
                SafeErrorCode::InvalidArgument,
                "日期范围无效（end 早于 start）",
            ));
        }
        let span_days = (end_naive - start_naive).num_days() + 1;
        if span_days > 366 {
            return Err(StorageError::new(
                SafeErrorCode::InvalidArgument,
                "日期范围过长（最多 366 天）",
            ));
        }
        // 完整本地日期骨架。
        let mut skeleton: Vec<String> = Vec::new();
        let mut day = start_naive;
        loop {
            skeleton.push(day.format("%Y-%m-%d").to_string());
            if day == end_naive {
                break;
            }
            day = day
                .succ_opt()
                .ok_or_else(|| StorageError::internal("日期序列越界"))?;
        }
        // 存在的行映射进骨架；缺失日期补零 has_data=false。
        let mut existing: HashMap<String, (i64, i64)> = HashMap::new();
        {
            let mut stmt = self.tx.prepare(
                "SELECT local_date, active_duration_ms, work_block_count
                 FROM daily_work_metrics WHERE local_date BETWEEN ?1 AND ?2",
            )?;
            let iter = stmt.query_map(params![start.as_str(), end.as_str()], |row| {
                Ok((
                    row.get::<_, String>(0)?,
                    row.get::<_, i64>(1)?,
                    row.get::<_, i64>(2)?,
                ))
            })?;
            for row in iter {
                let (date, active, blocks) = row?;
                existing.insert(date, (active, blocks));
            }
        }
        Ok(skeleton
            .into_iter()
            .map(|date| match existing.remove(&date) {
                Some((active, blocks)) => DayMetric {
                    local_date: date,
                    active_duration_ms: active,
                    work_block_count: blocks,
                    has_data: true,
                },
                None => DayMetric {
                    local_date: date,
                    active_duration_ms: 0,
                    work_block_count: 0,
                    has_data: false,
                },
            })
            .collect())
    }

    /// 本快照内已执行的 `stats_cutoff_series` 次数（命令级单批次回归钩子；测试用）。
    #[doc(hidden)]
    pub fn stats_cutoff_series_calls(&self) -> u32 {
        self.cutoff_calls.get()
    }

    /// 从 before 向前寻找最近 limit 个有记录日期（11 阶段二 2.3）；**不是固定
    /// lookback**——历史缺失时返回不足 limit 个，调用方据此判定 `sampleDays < 3`，
    /// 不擅自补足。
    pub fn recent_recorded_dates(
        &self,
        before: &LocalDate,
        limit: usize,
    ) -> Result<Vec<LocalDate>> {
        let limit = i64::try_from(limit).map_err(|_| StorageError::internal("limit 超出 i64"))?;
        let mut stmt = self.tx.prepare(
            "SELECT local_date FROM daily_work_metrics
             WHERE local_date < ?1
             ORDER BY local_date DESC LIMIT ?2",
        )?;
        let iter = stmt.query_map(params![before.as_str(), limit], |row| {
            row.get::<_, String>(0)
        })?;
        let mut dates = Vec::new();
        for row in iter {
            let raw = row?;
            dates.push(LocalDate::parse(&raw).map_err(|e| StorageError::new(e.code, e.message))?);
        }
        dates.reverse(); // DESC → 升序
        Ok(dates)
    }

    /// 多日期同时刻截断聚合（11 阶段二 2.3）：一条 VALUES CTE + LEFT JOIN 完成全部
    /// 日期的活动时长截断（零活动日期返回 0 而非缺失）；今日 workBlockCount 用同快照
    /// 第二条批量 SQL 从 `work_blocks` 按 `recompute_dates` 口径计数（含未闭合块）。
    /// **重复日期按首次出现去重查询、按原始输入 `get()` 映射**（不翻倍不归零），
    /// 结果保持确定性顺序；**不逐日发 SQL**。
    pub fn stats_cutoff_series(
        &self,
        tz: &chrono_tz::Tz,
        today: &LocalDate,
        now_utc_ms: i64,
        dates: &[LocalDate],
    ) -> Result<Vec<DayAtCutoff>> {
        self.cutoff_calls.set(self.cutoff_calls.get() + 1);
        if dates.is_empty() {
            return Ok(Vec::new());
        }
        // 按首次出现顺序去重：阶段三组装（昨日 + 近 7 有效日 + 上周同期）可能自然
        // 产生重复日期（昨日通常也在近 7 有效日内）。CTE 只查询唯一日期，避免
        // GROUP BY 对重复日期重复累计。
        let mut cuts: Vec<(String, i64, i64)> = Vec::with_capacity(dates.len());
        let mut seen: HashSet<String> = HashSet::new();
        for date in dates {
            if seen.contains(date.as_str()) {
                continue;
            }
            let (day_start, _day_end) = local_day_range_utc_ms(tz, date)?;
            let cutoff = same_moment_cutoff_utc_ms(tz, date, now_utc_ms, today)?;
            seen.insert(date.as_str().to_string());
            cuts.push((date.as_str().to_string(), day_start, cutoff));
        }
        let placeholders: Vec<String> = cuts.iter().map(|_| "(?,?,?)".to_string()).collect();
        let values_sql = placeholders.join(",");
        let mut params: Vec<&dyn rusqlite::ToSql> = Vec::new();
        for (date, start, cutoff) in &cuts {
            params.push(date);
            params.push(start);
            params.push(cutoff);
        }

        // 批量活动时长（LEFT JOIN：零活动日期仍返回该行并 COALESCE 为 0）。
        let mut active_ms: HashMap<String, i64> = HashMap::new();
        {
            let sql = format!(
                "WITH cuts(local_date, day_start, cutoff) AS (VALUES {values_sql})
                 SELECT c.local_date,
                        COALESCE(SUM(CASE WHEN s.activity_state = 'active'
                                      THEN MIN(s.end_at_utc_ms, c.cutoff)
                                         - MAX(s.start_at_utc_ms, c.day_start)
                                      ELSE 0 END), 0) AS active_duration_ms
                 FROM cuts c
                 LEFT JOIN activity_segments s
                   ON s.end_at_utc_ms > c.day_start AND s.start_at_utc_ms < c.cutoff
                   AND s.end_at_utc_ms > s.start_at_utc_ms
                 GROUP BY c.local_date
                 ORDER BY c.local_date"
            );
            let mut stmt = self.tx.prepare(&sql)?;
            let iter = stmt.query_map(params.as_slice(), |row| {
                Ok((row.get::<_, String>(0)?, row.get::<_, i64>(1)?))
            })?;
            for row in iter {
                let (date, ms) = row?;
                active_ms.insert(date, ms);
            }
        }

        // 批量工作块数（与 recompute_dates 同一口径：块与 [day_start, cutoff] 相交
        // 且 day_active > 0 才计 1，含当前未闭合块）。
        let mut block_counts: HashMap<String, i64> = HashMap::new();
        {
            let sql = format!(
                "WITH cuts(local_date, day_start, cutoff) AS (VALUES {values_sql})
                 SELECT c.local_date, w.work_block_id,
                        COALESCE(SUM(MIN(s.end_at_utc_ms, c.cutoff)
                                     - MAX(s.start_at_utc_ms, c.day_start)), 0) AS day_active
                 FROM cuts c
                 JOIN work_blocks w
                   ON w.end_at_utc_ms > c.day_start AND w.start_at_utc_ms < c.cutoff
                 LEFT JOIN activity_segments s
                   ON s.start_at_utc_ms >= w.start_at_utc_ms
                  AND s.end_at_utc_ms <= w.end_at_utc_ms
                  AND s.activity_state = 'active'
                  AND s.end_at_utc_ms > c.day_start
                  AND s.start_at_utc_ms < c.cutoff
                 GROUP BY c.local_date, w.work_block_id"
            );
            let mut stmt = self.tx.prepare(&sql)?;
            let iter = stmt.query_map(params.as_slice(), |row| {
                Ok((row.get::<_, String>(0)?, row.get::<_, i64>(2)?))
            })?;
            for row in iter {
                let (date, day_active) = row?;
                if day_active > 0 {
                    *block_counts.entry(date).or_insert(0) += 1;
                }
            }
        }

        // 按原始输入顺序映射（含重复日期）：同一日期返回同一正确值——不翻倍、不归零。
        Ok(dates
            .iter()
            .map(|date| DayAtCutoff {
                local_date: date.as_str().to_string(),
                active_duration_ms: active_ms.get(date.as_str()).copied().unwrap_or(0),
                work_block_count: block_counts.get(date.as_str()).copied().unwrap_or(0),
            })
            .collect())
    }

    /// 周期内应用总量（slot 排名输入）：身份解析与 `reader.today()` 相同的
    /// `JOIN app_identities` + `AppDto` 路径，按 SUM DESC、app_id ASC 排序（tie-break）。
    pub fn stats_app_totals(&self, start: &LocalDate, end: &LocalDate) -> Result<Vec<AppTotalRow>> {
        let mut stmt = self.tx.prepare(
            "SELECT u.app_id, a.display_name, SUM(u.active_duration_ms)
             FROM daily_app_usage u
             JOIN app_identities a ON a.app_id = u.app_id
             WHERE u.local_date BETWEEN ?1 AND ?2
             GROUP BY u.app_id
             ORDER BY SUM(u.active_duration_ms) DESC, u.app_id ASC",
        )?;
        let iter = stmt.query_map(params![start.as_str(), end.as_str()], |row| {
            Ok(AppTotalRow {
                app_id: row.get(0)?,
                display_name: row.get(1)?,
                total_active_ms: row.get(2)?,
            })
        })?;
        let mut out = Vec::new();
        for row in iter {
            out.push(row?);
        }
        Ok(out)
    }

    /// 逐日逐应用行（日/周桶聚合输入）：按 (local_date, app_id) 升序。
    pub fn stats_app_rows(&self, start: &LocalDate, end: &LocalDate) -> Result<Vec<AppDayRow>> {
        let mut stmt = self.tx.prepare(
            "SELECT u.local_date, u.app_id, a.display_name, u.active_duration_ms
             FROM daily_app_usage u
             JOIN app_identities a ON a.app_id = u.app_id
             WHERE u.local_date BETWEEN ?1 AND ?2
             ORDER BY u.local_date ASC, u.app_id ASC",
        )?;
        let iter = stmt.query_map(params![start.as_str(), end.as_str()], |row| {
            Ok(AppDayRow {
                local_date: row.get(0)?,
                app_id: row.get(1)?,
                display_name: row.get(2)?,
                active_ms: row.get(3)?,
            })
        })?;
        let mut out = Vec::new();
        for row in iter {
            out.push(row?);
        }
        Ok(out)
    }

    /// 惯性窗口小时均值（11 阶段二 2.3 + 阶段零 P0-4）：**有效日来自
    /// `daily_work_metrics`**（存在当日工作统计投影的日期），小时总量来自
    /// `hourly_app_usage`；为每个有效日建立 24 个零值、覆盖已有小时数据，统一除以
    /// `effectiveDays` 求平均。某日有工作统计投影但小时表无行（零活动/投影边界）时
    /// 该日必须计入分母。返回 `([每小时均值; 24], 有效日数)`。
    pub fn stats_hourly_profile(
        &self,
        start: &LocalDate,
        end: &LocalDate,
    ) -> Result<([i64; 24], u32)> {
        let mut effective: HashSet<String> = HashSet::new();
        {
            let mut stmt = self.tx.prepare(
                "SELECT local_date FROM daily_work_metrics
                 WHERE local_date >= ?1 AND local_date <= ?2",
            )?;
            let iter = stmt.query_map(params![start.as_str(), end.as_str()], |row| {
                row.get::<_, String>(0)
            })?;
            for row in iter {
                effective.insert(row?);
            }
        }
        let mut per_hour: [i64; 24] = [0; 24];
        let effective_days = effective.len() as u32;
        if effective_days > 0 {
            let mut stmt = self.tx.prepare(
                "SELECT local_date, local_hour, SUM(active_duration_ms)
                 FROM hourly_app_usage
                 WHERE local_date >= ?1 AND local_date <= ?2
                 GROUP BY local_date, local_hour",
            )?;
            let iter = stmt.query_map(params![start.as_str(), end.as_str()], |row| {
                Ok((
                    row.get::<_, String>(0)?,
                    row.get::<_, u32>(1)?,
                    row.get::<_, i64>(2)?,
                ))
            })?;
            for row in iter {
                let (date, hour, ms) = row?;
                // 只累计有效日的贡献；有效日缺失的小时保持 0（统一分母）。
                if effective.contains(&date) && hour < 24 {
                    per_hour[hour as usize] = per_hour[hour as usize].saturating_add(ms);
                }
            }
            for value in per_hour.iter_mut() {
                *value /= i64::from(effective_days);
            }
        }
        Ok((per_hour, effective_days))
    }

    /// 工作节奏（v0.2 候选：惯性卡片融合）：窗口内每个有效日的 Work Block 覆盖
    /// 分钟段（0..1440 半开区间，按 reporting time zone 归位；跨午夜块裁剪到各
    /// 日）。有效日口径与 `stats_hourly_profile` 相同（daily_work_metrics 有行）。
    /// 含 open 块（今日进行中覆盖按当前 end_at 统计，随轮询更新，与状态轮询的
    /// "今日 provisional" 口径一致）。返回 (每日覆盖段（升序去重日期）, 有效日数)。
    pub fn stats_work_pace_days(
        &self,
        start: &LocalDate,
        end: &LocalDate,
    ) -> Result<(Vec<wuji_core::stats::DayCoverage>, u32)> {
        let tz = self.reporting_tz()?;
        let mut effective: HashSet<String> = HashSet::new();
        {
            let mut stmt = self.tx.prepare(
                "SELECT local_date FROM daily_work_metrics
                 WHERE local_date >= ?1 AND local_date <= ?2",
            )?;
            let iter = stmt.query_map(params![start.as_str(), end.as_str()], |row| {
                row.get::<_, String>(0)
            })?;
            for row in iter {
                effective.insert(row?);
            }
        }
        if effective.is_empty() {
            return Ok((Vec::new(), 0));
        }
        let (win_start, _) = local_day_range_utc_ms(&tz, start)?;
        let (_, win_end) = local_day_range_utc_ms(&tz, end)?;
        let mut by_date: BTreeMap<String, Vec<(u32, u32)>> = BTreeMap::new();
        {
            let mut stmt = self.tx.prepare(
                "SELECT start_at_utc_ms, end_at_utc_ms FROM work_blocks
                 WHERE end_at_utc_ms > ?1 AND start_at_utc_ms < ?2",
            )?;
            let iter = stmt.query_map(params![win_start, win_end], |row| {
                Ok((row.get::<_, i64>(0)?, row.get::<_, i64>(1)?))
            })?;
            for row in iter {
                let (block_start, block_end) = row?;
                let block_end = block_end.max(block_start);
                // 归属日规则（用户语义）：凌晨（本地 00:00-06:00）开始的块不可能是
                // "新一天开工"——一律是前一天熬夜的延续，整体归属前一天（即使中间
                // Agent 未运行而断开，也按熬夜归前日）。非凌晨块归属块开始的本地日，
                // 跨午夜延伸段允许 end > 1440（防御上限 2880 分钟）。
                let (start_date_raw, start_hour, _) = local_fields(&tz, block_start)?;
                let target_date = if start_hour < 6 {
                    let prev = NaiveDate::parse_from_str(&start_date_raw, "%Y-%m-%d")
                        .map_err(|_| {
                            StorageError::new(
                                wuji_core::error::SafeErrorCode::InternalSafeError,
                                "日期解析失败",
                            )
                        })?
                        .pred_opt()
                        .ok_or_else(|| {
                            StorageError::new(
                                wuji_core::error::SafeErrorCode::InternalSafeError,
                                "日期越界",
                            )
                        })?;
                    prev.format("%Y-%m-%d").to_string()
                } else {
                    start_date_raw
                };
                if !effective.contains(&target_date)
                    || target_date.as_str() < start.as_str()
                    || target_date.as_str() > end.as_str()
                {
                    continue;
                }
                let local = LocalDate::parse(&target_date)
                    .map_err(|e| StorageError::new(e.code, e.message))?;
                let (day_start, _day_end) = local_day_range_utc_ms(&tz, &local)?;
                let clip_start = block_start.max(day_start).max(win_start);
                let clip_end = block_end.min(win_end).max(clip_start);
                if clip_end > clip_start {
                    let sm = ((clip_start - day_start) / 60_000) as u32;
                    let em = (((clip_end - day_start) / 60_000) as u32).min(2880);
                    if em > sm {
                        by_date.entry(target_date).or_default().push((sm, em));
                    }
                }
            }
        }
        // 升序合并重叠/邻接段（防御：块理论不重叠，但裁剪后仍保证不变量）。
        let mut out: Vec<wuji_core::stats::DayCoverage> = Vec::new();
        for date in effective.iter() {
            let mut segs = by_date.get(date).cloned().unwrap_or_default();
            segs.sort_unstable();
            let mut merged: Vec<(u32, u32)> = Vec::new();
            for (s, e) in segs {
                if let Some(last) = merged.last_mut()
                    && s <= last.1
                {
                    last.1 = last.1.max(e);
                    continue;
                }
                merged.push((s, e));
            }
            out.push(wuji_core::stats::DayCoverage { segments: merged });
        }
        Ok((out, effective.len() as u32))
    }

    /// 全部有记录日期（升序去重；里程碑连续天数输入）。
    pub fn stats_recorded_dates(&self) -> Result<Vec<String>> {
        let mut stmt = self
            .tx
            .prepare("SELECT DISTINCT local_date FROM daily_work_metrics ORDER BY local_date")?;
        let iter = stmt.query_map([], |row| row.get::<_, String>(0))?;
        let mut out = Vec::new();
        for row in iter {
            out.push(row?);
        }
        Ok(out)
    }
}

fn naive_of(date: &LocalDate) -> Result<NaiveDate> {
    NaiveDate::parse_from_str(date.as_str(), "%Y-%m-%d").map_err(|_| {
        StorageError::new(
            SafeErrorCode::InvalidArgument,
            "日期必须使用 YYYY-MM-DD 格式",
        )
    })
}
