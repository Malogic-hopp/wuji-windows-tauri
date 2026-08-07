//! 统计主页纯函数（11 实施方案阶段一 1.2）。
//!
//! 全部不依赖数据库/时间库，可独立单元测试。模块归属：口径在 Rust 的
//! 比较方向（ComparisonPolicy）、整数百分比、摘要方向/时段、7 日均线、
//! 惯性派生、连续日期、days 规范化。
//!
//! 依赖 Reader 私有行类型的槽位分配与构成桶组装**不在本模块**——它们属于
//! desktop 组装层（11 阶段一 1.2）；本模块只定义纯计算输入（`DailyMetricSample`）。

use crate::dto::{
    ComparisonDirection, CoveragePointDto, InertiaDto, PeriodKind, ReliabilityKind,
    SummaryDirection, SummaryDto, UnavailableReason, WorkPaceDto,
};

/// 7 日均线的纯计算输入（11 阶段一 1.2）：Query 层把 Reader 的 `DayMetric`
/// 映射为它后再调用；不是 DTO，无需 specta/serde。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct DailyMetricSample {
    pub active_duration_ms: i64,
    pub has_data: bool,
    pub is_today: bool,
}

/// 比较场景策略（11 阶段零 P0-5）：**由策略决定不可用归因**，不依赖参数判断顺序。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ComparisonPolicy {
    /// 昨日、上周同期：基线缺失 → NoData（`sampleDays` 不参与判定）。
    DirectBaseline,
    /// 近 7 日均值：有效样本 < min_samples → InsufficientSamples（即使基线为 None
    /// 也归因样本不足，而不是 NoData）。
    HistoricalAverage { min_samples: i32 },
}

/// 整数百分比：`(current - baseline) / baseline * 100`，i128 中间值防溢出，
/// 四舍五入到最近整数，结果钳制到 i32 可表示范围（11 阶段一 1.2）。
/// 要求 baseline > 0（零基线由调用方按 §4.1 分支处理）。
pub fn rounded_percent_delta(current_ms: i64, baseline_ms: i64) -> i32 {
    let diff = i128::from(current_ms) - i128::from(baseline_ms);
    let base = i128::from(baseline_ms);
    debug_assert!(base > 0, "rounded_percent_delta 要求基线 > 0");
    if base <= 0 {
        return 0;
    }
    let scaled = diff * 100;
    let half = base / 2;
    let rounded = if scaled >= 0 {
        (scaled + half) / base
    } else {
        (scaled - half) / base
    };
    rounded.clamp(i128::from(i32::MIN), i128::from(i32::MAX)) as i32
}

/// 比较方向五态（10 §4.1 + 11 阶段零 P0-5）：
/// - 不可用归因由 `policy` 决定（DirectBaseline 缺失 → NoData；HistoricalAverage 样本不足 → InsufficientSamples）；
/// - 基线 = 0 且 current = 0 → Stable（deltaPercent = 0）；基线 = 0 且 current > 0 → UpFromZero（deltaPercent = null）；
/// - 基线 > 0：方向阈值用**精确比例**（i128 交叉相乘，|δ| > 5% 才 Up/Down，恰 5% 仍 Stable），
///   `deltaPercent` 单独四舍五入只用于显示。
#[must_use]
pub fn compare_direction(
    current_ms: i64,
    baseline_ms: Option<i64>,
    sample_days: i32,
    policy: ComparisonPolicy,
) -> (ComparisonDirection, Option<i32>, Option<UnavailableReason>) {
    match policy {
        ComparisonPolicy::DirectBaseline => {
            let Some(baseline) = baseline_ms else {
                return (
                    ComparisonDirection::Unavailable,
                    None,
                    Some(UnavailableReason::NoData),
                );
            };
            direction_with_baseline(current_ms, baseline)
        }
        ComparisonPolicy::HistoricalAverage { min_samples } => {
            if sample_days < min_samples {
                return (
                    ComparisonDirection::Unavailable,
                    None,
                    Some(UnavailableReason::InsufficientSamples),
                );
            }
            let Some(baseline) = baseline_ms else {
                // 近 7 日均值场景下基线缺失（样本充足而组装未产出均值，正常组装
                // 不可达）：按方案 11 §1.2 字面读法统一归因 InsufficientSamples，
                // 与"该场景唯一不可用原因是样本不足"一致（阶段三联调时以组装层
                // 不变式兜底，此处不产生 NoData 双轨）。
                return (
                    ComparisonDirection::Unavailable,
                    None,
                    Some(UnavailableReason::InsufficientSamples),
                );
            };
            direction_with_baseline(current_ms, baseline)
        }
    }
}

/// 基线已确认存在时的方向判定（基线 ≥ 0）。
fn direction_with_baseline(
    current_ms: i64,
    baseline_ms: i64,
) -> (ComparisonDirection, Option<i32>, Option<UnavailableReason>) {
    if baseline_ms == 0 {
        if current_ms == 0 {
            (ComparisonDirection::Stable, Some(0), None)
        } else {
            (ComparisonDirection::UpFromZero, None, None)
        }
    } else {
        let diff = i128::from(current_ms) - i128::from(baseline_ms);
        let base = i128::from(baseline_ms);
        let exceeds_threshold = diff.abs() * 100 > base * 5;
        let direction = if exceeds_threshold {
            if diff > 0 {
                ComparisonDirection::Up
            } else {
                ComparisonDirection::Down
            }
        } else {
            ComparisonDirection::Stable
        };
        (
            direction,
            Some(rounded_percent_delta(current_ms, baseline_ms)),
            None,
        )
    }
}

