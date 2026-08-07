//! 只读探针：dump 工作惯性（近 14 天）的真实查询输入与派生结果。
//! 用法：cargo run -p wuji-storage --example dump_work_pace -- <db路径>
use std::path::PathBuf;

use wuji_core::dto::LocalDate;
use wuji_core::stats::{DayCoverage, derive_inertia, derive_work_pace};
use wuji_storage::Reader;
use wuji_storage::timeutil::{local_date_of, now_utc_ms};

fn hhmm(minutes: u32) -> String {
    let h = minutes / 60;
    let m = minutes % 60;
    format!("{h:02}:{m:02}")
}

fn main() {
    let db = std::env::args()
        .nth(1)
        .unwrap_or_else(|| panic!("用法: dump_work_pace <db路径>"));
    let mut reader = Reader::open(&PathBuf::from(db)).expect("打开只读 Reader");
    let tz = reader.schema_meta().reporting_tz().expect("reporting tz");
    let today = LocalDate::parse(&local_date_of(&tz, now_utc_ms()).unwrap()).unwrap();
    // 日期推进：直接对 YYYY-MM-DD 做字符串级计算（示例探针专用，不引入新依赖）。
    let end = LocalDate::parse(&shift_ymd(today.as_str(), -1)).unwrap();
    let start = LocalDate::parse(&shift_ymd(today.as_str(), -14)).unwrap();
    println!("reporting_tz={} 窗口=[{start}, {end}]", tz.name());

    // ---- 惯性曲线（活跃均值） ----
    let (profile, effective_days) = reader
        .with_snapshot(|s| s.stats_hourly_profile(&start, &end))
        .unwrap();
    println!("\n== hourly_profile（每小时活跃均值 ms）effective_days={effective_days} ==");
    for (h, ms) in profile.iter().enumerate() {
        if *ms > 0 {
            println!("  {h:02}:00  {ms} ms ({:.2} h)", *ms as f64 / 3_600_000.0);
        }
    }
    let inertia = derive_inertia(&profile, effective_days);
    println!("\n== inertia ==");
    println!(
        "  开工={:?} 高峰={:?} 收工={:?} 午休低谷={:?} 有效日={}/{} reliability={:?}",
        inertia.start_hour,
        inertia.peak_hour,
        inertia.end_hour,
        inertia.lunch_lowest_hour,
        inertia.effective_days,
        inertia.total_days,
        inertia.reliability
    );

    // ---- 工作节奏（在工位覆盖） ----
    let (days, effective) = reader
        .with_snapshot(|s| s.stats_work_pace_days(&start, &end))
        .unwrap();
    println!("\n== stats_work_pace_days: 每有效日覆盖段（分钟）effective={effective} ==");
    let mut idx = 0usize;
    for day in &days {
        idx += 1;
        let mut desc: Vec<String> = day
            .segments
            .iter()
            .map(|&(s, e)| format!("{}–{} ({}m)", hhmm(s), hhmm(e), e - s))
            .collect();
        if desc.is_empty() {
            desc.push("(无覆盖)".to_string());
        }
        println!("  日{idx:02}: {}", desc.join(" | "));
    }
    let pace = derive_work_pace(&days, 14);
    println!("\n== work_pace（当前工作树 derive_work_pace 结果）==");
    println!(
        "  工作 {}%  有效日 {}/{}  reliability={:?}",
        pace.work_ratio_percent, pace.effective_days, pace.total_days, pace.reliability
    );
    println!(
        "  常见开工={:?}  常见收工={:?}  上午(8-12点)有工作 {} 天",
        pace.common_start_minutes.map(|m| hhmm(m as u32)),
        pace.common_end_minutes.map(|m| hhmm(m as u32)),
        pace.morning_work_days
    );
    println!("  hourly_coverage_ms:");
    for p in &pace.hourly_coverage_ms {
        if p.avg_coverage_ms.0 > 0 {
            println!("    {:02}:00  {} ms", p.local_hour, p.avg_coverage_ms.0);
        }
    }

    // ---- 每日首/末覆盖（开工/收工诊断） ----
    println!("\n== 每有效日 首覆盖开始 / 末覆盖结束 ==");
    for (i, day) in days.iter().enumerate() {
        let first = day.segments.first().map(|&(s, _)| hhmm(s));
        let last = day.segments.last().map(|&(_, e)| hhmm(e));
        println!("  日{:02}: 开工={:?}  收工={:?}", i + 1, first, last);
    }
    // 中间休息段（补集）逐日明细
    println!("\n== 每有效日 未工作连续段（分钟）==");
    for (i, day) in days.iter().enumerate() {
        let mut cov = [false; 1440];
        for &(s, e) in &day.segments {
            for m in s..e.min(1440) {
                cov[m as usize] = true;
            }
        }
        let mut rests: Vec<(u32, u32)> = Vec::new();
        let mut cur: Option<u32> = None;
        for (minute, in_cov) in cov.iter().enumerate() {
            if !in_cov {
                if cur.is_none() {
                    cur = Some(minute as u32);
                }
            } else if let Some(s) = cur.take() {
                rests.push((s, minute as u32));
            }
        }
        if let Some(s) = cur {
            rests.push((s, 1440));
        }
        let desc: Vec<String> = rests
            .iter()
            .map(|&(s, e)| {
                let tag = if s == 0 {
                    "TAIL"
                } else if e == 1440 {
                    "HEAD"
                } else {
                    let mid = (s + e) / 2;
                    if (660..=840).contains(&mid) {
                        "MIDDAY"
                    } else {
                        "BETWEEN"
                    }
                };
                format!("{} {}–{} ({}m)", tag, hhmm(s), hhmm(e), e - s)
            })
            .collect();
        println!("  日{:02}: {}", i + 1, desc.join(" | "));
    }

    let _ = DayCoverage { segments: vec![] };
}

/// YYYY-MM-DD 加减天数（探针专用，最小实现）。
fn shift_ymd(raw: &str, days: i64) -> String {
    let (y, m, d) = {
        let mut it = raw.split('-');
        (
            it.next().unwrap().parse::<i64>().unwrap(),
            it.next().unwrap().parse::<i64>().unwrap(),
            it.next().unwrap().parse::<i64>().unwrap(),
        )
    };
    let base = chrono::NaiveDate::from_ymd_opt(y as i32, m as u32, d as u32).unwrap();
    (base + chrono::Duration::days(days))
        .format("%Y-%m-%d")
        .to_string()
}
