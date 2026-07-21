//! HealthHeartbeatLoop：每秒向 control lane 提交心跳（09 §5、§5.2）。

use std::sync::Arc;

use tokio::sync::mpsc;

use crate::capture_loop::{ContinuityState, now_utc_ms};
use crate::shared::SharedState;
use crate::writer_task::{HeartbeatSnapshot, WriterControl};

pub async fn run_heartbeat(
    control_tx: mpsc::Sender<WriterControl>,
    continuity: Arc<ContinuityState>,
    shared: Arc<SharedState>,
) {
    let mut ticker = tokio::time::interval(std::time::Duration::from_secs(1));
    loop {
        ticker.tick().await;
        let status = shared.status_dto();
        let snapshot = HeartbeatSnapshot {
            heartbeat_at_utc_ms: now_utc_ms(),
            last_observation_at_utc_ms: status.last_observation_at_utc_ms.map(|v| v.0),
            capture_queue_depth: i64::from(status.capture_queue_depth),
            writer_queue_depth: i64::from(status.writer_queue_depth),
            dropped_capture_count: continuity.dropped_capture_count() as i64,
            dropped_writer_count: continuity.dropped_writer_count() as i64,
            continuity_epoch: continuity.current_epoch() as i64,
        };
        // 控制消息不得 try_send 丢弃（09 §5.2）；写满时等待容量。
        if control_tx
            .send(WriterControl::Heartbeat(snapshot))
            .await
            .is_err()
        {
            break;
        }
    }
}
