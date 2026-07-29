//! Settings last-known-good 双槽原子备份（S2-01：隐私一票否决）。
//!
//! 完整 Settings（含 excludedProcessNames）只存储于独立于行为 SQLite 的
//! 私密备份文件；SQLite settings_revisions 仅保留 revision/digest 等必要元数据。
//!
//! DB 感知双槽协议（阶段 4.1 复审 P1-02/P1-03）：
//! - 槽位选择只由"槽位内容 vs DB metadata"决定，无 marker 文件：
//!   marker 曾是未受保护的安全状态（更新失败被静默忽略、非原子写），已移除；
//! - 写入候选时绝不覆盖唯一匹配当前 DB 的 LKG 槽位；
//! - 两个槽位都不匹配 DB 时覆盖 revision 较低的槽（相等则 A）；
//! - 临时文件 fsync 后原子 rename；单槽损坏不影响另一槽。

use std::path::{Path, PathBuf};

use wuji_core::settings::Settings;

use crate::settings_store::{SettingsLoad, parse_settings};

/// 双槽备份文件名（pub：集成测试需要构造确定槽位布局）。
pub const SLOT_A: &str = "settings-lkg-a.json";
/// 双槽备份文件名 B。
pub const SLOT_B: &str = "settings-lkg-b.json";

/// 将 Settings 完整内容写入双槽备份。
///
/// `protect` 是当前 DB 的 (revision, digest)：唯一匹配它的槽位是 last-known-good，
/// 本次写入不得覆盖（复审 P1-02）。crash-consistent 顺序要求调用方先完成本写入、
/// 再提交 SQLite；本写入失败时不得提交 DB 也不得返回成功。
pub fn write_backup(
    config_dir: &Path,
    settings: &Settings,
    protect: Option<&(i64, String)>,
) -> Result<(), String> {
    use std::io::Write as _;

    std::fs::create_dir_all(config_dir).map_err(|e| format!("创建 config 目录失败: {e}"))?;

    let content = settings.canonical_json();
    let target_name = choose_slot(config_dir, protect);

    let target = config_dir.join(target_name);
    let tmp = config_dir.join(format!("{target_name}.tmp"));

    // 写临时文件并 fsync，然后原子替换（中途崩溃只留下 .tmp，不污染正式槽位）。
    {
        let mut file = std::fs::File::create(&tmp)
            .map_err(|e| format!("创建 Settings 备份临时文件失败: {e}"))?;
        file.write_all(content.as_bytes())
            .map_err(|e| format!("写入 Settings 备份临时文件失败: {e}"))?;
        file.sync_all()
            .map_err(|e| format!("Settings 备份 fsync 失败: {e}"))?;
    }
    std::fs::rename(&tmp, &target).map_err(|e| format!("Settings 备份原子化失败: {e}"))?;

    Ok(())
}

/// 槽位选择（复审 P1-02/P1-03）：
/// - 恰好一个槽匹配 DB → 写另一个（保护唯一 LKG）；
/// - 两个都匹配（有冗余）→ 任一（写 A）；
/// - 都不匹配 → 覆盖 revision 较低的槽（缺失视为 -1），相等写 A。
fn choose_slot(config_dir: &Path, protect: Option<&(i64, String)>) -> &'static str {
    let matches = |name: &str| {
        let slot = load_and_validate(config_dir.join(name));
        slot.is_some_and(|settings| {
            protect.is_some_and(|(revision, digest)| {
                settings.revision.parse::<i64>().ok() == Some(*revision)
                    && settings.content_digest() == *digest
            })
        })
    };
    let a_matches = matches(SLOT_A);
    let b_matches = matches(SLOT_B);
    match (a_matches, b_matches) {
        (true, false) => SLOT_B,
        (false, true) => SLOT_A,
        (true, true) => SLOT_A,
        (false, false) => {
            let revision_of = |name: &str| {
                load_and_validate(config_dir.join(name))
                    .and_then(|s| s.revision.parse::<i64>().ok())
                    .unwrap_or(-1)
            };
            if revision_of(SLOT_A) <= revision_of(SLOT_B) {
                SLOT_A
            } else {
                SLOT_B
            }
        }
    }
}

