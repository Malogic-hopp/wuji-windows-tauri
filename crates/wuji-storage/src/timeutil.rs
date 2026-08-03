//! 时间换算：UTC 毫秒 ↔ 固定 reporting time zone 的本地字段（09 §6.7）。
//!
//! 所有换算统一使用锁定版本的 chrono-tz（09 §6.7）；建库后时区不可修改。

use chrono::{NaiveDate, Offset, TimeZone, Timelike};
use chrono_tz::Tz;
use wuji_core::dto::LocalDate;

use crate::error::{Result, StorageError};

/// UTC 毫秒 → (local_date, local_hour, utc_offset_minutes)，即 hourly 桶的本地桶起点字段。
pub fn local_fields(tz: &Tz, utc_ms: i64) -> Result<(String, u32, i32)> {
    let utc = chrono::DateTime::from_timestamp_millis(utc_ms)
        .ok_or_else(|| StorageError::internal("时间戳越界"))?;
    let local = utc.with_timezone(tz);
    let offset_minutes = local.offset().fix().local_minus_utc() / 60;
    Ok((
        local.format("%Y-%m-%d").to_string(),
        local.hour(),
        offset_minutes,
    ))
}

/// UTC 毫秒落在哪个 local date（YYYY-MM-DD）。
pub fn local_date_of(tz: &Tz, utc_ms: i64) -> Result<String> {
    Ok(local_fields(tz, utc_ms)?.0)
}

/// 本地日期对应的 UTC 半开区间 [start, end)。
pub fn local_day_range_utc_ms(tz: &Tz, date: &LocalDate) -> Result<(i64, i64)> {
    let naive = NaiveDate::parse_from_str(date.as_str(), "%Y-%m-%d").map_err(|_| {
        StorageError::new(
            wuji_core::error::SafeErrorCode::InvalidArgument,
            "日期必须使用 YYYY-MM-DD 格式",
        )
    })?;
    let start = local_midnight_utc_ms(tz, naive)?;
    let next = naive
        .succ_opt()
        .ok_or_else(|| StorageError::internal("日期越界"))?;
    let end = local_midnight_utc_ms(tz, next)?;
    Ok((start, end))
}

/// 本地午夜对应的 UTC 毫秒。当地午夜被 DST 跳过（如太平洋岛国跨日）时拒绝，
/// 不静默改算（09 §6.7 的保守方向）。
fn local_midnight_utc_ms(tz: &Tz, date: NaiveDate) -> Result<i64> {
    let midnight = date
        .and_hms_opt(0, 0, 0)
        .ok_or_else(|| StorageError::internal("日期越界"))?;
    match tz.from_local_datetime(&midnight).earliest() {
        Some(dt) => Ok(dt.timestamp_millis()),
        None => Err(StorageError::internal(
            "本地日期在固定 reporting time zone 中不存在（被历法调整跳过）",
        )),
    }
}

/// 同时刻截断截止点（10 §5.2 + 11 实施方案阶段二 2.1）：
/// - 今日：返回 now_utc_ms（并校验 now 落在目标本地日 UTC 范围内——today 与 now
///   不一致时显式报错，不静默改算）；
/// - 历史日：返回目标本地日同一墙钟时刻（**保留完整秒与毫秒**，不得换算前截断到分钟）
///   对应的 UTC 毫秒。DST 规则：
///   1. 正常唯一时间：直接转换；
///   2. 不存在时间（spring-forward 缺口）：缺口后的**第一个合法时刻**（不得钳制到日末）；
///   3. 重复时间（fall-back）：优先选择与 now 的 UTC offset 相同的实例，无法匹配则取较早实例；
///   4. 最终结果限制在该本地日 UTC 范围 `[day_start, day_end)` 内。
///
/// 沿用保守原则：无法换算的极端日显式报错，不静默改算。
pub fn same_moment_cutoff_utc_ms(
    tz: &Tz,
    date: &LocalDate,
    now_utc_ms: i64,
    today: &LocalDate,
) -> Result<i64> {
    let date_naive = NaiveDate::parse_from_str(date.as_str(), "%Y-%m-%d").map_err(|_| {
        StorageError::new(
            wuji_core::error::SafeErrorCode::InvalidArgument,
            "日期必须使用 YYYY-MM-DD 格式",
        )
    })?;
    // 先算目标本地日 UTC 范围：今日分支同样受"结果限制在范围内"合同约束。
    let (day_start, day_end) = local_day_range_utc_ms(tz, date)?;
    if date == today {
        // 正常调用下 today = now 的本地日期，now 恒在范围内；不一致（调用方传错
        // today）时显式报错，不静默改算（沿用保守原则）。
        if (day_start..day_end).contains(&now_utc_ms) {
            return Ok(now_utc_ms);
        }
        return Err(StorageError::internal(
            "today 与 now 不一致（now 不在目标本地日范围内）",
        ));
    }
    let now_utc = chrono::DateTime::from_timestamp_millis(now_utc_ms)
        .ok_or_else(|| StorageError::internal("时间戳越界"))?;
    let now_local = now_utc.with_timezone(tz);
    // 目标本地日同一墙钟时刻：保留秒与毫秒（UI 只在展示时格式化为 HH:MM）。
    let millis = now_local.timestamp_millis().rem_euclid(1000) as u32;
    let target = date_naive
        .and_hms_milli_opt(
            now_local.hour(),
            now_local.minute(),
            now_local.second(),
            millis,
        )
        .ok_or_else(|| StorageError::internal("日期越界"))?;
    let cutoff = match tz.from_local_datetime(&target) {
        chrono::LocalResult::Single(dt) => dt.timestamp_millis(),
        chrono::LocalResult::Ambiguous(early, late) => {
            // fall-back 重复时间：优先与 now 的 UTC offset 相同的实例；无法匹配取较早。
            let now_offset = now_local.offset().fix().local_minus_utc();
            let early_offset = early.offset().fix().local_minus_utc();
            let late_offset = late.offset().fix().local_minus_utc();
            let chosen = if early_offset == now_offset {
                early
            } else if late_offset == now_offset {
                late
            } else {
                early
            };
            chosen.timestamp_millis()
        }
        chrono::LocalResult::None => {
            // spring-forward 缺口：从本地日起点逐秒前进，取本地时刻 >= 目标墙钟的
            // 第一个合法 UTC 瞬间（即缺口后的第一个合法时刻；缺口 ≤ 2 小时，循环有界）。
            let mut utc_ms = day_start;
            while utc_ms < day_end {
                let utc_dt = chrono::DateTime::from_timestamp_millis(utc_ms)
                    .ok_or_else(|| StorageError::internal("时间戳越界"))?;
                let local = utc_dt.with_timezone(tz);
                if local.date_naive() == date_naive && local.time() >= target.time() {
                    break;
                }
                utc_ms += 1000;
            }
            utc_ms
        }
    };
    Ok(cutoff.clamp(day_start, day_end.saturating_sub(1)))
}

