//! Settings 统一 crash-consistent 持久化协议（阶段 4.1 复审 P1-01/P1-02）。
//!
//! 启动前滚、启动幂等恢复与运行时 SettingsApplied 共用同一条路径：
//!
//! 1. 无副作用预校验：revision 降级与 digest 冲突在触碰任何槽位之前拒绝；
//! 2. DB 感知候选写入：`write_backup` 绝不覆盖唯一匹配当前 DB 的 LKG 槽；
//! 3. SQLite 提交（`engine.apply_settings` → `ensure_settings_revision`）；
//! 4. 幂等路径（文件/内容与 DB 一致）不重复提交，但修复缺失的备份冗余。

use std::path::Path;

use wuji_core::error::SafeErrorCode;
use wuji_core::settings::Settings;
use wuji_storage::Writer;
use wuji_storage::error::{Result as StorageResult, StorageError};

use crate::activity::ActivityEngine;
use crate::settings_backup;

/// 持久化结果。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SettingsPersistOutcome {
    /// 新 revision 已写入候选并提交 DB。
    Applied(i64),
    /// 内容与 DB 一致（幂等）；未重复提交，备份冗余已按需修复。
    Idempotent(i64),
}

impl SettingsPersistOutcome {
    /// 已生效的 revision（两种结果相同）。
    pub fn revision(self) -> i64 {
        match self {
            Self::Applied(revision) | Self::Idempotent(revision) => revision,
        }
    }
}

/// 统一 Settings 持久化（复审 P1-01）：启动与运行时都必须走这里。
pub fn apply_settings_persistent(
    engine: &mut ActivityEngine,
    writer: &mut Writer,
    backup_dir: &Path,
    settings: &Settings,
    at_utc_ms: i64,
) -> StorageResult<SettingsPersistOutcome> {
    let revision = settings
        .revision
        .parse::<i64>()
        .map_err(|_| StorageError::internal("settings revision 非数字"))?;
    let digest = settings.content_digest();
    let (db_revision, db_digest) = writer
        .latest_settings_revision_digest()?
        .unwrap_or_else(|| (0, Settings::default().content_digest()));

    // 预校验（复审 P1-02）：降级与 digest 冲突在触碰任何槽位之前拒绝。
    if revision < db_revision {
        return Err(StorageError::new(
            SafeErrorCode::SettingsConflict,
            "settings revision 低于已应用值，拒绝降级",
        ));
    }
    if revision == db_revision && digest != db_digest {
        return Err(StorageError::new(
            SafeErrorCode::SettingsConflict,
            "设置内容与已应用 revision 摘要冲突",
        ));
    }

    if revision == db_revision {
        // 幂等：内容与 DB 一致。修复缺失的备份冗余（复审 P1-01）；
        // 不重复提交 DB。备份写失败不视为致命（DB 仍是权威），但返回错误让
        // 调用方上报——不得声称冗余已建立。
        if engine.settings_revision() < revision {
            engine.apply_settings(writer, settings.clone(), at_utc_ms)?;
        }
        if settings_backup::read_backup_matching(
            backup_dir,
            Some(&(db_revision, db_digest.clone())),
        )
        .is_none()
        {
            settings_backup::write_backup(backup_dir, settings, Some(&(db_revision, db_digest)))
                .map_err(|e| {
                    StorageError::new(
                        SafeErrorCode::SettingsSavedNotApplied,
                        format!("Settings 备份冗余修复失败: {e}"),
                    )
                })?;
        }
        return Ok(SettingsPersistOutcome::Idempotent(revision));
    }

    // 前滚：先写候选槽（DB 感知：绝不覆盖唯一匹配 DB 的 LKG），再提交 SQLite。
    settings_backup::write_backup(backup_dir, settings, Some(&(db_revision, db_digest))).map_err(
        |e| {
            StorageError::new(
                SafeErrorCode::SettingsSavedNotApplied,
                format!("Settings 备份写入失败，未应用: {e}"),
            )
        },
    )?;
    engine.apply_settings(writer, settings.clone(), at_utc_ms)?;
    Ok(SettingsPersistOutcome::Applied(revision))
}
