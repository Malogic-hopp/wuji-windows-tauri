//! E2E 测试共享基础设施：进程身份守卫（阶段 4.2 第六~八轮复审）。
//!
//! 设计约束（第七轮定稿）：
//! - 进程身份 = 创建后立即 `DuplicateHandle` 复制的 Windows 进程句柄；
//!   绝不通过 PID 重新 OpenProcess（消除 PID 交付竞态与身份错绑）；
//! - spawn/detached 都由守卫统一创建：返回前身份已登记，登记失败即
//!   kill + wait，不存在"已启动未登记"的孤儿窗口；
//! - detached 用原生 Command + creation flags 创建，无 PowerShell、无身份文件；
//!   真实父进程退出门禁另由独立 launcher + 跨进程 DuplicateHandle 覆盖；
//! - 优雅退出在同一 PipeClient 上完成 hello → shutdown → 校验 willExit；
//!   进程是否退出只以句柄 signaled 为准，pipe 状态不作退出证明；
//! - Drop 不 panic：未确认退出时不删除 channel 目录并 eprintln 留诊断。

use std::path::PathBuf;
use std::process::{Child, Command};
use std::time::{Duration, Instant};

use wuji_rebuild_agent::command_server::client::PipeClient;

pub const AGENT_BIN: &str = env!("CARGO_BIN_EXE_wuji-rebuild-agent-v01");

pub fn test_channel() -> String {
    format!(
        "rebuild-v01-test-{}",
        ulid::Ulid::generate().to_string().to_lowercase()
    )
}

pub fn data_root(channel: &str) -> PathBuf {
    PathBuf::from(std::env::var_os("LOCALAPPDATA").expect("LOCALAPPDATA"))
        .join("WUJI-Rebuild-V01")
        .join(channel)
}

pub fn db_path(channel: &str) -> PathBuf {
    data_root(channel).join("data").join("wuji-rebuild-v0.1.db")
}

pub fn pipe_name(channel: &str) -> String {
    let sid = wuji_windows::current_user_sid().expect("sid");
    let scope = wuji_core::runtime_names::user_scope(&sid);
    format!("\\\\.\\pipe\\WUJI.Rebuild.V01.Test.{channel}.{scope}")
}

pub fn cleanup(channel: &str) {
    let _ = std::fs::remove_dir_all(data_root(channel));
}

pub fn ulid() -> String {
    ulid::Ulid::generate().to_string()
}

fn agent_command(channel: &str, capture_on_start: bool) -> Command {
    // stderr 落盘到 channel 目录（失败诊断；复审第六轮 hello EOF 证据收集）。
    let dir = data_root(channel);
    let _ = std::fs::create_dir_all(&dir);
    let stderr =
        std::fs::File::create(dir.join("agent-stderr.log")).expect("创建 agent stderr 文件");
    let mut command = Command::new(AGENT_BIN);
    command.arg("--channel").arg(channel);
    if capture_on_start {
        command.arg("--capture-on-start");
    }
    command
        .stdin(std::process::Stdio::null())
        .stdout(std::process::Stdio::null())
        .stderr(std::process::Stdio::from(stderr));
    command
}

/// 优雅退出结果。
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ShutdownOutcome {
    /// 同连接 hello + shutdown 成功，且句柄确认进程已退出。
    Exited,
    /// pipe 不可连接且无任何已登记存活句柄（目标未在运行）。
    NotRunning,
    /// 失败（诊断信息）。
    Failed(String),
}

/// 稳定身份 key（guard 内单调递增；查找/终止/注销都基于它，PID 只用于日志）。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct TrackedProcessKey(u64);

impl TrackedProcessKey {
    /// 测试专用：构造必然未登记的 key（拒绝路径验证）。
    #[allow(dead_code)]
    pub fn bogus_for_tests() -> Self {
        Self(u64::MAX)
    }
}

/// 已登记的进程身份：DuplicateHandle 复制的句柄（PID 复用无法仿冒）。
struct TrackedProcess {
    key: TrackedProcessKey,
    /// 仅日志展示。
    pid: u32,
    handle: wuji_windows::ProcessHandle,
}

