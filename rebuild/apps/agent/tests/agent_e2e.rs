//! V01-5 Agent 运行时端到端测试（09 §11 V01-5 退出条件）。
//!
//! 覆盖：真实进程启动、capture 状态机、request ID 幂等、envelope 拒绝、
//! settings_reload、受控退出、崩溃恢复、单实例。

mod common;

use std::path::PathBuf;
use std::time::{Duration, Instant};

use common::*;
use rusqlite::Connection;
use wuji_rebuild_agent::command_server::client::PipeClient;

fn wait_for<F: FnMut() -> bool>(mut condition: F, timeout: Duration, what: &str) {
    let deadline = Instant::now() + timeout;
    while Instant::now() < deadline {
        if condition() {
            return;
        }
        std::thread::sleep(Duration::from_millis(200));
    }
    panic!("等待超时: {what}");
}

fn connect_pipe(channel: &str, timeout: Duration) -> PipeClient {
    let pipe = pipe_name(channel);
    let deadline = Instant::now() + timeout;
    loop {
        match PipeClient::connect(&pipe) {
            Ok(client) => return client,
            Err(_) if Instant::now() < deadline => {
                std::thread::sleep(Duration::from_millis(200));
            }
            Err(error) => panic!("无法连接 agent pipe: {error}"),
        }
    }
}

fn query_i64(db: &PathBuf, sql: &str) -> i64 {
    Connection::open(db)
        .unwrap()
        .query_row(sql, [], |r| r.get(0))
        .unwrap()
}

/// 独立 launcher 子进程入口。正常测试枚举时无环境变量，立即返回；
/// parent-exit 用例会单独启动本测试并设置握手环境变量。
#[test]
fn detached_parent_launcher_helper() {
    TestAgentGuard::run_launcher_helper_from_env()
        .expect("launcher helper 必须完成句柄交付或清理 Agent");
}

