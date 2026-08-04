//! 统计主页 QueryService + 命令层端到端测试（11 实施方案阶段三 3.1/3.2 DoD）。
//!
//! **固定时钟注入**（P1-2）：所有测试走 `stats_home_at`/`stats_status_at(now)`，
//! 使用固定 UTC 时刻 `FIXED_NOW`（上海 12:00，今日会话全部饱和）——断言精确、
//! 无容差、无跨午夜错位；另补跨午夜边界用例。

use std::path::PathBuf;

use chrono::{Days, NaiveDate};
use wuji_core::domain::{ActivityState, CaptureQuality, GapKind};
use wuji_core::dto::{LocalDate, RuntimeId};
use wuji_rebuild_desktop_lib::paths;
use wuji_rebuild_desktop_lib::query::QueryService;
use wuji_storage::timeutil::local_day_range_utc_ms;
use wuji_storage::writer::StorageTransaction;
use wuji_storage::{ObservationInsert, Writer};

const T0: i64 = 1_784_332_800_000; // 2026-07-18T00:00:00Z
const SHANGHAI: &str = "Asia/Shanghai";
const GAP_CAP_MS: i64 = 15_000;
/// 固定 UTC 时刻：2026-07-18T04:00:00Z = 上海 12:00（早于任何会话结束 → 全部饱和）。
const FIXED_NOW: i64 = T0 + 4 * 3_600_000;
/// 跨午夜边界：2026-07-18T16:00:00Z = 上海 07-19 00:00。
const MIDNIGHT_NOW: i64 = T0 + 16 * 3_600_000;
const FIXED_TODAY: &str = "2026-07-18";

fn test_channel() -> String {
    format!(
        "rebuild-v01-test-{}",
        ulid::Ulid::generate().to_string().to_lowercase()
    )
}

fn db_path(channel: &str) -> PathBuf {
    paths::data_root(channel)
        .expect("data root")
        .join("data")
        .join("wuji-rebuild-v0.1.db")
}

fn bootstrap(channel: &str) -> Writer {
    let path = db_path(channel);
    std::fs::create_dir_all(path.parent().unwrap()).unwrap();
    Writer::bootstrap_with_timezone(&path, SHANGHAI, T0).expect("bootstrap 应成功")
}

fn local(s: &str) -> LocalDate {
    LocalDate::parse(s).unwrap()
}

fn seed_app(tx: &StorageTransaction<'_>, name: &str) -> i64 {
    let hash = name.bytes().fold(0xcbf29ce484222325_u64, |h, b| {
        (h ^ u64::from(b)).wrapping_mul(0x100000001b3)
    });
    let app_key = format!("proc:{hash:064x}");
    tx.upsert_app_identity(&app_key, name, &format!("{name}.exe"), T0)
        .expect("app upsert")
}

fn insert_obs(
    tx: &StorageTransaction<'_>,
    runtime: &RuntimeId,
    seq: i64,
    at: i64,
    app: i64,
) -> i64 {
    match tx.insert_observation(
        runtime,
        seq,
        0,
        at,
        at,
        app,
        ActivityState::Active,
        CaptureQuality::Normal,
        0,
    ) {
        Ok(ObservationInsert::Inserted(id)) => id,
        other => panic!("observation 插入失败: {other:?}"),
    }
}

struct Seed<'a, 'w> {
    tx: &'a StorageTransaction<'w>,
    runtime: RuntimeId,
    seq: i64,
}

impl Seed<'_, '_> {
    fn obs(&mut self, at: i64, app: i64) -> i64 {
        self.seq += 1;
        insert_obs(self.tx, &self.runtime, self.seq, at, app)
    }

    fn session(&mut self, app: i64, start: i64, end: i64, close_block: bool) {
        let obs1 = self.obs(start, app);
        let obs2 = self.obs(end, app);
        let seg = self
            .tx
            .open_segment(&self.runtime, 0, app, ActivityState::Active, start, obs1)
            .expect("open segment");
        self.tx
            .update_open_segment(seg, end, obs2)
            .expect("update segment");
        self.tx
            .close_open_segment("capture_stopped")
            .expect("close segment");
        let wb = self
            .tx
            .open_work_block(&self.runtime, start, seg)
            .expect("open work block");
        self.tx
            .update_open_work_block(wb, end, end - start, 0, seg)
            .expect("update work block");
        if close_block {
            self.tx
                .close_open_work_block("capture_stopped")
                .expect("close work block");
        }
    }

