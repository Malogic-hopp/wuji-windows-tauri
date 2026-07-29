//! 阶段 4.4（P1-04）：Settings effectivity 与 revision 一致性的确定性测试。
//!
//! 拓扑（除 E07/E08 特别说明外）：与 `main.rs` 同一装配函数
//! `control_plane::assemble` 的唯一 CaptureCoordinator + 真实 Capture Loop
//! （脚本化采集源，样本仍经唯一 CapturePipelineItem FIFO，不绕行）+
//! 真实 Processor + 真实 WriterTask + 临时 v0.1 SQLite + pipeline supervisor。
//!
//! 时钟纪律：paused Tokio 时钟驱动全部任务定时器；Capture Loop 的 UTC
//! 时间戳来自注入式确定性时钟（`CaptureLoopConfig::utc_now_ms`），样本步进时
//! 两个时钟同步前进，引擎 UTC/monotonic skew 检查恒为零。
//!
//! rendezvous 纪律：只用 channel/watch/queue-depth/SQLite 条件 + 有界
//! yield/advance 等待，不使用随机 sleep、不扩大 timeout、不降低断言。
//!
//! 覆盖启动准备文档 E01–E12：
//! - E01 `e01_revision_boundary_zero_to_one`
//! - E02 `e02_excluded_add_switches_observation_to_privacy_excluded`
//! - E03 `e03_excluded_remove_restores_observation_without_privacy_leak`
//! - E04 `e04_idle_threshold_change_reclassifies_same_reading`
//! - E05 `e05_work_break_threshold_applies_only_after_boundary`
//! - E06 `e06_capture_error_carries_and_passes_revision_on_both_sides`
//! - E07 `e07_processor_mismatch_{observation,privacy_excluded,capture_error}_fails_closed`
//! - E08 `e08_writer_defensive_mismatch_rejects_all_three_variants`
//! - E09 `e09_commit_failure_keeps_last_known_good`
//! - E10 `e10_retry_after_repair_commits_exactly_once`
//! - E11 `e11a_post_fault_settings_are_fenced_uncommitted`、
//!   `e11_consumer_exit_after_commit_keeps_committed_and_fences` 与
//!   `e11c_transition_in_flight_processor_exit_is_uncommitted_and_safe_stops`
//! - E12 `e12_ipc_reload_and_reconciler_single_commit_with_effectivity`

use std::collections::VecDeque;
use std::sync::atomic::{AtomicI64, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use rusqlite::Connection;
use tempfile::TempDir;
use tokio::sync::{mpsc, watch};
use wuji_core::domain::{ActivityState, CaptureQuality, CaptureState, ProcessState, WriterState};
use wuji_core::dto::RuntimeId;
use wuji_core::error::{ErrorSource, SafeErrorCode};
use wuji_core::pipeline::{
    BarrierId, BarrierKind, BarrierToken, CapturePipelineItem, FilteredObservation, IdleReading,
    ProcessorOutput,
};
use wuji_core::settings::Settings;
use wuji_rebuild_agent::activity::{ActivityEngine, EngineEvent};
use wuji_rebuild_agent::capture_coordinator::{CaptureCoordinator, SystemLifecycleEvent};
use wuji_rebuild_agent::capture_loop::{
    CaptureLoopConfig, CaptureSource, ContinuityState, RawSample, spawn_capture_loop,
};
use wuji_rebuild_agent::command_server::{
    CommandServerContext, RequestIdCache, handle_request_line,
};
use wuji_rebuild_agent::control_plane::{MaintenanceControl, supervise_pipeline_exits};
use wuji_rebuild_agent::pipeline_health::{PipelineHealth, PipelineTask, TaskLifecycle};
use wuji_rebuild_agent::processor_task::{
    spawn_observation_processor, spawn_observation_processor_with_capacity,
};
use wuji_rebuild_agent::settings_reconciler::run_settings_reconciler_observed;
use wuji_rebuild_agent::shared::SharedState;
use wuji_rebuild_agent::writer_task::{WriterControl, WriterDataMessage, WriterTask};
use wuji_storage::Writer;

const T0: i64 = 1_784_332_800_000;
const SHANGHAI: &str = "Asia/Shanghai";
/// 常规样本时间步长：与 sampling_interval_seconds=1 相等，保证一次
/// advance 恰好产生一条样本，且 UTC/monotonic 同步（引擎 skew 检查恒为零）。
const SAMPLE_STEP_MS: i64 = 1_000;

/// 脚本化采集源：按 FIFO 弹出脚本样本，脚本为空时回落到默认样本。
/// 样本仍由真实 Capture Loop 盖章 sequence/revision 并写入唯一 FIFO。
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

impl CaptureSource for ScriptedSource {
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

/// 与生产一致的真实拓扑（唯一 Coordinator + Capture/Processor/Writer + SQLite）。
struct Wiring {
    dir: TempDir,
    coordinator: Arc<CaptureCoordinator>,
    shared: Arc<SharedState>,
    health: Arc<PipelineHealth>,
    source: ScriptedSource,
    utc_clock: Arc<AtomicI64>,
    capture_rx: watch::Receiver<CaptureState>,
    settings_rx: watch::Receiver<Settings>,
    maintenance: MaintenanceControl,
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
    // 与 main.rs 相同：启动恢复负责 runtime 行登记与 agent_restart gap。
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
        dir,
        coordinator: plane.coordinator,
        shared,
        health: plane.health,
        source,
        utc_clock,
        capture_rx,
        settings_rx: plane.settings_rx,
        maintenance: plane.maintenance,
        capture_handle,
        processor_handle,
        writer_handle,
        supervisor_handle,
    }
}

impl Wiring {
    fn db(&self) -> Connection {
        Connection::open(self.dir.path().join("wuji-rebuild-v0.1.db")).unwrap()
    }

    /// (capture_sequence, activity_state, settings_revision)，按 sequence 排序。
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

    fn observation_count_for_app(&self, app_key: &str) -> i64 {
        self.db()
            .query_row(
                "SELECT COUNT(*) FROM foreground_observations o
                 JOIN app_identities a ON a.app_id = o.app_id WHERE a.app_key = ?1",
                [app_key],
                |r| r.get(0),
            )
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

    fn gap_event_count(&self, kind: &str) -> i64 {
        self.db()
            .query_row(
                "SELECT COALESCE(SUM(event_count), 0) FROM capture_gaps WHERE kind = ?1",
                [kind],
                |r| r.get(0),
            )
            .unwrap()
    }

    /// (start_at_utc_ms, end_at_utc_ms, status)，恰好一行的 kind 专用。
    fn gap_row(&self, kind: &str) -> (i64, Option<i64>, String) {
        self.db()
            .query_row(
                "SELECT start_at_utc_ms, end_at_utc_ms, status FROM capture_gaps WHERE kind = ?1",
                [kind],
                |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?)),
            )
            .unwrap()
    }

    fn settings_revisions(&self) -> Vec<i64> {
        let conn = self.db();
        let mut stmt = conn
            .prepare("SELECT revision FROM settings_revisions ORDER BY revision")
            .unwrap();
        stmt.query_map([], |r| r.get(0))
            .unwrap()
            .collect::<rusqlite::Result<Vec<_>>>()
            .unwrap()
    }

    fn settings_revision_count(&self, revision: i64) -> i64 {
        self.db()
            .query_row(
                "SELECT COUNT(*) FROM settings_revisions WHERE revision = ?1",
                [revision],
                |r| r.get(0),
            )
            .unwrap()
    }

    fn max_db_revision(&self) -> i64 {
        self.db()
            .query_row(
                "SELECT COALESCE(MAX(revision), -1) FROM settings_revisions",
                [],
                |r| r.get(0),
            )
            .unwrap()
    }

    /// (status, active_duration_ms, close_reason, end_at_utc_ms)。
    fn work_block_rows(&self) -> Vec<(String, i64, Option<String>, i64)> {
        let conn = self.db();
        let mut stmt = conn
            .prepare(
                "SELECT status, active_duration_ms, close_reason, end_at_utc_ms
                 FROM work_blocks",
            )
            .unwrap();
        stmt.query_map([], |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?, r.get(3)?)))
            .unwrap()
            .collect::<rusqlite::Result<Vec<_>>>()
            .unwrap()
    }

    /// 字节级隐私证据：进程名不得出现在 DB/WAL 中。
    fn assert_db_bytes_lack(&self, needle: &str) {
        let db_bytes = std::fs::read(self.dir.path().join("wuji-rebuild-v0.1.db")).unwrap();
        let wal_path = self
            .dir
            .path()
            .join("wuji-rebuild-v0.1.db")
            .with_extension("db-wal");
        let wal_bytes = std::fs::read(&wal_path).unwrap_or_default();
        for bytes in [&db_bytes, &wal_bytes] {
            assert!(
                !bytes.windows(needle.len()).any(|w| w == needle.as_bytes()),
                "DB/WAL 字节中不得出现 {needle}"
            );
        }
    }

    /// 生产一条样本：UTC 时钟与 paused 时钟同步步进（引擎 skew 检查为零），
    /// 然后 yield 驱动 Capture→Processor→Writer（数据通路无定时器）。
    async fn produce_sample(&self) {
        self.produce_sample_step(SAMPLE_STEP_MS).await;
    }

    async fn produce_sample_step(&self, step_ms: i64) {
        self.utc_clock.fetch_add(step_ms, Ordering::AcqRel);
        tokio::time::advance(Duration::from_millis(step_ms as u64)).await;
        for _ in 0..5 {
            tokio::task::yield_now().await;
        }
    }

    /// yield 驱动的 rendezvous（数据/控制通路无定时器时使用，不产生额外样本）。
    async fn yield_until(&self, what: &str, mut condition: impl FnMut() -> bool) {
        for _ in 0..20_000 {
            if condition() {
                return;
            }
            tokio::task::yield_now().await;
        }
        panic!("rendezvous 超时: {what}");
    }

    /// advance 驱动的 rendezvous（Writer drain 的 10ms sleep、超时类 deadline
    /// 需要推进时钟）。只能在采集已冻结或不会采样的窗口使用。
    async fn advance_until(&self, what: &str, mut condition: impl FnMut() -> bool) {
        for _ in 0..2_000 {
            if condition() {
                return;
            }
            tokio::task::yield_now().await;
            tokio::time::advance(Duration::from_millis(10)).await;
        }
        panic!("rendezvous 超时: {what}");
    }

    async fn capture_start(&self) {
        let result = self
            .coordinator
            .apply_capture_command("capture_start", T0)
            .await;
        assert_eq!(result, Ok(CaptureState::Running), "capture_start 必须成功");
    }

    /// 经唯一 Coordinator 应用 Settings。transition 冻结与锁获取之间没有
    /// 挂起点（lock→sync_external→begin_transition→publish 全部同步），
    /// 因此"锁被持有"等价于"冻结已发布"；此后推进时钟不可能产生
    /// transition 期间样本（gate 已关闭）。若 apply 在首个 yield 内原子
    /// 完成（无计时器阻塞的常见路径），同样安全——此前未推进任何时钟。
    async fn apply_settings(&self, settings: Settings) -> Result<i64, wuji_core::error::SafeError> {
        let at = self.utc_clock.load(Ordering::Acquire);
        let apply = tokio::spawn({
            let coordinator = self.coordinator.clone();
            async move { coordinator.apply_settings(settings, at).await }
        });
        self.yield_until("apply 进入 transition 或已完成", || {
            apply.is_finished()
                || self
                    .coordinator
                    .try_acquire_transition_lock_for_test()
                    .is_err()
        })
        .await;
        self.advance_until("settings apply 完成", || apply.is_finished())
            .await;
        apply.await.expect("apply task 不 panic")
    }

    /// 受控关闭：supervisor 先停（与生产一致），Writer drain+终态提交后返回
    /// (Writer, Engine)，用于直接断言 Engine 最终 revision。
    async fn shutdown(self) -> (Writer, ActivityEngine) {
        self.supervisor_handle.abort();
        self.maintenance.shutdown().await.expect("Writer shutdown");
        let parts = self.writer_handle.await.expect("writer task 不 panic");
        self.capture_handle.abort();
        self.processor_handle.abort();
        parts
    }
}

