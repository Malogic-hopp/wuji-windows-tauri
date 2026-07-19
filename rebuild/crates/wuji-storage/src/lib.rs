//! WUJI Rebuild v0.1 存储 crate：bootstrap、Single SQLite Writer 与只读 Query。
//!
//! 合同：docs/rebuild/09-Tauri-Rust-Rebuild-v0.1实施基线.md §7。
//! DDL 唯一来源：`schema/schema.sql`（编译期内嵌，09 §7.2）。

pub mod connection;
pub mod error;
pub mod models;
pub mod reader;
pub mod recompute;
pub mod timeutil;
pub mod writer;

pub use error::{Result, StorageError};
pub use models::{GapRow, RuntimeRow, SchemaMeta, SegmentRow, WorkBlockRow};
pub use reader::Reader;
pub use writer::{ObservationInsert, Writer};
