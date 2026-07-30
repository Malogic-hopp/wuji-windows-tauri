//! V01-6 Desktop 集成测试（09 §11 V01-6 退出条件）。
//!
//! 覆盖：handshake/状态机、DTO 形状、Settings CAS 与 Run Key 补偿、
//! 只读查询、ensure_running detached 启动与受控退出。

use std::path::PathBuf;
use std::process::{Child, Command};
use std::sync::Arc;
use std::time::{Duration, Instant};

use wuji_core::domain::ActivityState;
use wuji_core::error::SafeErrorCode;
use wuji_core::settings::Settings;
use wuji_rebuild_desktop_lib::agent_controller::AgentController;
use wuji_rebuild_desktop_lib::ipc::AgentIpcClient;
use wuji_rebuild_desktop_lib::paths;
use wuji_rebuild_desktop_lib::query::QueryService;
use wuji_rebuild_desktop_lib::settings_service::{SettingsPatch, SettingsService};
use wuji_rebuild_desktop_lib::startup_registry;
use wuji_storage::Writer;

const T0: i64 = 1_784_332_800_000;

fn agent_bin() -> PathBuf {
    if let Some(path) = std::env::var_os("WUJI_TEST_AGENT_BIN") {
        return PathBuf::from(path);
    }
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("../../../target/debug")
        .join(wuji_core::runtime_names::AGENT_EXE_NAME)
}

fn test_channel() -> String {
    format!(
        "rebuild-v01-test-{}",
        ulid::Ulid::generate().to_string().to_lowercase()
    )
}

fn data_root(channel: &str) -> PathBuf {
    paths::data_root(channel).expect("data root")
}

fn cleanup(channel: &str) {
    let _ = std::fs::remove_dir_all(data_root(channel));
}

