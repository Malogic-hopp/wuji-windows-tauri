//! 只读查询服务：Today/Timeline（09 §7.3、§8.4）。
//!
//! 每次调用以 read-only + query_only 打开数据库（短生命周期 reader，04 §13）。

use std::path::PathBuf;

use chrono::{Datelike, Days, Months, NaiveDate};
use wuji_core::dto::{
    AppPaletteEntryDto, CompositionBucketDto, HeatmapDto, HourlyPointDto, InertiaDto, Int64String,
    LiveStatusDto, LocalDate, MilestoneDto, MonthlyPointDto, SameTimeComparisonDto, StatsHomeDto,
    StatsStatusDto, StatusDto, SummaryDto, TimelineCursor, TimelinePageDto, TodayDto,
    TrendPointDto, WeekProgressDto, WeeklyPointDto, WorkPaceDto,
};
use wuji_core::error::{SafeError, SafeErrorCode};
use wuji_core::stats::{
    ComparisonPolicy, DailyMetricSample, build_summary, compare_direction, compute_moving_avg7,
    derive_inertia, derive_work_pace, longest_consecutive, normalize_days, summary_direction,
};
use wuji_storage::Reader;
use wuji_storage::error::StorageError;
use wuji_storage::timeutil::{local_date_of, now_utc_ms};
use wuji_storage::{DayMetric, ReaderSnapshot};

use crate::paths;
use crate::stats_assembly::{allocate_slots, bucketize_composition};

pub struct QueryService {
    database: PathBuf,
}

impl QueryService {
    pub fn new(channel: &str) -> Result<Self, String> {
        Ok(Self {
            database: paths::data_root(channel)?
                .join("data")
                .join("wuji-rebuild-v0.1.db"),
        })
    }

    pub fn database_path(&self) -> &PathBuf {
        &self.database
    }

    fn open_reader(&self) -> Result<Reader, SafeError> {
        Reader::open(&self.database).map_err(|error| error.to_safe_error())
    }

    fn storage_error(error: StorageError) -> SafeError {
        error.to_safe_error()
    }

    /// `activity_get_today`：以 DB reporting time zone 的当前 local date 查询（09 §8.4）。
    pub fn today(&self) -> Result<TodayDto, SafeError> {
        let reader = self.open_reader()?;
        let tz = reader
            .schema_meta()
            .reporting_tz()
            .map_err(Self::storage_error)?;
        let date_text = local_date_of(&tz, now_utc_ms()).map_err(Self::storage_error)?;
        let date = LocalDate::parse(&date_text)
            .map_err(|_| SafeError::new(SafeErrorCode::InternalSafeError, "本地日期解析失败"))?;
        reader.today(&date).map_err(Self::storage_error)
    }

    /// `activity_get_timeline`：单 local day + cursor 分页（09 §8.4）。
    pub fn timeline(
        &self,
        local_date: &str,
        cursor: Option<String>,
        limit: Option<u32>,
    ) -> Result<TimelinePageDto, SafeError> {
        let date = LocalDate::parse(local_date)?;
        let limit = limit.unwrap_or(200);
        if limit == 0 || limit > 500 {
            return Err(SafeError::new(
                SafeErrorCode::InvalidArgument,
                "limit 必须在 1 到 500 之间",
            ));
        }
        let cursor = match cursor {
            Some(raw) => Some(TimelineCursor::decode(&raw)?),
            None => None,
        };
        let reader = self.open_reader()?;
        reader
            .timeline(&date, cursor, limit)
            .map_err(Self::storage_error)
    }

    /// `activity_get_heatmap`：最近 days 天 × 24 小时聚合（09 §8.4）。
    /// `week_offset` 按整周平移锚点：0（默认）为本周，-1 为上周，依此类推。
    pub fn heatmap(
        &self,
        days: Option<u32>,
        week_offset: Option<i32>,
    ) -> Result<HeatmapDto, SafeError> {
        let days = days.unwrap_or(7);
        if days == 0 || days > 31 {
            return Err(SafeError::new(
                SafeErrorCode::InvalidArgument,
                "days 必须在 1 到 31 之间",
            ));
        }
        let week_offset = week_offset.unwrap_or(0);
        if !(-520..=0).contains(&week_offset) {
            return Err(SafeError::new(
                SafeErrorCode::InvalidArgument,
                "week_offset 必须在 -520 到 0 之间",
            ));
        }
        let reader = self.open_reader()?;
        let tz = reader
            .schema_meta()
            .reporting_tz()
            .map_err(Self::storage_error)?;
        let date_text = local_date_of(&tz, now_utc_ms()).map_err(Self::storage_error)?;
        let date = LocalDate::parse(&date_text)
            .map_err(|_| SafeError::new(SafeErrorCode::InternalSafeError, "本地日期解析失败"))?;
        reader
            .heatmap(&date, days, week_offset)
            .map_err(Self::storage_error)
    }