#[test]
fn agent_end_to_end_capture_fsm_ipc_and_shutdown() {
    let channel = test_channel();
    let mut guard = TestAgentGuard::new(&channel);
    let (mut agent, agent_key) = guard.spawn_tracked_agent(true).unwrap();
    let db = db_path(&channel);

    wait_for(|| db.exists(), Duration::from_secs(15), "数据库文件出现");
    let mut client = connect_pipe(&channel, Duration::from_secs(15));

    // hello 握手。
    let hello = client.hello(&channel);
    assert_eq!(hello["ok"], true, "hello 必须成功: {hello}");
    assert_eq!(hello["result"]["protocolVersion"], 1);
    assert_eq!(hello["result"]["schemaVersion"], 1);

    // status_get：capture-on-start 后应为 running。
    let status = client.call(&ulid(), "status_get", serde_json::json!({}));
    assert_eq!(status["ok"], true);
    assert_eq!(status["result"]["captureState"], "running");

    // 等待真实采集落库。
    wait_for(
        || db.exists() && query_i64(&db, "SELECT COUNT(*) FROM foreground_observations") > 0,
        Duration::from_secs(20),
        "Observation 落库",
    );

    // pause → 幂等 pause → paused 状态 start 拒绝 → resume → stop → 幂等 stop。
    let response = client.call(&ulid(), "capture_pause", serde_json::json!({}));
    assert_eq!(response["ok"], true);
    assert_eq!(response["result"]["captureState"], "paused");
    let response = client.call(&ulid(), "capture_pause", serde_json::json!({}));
    assert_eq!(response["ok"], true, "重复 pause 必须幂等成功");
    let response = client.call(&ulid(), "capture_start", serde_json::json!({}));
    assert_eq!(response["ok"], false);
    assert_eq!(response["error"]["code"], "CAPTURE_INVALID_STATE");
    let response = client.call(&ulid(), "capture_resume", serde_json::json!({}));
    assert_eq!(response["result"]["captureState"], "running");
    let response = client.call(&ulid(), "capture_stop", serde_json::json!({}));
    assert_eq!(response["result"]["captureState"], "stopped");
    let response = client.call(&ulid(), "capture_stop", serde_json::json!({}));
    assert_eq!(response["ok"], true, "重复 stop 必须幂等成功");

    // request ID 幂等：同 ID 同 payload 返回原响应；同 ID 不同 payload 拒绝。
    let id = ulid();
    let first = client.call(&id, "status_get", serde_json::json!({}));
    let second = client.call(&id, "status_get", serde_json::json!({}));
    assert_eq!(first, second, "同 ID 同 payload 必须返回原响应");
    let conflict = client.call(&id, "capture_stop", serde_json::json!({}));
    assert_eq!(conflict["ok"], false);
    assert_eq!(conflict["error"]["code"], "IPC_REQUEST_ID_REUSED");

    // envelope 拒绝路径。
    let unknown = client.call(&ulid(), "not_a_command", serde_json::json!({}));
    assert_eq!(unknown["error"]["code"], "IPC_INVALID_MESSAGE");
    let wrong_protocol = client.call_with_protocol(&ulid(), "status_get", serde_json::json!({}), 2);
    assert_eq!(wrong_protocol["error"]["code"], "IPC_PROTOCOL_UNSUPPORTED");

    // settings_reload：写入合法 revision 1 设置并应用。
    let settings = wuji_core::settings::Settings {
        revision: "1".to_string(),
        idle_threshold_seconds: 90,
        ..wuji_core::settings::Settings::default()
    };
    let settings_dir = data_root(&channel).join("config");
    std::fs::create_dir_all(&settings_dir).unwrap();
    std::fs::write(
        settings_dir.join("settings.json"),
        serde_json::to_string_pretty(&settings).unwrap(),
    )
    .unwrap();
    let reload = client.call(
        &ulid(),
        "settings_reload",
        serde_json::json!({
            "savedRevision": "1",
            "contentDigest": settings.content_digest(),
        }),
    );
    assert_eq!(reload["ok"], true, "settings_reload 必须成功: {reload}");
    assert_eq!(reload["result"]["appliedRevision"], "1");

    // 受控退出：open 行按 agent_shutdown 关闭，进程退出。
    let shutdown = client.call(&ulid(), "agent_shutdown_dev", serde_json::json!({}));
    assert_eq!(shutdown["ok"], true);
    assert_eq!(shutdown["result"]["willExit"], true);
    wait_for(
        || agent.try_wait().map(|s| s.is_some()).unwrap_or(true),
        Duration::from_secs(15),
        "agent 进程退出",
    );
    wait_for(
        || {
            query_i64(
                &db,
                "SELECT COUNT(*) FROM activity_segments WHERE status = 'open'",
            ) == 0
        },
        Duration::from_secs(10),
        "open 行全部关闭",
    );
    assert_eq!(
        query_i64(
            &db,
            "SELECT COUNT(*) FROM work_blocks WHERE status = 'open'"
        ),
        0,
        "受控退出后不得有 open Work Block"
    );

    let _ = agent.kill();
    let _ = agent.wait();
    guard.untrack(agent_key);
}

#[test]
fn agent_rejects_oversize_and_wrong_channel_hello() {
    let channel = test_channel();
    let mut guard = TestAgentGuard::new(&channel);
    let (mut agent, agent_key) = guard.spawn_tracked_agent(false).unwrap();
    let mut client = connect_pipe(&channel, Duration::from_secs(15));

    // 错误 channel 的 hello：拒绝并断开。
    let wrong = client.hello("wrong-channel");
    assert_eq!(wrong["ok"], false);
    assert_eq!(wrong["error"]["code"], "IPC_CHANNEL_MISMATCH");

    // 正确 hello 后发送超限消息：IPC_PAYLOAD_TOO_LARGE 并断开。
    let mut client = connect_pipe(&channel, Duration::from_secs(15));
    let hello = client.hello(&channel);
    assert_eq!(hello["ok"], true);
    let big_payload = "x".repeat(70 * 1024);
    let response = client.call(
        &ulid(),
        "status_get",
        serde_json::json!({ "blob": big_payload }),
    );
    assert_eq!(response["ok"], false);
    assert_eq!(response["error"]["code"], "IPC_PAYLOAD_TOO_LARGE");

    // 服务端在超限后已断开该连接；直接结束 agent，不再复用旧连接。
    let _ = agent.kill();
    let _ = agent.wait();
    guard.untrack(agent_key);
}

