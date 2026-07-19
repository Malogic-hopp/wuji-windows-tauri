//! 依赖边界门禁（09 §4：wuji-core 不依赖 Tauri、Win32 或 rusqlite；09 §11 V01-1 退出条件）。

#[test]
fn wuji_core_has_no_tauri_win32_or_sqlite_dependencies() {
    let manifest =
        std::fs::read_to_string(concat!(env!("CARGO_MANIFEST_DIR"), "/Cargo.toml")).unwrap();
    let forbidden = [
        "tauri",
        "windows",
        "windows-sys",
        "winapi",
        "rusqlite",
        "sqlite",
    ];

    // 只检查 [dependencies] 段内真实的依赖键，注释与文档性文字不算依赖。
    let deps_section = manifest.split("[dependencies]").nth(1).unwrap_or("");
    for line in deps_section.lines() {
        let line = line.trim();
        if line.is_empty() || line.starts_with('#') || line.starts_with('[') {
            continue;
        }
        let key = line.split(['=', ' ', '{']).next().unwrap_or("").trim();
        for name in forbidden {
            assert_ne!(key, name, "wuji-core 不得依赖 {name}（09 §4 依赖边界）");
        }
    }
}