    /// Agent 离线时的最后已知 runtime 快照（09 §10.5：不得单独证明 Running）。
    pub fn latest_runtime(&self) -> Result<Option<wuji_storage::RuntimeRow>, SafeError> {
        let reader = self.open_reader()?;
        reader.latest_runtime().map_err(Self::storage_error)
    }

    /// 已应用 settings revision（Writer 提交记录的最大 revision；无记录时为 0）。
    pub fn applied_settings_revision(&self) -> Result<String, SafeError> {
        let reader = self.open_reader()?;
        let max_revision = reader
            .max_settings_revision()
            .map_err(Self::storage_error)?;
        Ok(max_revision.unwrap_or(0).to_string())
    }

    /// 数据库是否可读（Diagnostics）。
    pub fn database_reachable(&self) -> bool {
        Reader::open(&self.database).is_ok()
    }

    /// `stats_get_home`（11 实施方案阶段三 3.1）：全量统计主页，一次命令一个
    /// `ReaderSnapshot` 读事务快照；统一 daily 超集保证摘要窗口与 `days` 无关。
    pub fn stats_home(&self, days: Option<i32>) -> Result<StatsHomeDto, SafeError> {
        self.stats_home_at(days, now_utc_ms())
    }

    /// 以指定 UTC 时刻为基准的全量查询（生产走 `stats_home`；阶段三测试注入固定时钟，
    /// 消除真实时钟依赖与跨午夜错位）。命令级单批次 cutoff（P1-1）。
    pub fn stats_home_at(&self, days: Option<i32>, now: i64) -> Result<StatsHomeDto, SafeError> {
        self.stats_home_at_observed(days, now).map(|(dto, _)| dto)
    }

    /// 与本次 DTO 原子返回同一快照的最终 cutoff 批次计数（阶段三回归门禁；测试用）。
    /// 计数在全部子查询完成后读取，不使用跨命令共享状态，避免并发命令互相覆盖。
    #[doc(hidden)]
    pub fn stats_home_at_observed(
        &self,
        days: Option<i32>,
        now: i64,
    ) -> Result<(StatsHomeDto, u32), SafeError> {
        let days = normalize_days(days);
        let mut reader = self.open_reader()?;
        let tz = reader
            .schema_meta()
            .reporting_tz()
            .map_err(Self::storage_error)?;
        let tz_id = reader.schema_meta().reporting_time_zone_id.clone();
        let date_text = local_date_of(&tz, now).map_err(Self::storage_error)?;
        let today = LocalDate::parse(&date_text).map_err(|e| SafeError::new(e.code, e.message))?;
        let result = reader.with_snapshot(|snapshot| {
            let ctx = StatsQueryContext {
                snapshot,
                now_utc_ms: now,
                local_date: today.clone(),
                reporting_tz: tz,
            };
            // 命令级单批次 cutoff：live / 周进度 / 月度共用同一索引。
            let plan = build_cutoff_plan(&ctx)?;
            let live = stats_live_status_dto(&ctx, &plan)?;
            // 统一 daily 超集：read_days = max(days + 6, 15)（含今日），趋势均线
            // lookback 与摘要窗口 [today-14, today-1] 全部落在超集内。
            let read_days = std::cmp::max(days + 6, 15);
            let start_naive = naive_of(&today)? - Days::new(u64::from(read_days - 1));
            let daily = ctx
                .snapshot
                .stats_daily_rows(&local_of(start_naive)?, &ctx.local_date)?;
            let mut trend = stats_trend(&ctx, days, &daily)?;
            // 今日趋势柱以 live cutoff 覆盖（与状态轮询同口径，避免投影时点差）。
            if let Some(point) = trend.iter_mut().find(|p| p.is_today) {
                point.active_duration_ms = live.today_active_ms;
                point.work_block_count = live.work_block_count;
            }
            let (weekly, week_progress) = stats_weekly(&ctx, &plan)?;
            let (composition, palette) = stats_composition(&ctx, days, &daily)?;
            let (hourly_profile, inertia) = stats_hourly_profile(&ctx)?;
            let work_pace = stats_work_pace(&ctx)?;
            let (monthly, milestone) = stats_monthly(&ctx, &plan)?;
            let summary = build_summary_from_daily(&ctx, &daily, &inertia)?;
            let dto = StatsHomeDto {
                has_any_data: milestone.total_recorded_days.0 > 0,
                local_date: today.clone(),
                reporting_time_zone_id: tz_id.clone(),
                status: StatusDto {
                    today_active_ms: live.today_active_ms,
                    work_block_count: live.work_block_count,
                    cutoff_local_time: live.cutoff_local_time.clone(),
                    yesterday_same: live.yesterday_same.clone(),
                    last7_avg_same: live.last7_avg_same.clone(),
                    summary,
                },
                trend,
                weekly,
                week_progress,
                composition,
                palette,
                hourly_profile,
                inertia,
                work_pace,
                milestone,
                monthly,
            };
            // 必须是闭包返回前的最后一次快照观测：后续任何辅助函数重新调用 cutoff
            // 都会反映在与本次 DTO 绑定的计数中。
            Ok((dto, snapshot.stats_cutoff_series_calls()))
        });
        result.map_err(Self::storage_error)
    }