/// E01：revision 0→1——Barrier 前输出/DB 为 rev 0；Barrier 后首条及以后为
/// rev 1；无旧 revision 穿越；DB/Engine/SharedState/settings watch/DTO 五方一致。
#[tokio::test(start_paused = true)]
async fn e01_revision_boundary_zero_to_one() {
    let w = wiring(Settings {
        sampling_interval_seconds: 1,
        ..Settings::default()
    });
    w.capture_start().await;
    w.produce_sample().await;
    w.produce_sample().await;
    w.yield_until("前 2 条 rev-0 Observation 落库", || {
        w.observation_rows().len() == 2
    })
    .await;
    assert_eq!(
        w.observation_rows(),
        vec![(1, "active".to_string(), 0), (2, "active".to_string(), 0)],
        "Barrier 前全部数据必须保持旧 revision"
    );

    let rev1 = Settings {
        revision: "1".to_string(),
        sampling_interval_seconds: 1,
        ..Settings::default()
    };
    let applied = w.apply_settings(rev1).await.expect("apply 必须成功");
    assert_eq!(applied, 1);
    // 边界时刻五方一致：SharedState applied / settings watch / DTO / DB / 采集恢复。
    assert_eq!(w.shared.applied_settings_revision(), 1);
    assert_eq!(w.settings_rx.borrow().revision, "1");
    assert_eq!(*w.capture_rx.borrow(), CaptureState::Running);
    assert_eq!(w.shared.status_dto().capture_state, CaptureState::Running);
    assert_eq!(w.coordinator.effective_state(), CaptureState::Running);
    assert!(w.shared.errors().is_empty(), "正常边界不得产生诊断");
    // Barrier 前数据不得被重标。
    assert_eq!(w.observation_rows().len(), 2);

    w.produce_sample().await;
    w.produce_sample().await;
    w.yield_until("后 2 条 rev-1 Observation 落库", || {
        w.observation_rows().len() == 4
    })
    .await;
    assert_eq!(
        w.observation_rows(),
        vec![
            (1, "active".to_string(), 0),
            (2, "active".to_string(), 0),
            (3, "active".to_string(), 1),
            (4, "active".to_string(), 1)
        ],
        "Barrier 后首条及以后必须是新 revision，旧 revision 不得穿越"
    );
    assert_eq!(w.settings_revisions(), vec![0, 1]);

    let (_writer, engine) = w.shutdown().await;
    assert_eq!(
        engine.settings_revision(),
        1,
        "Engine revision 必须与 DB/SharedState/watch 一致"
    );
}

/// E02：excludedProcessNames 添加——同进程名在边界前是 Observation(rev 0)，
/// 边界后切换为 PrivacyExcluded(rev 1)，且不再产生该 App 的 Observation。
#[tokio::test(start_paused = true)]
async fn e02_excluded_add_switches_observation_to_privacy_excluded() {
    let w = wiring(Settings {
        sampling_interval_seconds: 1,
        ..Settings::default()
    });
    w.source.push(sample("keepass.exe", 0));
    w.capture_start().await;
    w.produce_sample().await;
    w.yield_until("边界前 keepass Observation 落库", || {
        w.observation_rows().len() == 1
    })
    .await;
    let keepass_key = wuji_core::pipeline::app_key_for("keepass.exe");
    assert_eq!(w.observation_count_for_app(&keepass_key), 1);

    let rev1 = Settings {
        revision: "1".to_string(),
        sampling_interval_seconds: 1,
        excluded_process_names: vec!["keepass.exe".to_string()],
        ..Settings::default()
    };
    w.apply_settings(rev1).await.expect("apply 必须成功");

    w.source.push(sample("keepass.exe", 0));
    w.produce_sample().await;
    w.yield_until("privacy_excluded gap 打开", || {
        w.gap_count("privacy_excluded") == 1
    })
    .await;
    // 边界后同一进程名不再产生 Observation（seq 2 无 Observation 行）。
    assert_eq!(w.observation_rows().len(), 1);
    assert_eq!(
        w.observation_count_for_app(&keepass_key),
        1,
        "边界后不得新增该 App 的 Observation"
    );
    let (start, end, status) = w.gap_row("privacy_excluded");
    assert_eq!(start, T0 + 2 * SAMPLE_STEP_MS, "gap 起点为边界后样本时刻");
    assert_eq!(end, None, "gap 保持 open");
    assert_eq!(status, "open");
    // PrivacyExcluded(rev 1) 通过统一防线校验：无诊断、无 fatal。
    assert!(w.shared.errors().is_empty());
    assert_eq!(*w.capture_rx.borrow(), CaptureState::Running);
    assert_eq!(w.shared.writer_state(), WriterState::Healthy);
    w.shutdown().await;
}

/// E03：excludedProcessNames 移除——边界前是 PrivacyExcluded(rev 0)（进程名
/// 不得泄露到 DB/WAL 字节），边界后恢复 Observation(rev 1) 并关闭隐私 gap。
#[tokio::test(start_paused = true)]
async fn e03_excluded_remove_restores_observation_without_privacy_leak() {
    let w = wiring(Settings {
        sampling_interval_seconds: 1,
        excluded_process_names: vec!["keepass.exe".to_string()],
        ..Settings::default()
    });
    w.source.push(sample("keepass.exe", 0));
    w.capture_start().await;
    w.produce_sample().await;
    w.yield_until("privacy_excluded gap 打开", || {
        w.gap_count("privacy_excluded") == 1
    })
    .await;
    assert_eq!(w.observation_rows().len(), 0);
    // 隐私证据：排除期间进程名不得出现在 DB/WAL 字节中。
    w.assert_db_bytes_lack("keepass.exe");

    let rev1 = Settings {
        revision: "1".to_string(),
        sampling_interval_seconds: 1,
        ..Settings::default()
    };
    w.apply_settings(rev1).await.expect("apply 必须成功");

    w.source.push(sample("keepass.exe", 0));
    w.produce_sample().await;
    w.yield_until("边界后 Observation 落库", || {
        w.observation_rows().len() == 1
    })
    .await;
    assert_eq!(
        w.observation_rows(),
        vec![(2, "active".to_string(), 1)],
        "边界后恢复 Observation 且携带新 revision"
    );
    let (_, end, status) = w.gap_row("privacy_excluded");
    assert_eq!(status, "closed", "首个有效 Observation 必须关闭隐私 gap");
    assert!(end.is_some());
    assert!(w.shared.errors().is_empty());
    w.shutdown().await;
}

/// E04：idle threshold 改变——同一 idle reading（120s）在边界两侧按各自
/// Settings 分类：旧阈值 300 → active(rev 0)；新阈值 60 → idle(rev 1)。
#[tokio::test(start_paused = true)]
async fn e04_idle_threshold_change_reclassifies_same_reading() {
    let w = wiring(Settings {
        sampling_interval_seconds: 1,
        idle_threshold_seconds: 300,
        ..Settings::default()
    });
    for _ in 0..2 {
        w.source.push(sample("code.exe", 120));
    }
    w.capture_start().await;
    w.produce_sample().await;
    w.produce_sample().await;
    w.yield_until("边界前 2 条 Observation 落库", || {
        w.observation_rows().len() == 2
    })
    .await;
    assert_eq!(
        w.observation_rows(),
        vec![(1, "active".to_string(), 0), (2, "active".to_string(), 0)],
        "旧阈值 300：reading 120 必须分类 active"
    );

    let rev1 = Settings {
        revision: "1".to_string(),
        sampling_interval_seconds: 1,
        idle_threshold_seconds: 60,
        ..Settings::default()
    };
    w.apply_settings(rev1).await.expect("apply 必须成功");

    for _ in 0..2 {
        w.source.push(sample("code.exe", 120));
    }
    w.produce_sample().await;
    w.produce_sample().await;
    w.yield_until("边界后 2 条 Observation 落库", || {
        w.observation_rows().len() == 4
    })
    .await;
    assert_eq!(
        w.observation_rows(),
        vec![
            (1, "active".to_string(), 0),
            (2, "active".to_string(), 0),
            (3, "idle".to_string(), 1),
            (4, "idle".to_string(), 1)
        ],
        "新阈值 60：同一 reading 120 必须分类 idle 且携带新 revision"
    );
    assert!(w.shared.errors().is_empty());
    w.shutdown().await;
}

/// E05：work-break threshold 改变——边界前旧阈值（300s）下 pending idle 20s
/// 不触发 idle_break（work block 保持 open）；边界后首个 idle 事件采用新
/// 阈值（15s），pending 30s ≥ 15s 触发 idle_break 并回溯到 idle 起点。
/// 若新阈值提前泄漏到边界前，pending 20s 在边界前就会断块——两侧分别断言。
#[tokio::test(start_paused = true)]
async fn e05_work_break_threshold_applies_only_after_boundary() {
    let w = wiring(Settings {
        // sampling interval 与步长相等（10s）：一次 advance 恰好一条样本，
        // gap cap 随之为 30s（10s 归属间隔安全）。
        sampling_interval_seconds: 10,
        idle_threshold_seconds: 60,
        work_break_idle_seconds: 300,
        ..Settings::default()
    });
    for _ in 0..3 {
        w.source.push(sample("code.exe", 0));
    }
    for _ in 0..3 {
        w.source.push(sample("code.exe", 120));
    }
    w.capture_start().await;
    // 10s 步长：Active ×3（T0+10/20/30s）→ Idle ×3（+40/50/60s），
    // pending idle 累计 20s（旧阈值 300s 不触发）。
    for _ in 0..6 {
        w.produce_sample_step(10_000).await;
    }
    w.yield_until("边界前 6 条 Observation 落库", || {
        w.observation_rows().len() == 6
    })
    .await;
    let before = w.work_block_rows();
    assert_eq!(before.len(), 1, "应恰好一个 work block");
    assert_eq!(
        before[0].0, "open",
        "边界前旧阈值（300s）下 pending 20s 不得触发 idle_break"
    );
    assert_eq!(before[0].1, 20_000, "active 归属 2×10s");

    let rev1 = Settings {
        revision: "1".to_string(),
        sampling_interval_seconds: 10,
        idle_threshold_seconds: 60,
        work_break_idle_seconds: 15,
        ..Settings::default()
    };
    w.apply_settings(rev1).await.expect("apply 必须成功");

    w.source.push(sample("code.exe", 120));
    w.produce_sample_step(10_000).await;
    w.yield_until("边界后第 7 条 Observation 落库", || {
        w.observation_rows().len() == 7
    })
    .await;
    let after = w.work_block_rows();
    assert_eq!(after.len(), 1);
    assert_eq!(after[0].0, "closed", "边界后新阈值（15s）必须触发断块");
    assert_eq!(after[0].2.as_deref(), Some("idle_break"));
    assert_eq!(
        after[0].3,
        T0 + 40_000,
        "idle_break 必须回溯结束于 idle 起点（09 §6.6）"
    );
    assert_eq!(
        w.observation_rows()[6],
        (7, "idle".to_string(), 1),
        "触发断块的边界后事件必须携带新 revision"
    );
    assert!(w.shared.errors().is_empty());
    w.shutdown().await;
}