    fn gap_only(&mut self, start: i64, end: i64) {
        self.tx
            .open_gap(&self.runtime, GapKind::CapturePaused, start)
            .expect("open gap");
        self.tx.close_open_gap(end).expect("close gap");
    }
}

fn hour_starts_between(start: i64, end: i64) -> Vec<i64> {
    let first = start.div_euclid(3_600_000) * 3_600_000;
    let last = (end - 1).div_euclid(3_600_000) * 3_600_000;
    (0..=((last - first) / 3_600_000))
        .map(|i| first + i * 3_600_000)
        .collect()
}

/// 种子规则（固定日期，today = 2026-07-18）：07-04..07-17 每天 app_a 09:00-10:00 本地
/// （01:00-02:00Z），除 07-11（无数据）与 07-13（gap-only，有 daily 行无小时行）；
/// 07-17 追加 app_b 11:00-11:30 本地（03:00-03:30Z）；07-18 会话 [本地日零点, +30min]
/// 且 work block 未闭合（进行中）。
fn seed_main_fixture(channel: &str) {
    let tz: chrono_tz::Tz = SHANGHAI.parse().unwrap();
    let today = local(FIXED_TODAY);
    let today_naive = NaiveDate::parse_from_str(FIXED_TODAY, "%Y-%m-%d").unwrap();
    let (today_start, _) = local_day_range_utc_ms(&tz, &today).unwrap();

    let mut writer = bootstrap(channel);
    let tx = writer.transaction().unwrap();
    let runtime = RuntimeId::new();
    tx.insert_runtime(&runtime, T0).unwrap();
    let app_a = seed_app(&tx, "code");
    let app_b = seed_app(&tx, "notepad");
    let mut seed = Seed {
        tx: &tx,
        runtime,
        seq: 0,
    };
    let mut hours = Vec::new();

    for offset in 1..=14_i64 {
        let d = today_naive - Days::new(offset as u64);
        let d_local = local(&d.format("%Y-%m-%d").to_string());
        let (d_start, _) = local_day_range_utc_ms(&tz, &d_local).unwrap();
        match offset {
            7 => continue, // 07-11 无数据日（trend hasData=false）
            5 => {
                seed.gap_only(d_start, d_start + 1_800_000); // 07-13
            }
            _ => {
                // 本地 09:00-10:00 = d_start + 9h .. d_start + 10h。
                seed.session(
                    app_a,
                    d_start + 9 * 3_600_000,
                    d_start + 10 * 3_600_000,
                    true,
                );
                hours.extend(hour_starts_between(
                    d_start + 9 * 3_600_000,
                    d_start + 10 * 3_600_000,
                ));
                if offset == 1 {
                    // 07-17 本地 11:00-11:30 = d_start + 11h .. +11.5h。
                    seed.session(
                        app_b,
                        d_start + 11 * 3_600_000,
                        d_start + 11 * 3_600_000 + 1_800_000,
                        true,
                    );
                    hours.extend(hour_starts_between(
                        d_start + 11 * 3_600_000,
                        d_start + 11 * 3_600_000 + 1_800_000,
                    ));
                }
            }
        }
    }
    // 07-18：本地 00:00-00:30（进行中，未闭合块）。
    seed.session(app_a, today_start, today_start + 1_800_000, false);
    hours.extend(hour_starts_between(today_start, today_start + 1_800_000));

    tx.recompute_hours(&tz, &hours).unwrap();
    let dates: Vec<LocalDate> = (0..=14)
        .map(|o| local(&(today_naive - Days::new(o)).format("%Y-%m-%d").to_string()))
        .collect();
    tx.recompute_dates(&tz, &dates, GAP_CAP_MS).unwrap();
    tx.commit().unwrap();
}

