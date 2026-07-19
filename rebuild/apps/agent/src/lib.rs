//! WUJI Rebuild v0.1 Rust Agent 库：采集流水线、隐私过滤与 bounded queue。
//!
//! V01-5 的双 lane Writer、IPC、心跳与恢复将在本库上继续构建。

pub mod activity;
pub mod capture_loop;
pub mod processor_task;
