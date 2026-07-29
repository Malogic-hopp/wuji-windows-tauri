// Release 版使用 Windows GUI subsystem，避免启动 Desktop 时弹出控制台窗口。
// Debug 版保留控制台，便于 `pnpm tauri dev` 查看诊断输出。
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

fn main() {
    wuji_rebuild_desktop_lib::run();
}