/// 从双槽备份恢复 Settings：读取两槽，取 revision 最高且验证通过的。
///
/// 返回 None 表示两槽均不可恢复（文件缺失或内容损坏）。
/// 注意：本函数不做 DB 交叉验证，启动对账必须使用 `read_backup_matching`（审核 P1-01）。
pub fn read_backup(config_dir: &Path) -> Option<Settings> {
    let a = load_and_validate(config_dir.join(SLOT_A));
    let b = load_and_validate(config_dir.join(SLOT_B));

    match (a, b) {
        (Some(a_settings), Some(b_settings)) => {
            let a_rev = a_settings.revision.parse::<u64>().unwrap_or(0);
            let b_rev = b_settings.revision.parse::<u64>().unwrap_or(0);
            if a_rev >= b_rev {
                Some(a_settings)
            } else {
                Some(b_settings)
            }
        }
        (Some(settings), None) | (None, Some(settings)) => Some(settings),
        (None, None) => None,
    }
}

/// 以 SQLite metadata 交叉验证双槽（审核 P1-01 第 6 条）：
/// 返回 revision/digest 与 DB 完全一致的槽位内容；
/// 两个槽都检查，而不是只信任"最高 revision"——DB 未提交的更高槽位
/// （commit 前崩溃残留）被自然忽略。
pub fn read_backup_matching(config_dir: &Path, db: Option<&(i64, String)>) -> Option<Settings> {
    let (db_revision, db_digest) = db?;
    [SLOT_A, SLOT_B]
        .iter()
        .filter_map(|name| load_and_validate(config_dir.join(name)))
        .find(|settings| {
            settings.revision.parse::<i64>().ok() == Some(*db_revision)
                && settings.content_digest() == *db_digest
        })
}

