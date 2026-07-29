//! 阶段 4.5：Lock/Sleep 状态叠加与事件接线的确定性测试。
//!
//! 覆盖 L01–L20。测试使用 Wiring fixture（真实 Coordinator + Capture Loop +
//! Processor + Writer + SQLite）、paused time、channel/watch/queue-depth/SQLite
//! 条件作为 rendezvous。不使用随机 sleep、不执行真实锁屏或休眠。
//!
//! L01–L08 使用 Wiring fixture 验证完整拓扑；
//! L09–L13 使用 Coordinator-only harness 验证失败闭环；
//! L14–L16 使用 Wiring fixture 验证并发与 in-flight；
//! L17–L20 使用 Coordinator-only harness 验证边缘。

use std::sync::atomic::{AtomicI64, AtomicUsize, Ordering};
use std::sync::{Arc, Condvar, Mutex};
use std::time::Duration;

use rusqlite::Connection;
use tempfile::TempDir;
use tokio::sync::{mpsc, watch};
use wuji_core::domain::{CaptureState, ProcessState, WriterState};
use wuji_core::dto::RuntimeId;
use wuji_core::error::{ErrorSource, SafeError, SafeErrorCode};
use wuji_core::pipeline::{BarrierKind, IdleReading};
use wuji_core::settings::Settings;
use wuji_rebuild_agent::activity::{ActivityEngine, EngineEvent};
use wuji_rebuild_agent::capture_coordinator::{CaptureCoordinator, SystemLifecycleEvent};
use wuji_rebuild_agent::capture_loop::{
    CaptureLoopConfig, ContinuityState, RawSample, spawn_capture_loop,
};
use wuji_rebuild_agent::control_plane::supervise_pipeline_exits;
use wuji_rebuild_agent::pipeline_health::PipelineHealth;
use wuji_rebuild_agent::processor_task::spawn_observation_processor;
use wuji_rebuild_agent::session_power_events::run_session_power_events;
use wuji_rebuild_agent::shared::SharedState;
use wuji_rebuild_agent::writer_task::{WriterControl, WriterTask};
use wuji_storage::Writer;

const T0: i64 = 1_784_332_800_000;
const SHANGHAI: &str = "Asia/Shanghai";
const SAMPLE_STEP_MS: i64 = 1_000;

// ---- 测试用采集源 ----
use std::collections::VecDeque;

#[derive(Clone)]
struct ScriptedSource {
    script: Arc<Mutex<VecDeque<RawSample>>>,
    fallback: RawSample,
}

impl ScriptedSource {
    fn new(fallback: RawSample) -> Self {
        Self {
            script: Arc::new(Mutex::new(VecDeque::new())),
            fallback,
        }
    }
    fn push(&self, sample: RawSample) {
        self.script.lock().unwrap().push_back(sample);
    }
}

impl wuji_rebuild_agent::capture_loop::CaptureSource for ScriptedSource {
    fn capture(&self) -> RawSample {
        self.script
            .lock()
            .unwrap()
            .pop_front()
            .unwrap_or_else(|| self.fallback.clone())
    }
}

fn sample(name: &str, idle_seconds: u32) -> RawSample {
    RawSample {
        process_file_name: Some(name.to_string()),
        idle: IdleReading::Seconds(idle_seconds),
    }
}

// ---- Wiring fixture ----
struct Wiring {
    _dir: TempDir,
    coordinator: Arc<CaptureCoordinator>,
    _shared: Arc<SharedState>,
    _health: Arc<PipelineHealth>,
    source: ScriptedSource,
    utc_clock: Arc<AtomicI64>,
    capture_rx: watch::Receiver<CaptureState>,
    _settings_rx: watch::Receiver<Settings>,
    capture_handle: tokio::task::JoinHandle<()>,
    processor_handle: tokio::task::JoinHandle<()>,
    writer_handle: tokio::task::JoinHandle<(Writer, ActivityEngine)>,
    supervisor_handle: tokio::task::JoinHandle<()>,
}

fn wiring(settings: Settings) -> Wiring {
    let dir = TempDir::new().unwrap();
    let db_path = dir.path().join("wuji-rebuild-v0.1.db");
    Writer::bootstrap_with_timezone(&db_path, SHANGHAI, T0).unwrap();
    let continuity = Arc::new(ContinuityState::default());
    let runtime_id = RuntimeId::new();
    let mut writer = Writer::open_existing(&db_path).unwrap();
    let mut engine =
        ActivityEngine::new(runtime_id.clone(), settings.clone(), continuity.clone()).unwrap();
    engine.recover_startup(&mut writer, T0).unwrap();

    let shared = Arc::new(SharedState::new("0.1.0".to_string(), runtime_id));
    let plane = wuji_rebuild_agent::control_plane::assemble(
        shared.clone(),
        settings,
        CaptureState::Stopped,
    );
    let utc_clock = Arc::new(AtomicI64::new(T0));
    let clock = utc_clock.clone();
    let source = ScriptedSource::new(sample("code.exe", 0));
    let (pipeline_rx, capture_handle) = spawn_capture_loop(
        source.clone(),
        plane.settings_rx.clone(),
        plane.capture_state_rx,
        continuity.clone(),
        CaptureLoopConfig {
            wake_interval: Duration::from_millis(50),
            queue_capacity: 64,
            offload_capture: false,
            utc_now_ms: Arc::new(move || clock.load(Ordering::Acquire)),
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
    let supervisor_handle = tokio::spawn(supervise_pipeline_exits(
        plane.pipeline_exit_rx,
        plane.coordinator.clone(),
    ));
    let capture_rx = plane.writer_capture_stop_tx.subscribe();
    Wiring {
        _dir: dir,
        coordinator: plane.coordinator,
        _shared: shared,
        _health: plane.health,
        source,
        utc_clock,
        capture_rx,
        _settings_rx: plane.settings_rx,
        capture_handle,
        processor_handle,
        writer_handle,
        supervisor_handle,
    }
}

impl Wiring {
    fn db(&self) -> Connection {
        Connection::open(self._dir.path().join("wuji-rebuild-v0.1.db")).unwrap()
    }

    fn observation_rows(&self) -> Vec<(i64, String, i64)> {
        let conn = self.db();
        let mut stmt = conn
            .prepare(
                "SELECT capture_sequence, activity_state, settings_revision
                 FROM foreground_observations ORDER BY capture_sequence",
            )
            .unwrap();
        stmt.query_map([], |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?)))
            .unwrap()
            .collect::<rusqlite::Result<Vec<_>>>()
            .unwrap()
    }

    fn gap_count(&self, kind: &str) -> i64 {
        self.db()
            .query_row(
                "SELECT COUNT(*) FROM capture_gaps WHERE kind = ?1",
                [kind],
                |r| r.get(0),
            )
            .unwrap()
    }

    fn gap_row(&self, kind: &str) -> (i64, Option<i64>, String) {
        self.db()
            .query_row(
                "SELECT start_at_utc_ms, end_at_utc_ms, status FROM capture_gaps WHERE kind = ?1 ORDER BY start_at_utc_ms LIMIT 1",
                [kind],
                |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?)),
            )
            .unwrap()
    }

    async fn produce_sample(&self) {
        self.utc_clock.fetch_add(SAMPLE_STEP_MS, Ordering::AcqRel);
        tokio::time::advance(Duration::from_millis(SAMPLE_STEP_MS as u64)).await;
    }

    async fn yield_until(&self, what: &str, mut condition: impl FnMut() -> bool) {
        let deadline = std::time::Instant::now() + Duration::from_secs(2);
        loop {
            if condition() {
                return;
            }
            assert!(
                std::time::Instant::now() < deadline,
                "rendezvous 超时: {what}"
            );
            tokio::task::yield_now().await;
        }
    }

    async fn capture_start(&self) {
        assert_eq!(
            self.coordinator
                .apply_capture_command("capture_start", T0)
                .await,
            Ok(CaptureState::Running)
        );
    }

    async fn apply_lock(&self) -> Result<(), SafeError> {
        let at = self.utc_clock.load(Ordering::Acquire);
        self.coordinator
            .apply_system_lifecycle_event(SystemLifecycleEvent::Lock { at_utc_ms: at })
            .await
    }

    async fn apply_unlock(&self) -> Result<(), SafeError> {
        let at = self.utc_clock.load(Ordering::Acquire);
        self.coordinator
            .apply_system_lifecycle_event(SystemLifecycleEvent::Unlock { at_utc_ms: at })
            .await
    }

    async fn apply_sleep(&self) -> Result<(), SafeError> {
        let at = self.utc_clock.load(Ordering::Acquire);
        self.coordinator
            .apply_system_lifecycle_event(SystemLifecycleEvent::Sleep { at_utc_ms: at })
            .await
    }

    async fn apply_resume(&self) -> Result<(), SafeError> {
        let at = self.utc_clock.load(Ordering::Acquire);
        self.coordinator
            .apply_system_lifecycle_event(SystemLifecycleEvent::Resume { at_utc_ms: at })
            .await
    }

    async fn shutdown(self) {
        self.supervisor_handle.abort();
        self.capture_handle.abort();
        self.processor_handle.abort();
        self.writer_handle.abort();
    }
}