#[test]
fn agent_crash_restart_recovers_open_rows() {
    let channel = test_channel();
    let mut guard = TestAgentGuard::new(&channel);
    let db = db_path(&channel);

    {
        let (mut agent, agent_key) = guard.spawn_tracked_agent(true).unwrap();
        wait_for(|| db.exists(), Duration::from_secs(15), "数据库文件出现");
        let mut client = connect_pipe(&channel, Duration::from_secs(15));
        assert_eq!(client.hello(&channel)["ok"], true);
        wait_for(
            || db.exists() && query_i64(&db, "SELECT COUNT(*) FROM foreground_observations") > 1,
            Duration::from_secs(20),
            "Observation 落库",
        );
        // 崩溃：直接 kill，open 行遗留。
        agent.kill().expect("kill");
        agent.wait().expect("wait");
        guard.untrack(agent_key);
    }

    {
        let (mut agent, agent_key) = guard.spawn_tracked_agent(false).unwrap();
        let mut client = connect_pipe(&channel, Duration::from_secs(15));
        assert_eq!(client.hello(&channel)["ok"], true);

        // 启动恢复：遗留 open 行以 agent_restart 关闭，存在 agent_restart gap。
        wait_for(
            || {
                query_i64(
                    &db,
                    "SELECT COUNT(*) FROM capture_gaps WHERE kind = 'agent_restart'",
                ) >= 1
            },
            Duration::from_secs(15),
            "agent_restart gap 出现",
        );
        let open_rows: i64 = query_i64(
            &db,
            "SELECT (SELECT COUNT(*) FROM activity_segments WHERE status = 'open') +
                    (SELECT COUNT(*) FROM work_blocks WHERE status = 'open')",
        );
        assert_eq!(open_rows, 0, "恢复后不得遗留 open 行");
        let runtimes: i64 = query_i64(&db, "SELECT COUNT(*) FROM agent_runtime");
        assert!(
            runtimes >= 3,
            "bootstrap + 两次运行至少三个 runtime: {runtimes}"
        );
        let close_reason: String = {
            let conn = Connection::open(&db).unwrap();
            conn.query_row(
                "SELECT close_reason FROM activity_segments ORDER BY segment_id DESC LIMIT 1",
                [],
                |r| r.get(0),
            )
            .unwrap_or_default()
        };
        assert_eq!(close_reason, "agent_restart");

        let shutdown = client.call(&ulid(), "agent_shutdown_dev", serde_json::json!({}));
        assert_eq!(shutdown["ok"], true);
        wait_for(
            || agent.try_wait().map(|s| s.is_some()).unwrap_or(true),
            Duration::from_secs(15),
            "agent 进程退出",
        );
        let _ = agent.kill();
        let _ = agent.wait();
        guard.untrack(agent_key);
    }
}

#[test]
fn agent_rejects_invalid_utf8_and_malformed_hello() {
    let channel = test_channel();
    let mut guard = TestAgentGuard::new(&channel);
    let (mut agent, agent_key) = guard.spawn_tracked_agent(false).unwrap();

    // 非法 UTF-8：拒绝替换解码，回 IPC_INVALID_MESSAGE 并断开（R05）。
    {
        use std::io::{Read, Write};
        let pipe = pipe_name(&channel);
        let deadline = Instant::now() + Duration::from_secs(15);
        let mut file = loop {
            match std::fs::OpenOptions::new()
                .read(true)
                .write(true)
                .open(&pipe)
            {
                Ok(file) => break file,
                Err(_) if Instant::now() < deadline => {
                    std::thread::sleep(Duration::from_millis(200));
                }
                Err(error) => panic!("无法连接 agent pipe: {error}"),
            }
        };
        file.write_all(&[0xFF, 0xFE, b'\n']).unwrap();
        file.flush().unwrap();
        let mut buffer = [0_u8; 4096];
        let read = file.read(&mut buffer).unwrap();
        let response = std::str::from_utf8(&buffer[..read]).unwrap();
        assert!(
            response.contains("IPC_INVALID_MESSAGE"),
            "非法 UTF-8 必须拒绝而不是替换解码: {response}"
        );
    }

    // hello 缺 desktopVersion → IPC_INVALID_MESSAGE（hello 全字段校验，R05）。
    let mut client = connect_pipe(&channel, Duration::from_secs(15));
    let bad_hello = client.call_with_protocol(
        &ulid(),
        "hello",
        serde_json::json!({
            "protocolVersion": 1,
            "channel": channel,
        }),
        1,
    );
    assert_eq!(bad_hello["ok"], false);
    assert_eq!(bad_hello["error"]["code"], "IPC_INVALID_MESSAGE");

    let _ = agent.kill();
    let _ = agent.wait();
    guard.untrack(agent_key);
}