/// 摘要方向五档（10 §5.3）：任一窗口有效日 < 3 由调用方在取日均值前判定（本函数只收
/// 有效日均值，两参均为 None → None）；零基线适用 §4.1；阈值用**精确比例**（i128）：
/// δ > +10% → Up；+5% < δ ≤ +10% → UpSlight；|δ| ≤ 5% → Flat；对称 Down/DownSlight。
#[must_use]
pub fn summary_direction(
    recent_avg: Option<i64>,
    prior_avg: Option<i64>,
) -> Option<SummaryDirection> {
    let recent = recent_avg?;
    let prior = prior_avg?;
    if prior == 0 {
        return Some(if recent == 0 {
            SummaryDirection::Flat
        } else {
            SummaryDirection::Up
        });
    }
    let delta = i128::from(recent) - i128::from(prior);
    let base = i128::from(prior);
    let scaled = delta * 100;
    if scaled > base * 10 {
        Some(SummaryDirection::Up)
    } else if scaled > base * 5 {
        Some(SummaryDirection::UpSlight)
    } else if -scaled > base * 10 {
        Some(SummaryDirection::Down)
    } else if -scaled > base * 5 {
        Some(SummaryDirection::DownSlight)
    } else {
        Some(SummaryDirection::Flat)
    }
}

/// 时段映射（10 §5.3）：[6,12) → morning，[12,18) → afternoon，[18,24) → evening，[0,6) → night。
#[must_use]
pub fn period_of_hour(hour: u32) -> PeriodKind {
    match hour {
        6..=11 => PeriodKind::Morning,
        12..=17 => PeriodKind::Afternoon,
        18..=23 => PeriodKind::Evening,
        _ => PeriodKind::Night,
    }
}

/// 摘要组装（10 §5.3）：reliability 为 null 或峰值小时缺失（含全零曲线）时
/// primaryPeriod = null；direction 原样透传。
#[must_use]
pub fn build_summary(
    direction: Option<SummaryDirection>,
    peak_hour: Option<u32>,
    reliability: Option<ReliabilityKind>,
) -> SummaryDto {
    let primary_period = match (reliability, peak_hour) {
        (Some(_), Some(hour)) => Some(period_of_hour(hour)),
        _ => None,
    };
    SummaryDto {
        direction,
        primary_period,
    }
}

/// 7 日滑动均线（10 §4.2）：从 idx 向前 7 个自然日窗口，仅完整历史日
/// （hasData=true 且 !isToday）计入；有效点 < 3 或该点本身是今日 → (None, count)。
/// `points` 为升序、连续的每日序列（Reader 骨架补齐后保证连续性）。
/// 返回 `(均值, 窗口内有效完整日数)`；均值供上层包装为 Int64String。
#[must_use]
pub fn compute_moving_avg7(points: &[DailyMetricSample], idx: usize) -> (Option<i64>, i32) {
    if points.is_empty() {
        return (None, 0);
    }
    let idx = idx.min(points.len() - 1);
    let start = idx.saturating_sub(6);
    let window = &points[start..=idx];
    let mut sum: i64 = 0;
    let mut count: i32 = 0;
    for point in window {
        if point.has_data && !point.is_today {
            sum = sum.saturating_add(point.active_duration_ms);
            count += 1;
        }
    }
    if points[idx].is_today || count < 3 {
        (None, count)
    } else {
        (Some(sum / i64::from(count)), count)
    }
}

/// 惯性派生（10 §4.4 + 11 阶段一 1.2）：
/// - `profile` 为 24 个整点（localHour 0-23）的每小时均值，已由 Query 层统一除以 effectiveDays；
/// - **reliability = null（有效日 < 3）时派生字段全部 null**（10 §4.4/§9 P0-8），即使曲线非零；
/// - 24 小时全零 → 派生字段同样全部 null（reliability 仍按 effectiveDays 取值）；
/// - peakHour = argmax（并列取最早）；startHour/endHour = 含峰值的连续活跃段
///   （环形，跨午夜相连）：从峰值向两端找 v*100 < peak*30 的断点，开工 = 峰值前最近
///   断点的下一小时、收工 = 峰值后最近断点的前一小时（实现纠错 2026-08-04：原线性
///   "首个/最后一个 ≥30%"把凌晨熬夜尾巴当开工、分离次段计入收工——改为主时段语义，
///   熬夜 20:00→次日 3:00 得到开工 20 / 收工 3；全圈活跃退化到开工=收工=peak）；
/// - lunchLowestHour：候选小时**固定 {12,13,14}**（11 阶段一 1.2），区间内最小值且**严格低于** 11 点与 15 点
///   均值（整数比较 `min*2 < hour11 + hour15`），否则 null；并列取最早。
#[must_use]
pub fn derive_inertia(profile: &[i64; 24], effective_days: u32) -> InertiaDto {
    let reliability = match effective_days {
        0..=2 => None,
        3..=6 => Some(ReliabilityKind::Preliminary),
        _ => Some(ReliabilityKind::Normal),
    };
    // 10 §4.4/§9 P0-8：有效日 < 3（reliability = null）时，即使曲线非零也一律
    // 返回全 null 派生字段——只记录 1-2 天（真实可达）不得伪造开工/高峰/收工/午休。
    // 与"24 小时全零"（peak ≤ 0）同等待遇。total_days 窗口固定 14（10 §4.4；
    // 未来窗口可配置时需改签名，v0.1 不预留）。
    let peak = *profile.iter().max().unwrap_or(&0);
    if reliability.is_none() || peak <= 0 {
        return InertiaDto {
            start_hour: None,
            peak_hour: None,
            end_hour: None,
            lunch_lowest_hour: None,
            effective_days: effective_days as i32,
            total_days: 14,
            reliability,
        };
    }
    let peak_hour = profile.iter().position(|v| *v == peak).unwrap_or(0);
    let threshold = i128::from(peak) * 30;
    // 开工/收工 = 含峰值的连续活跃段（环形，跨午夜相连）：从峰值向两端找
    // fraction < 30%×peak 的断点；开工 = 峰值前最近断点的下一小时，收工 =
    // 峰值后最近断点的前一小时。修复：凌晨 0-5 点的"熬夜尾巴"不再被当成开工
    // （熬夜 20:00→次日 3:00 的曲线在环形上与晚上段连成 [20..3]，开工 20、
    // 收工 3）；全圈活跃（防御）退化到开工=收工=peak。
    let start_hour = {
        let mut i = (peak_hour + 23) % 24; // peak 前一小时
        let mut start = peak_hour as i32;
        for _ in 0..24 {
            if i128::from(profile[i]) * 100 < threshold {
                start = ((i + 1) % 24) as i32;
                break;
            }
            i = (i + 23) % 24;
        }
        start
    };
    let end_hour = {
        let mut i = (peak_hour + 1) % 24; // peak 后一小时
        let mut end = peak_hour as i32;
        for _ in 0..24 {
            if i128::from(profile[i]) * 100 < threshold {
                end = ((i + 23) % 24) as i32; // 断点前一小时
                break;
            }
            i = (i + 1) % 24;
        }
        end
    };
    let lunch_min = [profile[12], profile[13], profile[14]]
        .into_iter()
        .min()
        .unwrap_or(0);
    let boundary = i128::from(profile[11]) + i128::from(profile[15]);
    // 午休低谷前置条件：上午（11 点）必须有实质活跃——午休是"工作中间的休息"；
    // 若 11 点就为 0（上午未开工），12-14 点的低谷是"还没开始工作"而非午休，
    // 不得标注（修复：真实数据 12 点活跃≈0 但上午未工作 → 此前误标"午休低谷 12 点"）。
    let lunch_lowest_hour = if profile[11] > 0 && i128::from(lunch_min) * 2 < boundary {
        (12..=14).find(|h| profile[*h as usize] == lunch_min)
    } else {
        None
    };
    InertiaDto {
        start_hour: Some(start_hour),
        peak_hour: Some(peak_hour as i32),
        end_hour: Some(end_hour),
        lunch_lowest_hour,
        effective_days: effective_days as i32,
        total_days: 14,
        reliability,
    }
}

