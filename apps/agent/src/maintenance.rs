//! MaintenanceLite：定期 WAL checkpoint（09 §5：v0.1 只执行 checkpoint 与安全日志轮换）。

use crate::control_plane::MaintenanceControl;

const CHECKPOINT_INTERVAL: std::time::Duration = std::time::Duration::from_secs(60);

pub async fn run_maintenance(control: MaintenanceControl) {
    let mut ticker = tokio::time::interval(CHECKPOINT_INTERVAL);
    loop {
        ticker.tick().await;
        // 只发送 Checkpoint，不自行打开第二个读写连接（09 §5.2）。
        if control.checkpoint().await.is_err() {
            break;
        }
    }
}