    /// `stats_get_status`（11 实施方案阶段三 3.1）：轻量轮询，只走 cutoff 批次与
    /// 本周/上周同期查询，不触发趋势/惯性/月度/里程碑；响应含报告时区 localDate。
    pub fn stats_status(&self) -> Result<StatsStatusDto, SafeError> {
        self.stats_status_at(now_utc_ms())
    }

    /// 以指定 UTC 时刻为基准的轻量轮询（阶段三测试注入固定时钟）。
    pub fn stats_status_at(&self, now: i64) -> Result<StatsStatusDto, SafeError> {
        self.stats_status_at_observed(now).map(|(dto, _)| dto)
    }

    /// 与本次 DTO 原子返回同一快照的最终 cutoff 批次计数（阶段三回归门禁；测试用）。
    #[doc(hidden)]
    pub fn stats_status_at_observed(&self, now: i64) -> Result<(StatsStatusDto, u32), SafeError> {
        let mut reader = self.open_reader()?;
        let tz = reader
            .schema_meta()
            .reporting_tz()
            .map_err(Self::storage_error)?;
        let tz_id = reader.schema_meta().reporting_time_zone_id.clone();
        let date_text = local_date_of(&tz, now).map_err(Self::storage_error)?;
        let today = LocalDate::parse(&date_text).map_err(|e| SafeError::new(e.code, e.message))?;
        let result = reader.with_snapshot(|snapshot| {
            let ctx = StatsQueryContext {
                snapshot,
                now_utc_ms: now,
                local_date: today.clone(),
                reporting_tz: tz,
            };
            // 单次 cutoff 批次供 live / weekProgress / todayTrendPoint 共用（P1-1）。
            let plan = build_cutoff_plan(&ctx)?;
            let live_status = stats_live_status_dto(&ctx, &plan)?;
            let week_progress = week_progress_dto(&ctx, &plan)?;
            let today_trend_point = stats_today_trend_point(&ctx, &plan)?;
            let dto = StatsStatusDto {
                local_date: today.clone(),
                reporting_time_zone_id: tz_id.clone(),
                live_status,
                week_progress,
                today_trend_point,
            };
            Ok((dto, snapshot.stats_cutoff_series_calls()))
        });
        result.map_err(Self::storage_error)
    }
}

// ===== 统计主页组装（11 实施方案阶段三 3.1）=====

/// 命令级查询上下文：一个 `ReaderSnapshot` 读事务快照 + 单次 now 基准，保证
/// 同一命令的全部区块来自同一数据库视图（阶段零 Q-1）。
struct StatsQueryContext<'a> {
    snapshot: &'a ReaderSnapshot<'a>,
    now_utc_ms: i64,
    local_date: LocalDate,
    reporting_tz: chrono_tz::Tz,
}

fn naive_of(date: &LocalDate) -> Result<NaiveDate, StorageError> {
    NaiveDate::parse_from_str(date.as_str(), "%Y-%m-%d").map_err(|_| {
        StorageError::new(
            SafeErrorCode::InvalidArgument,
            "日期必须使用 YYYY-MM-DD 格式",
        )
    })
}

fn local_of(naive: NaiveDate) -> Result<LocalDate, StorageError> {
    LocalDate::parse(&naive.format("%Y-%m-%d").to_string())
        .map_err(|e| StorageError::new(e.code, e.message))
}