/// 单个有效日的 Work Block 覆盖（v0.2 候选：工作节奏）。段为 0..1440 分钟粒度
/// 半开区间，升序、不重叠；可为空（当天有记录但无任何工作块 → 全天未工作）。
/// 由 Reader 按 reporting time zone 把 UTC 块裁剪/归位后构造，本模块只做纯计算。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DayCoverage {
    pub segments: Vec<(u32, u32)>,
}

/// 上中位数（对个别天的异常作息稳健；空输入返回 0，调用方保证非空）。
fn median_u32(vals: &[u32]) -> u32 {
    if vals.is_empty() {
        return 0;
    }
    let mut sorted = vals.to_vec();
    sorted.sort_unstable();
    sorted[sorted.len() / 2]
}

/// 工作节奏纯函数（v0.2 候选：惯性卡片融合，10 §4.4 之外新增）：
/// - hourly_coverage_ms：每有效日 24 点在工位覆盖毫秒均值（与惯性同轴不同口径，
///   熬夜尾巴映射回钟点——凌晨在工位如实反映）；
/// - work_ratio_percent：工作覆盖均值 / 1440 分钟（覆盖 + 补集 = 24h，占比自洽，
///   跨午夜熬夜全程计入开始日）；
/// - common_start/end：常见开工/收工 = 当天窗口内真实覆盖段（熬夜尾巴不参与
///   开工；收工截到 24:00）的钟点中位数——避免熬夜映射把"凌晨开工"/"27:05 开工"
///   式伪值拉进中位数；
/// - morning_work_days：8:00-12:00 有覆盖钟点的有效日数（回答"缺上午"）；
/// - reliability 与惯性同门禁：有效日 < 3 → null，全部派生字段返回零/空。
#[must_use]
pub fn derive_work_pace(days: &[DayCoverage], total_days: u32) -> WorkPaceDto {
    let effective_days = days.len() as u32;
    let reliability = match effective_days {
        0..=2 => None,
        3..=6 => Some(ReliabilityKind::Preliminary),
        _ => Some(ReliabilityKind::Normal),
    };
    let mut per_hour: [i64; 24] = [0; 24];
    let mut first_works: Vec<u32> = Vec::new();
    let mut last_works: Vec<u32> = Vec::new();
    let mut morning_work_days: i32 = 0;
    let mut total_coverage_ms: i64 = 0;
    for day in days {
        // 工作钟点位图：段内 < 1440 部分原样标记；熬夜尾巴（≥ 1440）映射回 0..1440
        // 钟点（熬夜 00:00-03:00 与任何"当天凌晨工作"同样都是"凌晨在工位"）。
        let mut cov = [false; 1440];
        let mut day_first: Option<u32> = None; // 窗口内首个真实覆盖起点（s < 1440）
        let mut day_last: u32 = 0; // 窗口内最后覆盖终点（≤ 1440）
        for &(s, e) in &day.segments {
            let end = e.min(2880);
            if end <= s {
                continue;
            }
            total_coverage_ms += i64::from(end - s) * 60_000;
            if s < 1440 {
                if day_first.is_none() {
                    day_first = Some(s);
                }
                day_last = day_last.max(end.min(1440));
            }
            for m in s..end.min(1440) {
                cov[m as usize] = true;
            }
            for m in 1440..end {
                cov[(m - 1440) as usize] = true;
            }
        }
        if let Some(first) = day_first {
            first_works.push(first);
        }
        if day_last > 0 {
            last_works.push(day_last);
        }
        if cov[480..720].iter().any(|v| *v) {
            morning_work_days += 1;
        }
        for (h, slot) in per_hour.iter_mut().enumerate() {
            for minute in &cov[(h * 60)..(h * 60 + 60)] {
                if *minute {
                    *slot += 60_000;
                }
            }
        }
    }
    let (common_start_minutes, common_end_minutes) = if effective_days > 0 {
        (
            (!first_works.is_empty()).then(|| median_u32(&first_works) as i32),
            (!last_works.is_empty()).then(|| median_u32(&last_works) as i32),
        )
    } else {
        (None, None)
    };
    let work_ratio_percent = if effective_days > 0 {
        let mean_ms = total_coverage_ms / i64::from(effective_days);
        let ratio = (i128::from(mean_ms) * 100 + 43_200_000) / 86_400_000;
        ratio as i32
    } else {
        0
    };
    let hourly_coverage_ms: Vec<CoveragePointDto> = (0..24)
        .map(|h| CoveragePointDto {
            local_hour: h,
            avg_coverage_ms: crate::dto::Int64String(
                per_hour[h as usize] / i64::from(effective_days.max(1)),
            ),
        })
        .collect();
    WorkPaceDto {
        hourly_coverage_ms,
        work_ratio_percent,
        common_start_minutes,
        common_end_minutes,
        morning_work_days,
        effective_days: effective_days as i32,
        total_days: total_days as i32,
        reliability,
    }
}