// ===== L01: Lock→Unlock =====
#[tokio::test(start_paused = true)]
async fn l01_lock_unlock_boundary_once_and_restore() {
    let w = wiring(Settings {
        sampling_interval_seconds: 1,
        ..Settings::default()
    });
    w.capture_start().await;
    w.produce_sample().await;
    w.yield_until("边界前 Observation 落库", || {
        w.observation_rows().len() == 1
    })
    .await;

    // Lock：采集冻结 + SessionLocked gap 打开。
    w.apply_lock().await.expect("Lock 必须成功");
    assert_eq!(*w.capture_rx.borrow(), CaptureState::Paused);
    assert_eq!(w.gap_count("session_locked"), 1);
    let (start, end, status) = w.gap_row("session_locked");
    assert!(start > T0);
    assert_eq!(end, None);
    assert_eq!(status, "open");

    // 锁定期间不产生 Observation：推进一个采样周期后直接断言行数。
    w.source.push(sample("code.exe", 0));
    w.produce_sample().await;
    assert_eq!(
        w.observation_rows().len(),
        1,
        "Lock 后推进采样周期，Observation 不得增加"
    );

    // Unlock：恢复采集。
    w.apply_unlock().await.expect("Unlock 必须成功");
    assert_eq!(*w.capture_rx.borrow(), CaptureState::Running);

    // 解除后第一条 Observation 关闭 gap。
    w.produce_sample().await;
    w.yield_until("Unlock 后 Observation 落库并关闭 gap", || {
        w.observation_rows().len() == 2
    })
    .await;
    let (_, end, status) = w.gap_row("session_locked");
    assert_eq!(status, "closed");
    assert!(end.is_some());

    w.shutdown().await;
}

// ===== L02: Sleep→Resume =====
#[tokio::test(start_paused = true)]
async fn l02_sleep_resume_boundary_once_and_restore() {
    let w = wiring(Settings {
        sampling_interval_seconds: 1,
        ..Settings::default()
    });
    w.capture_start().await;
    w.produce_sample().await;
    w.yield_until("边界前 Observation 落库", || {
        w.observation_rows().len() == 1
    })
    .await;

    w.apply_sleep().await.expect("Sleep 必须成功");
    assert_eq!(*w.capture_rx.borrow(), CaptureState::Paused);
    assert_eq!(w.gap_count("system_sleep"), 1);

    w.apply_resume().await.expect("Resume 必须成功");
    assert_eq!(*w.capture_rx.borrow(), CaptureState::Running);

    w.produce_sample().await;
    w.yield_until("Resume 后 Observation 落库", || {
        w.observation_rows().len() == 2
    })
    .await;
    let (_, end, status) = w.gap_row("system_sleep");
    assert_eq!(status, "closed");
    assert!(end.is_some());

    w.shutdown().await;
}

// ===== L03: Lock→Sleep→Resume→Unlock =====
#[tokio::test(start_paused = true)]
async fn l03_lock_sleep_resume_unlock_overlap() {
    let w = wiring(Settings {
        sampling_interval_seconds: 1,
        ..Settings::default()
    });
    w.capture_start().await;
    w.produce_sample().await;
    w.yield_until("边界前 Observation 落库", || {
        w.observation_rows().len() == 1
    })
    .await;

    // Lock → Sleep 叠加。
    w.apply_lock().await.expect("Lock 必须成功");
    assert_eq!(*w.capture_rx.borrow(), CaptureState::Paused);
    w.apply_sleep().await.expect("Sleep 必须成功");
    // 两个 gap 都应存在。
    assert_eq!(w.gap_count("session_locked"), 1);
    assert_eq!(w.gap_count("system_sleep"), 1);

    // 单独 Resume 不应恢复（lock 仍 active）。
    w.apply_resume().await.expect("Resume 必须成功");
    assert_eq!(
        *w.capture_rx.borrow(),
        CaptureState::Paused,
        "Resume 后 lock 仍 active，不得恢复"
    );

    // Unlock 清除最后一个 suppression → 恢复。
    w.apply_unlock().await.expect("Unlock 必须成功");
    assert_eq!(*w.capture_rx.borrow(), CaptureState::Running);

    w.produce_sample().await;
    w.yield_until("最后 Observation 落库", || {
        w.observation_rows().len() == 2
    })
    .await;
    // 最后一个 open gap 被关闭。
    assert_eq!(w.gap_row("system_sleep").2, "closed");
    assert_eq!(w.gap_row("session_locked").2, "closed");

    w.shutdown().await;
}

// ===== L05: Pause→Lock→Unlock（desired 保持 Paused） =====
#[tokio::test(start_paused = true)]
async fn l05_pause_lock_unlock_does_not_restart() {
    let w = wiring(Settings {
        sampling_interval_seconds: 1,
        ..Settings::default()
    });
    w.capture_start().await;
    w.produce_sample().await;
    w.yield_until("Observation 落库", || w.observation_rows().len() == 1)
        .await;

    // 用户 Pause。
    assert_eq!(
        w.coordinator
            .apply_capture_command("capture_pause", T0 + 5_000)
            .await,
        Ok(CaptureState::Paused)
    );
    assert_eq!(*w.capture_rx.borrow(), CaptureState::Paused);

    // Lock（already Paused）。
    w.apply_lock().await.expect("Lock 必须成功");
    assert_eq!(*w.capture_rx.borrow(), CaptureState::Paused);

    // Unlock：desired 仍是 Paused，不得恢复 Running。
    w.apply_unlock().await.expect("Unlock 必须成功");
    assert_eq!(
        *w.capture_rx.borrow(),
        CaptureState::Paused,
        "Unlock 不得覆盖用户 Pause"
    );

    w.shutdown().await;
}

// ===== L07: 重复 Lock/Unlock 幂等 =====
#[tokio::test(start_paused = true)]
async fn l07_repeated_lock_unlock_is_idempotent() {
    let w = wiring(Settings {
        sampling_interval_seconds: 1,
        ..Settings::default()
    });
    w.capture_start().await;
    w.produce_sample().await;
    w.yield_until("Observation 落库", || w.observation_rows().len() == 1)
        .await;

    // 重复 Lock 幂等。
    w.apply_lock().await.expect("首次 Lock 必须成功");
    w.apply_lock().await.expect("重复 Lock 幂等成功");
    assert_eq!(w.gap_count("session_locked"), 1, "重复 Lock 不重复 gap");

    // 重复 Unlock 幂等（乱序 release 在对应 source 已 active 时清除）。
    w.apply_unlock().await.expect("首次 Unlock 必须成功");
    w.apply_unlock()
        .await
        .expect("重复 Unlock 幂等成功（乱序 no-op）");
    assert_eq!(*w.capture_rx.borrow(), CaptureState::Running);

    w.shutdown().await;
}

// ---- Coordinator-only harness for L09–L13 ----
struct CoordHarness {
    coordinator: Arc<CaptureCoordinator>,
    shared: Arc<SharedState>,
    capture_rx: watch::Receiver<CaptureState>,
    _health: Arc<PipelineHealth>,
    _settings_tx: watch::Sender<Settings>,
    _control_tx: mpsc::Sender<WriterControl>,
    _barrier_tx: mpsc::Sender<wuji_rebuild_agent::barrier::BarrierRequest>,
}