/// 截止时刻（本地 HH:MM，仅供展示；换算精度在 timeutil 保留秒/毫秒）。
fn cutoff_local_time(tz: &chrono_tz::Tz, now_utc_ms: i64) -> String {
    chrono::DateTime::from_timestamp_millis(now_utc_ms)
        .map(|dt| dt.with_timezone(tz).format("%H:%M").to_string())
        .unwrap_or_default()
}

/// 命令级 cutoff 批次索引（P1-1）：每个命令只调用一次 `stats_cutoff_series`，
/// 活动/工作块按日期建索引，live、周进度、月度、今日趋势点共用。
struct CutoffIndex {
    active: std::collections::HashMap<String, i64>,
    blocks: std::collections::HashMap<String, i64>,
}

impl CutoffIndex {
    /// 缺键即内部错误（0 是合法业务值，不得静默伪装成零活动）。
    fn active_of(&self, date: &LocalDate) -> Result<i64, StorageError> {
        self.active
            .get(date.as_str())
            .copied()
            .ok_or_else(|| StorageError::internal(format!("cutoff 批次缺少日期 {}", date.as_str())))
    }

    fn blocks_of(&self, date: &LocalDate) -> Result<i64, StorageError> {
        self.blocks
            .get(date.as_str())
            .copied()
            .ok_or_else(|| StorageError::internal(format!("cutoff 批次缺少日期 {}", date.as_str())))
    }
}

/// 命令级 cutoff 计划：统一收集日期集合（今日/昨日/近 7 有效日/上周同周序日）
/// + 单批次索引；重复日期由 Reader 侧去重（阶段二 P1 回归）。
struct CutoffPlan {
    yesterday: LocalDate,
    recent: Vec<LocalDate>,
    last_same: LocalDate,
    index: CutoffIndex,
}

fn build_cutoff_plan(ctx: &StatsQueryContext) -> Result<CutoffPlan, StorageError> {
    let yesterday = local_of(naive_of(&ctx.local_date)? - Days::new(1))?;
    let recent = ctx.snapshot.recent_recorded_dates(&ctx.local_date, 7)?;
    let today_naive = naive_of(&ctx.local_date)?;
    let wd = u64::from(today_naive.weekday().num_days_from_monday());
    let monday = today_naive - Days::new(wd);
    let last_same = local_of(monday - Days::new(7) + Days::new(wd))?;
    let mut dates = vec![ctx.local_date.clone(), yesterday.clone()];
    dates.extend(recent.iter().cloned());
    dates.push(last_same.clone());
    // 单次批量查询（活动 + 工作块两条 SQL；不逐日 N+1）。
    let series = ctx.snapshot.stats_cutoff_series(
        &ctx.reporting_tz,
        &ctx.local_date,
        ctx.now_utc_ms,
        &dates,
    )?;
    let mut active = std::collections::HashMap::new();
    let mut blocks = std::collections::HashMap::new();
    for row in series {
        active.insert(row.local_date.clone(), row.active_duration_ms);
        blocks.insert(row.local_date, row.work_block_count);
    }
    Ok(CutoffPlan {
        yesterday,
        recent,
        last_same,
        index: CutoffIndex { active, blocks },
    })
}

/// 实时状态（10 §4.1 + 11 阶段零 P0-1）：今日/昨日/近 7 有效日同时刻比较，不含摘要。
fn stats_live_status_dto(
    ctx: &StatsQueryContext,
    plan: &CutoffPlan,
) -> Result<LiveStatusDto, StorageError> {
    let today_active = plan.index.active_of(&ctx.local_date)?;
    let today_blocks = plan.index.blocks_of(&ctx.local_date)?;
    let yesterday_active = plan.index.active_of(&plan.yesterday)?;
    // 近 7 有效日（含昨日——阶段三组装自然产生重复日期，批次已去重）。
    let yesterday_recorded = plan.recent.iter().any(|d| d == &plan.yesterday);
    let mut recent_actives = Vec::with_capacity(plan.recent.len());
    for d in &plan.recent {
        recent_actives.push(plan.index.active_of(d)?);
    }

    let (y_dir, y_delta, y_reason) = compare_direction(
        today_active,
        if yesterday_recorded {
            Some(yesterday_active)
        } else {
            None
        },
        if yesterday_recorded { 1 } else { 0 },
        ComparisonPolicy::DirectBaseline,
    );
    let yesterday_same = SameTimeComparisonDto {
        active_duration_ms: if yesterday_recorded {
            Some(Int64String(yesterday_active))
        } else {
            None
        },
        delta_percent: y_delta,
        direction: y_dir,
        sample_days: if yesterday_recorded { 1 } else { 0 },
        unavailable_reason: y_reason,
    };

    let sample_days = plan.recent.len() as i32;
    let baseline = if sample_days >= 3 {
        Some(recent_actives.iter().sum::<i64>() / i64::from(sample_days))
    } else {
        None
    };
    let (l7_dir, l7_delta, l7_reason) = compare_direction(
        today_active,
        baseline,
        sample_days,
        ComparisonPolicy::HistoricalAverage { min_samples: 3 },
    );
    let last7_avg_same = SameTimeComparisonDto {
        active_duration_ms: baseline.map(Int64String),
        delta_percent: l7_delta,
        direction: l7_dir,
        sample_days,
        unavailable_reason: l7_reason,
    };

    Ok(LiveStatusDto {
        today_active_ms: Int64String(today_active),
        work_block_count: Int64String(today_blocks),
        cutoff_local_time: cutoff_local_time(&ctx.reporting_tz, ctx.now_utc_ms),
        yesterday_same,
        last7_avg_same,
    })
}