/// 测试进程身份守卫（第七/八轮复审：原生句柄方案）。
pub struct TestAgentGuard {
    channel: String,
    tracked: Vec<TrackedProcess>,
    next_key: u64,
    /// 句柄登记失败且首次清理未确认退出时，保留原始 Child 责任到 Drop。
    emergency_children: Vec<Child>,
    /// 测试专用故障注入：下一次句柄复制按指定错误失败（确定性，不依赖真实竞态）。
    fail_next_duplicate_for_tests: Option<String>,
    /// 测试专用：模拟登记失败后的首次 Child 清理失败。
    fail_next_cleanup_for_tests: bool,
    /// 测试专用：force_kill 的下一次 wait 确定性返回 Timeout 分支。
    force_next_wait_timeout_for_tests: bool,
}

impl TestAgentGuard {
    pub fn new(channel: &str) -> Self {
        Self {
            channel: channel.to_string(),
            tracked: Vec::new(),
            next_key: 0,
            emergency_children: Vec::new(),
            fail_next_duplicate_for_tests: None,
            fail_next_cleanup_for_tests: false,
            force_next_wait_timeout_for_tests: false,
        }
    }

    /// 已登记身份数量（测试断言用）。
    #[allow(dead_code)] // 共享 helper：仅部分测试 target 使用
    pub fn tracked_count(&self) -> usize {
        self.tracked.len()
    }

    /// 测试专用：注入下一次句柄复制失败（确定性故障注入）。
    #[allow(dead_code)] // 共享 helper：仅部分测试 target 使用
    pub fn fail_next_duplicate_for_tests(&mut self, message: &str) {
        self.fail_next_duplicate_for_tests = Some(message.to_string());
    }

    /// 测试专用：让下一次登记失败后的首次清理保留 Child 给 Drop 兜底。
    #[allow(dead_code)]
    pub fn fail_next_cleanup_for_tests(&mut self) {
        self.fail_next_cleanup_for_tests = true;
    }

    /// 测试专用：确定性覆盖 force_kill 的 wait timeout 分支。
    #[allow(dead_code)]
    pub fn force_next_wait_timeout_for_tests(&mut self) {
        self.force_next_wait_timeout_for_tests = true;
    }

    fn duplicate_with_injection(
        &mut self,
        child: &Child,
    ) -> std::io::Result<wuji_windows::ProcessHandle> {
        if let Some(message) = self.fail_next_duplicate_for_tests.take() {
            return Err(std::io::Error::other(message));
        }
        wuji_windows::ProcessHandle::duplicate_from_child(child)
    }

    fn register_handle(
        &mut self,
        pid: u32,
        handle: wuji_windows::ProcessHandle,
    ) -> TrackedProcessKey {
        let key = TrackedProcessKey(self.next_key);
        self.next_key += 1;
        self.tracked.push(TrackedProcess { key, pid, handle });
        key
    }

    fn register(&mut self, child: &Child) -> std::io::Result<TrackedProcessKey> {
        let handle = self.duplicate_with_injection(child)?;
        Ok(self.register_handle(child.id(), handle))
    }

    fn terminate_child_and_confirm(child: &mut Child, timeout: Duration) -> std::io::Result<()> {
        let kill_error = child.kill().err();
        let deadline = Instant::now() + timeout;
        loop {
            match child.try_wait() {
                Ok(Some(_)) => return Ok(()),
                Ok(None) if Instant::now() < deadline => {
                    std::thread::sleep(Duration::from_millis(50));
                }
                Ok(None) => {
                    let suffix = kill_error
                        .map(|e| format!("；kill 首次失败: {e}"))
                        .unwrap_or_default();
                    return Err(std::io::Error::new(
                        std::io::ErrorKind::TimedOut,
                        format!("等待未登记 Child 退出超时{suffix}"),
                    ));
                }
                Err(wait_error) => {
                    let suffix = kill_error
                        .map(|e| format!("；kill 首次失败: {e}"))
                        .unwrap_or_default();
                    return Err(std::io::Error::other(format!(
                        "确认未登记 Child 退出失败: {wait_error}{suffix}"
                    )));
                }
            }
        }
    }

