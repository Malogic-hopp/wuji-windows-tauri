//! 第七/八轮复审：进程身份守卫（原生 DuplicateHandle 方案）的确定性回归测试。
//!
//! 覆盖：稳定 key、exit 后 untrack、未登记拒绝强杀、
//! 并行同二进制 channel 互不误杀、同连接优雅退出 + 句柄确认、
//! detached 立即 drop 清理、复制失败故障注入（spawn/detached 两路径）、
//! detached 丢弃原 Child 后 Agent 仍在线、强杀超时保留登记、
//! 先确认退出再离线读取。

mod common;

use std::time::Duration;

use common::*;
use rusqlite::Connection;
use wuji_rebuild_agent::command_server::client::PipeClient;

fn connect_pipe(channel: &str, timeout: Duration) -> PipeClient {
    let pipe = pipe_name(channel);
    let deadline = std::time::Instant::now() + timeout;
    loop {
        match PipeClient::connect(&pipe) {
            Ok(client) => return client,
            Err(_) if std::time::Instant::now() < deadline => {
                std::thread::sleep(Duration::from_millis(200));
            }
            Err(error) => panic!("无法连接 agent pipe: {error}"),
        }
    }
}

fn wait_for<F: FnMut() -> bool>(mut condition: F, timeout: Duration, what: &str) {
    let deadline = std::time::Instant::now() + timeout;
    while std::time::Instant::now() < deadline {
        if condition() {
            return;
        }
        std::thread::sleep(Duration::from_millis(200));
    }
    panic!("等待超时: {what}");
}

fn agent_alive(channel: &str) -> bool {
    let deadline = std::time::Instant::now() + Duration::from_secs(8);
    loop {
        let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
            PipeClient::connect(&pipe_name(channel)).map(|mut client| client.hello(channel))
        }));
        if let Ok(Ok(hello)) = &result
            && hello["ok"] == true
        {
            return true;
        }
        if std::time::Instant::now() >= deadline {
            return false;
        }
        std::thread::sleep(Duration::from_millis(150));
    }
}

/// 场景 1+2：稳定 key；确认退出后 untrack；同一 PID 的新旧身份是不同 key。
#[test]
fn stable_keys_and_untrack_after_confirmed_exit() {
    let channel = test_channel();
    let mut guard = TestAgentGuard::new(&channel);
    let (mut agent, agent_key) = guard.spawn_tracked_agent(false).unwrap();
    // 确认退出后注销。
    let mut client = connect_pipe(&channel, Duration::from_secs(15));
    assert_eq!(client.hello(&channel)["ok"], true);
    let shutdown = client.call(&ulid(), "agent_shutdown_dev", serde_json::json!({}));
    assert_eq!(shutdown["ok"], true);
    wait_for(
        || agent.try_wait().map(|s| s.is_some()).unwrap_or(true),
        Duration::from_secs(15),
        "agent 退出",
    );
    let _ = agent.kill();
    let _ = agent.wait();
    guard.untrack(agent_key);
    assert_eq!(guard.tracked_count(), 0);

    // 新启动的进程即使复用 PID，也是新的稳定 key（不得去重为同一项）。
    let (mut agent2, agent2_key) = guard.spawn_tracked_agent(false).unwrap();
    assert_ne!(
        format!("{:?}", agent_key),
        format!("{:?}", agent2_key),
        "新旧身份必须是不同 key"
    );
    assert_eq!(guard.tracked_count(), 1);
    let mut client = connect_pipe(&channel, Duration::from_secs(15));
    assert_eq!(client.hello(&channel)["ok"], true);
    let shutdown = client.call(&ulid(), "agent_shutdown_dev", serde_json::json!({}));
    assert_eq!(shutdown["ok"], true);
    wait_for(
        || agent2.try_wait().map(|s| s.is_some()).unwrap_or(true),
        Duration::from_secs(15),
        "agent2 退出",
    );
    let _ = agent2.kill();
    let _ = agent2.wait();
    guard.untrack(agent2_key);
    assert_eq!(guard.tracked_count(), 0);
}

