//! 统计主页组装层（11 实施方案阶段一 1.2、阶段三 3.1）：槽位分配与构成桶聚合。
//!
//! 依赖 `wuji-storage` 导出的行类型（`AppTotalRow`/`AppDayRow`/`DayMetric`），
//! 因此不进 wuji-core；仍属 Rust 口径（槽位、ISO 周聚合、完整骨架 + hasData）。

use chrono::{Datelike, Days, NaiveDate};
use std::collections::HashSet;
use wuji_core::dto::{
    AppDto, AppPaletteEntryDto, BucketKind, CompositionBucketDto, Int64String, LocalDate,
    TopEntryDto,
};
use wuji_storage::{AppDayRow, AppTotalRow, DayMetric};

/// 周期内整体 Top N 固定槽位（10 §4.4）：按传入顺序（Reader 已按 SUM DESC、
/// app_id ASC 排序）分配 0..top_n；`slot` 由构造保证 < top_n（调用方传 3）。
pub fn allocate_slots(totals: &[AppTotalRow], top_n: u32) -> Vec<AppPaletteEntryDto> {
    totals
        .iter()
        .take(top_n as usize)
        .enumerate()
        .map(|(index, row)| AppPaletteEntryDto {
            app: AppDto {
                app_id: Int64String(row.app_id),
                display_name: row.display_name.clone(),
            },
            slot: index as u32,
        })
        .collect()
}

/// 构成桶聚合（10 §4.4 + 11 阶段一 1.1）：7/14 天按完整日期骨架返回日桶
/// （每个自然日一个桶，`hasData` 如实表达），30 天按 ISO 周返回周桶（跨月周保持
/// 单桶不拆分，桶界钳制到窗口）。桶内 `apps` 按 palette 槽位顺序只含活跃 > 0 的
/// 应用，其余应用聚合为 `othersActiveMs`。
pub fn bucketize_composition(
    app_rows: &[AppDayRow],
    palette: &[AppPaletteEntryDto],
    day_metrics: &[DayMetric],
    days: u32,
    today: &LocalDate,
) -> Vec<CompositionBucketDto> {
    if days == 30 {
        bucketize_weeks(app_rows, palette, day_metrics, today)
    } else {
        bucketize_days(app_rows, palette, day_metrics, days, today)
    }
}

fn naive_of(date: &LocalDate) -> NaiveDate {
    NaiveDate::parse_from_str(date.as_str(), "%Y-%m-%d").expect("本地日期必须合法")
}

fn local_of(naive: NaiveDate) -> LocalDate {
    LocalDate::parse(&naive.format("%Y-%m-%d").to_string()).expect("格式化日期必须合法")
}

/// 桶内应用条目：palette 槽位顺序、活跃 > 0；非 palette 应用聚合为 other。
fn bucket_apps(rows: &[&AppDayRow], palette: &[AppPaletteEntryDto]) -> (Vec<TopEntryDto>, i64) {
    let mut apps = Vec::new();
    let mut others = 0_i64;
    let mut palette_ids = HashSet::new();
    for entry in palette {
        palette_ids.insert(entry.app.app_id.0);
        if let Some(row) = rows
            .iter()
            .find(|r| r.app_id == entry.app.app_id.0 && r.active_ms > 0)
        {
            apps.push(TopEntryDto {
                app: entry.app.clone(),
                active_duration_ms: Int64String(row.active_ms),
            });
        }
    }
    for row in rows {
        if !palette_ids.contains(&row.app_id) {
            others += row.active_ms;
        }
    }
    (apps, others)
}

/// 周桶内应用条目：palette 槽位顺序、按周聚合总量（active > 0），非 palette 聚合为 other。
fn bucket_apps_aggregate(
    rows: &[&AppDayRow],
    palette: &[AppPaletteEntryDto],
) -> (Vec<TopEntryDto>, i64) {
    let mut apps = Vec::new();
    let mut others = 0_i64;
    let mut palette_ids = HashSet::new();
    for entry in palette {
        palette_ids.insert(entry.app.app_id.0);
        let total: i64 = rows
            .iter()
            .filter(|r| r.app_id == entry.app.app_id.0)
            .map(|r| r.active_ms)
            .sum();
        if total > 0 {
            apps.push(TopEntryDto {
                app: entry.app.clone(),
                active_duration_ms: Int64String(total),
            });
        }
    }
    for row in rows {
        if !palette_ids.contains(&row.app_id) {
            others += row.active_ms;
        }
    }
    (apps, others)
}