#[test]
fn stats_home_full_dto_matches_seed() {
    let channel = test_channel();
    seed_main_fixture(&channel);
    let service = QueryService::new(&channel).expect("query service");
    let (home, cutoff_calls) = service
        .stats_home_at_observed(Some(14), FIXED_NOW)
        .expect("stats_home");
    // 命令级单批次 cutoff（P2-1 回归）：计数与本次 DTO 同快照原子返回，
    // 并在全部辅助查询结束后读取。
    assert_eq!(
        cutoff_calls, 1,
        "stats_home 必须只调用一次 stats_cutoff_series"
    );

    assert_eq!(home.local_date.as_str(), FIXED_TODAY);
    assert_eq!(home.reporting_time_zone_id, SHANGHAI);
    assert!(home.has_any_data);
    // 固定 12:00 本地：今日会话 [00:00,00:30] 饱和 → 精确 1800s；未闭合块计 1。
    assert_eq!(home.status.today_active_ms.0, 1_800_000);
    assert_eq!(home.status.work_block_count.0, 1);
    assert_eq!(home.status.cutoff_local_time, "12:00");
    // 昨日（07-17）app_a 3600 + app_b 1800 = 5400s 饱和 → 今日 1800 vs 5400 → Down -67%。
    assert_eq!(home.status.yesterday_same.sample_days, 1);
    assert_eq!(
        home.status.yesterday_same.active_duration_ms.map(|v| v.0),
        Some(5_400_000)
    );
    assert_eq!(
        home.status.yesterday_same.direction,
        wuji_core::dto::ComparisonDirection::Down
    );
    assert_eq!(home.status.yesterday_same.delta_percent, Some(-67));
    // 近 7 有效日（07-10,12,13,14,15,16,17）同时刻均值 = 23400/7 = 3342857ms。
    assert_eq!(home.status.last7_avg_same.sample_days, 7);
    assert_eq!(
        home.status.last7_avg_same.active_duration_ms.map(|v| v.0),
        Some(3_342_857)
    );
    assert_eq!(
        home.status.last7_avg_same.direction,
        wuji_core::dto::ComparisonDirection::Down
    );
    assert_eq!(home.status.last7_avg_same.delta_percent, Some(-46));
    // 摘要：recent [07-11,07-17] 日均 3300s vs prior [07-04,07-10] 3600s → DownSlight；峰值 9 点 → Morning。
    assert_eq!(
        home.status.summary.direction,
        Some(wuji_core::dto::SummaryDirection::DownSlight)
    );
    assert_eq!(
        home.status.summary.primary_period,
        Some(wuji_core::dto::PeriodKind::Morning)
    );

    // 趋势：14 点；今日 MA 恒 null + 精确活跃；存在 hasData=false 日（07-11）。
    assert_eq!(home.trend.len(), 14);
    let today_point = home.trend.last().unwrap();
    assert!(today_point.is_today);
    assert_eq!(today_point.moving_avg7_active_ms, None);
    assert_eq!(today_point.active_duration_ms.0, 1_800_000);
    assert!(
        home.trend.iter().any(|p| !p.has_data),
        "跳过日 hasData=false"
    );
    assert!(
        home.trend.iter().any(|p| p.moving_avg7_active_ms.is_some()),
        "完整历史日均线非空"
    );

    // 周：12 点；当前周 = [07-13, 07-18]：07-13(gap 0)+14+15+16+17+今日 1800 = 18000s。
    assert_eq!(home.weekly.len(), 12);
    assert!(home.weekly.last().unwrap().is_current_week);
    assert_eq!(home.week_progress.current_active_ms.0, 18_000_000);
    assert_eq!(
        home.week_progress.recorded_days, 6,
        "本周含今日共 6 个记录日"
    );
    let weekly_current = home.weekly.last().unwrap();
    assert_eq!(weekly_current.completed_recorded_days, 5);
    assert_eq!(
        weekly_current.current_week_daily_avg_ms.map(|v| v.0),
        Some(3_240_000)
    );
    // 上周同期公式（P2-3 精确值）：完整日前缀 07-06..07-10 = 18000s + 同周序日 07-11 cutoff 0。
    assert_eq!(home.week_progress.last_week_same.sample_days, 1);
    assert_eq!(
        home.week_progress
            .last_week_same
            .active_duration_ms
            .map(|v| v.0),
        Some(18_000_000)
    );
    assert_eq!(
        home.week_progress.last_week_same.direction,
        wuji_core::dto::ComparisonDirection::Stable
    );
    assert_eq!(home.week_progress.last_week_same.delta_percent, Some(0));

    // 构成：14 日桶；今日 isCurrent + hasData；07-11 桶 hasData=false。
    assert_eq!(home.composition.len(), 14);
    assert!(home.composition.last().unwrap().is_current);
    assert!(home.composition.last().unwrap().has_data);
    assert!(
        !home
            .composition
            .iter()
            .find(|b| b.start_date.as_str() == "2026-07-11")
            .expect("07-11 桶")
            .has_data
    );
    assert_eq!(home.palette.len(), 2, "两个应用 → 2 个槽位");
    assert_eq!(home.palette[0].slot, 0);
    assert_eq!(home.palette[1].slot, 1);

    // 惯性：24 点；有效日 = 13（12 会话 + 07-13 gap）；统一分母精确值。
    assert_eq!(home.hourly_profile.len(), 24);
    assert_eq!(home.inertia.effective_days, 13);
    assert_eq!(home.hourly_profile[9].avg_active_ms.0, 43_200_000 / 13);
    assert_eq!(home.hourly_profile[11].avg_active_ms.0, 1_800_000 / 13);
    assert_eq!(home.inertia.peak_hour, Some(9));
    assert_eq!(
        home.inertia.reliability,
        Some(wuji_core::dto::ReliabilityKind::Normal)
    );

    // 月度：6 点；当前月 2026-07 recordedDays = 13（不含今日）；总量 = 完整日 45000 + 今日 1800。
    assert_eq!(home.monthly.len(), 6);
    assert!(home.monthly.last().unwrap().is_current_month);
    assert_eq!(home.monthly.last().unwrap().month, "2026-07");
    assert_eq!(home.monthly.last().unwrap().recorded_days, 13);
    assert_eq!(
        home.monthly.last().unwrap().active_duration_ms.0,
        46_800_000
    );
    assert_eq!(
        home.monthly
            .last()
            .unwrap()
            .avg_active_ms_per_recorded_day
            .map(|v| v.0),
        Some(45_000_000 / 13)
    );
    // 里程碑：13（07-04..07-17 去 07-11）+ 今日 = 14；最长连续 7。
    assert_eq!(home.milestone.total_recorded_days.0, 14);
    assert_eq!(home.milestone.longest_consecutive_days.0, 7);
    assert_eq!(
        home.milestone.first_recorded_month.as_deref(),
        Some("2026-07")
    );

    let _ = std::fs::remove_dir_all(paths::data_root(&channel).unwrap());
}

