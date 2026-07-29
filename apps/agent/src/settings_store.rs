//! Settings 文件加载与启动对账（09 §9.1、审核 P1-01）。
//!
//! 启动对账同时输入三方证据：
//! - SQLite 最大已应用 revision/digest（`Writer::latest_settings_revision_digest`）；
//! - settings 文件（Desktop 唯一写入者，合法严格前滚的唯一来源）；
//! - 双槽备份候选（含 excludedProcessNames 的完整内容，必须与 DB metadata 交叉验证）。
//!
//! 硬规则：
//! - DB revision > 0 时禁止回退默认 revision 0；
//! - 文件/备份必须与 DB revision/digest 匹配，或是合法严格前滚（仅文件）；
//! - 无法恢复 DB 当前 revision 时阻止 Capture、不回默认值、IPC 保持在线、
//!   上报来源明确的安全错误，applied revision 保持 DB 值。

use std::path::Path;

use wuji_core::error::SafeErrorCode;
use wuji_core::settings::Settings;

pub enum SettingsLoad {
    Missing,
    Ready(Settings),
    Invalid(String),
}

pub fn load_settings_file(path: &Path) -> SettingsLoad {
    let raw = match std::fs::read_to_string(path) {
        Ok(raw) => raw,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => return SettingsLoad::Missing,
        Err(_) => return SettingsLoad::Invalid("设置文件不可读".to_string()),
    };
    parse_settings(&raw)
}

pub(crate) fn parse_settings(raw: &str) -> SettingsLoad {
    let settings: Settings = match serde_json::from_str(raw) {
        Ok(settings) => settings,
        Err(_) => return SettingsLoad::Invalid("设置文件不是合法 JSON".to_string()),
    };
    if let Err(errors) = settings.validate() {
        let message = errors
            .first()
            .map(|e| format!("{}: {}", e.field, e.message))
            .unwrap_or_else(|| "设置字段不合法".to_string());
        return SettingsLoad::Invalid(message);
    }
    SettingsLoad::Ready(settings)
}

/// 启动对账结果。
pub struct StartupSettingsDecision {
    /// 本次运行实际生效的 Settings（可能是从双槽备份恢复的 last-known-good）。
    pub settings: Settings,
    /// 需要上报的安全诊断（settings 页面与诊断页可见）。
    pub diagnostic: Option<SafeErrorCode>,
    /// false 表示无法恢复 DB 当前 revision：禁止进入 Running，
    /// capture_start 返回 SETTINGS_INVALID，IPC 保持在线。
    pub capture_allowed: bool,
    /// SharedState 必须呈现的 applied revision（与 DB 语义一致；blocked 时保持 DB 值，
    /// 不得重置为 0）。
    pub applied_revision: i64,
}