/// 趋势（11 阶段三 3.1）：从统一超集取尾部 days 个点，均线用超集内 lookback。
fn stats_trend(
    ctx: &StatsQueryContext,
    days: u32,
    daily: &[DayMetric],
) -> Result<Vec<TrendPointDto>, StorageError> {
    let today_str = ctx.local_date.as_str().to_string();
    let samples: Vec<DailyMetricSample> = daily
        .iter()
        .map(|d| DailyMetricSample {
            active_duration_ms: d.active_duration_ms,
            has_data: d.has_data,
            is_today: d.local_date == today_str,
        })
        .collect();
    let start = daily.len().saturating_sub(days as usize);
    let mut out = Vec::with_capacity(days as usize);
    for (offset, day) in daily.iter().enumerate().skip(start) {
        let (ma, ma_days) = compute_moving_avg7(&samples, offset);
        out.push(TrendPointDto {
            local_date: LocalDate::parse(&day.local_date)
                .map_err(|e| StorageError::new(e.code, e.message))?,
            active_duration_ms: Int64String(day.active_duration_ms),
            work_block_count: Int64String(day.work_block_count),
            has_data: day.has_data,
            is_today: day.local_date == today_str,
            moving_avg7_active_ms: ma.map(Int64String),
            moving_avg7_sample_days: ma_days,
        });
    }
    Ok(out)
}

/// 本周截至当前（完整日前缀 + 今日 cutoff；11 阶段三 3.1 周进度公式）。
struct CurrentWeekStats {
    total_ms: i64,
    recorded_days: i32,
    completed_days: i32,
    completed_sum_ms: i64,
}

/// 上周同期（上周一..上周同周序日-1 完整 + 上周同周序日 cutoff）。
struct LastWeekSameStats {
    total_ms: i64,
    has_data: bool,
}

fn current_week_stats(
    ctx: &StatsQueryContext,
    plan: &CutoffPlan,
) -> Result<CurrentWeekStats, StorageError> {
    let today_naive = naive_of(&ctx.local_date)?;
    let wd = u64::from(today_naive.weekday().num_days_from_monday());
    let monday = today_naive - Days::new(wd);
    let daily = ctx
        .snapshot
        .stats_daily_rows(&local_of(monday)?, &ctx.local_date)?;
    let today_cutoff = plan.index.active_of(&ctx.local_date)?;
    let today_str = ctx.local_date.as_str().to_string();
    let mut total = 0_i64;
    let mut recorded = 0_i32;
    let mut completed_days = 0_i32;
    let mut completed_sum = 0_i64;
    for day in &daily {
        if day.local_date == today_str {
            if day.has_data {
                recorded += 1;
            }
            continue;
        }
        total += day.active_duration_ms;
        if day.has_data {
            recorded += 1;
            completed_days += 1;
            completed_sum += day.active_duration_ms;
        }
    }
    total += today_cutoff;
    Ok(CurrentWeekStats {
        total_ms: total,
        recorded_days: recorded,
        completed_days,
        completed_sum_ms: completed_sum,
    })
}

