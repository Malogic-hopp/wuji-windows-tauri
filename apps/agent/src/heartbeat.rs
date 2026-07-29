//! HealthHeartbeatLoop：每秒向 control lane 提交心跳（09 §5、§5.2）。

use std::sync::Arc;

use crate::capture_loop::{ContinuityState, now_utc_ms};
use crate::control_plane::MaintenanceControl;
use crate::shared::SharedState;
use crate::writer_task::HeartbeatSnapshot;

pub async fn run_heartbeat(
    control: MaintenanceControl,
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
            // 队列深度来自真实原子表（审核 R09），不再回读自身心跳写入的值。
            capture_queue_depth: continuity.capture_queue_depth(),
            writer_queue_depth: continuity.writer_queue_depth(),
            dropped_capture_count: continuity.dropped_capture_count() as i64,
            dropped_writer_count: continuity.dropped_writer_count() as i64,
            continuity_epoch: continuity.current_epoch() as i64,
        };
        // 控制消息不得 try_send 丢弃（09 §5.2）；写满时等待容量。
        if control.heartbeat(snapshot).await.is_err() {
            break;
        }
    }
}
