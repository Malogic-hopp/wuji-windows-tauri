//! 阶段 4.3 生产接线测试（复审 P2-01）：与 `main.rs` 调用同一个生产装配函数
//! `control_plane::assemble`，证明 Lifecycle / settings_reload / reconciler /
//! 系统事件全部经过唯一 CaptureCoordinator，不存在旁路控制路径。
//!
//! 拓扑与 main.rs 完全一致（同一装配函数 + 真实 Capture→Processor→Writer）：
//! `CommandServer ─┐
//!  reconciler ────┼→ assemble() 返回的唯一 Arc<CaptureCoordinator>
//!  session/power ─┘        │→ BarrierRequest → Capture Loop（单 FIFO）
//!                          └→ WriterControl（唯一构造点）→ WriterTask → SQLite`
//! 旁路的结构性排除：装配函数不暴露 BarrierRequest sender 与完整
//! `mpsc::Sender<WriterControl>`（仅提供窄通道 MaintenanceControl）；
//! CommandServerContext 与 reconciler 在类型层面不持有任何控制通道。

use std::sync::{Arc, Mutex};
use std::time::Duration;

use rusqlite::Connection;
use tempfile::TempDir;
use tokio::sync::{mpsc, watch};
use wuji_core::domain::CaptureState;
use wuji_core::dto::RuntimeId;
use wuji_core::pipeline::IdleReading;
use wuji_core::settings::Settings;
use wuji_rebuild_agent::activity::ActivityEngine;
use wuji_rebuild_agent::capture_coordinator::CaptureCoordinator;
use wuji_rebuild_agent::capture_loop::{
    CaptureLoopConfig, CaptureSource, ContinuityState, RawSample, spawn_capture_loop,
};
use wuji_rebuild_agent::command_server::{
    CommandServerContext, RequestIdCache, handle_request_line,
};
use wuji_rebuild_agent::control_plane::{ControlPlane, MaintenanceControl};
use wuji_rebuild_agent::processor_task::spawn_observation_processor;
use wuji_rebuild_agent::settings_reconciler::{
    run_settings_reconciler_observed, run_settings_reconciler_with_interval,
};
use wuji_rebuild_agent::shared::SharedState;
use wuji_rebuild_agent::writer_task::WriterTask;
use wuji_storage::Writer;

const SHANGHAI: &str = "Asia/Shanghai";

struct ScriptedSource;

impl CaptureSource for ScriptedSource {
    fn capture(&self) -> RawSample {
        RawSample {
            process_file_name: Some("code.exe".to_string()),
            idle: IdleReading::Seconds(0),
        }
    }
}

fn ulid() -> String {
    ulid::Ulid::generate().to_string()
}

fn envelope(request_id: &str, command: &str, payload: serde_json::Value) -> String {
    serde_json::json!({
        "protocolVersion": 1,
        "requestId": request_id,
        "command": command,
        "sentAtUtcMs": "1784332800000",
        "payload": payload,
    })
    .to_string()
}

/// 与 main.rs 相同的生产接线（同一装配函数 + 真实 Capture→Processor→Writer）。
struct Wiring {
    context: Arc<CommandServerContext>,
    request_ids: Arc<Mutex<RequestIdCache>>,
    coordinator: Arc<CaptureCoordinator>,
    shared: Arc<SharedState>,
    maintenance: MaintenanceControl,
    capture_watch_rx: watch::Receiver<CaptureState>,
    settings_watch_rx: watch::Receiver<Settings>,
    settings_path: std::path::PathBuf,
    dir: TempDir,
    capture_handle: tokio::task::JoinHandle<()>,
    processor_handle: tokio::task::JoinHandle<()>,
    writer_handle: tokio::task::JoinHandle<(Writer, ActivityEngine)>,
    reconciler_handle: tokio::task::JoinHandle<()>,
    pipeline_supervisor_handle: tokio::task::JoinHandle<()>,
}