    fn handle_registration_failure(
        &mut self,
        mut child: Child,
        registration_error: std::io::Error,
    ) -> std::io::Error {
        let cleanup_result = if self.fail_next_cleanup_for_tests {
            self.fail_next_cleanup_for_tests = false;
            Err(std::io::Error::other("注入：首次 Child 清理失败"))
        } else {
            Self::terminate_child_and_confirm(&mut child, Duration::from_secs(5))
        };
        match cleanup_result {
            Ok(()) => registration_error,
            Err(cleanup_error) => {
                self.emergency_children.push(child);
                std::io::Error::other(format!(
                    "{registration_error}；首次清理未确认退出，已由 guard 保留 Child 责任: {cleanup_error}"
                ))
            }
        }
    }

    /// 统一 spawn：创建 → 立即 DuplicateHandle 复制 → 登记成功才返回 Child 与稳定 key；
    /// 任一步失败用仍持有的 Child 执行 kill + wait（无孤儿窗口）。
    pub fn spawn_tracked_agent(
        &mut self,
        capture_on_start: bool,
    ) -> std::io::Result<(Child, TrackedProcessKey)> {
        let child = agent_command(&self.channel, capture_on_start)
            .spawn()
            .map_err(|e| std::io::Error::other(format!("spawn agent 失败: {e}")))?;
        match self.register(&child) {
            Ok(key) => Ok((child, key)),
            Err(error) => Err(self.handle_registration_failure(child, error)),
        }
    }

    /// detached 原生启动（无 PowerShell、无身份文件、无 launcher 超时）：
    /// 原生 spawn → 立即 DuplicateHandle → 登记 → 丢弃原 Child（父进程句柄释放，
    /// 模拟启动器退出；std Child 的 Drop 不终止进程）。
    /// creation flags 与生产 agent_controller 一致（09 §9.3）：
    /// DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP | CREATE_NO_WINDOW，
    /// 不继承父进程 console/句柄。
    #[allow(dead_code)] // 共享 helper：agent_guard 使用，agent_e2e 改走真实 launcher-exit 路径
    pub fn launch_detached_tracked(
        &mut self,
        capture_on_start: bool,
    ) -> std::io::Result<TrackedProcessKey> {
        let mut command = agent_command(&self.channel, capture_on_start);
        configure_detached(&mut command);
        let child = command
            .spawn()
            .map_err(|e| std::io::Error::other(format!("detached spawn 失败: {e}")))?;
        let key = match self.register(&child) {
            Ok(key) => key,
            Err(error) => return Err(self.handle_registration_failure(child, error)),
        };
        // 丢弃原 Child（句柄释放，模拟启动器退出）；复制句柄仍由 guard 持有。
        drop(child);
        Ok(key)
    }