#[test]
fn stats_status_is_light_and_carries_reporting_date() {
    let channel = test_channel();
    seed_main_fixture(&channel);
    let service = QueryService::new(&channel).expect("query service");
    let (status, cutoff_calls) = service
        .stats_status_at_observed(FIXED_NOW)
        .expect("stats_status");
    // 命令级单批次 cutoff：5 秒轮询恰一次（P2-1 回归）。
    assert_eq!(
        cutoff_calls, 1,
        "stats_status 必须只调用一次 stats_cutoff_series"
    );

    assert_eq!(status.local_date.as_str(), FIXED_TODAY);
    assert_eq!(status.reporting_time_zone_id, SHANGHAI);
    assert_eq!(status.live_status.today_active_ms.0, 1_800_000);
    assert_eq!(status.live_status.work_block_count.0, 1);
    assert_eq!(status.live_status.cutoff_local_time, "12:00");
    assert!(status.today_trend_point.is_today);
    assert_eq!(status.today_trend_point.moving_avg7_active_ms, None);
    assert_eq!(status.today_trend_point.active_duration_ms.0, 1_800_000);
    assert_eq!(status.week_progress.current_active_ms.0, 18_000_000);

    let _ = std::fs::remove_dir_all(paths::data_root(&channel).unwrap());
}