fn last_week_same_stats(
    ctx: &StatsQueryContext,
    plan: &CutoffPlan,
) -> Result<LastWeekSameStats, StorageError> {
    let today_naive = naive_of(&ctx.local_date)?;
    let wd = u64::from(today_naive.weekday().num_days_from_monday());
    let monday = today_naive - Days::new(wd);
    let last_monday = monday - Days::new(7);
    let last_sunday = last_monday + Days::new(6);
    // noData 判定扫描整个上周（Mon..Sun）；基线只累计 Mon..same-1 完整值 + same cutoff。
    let daily = ctx
        .snapshot
        .stats_daily_rows(&local_of(last_monday)?, &local_of(last_sunday)?)?;
    let same_cutoff = plan.index.active_of(&plan.last_same)?;
    let same_str = plan.last_same.as_str().to_string();
    let mut total = 0_i64;
    let mut has_data = false;
    for day in &daily {
        if day.has_data {
            has_data = true;
        }
        // 上周同周序日以 cutoff 为准；更晚周序日不计入同期基线。
        if day.local_date >= same_str {
            continue;
        }
        total += day.active_duration_ms;
    }
    total += same_cutoff;
    Ok(LastWeekSameStats {
        total_ms: total,
        has_data,
    })
}

fn build_week_progress(
    ctx: &StatsQueryContext,
    cws: &CurrentWeekStats,
    lws: &LastWeekSameStats,
) -> WeekProgressDto {
    let (direction, delta, reason) = compare_direction(
        cws.total_ms,
        if lws.has_data {
            Some(lws.total_ms)
        } else {
            None
        },
        1,
        ComparisonPolicy::DirectBaseline,
    );
    WeekProgressDto {
        current_active_ms: Int64String(cws.total_ms),
        last_week_same: SameTimeComparisonDto {
            active_duration_ms: if lws.has_data {
                Some(Int64String(lws.total_ms))
            } else {
                None
            },
            delta_percent: delta,
            direction,
            sample_days: 1,
            unavailable_reason: reason,
        },
        recorded_days: cws.recorded_days,
        cutoff_local_time: cutoff_local_time(&ctx.reporting_tz, ctx.now_utc_ms),
    }
}

/// `stats_status` 用：只算本周/上周同期，不读 12 周跨度（轻量）。
fn week_progress_dto(
    ctx: &StatsQueryContext,
    plan: &CutoffPlan,
) -> Result<WeekProgressDto, StorageError> {
    let cws = current_week_stats(ctx, plan)?;
    let lws = last_week_same_stats(ctx, plan)?;
    Ok(build_week_progress(ctx, &cws, &lws))
}

/// 12 ISO 周 + 本周进度（11 阶段三 3.1 stats_weekly）。
fn stats_weekly(
    ctx: &StatsQueryContext,
    plan: &CutoffPlan,
) -> Result<(Vec<WeeklyPointDto>, WeekProgressDto), StorageError> {
    let cws = current_week_stats(ctx, plan)?;
    let lws = last_week_same_stats(ctx, plan)?;
    let week_progress = build_week_progress(ctx, &cws, &lws);

    let today_naive = naive_of(&ctx.local_date)?;
    let wd = u64::from(today_naive.weekday().num_days_from_monday());
    let monday = today_naive - Days::new(wd);
    let first_monday = monday - Days::new(11 * 7);
    let span = ctx
        .snapshot
        .stats_daily_rows(&local_of(first_monday)?, &ctx.local_date)?;
    let mut points = Vec::with_capacity(12);
    for week in 0..12 {
        let ws = first_monday + Days::new(week * 7);
        if week == 11 {
            points.push(WeeklyPointDto {
                week_start_date: local_of(ws)?,
                active_duration_ms: Int64String(cws.total_ms),
                is_current_week: true,
                completed_recorded_days: cws.completed_days,
                current_week_daily_avg_ms: if cws.completed_days > 0 {
                    Some(Int64String(
                        cws.completed_sum_ms / i64::from(cws.completed_days),
                    ))
                } else {
                    None
                },
            });
        } else {
            let we = ws + Days::new(6);
            let mut total = 0_i64;
            for day in &span {
                let d = naive_of(
                    &LocalDate::parse(&day.local_date)
                        .map_err(|e| StorageError::new(e.code, e.message))?,
                )?;
                if d >= ws && d <= we {
                    total += day.active_duration_ms;
                }
            }
            points.push(WeeklyPointDto {
                week_start_date: local_of(ws)?,
                active_duration_ms: Int64String(total),
                is_current_week: false,
                completed_recorded_days: 0,
                current_week_daily_avg_ms: None,
            });
        }
    }
    Ok((points, week_progress))
}