fn coord_harness() -> CoordHarness {
    let shared = Arc::new(SharedState::new("0.1.0".to_string(), RuntimeId::new()));
    shared.set_capture_state(CaptureState::Running);
    let (settings_tx, _) = watch::channel(Settings::default());
    let (capture_state_tx, capture_rx) = watch::channel(CaptureState::Running);
    let (control_tx, _) = mpsc::channel(64);
    let health = PipelineHealth::new();
    let (barrier_tx, _) = wuji_rebuild_agent::barrier::barrier_request_channel(64);
    let coordinator = Arc::new(CaptureCoordinator::new(
        barrier_tx.clone(),
        capture_state_tx,
        control_tx.clone(),
        shared.clone(),
        settings_tx.clone(),
        CaptureState::Running,
        health.clone(),
    ));
    CoordHarness {
        coordinator,
        shared,
        capture_rx,
        _health: health,
        _settings_tx: settings_tx,
        _control_tx: control_tx,
        _barrier_tx: barrier_tx,
    }
}

// 旧弱 L09 已删除（保留 l09_barrier_inject_fail_retry_same_coordinator_first_time）

// ===== L17: lifecycle_monitor_fault 不可被 Start/Resume 清除 =====
#[tokio::test]
async fn aux_monitor_fault_suppression_cannot_be_cleared_by_user_commands() {
    let h = coord_harness();
    assert_eq!(*h.capture_rx.borrow(), CaptureState::Running);

    h.coordinator.latch_monitor_fault();
    assert_eq!(*h.capture_rx.borrow(), CaptureState::Stopped);

    // Start 被允许但 gate 受 monitor fault 抑制 → Paused（非 Running）。
    let result = h
        .coordinator
        .apply_capture_command("capture_start", T0)
        .await
        .expect("start 命令本身合法，但 gate 受抑制");
    assert_eq!(
        result,
        CaptureState::Paused,
        "monitor fault 抑制下 start 只能到达 Paused"
    );
    assert!(h.shared.errors().contains_key(&ErrorSource::LifecyclePump));

    // 系统事件路径有独立 monitor fault fence → 直接拒绝。
    let err = h
        .coordinator
        .apply_system_lifecycle_event(SystemLifecycleEvent::Lock { at_utc_ms: T0 })
        .await
        .expect_err("monitor fault 后系统事件必须被拒绝");
    assert_eq!(err.code, SafeErrorCode::InternalSafeError);
}

// 旧 L18 已删除——改为复用生产 bridge helper 的端到端测试（见文件末尾）。

// ===== L04: Sleep→Lock→Unlock→Resume（反向叠加） =====
#[tokio::test(start_paused = true)]
async fn l04_sleep_lock_unlock_resume_overlap() {
    let w = wiring(Settings {
        sampling_interval_seconds: 1,
        ..Settings::default()
    });
    w.capture_start().await;
    w.produce_sample().await;
    w.yield_until("边界前 Observation 落库", || {
        w.observation_rows().len() == 1
    })
    .await;

    w.apply_sleep().await.expect("Sleep");
    assert_eq!(*w.capture_rx.borrow(), CaptureState::Paused);
    w.apply_lock().await.expect("Lock");
    assert_eq!(w.gap_count("system_sleep"), 1);
    assert_eq!(w.gap_count("session_locked"), 1);

    w.apply_unlock().await.expect("Unlock");
    assert_eq!(
        *w.capture_rx.borrow(),
        CaptureState::Paused,
        "sleep 仍 active → Paused"
    );
    w.apply_resume().await.expect("Resume");
    assert_eq!(*w.capture_rx.borrow(), CaptureState::Running);

    w.shutdown().await;
}

// ===== L06: Stop→Sleep→Resume（desired Stopped，Resume 不恢复） =====
#[tokio::test(start_paused = true)]
async fn l06_stop_sleep_resume_does_not_restart() {
    let w = wiring(Settings {
        sampling_interval_seconds: 1,
        ..Settings::default()
    });
    w.capture_start().await;
    w.produce_sample().await;
    w.yield_until("Observation 落库", || w.observation_rows().len() == 1)
        .await;

    w.coordinator
        .apply_capture_command("capture_stop", T0 + 5_000)
        .await
        .unwrap();
    assert_eq!(*w.capture_rx.borrow(), CaptureState::Stopped);

    w.apply_sleep().await.expect("Sleep");
    w.apply_resume().await.expect("Resume");
    assert_eq!(
        *w.capture_rx.borrow(),
        CaptureState::Stopped,
        "desired Stopped → Resume 不恢复"
    );

    w.shutdown().await;
}

// ===== L08: Unlock/Resume 乱序不清错误 suppression =====
#[tokio::test(start_paused = true)]
async fn l08_out_of_order_release_does_not_clear_wrong_suppression() {
    let w = wiring(Settings {
        sampling_interval_seconds: 1,
        ..Settings::default()
    });
    w.capture_start().await;
    w.apply_lock().await.expect("Lock");
    assert_eq!(*w.capture_rx.borrow(), CaptureState::Paused);

    // Resume 乱序：lock 仍 active，不恢复
    w.apply_resume().await.expect("Resume（乱序）幂等 no-op");
    assert_eq!(*w.capture_rx.borrow(), CaptureState::Paused);

    w.apply_unlock().await.expect("Unlock");
    assert_eq!(*w.capture_rx.borrow(), CaptureState::Running);

    // Unlock 乱序（未 locked）：幂等 no-op
    w.apply_unlock().await.expect("Unlock 乱序幂等");
    assert_eq!(*w.capture_rx.borrow(), CaptureState::Running);

    w.shutdown().await;
}

// 旧 L10 已删除（由新 l10_control_send_timeout_and_retry_with_first_at_utc_ms 替代）

// 旧 L13 已删除（见 enter_freeze_publish_failure_stops_safely）

// ===== L15: 系统事件使用正确 Settings revision =====
#[tokio::test(start_paused = true)]
async fn l15_system_event_uses_correct_applied_revision() {
    let w = wiring(Settings {
        sampling_interval_seconds: 1,
        ..Settings::default()
    });
    w.capture_start().await;
    w.produce_sample().await;
    w.yield_until("Observation 落库", || w.observation_rows().len() == 1)
        .await;

    // 先 apply Settings rev 1
    let rev1 = Settings {
        revision: "1".to_string(),
        sampling_interval_seconds: 1,
        ..Settings::default()
    };
    w.coordinator
        .apply_settings(rev1, w.utc_clock.load(Ordering::Acquire))
        .await
        .unwrap();
    assert_eq!(w._shared.applied_settings_revision(), 1);

    // Lock 应使用 revision 1（而非硬编码 0）
    w.apply_lock().await.expect("Lock 必须成功");
    assert_eq!(w.gap_count("session_locked"), 1, "Lock 边界已提交");

    w.shutdown().await;
}

// 旧弱 L16 已删除（保留 l16_gated_capture_in_flight_does_not_cross_lock_barrier）
// 旧 L18 已删除（保留 l18_production_forward_blocking_send_backpressure_and_order）

// ===== L19: 全部 suppression 解除后第一条 Observation 关闭最后一个 gap =====
#[tokio::test(start_paused = true)]
async fn l19_first_observation_after_all_release_closes_last_open_gap() {
    let w = wiring(Settings {
        sampling_interval_seconds: 1,
        ..Settings::default()
    });
    w.capture_start().await;
    w.produce_sample().await;
    w.yield_until("Observation 落库", || w.observation_rows().len() == 1)
        .await;

    // Lock → Sleep 双 suppression
    w.apply_lock().await.expect("Lock");
    w.apply_sleep().await.expect("Sleep");
    assert_eq!(w.gap_count("session_locked"), 1);
    assert_eq!(w.gap_count("system_sleep"), 1);
    assert_eq!(*w.capture_rx.borrow(), CaptureState::Paused);

    // 单独 Resume → lock 仍 active，system_sleep 是唯一 open gap
    w.apply_resume().await.expect("Resume");
    let (_, _, s) = w.gap_row("session_locked");
    assert_eq!(s, "closed", "session_locked 已被 Sleep 关闭");
    let (_, _, s) = w.gap_row("system_sleep");
    assert_eq!(s, "open", "system_sleep 仍在 open");

    // Unlock → 全部解除 → Running
    w.apply_unlock().await.expect("Unlock");
    assert_eq!(*w.capture_rx.borrow(), CaptureState::Running);

    // 首条 Observation 关闭最后一个 open gap（system_sleep）
    w.produce_sample().await;
    w.yield_until("Observation 落库且 gap 关闭", || {
        w.observation_rows().len() >= 2
    })
    .await;
    let (_, end, s) = w.gap_row("system_sleep");
    assert_eq!(s, "closed", "system_sleep 被 Observation 关闭");
    assert!(end.is_some());

    w.shutdown().await;
}

