//! 只读查询服务：Today/Timeline（09 §7.3、§8.4）。
//!
//! 每次调用以 read-only + query_only 打开数据库（短生命周期 reader，04 §13）。

use std::path::PathBuf;

use wuji_core::dto::{HeatmapDto, LocalDate, TimelineCursor, TimelinePageDto, TodayDto};
use wuji_core::error::{SafeError, SafeErrorCode};
use wuji_storage::Reader;
use wuji_storage::error::StorageError;
use wuji_storage::timeutil::{local_date_of, now_utc_ms};

use crate::paths;

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
}
