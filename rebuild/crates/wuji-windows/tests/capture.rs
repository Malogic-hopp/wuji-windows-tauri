//! V01-3 真实 Windows 捕获集成测试（09 §11 V01-3 退出条件）。
//!
//! 覆盖：真实前台采样、进程文件名解析、目标进程退出、目标进程卡死。
//! 这些测试只在 Windows 上运行，且不要求交互前台窗口必然存在。

#![cfg(windows)]

use wuji_windows::{CaptureError, capture_foreground, idle_seconds, process_image_file_name};

fn spawn_sleeper(program: &str, args: &[&str]) -> std::process::Child {
    std::process::Command::new(program)
        .args(args)
        .stdin(std::process::Stdio::null())
        .stdout(std::process::Stdio::null())
        .stderr(std::process::Stdio::null())
        .spawn()
        .expect("测试子进程必须能启动")
}

#[test]
fn idle_api_returns_structured_result() {
    match idle_seconds() {
        Ok(seconds) => assert!(seconds < 86_400, "idle 秒数应在合理范围内: {seconds}"),
        Err(error) => assert_eq!(error, CaptureError::IdleUnavailable),
    }
}

#[test]
fn current_process_image_name_resolves() {
    let pid = std::process::id();
    let name = process_image_file_name(pid).expect("当前进程文件名必须可解析");
    let current_exe = std::env::current_exe().unwrap();
    let expected = current_exe
        .file_name()
        .unwrap()
        .to_string_lossy()
        .to_string();
    assert_eq!(name.to_lowercase(), expected.to_lowercase());
}

#[test]
fn exiting_process_yields_controlled_error() {
    let mut child = spawn_sleeper("cmd.exe", &["/c", "timeout", "/t", "30", "/nobreak"]);
    let pid = child.id();
    // 目标存活时可解析；杀死进程后必须返回受控错误而不是 panic。
    let alive = process_image_file_name(pid);
    assert!(alive.is_ok(), "存活子进程文件名必须可解析: {alive:?}");
    child.kill().expect("kill");
    child.wait().expect("wait");
    let after = process_image_file_name(pid);
    assert!(
        after.is_err(),
        "退出进程必须返回受控错误而不是成功: {after:?}"
    );
}

#[test]
fn hung_process_is_still_queryable() {
    // v0.1 不读取窗口标题；进程名查询走内核路径，目标卡死不影响采集。
    let mut child = spawn_sleeper(
        "powershell.exe",
        &["-NoProfile", "-Command", "Start-Sleep", "-Seconds", "30"],
    );
    let pid = child.id();
    let name = process_image_file_name(pid).expect("卡死子进程文件名必须可解析");
    assert!(name.to_lowercase().contains("powershell"));
    child.kill().expect("kill");
    child.wait().expect("wait");
}

#[test]
fn foreground_capture_smoke() {
    // 不要求必然存在前台窗口（无交互会话/锁定时允许受控失败），但绝不 panic。
    match capture_foreground() {
        Ok(sample) => {
            // 字段级降级：两个字段各自独立成败。
            if let Ok(name) = &sample.process_file_name {
                assert!(!name.is_empty(), "进程文件名不得为空");
            }
            let _ = sample.idle_seconds;
        }
        Err(error) => {
            assert!(
                matches!(
                    error,
                    CaptureError::ForegroundUnavailable | CaptureError::ProcessQueryFailed
                ),
                "前台采样失败必须是受控错误: {error:?}"
            );
        }
    }
}
