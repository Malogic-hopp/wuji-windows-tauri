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
}