fn production_wiring() -> Wiring {
    let dir = TempDir::new().unwrap();
    let db_path = dir.path().join("wuji-rebuild-v0.1.db");
    let now = wuji_rebuild_agent::capture_loop::now_utc_ms();
    Writer::bootstrap_with_timezone(&db_path, SHANGHAI, now).unwrap();
    let continuity = Arc::new(ContinuityState::default());
    let runtime_id = RuntimeId::new();
    let settings = Settings {
        sampling_interval_seconds: 1,
        ..Settings::default()
    };
    // 与 main.rs 相同：启动恢复负责 runtime 行登记（不手工 insert_runtime）。
    let mut writer = Writer::open_existing(&db_path).unwrap();
    let mut engine =
        ActivityEngine::new(runtime_id.clone(), settings.clone(), continuity.clone()).unwrap();
    engine.recover_startup(&mut writer, now).unwrap();

    let shared = Arc::new(SharedState::new("0.1.0".to_string(), runtime_id));
    // 复审 P2-01：与 main.rs 调用同一个生产装配函数——唯一 Coordinator、
    // Barrier sender 与完整 WriterControl sender 都不进入测试作用域。
    let plane: ControlPlane = wuji_rebuild_agent::control_plane::assemble(
        shared.clone(),
        settings,
        CaptureState::Stopped,
    );
    let (shutdown_tx, _shutdown_rx) = watch::channel(false);

    // Capture Loop 是 CapturePipelineItem FIFO 的唯一生产者。
    let (pipeline_rx, capture_handle) = spawn_capture_loop(
        ScriptedSource,
        plane.settings_rx.clone(),
        plane.capture_state_rx,
        continuity.clone(),
        CaptureLoopConfig {
            wake_interval: Duration::from_millis(50),
            queue_capacity: 64,
            offload_capture: false,
            ..CaptureLoopConfig::default()
        },
        plane.barrier_request_rx,
        &plane.health,
    );
    let (processor_rx, processor_handle) = spawn_observation_processor(
        pipeline_rx,
        plane.settings_rx.clone(),
        continuity.clone(),
        &plane.health,
    );

    let writer_task = WriterTask::new(
        writer,
        engine,
        shared.clone(),
        plane.writer_capture_stop_tx.clone(),
        continuity.clone(),
        dir.path().join("config"),
        plane.health.clone(),
    );
    let writer_handle = tokio::spawn(writer_task.into_run_future(processor_rx, plane.control_rx));

    // 测试观察口：从装配暴露的 watch 发送端订阅（不影响生产路径）。
    let capture_watch_rx = plane.writer_capture_stop_tx.subscribe();
    let settings_watch_rx = plane.settings_rx;
    let coordinator = plane.coordinator.clone();
    let pipeline_supervisor_handle =
        tokio::spawn(wuji_rebuild_agent::control_plane::supervise_pipeline_exits(
            plane.pipeline_exit_rx,
            coordinator.clone(),
        ));

    let settings_path = dir.path().join("settings.json");
    let context = Arc::new(CommandServerContext {
        shared: shared.clone(),
        coordinator: coordinator.clone(),
        settings_path: settings_path.clone(),
        settings_digest_for: |settings: &Settings| settings.content_digest(),
        shutdown_tx,
        channel: "rebuild-v01-test-wiring".to_string(),
    });
    // reconciler 与 IPC 共用同一个 Coordinator。
    let reconciler_handle = tokio::spawn(run_settings_reconciler_with_interval(
        settings_path.clone(),
        shared.clone(),
        coordinator.clone(),
        Duration::from_millis(20),
    ));

    Wiring {
        context,
        request_ids: Arc::new(Mutex::new(RequestIdCache::new("0.1.0".to_string()))),
        coordinator,
        shared,
        maintenance: plane.maintenance,
        capture_watch_rx,
        settings_watch_rx,
        settings_path,
        dir,
        capture_handle,
        processor_handle,
        writer_handle,
        reconciler_handle,
        pipeline_supervisor_handle,
    }
}

