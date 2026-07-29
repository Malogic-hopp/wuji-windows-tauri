//! WUJI Rebuild v0.1 Rust Agent 库：采集流水线、行为状态机、Writer、IPC 与心跳。

pub mod activity;
pub mod barrier;
pub mod capture_coordinator;
pub mod capture_loop;
pub mod command_server;
pub mod control_plane;
pub mod heartbeat;
pub mod maintenance;
pub mod pipeline_health;
pub mod processor_task;
pub mod runtime_paths;
pub mod session_power_events;
pub mod settings_backup;
pub mod settings_persist;
pub mod settings_reconciler;
pub mod settings_store;
pub mod shared;
pub mod writer_task;