#[test]
fn summary_is_independent_of_days_switching() {
    let channel = test_channel();
    seed_main_fixture(&channel);
    let service = QueryService::new(&channel).expect("query service");
    let home7 = service.stats_home_at(Some(7), FIXED_NOW).expect("home 7");
    let home14 = service.stats_home_at(Some(14), FIXED_NOW).expect("home 14");
    let home30 = service.stats_home_at(Some(30), FIXED_NOW).expect("home 30");
    assert_eq!(home7.trend.len(), 7);
    assert_eq!(home14.trend.len(), 14);
    assert_eq!(home30.trend.len(), 30);
    // 均线 lookback 越过可见窗口：7 天视图首日（07-12）的窗口 [07-06, 07-12] 大半在
    // 可见窗口外——若无超集供给（read_days = max(days+6, 15)）该点只有 1 个有效样本。
    assert!(
        home7.trend.first().unwrap().moving_avg7_active_ms.is_some(),
        "7 天视图首日均线必须由超集 lookback 供给"
    );
    // 摘要固定窗口 [07-11, 07-17]，与 days 切换无关。
    assert_eq!(home7.status.summary, home14.status.summary);
    assert_eq!(home14.status.summary, home30.status.summary);
    assert_eq!(
        home30.composition.first().unwrap().bucket_kind,
        wuji_core::dto::BucketKind::Week
    );
    assert_eq!(
        home7.composition.first().unwrap().bucket_kind,
        wuji_core::dto::BucketKind::Day
    );
    assert!(
        (5..=6).contains(&home30.composition.len()),
        "30 天跨 5-6 个 ISO 周（取决于对齐）"
    );

    let _ = std::fs::remove_dir_all(paths::data_root(&channel).unwrap());
}

#[test]
fn empty_database_has_any_data_false_and_no_data_comparisons() {
    let channel = test_channel();
    bootstrap(&channel);
    let service = QueryService::new(&channel).expect("query service");
    let home = service.stats_home_at(None, FIXED_NOW).expect("home");
    assert!(!home.has_any_data, "全新库 → 整页空状态");
    assert_eq!(home.milestone.first_recorded_month, None);
    assert!(home.trend.iter().all(|p| !p.has_data));
    let status = service.stats_status_at(FIXED_NOW).expect("status");
    assert_eq!(
        status.live_status.yesterday_same.direction,
        wuji_core::dto::ComparisonDirection::Unavailable
    );
    assert_eq!(
        status.live_status.yesterday_same.unavailable_reason,
        Some(wuji_core::dto::UnavailableReason::NoData)
    );
    assert_eq!(status.live_status.yesterday_same.sample_days, 0);
    assert_eq!(
        status.live_status.last7_avg_same.unavailable_reason,
        Some(wuji_core::dto::UnavailableReason::InsufficientSamples)
    );

    let _ = std::fs::remove_dir_all(paths::data_root(&channel).unwrap());
}

/// 五态 e2e（固定时钟，全部精确断言）：昨日 gap-only（截止活跃恒 0）→ UpFromZero。
#[test]
fn zero_baseline_yesterday_yields_up_from_zero() {
    let channel = test_channel();
    let tz: chrono_tz::Tz = SHANGHAI.parse().unwrap();
    let today_naive = NaiveDate::parse_from_str(FIXED_TODAY, "%Y-%m-%d").unwrap();
    let (today_start, _) = local_day_range_utc_ms(&tz, &local(FIXED_TODAY)).unwrap();

    let mut writer = bootstrap(&channel);
    let tx = writer.transaction().unwrap();
    let runtime = RuntimeId::new();
    tx.insert_runtime(&runtime, T0).unwrap();
    let app = seed_app(&tx, "code");
    let mut seed = Seed {
        tx: &tx,
        runtime,
        seq: 0,
    };
    let yesterday = today_naive - Days::new(1);
    let y_text = yesterday.format("%Y-%m-%d").to_string();
    let (y_start, _) = local_day_range_utc_ms(&tz, &local(&y_text)).unwrap();
    seed.gap_only(y_start, y_start + 1_800_000);
    seed.session(app, today_start, today_start + 1_800_000, true);
    tx.recompute_dates(&tz, &[local(&y_text), local(FIXED_TODAY)], GAP_CAP_MS)
        .unwrap();
    tx.commit().unwrap();

    let service = QueryService::new(&channel).expect("query service");
    let status = service.stats_status_at(FIXED_NOW).expect("status");
    assert_eq!(status.live_status.today_active_ms.0, 1_800_000);
    assert_eq!(
        status.live_status.yesterday_same.direction,
        wuji_core::dto::ComparisonDirection::UpFromZero
    );
    assert_eq!(
        status.live_status.yesterday_same.delta_percent, None,
        "禁止伪造百分比"
    );
    assert_eq!(
        status
            .live_status
            .yesterday_same
            .active_duration_ms
            .map(|v| v.0),
        Some(0)
    );
    assert_eq!(status.live_status.yesterday_same.sample_days, 1);

    let _ = std::fs::remove_dir_all(paths::data_root(&channel).unwrap());
}