#[test]
fn second_instance_is_rejected() {
    let channel = test_channel();
    let mut guard = TestAgentGuard::new(&channel);
    let (mut first, first_key) = guard.spawn_tracked_agent(false).unwrap();
    let _client = connect_pipe(&channel, Duration::from_secs(15));

    let (mut second, second_key) = guard.spawn_tracked_agent(false).unwrap();
    wait_for(
        || second.try_wait().map(|s| s.is_some()).unwrap_or(true),
        Duration::from_secs(10),
        "第二个实例必须立即退出",
    );
    let status = second.wait().unwrap();
    guard.untrack(second_key);
    assert!(!status.success(), "第二个实例必须以失败退出");

    let mut client = connect_pipe(&channel, Duration::from_secs(5));
    assert_eq!(
        client.hello(&channel)["ok"],
        true,
        "shutdown 前必须先完成 hello"
    );
    let shutdown = client.call(&ulid(), "agent_shutdown_dev", serde_json::json!({}));
    assert_eq!(shutdown["ok"], true);
    wait_for(
        || first.try_wait().map(|s| s.is_some()).unwrap_or(true),
        Duration::from_secs(15),
        "第一个实例退出",
    );
    let _ = first.kill();
    let _ = first.wait();
    guard.untrack(first_key);
}

/// 门禁（审核 §7）：父进程退出后 Agent 存活（脱离 Desktop 生命周期）；
/// 崩溃后离线只读历史可用（ro reader 打开 WAL 库）。
/// 第六轮：detached 由守卫统一启动并登记身份；离线读取前必须先确认进程退出。
#[test]
fn agent_survives_parent_exit_and_offline_read_works_after_kill() {
    let channel = test_channel();
    let mut guard = TestAgentGuard::new(&channel);
    let db = db_path(&channel);

    // 独立 launcher 创建 Agent，并把原始进程句柄复制到本测试进程；
    // launch_via_exiting_parent_tracked 返回前已确认 launcher 进程真实退出。
    let agent_key = guard
        .launch_via_exiting_parent_tracked(true, "detached_parent_launcher_helper")
        .expect("真实父进程退出与句柄安全交付必须成功");
    let stderr_file = data_root(&channel).join("agent-stderr.log");

    // 独立启动器进程已经真实退出后，Agent 仍须在线（pipe 可连、hello 通过）。
    let mut client = connect_pipe(&channel, Duration::from_secs(20));
    let hello_result =
        std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| client.hello(&channel)));
    match hello_result {
        Ok(hello) if hello["ok"] == true => {}
        other => {
            let stderr = std::fs::read_to_string(&stderr_file).unwrap_or_default();
            panic!("hello 必须成功: {other:?}\nagent stderr:\n{stderr}");
        }
    }
    wait_for(
        || db.exists() && query_i64(&db, "SELECT COUNT(*) FROM foreground_observations") > 1,
        Duration::from_secs(20),
        "Observation 落库",
    );

    // 崩溃（强杀）：必须先确认进程身份已退出、pipe 不可连接（复审 P1-03 假通过），
    // 再做离线读取；SQLite 可读本身不能证明 Agent 已退出。
    guard
        .force_kill_and_wait(agent_key, Duration::from_secs(15))
        .expect("强杀并确认退出必须成功");

    wait_for(
        || {
            Connection::open(&db)
                .and_then(|conn| {
                    conn.query_row("SELECT COUNT(*) FROM foreground_observations", [], |r| {
                        r.get::<_, i64>(0)
                    })
                })
                .map(|count| count > 1)
                .unwrap_or(false)
        },
        Duration::from_secs(10),
        "崩溃后离线只读历史",
    );
    // quick_check 也必须可读（PRAGMA 在 ro 连接上可用）。
    {
        let conn = Connection::open(&db).unwrap();
        let quick_check: String = conn
            .query_row("PRAGMA quick_check", [], |r| r.get(0))
            .unwrap();
        assert_eq!(quick_check, "ok");
    }
}