/// 最长连续记录天数（10 §5.1 里程碑）。输入为升序、去重的 YYYY-MM-DD 列表
/// （Reader `stats_recorded_dates` 保证；重复/乱序输入不在合同内）。
#[must_use]
pub fn longest_consecutive(dates: &[String]) -> i64 {
    let mut best = 0_i64;
    let mut run = 0_i64;
    let mut prev: Option<i64> = None;
    for raw in dates {
        let Some((year, month, day)) = parse_ymd(raw) else {
            continue;
        };
        let day_no = days_from_civil(year, month, day);
        run = if prev.is_some_and(|p| day_no == p + 1) {
            run + 1
        } else {
            1
        };
        best = best.max(run);
        prev = Some(day_no);
    }
    best
}

fn parse_ymd(raw: &str) -> Option<(i64, u32, u32)> {
    let parts: Vec<&str> = raw.split('-').collect();
    if parts.len() != 3 {
        return None;
    }
    let year: i64 = parts[0].parse().ok()?;
    let month: u32 = parts[1].parse().ok()?;
    let day: u32 = parts[2].parse().ok()?;
    if !(1..=12).contains(&month) || !(1..=31).contains(&day) {
        return None;
    }
    Some((year, month, day))
}

/// 公历日期 → 自 1970-01-01 的天数（Hinnant days-from-civil 算法；不依赖时间库）。
fn days_from_civil(year: i64, month: u32, day: u32) -> i64 {
    let y = if month <= 2 { year - 1 } else { year };
    let era = y.div_euclid(400);
    let yoe = (y - era * 400) as u64; // [0, 399]
    let mp = (u64::from(month) + 9) % 12; // [0, 11]
    let doy = (153 * mp + 2) / 5 + u64::from(day) - 1;
    let doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    era * 146_097 + doe as i64 - 719_468
}