fn has_data_days(day_metrics: &[DayMetric]) -> HashSet<String> {
    day_metrics
        .iter()
        .filter(|d| d.has_data)
        .map(|d| d.local_date.clone())
        .collect()
}

fn bucketize_days(
    app_rows: &[AppDayRow],
    palette: &[AppPaletteEntryDto],
    day_metrics: &[DayMetric],
    days: u32,
    today: &LocalDate,
) -> Vec<CompositionBucketDto> {
    let today_naive = naive_of(today);
    let start = today_naive - Days::new(u64::from(days - 1));
    let has_data = has_data_days(day_metrics);
    (0..days)
        .map(|offset| {
            let date_naive = start + Days::new(u64::from(offset));
            let date_str = date_naive.format("%Y-%m-%d").to_string();
            let day_rows: Vec<&AppDayRow> = app_rows
                .iter()
                .filter(|r| r.local_date == date_str)
                .collect();
            let (apps, others) = bucket_apps(&day_rows, palette);
            CompositionBucketDto {
                start_date: local_of(date_naive),
                end_date: local_of(date_naive),
                bucket_kind: BucketKind::Day,
                is_current: date_naive == today_naive,
                has_data: has_data.contains(&date_str),
                apps,
                others_active_ms: Int64String(others),
            }
        })
        .collect()
}

fn bucketize_weeks(
    app_rows: &[AppDayRow],
    palette: &[AppPaletteEntryDto],
    day_metrics: &[DayMetric],
    today: &LocalDate,
) -> Vec<CompositionBucketDto> {
    let today_naive = naive_of(today);
    let start = today_naive - Days::new(29); // 30 天窗口
    // ISO 周（周一起始）：窗口内相交的周逐周一个桶，桶界钳制到窗口。
    let first_monday = start - Days::new(u64::from(start.weekday().num_days_from_monday()));
    let has_data = has_data_days(day_metrics);
    let mut buckets = Vec::new();
    let mut monday = first_monday;
    while monday <= today_naive {
        let bucket_start = monday.max(start);
        let sunday = monday + Days::new(6);
        let bucket_end = sunday.min(today_naive);
        let is_current = monday <= today_naive && today_naive <= sunday;
        // hasData = 桶内至少一个自然日存在 daily_work_metrics 行。
        let mut bucket_has_data = false;
        let mut day = bucket_start;
        while day <= bucket_end {
            if has_data.contains(&day.format("%Y-%m-%d").to_string()) {
                bucket_has_data = true;
                break;
            }
            day = day + Days::new(1);
        }
        let week_rows: Vec<&AppDayRow> = app_rows
            .iter()
            .filter(|r| {
                NaiveDate::parse_from_str(&r.local_date, "%Y-%m-%d")
                    .ok()
                    .is_some_and(|d| d >= bucket_start && d <= bucket_end)
            })
            .collect();
        let (apps, others) = bucket_apps_aggregate(&week_rows, palette);
        buckets.push(CompositionBucketDto {
            start_date: local_of(bucket_start),
            end_date: local_of(bucket_end),
            bucket_kind: BucketKind::Week,
            is_current,
            has_data: bucket_has_data,
            apps,
            others_active_ms: Int64String(others),
        });
        monday = monday + Days::new(7);
    }
    buckets
}

#[cfg(test)]
mod tests {
    use super::*;

    fn total_row(app_id: i64, name: &str, total: i64) -> AppTotalRow {
        AppTotalRow {
            app_id,
            display_name: name.to_string(),
            total_active_ms: total,
        }
    }

    fn day_row(date: &str, app_id: i64, name: &str, active: i64) -> AppDayRow {
        AppDayRow {
            local_date: date.to_string(),
            app_id,
            display_name: name.to_string(),
            active_ms: active,
        }
    }

    fn metric(date: &str, has_data: bool) -> DayMetric {
        DayMetric {
            local_date: date.to_string(),
            active_duration_ms: 0,
            work_block_count: 0,
            has_data,
        }
    }

