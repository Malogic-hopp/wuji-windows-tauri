// Release Agent 是后台进程：Desktop 启动和 Windows 登录自启动均不应显示控制台。
// Debug 构建仍保留控制台，方便本地开发和故障诊断。
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

//! WUJI Rebuild v0.1 Rust Agent 二进制入口（09 §5、§9.3）。
//!
//! 启动顺序：参数/channel 验证 → 单实例 → 路径解析 → 打开或创建数据库 →
//! 启动恢复 → Settings 对账 → 采集/Processor/Writer/IPC/心跳/维护。

use std::sync::Arc;

use tokio::sync::watch;
use wuji_core::domain::{CaptureState, ProcessState};
use wuji_core::dto::RuntimeId;
use wuji_core::settings::Settings;
use wuji_rebuild_agent::activity::ActivityEngine;
use wuji_rebuild_agent::capture_loop::{
    CaptureLoopConfig, CaptureSource, ContinuityState, RawSample, spawn_capture_loop,
};
use wuji_rebuild_agent::command_server::{CommandServerContext, run_command_server};
use wuji_rebuild_agent::heartbeat::run_heartbeat;
use wuji_rebuild_agent::maintenance::run_maintenance;
use wuji_rebuild_agent::processor_task::spawn_observation_processor;
use wuji_rebuild_agent::runtime_paths;
use wuji_rebuild_agent::settings_backup;
use wuji_rebuild_agent::settings_persist;
use wuji_rebuild_agent::settings_reconciler::run_settings_reconciler;
use wuji_rebuild_agent::settings_store::{load_settings_file, reconcile_startup_settings};
use wuji_rebuild_agent::shared::SharedState;
use wuji_rebuild_agent::writer_task::WriterTask;
use wuji_storage::Writer;

const AGENT_VERSION: &str = env!("CARGO_PKG_VERSION");

struct Win32CaptureSource;

impl CaptureSource for Win32CaptureSource {
    fn capture(&self) -> RawSample {
        match wuji_windows::capture_foreground() {
            Ok(sample) => RawSample {
                process_file_name: sample.process_file_name.ok(),
                idle: match sample.idle_seconds {
                    Ok(seconds) => wuji_core::pipeline::IdleReading::Seconds(seconds),
                    Err(_) => wuji_core::pipeline::IdleReading::Unavailable,
                },
            },
            Err(_) => RawSample {
                process_file_name: None,
                idle: wuji_core::pipeline::IdleReading::Unavailable,
            },
        }
    }
}

#[derive(Debug, Default)]
struct Args {
    channel: String,
    capture_on_start: bool,
}

fn parse_args() -> Result<Args, String> {
    let mut args = Args::default();
    let mut iter = std::env::args().skip(1);
    while let Some(arg) = iter.next() {
        match arg.as_str() {
            "--channel" => {
                args.channel = iter
                    .next()
                    .ok_or_else(|| "--channel 需要参数值".to_string())?;
            }
            "--capture-on-start" => args.capture_on_start = true,
            other => return Err(format!("未知参数: {other}")),
        }
    }
    Ok(args)
}

#[tokio::main(flavor = "current_thread")]
async fn main() {
    if let Err(error) = run().await {
        eprintln!("{}: {error}", wuji_core::runtime_names::AGENT_EXE_NAME);
        std::process::exit(1);
    }
}