/// E06：CaptureError 边界——前后两侧 CaptureError 各自携带并通过 revision
/// 校验（负向错配拒绝证据见 E08）。同 kind gap 事件累计，无诊断、无 fatal。
#[tokio::test(start_paused = true)]
async fn e06_capture_error_carries_and_passes_revision_on_both_sides() {
    let w = wiring(Settings {
        sampling_interval_seconds: 1,
        ..Settings::default()
    });
    w.source.push(RawSample {
        process_file_name: None,
        idle: IdleReading::Seconds(0),
    });
    w.capture_start().await;
    w.produce_sample().await;
    w.yield_until("边界前 capture_error gap 打开", || {
        w.gap_count("capture_error") == 1
    })
    .await;
    assert_eq!(w.gap_event_count("capture_error"), 1);
    let (start, _, _) = w.gap_row("capture_error");
    assert_eq!(start, T0 + SAMPLE_STEP_MS);

    let rev1 = Settings {
        revision: "1".to_string(),
        sampling_interval_seconds: 1,
        ..Settings::default()
    };
    w.apply_settings(rev1).await.expect("apply 必须成功");

    w.source.push(RawSample {
        process_file_name: None,
        idle: IdleReading::Seconds(0),
    });
    w.produce_sample().await;
    w.yield_until("边界后 capture_error 事件累计 2", || {
        w.gap_event_count("capture_error") == 2
    })
    .await;
    // 两侧都被接受：若统一防线误拒或漏校验，必产生 SETTINGS_CONFLICT/fatal。
    assert!(w.shared.errors().is_empty());
    assert_eq!(*w.capture_rx.borrow(), CaptureState::Running);
    assert_eq!(w.shared.writer_state(), WriterState::Healthy);
    w.shutdown().await;
}

// ---------------------------------------------------------------------------
// E07：Processor 三类 mismatch（真实拓扑 + 协议违例注入）
// ---------------------------------------------------------------------------

/// E07 故障注入拓扑：真实 Coordinator + Capture Loop + 唯一 FIFO +
/// Processor + Writer + SQLite。与主 fixture 的差别：测试保留 settings /
/// capture watch 发送端克隆，用于模拟"settings watch 无 Barrier 直接前进"
/// 的协议违例（P1-04 原始失败模式）；被注入的只有 watch 失步，样本仍全部
/// 经真实 Capture Loop 与唯一 CapturePipelineItem FIFO 流动。
struct FaultWiring {
    dir: TempDir,
    shared: Arc<SharedState>,
    health: Arc<PipelineHealth>,
    continuity: Arc<ContinuityState>,
    coordinator: Arc<CaptureCoordinator>,
    settings_tx: watch::Sender<Settings>,
    capture_state_tx: watch::Sender<CaptureState>,
    capture_rx: watch::Receiver<CaptureState>,
    settings_rx: watch::Receiver<Settings>,
    source: ScriptedSource,
    pipeline_rx: mpsc::Receiver<CapturePipelineItem>,
    control_rx: mpsc::Receiver<WriterControl>,
    exit_rx: mpsc::UnboundedReceiver<PipelineTask>,
    writer: Writer,
    engine: ActivityEngine,
    capture_handle: tokio::task::JoinHandle<()>,
}

fn fault_wiring(settings: Settings) -> FaultWiring {
    let dir = TempDir::new().unwrap();
    let db_path = dir.path().join("wuji-rebuild-v0.1.db");
    Writer::bootstrap_with_timezone(&db_path, SHANGHAI, T0).unwrap();
    let continuity = Arc::new(ContinuityState::default());
    let runtime_id = RuntimeId::new();
    {
        // 直接登记 runtime（不做 startup recovery）：初始零 gap，
        // 便于"零 SQLite 副作用"断言。
        let mut w = Writer::open_existing(&db_path).unwrap();
        let tx = w.transaction().unwrap();
        tx.insert_runtime(&runtime_id, T0).unwrap();
        tx.commit().unwrap();
    }
    let writer = Writer::open_existing(&db_path).unwrap();
    let engine =
        ActivityEngine::new(runtime_id.clone(), settings.clone(), continuity.clone()).unwrap();
    let shared = Arc::new(SharedState::new("0.1.0".to_string(), runtime_id));
    let (health, exit_rx) = PipelineHealth::with_exit_events();
    let (barrier_tx, barrier_rx) = wuji_rebuild_agent::barrier::barrier_request_channel(64);
    let (capture_state_tx, capture_state_rx) = watch::channel(CaptureState::Stopped);
    let (control_tx, control_rx) = mpsc::channel(64);
    let (settings_tx, settings_rx) = watch::channel(settings.clone());
    let coordinator = Arc::new(CaptureCoordinator::new(
        barrier_tx,
        capture_state_tx.clone(),
        control_tx,
        shared.clone(),
        settings_tx.clone(),
        CaptureState::Stopped,
        health.clone(),
    ));
    let capture_rx = capture_state_tx.subscribe();
    let source = ScriptedSource::new(sample("code.exe", 0));
    let (pipeline_rx, capture_handle) = spawn_capture_loop(
        source.clone(),
        settings_rx.clone(),
        capture_state_rx,
        continuity.clone(),
        CaptureLoopConfig {
            wake_interval: Duration::from_millis(50),
            queue_capacity: 64,
            offload_capture: false,
            ..CaptureLoopConfig::default()
        },
        barrier_rx,
        &health,
    );
    FaultWiring {
        dir,
        shared,
        health,
        continuity,
        coordinator,
        settings_tx,
        capture_state_tx,
        capture_rx,
        settings_rx,
        source,
        pipeline_rx,
        control_rx,
        exit_rx,
        writer,
        engine,
        capture_handle,
    }
}

/// E07 公共驱动：backlog[0] 是被注入样本（rev 0），watch 随后无 Barrier
/// 前进到 rev 1；Processor 必须在业务处理前拒绝并显式失败。
async fn run_processor_mismatch_case(
    initial: Settings,
    flipped: Settings,
    injected: RawSample,
    extra_db_assert: impl Fn(&TempDir),
) {
    let w = fault_wiring(initial);
    // 违例注入 ①：capture 直接发布 Running（不经 Coordinator transition；
    // 本用例不测试 transition 语义，fencing 断言只依赖 SharedState fatal）。
    w.capture_state_tx.send_replace(CaptureState::Running);
    w.source.push(injected);
    w.source.push(sample("code.exe", 0));
    w.source.push(sample("code.exe", 0));
    for _ in 0..3 {
        tokio::time::advance(Duration::from_millis(3_000)).await;
        for _ in 0..3 {
            tokio::task::yield_now().await;
        }
    }
    // rendezvous：3 条 rev-0 backlog 真实积压进唯一 FIFO（Processor 尚未启动）。
    yield_wait("rev-0 backlog 积压", || {
        w.continuity.capture_queue_depth() == 3
    })
    .await;

    // 违例注入 ②：settings watch 无 Barrier 直接前进（P1-04 原始失败模式）。
    w.settings_tx.send_replace(flipped);

    // 现在才启动 Processor（borrow 到 rev 1）与真实 Writer + supervisor。
    let (processor_rx, processor_handle) = spawn_observation_processor(
        w.pipeline_rx,
        w.settings_rx.clone(),
        w.continuity.clone(),
        &w.health,
    );
    let writer_task = WriterTask::new(
        w.writer,
        w.engine,
        w.shared.clone(),
        w.capture_state_tx.clone(),
        w.continuity.clone(),
        w.dir.path().join("config"),
        w.health.clone(),
    );
    let writer_handle = tokio::spawn(writer_task.into_run_future(processor_rx, w.control_rx));
    let supervisor_handle =
        tokio::spawn(supervise_pipeline_exits(w.exit_rx, w.coordinator.clone()));

    // Processor 在业务处理前拒绝首条 backlog：唯一违例消息 → Writer 统一
    // fatal；supervisor 对 Processor 退出兜底（同一 fail-closed 终态）。
    yield_wait("Writer fail-closed", || {
        w.shared.writer_state() == WriterState::Faulted
            && w.shared.process_state() == ProcessState::Faulted
    })
    .await;
    // 来源明确的 SETTINGS_CONFLICT；supervisor latch 不得覆盖更精确诊断。
    yield_wait("SETTINGS_CONFLICT 诊断", || {
        w.shared.errors().get(&ErrorSource::Writer) == Some(&SafeErrorCode::SettingsConflict)
    })
    .await;
    assert_eq!(*w.capture_rx.borrow(), CaptureState::Stopped);
    assert_eq!(w.shared.capture_state(), CaptureState::Stopped);
    assert_eq!(w.shared.status_dto().capture_state, CaptureState::Stopped);
    // Processor 显式退出（无静默 drop）；后续 backlog 未被处理
    // （没有后续旧 revision 输出：FIFO 中剩余 2 条原样滞留）。
    yield_wait("Processor 退出", || {
        w.health.processor_state() == TaskLifecycle::Dead
    })
    .await;
    assert_eq!(
        w.continuity.capture_queue_depth(),
        2,
        "违例后的 backlog 绝不得再被处理"
    );
    // 零 ActivityEngine/SQLite 副作用。
    let conn = Connection::open(w.dir.path().join("wuji-rebuild-v0.1.db")).unwrap();
    let observations: i64 = conn
        .query_row("SELECT COUNT(*) FROM foreground_observations", [], |r| {
            r.get(0)
        })
        .unwrap();
    let gaps: i64 = conn
        .query_row("SELECT COUNT(*) FROM capture_gaps", [], |r| r.get(0))
        .unwrap();
    assert_eq!(observations, 0, "违例不得产生 Observation");
    assert_eq!(gaps, 0, "违例不得产生任何 gap");
    drop(conn);
    extra_db_assert(&w.dir);

    // fencing：后续 start/settings/system-event 不得在本进程内绕过。
    let error = w
        .coordinator
        .apply_settings(
            Settings {
                revision: "2".to_string(),
                ..Settings::default()
            },
            T0,
        )
        .await
        .expect_err("违例后 settings 必须被 fencing");
    assert_eq!(error.code, SafeErrorCode::AgentWriterFaulted);
    let error = w
        .coordinator
        .apply_capture_command("capture_start", T0)
        .await
        .expect_err("违例后 start 必须被 fencing");
    assert_eq!(error.code, SafeErrorCode::AgentWriterFaulted);
    let error = w
        .coordinator
        .apply_system_lifecycle_event(SystemLifecycleEvent::Lock { at_utc_ms: T0 })
        .await
        .expect_err("违例后 system-event 必须被 fencing");
    assert_eq!(error.code, SafeErrorCode::AgentWriterFaulted);

    supervisor_handle.abort();
    writer_handle.abort();
    processor_handle.abort();
    w.capture_handle.abort();
}

/// 不依赖具体 fixture 的 yield rendezvous（FaultWiring 使用）。
async fn yield_wait(what: &str, mut condition: impl FnMut() -> bool) {
    for _ in 0..20_000 {
        if condition() {
            return;
        }
        tokio::task::yield_now().await;
    }
    panic!("rendezvous 超时: {what}");
}

/// E07a：Observation 类 mismatch——样本本会产生 Observation，错配后零输出。
#[tokio::test(start_paused = true)]
async fn e07_processor_mismatch_observation_fails_closed() {
    run_processor_mismatch_case(
        Settings {
            sampling_interval_seconds: 1,
            ..Settings::default()
        },
        Settings {
            revision: "1".to_string(),
            sampling_interval_seconds: 1,
            ..Settings::default()
        },
        sample("code.exe", 0),
        |_| {},
    )
    .await;
}

/// E07b：PrivacyExcluded 类 mismatch——旧 Settings 排除 keepass；错配后
/// 既不产生 Observation 也不产生 privacy_excluded gap，进程名不得落库。
#[tokio::test(start_paused = true)]
async fn e07_processor_mismatch_privacy_excluded_fails_closed() {
    run_processor_mismatch_case(
        Settings {
            sampling_interval_seconds: 1,
            excluded_process_names: vec!["keepass.exe".to_string()],
            ..Settings::default()
        },
        Settings {
            revision: "1".to_string(),
            sampling_interval_seconds: 1,
            ..Settings::default()
        },
        sample("keepass.exe", 0),
        |dir| {
            let db_bytes = std::fs::read(dir.path().join("wuji-rebuild-v0.1.db")).unwrap();
            assert!(
                !db_bytes
                    .windows("keepass.exe".len())
                    .any(|w| w == "keepass.exe".as_bytes()),
                "违例路径不得把进程名写入 DB"
            );
        },
    )
    .await;
}

