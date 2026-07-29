//! 连接级 PRAGMA 合同（09 §7.2 修订）：PRAGMA 按连接生效，不得假设已被持久化。

use std::path::Path;

use rusqlite::Connection;

use crate::error::{Result, StorageError};
use crate::models::{SUPPORTED_SCHEMA_VERSION, SchemaMeta};

/// Writer 唯一读写连接（09 §5.2、§7.2）。
pub fn open_writer_connection(path: &Path) -> Result<Connection> {
    let conn = Connection::open(path).map_err(|e| {
        StorageError::db_unavailable("无法打开行为数据库").with_detail(e.to_string())
    })?;
    apply_writer_pragmas(&conn)?;
    Ok(conn)
}

pub(crate) fn apply_writer_pragmas(conn: &Connection) -> Result<()> {
    conn.execute_batch(
        "PRAGMA foreign_keys = ON;
         PRAGMA busy_timeout = 750;
         PRAGMA synchronous = NORMAL;
         PRAGMA trusted_schema = OFF;",
    )
    .map_err(StorageError::from_sqlite)?;
    Ok(())
}

/// Tauri 只读 reader（09 §7.3）：read-only + query_only。
/// 打开失败（库不存在、WAL 共享内存无法建立）返回 DB_UNAVAILABLE，不用 immutable 降级。
pub fn open_reader_connection(path: &Path) -> Result<Connection> {
    if !path.exists() {
        return Err(StorageError::db_unavailable("数据库不存在"));
    }
    let conn = Connection::open_with_flags(path, rusqlite::OpenFlags::SQLITE_OPEN_READ_ONLY)
        .map_err(|e| {
            StorageError::db_unavailable("无法以只读方式打开数据库").with_detail(e.to_string())
        })?;
    conn.execute_batch("PRAGMA query_only = ON;")
        .map_err(StorageError::from_sqlite)?;
    Ok(conn)
}

/// 读取并校验 schema_meta（09 §7.2/§7.3：schema_version != 1 → DB_SCHEMA_UNSUPPORTED）。
pub fn read_and_verify_schema_meta(conn: &Connection) -> Result<SchemaMeta> {
    let meta = conn
        .query_row(
            "SELECT schema_version, algorithm_version, created_at_utc_ms, reporting_time_zone_id
             FROM schema_meta WHERE singleton_id = 1",
            [],
            |row| {
                Ok(SchemaMeta {
                    schema_version: row.get(0)?,
                    algorithm_version: row.get(1)?,
                    created_at_utc_ms: row.get(2)?,
                    reporting_time_zone_id: row.get(3)?,
                })
            },
        )
        .map_err(|_| {
            StorageError::schema_unsupported("数据库缺少 schema_meta，不是受支持的 v0.1 行为库")
        })?;

    if meta.schema_version != SUPPORTED_SCHEMA_VERSION {
        return Err(StorageError::schema_unsupported(
            "数据库 schema 版本不受支持，请使用匹配版本的 Agent/Desktop",
        ));
    }
    meta.reporting_tz()?;
    Ok(meta)
}

/// 验证 journal 模式为 WAL（v0.1 库由 bootstrap 固定为 WAL）。
pub fn verify_wal(conn: &Connection) -> Result<()> {
    let mode: String = conn
        .query_row("PRAGMA journal_mode", [], |row| row.get(0))
        .map_err(StorageError::from_sqlite)?;
    if !mode.eq_ignore_ascii_case("wal") {
        return Err(StorageError::db_unavailable("数据库不是预期的 WAL 模式"));
    }
    Ok(())
}
