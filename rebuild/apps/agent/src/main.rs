//! WUJI Rebuild v0.1 Rust Agent 二进制入口。
//!
//! V01-1 只固定二进制命名（09 §4.1）与 workspace 骨架；
//! Capture/Processor/Writer/IPC 在 V01-3–V01-5 落地。

fn main() {
    eprintln!(
        "{}: V01-1 workspace 骨架；Agent 运行时在 V01-3–V01-5 落地",
        wuji_core::runtime_names::AGENT_EXE_NAME
    );
}