/// 当前 UTC 毫秒（bootstrap/心跳用；测试显式注入时间）。
pub fn now_utc_ms() -> i64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_millis() as i64)
        .unwrap_or(0)
}

#[cfg(test)]
mod tests {
    use super::*;
    use chrono_tz::Asia::Shanghai;

    #[test]
    fn shanghai_local_fields() {
        // 2026-07-18T16:00:00Z = 2026-07-19 00:00 +08:00
        let (date, hour, offset) = local_fields(&Shanghai, 1784390400000).unwrap();
        assert_eq!(date, "2026-07-19");
        assert_eq!(hour, 0);
        assert_eq!(offset, 480);
    }

    #[test]
    fn shanghai_day_range() {
        let date = LocalDate::parse("2026-07-18").unwrap();
        let (start, end) = local_day_range_utc_ms(&Shanghai, &date).unwrap();
        assert_eq!(end - start, 86_400_000);
        assert_eq!(local_date_of(&Shanghai, start).unwrap(), "2026-07-18");
        assert_eq!(local_date_of(&Shanghai, end - 1).unwrap(), "2026-07-18");
    }

    #[test]
    fn new_york_fall_back_day_is_25_hours() {
        // 2026-11-01 America/New_York 发生 DST fallback，本地日 25 小时。
        let tz: Tz = "America/New_York".parse().unwrap();
        let date = LocalDate::parse("2026-11-01").unwrap();
        let (start, end) = local_day_range_utc_ms(&tz, &date).unwrap();
        assert_eq!(end - start, 25 * 3_600_000);
    }

    #[test]
    fn non_integer_hour_offset_uses_bucket_start_convention() {
        // UTC+5:45：local midnight 落在 UTC 小时正中。UTC 桶 18:00–19:00 横跨
        // 本地 23:45–次日 00:45 两个 local date，桶 local 字段只描述桶起点（09 §6.7）。
        let tz: Tz = "Asia/Kathmandu".parse().unwrap();
        // 2026-07-18T18:00:00Z
        let (date, hour, offset) = local_fields(&tz, 1_784_397_600_000).unwrap();
        assert_eq!(date, "2026-07-18");
        assert_eq!(hour, 23);
        assert_eq!(offset, 345);
    }

    // ---- same_moment_cutoff_utc_ms：11 实施方案阶段二 2.1 三类 DST 测试 ----

    fn local(s: &str) -> LocalDate {
        LocalDate::parse(s).unwrap()
    }

    #[test]
    fn cutoff_today_returns_now() {
        let tz: Tz = "Asia/Shanghai".parse().unwrap();
        let now = 1_784_332_800_000 + 12 * 3_600_000;
        let today = local("2026-07-18");
        assert_eq!(
            same_moment_cutoff_utc_ms(&tz, &today, now, &today).unwrap(),
            now
        );
    }

    #[test]
    fn cutoff_today_out_of_day_range_errors() {
        let tz: Tz = "Asia/Shanghai".parse().unwrap();
        // today 声称是 07-18，但 now = 2026-07-18T16:00:00Z 恰为 07-18 本地日的
        // day_end（上海 07-19 00:00）——不一致输入必须显式报错，不得静默改算。
        let now = 1_784_390_400_000;
        let today = local("2026-07-18");
        let err = same_moment_cutoff_utc_ms(&tz, &today, now, &today).unwrap_err();
        assert_eq!(err.code, wuji_core::error::SafeErrorCode::InternalSafeError);
        // day_end - 1 仍在范围内 → 正常返回 now。
        let ok = same_moment_cutoff_utc_ms(&tz, &today, now - 1, &today).unwrap();
        assert_eq!(ok, now - 1);
    }