async fn run() -> Result<(), String> {
    let args = parse_args()?;
    if args.channel.is_empty() {
        return Err("缺少 --channel 参数".to_string());
    }
    let paths = runtime_paths::resolve(&args.channel)?;

    // 单实例（09 §4.1：按 channel 与用户隔离）。
    let _instance = match wuji_windows::SingleInstanceGuard::acquire(&paths.agent_mutex)
        .map_err(|e| format!("单实例 mutex 创建失败: {e}"))?
    {
        Some(guard) => guard,
        None => return Err("已有同 channel Agent 在运行".to_string()),
    };

    std::fs::create_dir_all(paths.data_root.join("data"))
        .map_err(|e| format!("创建数据目录失败: {e}"))?;
    std::fs::create_dir_all(&paths.logs).map_err(|e| format!("创建日志目录失败: {e}"))?;

    // 打开或创建数据库（09 §7.1：全新空库或既有 v0.1 库；不打开旧库）。
    let now = wuji_rebuild_agent::capture_loop::now_utc_ms();
    let mut writer = if paths.database.exists() {
        Writer::open_existing(&paths.database).map_err(|e| format!("打开数据库失败: {e}"))?
    } else {
        Writer::bootstrap(&paths.database, now).map_err(|e| format!("创建数据库失败: {e}"))?
    };

    // Settings 加载与启动对账（09 §9.1、审核 P1-01）：同时输入 DB 最大已应用
    // revision/digest、settings 文件、经 DB metadata 交叉验证的双槽候选；
    // 无法恢复 DB 当前 revision 时禁止采集，不静默回 revision 0。
    let db_meta = writer
        .latest_settings_revision_digest()
        .map_err(|e| format!("读取 settings 对账信息失败: {e}"))?;
    let backup =
        settings_backup::read_backup_matching(&paths.data_root.join("config"), db_meta.as_ref());
    let decision = reconcile_startup_settings(db_meta, load_settings_file(&paths.settings), backup);
    let settings = decision.settings;

    let continuity = Arc::new(ContinuityState::default());
    let runtime_id = RuntimeId::new();
    let shared = Arc::new(SharedState::new(
        AGENT_VERSION.to_string(),
        runtime_id.clone(),
    ));
    if let Some(code) = decision.diagnostic {
        // 复审 P2-01：启动对账诊断归入 Settings 来源，不覆盖其他来源。
        shared.set_error(wuji_core::error::ErrorSource::Settings, code);
        eprintln!("settings 启动对账诊断: {code:?}");
    }
    shared.set_capture_blocked(!decision.capture_allowed);

    let mut engine = ActivityEngine::new(runtime_id, settings.clone(), continuity.clone())
        .map_err(|e| format!("引擎初始化失败: {e}"))?;
    engine
        .recover_startup(&mut writer, now)
        .map_err(|e| format!("启动恢复失败: {e}"))?;
    if decision.capture_allowed && settings.revision != wuji_core::settings::DEFAULT_REVISION {
        // 复审 P1-01：启动前滚/幂等恢复与运行时共用同一 crash-consistent 协议：
        // 先写 DB 感知候选（必要时修复缺失冗余），再提交 SQLite。
        settings_persist::apply_settings_persistent(
            &mut engine,
            &mut writer,
            &paths.data_root.join("config"),
            &settings,
            now,
        )
        .map_err(|e| format!("settings 对账失败: {e}"))?;
    }
    // applied revision 语义与 DB 一致（审核 P1-01）：blocked 时保持 DB 值，不得重置为 0。
    shared.set_applied_settings_revision(decision.applied_revision);

    // 初始 capture state：仅 --capture-on-start 直接进入 Running（09 §9.3）；
    // settings 不可恢复时强制 Stopped（R04）。
    let initial_capture = if args.capture_on_start && decision.capture_allowed {
        CaptureState::Running
    } else {
        CaptureState::Stopped
    };
    shared.set_capture_state(initial_capture);
    shared.set_process_state(ProcessState::Running);

    let (shutdown_tx, mut shutdown_rx) = watch::channel(false);

    // 阶段 4.3 复审 P2-01：生产控制面由唯一装配函数创建——唯一
    // Arc<CaptureCoordinator>；BarrierRequest sender 只属 Coordinator；
    // Lifecycle/SettingsApplied control 只能由 Coordinator 构造（完整
    // mpsc::Sender<WriterControl> 不在 main 作用域内出现）；
    // Heartbeat/Checkpoint/Shutdown 经窄通道 MaintenanceControl。
    let plane =
        wuji_rebuild_agent::control_plane::assemble(shared.clone(), settings, initial_capture);

    // 采集 → Processor → Writer lanes。
    // S2-04 返修：Capture Loop 是 CapturePipelineItem FIFO 的唯一生产者。
    // BarrierRequest 从 CaptureCoordinator 注入到 Capture Loop。
    let (pipeline_rx, capture_handle) = spawn_capture_loop(
        Win32CaptureSource,
        plane.settings_rx.clone(),
        plane.capture_state_rx,
        continuity.clone(),
        CaptureLoopConfig::default(),
        plane.barrier_request_rx,
        &plane.health,
    );
    let (processor_rx, processor_handle) = spawn_observation_processor(
        pipeline_rx,
        plane.settings_rx,
        continuity.clone(),
        &plane.health,
    );

    let writer_task = WriterTask::new(
        writer,
        engine,
        shared.clone(),
        plane.writer_capture_stop_tx,
        continuity.clone(),
        paths.data_root.join("config"),
        plane.health.clone(),
    );
    // into_run_future 在 spawn 前同步注册 Writer 健康（第二次复审 P1）；
    // 三个生产任务全部注册完成后，下方才启动 IPC/reconciler/pump 控制入口。
    let writer_handle = tokio::spawn(writer_task.into_run_future(processor_rx, plane.control_rx));

    let coordinator = plane.coordinator.clone();
    // 生产任务退出必须主动通知 Coordinator；仅更新 PipelineHealth 原子位不足以
    // 把已经发布的 Running 收敛为 fail-closed。事件在 guard Drop 时排队，
    // 因而这里稍后 spawn 不会丢失启动窗口内的退出。
    let pipeline_supervisor_handle =
        tokio::spawn(wuji_rebuild_agent::control_plane::supervise_pipeline_exits(
            plane.pipeline_exit_rx,
            coordinator.clone(),
        ));

    // IPC、心跳、维护。
    let server_context = Arc::new(CommandServerContext {
        shared: shared.clone(),
        coordinator: coordinator.clone(),
        settings_path: paths.settings.clone(),
        settings_digest_for: |settings: &Settings| settings.content_digest(),
        shutdown_tx,
        channel: args.channel.clone(),
    });
    let server_handle = {
        let pipe_name = paths.pipe_name.clone();
        tokio::spawn(async move { run_command_server(pipe_name, server_context).await })
    };
    let heartbeat_handle = tokio::spawn(run_heartbeat(
        plane.maintenance.clone(),
        continuity.clone(),
        shared.clone(),
    ));
    let maintenance_handle = tokio::spawn(run_maintenance(plane.maintenance.clone()));
    // Settings 自动对账（R04）：saved-not-applied 的后台重试。
    // 阶段 4.3：reconciler 只调用唯一 Coordinator，不再持有任何通道。
    let reconciler_handle = tokio::spawn(run_settings_reconciler(
        paths.settings.clone(),
        shared.clone(),
        coordinator.clone(),
    ));

    // 阶段 4.5：session/power 事件经统一 bounded bridge + consumer
    // 处理。返回 SessionPowerBridge { consumer, pump, shutdown_tx }。
    #[cfg(windows)]
    let mut lifecycle_bridge =
        match wuji_rebuild_agent::session_power_events::start_session_power_bridge(
            coordinator.clone(),
        ) {
            Ok(bridge) => Some(bridge),
            Err(e) => {
                eprintln!("{e}");
                None
            }
        };
    #[cfg(not(windows))]
    let lifecycle_bridge: Option<wuji_rebuild_agent::session_power_events::SessionPowerBridge> =
        None;

    // 等待退出：agent_shutdown_dev 或 Ctrl-C。
    tokio::select! {
        result = shutdown_rx.changed() => {
            let _ = result;
        }
        result = tokio::signal::ctrl_c() => {
            let _ = result;
        }
    }

    // 正常关闭 session/power 桥接（4.5）：
    // bridge.shutdown() 返回 ShutdownReport：consumer/pump/bridge 状态 + stop 结果
    if let Some(bridge) = lifecycle_bridge.as_mut() {
        let report = bridge.shutdown().await;
        if !report.is_complete() {
            eprintln!("session/power shutdown 不完整: {report:?}");
        }
        for error in &report.errors {
            eprintln!("session/power shutdown: {error}");
        }
    }

    // 关闭序列：经窄通道发送 Shutdown 并等待终态提交（09 §5.2）。
    pipeline_supervisor_handle.abort();
    let _ = plane.maintenance.shutdown().await;
    let (writer, _engine) = writer_handle
        .await
        .map_err(|e| format!("Writer 退出异常: {e}"))?;
    drop(writer);
    capture_handle.abort();
    processor_handle.abort();
    heartbeat_handle.abort();
    maintenance_handle.abort();
    reconciler_handle.abort();
    server_handle.abort();
    // lifecycle bridge 在本作用域末尾才 drop；若 shutdown 报告不完整，未完成句柄
    // 在其余 Agent 关闭期间仍由 bridge 持有，不会在 timeout 点静默 detach。
    Ok(())
}
