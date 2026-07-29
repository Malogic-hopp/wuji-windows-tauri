//! WUJI Rebuild v0.1 核心领域与合同 crate。
//!
//! 合同来源：docs/dev/09-Tauri-Rust-Rebuild-v0.1实施基线.md（下称 09）。
//! 边界（09 §4）：不依赖 Tauri、Win32 或 rusqlite；只保存纯领域类型、
//! Settings、DTO、稳定错误码与固定运行命名。

pub mod bindings;
pub mod domain;
pub mod dto;
pub mod error;
pub mod pipeline;
pub mod runtime_names;
pub mod settings;