/// E07c：CaptureError 类 mismatch——进程名不可得样本，错配后不得产生
/// capture_error gap。
#[tokio::test(start_paused = true)]
async fn e07_processor_mismatch_capture_error_fails_closed() {
    run_processor_mismatch_case(
        Settings {
            sampling_interval_seconds: 1,
            ..Settings::default()
        },
        Settings {
            revision: "1".to_string(),
            sampling_interval_seconds: 1,
            ..Settings::default()
        },
        RawSample {
            process_file_name: None,
            idle: IdleReading::Seconds(0),
        },
        |_| {},
    )
    .await;
}

// ---------------------------------------------------------------------------
// E08：Writer 三类防御性 mismatch（真实 Writer + SQLite；手工构造输出，
// 端到端违例证据见 E07）
// ---------------------------------------------------------------------------

fn obs_with_rev(sequence: u64, utc_ms: i64, revision: i64) -> ProcessorOutput {
    ProcessorOutput::Observation(FilteredObservation {
        sequence,
        continuity_epoch: 0,
        captured_at_utc_ms: utc_ms,
        captured_monotonic_ms: (utc_ms - T0) as u64,
        app_key: format!("proc:{sequence:032x}"),
        display_name: format!("app{sequence}"),
        normalized_process_name: format!("app{sequence}.exe"),
        activity_state: ActivityState::Active,
        quality: CaptureQuality::Normal,
        settings_revision: revision,
    })
}

/// E08：Writer 对 Observation、PrivacyExcluded、CaptureError 使用同一
/// revision 防线：三类错配全部拒绝（零 Engine/SQLite 副作用），留下
/// SETTINGS_CONFLICT 并 fail-closed 锁存；锁存后即使 revision 匹配的
/// 消息也一律拒绝；Engine revision 保持不变（不重标、不回退）。
#[tokio::test(start_paused = true)]
async fn e08_writer_defensive_mismatch_rejects_all_three_variants() {
    let dir = TempDir::new().unwrap();
    let db_path = dir.path().join("wuji-rebuild-v0.1.db");
    Writer::bootstrap_with_timezone(&db_path, SHANGHAI, T0).unwrap();
    let continuity = Arc::new(ContinuityState::default());
    let runtime_id = RuntimeId::new();
    {
        let mut w = Writer::open_existing(&db_path).unwrap();
        let tx = w.transaction().unwrap();
        tx.insert_runtime(&runtime_id, T0).unwrap();
        tx.commit().unwrap();
    }
    let writer = Writer::open_existing(&db_path).unwrap();
    let engine =
        ActivityEngine::new(runtime_id.clone(), Settings::default(), continuity.clone()).unwrap();
    let shared = Arc::new(SharedState::new("0.1.0".to_string(), runtime_id));
    let (capture_state_tx, _) = watch::channel(CaptureState::Running);
    let capture_rx = capture_state_tx.subscribe();
    let (data_tx, data_rx) = mpsc::channel(8);
    let (control_tx, control_rx) = mpsc::channel(8);
    let task = WriterTask::new(
        writer,
        engine,
        shared.clone(),
        capture_state_tx,
        continuity.clone(),
        dir.path().join("config"),
        PipelineHealth::new(),
    );
    let run = tokio::spawn(async move { task.run(data_rx, control_rx).await });

    // 经真实 Barrier 协议把 Engine 推进到 rev 1（DB 同步持久化）。
    let barrier_id = BarrierId::new();
    data_tx
        .send(ProcessorOutput::Barrier(BarrierToken {
            id: barrier_id.clone(),
            kind: BarrierKind::SettingsApplied,
            expected_revision: 0,
        }))
        .await
        .unwrap();
    let (ack_tx, ack_rx) = tokio::sync::oneshot::channel();
    control_tx
        .send(WriterControl::SettingsApplied {
            settings: Settings {
                revision: "1".to_string(),
                ..Settings::default()
            },
            at_utc_ms: T0,
            barrier_id,
            expected_revision: 0,
            ack: ack_tx,
        })
        .await
        .unwrap();
    ack_rx.await.expect("ack").expect("revision 1 applied");

    // 基线副作用：一条合法 rev-1 Observation 正常落库。
    data_tx.send(obs_with_rev(1, T0 + 3_000, 1)).await.unwrap();
    // 三类错配（rev 0 vs Engine rev 1）。
    data_tx.send(obs_with_rev(2, T0 + 6_000, 0)).await.unwrap();
    data_tx
        .send(ProcessorOutput::PrivacyExcluded {
            sequence: 3,
            continuity_epoch: 0,
            captured_at_utc_ms: T0 + 9_000,
            settings_revision: 0,
        })
        .await
        .unwrap();
    data_tx
        .send(ProcessorOutput::CaptureError {
            sequence: 4,
            continuity_epoch: 0,
            captured_at_utc_ms: T0 + 12_000,
            settings_revision: 0,
        })
        .await
        .unwrap();
    // 锁存后即使 revision 匹配的消息也必须被拒绝。
    data_tx.send(obs_with_rev(5, T0 + 15_000, 1)).await.unwrap();

    for _ in 0..20_000 {
        if shared.writer_state() == WriterState::Faulted {
            break;
        }
        tokio::task::yield_now().await;
    }
    assert_eq!(shared.writer_state(), WriterState::Faulted);
    for _ in 0..20_000 {
        if shared.process_state() == ProcessState::Faulted {
            break;
        }
        tokio::task::yield_now().await;
    }

    let conn = Connection::open(&db_path).unwrap();
    let observations: Vec<(i64, i64)> = {
        let mut stmt = conn
            .prepare(
                "SELECT capture_sequence, settings_revision FROM foreground_observations
                 ORDER BY capture_sequence",
            )
            .unwrap();
        stmt.query_map([], |r| Ok((r.get(0)?, r.get(1)?)))
            .unwrap()
            .collect::<rusqlite::Result<Vec<_>>>()
            .unwrap()
    };
    assert_eq!(
        observations,
        vec![(1, 1)],
        "错配与锁存后的消息都不得落库；基线 Observation 保持原 revision（不重标）"
    );
    let gaps: i64 = conn
        .query_row("SELECT COUNT(*) FROM capture_gaps", [], |r| r.get(0))
        .unwrap();
    assert_eq!(
        gaps, 0,
        "PrivacyExcluded/CaptureError 错配不得产生任何 gap（零副作用）"
    );
    drop(conn);
    assert_eq!(
        shared.errors().get(&ErrorSource::Writer),
        Some(&SafeErrorCode::SettingsConflict),
        "三类错配必须留下 Writer 来源的 SETTINGS_CONFLICT"
    );
    assert_eq!(shared.process_state(), ProcessState::Faulted);
    assert_eq!(*capture_rx.borrow(), CaptureState::Stopped);
    assert_eq!(shared.capture_state(), CaptureState::Stopped);
    assert_eq!(shared.status_dto().capture_state, CaptureState::Stopped);

    drop(control_tx);
    drop(data_tx);
    let (_writer, engine) = run.await.expect("writer task 不 panic");
    assert_eq!(
        engine.settings_revision(),
        1,
        "Engine revision 不得因错配改变（不重标、不回退）"
    );
}

// ---------------------------------------------------------------------------
// E09/E10：commit 失败 last-known-good 与修复后重试
// ---------------------------------------------------------------------------

/// E09：commit 明确失败（备份目录不可写 → crash-consistent 协议在 DB 提交
/// 前失败）——DB/Engine/SharedState applied/settings watch/DTO 全部保持
/// last-known-good；排除名单不被清空；采集按旧 Settings 恢复 Running。
#[tokio::test(start_paused = true)]
async fn e09_commit_failure_keeps_last_known_good() {
    let w = wiring(Settings {
        sampling_interval_seconds: 1,
        excluded_process_names: vec!["keepass.exe".to_string()],
        ..Settings::default()
    });
    // 备份目录阻塞：config 路径是已存在的文件（create_dir_all 确定性失败）。
    std::fs::write(w.dir.path().join("config"), b"not a directory").unwrap();
    w.source.push(sample("keepass.exe", 0));
    w.capture_start().await;
    w.produce_sample().await;
    w.yield_until("旧排除名单生效", || {
        w.gap_count("privacy_excluded") == 1
    })
    .await;

    let rev1 = Settings {
        revision: "1".to_string(),
        sampling_interval_seconds: 1,
        ..Settings::default()
    };
    let error = w
        .apply_settings(rev1)
        .await
        .expect_err("commit 失败必须返回错误");
    assert_eq!(error.code, SafeErrorCode::SettingsSavedNotApplied);

    // 五方保持 last-known-good。
    assert_eq!(w.max_db_revision(), 0, "DB revision 不得前进");
    assert_eq!(w.shared.applied_settings_revision(), 0);
    assert_eq!(w.settings_rx.borrow().revision, "0");
    assert_eq!(*w.capture_rx.borrow(), CaptureState::Running);
    assert_eq!(w.shared.status_dto().capture_state, CaptureState::Running);
    assert_eq!(
        w.shared.errors().get(&ErrorSource::Settings),
        Some(&SafeErrorCode::SettingsSavedNotApplied),
        "失败必须留下 Settings 来源的精确诊断"
    );

    // 排除名单不被清空：同一进程继续 PrivacyExcluded（旧 Settings 语义）。
    w.source.push(sample("keepass.exe", 0));
    w.produce_sample().await;
    w.yield_until("privacy_excluded 事件累计 2", || {
        w.gap_event_count("privacy_excluded") == 2
    })
    .await;
    assert_eq!(
        w.observation_rows().len(),
        0,
        "排除名单不得因 commit 失败被清空"
    );
    w.assert_db_bytes_lack("keepass.exe");

    let (_writer, engine) = w.shutdown().await;
    assert_eq!(engine.settings_revision(), 0, "Engine 必须保持旧 revision");
}

/// E10：故障修复后重试——同一目标 revision 恰好提交一次；边界前数据不被
/// 重新解释或重标；边界后样本携带新 revision。
#[tokio::test(start_paused = true)]
async fn e10_retry_after_repair_commits_exactly_once() {
    let w = wiring(Settings {
        sampling_interval_seconds: 1,
        ..Settings::default()
    });
    std::fs::write(w.dir.path().join("config"), b"not a directory").unwrap();
    w.capture_start().await;
    w.produce_sample().await;
    w.yield_until("边界前 Observation 落库", || {
        w.observation_rows().len() == 1
    })
    .await;

    let rev1 = Settings {
        revision: "1".to_string(),
        sampling_interval_seconds: 1,
        ..Settings::default()
    };
    let error = w
        .apply_settings(rev1.clone())
        .await
        .expect_err("第一次必须失败");
    assert_eq!(error.code, SafeErrorCode::SettingsSavedNotApplied);
    assert_eq!(w.max_db_revision(), 0);

    // 修复故障（备份目录恢复可写），同一目标 revision 重试。
    std::fs::remove_file(w.dir.path().join("config")).unwrap();
    let applied = w.apply_settings(rev1).await.expect("修复后重试必须成功");
    assert_eq!(applied, 1);
    // 恰好提交一次，无重复边界。
    assert_eq!(w.settings_revision_count(1), 1);
    assert_eq!(w.settings_revisions(), vec![0, 1]);
    assert_eq!(w.shared.applied_settings_revision(), 1);
    assert_eq!(w.settings_rx.borrow().revision, "1");
    // 边界前数据未被重新解释/重标。
    assert_eq!(
        w.observation_rows(),
        vec![(1, "active".to_string(), 0)],
        "失败重试不得重新解释或重标边界前数据"
    );

    w.produce_sample().await;
    w.yield_until("边界后 Observation 落库", || {
        w.observation_rows().len() == 2
    })
    .await;
    assert_eq!(
        w.observation_rows()[1],
        (2, "active".to_string(), 1),
        "边界后样本携带新 revision"
    );
    assert!(w.shared.errors().is_empty(), "成功后 Settings 诊断必须清除");
    w.shutdown().await;
}