/// days 规范化（10 §5.4）：7|14|30 → 7|14|30；缺失或非法 → 14。
#[must_use]
pub fn normalize_days(days: Option<i32>) -> u32 {
    match days {
        Some(7) => 7,
        Some(14) => 14,
        Some(30) => 30,
        _ => 14,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // ---- rounded_percent_delta ----

    #[test]
    fn percent_rounding_positive_and_negative() {
        assert_eq!(rounded_percent_delta(110, 100), 10);
        assert_eq!(rounded_percent_delta(105, 100), 5);
        assert_eq!(rounded_percent_delta(104, 100), 4);
        // 正负四舍五入对称：5.4% → 5；-5.4% → -5
        assert_eq!(rounded_percent_delta(1054, 1000), 5);
        assert_eq!(rounded_percent_delta(1000, 1054), -5);
        // 5.5% 向远离零舍入；5.6% 正常进位；-5.30% 舍为 -5
        assert_eq!(rounded_percent_delta(1055, 1000), 6);
        assert_eq!(rounded_percent_delta(1056, 1000), 6);
        assert_eq!(rounded_percent_delta(1000, 1056), -5); // -56/1056 = -5.30% → -5
        assert_eq!(rounded_percent_delta(944, 1000), -6); // -5.6% → -6
        // 半值边界
        assert_eq!(rounded_percent_delta(150, 100), 50);
        assert_eq!(rounded_percent_delta(149, 100), 49);
        assert_eq!(rounded_percent_delta(100, 150), -33); // -33.3% → -33
        // 大变化
        assert_eq!(rounded_percent_delta(0, 100), -100);
    }

    #[test]
    fn percent_i64_boundary_clamps_to_i32() {
        assert_eq!(rounded_percent_delta(i64::MAX, 1), i32::MAX);
        assert_eq!(rounded_percent_delta(i64::MIN, 1), i32::MIN);
        // 基线巨大、差值小的情形不溢出
        assert_eq!(rounded_percent_delta(i64::MAX, i64::MAX), 0);
    }

    // ---- compare_direction ----

    #[test]
    fn policy_decides_unavailable_reason_order_independently() {
        // 相同输入（baseline None、sampleDays 0）在不同策略下归因不同：
        // DirectBaseline → NoData；HistoricalAverage(min=1) → InsufficientSamples。
        let (dir, delta, reason) =
            compare_direction(100, None, 0, ComparisonPolicy::DirectBaseline);
        assert_eq!(dir, ComparisonDirection::Unavailable);
        assert_eq!(delta, None);
        assert_eq!(reason, Some(UnavailableReason::NoData));

        let (dir, delta, reason) = compare_direction(
            100,
            None,
            0,
            ComparisonPolicy::HistoricalAverage { min_samples: 1 },
        );
        assert_eq!(dir, ComparisonDirection::Unavailable);
        assert_eq!(delta, None);
        assert_eq!(reason, Some(UnavailableReason::InsufficientSamples));
    }

    #[test]
    fn historical_average_insufficient_samples_even_with_baseline() {
        // 近 7 日只有 2 个样本：即使 baseline 已算出也要归因样本不足。
        let (dir, delta, reason) = compare_direction(
            1_000,
            Some(800),
            2,
            ComparisonPolicy::HistoricalAverage { min_samples: 3 },
        );
        assert_eq!(dir, ComparisonDirection::Unavailable);
        assert_eq!(delta, None);
        assert_eq!(reason, Some(UnavailableReason::InsufficientSamples));
    }

    #[test]
    fn historical_average_baseline_none_attributes_insufficient_samples() {
        // 近 7 日均值场景下基线缺失（样本充足而组装未产出均值，正常不可达的防御
        // 分支）：按方案 11 §1.2 统一归因 InsufficientSamples，与场景唯一不可用
        // 原因一致（NoData 只属于 DirectBaseline 场景）。
        let (dir, delta, reason) = compare_direction(
            1_000,
            None,
            5,
            ComparisonPolicy::HistoricalAverage { min_samples: 3 },
        );
        assert_eq!(dir, ComparisonDirection::Unavailable);
        assert_eq!(delta, None);
        assert_eq!(reason, Some(UnavailableReason::InsufficientSamples));
    }

    #[test]
    fn zero_baseline_rules() {
        // 基线 = 0 且当前 = 0 → Stable, deltaPercent = 0
        let (dir, delta, reason) =
            compare_direction(0, Some(0), 1, ComparisonPolicy::DirectBaseline);
        assert_eq!(dir, ComparisonDirection::Stable);
        assert_eq!(delta, Some(0));
        assert_eq!(reason, None);
        // 基线 = 0 且当前 > 0 → UpFromZero, deltaPercent = null（禁止伪造百分比）
        let (dir, delta, reason) =
            compare_direction(720_000, Some(0), 1, ComparisonPolicy::DirectBaseline);
        assert_eq!(dir, ComparisonDirection::UpFromZero);
        assert_eq!(delta, None);
        assert_eq!(reason, None);
    }

    #[test]
    fn threshold_uses_exact_ratio_not_rounded_display() {
        // 实际 +5.4% → 方向 Up（精确比例），显示 deltaPercent 为 5（舍入只用于显示）
        let (dir, delta, reason) =
            compare_direction(10_540, Some(10_000), 1, ComparisonPolicy::DirectBaseline);
        assert_eq!(dir, ComparisonDirection::Up);
        assert_eq!(delta, Some(5));
        assert_eq!(reason, None);
        // 实际 -5.4% → Down，显示 -5
        let (dir, delta, _) =
            compare_direction(10_000, Some(10_540), 1, ComparisonPolicy::DirectBaseline);
        assert_eq!(dir, ComparisonDirection::Down);
        assert_eq!(delta, Some(-5));
        // 恰 5% → Stable（阈值是严格大于）
        let (dir, delta, _) =
            compare_direction(10_500, Some(10_000), 1, ComparisonPolicy::DirectBaseline);
        assert_eq!(dir, ComparisonDirection::Stable);
        assert_eq!(delta, Some(5));
        // 恰 -5% → Stable
        let (dir, _, _) =
            compare_direction(10_000, Some(10_526), 1, ComparisonPolicy::DirectBaseline);
        assert_eq!(dir, ComparisonDirection::Stable);
    }

    #[test]
    fn direct_baseline_normal_calculation_and_zero_current() {
        // 正常上升
        let (dir, delta, reason) =
            compare_direction(11_000, Some(10_000), 1, ComparisonPolicy::DirectBaseline);
        assert_eq!(dir, ComparisonDirection::Up);
        assert_eq!(delta, Some(10));
        assert_eq!(reason, None);
        // 当前为 0、基线 > 0 → Down -100%
        let (dir, delta, _) =
            compare_direction(0, Some(10_000), 1, ComparisonPolicy::DirectBaseline);
        assert_eq!(dir, ComparisonDirection::Down);
        assert_eq!(delta, Some(-100));
        // HistoricalAverage 样本充足时正常计算
        let (dir, _, reason) = compare_direction(
            11_000,
            Some(10_000),
            5,
            ComparisonPolicy::HistoricalAverage { min_samples: 3 },
        );
        assert_eq!(dir, ComparisonDirection::Up);
        assert_eq!(reason, None);
    }

    // ---- summary_direction ----

    #[test]
    fn summary_boundaries_use_exact_tiers() {
        // 恰 5% → Flat；恰 10% → UpSlight；10.1% → Up
        assert_eq!(
            summary_direction(Some(10_500), Some(10_000)),
            Some(SummaryDirection::Flat)
        );
        assert_eq!(
            summary_direction(Some(11_000), Some(10_000)),
            Some(SummaryDirection::UpSlight)
        );
        assert_eq!(
            summary_direction(Some(11_010), Some(10_000)),
            Some(SummaryDirection::Up)
        );
        // 对称下降
        assert_eq!(
            summary_direction(Some(10_000), Some(10_500)),
            Some(SummaryDirection::Flat)
        );
        assert_eq!(
            summary_direction(Some(10_000), Some(11_000)),
            Some(SummaryDirection::DownSlight)
        );
        assert_eq!(
            summary_direction(Some(10_000), Some(11_200)), // -1200/11200 = -10.71% → Down
            Some(SummaryDirection::Down)
        );
        // 5.1% → UpSlight（精确比例）
        assert_eq!(
            summary_direction(Some(10_510), Some(10_000)),
            Some(SummaryDirection::UpSlight)
        );
        // 持平
        assert_eq!(
            summary_direction(Some(10_000), Some(10_000)),
            Some(SummaryDirection::Flat)
        );
    }

    #[test]
    fn summary_zero_baseline_and_missing_windows() {
        // 任一窗口缺失 → None
        assert_eq!(summary_direction(None, Some(10_000)), None);
        assert_eq!(summary_direction(Some(10_000), None), None);
        assert_eq!(summary_direction(None, None), None);
        // 零基线：前窗口日均 0 且后窗口 > 0 → Up；均为 0 → Flat
        assert_eq!(
            summary_direction(Some(10_000), Some(0)),
            Some(SummaryDirection::Up)
        );
        assert_eq!(
            summary_direction(Some(0), Some(0)),
            Some(SummaryDirection::Flat)
        );
    }

    // ---- period_of_hour / build_summary ----

    #[test]
    fn period_boundaries() {
        assert_eq!(period_of_hour(0), PeriodKind::Night);
        assert_eq!(period_of_hour(5), PeriodKind::Night);
        assert_eq!(period_of_hour(6), PeriodKind::Morning);
        assert_eq!(period_of_hour(11), PeriodKind::Morning);
        assert_eq!(period_of_hour(12), PeriodKind::Afternoon);
        assert_eq!(period_of_hour(17), PeriodKind::Afternoon);
        assert_eq!(period_of_hour(18), PeriodKind::Evening);
        assert_eq!(period_of_hour(23), PeriodKind::Evening);
    }

    #[test]
    fn summary_assembly_rules() {
        let s = build_summary(
            Some(SummaryDirection::UpSlight),
            Some(10),
            Some(ReliabilityKind::Normal),
        );
        assert_eq!(s.direction, Some(SummaryDirection::UpSlight));
        assert_eq!(s.primary_period, Some(PeriodKind::Morning));
        // reliability 为 null → primaryPeriod null（有效日 < 3）
        let s = build_summary(Some(SummaryDirection::Up), Some(10), None);
        assert_eq!(s.primary_period, None);
        // 峰值小时缺失（全零曲线）→ primaryPeriod null
        let s = build_summary(
            Some(SummaryDirection::Up),
            None,
            Some(ReliabilityKind::Normal),
        );
        assert_eq!(s.primary_period, None);
        // direction 原样透传
        let s = build_summary(None, None, None);
        assert_eq!(s.direction, None);
        assert_eq!(s.primary_period, None);
    }

    // ---- compute_moving_avg7 ----

    fn sample(ms: i64, has_data: bool, is_today: bool) -> DailyMetricSample {
        DailyMetricSample {
            active_duration_ms: ms,
            has_data,
            is_today,
        }
    }

    #[test]
    fn moving_avg_window_and_validity() {
        // 7 个完整历史日：均值为 7 天平均
        let points: Vec<_> = (1..=7).map(|d| sample(d * 1000, true, false)).collect();
        let (avg, count) = compute_moving_avg7(&points, 6);
        assert_eq!(avg, Some(4000));
        assert_eq!(count, 7);
        // 窗口从 idx 向前 7 个自然日：idx=0 只有 1 个点 → 有效 < 3 → None
        let (avg, count) = compute_moving_avg7(&points, 0);
        assert_eq!(avg, None);
        assert_eq!(count, 1);
        // hasData=false 不计入；3 个有效点刚好满足 ≥3 → 有均值
        let points = vec![
            sample(1000, true, false),
            sample(0, false, false),
            sample(3000, true, false),
            sample(4000, true, false),
        ];
        let (avg, count) = compute_moving_avg7(&points, 3);
        assert_eq!(avg, Some(2666)); // (1000+3000+4000)/3
        assert_eq!(count, 3);
    }

    #[test]
    fn moving_avg_today_is_null() {
        // 今日点 → 恒 null（今日不进入均线；契约：今日 movingAvg7ActiveMs = null）
        let points = vec![
            sample(1000, true, false),
            sample(2000, true, false),
            sample(3000, true, false),
            sample(4000, true, false),
            sample(5000, true, true), // today
        ];
        let (avg, count) = compute_moving_avg7(&points, 4);
        assert_eq!(avg, None);
        assert_eq!(count, 4);
        // 空输入
        assert_eq!(compute_moving_avg7(&[], 0), (None, 0));
    }

    // ---- derive_inertia ----

    fn zeros24() -> [i64; 24] {
        [0; 24]
    }

    #[test]
    fn inertia_all_zero_derives_nothing() {
        let inertia = derive_inertia(&zeros24(), 11);
        assert_eq!(inertia.start_hour, None);
        assert_eq!(inertia.peak_hour, None);
        assert_eq!(inertia.end_hour, None);
        assert_eq!(inertia.lunch_lowest_hour, None);
        assert_eq!(inertia.effective_days, 11);
        assert_eq!(inertia.total_days, 14);
        assert_eq!(inertia.reliability, Some(ReliabilityKind::Normal));
    }

    #[test]
    fn inertia_reliability_tiers() {
        assert_eq!(derive_inertia(&zeros24(), 0).reliability, None);
        assert_eq!(derive_inertia(&zeros24(), 2).reliability, None);
        assert_eq!(
            derive_inertia(&zeros24(), 3).reliability,
            Some(ReliabilityKind::Preliminary)
        );
        assert_eq!(
            derive_inertia(&zeros24(), 6).reliability,
            Some(ReliabilityKind::Preliminary)
        );
        assert_eq!(
            derive_inertia(&zeros24(), 7).reliability,
            Some(ReliabilityKind::Normal)
        );
    }

    #[test]
    fn inertia_reliability_null_forces_all_derived_null_even_with_nonzero_curve() {
        // 10 §4.4/§9 P0-8 回归：只记录 1-2 天（reliability = null）且曲线非零时，
        // 派生字段（start/peak/end/lunch）必须全部 null，不得输出峰值/开工/收工/午休。
        let mut profile = zeros24();
        profile[9] = 40;
        profile[10] = 100;
        profile[12] = 10;
        profile[19] = 35;
        for days in [0_u32, 1, 2] {
            let inertia = derive_inertia(&profile, days);
            assert_eq!(inertia.reliability, None, "effective_days={days}");
            assert_eq!(inertia.start_hour, None);
            assert_eq!(inertia.peak_hour, None);
            assert_eq!(inertia.end_hour, None);
            assert_eq!(inertia.lunch_lowest_hour, None);
            assert_eq!(inertia.effective_days, days as i32);
            assert_eq!(inertia.total_days, 14);
        }
    }

    #[test]
    fn inertia_preliminary_keeps_derived_values() {
        // 有效日 = 3（Preliminary）时非零曲线可正常派生标注。
        let mut profile = zeros24();
        profile[9] = 40;
        profile[10] = 100;
        profile[19] = 35;
        let inertia = derive_inertia(&profile, 3);
        assert_eq!(inertia.reliability, Some(ReliabilityKind::Preliminary));
        assert_eq!(inertia.peak_hour, Some(10));
        assert_eq!(inertia.start_hour, Some(9));
        // 环形主时段：峰值 10 点向后 11 点（25 < 30）断 → 收工 10；
        // 19 点 35 是分离次段，不再计入收工（修复后语义：收工 = 主时段结束）。
        assert_eq!(inertia.end_hour, Some(10));
    }

    #[test]
    fn inertia_peak_start_end_rules() {
        let mut profile = zeros24();
        // 峰值 100 出现在 10 点；9 点 40（40%）、11 点 25（25%）；19 点 35（35%）、20 点 20
        profile[9] = 40;
        profile[10] = 100;
        profile[11] = 25;
        profile[19] = 35;
        profile[20] = 20;
        let inertia = derive_inertia(&profile, 11);
        assert_eq!(inertia.peak_hour, Some(10));
        assert_eq!(inertia.start_hour, Some(9)); // 主时段首个 ≥ 30%×100
        // 环形主时段：11 点（25 < 30）断 → 收工 10；19 点 35 分离次段不再计入。
        assert_eq!(inertia.end_hour, Some(10));
        // 12/13/14 全零而 11 点有值：0 严格低于 (25+0)/2 → 判为午休低谷（候选小时写死 {12,13,14}）
        assert_eq!(inertia.lunch_lowest_hour, Some(12));
    }

    #[test]
    fn inertia_cross_midnight_main_window() {
        // 熬夜 20:00→次日 3:00：晚上 20-23 高活跃（峰值 22）、凌晨 0-3 活跃（≥30%）。
        // 环形主时段把 20..23 与 0..3 连成一段 → 开工 20、收工 3（修复前：开工 0、收工 23）。
        let mut profile = zeros24();
        profile[20] = 80;
        profile[21] = 90;
        profile[22] = 100; // 峰值
        profile[23] = 70;
        profile[0] = 50;
        profile[1] = 45;
        profile[2] = 40;
        profile[3] = 35;
        let inertia = derive_inertia(&profile, 11);
        assert_eq!(inertia.peak_hour, Some(22));
        assert_eq!(
            inertia.start_hour,
            Some(20),
            "熬夜开工应为 20 点，凌晨尾巴不再当开工"
        );
        assert_eq!(inertia.end_hour, Some(3), "熬夜收工应跨午夜到凌晨 3 点");
    }

    #[test]
    fn inertia_peak_tie_takes_earliest() {
        let mut profile = zeros24();
        profile[8] = 50;
        profile[9] = 50; // 并列峰值
        profile[7] = 30;
        let inertia = derive_inertia(&profile, 11);
        assert_eq!(inertia.peak_hour, Some(8));
        assert_eq!(inertia.start_hour, Some(7)); // 30%×50=15 → 7 点 30 ≥ 15
        assert_eq!(inertia.end_hour, Some(9));
    }

    #[test]
    fn inertia_lunch_candidates_fixed_12_13_14() {
        // 真实午休：11 点 80、12 点 20、13 点 10、14 点 20、15 点 70 → 低谷 13
        let mut profile = zeros24();
        profile[11] = 80;
        profile[12] = 20;
        profile[13] = 10;
        profile[14] = 20;
        profile[15] = 70;
        let inertia = derive_inertia(&profile, 11);
        assert_eq!(inertia.lunch_lowest_hour, Some(13));
        // 12/13/14 并列最小 → 取最早（12）
        let mut profile = zeros24();
        profile[11] = 60;
        profile[12] = 20;
        profile[13] = 20;
        profile[14] = 20;
        profile[15] = 50;
        let inertia = derive_inertia(&profile, 11);
        assert_eq!(inertia.lunch_lowest_hour, Some(12));
        // 候选只取 {12,13,14}：15 点更低也不参与候选，候选仍可判为低谷
        let mut profile = zeros24();
        profile[11] = 60;
        profile[12] = 30;
        profile[13] = 30;
        profile[14] = 30;
        profile[15] = 10;
        let inertia = derive_inertia(&profile, 11);
        assert_eq!(inertia.lunch_lowest_hour, Some(12)); // 30*2=60 < (60+10)/2*2=70 → 成立
    }

    #[test]
    fn inertia_lunch_requires_strictly_below_boundary() {
        // 午间与 11/15 点持平 → 不严格低于 → null
        let mut profile = zeros24();
        profile[11] = 20;
        profile[12] = 20;
        profile[13] = 20;
        profile[14] = 20;
        profile[15] = 20;
        let inertia = derive_inertia(&profile, 11);
        assert_eq!(inertia.lunch_lowest_hour, None);
        // 15 点低于候选 → 候选非真实局部低谷 → null
        let mut profile = zeros24();
        profile[11] = 50;
        profile[12] = 60;
        profile[13] = 55;
        profile[14] = 50;
        profile[15] = 40;
        let inertia = derive_inertia(&profile, 11);
        assert_eq!(inertia.lunch_lowest_hour, None);
    }

    #[test]
    fn inertia_thirty_percent_threshold_is_exact() {
        // 30% 阈值：峰值 30，阈值 9（30%×30 = 9）；某小时 8（26.7%）不计入
        let mut profile = zeros24();
        profile[8] = 8;
        profile[9] = 30;
        profile[10] = 9;
        let inertia = derive_inertia(&profile, 11);
        assert_eq!(inertia.start_hour, Some(9)); // 8 点 8 < 9 不计入
        assert_eq!(inertia.end_hour, Some(10)); // 10 点 9 ≥ 9 计入
    }

    // ---- longest_consecutive ----

    #[test]
    fn longest_consecutive_runs() {
        assert_eq!(longest_consecutive(&[]), 0);
        assert_eq!(longest_consecutive(&["2026-08-01".to_string()]), 1);
        let dates = [
            "2026-07-28".to_string(),
            "2026-07-29".to_string(),
            "2026-07-30".to_string(),
            "2026-08-01".to_string(),
            "2026-08-02".to_string(),
        ];
        assert_eq!(longest_consecutive(&dates), 3);
        // 跨月连续
        let dates = [
            "2026-07-31".to_string(),
            "2026-08-01".to_string(),
            "2026-08-02".to_string(),
        ];
        assert_eq!(longest_consecutive(&dates), 3);
        // 跨闰年（2024-02-28/29 + 03-01）
        let dates = [
            "2024-02-28".to_string(),
            "2024-02-29".to_string(),
            "2024-03-01".to_string(),
        ];
        assert_eq!(longest_consecutive(&dates), 3);
        // 非法日期行被跳过
        let dates = [
            "2026-08-01".to_string(),
            "bad".to_string(),
            "2026-08-02".to_string(),
        ];
        assert_eq!(longest_consecutive(&dates), 2);
    }

    #[test]
    fn days_from_civil_is_stable() {
        // 1970-01-01 → 0
        assert_eq!(days_from_civil(1970, 1, 1), 0);
        // 2000-03-01（闰年）与 2026-08-03 与 chrono 语义一致（用相邻日期验证连续性）
        assert_eq!(days_from_civil(2026, 8, 3) - days_from_civil(2026, 8, 2), 1);
        assert_eq!(
            days_from_civil(2024, 3, 1) - days_from_civil(2024, 2, 29),
            1
        );
        assert_eq!(
            days_from_civil(2023, 3, 1) - days_from_civil(2023, 2, 28),
            1
        );
    }

    // ---- normalize_days ----

    #[test]
    fn normalize_days_preserves_valid_and_defaults() {
        assert_eq!(normalize_days(Some(7)), 7);
        assert_eq!(normalize_days(Some(14)), 14);
        assert_eq!(normalize_days(Some(30)), 30);
        assert_eq!(normalize_days(None), 14);
        assert_eq!(normalize_days(Some(0)), 14);
        assert_eq!(normalize_days(Some(15)), 14);
    }

    // ---- derive_work_pace ----

    #[test]
    fn work_pace_reliability_gate_like_inertia() {
        // 0 天 → null；1-2 天 → null（不伪造）；3-6 → Preliminary；7+ → Normal。
        assert_eq!(derive_work_pace(&[], 14).reliability, None);
        let one = [DayCoverage {
            segments: vec![(540, 660)],
        }];
        assert_eq!(derive_work_pace(&one, 14).reliability, None);
        let three = [
            DayCoverage {
                segments: vec![(540, 660)],
            },
            DayCoverage {
                segments: vec![(540, 660)],
            },
            DayCoverage {
                segments: vec![(540, 660)],
            },
        ];
        assert_eq!(
            derive_work_pace(&three, 14).reliability,
            Some(ReliabilityKind::Preliminary)
        );
    }

    #[test]
    fn work_pace_ratio_and_hourly_cover_consistent() {
        // 每天 9:00-12:00 在工位（覆盖并集含短 idle）：180 分钟 / 1440 = 12.5% → 13%。
        let days = vec![
            DayCoverage {
                segments: vec![(540, 720)],
            };
            7
        ];
        let pace = derive_work_pace(&days, 14);
        assert_eq!(pace.reliability, Some(ReliabilityKind::Normal));
        assert_eq!(pace.effective_days, 7);
        assert_eq!(pace.total_days, 14);
        assert_eq!(pace.work_ratio_percent, 13);
        // 每小时覆盖：9 点 60 分钟、10/11 点各 60、12 点 0（12:00 是半开边界）。
        let hours: Vec<u32> = pace
            .hourly_coverage_ms
            .iter()
            .filter(|p| p.avg_coverage_ms.0 > 0)
            .map(|p| p.local_hour)
            .collect();
        assert_eq!(hours, vec![9, 10, 11]);
        for p in &pace.hourly_coverage_ms {
            if (9..=11).contains(&p.local_hour) {
                assert_eq!(p.avg_coverage_ms.0, 3_600_000);
            }
        }
    }

    #[test]
    fn work_pace_common_start_end_and_morning_days() {
        // 两天开工不同：12:23 与 16:18；收工 23:02 与 24:00。
        // 中位开工 = 16:18（排序取上中位数）、中位收工 = 23:02；
        // 上午（8-12 点）有覆盖的天数：无（两天开工都 ≥ 12:23）。
        let days = vec![
            DayCoverage {
                segments: vec![(743, 1382)],
            },
            DayCoverage {
                segments: vec![(978, 1440)],
            },
        ];
        let pace = derive_work_pace(&days, 14);
        assert_eq!(pace.common_start_minutes, Some(978));
        assert_eq!(pace.common_end_minutes, Some(1440)); // 上中位数 [1382,1440] → 1440
        assert_eq!(pace.morning_work_days, 0);
        // 有上午覆盖的天：段 [480, 720)（8-12 点）。
        let days2 = vec![DayCoverage {
            segments: vec![(500, 720)],
        }];
        let pace2 = derive_work_pace(&days2, 14);
        assert_eq!(pace2.morning_work_days, 1);
        assert_eq!(pace2.common_start_minutes, Some(500));
        assert_eq!(pace2.common_end_minutes, Some(720));
    }

    #[test]
    fn work_pace_cross_midnight_segment_counts_to_start_day() {
        // 熬夜块 22:00→次日 03:00 整体归属开始日：段 [1320, 1620)。
        // - 占比：5h / 24 = 21%；
        // - 工作钟点：22/23 点 + 熬夜映射 0/1/2 点（凌晨在工位）；
        // - 开工（窗口内真实覆盖首段）= 22:00；收工截到 24:00（熬夜尾巴不参与收工
        //   中位数，避免"27:05 收工"式伪值）；
        // - 上午无覆盖 → morning_work_days = 0。
        let days = vec![
            DayCoverage {
                segments: vec![(1320, 1620)],
            };
            7
        ];
        let pace = derive_work_pace(&days, 14);
        assert_eq!(pace.reliability, Some(ReliabilityKind::Normal));
        assert_eq!(pace.work_ratio_percent, 21);
        let covered: Vec<u32> = pace
            .hourly_coverage_ms
            .iter()
            .filter(|p| p.avg_coverage_ms.0 > 0)
            .map(|p| p.local_hour)
            .collect();
        // 22/23 点（段内）+ 0/1/2 点（熬夜映射）。
        assert_eq!(covered, vec![0, 1, 2, 22, 23]);
        for p in &pace.hourly_coverage_ms {
            if covered.contains(&p.local_hour) {
                assert_eq!(p.avg_coverage_ms.0, 3_600_000);
            }
        }
        assert_eq!(pace.common_start_minutes, Some(1320));
        assert_eq!(pace.common_end_minutes, Some(1440)); // 截到 24:00
        assert_eq!(pace.morning_work_days, 0);
    }

    #[test]
    fn inertia_lunch_suppressed_when_morning_has_no_work() {
        // 上午未开工（11 点为 0）：12-14 点低谷是"还没开始工作"而非午休 → 不标注。
        // 真实数据场景：12 点活跃≈0、11 点=0、15 点有活跃。
        let mut profile = zeros24();
        profile[12] = 20;
        profile[13] = 10;
        profile[14] = 15;
        profile[15] = 70;
        let inertia = derive_inertia(&profile, 11);
        assert_eq!(inertia.lunch_lowest_hour, None);
        // 对照组：11 点有活跃（上午在工作）→ 12-14 低谷是真实午休 → 标注。
        let mut profile = zeros24();
        profile[11] = 80;
        profile[12] = 20;
        profile[13] = 10;
        profile[14] = 15;
        profile[15] = 70;
        let inertia = derive_inertia(&profile, 11);
        assert_eq!(inertia.lunch_lowest_hour, Some(13));
    }
}