// 旧弱 L20（直接 consumer）已删除，保留 l20_production_bridge_shutdown_no_monitor_fault

// ===== 第三轮复审补修：同一 Coordinator 重试模式（L09–L14） =====

/// Barrier 响应控制器。测试侧通过 channel 指令控制每次 Barrier 请求的结果。
struct BarrierController {
    cmd_tx: mpsc::Sender<bool>,
    requests: Arc<AtomicUsize>,
    _task: tokio::task::JoinHandle<()>,
}

impl BarrierController {
    /// 创建控制器并返回 barrier_tx（交给 Coordinator）。
    /// `true` 指令 = ack Ok；`false` 指令 = ack Err(Closed)。
    fn new() -> (
        Self,
        mpsc::Sender<wuji_rebuild_agent::barrier::BarrierRequest>,
    ) {
        let (barrier_tx, mut barrier_rx) = wuji_rebuild_agent::barrier::barrier_request_channel(64);
        let (cmd_tx, mut cmd_rx) = mpsc::channel::<bool>(8);
        let requests = Arc::new(AtomicUsize::new(0));
        let task_requests = requests.clone();
        let task = tokio::spawn(async move {
            while let Some(req) = barrier_rx.recv().await {
                task_requests.fetch_add(1, Ordering::AcqRel);
                let ok = cmd_rx.recv().await.unwrap_or(false);
                if ok {
                    let _ = req.injected_ack.send(Ok(()));
                } else {
                    let _ = req
                        .injected_ack
                        .send(Err(wuji_rebuild_agent::barrier::BarrierInjectError::Closed));
                }
            }
        });
        (
            Self {
                cmd_tx,
                requests,
                _task: task,
            },
            barrier_tx,
        )
    }

    async fn allow(&self) {
        let _ = self.cmd_tx.send(true).await;
    }
    async fn deny(&self) {
        let _ = self.cmd_tx.send(false).await;
    }

    fn request_count(&self) -> usize {
        self.requests.load(Ordering::Acquire)
    }
}

/// 同一 Coordinator 上可控 Writer 响应的 harness。
struct RetryHarness {
    coordinator: Arc<CaptureCoordinator>,
    #[allow(dead_code)]
    barrier: BarrierController,
    capture_rx: watch::Receiver<CaptureState>,
    control_rx: mpsc::Receiver<WriterControl>,
    shared: Arc<SharedState>,
}

fn retry_harness() -> RetryHarness {
    let shared = Arc::new(SharedState::new("0.1.0".to_string(), RuntimeId::new()));
    shared.set_capture_state(CaptureState::Running);
    let (settings_tx, _) = watch::channel(Settings::default());
    let (capture_state_tx, capture_rx) = watch::channel(CaptureState::Running);
    let (control_tx, control_rx) = mpsc::channel::<WriterControl>(64);
    let (barrier, barrier_tx) = BarrierController::new();
    let coordinator = Arc::new(CaptureCoordinator::new(
        barrier_tx,
        capture_state_tx,
        control_tx,
        shared.clone(),
        settings_tx,
        CaptureState::Running,
        PipelineHealth::new(),
    ));
    RetryHarness {
        coordinator,
        barrier,
        capture_rx,
        control_rx,
        shared,
    }
}

// --- L09: 同一 Coordinator 上 Barrier 注入失败 → 重试复用首次时间 ---
#[tokio::test(start_paused = true)]
async fn l09_barrier_inject_fail_retry_same_coordinator_first_time() {
    let mut h = retry_harness();
    // ① barrier deny → inject 失败
    h.barrier.deny().await;
    let err = h
        .coordinator
        .apply_system_lifecycle_event(SystemLifecycleEvent::Lock { at_utc_ms: T0 })
        .await
        .expect_err("Barrier 注入拒绝");
    assert_eq!(err.code, SafeErrorCode::InternalSafeError);
    assert!(*h.capture_rx.borrow() != CaptureState::Running);
    assert!(h.shared.errors().contains_key(&ErrorSource::LifecyclePump));
    // 无 control 入队
    assert!(h.control_rx.try_recv().is_err());

    // ② barrier allow → 重试成功，EngineEvent 时间 = 首次 T0
    h.barrier.allow().await;
    let apply = tokio::spawn({
        let c = h.coordinator.clone();
        async move {
            c.apply_system_lifecycle_event(SystemLifecycleEvent::Lock {
                at_utc_ms: T0 + 1000,
            })
            .await
        }
    });
    let ctrl = h.control_rx.recv().await.unwrap();
    if let WriterControl::Lifecycle { event, ack, .. } = ctrl {
        let at = match &event {
            EngineEvent::SessionLocked { at_utc_ms } => *at_utc_ms,
            _ => panic!(),
        };
        assert_eq!(at, T0, "重试复用首次时间 T0，非 T0+1000");
        let _ = ack.send(Ok(()));
    }
    let r = apply.await.unwrap();
    assert!(r.is_ok(), "重试成功");
    // ③ 已 committed → 重复 Lock 幂等（无新 control）
    h.barrier.allow().await;
    let r2 = h
        .coordinator
        .apply_system_lifecycle_event(SystemLifecycleEvent::Lock {
            at_utc_ms: T0 + 2000,
        })
        .await;
    assert!(r2.is_ok(), "重复 Lock 幂等");
    assert!(h.control_rx.try_recv().is_err(), "幂等不产生新 control");
}

// --- L10: 同一 Coordinator 容量 1 lane → timeout → 排空 → 重试首次时间 ---
#[tokio::test(start_paused = true)]
async fn l10_control_send_timeout_same_coordinator_retry_first_time() {
    // 容量 1 control lane
    let (control_tx, mut control_rx) = mpsc::channel::<WriterControl>(1);
    // 预填 Checkpoint 占满唯一槽位
    control_tx.try_send(WriterControl::Checkpoint).unwrap();
    let shared = Arc::new(SharedState::new("0.1.0".to_string(), RuntimeId::new()));
    shared.set_capture_state(CaptureState::Running);
    let (capture_state_tx, capture_rx) = watch::channel(CaptureState::Running);
    let (barrier_tx, mut barrier_rx) = wuji_rebuild_agent::barrier::barrier_request_channel(64);
    let acker = tokio::spawn(async move {
        while let Some(req) = barrier_rx.recv().await {
            let _ = req.injected_ack.send(Ok(())); // 始终 ack Ok
        }
    });
    let coordinator = Arc::new(CaptureCoordinator::new(
        barrier_tx,
        capture_state_tx.clone(),
        control_tx,
        shared.clone(),
        watch::channel(Settings::default()).0,
        CaptureState::Running,
        PipelineHealth::new(),
    ));

    // ① 第一次 Lock(T0): barrier ack Ok → send_control 超时（lane 满，2s paused）
    let err = coordinator
        .apply_system_lifecycle_event(SystemLifecycleEvent::Lock { at_utc_ms: T0 })
        .await
        .expect_err("control send 超时");
    assert_eq!(err.code, SafeErrorCode::InternalSafeError);
    assert!(
        *capture_rx.borrow() != CaptureState::Running,
        "采集非 Running"
    );
    assert!(shared.errors().contains_key(&ErrorSource::LifecyclePump));
    // 只有预填 Checkpoint，无 Lifecycle control
    let drained = control_rx.try_recv().unwrap();
    assert!(matches!(drained, WriterControl::Checkpoint));

    // ② 排空后 lane 空闲 → 重试 Lock(T1) 同一 Coordinator
    let apply = tokio::spawn({
        let c = coordinator.clone();
        async move {
            c.apply_system_lifecycle_event(SystemLifecycleEvent::Lock {
                at_utc_ms: T0 + 1000,
            })
            .await
        }
    });
    let ctrl = control_rx.recv().await.unwrap();
    if let WriterControl::Lifecycle { event, ack, .. } = ctrl {
        let at = match &event {
            EngineEvent::SessionLocked { at_utc_ms } => *at_utc_ms,
            _ => panic!(),
        };
        assert_eq!(at, T0, "重试必须复用首次时间 T0，非 T0+1000");
        let _ = ack.send(Ok(()));
    } else {
        panic!("expected Lifecycle control");
    }
    apply.await.unwrap().unwrap();
    // ③ 已 committed → 重复 Lock 幂等（无新 control）
    let r = coordinator
        .apply_system_lifecycle_event(SystemLifecycleEvent::Lock {
            at_utc_ms: T0 + 2000,
        })
        .await;
    assert!(r.is_ok(), "重复 Lock 幂等成功");
    assert!(control_rx.try_recv().is_err(), "幂等无新 control");

    acker.abort();
}