// ---------------------------------------------------------------------------
// E11：消费者退出——未提交与已提交两类语义严格区分
// ---------------------------------------------------------------------------

/// E11a：Processor 退出 → supervisor 经同一 transition lock fail-closed →
/// 后续 settings 在副作用前被 fencing——未提交语义：DB/applied/watch 保持
/// rev 0、零 revision 移动；安全停止，不得虚假 Running。
/// （Barrier 注入失败→未提交→last-known-good 路径由
/// `capture_coordinator::settings_injection_failure_keeps_last_known_good`
/// 在 Coordinator 层覆盖。）
#[tokio::test(start_paused = true)]
async fn e11a_post_fault_settings_are_fenced_uncommitted() {
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

    // Processor 真实退出；supervisor 经同一 transition lock 锁存 fault
    // （结果 rendezvous，与锁获取顺序无关）。
    w.processor_handle.abort();
    w.yield_until("Processor 退出且 fault 锁存", || {
        w.health.processor_state() == TaskLifecycle::Dead
            && w.shared.process_state() == ProcessState::Faulted
    })
    .await;
    // settings 尝试被 fencing：副作用前拒绝，稳定 AGENT_WRITER_FAULTED。
    let error = w
        .coordinator
        .apply_settings(
            Settings {
                revision: "1".to_string(),
                sampling_interval_seconds: 1,
                ..Settings::default()
            },
            w.utc_clock.load(Ordering::Acquire),
        )
        .await
        .expect_err("fault 后 settings 必须被 fencing");
    assert_eq!(error.code, SafeErrorCode::AgentWriterFaulted);

    // 未提交：DB/SharedState applied/settings watch 全部保持 rev 0。
    assert_eq!(w.max_db_revision(), 0);
    assert_eq!(w.shared.applied_settings_revision(), 0);
    assert_eq!(w.settings_rx.borrow().revision, "0");
    // 安全停止（supervisor 经同一 transition lock 锁存），不得虚假 Running。
    w.yield_until("writer_fault 锁存", || {
        w.shared.process_state() == ProcessState::Faulted
    })
    .await;
    assert_eq!(w.health.processor_state(), TaskLifecycle::Dead);
    assert_eq!(*w.capture_rx.borrow(), CaptureState::Stopped);
    assert_eq!(w.shared.capture_state(), CaptureState::Stopped);
    assert_eq!(w.shared.status_dto().capture_state, CaptureState::Stopped);
    // fencing：后续 settings 不得绕过。
    let fenced = w
        .coordinator
        .apply_settings(
            Settings {
                revision: "2".to_string(),
                ..Settings::default()
            },
            T0,
        )
        .await
        .expect_err("fault 后 settings 必须被 fencing");
    assert_eq!(fenced.code, SafeErrorCode::AgentWriterFaulted);

    let (_writer, engine) = w.shutdown().await;
    assert_eq!(
        engine.settings_revision(),
        0,
        "未提交：Engine 必须保持旧 revision"
    );
}

/// E11b：settings 已提交后 Processor 退出——已提交语义：DB/applied/watch
/// 保留 rev 1 不回滚；supervisor fail-closed；不得虚假 Running；后续
/// start/settings/system-event 全部被 fencing。
#[tokio::test(start_paused = true)]
async fn e11_consumer_exit_after_commit_keeps_committed_and_fences() {
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
    let applied = w
        .apply_settings(Settings {
            revision: "1".to_string(),
            sampling_interval_seconds: 1,
            ..Settings::default()
        })
        .await
        .expect("apply 必须成功");
    assert_eq!(applied, 1);
    assert_eq!(*w.capture_rx.borrow(), CaptureState::Running);

    w.processor_handle.abort();
    w.yield_until("fault 锁存", || {
        w.shared.process_state() == ProcessState::Faulted
    })
    .await;
    assert_eq!(w.health.processor_state(), TaskLifecycle::Dead);
    // 已提交事实保留：DB/applied/watch 不回滚。
    assert_eq!(w.max_db_revision(), 1, "已提交 revision 不得回滚");
    assert_eq!(w.shared.applied_settings_revision(), 1);
    assert_eq!(w.settings_rx.borrow().revision, "1");
    // 安全停止，不得虚假 Running。
    assert_eq!(*w.capture_rx.borrow(), CaptureState::Stopped);
    assert_eq!(w.shared.capture_state(), CaptureState::Stopped);
    assert_eq!(w.shared.status_dto().capture_state, CaptureState::Stopped);
    // fencing：三类后续 transition 全部拒绝。
    let fenced = w
        .coordinator
        .apply_capture_command("capture_start", T0)
        .await
        .expect_err("fault 后 start 必须被 fencing");
    assert_eq!(fenced.code, SafeErrorCode::AgentWriterFaulted);
    let fenced = w
        .coordinator
        .apply_settings(
            Settings {
                revision: "2".to_string(),
                ..Settings::default()
            },
            T0,
        )
        .await
        .expect_err("fault 后 settings 必须被 fencing");
    assert_eq!(fenced.code, SafeErrorCode::AgentWriterFaulted);
    let fenced = w
        .coordinator
        .apply_system_lifecycle_event(SystemLifecycleEvent::Sleep { at_utc_ms: T0 })
        .await
        .expect_err("fault 后 system-event 必须被 fencing");
    assert_eq!(fenced.code, SafeErrorCode::AgentWriterFaulted);

    let (_writer, engine) = w.shutdown().await;
    assert_eq!(engine.settings_revision(), 1, "Engine 保留已提交 revision");
}

// ---------------------------------------------------------------------------
// E12：IPC settings_reload × reconciler 真实重叠 + 单一有效提交 + effectivity
// ---------------------------------------------------------------------------

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

/// E12：IPC settings_reload 与 reconciler 真实并发重叠（唯一 Coordinator
/// 串行化），并发目标 revision 只产生一个有效提交；边界前后 Observation
/// revision 各自正确；IPC 响应 DTO、DB、Engine、SharedState、settings
/// watch 五方一致。
#[tokio::test(start_paused = true)]
async fn e12_ipc_reload_and_reconciler_single_commit_with_effectivity() {
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

    let settings_path = w.dir.path().join("settings.json");
    let rev1 = Settings {
        revision: "1".to_string(),
        sampling_interval_seconds: 1,
        ..Settings::default()
    };
    std::fs::write(&settings_path, rev1.canonical_json()).unwrap();
    let (shutdown_tx, _shutdown_rx) = watch::channel(false);
    let context = Arc::new(CommandServerContext {
        shared: w.shared.clone(),
        coordinator: w.coordinator.clone(),
        settings_path: settings_path.clone(),
        settings_digest_for: |settings: &Settings| settings.content_digest(),
        shutdown_tx,
        channel: "rebuild-v01-test-effectivity-e12".to_string(),
    });
    let request_ids = Arc::new(Mutex::new(RequestIdCache::new("0.1.0".to_string())));

    // 钉住唯一 transition lock，制造真实重叠：IPC 已进入 dispatch（Active）
    // 且 reconciler 已发出 attempt，二者同时排队在同一锁上。
    let gate = w.coordinator.acquire_transition_lock_for_test().await;
    let line = envelope(
        &ulid(),
        "settings_reload",
        serde_json::json!({
            "savedRevision": "1",
            "contentDigest": rev1.content_digest(),
        }),
    );
    let ipc = tokio::spawn({
        let context = context.clone();
        let request_ids = request_ids.clone();
        async move { handle_request_line(&line, &context, &request_ids).await }
    });
    w.yield_until("IPC dispatch 进入 Active", || {
        request_ids.lock().unwrap().active_count() == 1
    })
    .await;
    let (attempt_tx, mut attempt_rx) = mpsc::unbounded_channel();
    let reconciler = tokio::spawn(run_settings_reconciler_observed(
        settings_path,
        w.shared.clone(),
        w.coordinator.clone(),
        Duration::from_millis(20),
        Some(attempt_tx),
    ));
    w.yield_until("reconciler attempt 到达", || {
        attempt_rx.try_recv().is_ok()
    })
    .await;
    assert_eq!(
        request_ids.lock().unwrap().active_count(),
        1,
        "收到 reconciler attempt 时 IPC 必须仍在执行，证明真实重叠"
    );

    drop(gate);
    w.advance_until("IPC 完成", || ipc.is_finished()).await;
    let response: serde_json::Value =
        serde_json::from_str(&ipc.await.expect("IPC task 不 panic")).unwrap();
    assert_eq!(response["ok"], true, "settings_reload: {response}");
    assert_eq!(response["result"]["appliedRevision"], "1");

    // 并发目标 revision 只产生一个有效提交（任一锁顺序均满足）。
    w.yield_until("applied 前进到 1", || {
        w.shared.applied_settings_revision() == 1
    })
    .await;
    assert_eq!(w.settings_revision_count(1), 1);
    assert_eq!(w.settings_revisions(), vec![0, 1]);
    assert_eq!(w.settings_rx.borrow().revision, "1");
    // abort 前排空：reconciler 的幂等 apply 可能仍在飞；非阻塞抢锁成功即
    // 证明在飞 transition 已完成（或从未开始），避免 abort 砍掉半个
    // transition 导致 gate 永久冻结。
    let mut gate2 = None;
    for _ in 0..2_000 {
        if let Ok(guard) = w.coordinator.try_acquire_transition_lock_for_test() {
            gate2 = Some(guard);
            break;
        }
        tokio::task::yield_now().await;
        tokio::time::advance(Duration::from_millis(10)).await;
    }
    let gate2 = gate2.expect("reconciler 幂等 apply 必须在有界时间内完成");
    reconciler.abort();
    drop(gate2);

    // effectivity：边界前 rev 0 不变，边界后首条起 rev 1。
    w.produce_sample().await;
    w.yield_until("边界后 Observation 落库", || {
        w.observation_rows().len() == 2
    })
    .await;
    assert_eq!(
        w.observation_rows(),
        vec![(1, "active".to_string(), 0), (2, "active".to_string(), 1)],
        "IPC×reconciler 边界两侧 revision 必须各自正确"
    );
    assert!(w.shared.errors().is_empty());

    let (_writer, engine) = w.shutdown().await;
    assert_eq!(engine.settings_revision(), 1);
}

// =========================================================================
// 阶段 4.4 复审补修：新增测试
// =========================================================================

/// 携带特定 revision 的 PrivacyExcluded。
fn privacy_excluded_rev(sequence: u64, utc_ms: i64, revision: i64) -> ProcessorOutput {
    ProcessorOutput::PrivacyExcluded {
        sequence,
        continuity_epoch: 0,
        captured_at_utc_ms: utc_ms,
        settings_revision: revision,
    }
}

/// 携带特定 revision 的 CaptureError。
fn capture_error_rev(sequence: u64, utc_ms: i64, revision: i64) -> ProcessorOutput {
    ProcessorOutput::CaptureError {
        sequence,
        continuity_epoch: 0,
        captured_at_utc_ms: utc_ms,
        settings_revision: revision,
    }
}

// ---- 通用 WriterTask 测试 harness（无需完整拓扑）----