    /// 真实 launcher 进程创建 Agent、将其句柄复制到当前测试进程后退出。
    ///
    /// helper 在收到 ack 前保留原始 Agent Child 句柄；当前进程先从 helper
    /// 地址空间 DuplicateHandle，完成登记后才 ack，并确认 helper 实际退出。
    #[allow(dead_code)]
    pub fn launch_via_exiting_parent_tracked(
        &mut self,
        capture_on_start: bool,
        helper_test_name: &str,
    ) -> std::io::Result<TrackedProcessKey> {
        let dir = data_root(&self.channel);
        std::fs::create_dir_all(&dir)?;
        let handoff = dir.join("launcher-handoff.txt");
        let ack = dir.join("launcher-ack.txt");
        let cancel = dir.join("launcher-cancel.txt");
        let launcher_log = std::fs::File::create(dir.join("launcher-stderr.log"))?;
        let _ = std::fs::remove_file(&handoff);
        let _ = std::fs::remove_file(&ack);
        let _ = std::fs::remove_file(&cancel);

        let mut launcher = Command::new(std::env::current_exe()?);
        launcher
            .args(["--exact", helper_test_name, "--nocapture"])
            .env("WUJI_TEST_LAUNCHER_HELPER", "1")
            .env("WUJI_TEST_LAUNCHER_CHANNEL", &self.channel)
            .env(
                "WUJI_TEST_LAUNCHER_CAPTURE",
                if capture_on_start { "1" } else { "0" },
            )
            .env("WUJI_TEST_LAUNCHER_HANDOFF", &handoff)
            .env("WUJI_TEST_LAUNCHER_ACK", &ack)
            .env("WUJI_TEST_LAUNCHER_CANCEL", &cancel)
            .stdin(std::process::Stdio::null())
            .stdout(std::process::Stdio::null())
            .stderr(std::process::Stdio::from(launcher_log));
        let mut launcher = launcher.spawn()?;

        let deadline = Instant::now() + Duration::from_secs(15);
        let content = loop {
            if let Ok(content) = std::fs::read_to_string(&handoff)
                && !content.trim().is_empty()
            {
                break content;
            }
            if let Some(status) = launcher.try_wait()? {
                return Err(std::io::Error::other(format!(
                    "launcher 在交付 Agent 句柄前退出: {status}"
                )));
            }
            if Instant::now() >= deadline {
                let cleanup =
                    Self::cancel_launcher_and_wait(&mut launcher, &cancel, Duration::from_secs(20));
                return Err(std::io::Error::new(
                    std::io::ErrorKind::TimedOut,
                    format!("等待 launcher 句柄交付超时；launcher 清理结果: {cleanup:?}"),
                ));
            }
            std::thread::sleep(Duration::from_millis(50));
        };

        let mut parts = content.trim().split('|');
        let pid_result = parts
            .next()
            .and_then(|value| value.parse::<u32>().ok())
            .ok_or_else(|| std::io::Error::other("launcher handoff PID 无法解析"));
        let remote_handle_result = parts
            .next()
            .and_then(|value| value.parse::<usize>().ok())
            .ok_or_else(|| std::io::Error::other("launcher handoff HANDLE 无法解析"));
        let (pid, remote_handle) = match (pid_result, remote_handle_result) {
            (Ok(pid), Ok(remote_handle)) => (pid, remote_handle),
            (pid, remote_handle) => {
                let cleanup =
                    Self::cancel_launcher_and_wait(&mut launcher, &cancel, Duration::from_secs(20));
                return Err(std::io::Error::other(format!(
                    "launcher handoff 无效（PID={pid:?}, HANDLE={remote_handle:?}）；launcher 清理结果: {cleanup:?}"
                )));
            }
        };
        let handle = match wuji_windows::ProcessHandle::duplicate_from_remote_process(
            &launcher,
            remote_handle,
        ) {
            Ok(handle) => handle,
            Err(error) => {
                let cleanup =
                    Self::cancel_launcher_and_wait(&mut launcher, &cancel, Duration::from_secs(20));
                return Err(std::io::Error::other(format!(
                    "跨进程复制 Agent 句柄失败: {error}；launcher 清理结果: {cleanup:?}"
                )));
            }
        };
        let key = self.register_handle(pid, handle);

        if let Err(error) = std::fs::write(&ack, b"ok") {
            let cleanup = self.force_kill_and_wait(key, Duration::from_secs(5));
            let launcher_cleanup =
                Self::cancel_launcher_and_wait(&mut launcher, &cancel, Duration::from_secs(20));
            return Err(std::io::Error::other(format!(
                "写 launcher ack 失败: {error}；Agent 清理结果: {cleanup:?}；launcher 清理结果: {launcher_cleanup:?}"
            )));
        }

        let deadline = Instant::now() + Duration::from_secs(10);
        let status = loop {
            match launcher.try_wait()? {
                Some(status) => break status,
                None if Instant::now() < deadline => {
                    std::thread::sleep(Duration::from_millis(50));
                }
                None => {
                    let _ = launcher.kill();
                    let _ = launcher.wait();
                    let cleanup = self.force_kill_and_wait(key, Duration::from_secs(5));
                    return Err(std::io::Error::new(
                        std::io::ErrorKind::TimedOut,
                        format!("launcher 未按期退出；Agent 清理结果: {cleanup:?}"),
                    ));
                }
            }
        };
        if !status.success() {
            let cleanup = self.force_kill_and_wait(key, Duration::from_secs(5));
            return Err(std::io::Error::other(format!(
                "launcher 退出失败: {status}；Agent 清理结果: {cleanup:?}"
            )));
        }
        let _ = std::fs::remove_file(&handoff);
        let _ = std::fs::remove_file(&ack);
        let _ = std::fs::remove_file(&cancel);
        Ok(key)
    }