    fn local(s: &str) -> LocalDate {
        LocalDate::parse(s).unwrap()
    }

    #[test]
    fn allocate_slots_assigns_rank_slots_and_stays_below_top_n() {
        // 输入已按 SUM DESC 排序（Reader 保证）；top_n=3 → 前 3 个获得 0..3 槽位。
        let totals = vec![
            total_row(1, "a", 9_000),
            total_row(2, "b", 7_000),
            total_row(3, "c", 5_000),
            total_row(4, "d", 1_000),
        ];
        let slots = allocate_slots(&totals, 3);
        assert_eq!(slots.len(), 3);
        assert_eq!(slots[0].app.display_name, "a");
        assert_eq!(slots[0].slot, 0);
        assert_eq!(slots[1].app.display_name, "b");
        assert_eq!(slots[1].slot, 1);
        assert_eq!(slots[2].app.display_name, "c");
        assert_eq!(slots[2].slot, 2);
        for entry in &slots {
            assert!(entry.slot < 3, "slot 必须由构造保证 < 3");
        }
        // 应用数少于 top_n 时不补位。
        assert_eq!(allocate_slots(&totals[..2], 3).len(), 2);
    }

    #[test]
    fn allocate_slots_inherits_reader_tie_break_order() {
        // tie-break 由 Reader `ORDER BY SUM DESC, app_id ASC` 排序承载（stats_app_totals
        // 合同）；组装层继承输入顺序并截断——等值总量输入按 Reader 顺序保留。
        let tied = vec![total_row(1, "a", 9_000), total_row(2, "b", 9_000)];
        let slots = allocate_slots(&tied, 3);
        assert_eq!(slots.len(), 2);
        assert_eq!(slots[0].app.display_name, "a");
        assert_eq!(slots[0].slot, 0);
        assert_eq!(slots[1].app.display_name, "b");
        assert_eq!(slots[1].slot, 1);
    }

    #[test]
    fn day_buckets_follow_full_skeleton_with_palette_order_and_others() {
        let today = local("2026-07-18");
        let app_rows = vec![
            day_row("2026-07-17", 1, "code", 3_600_000),
            day_row("2026-07-17", 2, "notepad", 900_000),
            day_row("2026-07-17", 3, "other-app", 300_000),
            day_row("2026-07-18", 1, "code", 1_800_000),
        ];
        let palette = vec![
            AppPaletteEntryDto {
                app: AppDto {
                    app_id: Int64String(1),
                    display_name: "code".to_string(),
                },
                slot: 0,
            },
            AppPaletteEntryDto {
                app: AppDto {
                    app_id: Int64String(2),
                    display_name: "notepad".to_string(),
                },
                slot: 1,
            },
        ];
        // 骨架：07-17 有记录（has_data=true），07-18（今日）无记录。
        let metrics = vec![metric("2026-07-17", true), metric("2026-07-18", false)];
        let buckets = bucketize_composition(&app_rows, &palette, &metrics, 2, &today);
        assert_eq!(buckets.len(), 2);
        assert_eq!(buckets[0].start_date.as_str(), "2026-07-17");
        assert!(buckets[0].has_data);
        assert!(!buckets[0].is_current);
        assert_eq!(buckets[0].bucket_kind, BucketKind::Day);
        // 07-17：palette 槽位顺序（code 先于 notepad），非 palette 聚合为 other。
        assert_eq!(buckets[0].apps.len(), 2);
        assert_eq!(buckets[0].apps[0].app.display_name, "code");
        assert_eq!(buckets[0].apps[0].active_duration_ms.0, 3_600_000);
        assert_eq!(buckets[0].apps[1].app.display_name, "notepad");
        assert_eq!(buckets[0].others_active_ms.0, 300_000);
        // 07-18：今日 hasData=false，但当日有应用活跃（记录日无投影的边界）。
        assert_eq!(buckets[1].start_date.as_str(), "2026-07-18");
        assert!(!buckets[1].has_data);
        assert!(buckets[1].is_current);
        assert_eq!(buckets[1].apps.len(), 1);
        assert_eq!(buckets[1].apps[0].app.display_name, "code");
    }

