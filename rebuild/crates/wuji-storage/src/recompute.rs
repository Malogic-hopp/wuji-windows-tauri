//! Projection 触及桶重算（09 §7.3）：从来源 Segment/Work/gap/Observation 确定值重建，
//! 不做增量累加；没有来源的旧桶行同事务删除，保证幂等。

use chrono_tz::Tz;
use rusqlite::params;
use wuji_core::dto::LocalDate;

use crate::error::Result;
use crate::timeutil::{local_date_of, local_day_range_utc_ms, local_fields};
use crate::writer::{MAX_GAP_CAP_MS, StorageTransaction};

impl StorageTransaction<'_> {
    /// 重算指定 UTC 小时桶（09 §7.3；local 字段恒等于桶起点换算，09 §6.7）。
    pub fn recompute_hours(&self, tz: &Tz, utc_hour_starts_ms: &[i64]) -> Result<()> {
        for &hour in utc_hour_starts_ms {
            let (local_date, local_hour, offset) = local_fields(tz, hour)?;
            let next = hour + 3_600_000;
            self.raw_tx().execute(
                "DELETE FROM hourly_app_usage WHERE utc_hour_start_ms = ?1",
                params![hour],
            )?;
            self.raw_tx().execute(
                "INSERT INTO hourly_app_usage
                 (utc_hour_start_ms, local_date, local_hour, local_utc_offset_minutes,
                  app_id, active_duration_ms, idle_duration_ms, unknown_duration_ms, segment_count)
                 SELECT ?1, ?2, ?3, ?4, app_id,
                   SUM(CASE WHEN activity_state = 'active'
                            THEN MIN(end_at_utc_ms, ?6) - MAX(start_at_utc_ms, ?5) ELSE 0 END),
                   SUM(CASE WHEN activity_state = 'idle'
                            THEN MIN(end_at_utc_ms, ?6) - MAX(start_at_utc_ms, ?5) ELSE 0 END),
                   SUM(CASE WHEN activity_state = 'unknown'
                            THEN MIN(end_at_utc_ms, ?6) - MAX(start_at_utc_ms, ?5) ELSE 0 END),
                   COUNT(*)
                 FROM activity_segments
                 WHERE end_at_utc_ms > ?5 AND start_at_utc_ms < ?6
                 GROUP BY app_id",
                params![hour, local_date, local_hour as i64, offset, hour, next],
            )?;
        }
        Ok(())
    }

    /// 重算指定 local date 的 daily 读模型（09 §7.3 与计数口径）。
    /// `gap_cap_ms` 为当前派生 gap cap（raw switch 判定只按当前值；历史不重放旧设置）。
    pub fn recompute_dates(&self, tz: &Tz, dates: &[LocalDate], gap_cap_ms: i64) -> Result<()> {
        for date in dates {
            let (day_start, day_end) = local_day_range_utc_ms(tz, date)?;
            let date_str = date.as_str().to_string();

            self.raw_tx().execute(
                "DELETE FROM daily_app_usage WHERE local_date = ?1",
                params![date_str],
            )?;
            self.raw_tx().execute(
                "DELETE FROM daily_work_metrics WHERE local_date = ?1",
                params![date_str],
            )?;

            self.raw_tx().execute(
                "INSERT INTO daily_app_usage
                 (local_date, app_id, active_duration_ms, idle_duration_ms,
                  unknown_duration_ms, segment_count)
                 SELECT ?1, app_id,
                   SUM(CASE WHEN activity_state = 'active'
                            THEN MIN(end_at_utc_ms, ?3) - MAX(start_at_utc_ms, ?2) ELSE 0 END),
                   SUM(CASE WHEN activity_state = 'idle'
                            THEN MIN(end_at_utc_ms, ?3) - MAX(start_at_utc_ms, ?2) ELSE 0 END),
                   SUM(CASE WHEN activity_state = 'unknown'
                            THEN MIN(end_at_utc_ms, ?3) - MAX(start_at_utc_ms, ?2) ELSE 0 END),
                   COUNT(*)
                 FROM activity_segments
                 WHERE end_at_utc_ms > ?2 AND start_at_utc_ms < ?3
                 GROUP BY app_id",
                params![date_str, day_start, day_end],
            )?;

            let mut work_stmt = self.raw_tx().prepare(
                "SELECT activity_state,
                        SUM(MIN(end_at_utc_ms, ?2) - MAX(start_at_utc_ms, ?1))
                 FROM activity_segments s
                 WHERE s.end_at_utc_ms > ?1 AND s.start_at_utc_ms < ?2
                   AND EXISTS (
                     SELECT 1 FROM work_blocks w
                     WHERE w.start_at_utc_ms <= s.start_at_utc_ms
                       AND w.end_at_utc_ms >= s.end_at_utc_ms)
                 GROUP BY activity_state",
            )?;
            let mut work_active = 0_i64;
            let mut work_short_idle = 0_i64;
            let mut rows = work_stmt.query(params![day_start, day_end])?;
            while let Some(row) = rows.next()? {
                let state: String = row.get(0)?;
                let sum: i64 = row.get::<_, Option<i64>>(1)?.unwrap_or(0);
                match state.as_str() {
                    "active" => work_active = sum,
                    "idle" => work_short_idle = sum,
                    _ => {}
                }
            }

            let mut block_stmt = self.raw_tx().prepare(
                "SELECT w.work_block_id,
                        COALESCE(SUM(MIN(s.end_at_utc_ms, ?2) - MAX(s.start_at_utc_ms, ?1)), 0)
                 FROM work_blocks w
                 LEFT JOIN activity_segments s
                   ON s.start_at_utc_ms >= w.start_at_utc_ms
                  AND s.end_at_utc_ms <= w.end_at_utc_ms
                  AND s.activity_state = 'active'
                 WHERE w.end_at_utc_ms > ?1 AND w.start_at_utc_ms < ?2
                 GROUP BY w.work_block_id",
            )?;
            let mut block_count = 0_i64;
            let mut longest = 0_i64;
            let mut block_rows = block_stmt.query(params![day_start, day_end])?;
            while let Some(row) = block_rows.next()? {
                let day_active: i64 = row.get(1)?;
                if day_active > 0 {
                    block_count += 1;
                    longest = longest.max(day_active);
                }
            }

            // 会打断 switch 计数的 gap（sampling_transition 除外，它是切换的正常归属标记）。
            let mut gap_stmt = self.raw_tx().prepare(
                "SELECT kind, start_at_utc_ms, end_at_utc_ms
                 FROM capture_gaps
                 WHERE start_at_utc_ms < ?2
                   AND (end_at_utc_ms IS NULL OR end_at_utc_ms > ?1)",
            )?;
            let gap_rows =
                gap_stmt.query_map(params![day_start - MAX_GAP_CAP_MS, day_end], |row| {
                    Ok((
                        row.get::<_, String>(0)?,
                        row.get::<_, i64>(1)?,
                        row.get::<_, Option<i64>>(2)?,
                    ))
                })?;
            let mut blocking_gaps: Vec<(i64, i64)> = Vec::new();
            let mut data_gap_count = 0_i64;
            for gap in gap_rows {
                let (kind, start, end) = gap?;
                if kind == "sampling_transition" {
                    continue;
                }
                blocking_gaps.push((start, end.unwrap_or(i64::MAX)));
                if local_date_of(tz, start)? == date_str {
                    data_gap_count += 1;
                }
            }

            // raw_app_switch_count：09 §6.6 定义 + 边界对只按后一条 Observation 的日期计一次。
            let mut obs_stmt = self.raw_tx().prepare(
                "SELECT runtime_id, continuity_epoch, captured_at_utc_ms, app_id, activity_state
                 FROM foreground_observations
                 WHERE captured_at_utc_ms >= ?1 AND captured_at_utc_ms < ?2
                 ORDER BY runtime_id, capture_sequence",
            )?;
            let obs_rows =
                obs_stmt.query_map(params![day_start - MAX_GAP_CAP_MS, day_end], |row| {
                    Ok((
                        row.get::<_, String>(0)?,
                        row.get::<_, i64>(1)?,
                        row.get::<_, i64>(2)?,
                        row.get::<_, i64>(3)?,
                        row.get::<_, String>(4)?,
                    ))
                })?;
            let mut raw_switch_count = 0_i64;
            let mut previous: Option<(String, i64, i64, i64, String)> = None;
            for obs in obs_rows {
                let (runtime, epoch, captured, app, state) = obs?;
                if let Some((prev_rt, prev_epoch, prev_captured, prev_app, prev_state)) = &previous
                {
                    let delta = captured - prev_captured;
                    let spans_blocking_gap = blocking_gaps
                        .iter()
                        .any(|(gs, ge)| *gs < captured && *ge > *prev_captured);
                    if prev_rt == &runtime
                        && prev_epoch == &epoch
                        && prev_app != &app
                        && prev_state != "unknown"
                        && state != "unknown"
                        && delta > 0
                        && delta <= gap_cap_ms
                        && !spans_blocking_gap
                        && local_date_of(tz, captured)? == date_str
                    {
                        raw_switch_count += 1;
                    }
                }
                previous = Some((runtime, epoch, captured, app, state));
            }

            if work_active > 0
                || work_short_idle > 0
                || block_count > 0
                || raw_switch_count > 0
                || data_gap_count > 0
            {
                self.raw_tx().execute(
                    "INSERT INTO daily_work_metrics
                     (local_date, active_duration_ms, short_idle_duration_ms, work_block_count,
                      longest_work_block_active_ms, raw_app_switch_count, data_gap_count)
                     VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7)",
                    params![
                        date_str,
                        work_active,
                        work_short_idle,
                        block_count,
                        longest,
                        raw_switch_count,
                        data_gap_count,
                    ],
                )?;
            }
        }
        Ok(())
    }
}
