//! WUJI Rebuild v0.1 Rust Agent 二进制入口。
//!
//! V01-3 已落地：Win32 前台/进程/idle 采集、隐私过滤 Processor、bounded queue
//! 与 continuity epoch。双 lane Writer、IPC 与心跳在 V01-5 接入。

fn main() {
    eprintln!(
        "{}: V01-1–V01-3 骨架（capture pipeline 已实现）；Agent 运行时在 V01-5 落地",
        wuji_core::runtime_names::AGENT_EXE_NAME
    );
    let _ = wuji_rebuild_agent::capture_loop::CAPTURE_WAKE_INTERVAL;
}