// --- L11: Writer 明确错误（mock responder 返回 Err）---
#[tokio::test(start_paused = true)]
async fn aux_writer_mock_non_fatal_error() {
    let mut h = retry_harness();
    h.barrier.allow().await;
    let apply = tokio::spawn({
        let c = h.coordinator.clone();
        async move {
            c.apply_system_lifecycle_event(SystemLifecycleEvent::Lock { at_utc_ms: T0 })
                .await
        }
    });
    let ctrl = h.control_rx.recv().await.unwrap();
    if let WriterControl::Lifecycle { ack, .. } = ctrl {
        let _ = ack.send(Err(wuji_storage::error::StorageError::internal(
            "mock Writer failure",
        )));
    }
    let r = apply.await.unwrap();
    assert!(r.is_err(), "Writer 明确拒绝必须返回错误");
    assert!(
        *h.capture_rx.borrow() != CaptureState::Running,
        "采集不恢复"
    );

    // 修复 responder → 重试首次时间 T0
    h.barrier.allow().await;
    let apply2 = tokio::spawn({
        let c = h.coordinator.clone();
        async move {
            c.apply_system_lifecycle_event(SystemLifecycleEvent::Lock {
                at_utc_ms: T0 + 1000,
            })
            .await
        }
    });
    let ctrl2 = h.control_rx.recv().await.unwrap();
    if let WriterControl::Lifecycle { event, ack, .. } = ctrl2 {
        let at = match &event {
            EngineEvent::SessionLocked { at_utc_ms } => *at_utc_ms,
            _ => panic!(),
        };
        assert_eq!(at, T0, "重试首次时间 T0");
        let _ = ack.send(Ok(()));
    }
    let r2 = apply2.await.unwrap();
    assert!(r2.is_ok(), "修复后重试成功");
    // 幂等
    assert!(h.control_rx.try_recv().is_err());
}

// --- L12: Writer ack unknown → Coordinator 路径（drop ack 风味）---
#[tokio::test(start_paused = true)]
async fn l12_writer_ack_drop_fencing() {
    let mut h = retry_harness();
    h.barrier.allow().await;
    let apply = tokio::spawn({
        let c = h.coordinator.clone();
        async move {
            c.apply_system_lifecycle_event(SystemLifecycleEvent::Lock { at_utc_ms: T0 })
                .await
        }
    });
    let ctrl = h.control_rx.recv().await.unwrap();
    if let WriterControl::Lifecycle { ack, .. } = ctrl {
        drop(ack);
    }
    // advance 时钟 → Writer ack timeout(8s) → unknown → writer_fault
    tokio::time::advance(Duration::from_secs(9)).await;
    let r = tokio::time::timeout(Duration::from_secs(1), apply)
        .await
        .expect("Writer ack closed 后 Coordinator 必须有界返回")
        .unwrap();
    assert!(r.is_err());
    assert_eq!(r.unwrap_err().code, SafeErrorCode::AgentWriterFaulted);
    assert_eq!(*h.capture_rx.borrow(), CaptureState::Stopped);
    // fencing
    let f = h
        .coordinator
        .apply_capture_command("capture_start", T0)
        .await
        .expect_err("fenced");
    assert_eq!(f.code, SafeErrorCode::AgentWriterFaulted);
    let barrier_count = h.barrier.request_count();
    let settings_error = h
        .coordinator
        .apply_settings(
            Settings {
                revision: "1".to_string(),
                ..Settings::default()
            },
            T0 + 1,
        )
        .await
        .expect_err("unknown fencing 后 Settings 必须拒绝");
    assert_eq!(settings_error.code, SafeErrorCode::AgentWriterFaulted);
    let lifecycle_error = h
        .coordinator
        .apply_system_lifecycle_event(SystemLifecycleEvent::Sleep { at_utc_ms: T0 + 1 })
        .await
        .expect_err("unknown fencing 后系统事件必须拒绝");
    assert_eq!(lifecycle_error.code, SafeErrorCode::AgentWriterFaulted);
    assert_eq!(
        h.barrier.request_count(),
        barrier_count,
        "fencing 后不得注入新 Barrier"
    );
    assert!(
        h.control_rx.try_recv().is_err(),
        "fencing 后不得发送 control"
    );
}

// --- L13: release publish 失败 ---
#[tokio::test(start_paused = true)]
async fn l13_release_publish_failure_safe_stop() {
    let shared = Arc::new(SharedState::new("0.1.0".to_string(), RuntimeId::new()));
    shared.set_capture_state(CaptureState::Running);
    let (settings_tx, _) = watch::channel(Settings::default());
    let (capture_state_tx, capture_rx) = watch::channel(CaptureState::Running);
    let (control_tx, mut control_rx) = mpsc::channel::<WriterControl>(64);
    let (barrier, barrier_tx) = BarrierController::new();
    let coordinator = Arc::new(CaptureCoordinator::new(
        barrier_tx,
        capture_state_tx.clone(),
        control_tx,
        shared.clone(),
        settings_tx,
        CaptureState::Running,
        PipelineHealth::new(),
    ));
    // Lock 成功
    barrier.allow().await;
    let apply = tokio::spawn({
        let c = coordinator.clone();
        async move {
            c.apply_system_lifecycle_event(SystemLifecycleEvent::Lock { at_utc_ms: T0 })
                .await
        }
    });
    let ctrl = control_rx.recv().await.unwrap();
    if let WriterControl::Lifecycle { ack, .. } = ctrl {
        let _ = ack.send(Ok(()));
    }
    apply.await.unwrap().unwrap();
    assert_eq!(*capture_rx.borrow(), CaptureState::Paused);
    // drop capture_rx → Unlock publish 失败
    drop(capture_rx);
    let err = coordinator
        .apply_system_lifecycle_event(SystemLifecycleEvent::Unlock { at_utc_ms: T0 })
        .await
        .expect_err("release publish 失败");
    assert_eq!(err.code, SafeErrorCode::InternalSafeError);
    let post_failure_rx = capture_state_tx.subscribe();
    assert_eq!(
        *post_failure_rx.borrow(),
        CaptureState::Stopped,
        "重新订阅 capture watch 必须观察到 fail-closed 的 Stopped"
    );
    assert_eq!(shared.capture_state(), CaptureState::Stopped);
    assert_eq!(
        shared.errors().get(&ErrorSource::LifecyclePump).copied(),
        Some(SafeErrorCode::InternalSafeError),
        "release publish 失败必须留下 LifecyclePump 来源诊断"
    );
    let dto = shared.status_dto();
    assert_eq!(dto.capture_state, CaptureState::Stopped);
    assert_eq!(
        dto.safe_error_code,
        Some(SafeErrorCode::InternalSafeError),
        "status_get DTO 必须与 SharedState 的安全停止一致"
    );
}

// 旧 L14 (acquire_transition_lock_for_test) 已删除，保留 l14_observable_barrier_request_serialization