/// 五态 e2e：今日/昨日同墙钟会话 → Stable。
#[test]
fn identical_sessions_pin_stable_comparison() {
    let channel = test_channel();
    let tz: chrono_tz::Tz = SHANGHAI.parse().unwrap();
    let today_naive = NaiveDate::parse_from_str(FIXED_TODAY, "%Y-%m-%d").unwrap();
    let (today_start, _) = local_day_range_utc_ms(&tz, &local(FIXED_TODAY)).unwrap();

    let mut writer = bootstrap(&channel);
    let tx = writer.transaction().unwrap();
    let runtime = RuntimeId::new();
    tx.insert_runtime(&runtime, T0).unwrap();
    let app = seed_app(&tx, "code");
    let mut seed = Seed {
        tx: &tx,
        runtime,
        seq: 0,
    };
    let yesterday = today_naive - Days::new(1);
    let y_text = yesterday.format("%Y-%m-%d").to_string();
    let (y_start, _) = local_day_range_utc_ms(&tz, &local(&y_text)).unwrap();
    seed.session(app, y_start, y_start + 1_800_000, true);
    seed.session(app, today_start, today_start + 1_800_000, true);
    tx.recompute_dates(&tz, &[local(&y_text), local(FIXED_TODAY)], GAP_CAP_MS)
        .unwrap();
    tx.commit().unwrap();

    let service = QueryService::new(&channel).expect("query service");
    let status = service.stats_status_at(FIXED_NOW).expect("status");
    assert_eq!(
        status.live_status.yesterday_same.direction,
        wuji_core::dto::ComparisonDirection::Stable
    );
    assert_eq!(status.live_status.yesterday_same.delta_percent, Some(0));
    assert_eq!(status.live_status.yesterday_same.sample_days, 1);

    let _ = std::fs::remove_dir_all(paths::data_root(&channel).unwrap());
}

/// 五态 e2e：今日会话起始晚于昨日 → Down（固定 12:00 饱和：1200s vs 2400s → -50%）。
#[test]
fn later_today_session_pins_down_comparison() {
    let channel = test_channel();
    let tz: chrono_tz::Tz = SHANGHAI.parse().unwrap();
    let today_naive = NaiveDate::parse_from_str(FIXED_TODAY, "%Y-%m-%d").unwrap();
    let (today_start, _) = local_day_range_utc_ms(&tz, &local(FIXED_TODAY)).unwrap();

    let mut writer = bootstrap(&channel);
    let tx = writer.transaction().unwrap();
    let runtime = RuntimeId::new();
    tx.insert_runtime(&runtime, T0).unwrap();
    let app = seed_app(&tx, "code");
    let mut seed = Seed {
        tx: &tx,
        runtime,
        seq: 0,
    };
    let yesterday = today_naive - Days::new(1);
    let y_text = yesterday.format("%Y-%m-%d").to_string();
    let (y_start, _) = local_day_range_utc_ms(&tz, &local(&y_text)).unwrap();
    seed.session(app, y_start, y_start + 40 * 60_000, true);
    seed.session(
        app,
        today_start + 20 * 60_000,
        today_start + 40 * 60_000,
        true,
    );
    tx.recompute_dates(&tz, &[local(&y_text), local(FIXED_TODAY)], GAP_CAP_MS)
        .unwrap();
    tx.commit().unwrap();

    let service = QueryService::new(&channel).expect("query service");
    let status = service.stats_status_at(FIXED_NOW).expect("status");
    assert_eq!(
        status.live_status.yesterday_same.direction,
        wuji_core::dto::ComparisonDirection::Down
    );
    assert_eq!(status.live_status.yesterday_same.delta_percent, Some(-50));

    let _ = std::fs::remove_dir_all(paths::data_root(&channel).unwrap());
}