impl Wiring {
    async fn call(&self, command: &str, payload: serde_json::Value) -> serde_json::Value {
        let line = envelope(&ulid(), command, payload);
        let response = handle_request_line(&line, &self.context, &self.request_ids).await;
        serde_json::from_str(&response).expect("响应必须是 JSON")
    }

    fn db(&self) -> Connection {
        Connection::open(self.dir.path().join("wuji-rebuild-v0.1.db")).unwrap()
    }

    async fn wait_until(&self, what: &str, mut condition: impl FnMut() -> bool) {
        let deadline = std::time::Instant::now() + Duration::from_secs(5);
        while !condition() {
            assert!(std::time::Instant::now() < deadline, "等待超时: {what}");
            tokio::time::sleep(Duration::from_millis(25)).await;
        }
    }

    async fn shutdown(self) {
        self.pipeline_supervisor_handle.abort();
        let _ = self.maintenance.shutdown().await;
        let _ = self.writer_handle.await;
        self.capture_handle.abort();
        self.processor_handle.abort();
        self.reconciler_handle.abort();
    }
}

/// 四条控制路径（IPC Lifecycle、IPC settings_reload、reconciler、系统事件）
/// 在真实生产拓扑中全部经唯一 Coordinator 完成，shared/watch/DTO/SQLite 一致。
#[tokio::test]
async fn all_control_paths_converge_on_single_coordinator() {
    let wiring = production_wiring();

    // 1) IPC capture_start：Coordinator 打开 gate，样本经真实 FIFO 流入 SQLite。
    let started = wiring.call("capture_start", serde_json::json!({})).await;
    assert_eq!(started["ok"], true, "capture_start: {started}");
    assert_eq!(started["result"]["captureState"], "running");
    assert_eq!(*wiring.capture_watch_rx.borrow(), CaptureState::Running);
    wiring
        .wait_until("首条 Observation 落库", || {
            wiring
                .db()
                .query_row("SELECT COUNT(*) FROM foreground_observations", [], |r| {
                    r.get::<_, i64>(0)
                })
                .unwrap()
                >= 1
        })
        .await;

    // 2) IPC settings_reload：经 Coordinator 应用 revision 1。
    let rev1 = Settings {
        revision: "1".to_string(),
        idle_threshold_seconds: 90,
        sampling_interval_seconds: 1,
        ..Settings::default()
    };
    std::fs::write(&wiring.settings_path, rev1.canonical_json()).unwrap();
    let reloaded = wiring
        .call(
            "settings_reload",
            serde_json::json!({
                "savedRevision": "1",
                "contentDigest": rev1.content_digest(),
            }),
        )
        .await;
    assert_eq!(reloaded["ok"], true, "settings_reload: {reloaded}");
    assert_eq!(reloaded["result"]["appliedRevision"], "1");
    assert_eq!(wiring.shared.applied_settings_revision(), 1);
    assert_eq!(wiring.settings_watch_rx.borrow().revision, "1");

    // 3) reconciler：同一 Coordinator 自动应用 revision 2（无 IPC 参与）。
    let rev2 = Settings {
        revision: "2".to_string(),
        idle_threshold_seconds: 120,
        sampling_interval_seconds: 1,
        ..Settings::default()
    };
    std::fs::write(&wiring.settings_path, rev2.canonical_json()).unwrap();
    wiring
        .wait_until("reconciler 自动应用 revision 2", || {
            wiring.shared.applied_settings_revision() == 2
        })
        .await;
    wiring
        .wait_until("settings watch 前进到 2", || {
            wiring.settings_watch_rx.borrow().revision == "2"
        })
        .await;

    // 4) 系统事件（session/power pump 同款调用）：经 Coordinator 四态入口提交。
    wiring
        .coordinator
        .apply_system_lifecycle_event(
            wuji_rebuild_agent::capture_coordinator::SystemLifecycleEvent::Sleep {
                at_utc_ms: wuji_rebuild_agent::capture_loop::now_utc_ms(),
            },
        )
        .await
        .expect("系统事件必须经 Coordinator 四态入口提交");

    // 5) IPC capture_pause：Coordinator 冻结并提交 capture_paused 边界。
    let paused = wiring.call("capture_pause", serde_json::json!({})).await;
    assert_eq!(paused["ok"], true, "capture_pause: {paused}");
    assert_eq!(paused["result"]["captureState"], "paused");

    // 终态一致性：watch == SharedState == DTO == Coordinator effective。
    assert_eq!(*wiring.capture_watch_rx.borrow(), CaptureState::Paused);
    assert_eq!(wiring.shared.capture_state(), CaptureState::Paused);
    assert_eq!(wiring.coordinator.effective_state(), CaptureState::Paused);
    let status = wiring.call("status_get", serde_json::json!({})).await;
    assert_eq!(status["result"]["captureState"], "paused");

    // SQLite 证据：两条边界各就各位，Observation 已落库。
    let gap_kinds: Vec<String> = {
        let conn = wiring.db();
        let mut stmt = conn
            .prepare("SELECT kind FROM capture_gaps ORDER BY gap_id")
            .unwrap();
        stmt.query_map([], |r| r.get(0))
            .unwrap()
            .collect::<rusqlite::Result<Vec<_>>>()
            .unwrap()
    };
    assert!(
        gap_kinds.contains(&"system_sleep".to_string()),
        "系统事件边界必须落库: {gap_kinds:?}"
    );
    assert!(
        gap_kinds.contains(&"capture_paused".to_string()),
        "pause 边界必须落库: {gap_kinds:?}"
    );
    let max_revision: i64 = wiring
        .db()
        .query_row("SELECT MAX(revision) FROM settings_revisions", [], |r| {
            r.get(0)
        })
        .unwrap();
    assert_eq!(
        max_revision, 2,
        "IPC 与 reconciler 的 settings 都必须持久化"
    );

    wiring.shutdown().await;
}