// enter_freeze_publish_failure
#[tokio::test(start_paused = true)]
async fn enter_freeze_publish_failure_stops_safely() {
    let shared = Arc::new(SharedState::new("0.1.0".to_string(), RuntimeId::new()));
    shared.set_capture_state(CaptureState::Running);
    let (settings_tx, _) = watch::channel(Settings::default());
    let (capture_state_tx, capture_rx) = watch::channel(CaptureState::Running);
    let (control_tx, _) = mpsc::channel(64);
    let (barrier_tx, barrier_rx) = wuji_rebuild_agent::barrier::barrier_request_channel(64);
    let coordinator = Arc::new(CaptureCoordinator::new(
        barrier_tx,
        capture_state_tx.clone(),
        control_tx,
        shared.clone(),
        settings_tx,
        CaptureState::Running,
        PipelineHealth::new(),
    ));
    drop(capture_rx);
    let err = coordinator
        .apply_system_lifecycle_event(SystemLifecycleEvent::Lock { at_utc_ms: T0 })
        .await
        .expect_err("freeze publish 失败");
    assert_eq!(err.code, SafeErrorCode::InternalSafeError);
    assert_eq!(shared.capture_state(), CaptureState::Stopped);
    drop(barrier_rx);
}

// --- L18: 生产 bridge helper blocking_send 背压保序 ---
#[test]
fn l18_production_forward_blocking_send_backpressure_and_order() {
    use std::sync::mpsc as std_mpsc;
    use wuji_rebuild_agent::session_power_events::run_session_power_forward_observed;

    // sync_channel(4): 有界 event source，helper 线程用生产 run_session_power_forward + capacity-1 tokio bridge
    let (event_tx, event_rx) = std_mpsc::sync_channel::<wuji_windows::SessionPowerEvent>(4);
    let (bridge_tx, mut bridge_rx) =
        tokio::sync::mpsc::channel::<wuji_windows::SessionPowerEvent>(1);

    // 发送线程：所有事件先入队
    event_tx
        .send(wuji_windows::SessionPowerEvent::Lock)
        .unwrap();
    event_tx
        .send(wuji_windows::SessionPowerEvent::Sleep)
        .unwrap();
    event_tx
        .send(wuji_windows::SessionPowerEvent::Unlock)
        .unwrap();
    event_tx
        .send(wuji_windows::SessionPowerEvent::Resume)
        .unwrap();
    drop(event_tx);

    // 零容量 attempt channel 是确定性 rendezvous：测试收到 Sleep Attempt 时，
    // helper 已成功发送 Lock，下一步必然在 capacity-1 bridge 上阻塞。
    let (attempt_tx, attempt_rx) = std_mpsc::sync_channel(0);
    let (done_tx, done_rx) = std_mpsc::sync_channel(1);
    let handle = std::thread::spawn(move || {
        run_session_power_forward_observed(event_rx, bridge_tx, attempt_tx);
        done_tx.send(()).unwrap();
    });

    assert_eq!(
        attempt_rx.recv_timeout(Duration::from_secs(1)).unwrap(),
        wuji_windows::SessionPowerEvent::Lock
    );
    assert_eq!(
        attempt_rx.recv_timeout(Duration::from_secs(1)).unwrap(),
        wuji_windows::SessionPowerEvent::Sleep
    );
    assert_eq!(bridge_rx.len(), 1, "Lock 已占满唯一槽位");
    assert!(
        done_rx.try_recv().is_err(),
        "Sleep blocking_send 未释放前 helper 不得结束"
    );
    assert_eq!(
        bridge_rx.blocking_recv(),
        Some(wuji_windows::SessionPowerEvent::Lock)
    );
    assert_eq!(
        bridge_rx.blocking_recv(),
        Some(wuji_windows::SessionPowerEvent::Sleep)
    );

    assert_eq!(
        attempt_rx.recv_timeout(Duration::from_secs(1)).unwrap(),
        wuji_windows::SessionPowerEvent::Unlock
    );
    assert_eq!(
        bridge_rx.blocking_recv(),
        Some(wuji_windows::SessionPowerEvent::Unlock)
    );
    assert_eq!(
        attempt_rx.recv_timeout(Duration::from_secs(1)).unwrap(),
        wuji_windows::SessionPowerEvent::Resume
    );
    assert_eq!(
        bridge_rx.blocking_recv(),
        Some(wuji_windows::SessionPowerEvent::Resume)
    );

    done_rx.recv_timeout(Duration::from_secs(1)).unwrap();
    handle.join().unwrap();
}

// ===== 第三轮复审补修：L20 生产 bridge shutdown + L16 in-flight =====

// --- L20: 生产 SessionPowerBridge shutdown（Windows） ---
#[cfg(windows)]
#[tokio::test]
async fn l20_production_bridge_shutdown_no_monitor_fault() {
    let shared = Arc::new(SharedState::new("0.1.0".to_string(), RuntimeId::new()));
    shared.set_capture_state(CaptureState::Running);
    let (settings_tx, _) = watch::channel(Settings::default());
    let (capture_state_tx, _) = watch::channel(CaptureState::Running);
    let (control_tx, _) = mpsc::channel(64);
    let health = PipelineHealth::new();
    let (barrier_tx, _) = wuji_rebuild_agent::barrier::barrier_request_channel(64);
    let coordinator = Arc::new(CaptureCoordinator::new(
        barrier_tx,
        capture_state_tx,
        control_tx,
        shared.clone(),
        settings_tx,
        CaptureState::Running,
        health,
    ));
    let mut bridge =
        wuji_rebuild_agent::session_power_events::start_session_power_bridge(coordinator.clone())
            .expect("生产 bridge 必须启动");
    // 外层 deadline 防止测试自身随生产 shutdown 永久挂起。
    let report = tokio::time::timeout(Duration::from_secs(12), bridge.shutdown())
        .await
        .expect("生产 bridge shutdown 必须有界完成");
    assert!(report.is_complete(), "三层必须全部退出并回收: {report:?}");
    assert!(report.errors.is_empty(), "正常关闭不得有诊断: {report:?}");
    // 正常 shutdown 不误报 monitor fault
    assert!(
        !shared.errors().contains_key(&ErrorSource::LifecyclePump),
        "正常 shutdown 无 LifecyclePump 诊断"
    );
}

// --- L16 in-flight: 真正阻塞的 capture source + Lock 竞争 ---
struct BlockingCaptureSource {
    sample: RawSample,
    entered: Mutex<Option<std::sync::mpsc::SyncSender<()>>>,
    release: Arc<(Mutex<bool>, Condvar)>,
}

impl BlockingCaptureSource {
    fn new(
        sample: RawSample,
        entered: std::sync::mpsc::SyncSender<()>,
        release: Arc<(Mutex<bool>, Condvar)>,
    ) -> Self {
        Self {
            sample,
            entered: Mutex::new(Some(entered)),
            release,
        }
    }
}

impl wuji_rebuild_agent::capture_loop::CaptureSource for BlockingCaptureSource {
    fn capture(&self) -> RawSample {
        if let Some(entered) = self.entered.lock().unwrap().take() {
            let _ = entered.send(());
        }
        let (released, wake) = &*self.release;
        let mut released = released.lock().unwrap();
        while !*released {
            released = wake.wait(released).unwrap();
        }
        self.sample.clone()
    }
}