    fn cancel_launcher_and_wait(
        launcher: &mut Child,
        cancel: &std::path::Path,
        timeout: Duration,
    ) -> std::io::Result<()> {
        std::fs::write(cancel, b"cancel")?;
        let deadline = Instant::now() + timeout;
        loop {
            match launcher.try_wait()? {
                Some(_) => return Ok(()),
                None if Instant::now() < deadline => {
                    std::thread::sleep(Duration::from_millis(50));
                }
                None => {
                    return Err(std::io::Error::new(
                        std::io::ErrorKind::TimedOut,
                        "launcher 收到 cancel 后仍未退出",
                    ));
                }
            }
        }
    }

    /// 由独立 launcher 测试进程执行：创建 Agent，交付原始句柄数值，收到 ack 后退出。
    #[allow(dead_code)]
    pub fn run_launcher_helper_from_env() -> std::io::Result<()> {
        if std::env::var_os("WUJI_TEST_LAUNCHER_HELPER").is_none() {
            return Ok(());
        }
        let channel = std::env::var("WUJI_TEST_LAUNCHER_CHANNEL")
            .map_err(|e| std::io::Error::other(format!("缺少 launcher channel: {e}")))?;
        let capture_on_start = std::env::var("WUJI_TEST_LAUNCHER_CAPTURE").as_deref() == Ok("1");
        let handoff = PathBuf::from(
            std::env::var_os("WUJI_TEST_LAUNCHER_HANDOFF")
                .ok_or_else(|| std::io::Error::other("缺少 launcher handoff 路径"))?,
        );
        let ack = PathBuf::from(
            std::env::var_os("WUJI_TEST_LAUNCHER_ACK")
                .ok_or_else(|| std::io::Error::other("缺少 launcher ack 路径"))?,
        );
        let cancel = PathBuf::from(
            std::env::var_os("WUJI_TEST_LAUNCHER_CANCEL")
                .ok_or_else(|| std::io::Error::other("缺少 launcher cancel 路径"))?,
        );

        let mut command = agent_command(&channel, capture_on_start);
        configure_detached(&mut command);
        let mut agent = command.spawn()?;
        #[cfg(windows)]
        let raw_handle = {
            use std::os::windows::io::AsRawHandle;
            agent.as_raw_handle() as usize
        };
        #[cfg(not(windows))]
        let raw_handle = 0_usize;

        let temp = handoff.with_extension("tmp");
        if let Err(error) = std::fs::write(&temp, format!("{}|{raw_handle}", agent.id()))
            .and_then(|_| std::fs::rename(&temp, &handoff))
        {
            let cleanup = Self::terminate_child_and_confirm(&mut agent, Duration::from_secs(5));
            return Err(std::io::Error::other(format!(
                "写 launcher handoff 失败: {error}；Agent 清理结果: {cleanup:?}"
            )));
        }

        let deadline = Instant::now() + Duration::from_secs(15);
        while !ack.exists() {
            if cancel.exists() {
                Self::terminate_child_and_confirm(&mut agent, Duration::from_secs(5))?;
                return Err(std::io::Error::other(
                    "主测试取消 launcher 句柄交付，Agent 已清理",
                ));
            }
            if Instant::now() >= deadline {
                return Self::terminate_child_and_confirm(&mut agent, Duration::from_secs(5))
                    .and_then(|()| {
                        Err(std::io::Error::new(
                            std::io::ErrorKind::TimedOut,
                            "等待 launcher ack 超时",
                        ))
                    });
            }
            std::thread::sleep(Duration::from_millis(50));
        }
        // 返回测试函数并让 launcher 测试进程真实退出；Agent 继续由主测试进程中的复制句柄管理。
        drop(agent);
        Ok(())
    }

    /// 确认退出后注销（按稳定 key）。
    pub fn untrack(&mut self, key: TrackedProcessKey) {
        self.tracked.retain(|t| t.key != key);
    }

    /// 指定 key 的进程是否仍存活（测试断言用）。
    #[allow(dead_code)] // 共享 helper：仅部分测试 target 使用
    pub fn is_alive(&self, key: TrackedProcessKey) -> std::io::Result<bool> {
        self.tracked
            .iter()
            .find(|t| t.key == key)
            .ok_or_else(|| std::io::Error::other("身份未登记"))
            .and_then(|t| t.handle.is_alive())
    }