#[allow(dead_code)]
struct WtHarness {
    dir: TempDir,
    db_path: std::path::PathBuf,
    shared: Arc<SharedState>,
    capture_rx: watch::Receiver<CaptureState>,
    data_tx: mpsc::Sender<WriterDataMessage>,
    control_tx: mpsc::Sender<WriterControl>,
    run: tokio::task::JoinHandle<(Writer, ActivityEngine)>,
}

impl WtHarness {
    /// 创建 WriterTask fixture，Engine/DB 初始 revision 为 0。
    fn new() -> Self {
        let dir = TempDir::new().unwrap();
        let db_path = dir.path().join("wuji.db");
        Writer::bootstrap_with_timezone(&db_path, SHANGHAI, T0).unwrap();
        let continuity = Arc::new(ContinuityState::default());
        let runtime_id = RuntimeId::new();
        {
            let mut w = Writer::open_existing(&db_path).unwrap();
            let tx = w.transaction().unwrap();
            tx.insert_runtime(&runtime_id, T0).unwrap();
            tx.commit().unwrap();
        }
        let writer = Writer::open_existing(&db_path).unwrap();
        let engine =
            ActivityEngine::new(runtime_id.clone(), Settings::default(), continuity.clone())
                .unwrap();
        let shared = Arc::new(SharedState::new("0.1.0".to_string(), runtime_id));
        let (capture_state_tx, capture_rx) = watch::channel(CaptureState::Running);
        let (data_tx, data_rx) = mpsc::channel(8);
        let (control_tx, control_rx) = mpsc::channel(8);
        let task = WriterTask::new(
            writer,
            engine,
            shared.clone(),
            capture_state_tx,
            continuity.clone(),
            dir.path().join("config"),
            PipelineHealth::new(),
        );
        let run = tokio::spawn(async move { task.run(data_rx, control_rx).await });
        WtHarness {
            dir,
            db_path,
            shared,
            capture_rx,
            data_tx,
            control_tx,
            run,
        }
    }

    /// 通过真实 Barrier 协议推进到 rev 1。
    async fn advance_to_rev1(&self) {
        let barrier_id = BarrierId::new();
        self.data_tx
            .send(ProcessorOutput::Barrier(BarrierToken {
                id: barrier_id.clone(),
                kind: BarrierKind::SettingsApplied,
                expected_revision: 0,
            }))
            .await
            .unwrap();
        let (ack_tx, ack_rx) = tokio::sync::oneshot::channel();
        self.control_tx
            .send(WriterControl::SettingsApplied {
                settings: Settings {
                    revision: "1".to_string(),
                    ..Settings::default()
                },
                at_utc_ms: T0,
                barrier_id,
                expected_revision: 0,
                ack: ack_tx,
            })
            .await
            .unwrap();
        ack_rx.await.unwrap().unwrap();
    }
}

/// Writer 尚未启动的 rev-1 fixture。E13/E14 先把 data/control 两条 lane
/// 全部预装，再启动 biased select，确定性证明 control-first 的 drain 交错，
/// 避免已运行 Writer 抢先消费 data 导致测试退化为 post-fault 路径。
struct UnstartedWtHarness {
    dir: TempDir,
    db_path: std::path::PathBuf,
    shared: Arc<SharedState>,
    capture_rx: watch::Receiver<CaptureState>,
    data_tx: mpsc::Sender<WriterDataMessage>,
    control_tx: mpsc::Sender<WriterControl>,
    task: WriterTask,
    data_rx: mpsc::Receiver<WriterDataMessage>,
    control_rx: mpsc::Receiver<WriterControl>,
}

impl UnstartedWtHarness {
    fn new_at_rev1() -> Self {
        let dir = TempDir::new().unwrap();
        let db_path = dir.path().join("wuji.db");
        Writer::bootstrap_with_timezone(&db_path, SHANGHAI, T0).unwrap();
        let continuity = Arc::new(ContinuityState::default());
        let runtime_id = RuntimeId::new();
        let mut writer = Writer::open_existing(&db_path).unwrap();
        {
            let tx = writer.transaction().unwrap();
            tx.insert_runtime(&runtime_id, T0).unwrap();
            tx.commit().unwrap();
        }
        let mut engine =
            ActivityEngine::new(runtime_id.clone(), Settings::default(), continuity.clone())
                .unwrap();
        let settings = Settings {
            revision: "1".to_string(),
            ..Settings::default()
        };
        let backup_dir = dir.path().join("config");
        wuji_rebuild_agent::settings_persist::apply_settings_persistent(
            &mut engine,
            &mut writer,
            &backup_dir,
            &settings,
            T0,
        )
        .unwrap();

        let shared = Arc::new(SharedState::new("0.1.0".to_string(), runtime_id));
        shared.set_applied_settings_revision(1);
        let (capture_state_tx, capture_rx) = watch::channel(CaptureState::Running);
        let (data_tx, data_rx) = mpsc::channel(8);
        let (control_tx, control_rx) = mpsc::channel(8);
        let task = WriterTask::new(
            writer,
            engine,
            shared.clone(),
            capture_state_tx,
            continuity,
            backup_dir,
            PipelineHealth::new(),
        );
        Self {
            dir,
            db_path,
            shared,
            capture_rx,
            data_tx,
            control_tx,
            task,
            data_rx,
            control_rx,
        }
    }

    fn start(self) -> WtHarness {
        let Self {
            dir,
            db_path,
            shared,
            capture_rx,
            data_tx,
            control_tx,
            task,
            data_rx,
            control_rx,
        } = self;
        let run = tokio::spawn(async move { task.run(data_rx, control_rx).await });
        WtHarness {
            dir,
            db_path,
            shared,
            capture_rx,
            data_tx,
            control_tx,
            run,
        }
    }
}

// ---------------------------------------------------------------------------
// 复审补修 P1-01 测试 A：protocol fault 后 Settings control 不得提交
//
// 时序：Engine/DB 已处于 rev 1。data lane 中先放 rev-0 Observation，再放
// 匹配的 Settings Barrier（expected_revision=1）。Writer 在 biased select
// 下先消费 control，进入 drain_to_barrier，先遇到 rev-0 数据 →
// protocol_violation（reject_protocol_violation → mark_fatal）→ drain
// 立即返回 SETTINGS_CONFLICT。SettingsApplied 的 ack 收到错误。
// 断言：DB 最大 revision 仍为 1；Engine revision 不变；Writer/Process
// Faulted；Capture Stopped；诊断是 SETTINGS_CONFLICT。
// ---------------------------------------------------------------------------

#[tokio::test(start_paused = true)]
async fn e13_settings_control_rejected_after_drain_protocol_violation() {
    let h = UnstartedWtHarness::new_at_rev1();

    // data lane: 先 rev-0 Observation，再匹配 Barrier。
    let barrier_id = BarrierId::new();
    h.data_tx.try_send(obs_with_rev(1, T0 + 3_000, 0)).unwrap();
    h.data_tx
        .try_send(ProcessorOutput::Barrier(BarrierToken {
            id: barrier_id.clone(),
            kind: BarrierKind::SettingsApplied,
            expected_revision: 1,
        }))
        .unwrap();

    // control（biased select 先消费）。
    let (ack_tx, ack_rx) = tokio::sync::oneshot::channel();
    h.control_tx
        .try_send(WriterControl::SettingsApplied {
            settings: Settings {
                revision: "2".to_string(),
                ..Settings::default()
            },
            at_utc_ms: T0 + 6_000,
            barrier_id,
            expected_revision: 1,
            ack: ack_tx,
        })
        .unwrap();
    let h = h.start();

    let result = ack_rx.await.unwrap();
    assert!(result.is_err(), "协议违例后 Settings control 必须被拒绝");
    assert_eq!(
        result.unwrap_err().code,
        SafeErrorCode::SettingsConflict,
        "错误码必须是 SETTINGS_CONFLICT"
    );

    // DB 副作用为零。
    let conn = Connection::open(&h.db_path).unwrap();
    let max_rev: i64 = conn
        .query_row(
            "SELECT COALESCE(MAX(revision), -1) FROM settings_revisions",
            [],
            |r| r.get(0),
        )
        .unwrap();
    assert_eq!(max_rev, 1, "DB revision 不得前进");
    let gap_count: i64 = conn
        .query_row("SELECT COUNT(*) FROM capture_gaps", [], |r| r.get(0))
        .unwrap();
    assert_eq!(gap_count, 0, "不得产生 gap");
    drop(conn);

    yield_wait("Writer faulted", || {
        h.shared.writer_state() == WriterState::Faulted
    })
    .await;
    assert_eq!(h.shared.process_state(), ProcessState::Faulted);
    assert_eq!(*h.capture_rx.borrow(), CaptureState::Stopped);
    assert_eq!(
        h.shared.errors().get(&ErrorSource::Writer),
        Some(&SafeErrorCode::SettingsConflict)
    );

    drop(h.control_tx);
    drop(h.data_tx);
    let (_w, eng) = h.run.await.unwrap();
    assert_eq!(eng.settings_revision(), 1, "Engine revision 不得前进");
}

// ---------------------------------------------------------------------------
// 复审补修 P1-01 测试 B：protocol fault 后 Lifecycle control 不得提交
//
// 与 A 同样时序，但发送 Lifecycle control。断言：ack 返回 SETTINGS_CONFLICT
// (非 INTERNAL_SAFE_ERROR)；不产生 lifecycle gap；Engine/SQLite 保持 rev 1。
// ---------------------------------------------------------------------------

#[tokio::test(start_paused = true)]
async fn e14_lifecycle_control_rejected_after_drain_protocol_violation() {
    let h = UnstartedWtHarness::new_at_rev1();

    let barrier_id = BarrierId::new();
    h.data_tx.try_send(obs_with_rev(1, T0 + 3_000, 0)).unwrap();
    h.data_tx
        .try_send(ProcessorOutput::Barrier(BarrierToken {
            id: barrier_id.clone(),
            kind: BarrierKind::Lifecycle,
            expected_revision: 1,
        }))
        .unwrap();

    let (ack_tx, ack_rx) = tokio::sync::oneshot::channel();
    h.control_tx
        .try_send(WriterControl::Lifecycle {
            event: EngineEvent::CapturePaused {
                at_utc_ms: T0 + 6_000,
            },
            barrier_id,
            expected_revision: 1,
            ack: ack_tx,
        })
        .unwrap();
    let h = h.start();

    let result = ack_rx.await.unwrap();
    assert!(result.is_err(), "协议违例后 Lifecycle 必须被拒绝");
    assert_eq!(
        result.unwrap_err().code,
        SafeErrorCode::SettingsConflict,
        "错误码保持 SETTINGS_CONFLICT，不得覆盖为 INTERNAL_SAFE_ERROR"
    );

    // 零 DB 副作用（不含 lifecycle gap）。
    let conn = Connection::open(&h.db_path).unwrap();
    let gap_count: i64 = conn
        .query_row("SELECT COUNT(*) FROM capture_gaps", [], |r| r.get(0))
        .unwrap();
    assert_eq!(gap_count, 0, "协议违例后不得产生任何 gap");
    drop(conn);

    yield_wait("Writer faulted", || {
        h.shared.writer_state() == WriterState::Faulted
    })
    .await;
    assert_eq!(h.shared.process_state(), ProcessState::Faulted);
    assert_eq!(*h.capture_rx.borrow(), CaptureState::Stopped);
    assert_eq!(
        h.shared.errors().get(&ErrorSource::Writer),
        Some(&SafeErrorCode::SettingsConflict)
    );

    drop(h.control_tx);
    drop(h.data_tx);
    let (_w, eng) = h.run.await.unwrap();
    assert_eq!(eng.settings_revision(), 1);
}

// ---------------------------------------------------------------------------
// 复审补修 P1-01 测试 C1：pending-first —— Barrier 已在 pending，
// 随后 protocol violation 锁存，再发送匹配 control → 拒绝。
// ---------------------------------------------------------------------------