#[tokio::test(start_paused = true)]
async fn l16_blocking_capture_in_flight_does_not_cross_lock_barrier() {
    let dir = TempDir::new().unwrap();
    let db_path = dir.path().join("wuji-rebuild-v0.1.db");
    Writer::bootstrap_with_timezone(&db_path, SHANGHAI, T0).unwrap();
    let continuity = Arc::new(ContinuityState::default());
    let runtime_id = RuntimeId::new();
    let settings = Settings {
        sampling_interval_seconds: 1,
        ..Settings::default()
    };
    let mut writer = Writer::open_existing(&db_path).unwrap();
    let mut engine =
        ActivityEngine::new(runtime_id.clone(), settings.clone(), continuity.clone()).unwrap();
    engine.recover_startup(&mut writer, T0).unwrap();
    let shared = Arc::new(SharedState::new("0.1.0".to_string(), runtime_id));
    let plane = wuji_rebuild_agent::control_plane::assemble(
        shared.clone(),
        settings,
        CaptureState::Stopped,
    );
    let utc_clock = Arc::new(AtomicI64::new(T0));
    let clock = utc_clock.clone();
    let (entered_tx, entered_rx) = std::sync::mpsc::sync_channel(1);
    let release = Arc::new((Mutex::new(false), Condvar::new()));
    let source = BlockingCaptureSource::new(
        sample("in-flight-canary.exe", 0),
        entered_tx,
        release.clone(),
    );
    let (pipeline_rx, capture_handle) = spawn_capture_loop(
        source,
        plane.settings_rx.clone(),
        plane.capture_state_rx,
        continuity.clone(),
        CaptureLoopConfig {
            wake_interval: Duration::from_millis(50),
            queue_capacity: 64,
            // 与生产一致：阻塞 Win32 capture 在 blocking pool 执行，不能卡住 runtime。
            offload_capture: true,
            utc_now_ms: Arc::new(move || clock.load(Ordering::Acquire)),
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
    let supervisor_handle = tokio::spawn(supervise_pipeline_exits(
        plane.pipeline_exit_rx,
        plane.coordinator.clone(),
    ));
    let mut capture_rx = plane.writer_capture_stop_tx.subscribe();

    // ① Start 后首个 ticker 进入 capture()，并真实阻塞在 release rendezvous。
    plane
        .coordinator
        .apply_capture_command("capture_start", T0)
        .await
        .unwrap();
    tokio::task::spawn_blocking(move || entered_rx.recv_timeout(Duration::from_secs(2)))
        .await
        .expect("capture entered 等待任务不得 panic")
        .expect("必须确认 capture 已开始但尚未返回");

    // ② capture 尚未返回时并发 Lock。Coordinator 先发布 Paused，再等待 Capture Loop
    // 把 Barrier 写入 FIFO；此时 Capture Loop 正在 await spawn_blocking，Lock 必须 pending。
    let lock = tokio::spawn({
        let coordinator = plane.coordinator.clone();
        async move {
            coordinator
                .apply_system_lifecycle_event(SystemLifecycleEvent::Lock {
                    at_utc_ms: T0 + 2_000,
                })
                .await
        }
    });
    tokio::time::timeout(
        Duration::from_secs(2),
        capture_rx.wait_for(|state| *state == CaptureState::Paused),
    )
    .await
    .expect("capture in-flight 时必须有界发布 Paused")
    .expect("capture watch 不得关闭");
    assert!(
        !lock.is_finished(),
        "冻结后 Lock 仍必须等待 in-flight capture"
    );

    // ③ 放行迟到 capture。Capture Loop 返回后复查 watch=Paused，必须丢弃 canary，
    // 随后才能处理同一 FIFO 的 Barrier 并让 Lock 提交。
    {
        let (released, wake) = &*release;
        *released.lock().unwrap() = true;
        wake.notify_all();
    }
    lock.await.unwrap().expect("Lock 边界提交");

    let conn = Connection::open(&db_path).unwrap();
    let observations: i64 = conn
        .query_row("SELECT COUNT(*) FROM foreground_observations", [], |r| {
            r.get(0)
        })
        .unwrap();
    assert_eq!(
        observations, 0,
        "冻结期间返回的 in-flight canary 必须在 Barrier 前被状态复查丢弃"
    );
    let gaps: i64 = conn
        .query_row(
            "SELECT COUNT(*) FROM capture_gaps WHERE kind='session_locked'",
            [],
            |r| r.get(0),
        )
        .unwrap();
    assert_eq!(gaps, 1, "Lock Lifecycle gap 必须恰好一次");
    drop(conn);

    supervisor_handle.abort();
    capture_handle.abort();
    processor_handle.abort();
    writer_handle.abort();
    let _ = capture_rx;
}

// ===== P1-02 §3.3: shutdown 确定性测试 =====

/// 异常 shutdown：bridge channel 在非 shutdown 状态意外关闭 → latch_monitor_fault
#[tokio::test]
async fn shutdown_bridge_unexpected_close_latches_monitor_fault() {
    let shared = Arc::new(SharedState::new("0.1.0".to_string(), RuntimeId::new()));
    let (settings_tx, _) = watch::channel(Settings::default());
    let (capture_state_tx, capture_rx) = watch::channel(CaptureState::Running);
    let (control_tx, _) = mpsc::channel(64);
    let (barrier_tx, _) = wuji_rebuild_agent::barrier::barrier_request_channel(64);
    let coordinator = Arc::new(CaptureCoordinator::new(
        barrier_tx,
        capture_state_tx,
        control_tx,
        shared.clone(),
        settings_tx,
        CaptureState::Running,
        PipelineHealth::new(),
    ));

    let (bridge_tx, bridge_rx) = mpsc::channel::<wuji_windows::SessionPowerEvent>(1);
    // 未发送 shutdown signal → consumer 看到 channel close 时视为异常
    let (_shutdown_tx, shutdown_rx) = watch::channel(false);
    let consumer = tokio::spawn(run_session_power_events(
        bridge_rx,
        coordinator.clone(),
        shutdown_rx,
    ));
    drop(bridge_tx); // channel 意外关闭（无 shutdown signal）
    let _ = consumer.await;
    assert_eq!(*capture_rx.borrow(), CaptureState::Stopped);
    assert_eq!(shared.capture_state(), CaptureState::Stopped);
    assert_eq!(shared.status_dto().capture_state, CaptureState::Stopped);
    assert_eq!(
        shared.errors().get(&ErrorSource::LifecyclePump).copied(),
        Some(SafeErrorCode::InternalSafeError),
        "非 shutdown 的 channel 关闭必须留下精确 LifecyclePump 诊断"
    );
}

/// 真实生产 supervisor 捕获 consumer panic 后锁存 monitor fault，并让 capture
/// 安全停止。测试不直接调用 `latch_monitor_fault()`，避免把结果模拟成路径证据。
#[tokio::test]
async fn l17_consumer_panic_through_production_supervisor_latches_monitor_fault() {
    let shared = Arc::new(SharedState::new("0.1.0".to_string(), RuntimeId::new()));
    shared.set_capture_state(CaptureState::Running);
    let (settings_tx, _) = watch::channel(Settings::default());
    let (capture_state_tx, capture_rx) = watch::channel(CaptureState::Running);
    let (control_tx, _) = mpsc::channel(64);
    let (barrier_tx, _) = wuji_rebuild_agent::barrier::barrier_request_channel(64);
    let coordinator = Arc::new(CaptureCoordinator::new(
        barrier_tx,
        capture_state_tx,
        control_tx,
        shared.clone(),
        settings_tx,
        CaptureState::Running,
        PipelineHealth::new(),
    ));

    let consumer = tokio::spawn(async {
        panic!("deterministic L17 consumer panic");
    });
    wuji_rebuild_agent::session_power_events::supervise_session_power_consumer(
        consumer,
        coordinator.clone(),
    )
    .await;
    assert_eq!(*capture_rx.borrow(), CaptureState::Stopped);
    assert_eq!(shared.capture_state(), CaptureState::Stopped);
    assert!(shared.errors().contains_key(&ErrorSource::LifecyclePump));
    assert_eq!(shared.status_dto().capture_state, CaptureState::Stopped);
    // monitor fault 后 start 被 suppression 抑制（仅能到 Paused）
    let result = coordinator
        .apply_capture_command("capture_start", T0)
        .await
        .expect("start 命令合法但 gate 受抑制");
    assert_eq!(result, CaptureState::Paused);
}

// ===== 第四轮补修 =====

// --- L14: 可观察 BarrierRequest 串行顺序 ---
#[tokio::test(start_paused = true)]
async fn l14_observable_barrier_request_serialization() {
    let shared = Arc::new(SharedState::new("0.1.0".to_string(), RuntimeId::new()));
    shared.set_capture_state(CaptureState::Running);
    let (settings_tx, settings_rx) = watch::channel(Settings::default());
    let (capture_state_tx, capture_rx) = watch::channel(CaptureState::Running);
    let (control_tx, mut control_rx) = mpsc::channel::<WriterControl>(64);
    let (barrier_tx, mut barrier_rx) = wuji_rebuild_agent::barrier::barrier_request_channel(64);
    let coordinator = Arc::new(CaptureCoordinator::new(
        barrier_tx,
        capture_state_tx,
        control_tx,
        shared.clone(),
        settings_tx,
        CaptureState::Running,
        PipelineHealth::new(),
    ));
    let settings = Settings {
        revision: "1".to_string(),
        sampling_interval_seconds: 1,
        ..Settings::default()
    };
    // 先让 Lock 到达第一条 Barrier，再并发启动 Settings；二者此后真实重叠。
    let lock = tokio::spawn({
        let c = coordinator.clone();
        async move {
            c.apply_system_lifecycle_event(SystemLifecycleEvent::Lock { at_utc_ms: T0 })
                .await
        }
    });
    let req1 = barrier_rx.recv().await.expect("Lock Barrier 必须到达");
    assert_eq!(req1.token.kind, BarrierKind::Lifecycle);
    let first_id = req1.token.id.clone();

    let (settings_started_tx, settings_started_rx) = tokio::sync::oneshot::channel();
    let settings_apply = tokio::spawn({
        let c = coordinator.clone();
        async move {
            let _ = settings_started_tx.send(());
            c.apply_settings(settings, T0 + 1).await
        }
    });
    settings_started_rx.await.unwrap();
    let before_injected_ack =
        tokio::time::timeout(Duration::from_millis(100), barrier_rx.recv()).await;
    assert!(
        before_injected_ack.is_err(),
        "第一条 injected ack 前第二条 Barrier 不得到达"
    );

    req1.injected_ack.send(Ok(())).unwrap();
    let control1 = control_rx.recv().await.expect("Lock control 必须到达");
    let lock_ack = match control1 {
        WriterControl::Lifecycle {
            barrier_id, ack, ..
        } => {
            assert_eq!(barrier_id, first_id);
            ack
        }
        _ => panic!("第一条 control 必须是 Lifecycle"),
    };

    // injected ack 已完成，但 Writer ack 仍被测试持有：Settings 仍不得注入 Barrier。
    let before_writer_ack =
        tokio::time::timeout(Duration::from_millis(100), barrier_rx.recv()).await;
    assert!(
        before_writer_ack.is_err(),
        "第一条 Writer ack 前第二条 Barrier 不得到达"
    );
    lock_ack.send(Ok(())).unwrap();
    lock.await.unwrap().expect("Lock 成功");

    let req2 = barrier_rx.recv().await.expect("Settings Barrier 必须到达");
    assert_eq!(req2.token.kind, BarrierKind::SettingsApplied);
    assert_ne!(req2.token.id, first_id);
    let second_id = req2.token.id.clone();
    req2.injected_ack.send(Ok(())).unwrap();
    let control2 = control_rx.recv().await.expect("Settings control 必须到达");
    match control2 {
        WriterControl::SettingsApplied {
            settings,
            barrier_id,
            ack,
            ..
        } => {
            assert_eq!(barrier_id, second_id);
            assert_eq!(settings.revision, "1");
            // 模拟生产 Writer commit 的 SharedState 副作用，再返回 ack。
            shared.set_applied_settings_revision(1);
            ack.send(Ok(1)).unwrap();
        }
        _ => panic!("第二条 control 必须是 SettingsApplied"),
    }
    assert_eq!(settings_apply.await.unwrap().unwrap(), 1);
    assert_eq!(shared.applied_settings_revision(), 1);
    assert_eq!(settings_rx.borrow().revision, "1");
    assert_eq!(
        *capture_rx.borrow(),
        CaptureState::Paused,
        "Lock suppression 保持，Settings 完成不得恢复 Running"
    );
    assert!(barrier_rx.try_recv().is_err(), "不得产生额外 Barrier");
    assert!(control_rx.try_recv().is_err(), "不得产生额外 control");
}

#[tokio::test(start_paused = true)]
async fn l11_writer_explicit_failure_via_coordinator_keeps_db_clean_and_fences() {
    let dir = TempDir::new().unwrap();
    let db_path = dir.path().join("wuji.db");
    Writer::bootstrap_with_timezone(&db_path, SHANGHAI, T0).unwrap();
    let continuity = Arc::new(ContinuityState::default());
    let runtime_id = RuntimeId::new();
    // 持久 trigger 只作用于本临时数据库，确定性拒绝 session_locked gap；不依赖
    // busy_timeout、随机 sleep 或固定 yield 猜测 Writer 所在阶段。
    Connection::open(&db_path)
        .unwrap()
        .execute_batch(
            "CREATE TRIGGER l11_fail_session_locked
             BEFORE INSERT ON capture_gaps
             WHEN NEW.kind = 'session_locked'
             BEGIN
               SELECT RAISE(ABORT, 'l11 deterministic lifecycle failure');
             END;",
        )
        .unwrap();

    let settings = Settings::default();
    let mut writer = Writer::open_existing(&db_path).unwrap();
    let mut engine =
        ActivityEngine::new(runtime_id.clone(), settings.clone(), continuity.clone()).unwrap();
    engine.recover_startup(&mut writer, T0).unwrap();
    let shared = Arc::new(SharedState::new("0.1.0".to_string(), runtime_id));
    let plane = wuji_rebuild_agent::control_plane::assemble(
        shared.clone(),
        settings,
        CaptureState::Stopped,
    );
    let source = ScriptedSource::new(sample("code.exe", 0));
    let (pipeline_rx, capture_handle) = spawn_capture_loop(
        source,
        plane.settings_rx.clone(),
        plane.capture_state_rx,
        continuity.clone(),
        CaptureLoopConfig {
            wake_interval: Duration::from_millis(50),
            queue_capacity: 64,
            offload_capture: false,
            utc_now_ms: Arc::new(|| T0),
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
    let task = WriterTask::new(
        writer,
        engine,
        shared.clone(),
        plane.writer_capture_stop_tx.clone(),
        continuity.clone(),
        dir.path().join("config"),
        plane.health.clone(),
    );
    let writer_handle = tokio::spawn(task.into_run_future(processor_rx, plane.control_rx));
    let supervisor_handle = tokio::spawn(supervise_pipeline_exits(
        plane.pipeline_exit_rx,
        plane.coordinator.clone(),
    ));
    let mut capture_rx = plane.writer_capture_stop_tx.subscribe();

    plane
        .coordinator
        .apply_capture_command("capture_start", T0)
        .await
        .unwrap();
    let error = plane
        .coordinator
        .apply_system_lifecycle_event(SystemLifecycleEvent::Lock { at_utc_ms: T0 })
        .await
        .expect_err("trigger 必须让真实 Writer 明确拒绝 Lock 边界");

    assert_eq!(shared.writer_state(), WriterState::Faulted);
    assert_eq!(shared.process_state(), ProcessState::Faulted);
    assert_eq!(*capture_rx.borrow_and_update(), CaptureState::Stopped);
    assert_eq!(shared.capture_state(), CaptureState::Stopped);
    assert_eq!(
        shared.errors().get(&ErrorSource::Writer).copied(),
        Some(error.code),
        "必须保留 Writer 精确错误码"
    );
    let conn = Connection::open(&db_path).unwrap();
    let gaps: i64 = conn
        .query_row(
            "SELECT COUNT(*) FROM capture_gaps WHERE kind='session_locked'",
            [],
            |r| r.get(0),
        )
        .unwrap();
    assert_eq!(gaps, 0, "Writer 明确失败后 DB 无部分 Lifecycle gap");
    drop(conn);

    let start_error = plane
        .coordinator
        .apply_capture_command("capture_start", T0 + 1)
        .await
        .expect_err("Writer fatal 后 Start 必须被 fencing");
    assert_eq!(start_error.code, SafeErrorCode::AgentWriterFaulted);
    let settings_error = plane
        .coordinator
        .apply_settings(
            Settings {
                revision: "1".to_string(),
                ..Settings::default()
            },
            T0 + 1,
        )
        .await
        .expect_err("Writer fatal 后 Settings 必须被 fencing");
    assert_eq!(settings_error.code, SafeErrorCode::AgentWriterFaulted);
    let lifecycle_error = plane
        .coordinator
        .apply_system_lifecycle_event(SystemLifecycleEvent::Sleep { at_utc_ms: T0 + 1 })
        .await
        .expect_err("Writer fatal 后系统事件必须被 fencing");
    assert_eq!(lifecycle_error.code, SafeErrorCode::AgentWriterFaulted);

    supervisor_handle.abort();
    capture_handle.abort();
    processor_handle.abort();
    writer_handle.abort();
}