/// 复审 P1-01：真实启动编排路径——启动前滚必须先持久化双槽候选再提交 DB；
/// 文件匹配 DB 但备份缺失时幂等路径必须修复冗余。
#[test]
fn startup_forward_roll_persists_backup_and_repairs_missing_redundancy() {
    let channel = test_channel();
    let mut guard = TestAgentGuard::new(&channel);
    let db = db_path(&channel);
    let config_dir = data_root(&channel).join("config");
    let settings_path = config_dir.join("settings.json");

    let write_settings = |revision: u64| {
        let settings = wuji_core::settings::Settings {
            revision: revision.to_string(),
            ..wuji_core::settings::Settings::default()
        };
        std::fs::create_dir_all(&config_dir).unwrap();
        std::fs::write(&settings_path, settings.canonical_json()).unwrap();
        settings
    };
    let delete_backups = |config_dir: &std::path::Path| {
        for name in [
            wuji_rebuild_agent::settings_backup::SLOT_A,
            wuji_rebuild_agent::settings_backup::SLOT_B,
        ] {
            let _ = std::fs::remove_file(config_dir.join(name));
        }
    };
    let backup_matches_db = |config_dir: &std::path::Path,
                             settings: &wuji_core::settings::Settings| {
        wuji_rebuild_agent::settings_backup::read_backup_matching(
            config_dir,
            Some(&(
                settings.revision.parse().unwrap(),
                settings.content_digest(),
            )),
        )
        .is_some()
    };

    // 第一次启动：文件 rev 1 前滚（DB=0）→ 启动前滚必须先写候选再提交。
    let s1 = write_settings(1);
    let (mut agent, agent_key) = guard.spawn_tracked_agent(false).unwrap();
    let mut client = connect_pipe(&channel, Duration::from_secs(15));
    assert_eq!(client.hello(&channel)["ok"], true);
    wait_for(
        || {
            db.exists()
                && query_i64(&db, "SELECT MAX(revision) FROM settings_revisions") == 1
                && backup_matches_db(&config_dir, &s1)
        },
        Duration::from_secs(15),
        "启动前滚后 DB 与双槽同时前进",
    );

    // 第二次启动：文件 rev 2 前滚（DB=1），备份全部删除 → 启动必须重新持久化候选。
    let s2 = write_settings(2);
    delete_backups(&config_dir);
    agent.kill().expect("kill");
    agent.wait().expect("wait");
    guard.untrack(agent_key);
    let (mut agent, agent_key) = guard.spawn_tracked_agent(false).unwrap();
    let mut client = connect_pipe(&channel, Duration::from_secs(15));
    assert_eq!(client.hello(&channel)["ok"], true);
    wait_for(
        || {
            query_i64(&db, "SELECT MAX(revision) FROM settings_revisions") == 2
                && backup_matches_db(&config_dir, &s2)
        },
        Duration::from_secs(15),
        "备份缺失时启动前滚仍必须产生可恢复候选",
    );

    // 第三次启动：文件与 DB 匹配（rev 2），备份再次删除 → 幂等路径必须修复冗余。
    delete_backups(&config_dir);
    agent.kill().expect("kill");
    agent.wait().expect("wait");
    guard.untrack(agent_key);
    let (mut agent, agent_key) = guard.spawn_tracked_agent(false).unwrap();
    let mut client = connect_pipe(&channel, Duration::from_secs(15));
    assert_eq!(client.hello(&channel)["ok"], true);
    wait_for(
        || backup_matches_db(&config_dir, &s2),
        Duration::from_secs(15),
        "文件匹配 DB 但备份缺失时必须修复冗余",
    );
    // 幂等修复不得额外前进 DB revision。
    assert_eq!(
        query_i64(&db, "SELECT MAX(revision) FROM settings_revisions"),
        2
    );

    let shutdown = client.call(&ulid(), "agent_shutdown_dev", serde_json::json!({}));
    assert_eq!(shutdown["ok"], true);
    wait_for(
        || agent.try_wait().map(|s| s.is_some()).unwrap_or(true),
        Duration::from_secs(15),
        "agent 进程退出",
    );
    let _ = agent.kill();
    let _ = agent.wait();
    guard.untrack(agent_key);
}