/// 五态 e2e：今日会话长于昨日同起会话 → Up（固定 12:00 饱和：2400s vs 1800s → +33%）。
#[test]
fn longer_today_session_pins_up_comparison() {
    let channel = test_channel();
    let tz: chrono_tz::Tz = SHANGHAI.parse().unwrap();
    let today_naive = NaiveDate::parse_from_str(FIXED_TODAY, "%Y-%m-%d").unwrap();
    let (today_start, _) = local_day_range_utc_ms(&tz, &local(FIXED_TODAY)).unwrap();

    let mut writer = bootstrap(&channel);
    let tx = writer.transaction().unwrap();
    let runtime = RuntimeId::new();
    tx.insert_runtime(&runtime, T0).unwrap();
    let app = seed_app(&tx, "code");
    let mut seed = Seed {
        tx: &tx,
        runtime,
        seq: 0,
    };
    let yesterday = today_naive - Days::new(1);
    let y_text = yesterday.format("%Y-%m-%d").to_string();
    let (y_start, _) = local_day_range_utc_ms(&tz, &local(&y_text)).unwrap();
    seed.session(app, y_start, y_start + 1_800_000, true);
    seed.session(app, today_start, today_start + 40 * 60_000, true);
    tx.recompute_dates(&tz, &[local(&y_text), local(FIXED_TODAY)], GAP_CAP_MS)
        .unwrap();
    tx.commit().unwrap();

    let service = QueryService::new(&channel).expect("query service");
    let status = service.stats_status_at(FIXED_NOW).expect("status");
    assert_eq!(
        status.live_status.yesterday_same.direction,
        wuji_core::dto::ComparisonDirection::Up
    );
    assert_eq!(status.live_status.yesterday_same.delta_percent, Some(33));

    let _ = std::fs::remove_dir_all(paths::data_root(&channel).unwrap());
}

/// 跨午夜边界（P1-2）：查询 now 落在上海 07-19 00:00 → 报告日期切到 07-19，
/// 昨日（07-18）已记录 → 比较对象存在。
#[test]
fn cross_midnight_query_uses_reporting_local_date() {
    let channel = test_channel();
    seed_main_fixture(&channel);
    let service = QueryService::new(&channel).expect("query service");
    let status = service.stats_status_at(MIDNIGHT_NOW).expect("status");
    assert_eq!(
        status.local_date.as_str(),
        "2026-07-19",
        "跨午夜后报告日期换日"
    );
    assert_eq!(status.live_status.today_active_ms.0, 0, "07-19 尚无活动");
    assert_eq!(
        status.live_status.yesterday_same.sample_days, 1,
        "昨日（07-18）已记录"
    );
    assert!(
        status
            .live_status
            .yesterday_same
            .active_duration_ms
            .is_some()
    );

    let _ = std::fs::remove_dir_all(paths::data_root(&channel).unwrap());
}

#[test]
fn stats_home_rejects_unknown_days_with_default_14() {
    let channel = test_channel();
    seed_main_fixture(&channel);
    let service = QueryService::new(&channel).expect("query service");
    let home = service.stats_home_at(Some(99), FIXED_NOW).expect("home 99");
    assert_eq!(home.trend.len(), 14);
    let _ = std::fs::remove_dir_all(paths::data_root(&channel).unwrap());
}

#[test]
fn stats_query_services_share_database_with_existing_queries() {
    let channel = test_channel();
    seed_main_fixture(&channel);
    let service = QueryService::new(&channel).expect("query service");
    assert_eq!(
        service
            .database_path()
            .file_name()
            .unwrap()
            .to_str()
            .unwrap(),
        "wuji-rebuild-v0.1.db"
    );
    let status = service.stats_status_at(FIXED_NOW).expect("status");
    assert_eq!(status.local_date.as_str().len(), 10);
    let _ = std::fs::remove_dir_all(paths::data_root(&channel).unwrap());
}
