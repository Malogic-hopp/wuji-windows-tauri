//! WUJI Rebuild v0.1 Rust Agent 二进制入口（09 §5、§9.3）。
//!
//! 启动顺序：参数/channel 验证 → 单实例 → 路径解析 → 打开或创建数据库 →
//! 启动恢复 → Settings 对账 → 采集/Processor/Writer/IPC/心跳/维护。

use std::sync::Arc;

use tokio::sync::{mpsc, watch};
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
use wuji_rebuild_agent::settings_store::{SettingsLoad, load_settings_file};
use wuji_rebuild_agent::shared::SharedState;
use wuji_rebuild_agent::writer_task::{WriterControl, WriterTask};
use wuji_storage::Writer;

const AGENT_VERSION: &str = env!("CARGO_PKG_VERSION");
const CONTROL_LANE_CAPACITY: usize = 64;

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

    // Settings 加载与对账（09 §9.1）：缺失用 revision 0 默认值；非法保留默认值并上报。
    let (settings, settings_invalid) = match load_settings_file(&paths.settings) {
        SettingsLoad::Missing => (Settings::default(), None),
        SettingsLoad::Ready(settings) => (settings, None),
        SettingsLoad::Invalid(message) => (Settings::default(), Some(message)),
    };

    let continuity = Arc::new(ContinuityState::default());
    let runtime_id = RuntimeId::new();
    let shared = Arc::new(SharedState::new(
        AGENT_VERSION.to_string(),
        runtime_id.clone(),
    ));
    if let Some(message) = settings_invalid {
        shared.set_safe_error(Some(wuji_core::error::SafeErrorCode::SettingsInvalid));
        eprintln!("settings 无效，使用内建默认值: {message}");
    }

    let mut engine = ActivityEngine::new(runtime_id, settings.clone(), continuity.clone())
        .map_err(|e| format!("引擎初始化失败: {e}"))?;
    engine
        .recover_startup(&mut writer, now)
        .map_err(|e| format!("启动恢复失败: {e}"))?;
    if settings.revision != wuji_core::settings::DEFAULT_REVISION {
        engine
            .apply_settings(&mut writer, settings.clone(), now)
            .map_err(|e| format!("settings 对账失败: {e}"))?;
    }

    // 初始 capture state：仅 --capture-on-start 直接进入 Running（09 §9.3）。
    let initial_capture = if args.capture_on_start {
        CaptureState::Running
    } else {
        CaptureState::Stopped
    };
    shared.set_capture_state(initial_capture);
    shared.set_process_state(ProcessState::Running);

    let (settings_tx, settings_rx) = watch::channel(settings);
    let (capture_state_tx, capture_state_rx) = watch::channel(initial_capture);
    let (control_tx, control_rx) = mpsc::channel::<WriterControl>(CONTROL_LANE_CAPACITY);
    let (shutdown_tx, mut shutdown_rx) = watch::channel(false);

    // 采集 → Processor → Writer lanes。
    let (capture_rx, capture_handle) = spawn_capture_loop(
        Win32CaptureSource,
        settings_rx.clone(),
        capture_state_rx,
        continuity.clone(),
        CaptureLoopConfig::default(),
    );
    let (processor_rx, processor_handle) =
        spawn_observation_processor(capture_rx, settings_rx, continuity.clone());

    let writer_task = WriterTask::new(writer, engine, shared.clone(), capture_state_tx.clone());
    let writer_handle =
        tokio::spawn(async move { writer_task.run(processor_rx, control_rx).await });

    // IPC、心跳、维护。
    let server_context = Arc::new(CommandServerContext {
        shared: shared.clone(),
        control_tx: control_tx.clone(),
        capture_state_tx,
        settings_tx,
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
        control_tx.clone(),
        continuity.clone(),
        shared.clone(),
    ));
    let maintenance_handle = tokio::spawn(run_maintenance(control_tx.clone()));

    // 等待退出：agent_shutdown_dev 或 Ctrl-C。
    tokio::select! {
        result = shutdown_rx.changed() => {
            let _ = result;
        }
        result = tokio::signal::ctrl_c() => {
            let _ = result;
        }
    }

    // 关闭序列：经 control lane 发送 Shutdown 并等待终态提交（09 §5.2）。
    {
        let (ack_tx, ack_rx) = tokio::sync::oneshot::channel();
        if control_tx
            .send(WriterControl::Shutdown { ack: ack_tx })
            .await
            .is_ok()
        {
            let _ = ack_rx.await;
        }
    }
    drop(control_tx);
    let (writer, _engine) = writer_handle
        .await
        .map_err(|e| format!("Writer 退出异常: {e}"))?;
    drop(writer);
    capture_handle.abort();
    processor_handle.abort();
    heartbeat_handle.abort();
    maintenance_handle.abort();
    server_handle.abort();
    Ok(())
}