    #[test]
    fn cutoff_normal_day_keeps_wall_clock_with_ms_precision() {
        // America/New_York 2026-07 为 EDT（UTC-4）。
        let tz: Tz = "America/New_York".parse().unwrap();
        // now = 2026-07-18T12:00:15.123Z = 本地 08:00:15.123 EDT
        let now = 1_784_376_015_123;
        let today = local("2026-07-18");
        let target = local("2026-07-17");
        let cutoff = same_moment_cutoff_utc_ms(&tz, &target, now, &today).unwrap();
        // 07-17 同一本地 08:00:15.123 EDT → 12:00:15.123Z；秒/毫秒精度保留。
        assert_eq!(cutoff, 1_784_289_615_123);
        // 结果限制在目标本地日 UTC 范围内。
        let (start, end) = local_day_range_utc_ms(&tz, &target).unwrap();
        assert!((start..end).contains(&cutoff));
    }

    #[test]
    fn cutoff_spring_forward_gap_takes_first_valid_instant_after_gap() {
        // 2026-03-08 US spring-forward：02:00 EST → 03:00 EDT，02:xx 不存在。
        let tz: Tz = "America/New_York".parse().unwrap();
        // now = 2026-03-07T07:30:00Z = 本地 02:30 EST（缺口前一日）；today = 03-07
        let now = 1_772_868_600_000;
        let today = local("2026-03-07");
        let target = local("2026-03-08");
        let cutoff = same_moment_cutoff_utc_ms(&tz, &target, now, &today).unwrap();
        // 目标日 02:30 不存在 → 缺口后第一个合法时刻 03:00 EDT = 07:00Z（不得钳制到日末）。
        assert_eq!(cutoff, 1_772_953_200_000);
    }

    #[test]
    fn cutoff_fall_back_prefers_same_utc_offset_instance() {
        // 2026-11-01 US fall-back：02:00 EDT → 01:00 EST，01:xx 出现两次。
        let tz: Tz = "America/New_York".parse().unwrap();
        // now = 2026-10-31T05:30:00Z = 本地 01:30 EDT（UTC-4）；today = 10-31。
        let now = 1_793_424_600_000;
        let today = local("2026-10-31");
        let target = local("2026-11-01");
        let cutoff = same_moment_cutoff_utc_ms(&tz, &target, now, &today).unwrap();
        // 目标日 01:30 两次（EDT 05:30Z / EST 06:30Z）；now offset = EDT → 较早实例 05:30Z。
        assert_eq!(cutoff, 1_793_511_000_000);
    }

    #[test]
    fn cutoff_fall_back_matches_winter_offset_to_late_instance() {
        let tz: Tz = "America/New_York".parse().unwrap();
        // now = 2026-11-02T06:30:00Z = 本地 01:30 EST（UTC-5，回拨后）；today = 11-02。
        let now = 1_793_601_000_000;
        let today = local("2026-11-02");
        let target = local("2026-11-01");
        let cutoff = same_moment_cutoff_utc_ms(&tz, &target, now, &today).unwrap();
        // now offset = EST → 匹配较晚实例 01:30 EST = 06:30Z。
        assert_eq!(cutoff, 1_793_514_600_000);
    }

    #[test]
    fn cutoff_fall_back_no_offset_match_takes_earliest() {
        // "无法匹配则取较早实例"分支：now 的 UTC offset（Paris 1910 LMT +00:09:21）
        // 与目标日回拨的两个歧义实例 offset（CEST +02 / CET +01）都不匹配 → 取较早。
        let tz: Tz = "Europe/Paris".parse().unwrap();
        let target = local("2026-10-25");
        // Paris 回拨规则是 03:00 CEST → 02:00 CET，真正歧义的是本地 02:00–02:59。
        let target_naive = chrono::NaiveDate::parse_from_str("2026-10-25", "%Y-%m-%d")
            .unwrap()
            .and_hms_opt(2, 30, 0)
            .unwrap();
        assert!(
            matches!(
                tz.from_local_datetime(&target_naive),
                chrono::LocalResult::Ambiguous(_, _)
            ),
            "Paris 2026-10-25 02:30 必须是歧义时间（回拨 03:00 CEST → 02:00 CET）"
        );
        // now = 1910-06-01T02:20:39Z = 本地 02:30:00（+00:09:21）；today = 1910-06-01。
        let now = -1_880_401_161_000;
        let today = local("1910-06-01");
        let cutoff = same_moment_cutoff_utc_ms(&tz, &target, now, &today).unwrap();
        // now offset（+00:09:21）与 +02/+01 均不匹配 → 取较早实例 02:30:00 CEST
        // = 2026-10-25T00:30:00Z。
        assert_eq!(cutoff, 1_792_888_200_000);
    }
}