#[tokio::test(start_paused = true)]
async fn e15_pending_barrier_before_fault_is_refused_by_protocol_guard() {
    let h = WtHarness::new();
    h.advance_to_rev1().await;

    // ① Barrier 先进入 data lane → 普通分支 → 登记 pending。
    let barrier_id = BarrierId::new();
    h.data_tx
        .send(ProcessorOutput::Barrier(BarrierToken {
            id: barrier_id.clone(),
            kind: BarrierKind::SettingsApplied,
            expected_revision: 1,
        }))
        .await
        .unwrap();

    // ② 再发 rev-0 数据 → 普通数据分支 → protocol_violation。
    h.data_tx
        .send(obs_with_rev(1, T0 + 3_000, 0))
        .await
        .unwrap();
    yield_wait("Writer faulted", || {
        h.shared.writer_state() == WriterState::Faulted
    })
    .await;

    // ③ 发送匹配 control：drain_to_barrier → pending Matched →
    //    ensure_protocol_healthy 拒绝。
    let (ack_tx, ack_rx) = tokio::sync::oneshot::channel();
    h.control_tx
        .send(WriterControl::SettingsApplied {
            settings: Settings {
                revision: "2".to_string(),
                ..Settings::default()
            },
            at_utc_ms: T0 + 6_000,
            barrier_id,
            expected_revision: 1,
            ack: ack_tx,
        })
        .await
        .unwrap();

    let result = ack_rx.await.unwrap();
    assert!(result.is_err(), "fault 后 pending 匹配也必须被拒绝");
    assert_eq!(result.unwrap_err().code, SafeErrorCode::SettingsConflict);

    let conn = Connection::open(&h.db_path).unwrap();
    let max_rev: i64 = conn
        .query_row(
            "SELECT COALESCE(MAX(revision), -1) FROM settings_revisions",
            [],
            |r| r.get(0),
        )
        .unwrap();
    assert_eq!(max_rev, 1);
    drop(conn);

    drop(h.control_tx);
    drop(h.data_tx);
    let (_w, eng) = h.run.await.unwrap();
    assert_eq!(eng.settings_revision(), 1);
}

// ---------------------------------------------------------------------------
// 复审补修 P1-01 测试 C2：fault 后迟到的全新 ID control 不能提交。
// ---------------------------------------------------------------------------

#[tokio::test(start_paused = true)]
async fn e16_late_control_after_fault_is_fenced() {
    let h = WtHarness::new();
    h.advance_to_rev1().await;

    // ① 触发 protocol_violation。
    h.data_tx
        .send(obs_with_rev(1, T0 + 3_000, 0))
        .await
        .unwrap();
    yield_wait("Writer faulted", || {
        h.shared.writer_state() == WriterState::Faulted
    })
    .await;

    // ② fault 后发送全新 control（全新 BarrierId），不发送 Barrier。
    // drain 入口守卫必须在不推进 paused clock 的情况下立即拒绝，不能退化
    // 为 5s Barrier timeout。
    let barrier_id = BarrierId::new();
    let (ack_tx, mut ack_rx) = tokio::sync::oneshot::channel();
    h.control_tx
        .send(WriterControl::SettingsApplied {
            settings: Settings {
                revision: "2".to_string(),
                ..Settings::default()
            },
            at_utc_ms: T0 + 6_000,
            barrier_id,
            expected_revision: 1,
            ack: ack_tx,
        })
        .await
        .unwrap();

    let mut received = None;
    for _ in 0..20_000 {
        match ack_rx.try_recv() {
            Ok(result) => {
                received = Some(result);
                break;
            }
            Err(tokio::sync::oneshot::error::TryRecvError::Empty) => {
                tokio::task::yield_now().await;
            }
            Err(tokio::sync::oneshot::error::TryRecvError::Closed) => {
                panic!("Writer 不得丢弃 Settings ack")
            }
        }
    }
    let result = received.expect("Settings ack 必须无需推进时钟即可有界返回");
    assert_eq!(
        result
            .expect_err("protocol fault 后 Settings 必须立即拒绝")
            .code,
        SafeErrorCode::SettingsConflict
    );
    assert_eq!(
        h.shared.errors().get(&ErrorSource::Writer),
        Some(&SafeErrorCode::SettingsConflict),
        "即时 fencing 不得覆盖精确诊断"
    );

    let conn = Connection::open(&h.db_path).unwrap();
    let max_rev: i64 = conn
        .query_row(
            "SELECT COALESCE(MAX(revision), -1) FROM settings_revisions",
            [],
            |r| r.get(0),
        )
        .unwrap();
    assert_eq!(max_rev, 1);
    drop(conn);

    drop(h.control_tx);
    drop(h.data_tx);
    let (_w, eng) = h.run.await.unwrap();
    assert_eq!(eng.settings_revision(), 1);
}

/// P1-01 第二次补修：protocol fault 已锁存后收到无 Barrier 的 Lifecycle
/// control，同样必须由 drain 入口立即返回 SETTINGS_CONFLICT，且 Lifecycle
/// 通用错误处理不得覆盖 Writer 的精确诊断。
#[tokio::test(start_paused = true)]
async fn e17_late_lifecycle_without_barrier_is_immediately_fenced() {
    let h = WtHarness::new();
    h.advance_to_rev1().await;
    h.data_tx
        .send(obs_with_rev(1, T0 + 3_000, 0))
        .await
        .unwrap();
    yield_wait("Writer faulted", || {
        h.shared.writer_state() == WriterState::Faulted
    })
    .await;

    let (ack_tx, mut ack_rx) = tokio::sync::oneshot::channel();
    h.control_tx
        .send(WriterControl::Lifecycle {
            event: EngineEvent::CapturePaused {
                at_utc_ms: T0 + 6_000,
            },
            barrier_id: BarrierId::new(),
            expected_revision: 1,
            ack: ack_tx,
        })
        .await
        .unwrap();

    let mut received = None;
    for _ in 0..20_000 {
        match ack_rx.try_recv() {
            Ok(result) => {
                received = Some(result);
                break;
            }
            Err(tokio::sync::oneshot::error::TryRecvError::Empty) => {
                tokio::task::yield_now().await;
            }
            Err(tokio::sync::oneshot::error::TryRecvError::Closed) => {
                panic!("Writer 不得丢弃 Lifecycle ack")
            }
        }
    }
    let result = received.expect("Lifecycle ack 必须无需推进时钟即可有界返回");
    assert_eq!(
        result
            .expect_err("protocol fault 后 Lifecycle 必须立即拒绝")
            .code,
        SafeErrorCode::SettingsConflict
    );
    assert_eq!(
        h.shared.errors().get(&ErrorSource::Writer),
        Some(&SafeErrorCode::SettingsConflict),
        "Lifecycle handler 不得覆盖 SETTINGS_CONFLICT"
    );
    assert_eq!(
        Connection::open(&h.db_path)
            .unwrap()
            .query_row("SELECT COUNT(*) FROM capture_gaps", [], |row| row
                .get::<_, i64>(0))
            .unwrap(),
        0,
        "协议 fault 后 Lifecycle 不得写入 gap"
    );

    drop(h.control_tx);
    drop(h.data_tx);
    let (_writer, engine) = h.run.await.unwrap();
    assert_eq!(engine.settings_revision(), 1);
}

// ---------------------------------------------------------------------------
// P2-01 补修：E08 拆分为三种首次 mismatch
// ---------------------------------------------------------------------------

/// E08b：PrivacyExcluded 作为首次 mismatch —— Writer 防线独立校验。
#[tokio::test(start_paused = true)]
async fn e08b_writer_mismatch_privacy_excluded_first_fails_closed() {
    let dir = TempDir::new().unwrap();
    let db_path = dir.path().join("wuji.db");
    Writer::bootstrap_with_timezone(&db_path, SHANGHAI, T0).unwrap();
    let continuity = Arc::new(ContinuityState::default());
    let runtime_id = RuntimeId::new();
    {
        let mut w = Writer::open_existing(&db_path).unwrap();
        let tx = w.transaction().unwrap();
        tx.insert_runtime(&runtime_id, T0).unwrap();
        tx.commit().unwrap();
    }
    let writer = Writer::open_existing(&db_path).unwrap();
    let engine =
        ActivityEngine::new(runtime_id.clone(), Settings::default(), continuity.clone()).unwrap();
    let shared = Arc::new(SharedState::new("0.1.0".to_string(), runtime_id));
    let (capture_state_tx, capture_rx) = watch::channel(CaptureState::Running);
    let (data_tx, data_rx) = mpsc::channel(8);
    let (control_tx, control_rx) = mpsc::channel(8);

    let task = WriterTask::new(
        writer,
        engine,
        shared.clone(),
        capture_state_tx,
        continuity.clone(),
        dir.path().join("config"),
        PipelineHealth::new(),
    );
    let run = tokio::spawn(async move { task.run(data_rx, control_rx).await });

    // 推进 Engine 到 rev 1。
    let barrier_id = BarrierId::new();
    data_tx
        .send(ProcessorOutput::Barrier(BarrierToken {
            id: barrier_id.clone(),
            kind: BarrierKind::SettingsApplied,
            expected_revision: 0,
        }))
        .await
        .unwrap();
    let (ack_tx, ack_rx) = tokio::sync::oneshot::channel();
    control_tx
        .send(WriterControl::SettingsApplied {
            settings: Settings {
                revision: "1".to_string(),
                ..Settings::default()
            },
            at_utc_ms: T0,
            barrier_id,
            expected_revision: 0,
            ack: ack_tx,
        })
        .await
        .unwrap();
    ack_rx.await.unwrap().unwrap();

    // 只发送一条 PrivacyExcluded(rev 0) → 它是首次 mismatch。
    data_tx
        .send(privacy_excluded_rev(1, T0 + 3_000, 0))
        .await
        .unwrap();
    // 再发一条合法 rev-1 Observation → 锁存后必须被拒绝。
    data_tx.send(obs_with_rev(2, T0 + 6_000, 1)).await.unwrap();

    yield_wait("Writer faulted", || {
        shared.writer_state() == WriterState::Faulted
    })
    .await;
    assert_eq!(shared.process_state(), ProcessState::Faulted);
    assert_eq!(
        shared.errors().get(&ErrorSource::Writer),
        Some(&SafeErrorCode::SettingsConflict)
    );

    // 零 SQLite 副作用（不含 gap）。
    let conn = Connection::open(&db_path).unwrap();
    let gaps: i64 = conn
        .query_row("SELECT COUNT(*) FROM capture_gaps", [], |r| r.get(0))
        .unwrap();
    assert_eq!(gaps, 0, "PrivacyExcluded 错配不得产生 gap");
    let obs_count: i64 = conn
        .query_row("SELECT COUNT(*) FROM foreground_observations", [], |r| {
            r.get(0)
        })
        .unwrap();
    assert_eq!(obs_count, 0, "锁存后合法 Observation 也不得落库");
    drop(conn);

    assert_eq!(*capture_rx.borrow(), CaptureState::Stopped);

    drop(control_tx);
    drop(data_tx);
    let (_w, eng) = run.await.unwrap();
    assert_eq!(eng.settings_revision(), 1, "Engine revision 不重标不回退");
}

