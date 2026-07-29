//! Settings 自动对账（09 §9.1、审核 R04）：saved-not-applied 的后台重试。
//!
//! 每 2 秒检查一次 settings 文件；仅当文件 revision 严格大于已应用 revision 时
//! 经唯一 CaptureCoordinator 重新应用（与 IPC settings_reload 同一路径、同一
//! transition lock，阶段 4.3）。文件缺失、损坏、revision 降级或 digest 冲突不动作：
//! 保持 last-known-good。

use std::path::PathBuf;
use std::sync::Arc;
use std::time::Duration;

use tokio::sync::mpsc;

use crate::capture_coordinator::CaptureCoordinator;
use crate::capture_loop::now_utc_ms;
use crate::settings_store::{SettingsLoad, load_settings_file};
use crate::shared::SharedState;

const RECONCILE_INTERVAL: Duration = Duration::from_secs(2);

pub async fn run_settings_reconciler(
    path: PathBuf,
    shared: Arc<SharedState>,
    coordinator: Arc<CaptureCoordinator>,
) {
    run_settings_reconciler_with_interval(path, shared, coordinator, RECONCILE_INTERVAL).await
}

/// 可注入间隔的版本（测试用小间隔；生产固定 2 秒）。
/// 阶段 4.3：reconciler 只调用 Coordinator，不再直接注入 Barrier 或发送 WriterControl；
/// 应用失败本周期放弃，下一周期重试（reconciler 是周期任务，天然具备重试）。
pub async fn run_settings_reconciler_with_interval(
    path: PathBuf,
    shared: Arc<SharedState>,
    coordinator: Arc<CaptureCoordinator>,
    reconcile_interval: Duration,
) {
    run_settings_reconciler_observed(path, shared, coordinator, reconcile_interval, None).await
}

/// 与生产 reconciler 完全相同的循环，仅额外在真正调用 Coordinator 前发送
/// attempt rendezvous。供集成测试证明 IPC 与 reconciler 确实并发进入控制面；
/// 生产调用传 None，不引入旁路或不同业务逻辑。
#[doc(hidden)]
pub async fn run_settings_reconciler_observed(
    path: PathBuf,
    shared: Arc<SharedState>,
    coordinator: Arc<CaptureCoordinator>,
    reconcile_interval: Duration,
    attempt_tx: Option<mpsc::UnboundedSender<i64>>,
) {
    let mut interval = tokio::time::interval(reconcile_interval);
    loop {
        interval.tick().await;
        let SettingsLoad::Ready(settings) = load_settings_file(&path) else {
            continue;
        };
        let Ok(revision) = settings.revision.parse::<i64>() else {
            continue;
        };
        if revision <= shared.applied_settings_revision() {
            continue;
        }
        if let Some(attempt_tx) = &attempt_tx {
            let _ = attempt_tx.send(revision);
        }
        // Coordinator 在锁内复检 revision、注入 Barrier、等待 Writer ack 并更新
        // settings watch；失败诊断已由 Coordinator/Writer 按来源设置。
        let _ = coordinator.apply_settings(settings, now_utc_ms()).await;
    }
}