    /// 同一 PipeClient 上完成 hello → agent_shutdown_dev → 校验 → 等待（复审 P1-01）。
    /// 连接可能撞上服务端 accept 间隙（PIPE_BUSY），做有上限重试；
    /// 重试成功后 hello 与 shutdown 仍在同一连接完成。
    pub fn try_graceful_shutdown(&self, timeout: Duration) -> ShutdownOutcome {
        let mut any_alive = false;
        for tracked in &self.tracked {
            match tracked.handle.is_alive() {
                Ok(true) => any_alive = true,
                Ok(false) => {}
                Err(error) => {
                    return ShutdownOutcome::Failed(format!(
                        "查询 PID {} 状态失败: {error}",
                        tracked.pid
                    ));
                }
            }
        }
        if !any_alive && PipeClient::connect(&pipe_name(&self.channel)).is_err() {
            return ShutdownOutcome::NotRunning;
        }
        let deadline = Instant::now() + Duration::from_secs(5);
        let mut client = loop {
            match PipeClient::connect(&pipe_name(&self.channel)) {
                Ok(client) => break client,
                Err(_) if Instant::now() < deadline => {
                    std::thread::sleep(Duration::from_millis(100));
                }
                Err(_) => {
                    return ShutdownOutcome::Failed("pipe 无法连接但有存活句柄".to_string());
                }
            }
        };
        let hello = client.hello(&self.channel);
        if hello["ok"] != true {
            return ShutdownOutcome::Failed(format!("hello 被拒绝: {hello}"));
        }
        let shutdown = client.call(&ulid(), "agent_shutdown_dev", serde_json::json!({}));
        if shutdown["ok"] != true {
            return ShutdownOutcome::Failed(format!("shutdown 被拒绝: {shutdown}"));
        }
        if shutdown["result"]["willExit"] != true {
            return ShutdownOutcome::Failed(format!("shutdown 缺少 willExit: {shutdown}"));
        }
        // 退出与否只以句柄为准（复审 §7）：pipe 断开不作为退出证明。
        // WAIT_FAILED 必须保留具体诊断，不折叠成普通的“尚未退出”。
        for tracked in &self.tracked {
            match tracked.handle.wait_exit(timeout) {
                Ok(wuji_windows::ProcessWaitOutcome::Exited) => {}
                Ok(wuji_windows::ProcessWaitOutcome::Timeout) => {
                    return ShutdownOutcome::Failed(format!(
                        "shutdown 后 PID {} 的句柄等待超时",
                        tracked.pid
                    ));
                }
                Err(error) => {
                    return ShutdownOutcome::Failed(format!(
                        "shutdown 后等待 PID {} 句柄失败: {error}",
                        tracked.pid
                    ));
                }
            }
        }
        ShutdownOutcome::Exited
    }

    /// 强杀并确认退出（复审 P1-03/五）：身份已登记 →（存活才）terminate →
    /// 句柄 wait_exit 确认消失 → 全部成立才 untrack；
    /// 任一步失败保留登记并返回错误，由 Drop 兜底。
    pub fn force_kill_and_wait(
        &mut self,
        key: TrackedProcessKey,
        timeout: Duration,
    ) -> Result<(), String> {
        let Some(pos) = self.tracked.iter().position(|t| t.key == key) else {
            return Err("身份未登记，拒绝强杀".to_string());
        };
        let handle = &self.tracked[pos].handle;
        if handle
            .is_alive()
            .map_err(|e| format!("查询进程状态失败（保留登记）: {e}"))?
            && let Err(terminate_error) = handle.terminate(1)
        {
            // terminate 失败时先确认真实状态：进程可能已在终止中
            // （如上一次 terminate 已生效但尚未 signaled），等待其退出再判断。
            match handle.wait_exit(timeout) {
                Ok(wuji_windows::ProcessWaitOutcome::Exited) => {}
                Ok(wuji_windows::ProcessWaitOutcome::Timeout) => {
                    return Err(format!(
                        "强杀调用失败且进程未退出（保留登记）: {terminate_error}"
                    ));
                }
                Err(e) => {
                    return Err(format!("强杀失败后确认退出失败（保留登记）: {e}"));
                }
            }
            // 已确认退出：跳过下方的重复 wait，直接注销。
            self.tracked.remove(pos);
            return Ok(());
        }
        if self.force_next_wait_timeout_for_tests {
            self.force_next_wait_timeout_for_tests = false;
            return Err("注入：强杀后等待退出超时（保留登记）".to_string());
        }
        match handle
            .wait_exit(timeout)
            .map_err(|e| format!("等待退出失败（保留登记）: {e}"))?
        {
            wuji_windows::ProcessWaitOutcome::Exited => {
                self.tracked.remove(pos);
                Ok(())
            }
            wuji_windows::ProcessWaitOutcome::Timeout => {
                Err("强杀后等待退出超时（保留登记）".to_string())
            }
        }
    }
}

