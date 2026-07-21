//! MaintenanceLite：定期 WAL checkpoint（09 §5：v0.1 只执行 checkpoint 与安全日志轮换）。

use tokio::sync::mpsc;

use crate::writer_task::WriterControl;

const CHECKPOINT_INTERVAL: std::time::Duration = std::time::Duration::from_secs(60);

pub async fn run_maintenance(control_tx: mpsc::Sender<WriterControl>) {
    let mut ticker = tokio::time::interval(CHECKPOINT_INTERVAL);
    loop {
        ticker.tick().await;
        // 只发送 WriterControl::Checkpoint，不自行打开第二个读写连接（09 §5.2）。
        if control_tx.send(WriterControl::Checkpoint).await.is_err() {
            break;
        }
    }
}