    #[test]
    fn palette_app_with_zero_active_is_omitted_from_bucket_apps() {
        let today = local("2026-07-18");
        let app_rows = vec![day_row("2026-07-17", 2, "notepad", 900_000)];
        let palette = vec![
            AppPaletteEntryDto {
                app: AppDto {
                    app_id: Int64String(1),
                    display_name: "code".to_string(),
                },
                slot: 0,
            },
            AppPaletteEntryDto {
                app: AppDto {
                    app_id: Int64String(2),
                    display_name: "notepad".to_string(),
                },
                slot: 1,
            },
        ];
        let metrics = vec![metric("2026-07-17", true), metric("2026-07-18", false)];
        let buckets = bucketize_composition(&app_rows, &palette, &metrics, 2, &today);
        // code 当日 0 活跃 → 不出现在 apps（零宽段），notepad 仍按槽位序出现。
        assert_eq!(buckets[0].apps.len(), 1);
        assert_eq!(buckets[0].apps[0].app.display_name, "notepad");
        assert_eq!(buckets[0].others_active_ms.0, 0);
    }

    #[test]
    fn week_buckets_follow_iso_monday_and_keep_cross_month_week_unsplit() {
        let today = local("2026-07-18"); // 周六
        // 30 天窗口 [06-19, 07-18]，无任何应用行/记录日（hasData=false）。
        let buckets = bucketize_composition(&[], &[], &[], 30, &today);
        // 30 天窗口跨 5-6 个 ISO 周（视窗口起点对齐，首尾部分周钳制）。
        assert_eq!(buckets.len(), 5);
        assert_eq!(buckets[0].start_date.as_str(), "2026-06-19");
        assert_eq!(buckets[0].end_date.as_str(), "2026-06-21"); // 首个桶从周一 06-15 起但钳制到窗口起点
        assert_eq!(buckets[0].bucket_kind, BucketKind::Week);
        assert!(!buckets[0].is_current);
        assert!(buckets[4].is_current, "含今日的周是当前周");
        assert_eq!(buckets[4].start_date.as_str(), "2026-07-13"); // ISO 周一
        assert_eq!(buckets[4].end_date.as_str(), "2026-07-18"); // 钳制到今日
        for bucket in &buckets {
            assert!(!bucket.has_data);
            assert!(bucket.apps.is_empty());
        }
    }

    #[test]
    fn week_bucket_has_data_when_any_day_recorded_and_apps_summed_per_week() {
        let today = local("2026-07-18");
        let app_rows = vec![
            day_row("2026-07-14", 1, "code", 3_600_000), // 当前周（07-13 起）周二
            day_row("2026-07-18", 1, "code", 1_800_000), // 今日
            day_row("2026-07-08", 2, "notepad", 900_000), // 上周（07-06 周）
        ];
        let metrics = vec![
            metric("2026-07-14", true),
            metric("2026-07-18", true),
            metric("2026-07-08", true),
        ];
        let palette = vec![
            AppPaletteEntryDto {
                app: AppDto {
                    app_id: Int64String(1),
                    display_name: "code".to_string(),
                },
                slot: 0,
            },
            AppPaletteEntryDto {
                app: AppDto {
                    app_id: Int64String(2),
                    display_name: "notepad".to_string(),
                },
                slot: 1,
            },
        ];
        let buckets = bucketize_composition(&app_rows, &palette, &metrics, 30, &today);
        assert_eq!(buckets.len(), 5);
        // 当前周（最后一个桶）：code 07-14 + 07-18 合计。
        let current = &buckets[4];
        assert!(current.is_current);
        assert!(current.has_data);
        assert_eq!(current.apps.len(), 1);
        assert_eq!(current.apps[0].app.display_name, "code");
        assert_eq!(current.apps[0].active_duration_ms.0, 5_400_000);
        assert_eq!(current.others_active_ms.0, 0);
        // 07-08 所在周（倒数第二个桶前…按顺序应为 index 2：06-29 周起第四周？直接按日期查找）。
        let week_of_0708 = buckets
            .iter()
            .find(|b| b.start_date.as_str() <= "2026-07-08" && "2026-07-08" <= b.end_date.as_str())
            .expect("07-08 所在周桶");
        assert_eq!(week_of_0708.apps.len(), 1);
        assert_eq!(week_of_0708.apps[0].app.display_name, "notepad");
        assert_eq!(week_of_0708.apps[0].active_duration_ms.0, 900_000);
    }
}