/// 读取单个槽位文件并验证：不存在返回 None，损坏返回 None。
fn load_and_validate(path: PathBuf) -> Option<Settings> {
    if !path.exists() {
        return None;
    }
    let raw = std::fs::read_to_string(&path).ok()?;
    match parse_settings(&raw) {
        SettingsLoad::Ready(settings) => Some(settings),
        _ => None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use tempfile::TempDir;

    fn temp_config() -> (TempDir, PathBuf) {
        let dir = TempDir::new().expect("temp dir");
        let config = dir.path().join("config");
        std::fs::create_dir_all(&config).unwrap();
        (dir, config)
    }

    fn write(config: &Path, settings: &Settings) {
        write_backup(config, settings, None).expect("写入备份");
    }

    #[test]
    fn round_trips_full_settings_including_excluded_names() {
        let (_dir, config) = temp_config();
        let settings = Settings {
            revision: "3".to_string(),
            idle_threshold_seconds: 90,
            excluded_process_names: vec!["keepass.exe".to_string(), "slack.exe".to_string()],
            ..Settings::default()
        };

        write(&config, &settings);
        let recovered = read_backup(&config).expect("恢复备份");

        assert_eq!(recovered.revision, "3");
        assert_eq!(recovered.idle_threshold_seconds, 90);
        assert_eq!(
            recovered.excluded_process_names,
            vec!["keepass.exe", "slack.exe"]
        );
        assert_eq!(
            recovered.content_digest(),
            settings.content_digest(),
            "digest 必须一致"
        );
    }

    #[test]
    fn recovers_from_corrupted_slot() {
        let (_dir, config) = temp_config();
        let settings = Settings {
            revision: "5".to_string(),
            ..Settings::default()
        };

        write(&config, &settings);
        let higher = Settings {
            revision: "6".to_string(),
            ..Settings::default()
        };
        write(&config, &higher);

        let recovered = read_backup(&config).expect("应有至少一个有效槽");
        assert_eq!(recovered.revision, "6", "损坏低 revision 槽不影响恢复");

        let recovered2 = read_backup(&config);
        assert!(recovered2.is_some(), "至少一个槽应可读");
    }

    #[test]
    fn returns_none_when_both_slots_missing() {
        let dir = TempDir::new().expect("temp dir");
        let config = dir.path().join("config");
        // 不创建 config 目录，也不写任何文件。
        let recovered = read_backup(&config);
        assert!(recovered.is_none());
    }

    #[test]
    fn successive_writes_keep_latest_recoverable() {
        let (_dir, config) = temp_config();

        let s1 = Settings {
            revision: "1".to_string(),
            ..Settings::default()
        };
        let s2 = Settings {
            revision: "2".to_string(),
            ..Settings::default()
        };
        let s3 = Settings {
            revision: "3".to_string(),
            ..Settings::default()
        };

        write(&config, &s1);
        write(&config, &s2);
        write(&config, &s3);

        let recovered = read_backup(&config).expect("至少一个槽可恢复");
        assert_eq!(recovered.revision, "3", "最新 revision 应可恢复");

        // 损坏包含最新 revision 的槽，应能回退到上一个槽。
        for name in [SLOT_A, SLOT_B] {
            let path = config.join(name);
            if path.exists() {
                let content = std::fs::read_to_string(&path).unwrap_or_default();
                if content.contains("\"revision\":\"3\"") {
                    std::fs::write(&path, b"corrupt").unwrap();
                }
            }
        }

        let recovered_after_corruption = read_backup(&config);
        assert!(
            recovered_after_corruption.is_some(),
            "损坏最新槽后仍能从旧槽恢复"
        );
        assert!(
            recovered_after_corruption
                .unwrap()
                .revision
                .parse::<u64>()
                .unwrap()
                >= 2,
            "应回退到上一个有效 revision"
        );
    }

    /// 审核 P1-01：双槽必须与 DB metadata 交叉验证；DB 未提交的高 revision 槽位不得误应用。
    #[test]
    fn read_backup_matching_ignores_uncommitted_higher_slot() {
        let (_dir, config) = temp_config();
        let committed = Settings {
            revision: "5".to_string(),
            ..Settings::default()
        };
        let uncommitted = Settings {
            revision: "6".to_string(),
            idle_threshold_seconds: 90,
            ..Settings::default()
        };
        write(&config, &committed);
        // 模拟"备份已写、DB commit 前崩溃"：更高 revision 只存在于槽位。
        write(&config, &uncommitted);

        // 不按最高 revision 信任：匹配 DB=5 时必须拿到 revision 5 的槽位。
        let matched = read_backup_matching(&config, Some(&(5, committed.content_digest())))
            .expect("必须找到与 DB 匹配的槽位");
        assert_eq!(matched.revision, "5");
        assert_eq!(matched.idle_threshold_seconds, 60);

        // DB=6 的 digest 不存在时（未提交），不得返回 revision 6 的内容。
        let not_matched = read_backup_matching(&config, Some(&(6, committed.content_digest())));
        assert!(not_matched.is_none(), "digest 不匹配不得返回任何槽位");

        // DB 无记录（None）时不返回任何内容。
        assert!(read_backup_matching(&config, None).is_none());
    }

    /// 同 revision 但内容不同的槽位（digest 冲突）不得作为 last-known-good。
    #[test]
    fn read_backup_matching_rejects_digest_conflict() {
        let (_dir, config) = temp_config();
        let mut settings = Settings {
            revision: "3".to_string(),
            ..Settings::default()
        };
        write(&config, &settings);
        settings.sampling_interval_seconds = 5;
        let wrong_digest = settings.content_digest();
        assert!(
            read_backup_matching(&config, Some(&(3, wrong_digest))).is_none(),
            "同 revision 不同 digest 不得匹配"
        );
    }

    /// 复审 P1-02：唯一匹配 DB 的 LKG 槽位在任何写入下都不得被覆盖。
    #[test]
    fn write_never_overwrites_only_db_matching_slot() {
        let (_dir, config) = temp_config();
        let committed = Settings {
            revision: "5".to_string(),
            ..Settings::default()
        };
        // 布局：A=5（匹配 DB），B=4（旧）。
        std::fs::write(config.join(SLOT_A), committed.canonical_json()).unwrap();
        let older = Settings {
            revision: "4".to_string(),
            ..Settings::default()
        };
        std::fs::write(config.join(SLOT_B), older.canonical_json()).unwrap();

        let protect = (5_i64, committed.content_digest());
        let newer = Settings {
            revision: "6".to_string(),
            ..Settings::default()
        };
        // 连续两次写入（模拟两次失败重试）：A 都必须保持 revision 5 内容。
        for _ in 0..2 {
            write_backup(&config, &newer, Some(&protect)).expect("写入候选");
            let a_raw = std::fs::read_to_string(config.join(SLOT_A)).unwrap();
            assert!(
                a_raw.contains("\"revision\":\"5\""),
                "唯一匹配 DB 的槽位不得被覆盖"
            );
        }
        let b_raw = std::fs::read_to_string(config.join(SLOT_B)).unwrap();
        assert!(b_raw.contains("\"revision\":\"6\""), "候选必须写入非匹配槽");
    }
}