/// 同一 request ID 重放原任务结果（复审 P2-02 方案 A：本测试不制造 timeout，
/// 只验证 request cache 幂等重放与副作用恰好一次；timeout 不取消副作用的
/// 确定性证据见 ipc_protocol::timeout_does_not_cancel_side_effect_and_retry_returns_real_result）。
#[tokio::test]
async fn same_request_id_replays_original_result_in_wiring() {
    let wiring = production_wiring();
    let rev1 = Settings {
        revision: "1".to_string(),
        sampling_interval_seconds: 1,
        ..Settings::default()
    };
    std::fs::write(&wiring.settings_path, rev1.canonical_json()).unwrap();
    let request_id = ulid();
    let line = envelope(
        &request_id,
        "settings_reload",
        serde_json::json!({
            "savedRevision": "1",
            "contentDigest": rev1.content_digest(),
        }),
    );

    let first = handle_request_line(&line, &wiring.context, &wiring.request_ids).await;
    let first: serde_json::Value = serde_json::from_str(&first).unwrap();
    assert_eq!(first["ok"], true, "首次应用必须成功: {first}");
    assert_eq!(first["result"]["appliedRevision"], "1");

    // 同一 request ID 重试：返回 request cache 中的原始结果，绝不产生第二次应用。
    let second = handle_request_line(&line, &wiring.context, &wiring.request_ids).await;
    let second: serde_json::Value = serde_json::from_str(&second).unwrap();
    assert_eq!(second, first, "同 request ID 必须重放原任务结果");

    // 副作用恰好一次：applied revision 只前进一次，revision 1 恰有一行。
    assert_eq!(wiring.shared.applied_settings_revision(), 1);
    let rev1_rows: i64 = wiring
        .db()
        .query_row(
            "SELECT COUNT(*) FROM settings_revisions WHERE revision = 1",
            [],
            |r| r.get(0),
        )
        .unwrap();
    assert_eq!(rev1_rows, 1, "重放不得重复提交 settings revision");

    wiring.shutdown().await;
}