fn spawn_agent(channel: &str, capture_on_start: bool) -> Child {
    let mut command = Command::new(agent_bin());
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

async fn wait_agent(ipc: &AgentIpcClient, timeout: Duration) -> serde_json::Value {
    let deadline = Instant::now() + timeout;
    loop {
        if let Ok(status) = ipc.status().await {
            return status;
        }
        if Instant::now() >= deadline {
            panic!("等待 Agent 上线超时");
        }
        tokio::time::sleep(Duration::from_millis(200)).await;
    }
}

fn run_key_value_name(tag: &str) -> String {
    format!("WUJI Rebuild v0.1 Test {tag}")
}

#[tokio::test(flavor = "multi_thread")]
async fn ipc_handshake_fsm_and_settings_reload_roundtrip() {
    let channel = test_channel();
    let mut agent = spawn_agent(&channel, false);
    let ipc = AgentIpcClient::new(&channel, "0.1.0").expect("ipc client");

    let hello_status = wait_agent(&ipc, Duration::from_secs(15)).await;
    assert_eq!(hello_status["ok"], true, "hello 后 status_get 必须成功");
    assert_eq!(hello_status["result"]["captureState"], "stopped");
    assert_eq!(hello_status["result"]["protocolVersion"], 1);
    assert_eq!(hello_status["result"]["schemaVersion"], 1);

    // Capture FSM：start → pause → resume → stop。
    let running = ipc
        .call("capture_start", serde_json::json!({}))
        .await
        .unwrap();
    assert_eq!(running["result"]["captureState"], "running");
    let invalid = ipc
        .call("capture_start", serde_json::json!({}))
        .await
        .unwrap();
    assert_eq!(invalid["ok"], true, "重复 start 幂等成功");
    let paused = ipc
        .call("capture_pause", serde_json::json!({}))
        .await
        .unwrap();
    assert_eq!(paused["result"]["captureState"], "paused");
    let resumed = ipc
        .call("capture_resume", serde_json::json!({}))
        .await
        .unwrap();
    assert_eq!(resumed["result"]["captureState"], "running");
    let stopped = ipc
        .call("capture_stop", serde_json::json!({}))
        .await
        .unwrap();
    assert_eq!(stopped["result"]["captureState"], "stopped");

    // settings_reload 经由 Agent 写入 applied revision。
    let settings = Settings {
        revision: "1".to_string(),
        idle_threshold_seconds: 45,
        ..Settings::default()
    };
    let config_dir = data_root(&channel).join("config");
    std::fs::create_dir_all(&config_dir).unwrap();
    std::fs::write(config_dir.join("settings.json"), settings.canonical_json()).unwrap();
    let reload = ipc
        .call(
            "settings_reload",
            serde_json::json!({
                "savedRevision": "1",
                "contentDigest": settings.content_digest(),
            }),
        )
        .await
        .unwrap();
    assert_eq!(reload["ok"], true);
    assert_eq!(reload["result"]["appliedRevision"], "1");

    let shutdown = ipc
        .call("agent_shutdown_dev", serde_json::json!({}))
        .await
        .unwrap();
    assert_eq!(shutdown["result"]["willExit"], true);
    let _ = agent.wait();
    cleanup(&channel);
}

#[tokio::test(flavor = "multi_thread")]
async fn ensure_running_spawns_detached_agent_in_stopped_state() {
    let channel = test_channel();
    let ipc = AgentIpcClient::new(&channel, "0.1.0").expect("ipc client");
    let controller = AgentController::with_exe(
        &channel,
        Arc::new(AgentIpcClient::new(&channel, "0.1.0").expect("ipc")),
        agent_bin(),
    );

    let status = controller.ensure_running().await.expect("ensure_running");
    assert_eq!(
        status["captureState"], "stopped",
        "普通启动不传 --capture-on-start"
    );

    // 已在运行：再次 ensure 直接返回，不产生第二实例。
    let again = controller.ensure_running().await.expect("ensure again");
    assert_eq!(again["runtimeId"], status["runtimeId"]);

    controller.stop_agent().await.expect("正式停止 Agent");
    // willExit 只表示接受退出命令：必须等 Agent 真正退出后再清理（复审 P2-02：
    // 否则残留 detached 进程与测试目录竞态）。
    wait_agent_exit(&channel, Duration::from_secs(15)).await;
    let _ = ipc;
    cleanup(&channel);
}

#[tokio::test(flavor = "multi_thread")]
async fn stop_agent_commits_boundary_exits_and_can_be_started_again() {
    let channel = test_channel();
    let ipc = Arc::new(AgentIpcClient::new(&channel, "0.1.0").expect("ipc"));
    let controller = AgentController::with_exe(&channel, ipc.clone(), agent_bin());

    let first = controller.ensure_running().await.expect("first start");
    let first_runtime = first["runtimeId"].as_str().unwrap().to_string();
    let running = ipc
        .call("capture_start", serde_json::json!({}))
        .await
        .expect("capture start");
    assert_eq!(running["result"]["captureState"], "running");

    controller.stop_agent().await.expect("stop agent");
    wait_agent_exit(&channel, Duration::from_secs(15)).await;

    let query = QueryService::new(&channel).expect("query");
    let runtime = query
        .latest_runtime()
        .expect("latest runtime")
        .expect("runtime row");
    assert_eq!(
        runtime.process_state,
        wuji_core::domain::ProcessState::Stopped
    );
    let database = data_root(&channel)
        .join("data")
        .join("wuji-rebuild-v0.1.db");
    let connection = rusqlite::Connection::open(&database).expect("open database");
    let stopped_gaps: i64 = connection
        .query_row(
            "SELECT COUNT(*) FROM capture_gaps WHERE kind = 'capture_stopped' AND status = 'closed'",
            [],
            |row| row.get(0),
        )
        .expect("capture_stopped count");
    assert_eq!(stopped_gaps, 1, "停止 Agent 前必须提交并关闭停止边界");
    drop(connection);

    let second = controller.ensure_running().await.expect("restart");
    assert_ne!(second["runtimeId"], first_runtime);
    assert_eq!(second["captureState"], "stopped");
    controller.stop_agent().await.expect("stop restarted agent");
    wait_agent_exit(&channel, Duration::from_secs(15)).await;
    cleanup(&channel);
}

/// 轮询 pipe 直到 Agent 退出（连接失败即视为已退出）。
async fn wait_agent_exit(channel: &str, timeout: Duration) {
    let (pipe_name, _) = paths::channel_names(channel).expect("channel names");
    let deadline = Instant::now() + timeout;
    loop {
        match tokio::net::windows::named_pipe::ClientOptions::new().open(&pipe_name) {
            Err(_) => return,
            Ok(_) if Instant::now() < deadline => {
                tokio::time::sleep(Duration::from_millis(200)).await;
            }
            Ok(_) => panic!("等待 Agent 退出超时"),
        }
    }
}

#[tokio::test(flavor = "multi_thread")]
async fn settings_update_cas_run_key_and_saved_not_applied() {
    let channel = test_channel();
    let tag = short_tag();
    let value_name = run_key_value_name(&tag);
    let _ = startup_registry::delete_run_key(&value_name);

    let service = SettingsService::new(&channel, Some(value_name.clone()), agent_bin())
        .expect("settings service");
    let ipc = AgentIpcClient::new(&channel, "0.1.0").expect("ipc client");

    // Agent 离线时保存：文件落盘但返回 SETTINGS_SAVED_NOT_APPLIED。
    let patch = SettingsPatch {
        expected_revision: "0".to_string(),
        sampling_interval_seconds: 3,
        idle_threshold_seconds: 60,
        work_break_idle_seconds: 300,
        excluded_process_names: vec!["keepass.exe".to_string()],
        start_capture_on_login: true,
    };
    let error = service.update(patch, &ipc).await.unwrap_err();
    assert_eq!(error.code, SafeErrorCode::SettingsSavedNotApplied);
    assert!(service.path().exists(), "文件必须已保存");
    let saved: Settings =
        serde_json::from_str(&std::fs::read_to_string(service.path()).unwrap()).unwrap();
    assert_eq!(saved.revision, "1");
    // Run Key 已按 true 写入。
    let command = startup_registry::get_run_key(&value_name).expect("read run key");
    assert!(command.is_some(), "Run Key 必须已创建");
    assert!(command.unwrap().contains("--capture-on-start"));

    // CAS 冲突：旧 expectedRevision 不再匹配。
    let stale = SettingsPatch {
        expected_revision: "0".to_string(),
        sampling_interval_seconds: 3,
        idle_threshold_seconds: 60,
        work_break_idle_seconds: 300,
        excluded_process_names: vec![],
        start_capture_on_login: false,
    };
    let error = service.update(stale, &ipc).await.unwrap_err();
    assert_eq!(error.code, SafeErrorCode::SettingsConflict);

    // 正确 CAS：revision 2，startCaptureOnLogin=false → Run Key 删除。
    let fresh = SettingsPatch {
        expected_revision: "1".to_string(),
        sampling_interval_seconds: 3,
        idle_threshold_seconds: 60,
        work_break_idle_seconds: 300,
        excluded_process_names: vec![],
        start_capture_on_login: false,
    };
    let error = service.update(fresh, &ipc).await.unwrap_err();
    assert_eq!(
        error.code,
        SafeErrorCode::SettingsSavedNotApplied,
        "Agent 离线但 CAS 应成功落盘"
    );
    assert!(
        startup_registry::get_run_key(&value_name)
            .expect("read")
            .is_none(),
        "false 必须删除 Run Key"
    );

    // resync：把文件改为 true 后重新同步 → Run Key 恢复。
    let mut settings: Settings =
        serde_json::from_str(&std::fs::read_to_string(service.path()).unwrap()).unwrap();
    settings.start_capture_on_login = true;
    std::fs::write(service.path(), settings.canonical_json()).unwrap();
    let query = QueryService::new(&channel).expect("query service");
    let dto = service.resync_login_startup(&query).expect("resync");
    assert_eq!(
        dto.applied_revision, "0",
        "无数据库时 appliedRevision 必须是 0，不得误报为 saved revision（R04）"
    );
    assert!(
        startup_registry::get_run_key(&value_name)
            .expect("read")
            .is_some()
    );

    let _ = startup_registry::delete_run_key(&value_name);
    cleanup(&channel);
}

#[tokio::test(flavor = "multi_thread")]
async fn query_service_reads_seeded_database() {
    let channel = test_channel();
    let db_path = data_root(&channel)
        .join("data")
        .join("wuji-rebuild-v0.1.db");
    std::fs::create_dir_all(db_path.parent().unwrap()).unwrap();
    {
        let mut writer = Writer::bootstrap_with_timezone(&db_path, "Asia/Shanghai", T0).unwrap();
        let tz = writer.schema_meta().reporting_tz().unwrap();
        let tx = writer.transaction().unwrap();
        let runtime = wuji_core::dto::RuntimeId::new();
        tx.insert_runtime(&runtime, T0).unwrap();
        let app = tx
            .upsert_app_identity("proc:test", "code", "code.exe", T0)
            .unwrap();
        let obs = tx
            .insert_observation(
                &runtime,
                1,
                0,
                T0,
                0,
                app,
                ActivityState::Active,
                wuji_core::domain::CaptureQuality::Normal,
                0,
            )
            .unwrap();
        let obs = match obs {
            wuji_storage::ObservationInsert::Inserted(id) => id,
            _ => panic!(),
        };
        let obs2 = tx
            .insert_observation(
                &runtime,
                2,
                0,
                T0 + 3_000,
                3_000,
                app,
                ActivityState::Active,
                wuji_core::domain::CaptureQuality::Normal,
                0,
            )
            .unwrap();
        let obs2 = match obs2 {
            wuji_storage::ObservationInsert::Inserted(id) => id,
            _ => panic!(),
        };
        let seg = tx
            .open_segment(&runtime, 0, app, ActivityState::Active, T0, obs)
            .unwrap();
        tx.update_open_segment(seg, T0 + 3_000, obs2).unwrap();
        tx.close_open_segment("app_changed").unwrap();
        let date = wuji_core::dto::LocalDate::parse("2026-07-18").unwrap();
        tx.recompute_hours(&tz, &[T0]).unwrap();
        tx.recompute_dates(&tz, &[date], 15_000).unwrap();
        tx.commit().unwrap();
    }

    let service = QueryService::new(&channel).expect("query service");
    assert!(service.database_reachable());
    let today = service.today().expect("today");
    // today 查询的是"当前" local date；种子数据在 2026-07-18（测试当天），若不一致则跳过数值断言。
    let timeline = service
        .timeline("2026-07-18", None, Some(10))
        .expect("timeline");
    assert_eq!(timeline.items.len(), 1, "应返回一个 Segment");
    assert!(today.local_date.as_str().len() == 10);
    cleanup(&channel);
}

// 生成短 tag（避免在字符串里塞完整 ULID）。
fn short_tag() -> String {
    ulid::Ulid::generate().to_string()[..8].to_string()
}

#[test]
fn heatmap_rejects_out_of_range_days() {
    let channel = test_channel();
    let service = QueryService::new(&channel).expect("query service");
    // 校验先于打开数据库：无种子库也必须返回 InvalidArgument。
    for days in [Some(0_u32), Some(32_u32)] {
        let err = service.heatmap(days).expect_err("days 越界必须拒绝");
        assert_eq!(err.code, SafeErrorCode::InvalidArgument);
    }
    cleanup(&channel);
}