/// 构成桶 + 固定槽位（10 §4.4）：日桶完整骨架 / 30 天 ISO 周桶，hasData 如实表达。
fn stats_composition(
    ctx: &StatsQueryContext,
    days: u32,
    day_metrics: &[DayMetric],
) -> Result<(Vec<CompositionBucketDto>, Vec<AppPaletteEntryDto>), StorageError> {
    let start = local_of(naive_of(&ctx.local_date)? - Days::new(u64::from(days - 1)))?;
    let totals = ctx.snapshot.stats_app_totals(&start, &ctx.local_date)?;
    let palette = allocate_slots(&totals, 3);
    let app_rows = ctx.snapshot.stats_app_rows(&start, &ctx.local_date)?;
    let buckets = bucketize_composition(&app_rows, &palette, day_metrics, days, &ctx.local_date);
    Ok((buckets, palette))
}

/// 惯性（10 §4.4）：窗口 [today-14, today-1]，统一分母 + 派生标注。
fn stats_hourly_profile(
    ctx: &StatsQueryContext,
) -> Result<(Vec<HourlyPointDto>, InertiaDto), StorageError> {
    let today_naive = naive_of(&ctx.local_date)?;
    let start = local_of(today_naive - Days::new(14))?;
    let end = local_of(today_naive - Days::new(1))?;
    let (profile, effective_days) = ctx.snapshot.stats_hourly_profile(&start, &end)?;
    let points = profile
        .iter()
        .enumerate()
        .map(|(hour, ms)| HourlyPointDto {
            local_hour: hour as u32,
            avg_active_ms: Int64String(*ms),
        })
        .collect();
    let inertia = derive_inertia(&profile, effective_days);
    Ok((points, inertia))
}

/// 工作节奏（v0.2 候选：惯性卡片融合）。窗口与惯性一致（today-14 → today-1，
/// 不含今日——与 10 §4.4"惯性窗口排除今日"同一口径，reliability 同门禁）。
fn stats_work_pace(ctx: &StatsQueryContext) -> Result<WorkPaceDto, StorageError> {
    let today_naive = naive_of(&ctx.local_date)?;
    let start = local_of(today_naive - Days::new(14))?;
    let end = local_of(today_naive - Days::new(1))?;
    let (days, _effective_days) = ctx.snapshot.stats_work_pace_days(&start, &end)?;
    Ok(derive_work_pace(&days, 14))
}

/// 月度（近 6 个日历月）+ 里程碑（10 §4.5）：当前月 recordedDays/均值不含今日。
fn stats_monthly(
    ctx: &StatsQueryContext,
    plan: &CutoffPlan,
) -> Result<(Vec<MonthlyPointDto>, MilestoneDto), StorageError> {
    let recorded = ctx.snapshot.stats_recorded_dates()?;
    let total_recorded = recorded.len() as i64;
    let longest = longest_consecutive(&recorded);
    let first_month = recorded.first().map(|d| d.chars().take(7).collect());
    let milestone = MilestoneDto {
        total_recorded_days: Int64String(total_recorded),
        longest_consecutive_days: Int64String(longest),
        first_recorded_month: first_month,
    };

    let today_naive = naive_of(&ctx.local_date)?;
    let current_first = NaiveDate::from_ymd_opt(today_naive.year(), today_naive.month(), 1)
        .ok_or_else(|| StorageError::internal("日期越界"))?;
    let span_start = current_first
        .checked_sub_months(Months::new(5))
        .ok_or_else(|| StorageError::internal("月份越界"))?;
    let span_daily = ctx
        .snapshot
        .stats_daily_rows(&local_of(span_start)?, &ctx.local_date)?;
    let today_cutoff = plan.index.active_of(&ctx.local_date)?;
    let today_str = ctx.local_date.as_str().to_string();

    let mut monthly = Vec::with_capacity(6);
    let (mut year, mut month) = (span_start.year(), span_start.month());
    for index in 0..6 {
        let first = NaiveDate::from_ymd_opt(year, month, 1)
            .ok_or_else(|| StorageError::internal("日期越界"))?;
        let next = if month == 12 {
            NaiveDate::from_ymd_opt(year + 1, 1, 1)
        } else {
            NaiveDate::from_ymd_opt(year, month + 1, 1)
        }
        .ok_or_else(|| StorageError::internal("日期越界"))?;
        let last = next - Days::new(1);
        let is_current = index == 5;
        let mut active_total = 0_i64;
        let mut recorded_days = 0_i32;
        let mut completed_sum = 0_i64;
        for day in &span_daily {
            let d = naive_of(
                &LocalDate::parse(&day.local_date)
                    .map_err(|e| StorageError::new(e.code, e.message))?,
            )?;
            if d < first || d > last {
                continue;
            }
            if is_current && day.local_date == today_str {
                continue; // 今日单独按 cutoff 计入总量，不进入 recordedDays/均值
            }
            active_total += day.active_duration_ms;
            if day.has_data {
                recorded_days += 1;
                completed_sum += day.active_duration_ms;
            }
        }
        if is_current {
            active_total += today_cutoff;
        }
        let avg = if recorded_days > 0 {
            Some(Int64String(completed_sum / i64::from(recorded_days)))
        } else {
            None
        };
        monthly.push(MonthlyPointDto {
            month: format!("{year:04}-{month:02}"),
            active_duration_ms: Int64String(active_total),
            recorded_days,
            is_current_month: is_current,
            avg_active_ms_per_recorded_day: avg,
        });
        if month == 12 {
            year += 1;
            month = 1;
        } else {
            month += 1;
        }
    }
    Ok((monthly, milestone))
}