/// 场景 3+9：未登记身份拒绝强杀；已退出身份按已退出处理且不触碰其他进程。
#[test]
fn force_kill_rejects_unknown_or_exited_identity() {
    let channel = test_channel();
    let mut guard = TestAgentGuard::new(&channel);
    let (mut agent, agent_key) = guard.spawn_tracked_agent(false).unwrap();

    // 未登记 key：拒绝，且不影响任何进程。
    let error = guard
        .force_kill_and_wait(TrackedProcessKey::bogus_for_tests(), Duration::from_secs(1))
        .expect_err("未登记身份必须拒绝");
    assert!(error.contains("未登记"));
    assert!(agent_alive(&channel), "误伤后目标必须仍在线");

    // 正常退出后再强杀：按"已退出"处理。
    let mut client = connect_pipe(&channel, Duration::from_secs(15));
    assert_eq!(client.hello(&channel)["ok"], true);
    let shutdown = client.call(&ulid(), "agent_shutdown_dev", serde_json::json!({}));
    assert_eq!(shutdown["ok"], true);
    wait_for(
        || agent.try_wait().map(|s| s.is_some()).unwrap_or(true),
        Duration::from_secs(15),
        "agent 退出",
    );
    guard
        .force_kill_and_wait(agent_key, Duration::from_secs(5))
        .expect("已退出身份按已退出处理");
    assert_eq!(guard.tracked_count(), 0);
    let _ = agent.kill();
    let _ = agent.wait();
}

/// 场景 5+11：两个不同 channel、同一 Agent 二进制并行——A 的清理只终止 A。
#[test]
fn parallel_channels_guard_kills_only_its_own_agent() {
    let channel_a = test_channel();
    let channel_b = test_channel();
    let agent_b;
    {
        let mut guard_a = TestAgentGuard::new(&channel_a);
        let mut guard_b = TestAgentGuard::new(&channel_b);
        let (mut agent_a, _agent_a_key) = guard_a.spawn_tracked_agent(false).unwrap();
        agent_b = Some(guard_b.spawn_tracked_agent(false).unwrap().0);

        let _ = connect_pipe(&channel_a, Duration::from_secs(15));
        let _ = connect_pipe(&channel_b, Duration::from_secs(15));
        // 模拟 A 测试 panic：guard_a 在此 drop。
        drop(guard_a);
        assert!(
            agent_a.try_wait().map(|s| s.is_some()).unwrap_or(false),
            "A 必须被 guard 终止"
        );
        assert!(agent_alive(&channel_b), "B 必须不受影响仍在线");
        // guard_b 在此 drop，清理 B。
        drop(guard_b);
    }
    if let Some(mut agent) = agent_b {
        let _ = agent.kill();
        let _ = agent.wait();
    }
    assert!(!agent_alive(&channel_a));
    assert!(!agent_alive(&channel_b));
}

/// 场景 6+10：同一连接依次 hello → shutdown；进程退出以句柄确认。
#[test]
fn graceful_shutdown_uses_single_connection_and_handle_confirms() {
    let channel = test_channel();
    let mut guard = TestAgentGuard::new(&channel);
    let (mut agent, agent_key) = guard.spawn_tracked_agent(false).unwrap();
    let _ = connect_pipe(&channel, Duration::from_secs(15));

    let outcome = guard.try_graceful_shutdown(Duration::from_secs(15));
    assert_eq!(
        outcome,
        ShutdownOutcome::Exited,
        "同连接优雅退出 + 句柄确认必须成功"
    );
    wait_for(
        || agent.try_wait().map(|s| s.is_some()).unwrap_or(true),
        Duration::from_secs(10),
        "agent 确认退出",
    );
    let _ = agent.kill();
    let _ = agent.wait();
    guard.untrack(agent_key);
}

/// 场景 7+12：detached 启动后立即"panic"（提前 drop 守卫）→ 必须清理且残留为 0。
#[test]
fn detached_launch_then_immediate_drop_cleans_up() {
    let channel = test_channel();
    {
        let mut guard = TestAgentGuard::new(&channel);
        let _key = guard.launch_detached_tracked(false).unwrap();
        assert!(agent_alive(&channel));
        // 模拟测试立即 panic：守卫在此 drop。
    }
    assert!(!agent_alive(&channel), "detached Agent 必须被守卫清理");
    assert!(!data_root(&channel).exists(), "channel 目录必须可清理");
}

/// 场景 3（注入）：spawn 路径句柄复制失败 → 立即 kill + wait，无残留。
#[test]
fn duplicate_failure_injection_kills_spawned_child() {
    let channel = test_channel();
    let mut guard = TestAgentGuard::new(&channel);
    guard.fail_next_duplicate_for_tests("注入：复制句柄失败");
    let error = guard
        .spawn_tracked_agent(false)
        .expect_err("复制失败必须返回错误");
    assert!(error.to_string().contains("复制句柄失败"));
    assert_eq!(guard.tracked_count(), 0, "失败不得登记");
    assert!(
        !agent_alive(&channel),
        "复制失败时子进程必须已被 kill + wait（无孤儿）"
    );
}