impl Drop for TestAgentGuard {
    fn drop(&mut self) {
        // 1) 仍有存活句柄：先同连接优雅退出，失败留诊断（Drop 不 panic）。
        let mut any_alive = !self.emergency_children.is_empty();
        let mut initial_query_failed = false;
        for tracked in &self.tracked {
            match tracked.handle.is_alive() {
                Ok(true) => any_alive = true,
                Ok(false) => {}
                Err(error) => {
                    initial_query_failed = true;
                    eprintln!(
                        "guard: 初始查询 PID {} 状态失败（仍尝试清理）: {error}",
                        tracked.pid
                    );
                }
            }
        }
        if (any_alive || initial_query_failed)
            && let ShutdownOutcome::Failed(message) =
                self.try_graceful_shutdown(Duration::from_secs(10))
        {
            eprintln!("guard: 优雅退出失败（{message}），进入句柄强杀兜底");
        }
        // 2) 对每个仍存活句柄强制终止并确认。
        let mut all_exited = true;
        for tracked in &self.tracked {
            let alive = match tracked.handle.is_alive() {
                Ok(alive) => alive,
                Err(e) => {
                    all_exited = false;
                    eprintln!(
                        "guard: 查询 PID {} 状态失败（保留责任与目录）: {e}",
                        tracked.pid
                    );
                    continue;
                }
            };
            if !alive {
                continue;
            }
            if let Err(terminate_error) = tracked.handle.terminate(1) {
                match tracked.handle.wait_exit(Duration::from_secs(5)) {
                    Ok(wuji_windows::ProcessWaitOutcome::Exited) => continue,
                    Ok(wuji_windows::ProcessWaitOutcome::Timeout) => {
                        all_exited = false;
                        eprintln!(
                            "guard: 强制终止 PID {} 失败且进程未退出: {terminate_error}",
                            tracked.pid
                        );
                        continue;
                    }
                    Err(wait_error) => {
                        all_exited = false;
                        eprintln!(
                            "guard: 强制终止 PID {} 失败，确认状态也失败: {terminate_error}；{wait_error}",
                            tracked.pid
                        );
                        continue;
                    }
                }
            }
            match tracked.handle.wait_exit(Duration::from_secs(5)) {
                Ok(wuji_windows::ProcessWaitOutcome::Exited) => {}
                Ok(wuji_windows::ProcessWaitOutcome::Timeout) => {
                    all_exited = false;
                    eprintln!("guard: PID {} 强杀后仍未退出（残留风险）", tracked.pid);
                }
                Err(e) => {
                    all_exited = false;
                    eprintln!("guard: 等待 PID {} 退出失败: {e}", tracked.pid);
                }
            }
        }
        // 3) 登记失败时保留下来的原始 Child 仍由 guard 精确负责。
        for child in &mut self.emergency_children {
            if let Err(error) = Self::terminate_child_and_confirm(child, Duration::from_secs(5)) {
                all_exited = false;
                eprintln!(
                    "guard: 未登记 Child PID {} 最终清理失败（保留目录）: {error}",
                    child.id()
                );
            }
        }
        // 4) 只有全部确认退出才删除目录；否则保留诊断目录（复审 §7）。
        if all_exited {
            cleanup(&self.channel);
        } else {
            eprintln!(
                "guard: 存在未确认退出的进程，保留 channel 目录 {} 供诊断",
                self.channel
            );
        }
    }
}

fn configure_detached(command: &mut Command) {
    const DETACHED_PROCESS: u32 = 0x0000_0008;
    const CREATE_NEW_PROCESS_GROUP: u32 = 0x0000_0200;
    const CREATE_NO_WINDOW: u32 = 0x0800_0000;

    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        command.creation_flags(DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP | CREATE_NO_WINDOW);
    }
    #[cfg(not(windows))]
    let _ = command;
}
