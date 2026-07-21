//! V01-5 Agent 运行时端到端测试（09 §11 V01-5 退出条件）。
//!
//! 覆盖：真实进程启动、capture 状态机、request ID 幂等、envelope 拒绝、
//! settings_reload、受控退出、崩溃恢复、单实例。

use std::path::PathBuf;
use std::process::{Child, Command};
use std::time::{Duration, Instant};

use rusqlite::Connection;
use wuji_rebuild_agent::command_server::client::PipeClient;

const AGENT_BIN: &str = env!("CARGO_BIN_EXE_wuji-rebuild-agent-v01");

fn test_channel() -> String {
    format!(
        "rebuild-v01-test-{}",
        ulid::Ulid::generate().to_string().to_lowercase()
    )
}

fn data_root(channel: &str) -> PathBuf {
    PathBuf::from(std::env::var_os("LOCALAPPDATA").expect("LOCALAPPDATA"))
        .join("WUJI-Rebuild-V01")
        .join(channel)
}

fn db_path(channel: &str) -> PathBuf {
    data_root(channel).join("data").join("wuji-rebuild-v0.1.db")
}

fn pipe_name(channel: &str) -> String {
    let sid = wuji_windows::current_user_sid().expect("sid");
    let scope = wuji_core::runtime_names::user_scope(&sid);
    format!("\\\\.\\pipe\\WUJI.Rebuild.V01.Test.{channel}.{scope}")
}

fn spawn_agent(channel: &str, capture_on_start: bool) -> Child {
    let mut command = Command::new(AGENT_BIN);
    command.arg("--channel").arg(channel);
    if capture_on_start {
        command.arg("--capture-on-start");
    }
    command
        .stdin(std::process::Stdio::null())
        .stdout(std::process::Stdio::null())
        .stderr(std::process::Stdio::null())
        .spawn()
        .expect("agent 进程必须能启动")
}

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

fn ulid() -> String {
    ulid::Ulid::generate().to_string()
}

fn query_i64(db: &PathBuf, sql: &str) -> i64 {
    let conn = Connection::open(db).unwrap();
    conn.query_row(sql, [], |r| r.get(0)).unwrap()
}

fn cleanup(channel: &str) {
    let _ = std::fs::remove_dir_all(data_root(channel));
}

#[test]
fn agent_end_to_end_capture_fsm_ipc_and_shutdown() {
    let channel = test_channel();
    let mut agent = spawn_agent(&channel, true);
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
    cleanup(&channel);
}

#[test]
fn agent_rejects_oversize_and_wrong_channel_hello() {
    let channel = test_channel();
    let mut agent = spawn_agent(&channel, false);
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
    cleanup(&channel);
}

#[test]
fn agent_crash_restart_recovers_open_rows() {
    let channel = test_channel();
    let db = db_path(&channel);

    {
        let mut agent = spawn_agent(&channel, true);
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
    }

    {
        let mut agent = spawn_agent(&channel, false);
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
    }
    cleanup(&channel);
}

#[test]
fn second_instance_is_rejected() {
    let channel = test_channel();
    let mut first = spawn_agent(&channel, false);
    let _client = connect_pipe(&channel, Duration::from_secs(15));

    let mut second = spawn_agent(&channel, false);
    wait_for(
        || second.try_wait().map(|s| s.is_some()).unwrap_or(true),
        Duration::from_secs(10),
        "第二个实例必须立即退出",
    );
    let status = second.wait().unwrap();
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
    cleanup(&channel);
}