/// 阶段 4.3.1 §三A：IPC settings_reload 与 reconciler 并发——同一 Coordinator
/// 串行化，revision 1 恰好提交一次（两种合法先后顺序均满足同一不变量）。
#[tokio::test]
async fn ipc_reload_and_reconciler_never_duplicate_apply() {
    let wiring = production_wiring();
    // 停止工厂中的周期实例，消除其首次 tick 与测试建 rendezvous 的调度竞态；
    // 下方启动的仍是同一 reconciler 实现，只增加 attempt 观察口。
    wiring.reconciler_handle.abort();
    let rev1 = Settings {
        revision: "1".to_string(),
        sampling_interval_seconds: 1,
        ..Settings::default()
    };
    // 测试专用 rendezvous 钉住生产唯一 transition lock；两条真实调用路径会
    // 同时排队，避免 SQLite 同步 busy 阻塞 current-thread runtime 的调度假象。
    let transition_gate = wiring.coordinator.acquire_transition_lock_for_test().await;
    std::fs::write(&wiring.settings_path, rev1.canonical_json()).unwrap();

    let line = envelope(
        &ulid(),
        "settings_reload",
        serde_json::json!({
            "savedRevision": "1",
            "contentDigest": rev1.content_digest(),
        }),
    );
    let ipc = tokio::spawn({
        let context = wiring.context.clone();
        let request_ids = wiring.request_ids.clone();
        async move { handle_request_line(&line, &context, &request_ids).await }
    });

    // 两端 rendezvous：IPC 已被 request cache 接受且 dispatch 未结束；reconciler
    // 也已读到 revision 1 并在调用同一 Coordinator 前发出 attempt。
    wiring
        .wait_until("IPC dispatch 进入 Active", || {
            wiring.request_ids.lock().unwrap().active_count() == 1
        })
        .await;
    let (attempt_tx, mut attempt_rx) = mpsc::unbounded_channel();
    let overlap_reconciler = tokio::spawn(run_settings_reconciler_observed(
        wiring.settings_path.clone(),
        wiring.shared.clone(),
        wiring.coordinator.clone(),
        Duration::from_millis(20),
        Some(attempt_tx),
    ));
    let attempted = tokio::time::timeout(Duration::from_secs(5), attempt_rx.recv())
        .await
        .expect("reconciler attempt 必须到达")
        .expect("attempt channel 不得关闭");
    assert_eq!(attempted, 1);
    assert_eq!(
        wiring.request_ids.lock().unwrap().active_count(),
        1,
        "收到 reconciler attempt 时 IPC 必须仍在执行，证明真实重叠"
    );

    drop(transition_gate);
    let reloaded: serde_json::Value =
        serde_json::from_str(&ipc.await.expect("IPC task 不 panic")).unwrap();
    assert_eq!(reloaded["ok"], true, "settings_reload: {reloaded}");
    // 任一锁顺序都由同一 Coordinator 完成（reconciler 可能先或后）。
    wiring
        .wait_until("applied 前进到 1", || {
            wiring.shared.applied_settings_revision() == 1
        })
        .await;

    // 再发一次同 revision IPC（幂等重放路径）：同样不得重复提交。
    let again = wiring
        .call(
            "settings_reload",
            serde_json::json!({
                "savedRevision": "1",
                "contentDigest": rev1.content_digest(),
            }),
        )
        .await;
    assert_eq!(again["ok"], true, "幂等重放: {again}");

    let rev1_rows: i64 = wiring
        .db()
        .query_row(
            "SELECT COUNT(*) FROM settings_revisions WHERE revision = 1",
            [],
            |r| r.get(0),
        )
        .unwrap();
    assert_eq!(rev1_rows, 1, "并发与重放都不得重复提交 revision 1");
    assert_eq!(wiring.shared.applied_settings_revision(), 1);
    overlap_reconciler.abort();

    wiring.shutdown().await;
}