/// 摘要（10 §5.3）：窗口固定 [today-14, today-1]，与 `days` 切换无关。
fn build_summary_from_daily(
    ctx: &StatsQueryContext,
    daily: &[DayMetric],
    inertia: &InertiaDto,
) -> Result<SummaryDto, StorageError> {
    let today_naive = naive_of(&ctx.local_date)?;
    let recent_start = (today_naive - Days::new(7)).format("%Y-%m-%d").to_string();
    let recent_end = (today_naive - Days::new(1)).format("%Y-%m-%d").to_string();
    let prior_start = (today_naive - Days::new(14)).format("%Y-%m-%d").to_string();
    let prior_end = (today_naive - Days::new(8)).format("%Y-%m-%d").to_string();
    let (recent_avg, recent_days) = window_avg(daily, &recent_start, &recent_end);
    let (prior_avg, prior_days) = window_avg(daily, &prior_start, &prior_end);
    let direction = if recent_days >= 3 && prior_days >= 3 {
        summary_direction(recent_avg, prior_avg)
    } else {
        None
    };
    Ok(build_summary(
        direction,
        inertia.peak_hour.map(|h| h as u32),
        inertia.reliability,
    ))
}

fn window_avg(daily: &[DayMetric], start: &str, end: &str) -> (Option<i64>, i32) {
    let mut sum = 0_i64;
    let mut count = 0_i32;
    for day in daily {
        if day.has_data && day.local_date.as_str() >= start && day.local_date.as_str() <= end {
            sum = sum.saturating_add(day.active_duration_ms);
            count += 1;
        }
    }
    (
        if count > 0 {
            Some(sum / i64::from(count))
        } else {
            None
        },
        count,
    )
}

/// 今日趋势点（轻量轮询用）：cutoff 活跃/块数 + hasData + MA 恒 null。
fn stats_today_trend_point(
    ctx: &StatsQueryContext,
    plan: &CutoffPlan,
) -> Result<TrendPointDto, StorageError> {
    let today = &ctx.local_date;
    let row_active = plan.index.active_of(today)?;
    let row_blocks = plan.index.blocks_of(today)?;
    // 单次 daily 查询 [today-6, today] 同时取得今日 hasData 与 MA sampleDays（P2-2），
    // 不再单独查今日。
    let today_naive = naive_of(today)?;
    let ma_start = local_of(today_naive - Days::new(6))?;
    let ma_daily = ctx.snapshot.stats_daily_rows(&ma_start, today)?;
    let today_str = today.as_str().to_string();
    let today_recorded = ma_daily
        .iter()
        .find(|d| d.local_date == today_str)
        .map(|d| d.has_data)
        .unwrap_or(false);
    let samples: Vec<DailyMetricSample> = ma_daily
        .iter()
        .map(|d| DailyMetricSample {
            active_duration_ms: d.active_duration_ms,
            has_data: d.has_data,
            is_today: d.local_date == today_str,
        })
        .collect();
    let (_, sample_days) = compute_moving_avg7(&samples, ma_daily.len().saturating_sub(1));
    Ok(TrendPointDto {
        local_date: today.clone(),
        active_duration_ms: Int64String(row_active),
        work_block_count: Int64String(row_blocks),
        has_data: today_recorded,
        is_today: true,
        moving_avg7_active_ms: None,
        moving_avg7_sample_days: sample_days,
    })
}