/// E08c：CaptureError 作为首次 mismatch —— Writer 防线独立校验。
#[tokio::test(start_paused = true)]
async fn e08c_writer_mismatch_capture_error_first_fails_closed() {
    let dir = TempDir::new().unwrap();
    let db_path = dir.path().join("wuji.db");
    Writer::bootstrap_with_timezone(&db_path, SHANGHAI, T0).unwrap();
    let continuity = Arc::new(ContinuityState::default());
    let runtime_id = RuntimeId::new();
    {
        let mut w = Writer::open_existing(&db_path).unwrap();
        let tx = w.transaction().unwrap();
        tx.insert_runtime(&runtime_id, T0).unwrap();
        tx.commit().unwrap();
    }
    let writer = Writer::open_existing(&db_path).unwrap();
    let engine =
        ActivityEngine::new(runtime_id.clone(), Settings::default(), continuity.clone()).unwrap();
    let shared = Arc::new(SharedState::new("0.1.0".to_string(), runtime_id));
    let (capture_state_tx, capture_rx) = watch::channel(CaptureState::Running);
    let (data_tx, data_rx) = mpsc::channel(8);
    let (control_tx, control_rx) = mpsc::channel(8);

    let task = WriterTask::new(
        writer,
        engine,
        shared.clone(),
        capture_state_tx,
        continuity.clone(),
        dir.path().join("config"),
        PipelineHealth::new(),
    );
    let run = tokio::spawn(async move { task.run(data_rx, control_rx).await });

    // 推进 Engine 到 rev 1。
    let barrier_id = BarrierId::new();
    data_tx
        .send(ProcessorOutput::Barrier(BarrierToken {
            id: barrier_id.clone(),
            kind: BarrierKind::SettingsApplied,
            expected_revision: 0,
        }))
        .await
        .unwrap();
    let (ack_tx, ack_rx) = tokio::sync::oneshot::channel();
    control_tx
        .send(WriterControl::SettingsApplied {
            settings: Settings {
                revision: "1".to_string(),
                ..Settings::default()
            },
            at_utc_ms: T0,
            barrier_id,
            expected_revision: 0,
            ack: ack_tx,
        })
        .await
        .unwrap();
    ack_rx.await.unwrap().unwrap();

    // 只发送一条 CaptureError(rev 0) → 它是首次 mismatch。
    data_tx
        .send(capture_error_rev(1, T0 + 3_000, 0))
        .await
        .unwrap();
    // 再发一条合法 rev-1 Observation → 锁存后必须被拒绝。
    data_tx.send(obs_with_rev(2, T0 + 6_000, 1)).await.unwrap();

    yield_wait("Writer faulted", || {
        shared.writer_state() == WriterState::Faulted
    })
    .await;
    assert_eq!(shared.process_state(), ProcessState::Faulted);
    assert_eq!(
        shared.errors().get(&ErrorSource::Writer),
        Some(&SafeErrorCode::SettingsConflict)
    );

    // 零 SQLite 副作用（不含 capture_error gap）。
    let conn = Connection::open(&db_path).unwrap();
    let gaps: i64 = conn
        .query_row("SELECT COUNT(*) FROM capture_gaps", [], |r| r.get(0))
        .unwrap();
    assert_eq!(gaps, 0, "CaptureError 错配不得产生 gap");
    let obs_count: i64 = conn
        .query_row("SELECT COUNT(*) FROM foreground_observations", [], |r| {
            r.get(0)
        })
        .unwrap();
    assert_eq!(obs_count, 0);
    drop(conn);

    assert_eq!(*capture_rx.borrow(), CaptureState::Stopped);

    drop(control_tx);
    drop(data_tx);
    let (_w, eng) = run.await.unwrap();
    assert_eq!(eng.settings_revision(), 1);
}

// ---------------------------------------------------------------------------
// P2-02 补修 E11c：真正 transition-in-flight 的 Processor 退出
//
// 使用唯一 Coordinator + 真实 Capture/Processor/Writer。测试用 control
// 转发门只观察 Coordinator 已发送 WriterControl 的时刻：这严格晚于
// injected ack，因而能证明 freeze 与 Barrier 注入均已发生。Writer data
// lane 预先填满且 Writer future 尚未 poll，Processor 必然阻塞在 Barrier
// 转发；此时终止 Processor，再启动 Writer 并放行 control。
// ---------------------------------------------------------------------------

/// E11c：真实 transition-in-flight Processor 退出。
///
/// 1. Writer data lane 容量为 1；Writer future 注册健康但暂不 poll。
/// 2. 采样填满 data lane。
/// 3. apply_settings 完成 freeze + Barrier injected ack 后发送 control；测试
///    转发门收到 control，作为严格 rendezvous。
/// 4. 此时 data lane 仍满，Processor 正阻塞转发 Barrier；终止 Processor。
/// 5. 启动真实 Writer、放行原 control；Writer 排空旧样本后看到 data lane
///    断开，返回未提交错误。
/// 6. DB/Engine/applied/watch 保持旧 revision，supervisor 最终安全锁存。
#[tokio::test(start_paused = true)]
async fn e11c_transition_in_flight_processor_exit_is_uncommitted_and_safe_stops() {
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
    let coordinator = plane.coordinator.clone();
    let health = plane.health.clone();
    let settings_rx = plane.settings_rx.clone();
    let capture_rx = plane.writer_capture_stop_tx.subscribe();
    let utc_clock = Arc::new(AtomicI64::new(T0));
    let clock = utc_clock.clone();
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
            utc_now_ms: Arc::new(move || clock.load(Ordering::Acquire)),
        },
        plane.barrier_request_rx,
        &health,
    );
    let (processor_rx, processor_handle) = spawn_observation_processor_with_capacity(
        pipeline_rx,
        plane.settings_rx.clone(),
        continuity.clone(),
        1,
        &health,
    );

    // Writer 使用独立转发后的 control lane。into_run_future 同步注册 Writer
    // 健康守卫，但 future 暂不 spawn，确保容量 1 的 data lane 可被钉满。
    let (forward_tx, forward_rx) = mpsc::channel(4);
    let writer_task = WriterTask::new(
        writer,
        engine,
        shared.clone(),
        plane.writer_capture_stop_tx.clone(),
        continuity.clone(),
        dir.path().join("config"),
        health.clone(),
    );
    let writer_future = writer_task.into_run_future(processor_rx, forward_rx);
    let supervisor_handle = tokio::spawn(supervise_pipeline_exits(
        plane.pipeline_exit_rx,
        coordinator.clone(),
    ));

    // 透明 control 转发门：收到 SettingsApplied 就证明 Coordinator 已完成
    // freeze、Barrier FIFO 注入和 injected ack。release 前不把 control 交给
    // Writer，确保 Processor 的 Barrier send 被满 data lane 阻塞。
    let (control_seen_tx, mut control_seen_rx) = tokio::sync::oneshot::channel();
    let (release_tx, release_rx) = tokio::sync::oneshot::channel();
    let mut coordinator_control_rx = plane.control_rx;
    let forward_for_proxy = forward_tx.clone();
    let proxy_handle = tokio::spawn(async move {
        let control = coordinator_control_rx
            .recv()
            .await
            .expect("Coordinator 必须发送 SettingsApplied control");
        assert!(matches!(&control, WriterControl::SettingsApplied { .. }));
        let _ = control_seen_tx.send(());
        release_rx.await.expect("测试必须放行 control");
        forward_for_proxy
            .send(control)
            .await
            .expect("真实 Writer control lane 必须在线");
    });

    assert_eq!(
        coordinator.apply_capture_command("capture_start", T0).await,
        Ok(CaptureState::Running)
    );
    // 推进采样直到唯一 data 槽被正常 Observation 占满；Writer 尚未 poll，
    // 因而这个条件一旦成立会稳定保持。
    for _ in 0..100 {
        utc_clock.fetch_add(50, Ordering::AcqRel);
        tokio::time::advance(Duration::from_millis(50)).await;
        tokio::task::yield_now().await;
        if continuity.writer_queue_depth() == 1 {
            break;
        }
    }
    assert_eq!(
        continuity.writer_queue_depth(),
        1,
        "writer data lane 必须满载"
    );

    let at = utc_clock.load(Ordering::Acquire);
    let apply = tokio::spawn({
        let coordinator = coordinator.clone();
        async move {
            coordinator
                .apply_settings(
                    Settings {
                        revision: "1".to_string(),
                        sampling_interval_seconds: 1,
                        ..Settings::default()
                    },
                    at,
                )
                .await
        }
    });

    let mut control_seen = false;
    for _ in 0..20_000 {
        match control_seen_rx.try_recv() {
            Ok(()) => {
                control_seen = true;
                break;
            }
            Err(tokio::sync::oneshot::error::TryRecvError::Empty) => {
                tokio::task::yield_now().await;
            }
            Err(tokio::sync::oneshot::error::TryRecvError::Closed) => {
                panic!("control 转发门不得提前关闭")
            }
        }
    }
    assert!(control_seen, "必须观察到 injected ack 之后的 WriterControl");
    assert_ne!(
        *capture_rx.borrow(),
        CaptureState::Running,
        "transition suppression 必须已关闭有效采集 gate"
    );
    assert_eq!(
        shared.capture_state(),
        *capture_rx.borrow(),
        "freeze 时 watch/shared 必须一致"
    );
    assert_eq!(
        continuity.writer_queue_depth(),
        1,
        "Barrier 转发前 data lane 仍满"
    );
    assert_eq!(health.processor_state(), TaskLifecycle::Alive);
    assert!(
        !apply.is_finished(),
        "Writer ack 未放行前 transition 不得完成"
    );

    // Processor 此刻正在向满 data lane 阻塞发送 Barrier；终止后 data lane
    // 只剩边界前 Observation，Barrier 不可能到达 Writer。
    processor_handle.abort();
    for _ in 0..20_000 {
        if health.processor_state() == TaskLifecycle::Dead {
            break;
        }
        tokio::task::yield_now().await;
    }
    assert_eq!(health.processor_state(), TaskLifecycle::Dead);

    let writer_handle = tokio::spawn(writer_future);
    release_tx.send(()).expect("放行 control");
    proxy_handle.await.expect("control proxy 不 panic");
    for _ in 0..20_000 {
        if apply.is_finished() {
            break;
        }
        tokio::task::yield_now().await;
    }
    assert!(
        apply.is_finished(),
        "transition 必须在 deadline 内完成（不得永久悬挂）"
    );
    let result = apply.await.expect("apply task 不 panic");
    assert_eq!(
        result
            .expect_err("transition-in-flight Processor 退出必须返回错误")
            .code,
        SafeErrorCode::InternalSafeError,
        "Writer 已证明 data lane 断开且边界未提交"
    );

    let max_revision = Connection::open(&db_path)
        .unwrap()
        .query_row(
            "SELECT COALESCE(MAX(revision), -1) FROM settings_revisions",
            [],
            |row| row.get::<_, i64>(0),
        )
        .unwrap();
    assert_eq!(max_revision, 0);
    assert_eq!(shared.applied_settings_revision(), 0);
    assert_eq!(settings_rx.borrow().revision, "0");

    for _ in 0..20_000 {
        if shared.process_state() == ProcessState::Faulted {
            break;
        }
        tokio::task::yield_now().await;
    }
    assert_eq!(shared.process_state(), ProcessState::Faulted);
    assert_eq!(*capture_rx.borrow(), CaptureState::Stopped);
    assert_eq!(shared.capture_state(), CaptureState::Stopped);
    assert_eq!(shared.status_dto().capture_state, CaptureState::Stopped);

    let fenced = coordinator
        .apply_capture_command("capture_start", T0)
        .await
        .expect_err("fault 后 start 必须被 fencing");
    assert_eq!(fenced.code, SafeErrorCode::AgentWriterFaulted);
    let fenced = coordinator
        .apply_settings(
            Settings {
                revision: "2".to_string(),
                ..Settings::default()
            },
            T0,
        )
        .await
        .expect_err("fault 后 settings 必须被 fencing");
    assert_eq!(fenced.code, SafeErrorCode::AgentWriterFaulted);

    supervisor_handle.abort();
    drop(forward_tx);
    let (_writer, engine) = writer_handle.await.expect("Writer 不 panic");
    capture_handle.abort();
    assert_eq!(engine.settings_revision(), 0, "Engine 保持旧 revision");
}
