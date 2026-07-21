//! WUJI Rebuild v0.1 Rust Agent 库：采集流水线、行为状态机、Writer、IPC 与心跳。

pub mod activity;
pub mod capture_loop;
pub mod command_server;
pub mod heartbeat;
pub mod maintenance;
pub mod processor_task;
pub mod runtime_paths;
pub mod settings_store;
pub mod shared;
pub mod writer_task;