/// 场景 4（注入）：detached 路径句柄复制失败 → 立即 kill + wait，无残留。
#[test]
fn duplicate_failure_injection_kills_detached_child() {
    let channel = test_channel();
    let mut guard = TestAgentGuard::new(&channel);
    guard.fail_next_duplicate_for_tests("注入：复制句柄失败");
    let error = guard
        .launch_detached_tracked(false)
        .expect_err("复制失败必须返回错误");
    assert!(error.to_string().contains("复制句柄失败"));
    assert_eq!(guard.tracked_count(), 0);
    assert!(!agent_alive(&channel), "detached 复制失败不得留下孤儿");
}

/// 登记失败后的首次清理也失败：guard 必须保留原始 Child，并在 Drop 最终清理。
#[test]
fn registration_cleanup_failure_keeps_child_responsibility_until_drop() {
    let channel = test_channel();
    {
        let mut guard = TestAgentGuard::new(&channel);
        guard.fail_next_duplicate_for_tests("注入：复制句柄失败");
        guard.fail_next_cleanup_for_tests();
        let error = guard
            .spawn_tracked_agent(false)
            .expect_err("复制与首次清理失败必须返回组合错误");
        assert!(
            error.to_string().contains("已由 guard 保留 Child 责任"),
            "错误必须说明清理责任仍由 guard 持有: {error}"
        );
        assert_eq!(guard.tracked_count(), 0, "不得伪装为已登记身份");
    }
    assert!(!agent_alive(&channel), "Drop 必须清理 emergency Child");
    assert!(!data_root(&channel).exists(), "确认退出后才清理目录");
}

/// 场景 5：detached 登记成功后原 Child 已丢弃（启动器退出语义），Agent 仍在线。
#[test]
fn detached_spawn_drops_original_child_and_agent_stays_alive() {
    let channel = test_channel();
    let mut guard = TestAgentGuard::new(&channel);
    let key = guard.launch_detached_tracked(false).unwrap();
    // 原 Child 已在 launch 内 drop（父进程句柄释放）：Agent 必须继续运行。
    assert!(agent_alive(&channel), "父句柄释放后 Agent 必须仍在线");
    guard
        .force_kill_and_wait(key, Duration::from_secs(15))
        .expect("强杀并确认退出");
    assert!(!agent_alive(&channel));
}

/// 场景 8+9：强杀等待超时保留登记；恢复后可完成清理。
#[test]
fn force_kill_timeout_keeps_tracked_then_recovers() {
    let channel = test_channel();
    let mut guard = TestAgentGuard::new(&channel);
    let key = guard.launch_detached_tracked(false).unwrap();
    // 确定性注入 wait timeout：不依赖 TerminateProcess 的实际完成时序。
    guard.force_next_wait_timeout_for_tests();
    let error = guard
        .force_kill_and_wait(key, Duration::from_secs(15))
        .expect_err("超时必须返回错误并保留登记");
    assert!(error.contains("超时"));
    assert_eq!(guard.tracked_count(), 1, "失败时不得 untrack");
    // 恢复：再次强杀并确认（进程已被上次 terminate 终止，wait 应成功）。
    guard
        .force_kill_and_wait(key, Duration::from_secs(15))
        .expect("恢复后必须成功");
    assert_eq!(guard.tracked_count(), 0);
    assert!(!agent_alive(&channel));
}

/// 场景 11：crash/offline 必须先由句柄确认退出，再验证 SQLite 离线读取。
#[test]
fn offline_read_requires_confirmed_exit_first() {
    let channel = test_channel();
    let mut guard = TestAgentGuard::new(&channel);
    let db = db_path(&channel);
    let key = guard.launch_detached_tracked(true).unwrap();
    let mut client = connect_pipe(&channel, Duration::from_secs(20));
    assert_eq!(client.hello(&channel)["ok"], true);
    wait_for(
        || {
            db.exists()
                && Connection::open(&db)
                    .unwrap()
                    .query_row("SELECT COUNT(*) FROM foreground_observations", [], |r| {
                        r.get::<_, i64>(0)
                    })
                    .unwrap()
                    > 0
        },
        Duration::from_secs(20),
        "Observation 落库",
    );

    // 先由句柄确认退出，再做离线读取。
    guard
        .force_kill_and_wait(key, Duration::from_secs(15))
        .expect("强杀并确认退出必须成功");
    assert!(!agent_alive(&channel));

    let count: i64 = Connection::open(&db)
        .unwrap()
        .query_row("SELECT COUNT(*) FROM foreground_observations", [], |r| {
            r.get(0)
        })
        .unwrap();
    assert!(count > 0, "离线读取必须能读出历史");
}