/// 启动对账（09 §9.1、审核 P1-01）。
///
/// `db` 是 SQLite 最大已应用 (revision, digest)；`backup` 是已经过 DB metadata
/// 交叉验证的双槽候选（`settings_backup::read_backup_matching` 的结果）。
pub fn reconcile_startup_settings(
    db: Option<(i64, String)>,
    file: SettingsLoad,
    backup: Option<Settings>,
) -> StartupSettingsDecision {
    let (db_revision, db_digest) = db.unwrap_or_else(|| (0, Settings::default().content_digest()));

    let blocked = |diagnostic: SafeErrorCode| -> StartupSettingsDecision {
        StartupSettingsDecision {
            // capture 被禁止，settings 不会被用于采集；applied_revision 保持 DB 值。
            settings: Settings::default(),
            diagnostic: Some(diagnostic),
            capture_allowed: false,
            applied_revision: db_revision,
        }
    };

    match file {
        SettingsLoad::Missing | SettingsLoad::Invalid(_) => {
            let code = SafeErrorCode::SettingsInvalid;
            if db_revision == 0 {
                // 全新库：DB revision 0 的内容就是内建默认值（备份即使有更高
                // revision 也未获 DB 提交，不得误应用）。
                return StartupSettingsDecision {
                    settings: Settings::default(),
                    diagnostic: if matches!(file, SettingsLoad::Invalid(_)) {
                        Some(code)
                    } else {
                        None
                    },
                    capture_allowed: true,
                    applied_revision: 0,
                };
            }
            match backup {
                Some(lkg) => StartupSettingsDecision {
                    settings: lkg,
                    diagnostic: Some(code),
                    capture_allowed: true,
                    applied_revision: db_revision,
                },
                None => blocked(code),
            }
        }
        SettingsLoad::Ready(settings) => {
            let file_revision = settings.revision.parse::<i64>().unwrap_or(-1);
            if file_revision > db_revision {
                // 合法严格前滚：唯一允许的前滚来源是 settings 文件。
                return StartupSettingsDecision {
                    applied_revision: file_revision,
                    settings,
                    diagnostic: None,
                    capture_allowed: true,
                };
            }
            if file_revision == db_revision && settings.content_digest() == db_digest {
                // 与 DB 匹配的重启重放：幂等。
                return StartupSettingsDecision {
                    settings,
                    diagnostic: None,
                    capture_allowed: true,
                    applied_revision: db_revision,
                };
            }
            // revision 降级或同 revision digest 冲突：拒绝文件（09 §9.1）。
            let code = SafeErrorCode::SettingsConflict;
            if let Some(lkg) = backup {
                return StartupSettingsDecision {
                    settings: lkg,
                    diagnostic: Some(code),
                    capture_allowed: true,
                    applied_revision: db_revision,
                };
            }
            if db_revision == 0 {
                // DB=0 时 DB 内容即默认值，允许运行但上报冲突。
                return StartupSettingsDecision {
                    settings: Settings::default(),
                    diagnostic: Some(code),
                    capture_allowed: true,
                    applied_revision: 0,
                };
            }
            blocked(code)
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn db_of(settings: &Settings) -> Option<(i64, String)> {
        Some((
            settings.revision.parse().unwrap(),
            settings.content_digest(),
        ))
    }

    fn saved(revision: u64) -> Settings {
        Settings {
            revision: revision.to_string(),
            ..Settings::default()
        }
    }

    /// 全新 DB revision 0，无文件/备份：允许默认（审核 4.1 必测场景 1）。
    #[test]
    fn missing_file_on_fresh_database_allows_defaults() {
        let decision = reconcile_startup_settings(None, SettingsLoad::Missing, None);
        assert!(decision.capture_allowed);
        assert_eq!(decision.diagnostic, None);
        assert_eq!(decision.settings.revision, "0");
        assert_eq!(decision.applied_revision, 0);
    }

    /// DB=N，文件与 DB 匹配：直接使用文件（场景 2）。
    #[test]
    fn file_matching_db_is_used() {
        let settings = saved(4);
        let decision =
            reconcile_startup_settings(db_of(&settings), SettingsLoad::Ready(settings), None);
        assert!(decision.capture_allowed);
        assert_eq!(decision.diagnostic, None);
        assert_eq!(decision.settings.revision, "4");
        assert_eq!(decision.applied_revision, 4);
    }

    /// DB=N，备份与 DB 匹配、文件损坏：从备份恢复，excludedProcessNames 不被清空（场景 3）。
    #[test]
    fn corrupt_file_recovers_matching_backup_with_excluded_names() {
        let mut lkg = saved(3);
        lkg.excluded_process_names = vec!["keepass.exe".to_string()];
        let decision = reconcile_startup_settings(
            db_of(&lkg),
            SettingsLoad::Invalid("损坏".to_string()),
            Some(lkg),
        );
        assert!(decision.capture_allowed);
        assert_eq!(decision.diagnostic, Some(SafeErrorCode::SettingsInvalid));
        assert_eq!(decision.settings.revision, "3", "不得静默回 revision 0");
        assert_eq!(
            decision.settings.excluded_process_names,
            vec!["keepass.exe"],
            "恢复失败不得清空排除名单"
        );
        assert_eq!(decision.applied_revision, 3);
    }

    /// 文件缺失且备份匹配 DB：恢复并上报（场景 3 变体）。
    #[test]
    fn missing_file_recovers_matching_backup() {
        let lkg = saved(2);
        let decision = reconcile_startup_settings(db_of(&lkg), SettingsLoad::Missing, Some(lkg));
        assert!(decision.capture_allowed);
        assert_eq!(decision.diagnostic, Some(SafeErrorCode::SettingsInvalid));
        assert_eq!(decision.settings.revision, "2");
        assert_eq!(decision.applied_revision, 2);
    }

    /// 文件 N+1 合法严格前滚（场景 4）。
    #[test]
    fn forward_revision_file_is_applied() {
        let old = saved(1);
        let decision = reconcile_startup_settings(db_of(&old), SettingsLoad::Ready(saved(2)), None);
        assert!(decision.capture_allowed);
        assert_eq!(decision.diagnostic, None);
        assert_eq!(decision.settings.revision, "2");
        assert_eq!(decision.applied_revision, 2);
    }

    /// 文件 revision 低于 DB：拒绝文件；备份匹配则恢复，否则阻止（场景 5）。
    #[test]
    fn downgrade_file_rejected_and_backup_recovers() {
        let lkg = saved(5);
        let decision =
            reconcile_startup_settings(db_of(&lkg), SettingsLoad::Ready(saved(2)), Some(lkg));
        assert!(decision.capture_allowed);
        assert_eq!(decision.diagnostic, Some(SafeErrorCode::SettingsConflict));
        assert_eq!(
            decision.settings.revision, "5",
            "拒绝降级，保持已应用 revision"
        );
        assert_eq!(decision.applied_revision, 5);
    }

    #[test]
    fn downgrade_file_without_matching_backup_blocks_capture() {
        let lkg = saved(5);
        let decision = reconcile_startup_settings(db_of(&lkg), SettingsLoad::Ready(saved(2)), None);
        assert!(!decision.capture_allowed);
        assert_eq!(decision.diagnostic, Some(SafeErrorCode::SettingsConflict));
        assert_eq!(
            decision.applied_revision, 5,
            "blocked 时 applied 保持 DB 值"
        );
    }

    /// 同 revision digest 冲突：拒绝文件；无匹配备份时阻止（场景 6）。
    #[test]
    fn same_revision_digest_conflict_blocks_without_backup() {
        let lkg = saved(4);
        let mut conflicting = saved(4);
        conflicting.idle_threshold_seconds = 90;
        let decision =
            reconcile_startup_settings(db_of(&lkg), SettingsLoad::Ready(conflicting), None);
        assert!(!decision.capture_allowed);
        assert_eq!(decision.diagnostic, Some(SafeErrorCode::SettingsConflict));
        assert_eq!(decision.applied_revision, 4);
    }

    #[test]
    fn same_revision_digest_conflict_recovers_matching_backup() {
        let lkg = saved(4);
        let mut conflicting = saved(4);
        conflicting.idle_threshold_seconds = 90;
        let decision =
            reconcile_startup_settings(db_of(&lkg), SettingsLoad::Ready(conflicting), Some(lkg));
        assert!(decision.capture_allowed);
        assert_eq!(decision.diagnostic, Some(SafeErrorCode::SettingsConflict));
        assert_eq!(decision.settings.idle_threshold_seconds, 60);
        assert_eq!(decision.applied_revision, 4);
    }

    /// DB>0 且文件/双槽均不可恢复：阻止采集、不回默认、applied 保持 DB 值（场景 7；
    /// 同时修正原测试"断言与名称相反"的错误）。
    #[test]
    fn unrecoverable_last_known_good_blocks_capture() {
        let lkg = saved(3);
        let decision = reconcile_startup_settings(db_of(&lkg), SettingsLoad::Missing, None);
        assert!(
            !decision.capture_allowed,
            "DB revision > 0 且不可恢复时必须阻止 Capture"
        );
        assert_eq!(decision.diagnostic, Some(SafeErrorCode::SettingsInvalid));
        assert_eq!(decision.applied_revision, 3, "applied 不得被重置为 0");
    }

    /// DB=0 但存在高 revision 备份（DB 未提交）：不得误应用（场景 10 的启动半边）。
    #[test]
    fn uncommitted_backup_is_not_applied_on_fresh_db() {
        let uncommitted = saved(9);
        let decision = reconcile_startup_settings(None, SettingsLoad::Missing, Some(uncommitted));
        assert!(decision.capture_allowed);
        assert_eq!(decision.diagnostic, None);
        assert_eq!(decision.settings.revision, "0", "DB 未提交的备份不得误应用");
        assert_eq!(decision.applied_revision, 0);
    }
}
