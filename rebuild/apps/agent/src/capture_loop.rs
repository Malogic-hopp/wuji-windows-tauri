//! ForegroundCaptureLoop：1 秒调度唤醒、按 sampling interval 采样、
//! bounded queue 与 continuity epoch（09 §5、§5.1、§5.2）。
//!
//! 阻塞 Win32 调用通过 spawn_blocking 离开 Tokio worker（09 §3.1）。

use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::Duration;

use tokio::sync::{mpsc, watch};
use tokio::time::Instant;
use wuji_core::pipeline::{IdleReading, RawCapture};
use wuji_core::settings::Settings;

/// 09 §5.1 固定值。
pub const CAPTURE_QUEUE_CAPACITY: usize = 256;
pub const WRITER_DATA_QUEUE_CAPACITY: usize = 512;
pub const CAPTURE_WAKE_INTERVAL: Duration = Duration::from_secs(1);

/// 共享连续性状态：queue drop 必须原子增加 epoch 与对应计数器（09 §5.2）。
#[derive(Debug, Default)]
pub struct ContinuityState {
    epoch: AtomicU64,
    dropped_capture: AtomicU64,
    dropped_writer: AtomicU64,
}

impl ContinuityState {
    pub fn current_epoch(&self) -> u64 {
        self.epoch.load(Ordering::Acquire)
    }

    pub fn dropped_capture_count(&self) -> u64 {
        self.dropped_capture.load(Ordering::Acquire)
    }

    pub fn dropped_writer_count(&self) -> u64 {
        self.dropped_writer.load(Ordering::Acquire)
    }

    pub fn note_capture_drop(&self) {
        self.epoch.fetch_add(1, Ordering::AcqRel);
        self.dropped_capture.fetch_add(1, Ordering::AcqRel);
    }

    pub fn note_writer_drop(&self) {
        self.epoch.fetch_add(1, Ordering::AcqRel);
        self.dropped_writer.fetch_add(1, Ordering::AcqRel);
    }
}

/// 采集来源抽象：真实实现为 wuji-windows 适配器，测试用脚本化 mock。
pub trait CaptureSource: Send + Sync + 'static {
    fn capture(&self) -> RawSample;
}

/// 一次原始采样的字段结果（对应 wuji_windows::ForegroundSample 的形状）。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RawSample {
    pub process_file_name: Option<String>,
    pub idle: IdleReading,
}

/// 调度判定：自上次尝试起经过 sampling interval 才再次采样（纯函数，便于测试）。
pub fn sample_due(now: Instant, last_attempt: Option<Instant>, interval: Duration) -> bool {
    match last_attempt {
        None => true,
        Some(last) => now.duration_since(last) >= interval,
    }
}

pub struct CaptureLoopConfig {
    pub wake_interval: Duration,
    pub queue_capacity: usize,
    /// true：采集调用经 spawn_blocking 离开 Tokio worker（09 §3.1，生产路径）；
    /// 测试置 false 以便 paused 时钟下确定性地驱动循环。
    pub offload_capture: bool,
}

impl Default for CaptureLoopConfig {
    fn default() -> Self {
        Self {
            wake_interval: CAPTURE_WAKE_INTERVAL,
            queue_capacity: CAPTURE_QUEUE_CAPACITY,
            offload_capture: true,
        }
    }
}

/// 运行采集循环，直到输出端被关闭。返回 (capture queue 接收端, join handle)。
pub fn spawn_capture_loop<S: CaptureSource>(
    source: S,
    settings_rx: watch::Receiver<Settings>,
    continuity: Arc<ContinuityState>,
    config: CaptureLoopConfig,
) -> (mpsc::Receiver<RawCapture>, tokio::task::JoinHandle<()>) {
    let source = Arc::new(source);
    let (tx, rx) = mpsc::channel(config.queue_capacity);
    let handle = tokio::spawn(async move {
        let base = Instant::now();
        let mut sequence = 0_u64;
        let mut last_attempt: Option<Instant> = None;
        let mut ticker = tokio::time::interval(config.wake_interval);

        loop {
            ticker.tick().await;
            if tx.is_closed() {
                break;
            }
            let interval =
                Duration::from_secs(u64::from(settings_rx.borrow().sampling_interval_seconds));
            let now = Instant::now();
            if !sample_due(now, last_attempt, interval) {
                continue;
            }
            // Capture Sequence 在捕获尝试开始时分配，失败与丢弃不复用（09 §5）。
            sequence += 1;
            last_attempt = Some(now);

            let sample = if config.offload_capture {
                let source = Arc::clone(&source);
                tokio::task::spawn_blocking(move || source.capture())
                    .await
                    .unwrap_or(RawSample {
                        process_file_name: None,
                        idle: IdleReading::Unavailable,
                    })
            } else {
                source.capture()
            };
            let raw = RawCapture {
                sequence,
                continuity_epoch: continuity.current_epoch(),
                captured_at_utc_ms: now_utc_ms(),
                captured_monotonic_ms: base.elapsed().as_millis() as u64,
                process_file_name: sample.process_file_name,
                idle: sample.idle,
            };
            match tx.try_send(raw) {
                Ok(()) => {}
                Err(mpsc::error::TrySendError::Full(_)) => {
                    // drop-new + 原子 epoch/计数器（09 §5.2）。
                    continuity.note_capture_drop();
                }
                Err(mpsc::error::TrySendError::Closed(_)) => break,
            }
        }
    });
    (rx, handle)
}

/// UTC 毫秒（采集事实时间；测试用 paused tokio 时钟驱动 monotonic，不驱动本值）。
pub fn now_utc_ms() -> i64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_millis() as i64)
        .unwrap_or(0)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::VecDeque;
    use std::sync::Mutex;

    struct MockSource {
        samples: Mutex<VecDeque<RawSample>>,
    }

    impl MockSource {
        fn with(samples: Vec<RawSample>) -> Self {
            Self {
                samples: Mutex::new(samples.into()),
            }
        }
    }

    impl CaptureSource for MockSource {
        fn capture(&self) -> RawSample {
            self.samples
                .lock()
                .unwrap()
                .pop_front()
                .unwrap_or(RawSample {
                    process_file_name: Some("code.exe".to_string()),
                    idle: IdleReading::Seconds(0),
                })
        }
    }

    fn settings_watch(interval_seconds: u32) -> watch::Receiver<Settings> {
        let (tx, rx) = watch::channel(Settings {
            sampling_interval_seconds: interval_seconds,
            ..Settings::default()
        });
        drop(tx);
        rx
    }

    #[test]
    fn sample_due_rules() {
        let t0 = Instant::now();
        assert!(sample_due(t0, None, Duration::from_secs(3)));
        assert!(!sample_due(
            t0 + Duration::from_secs(2),
            Some(t0),
            Duration::from_secs(3)
        ));
        assert!(sample_due(
            t0 + Duration::from_secs(3),
            Some(t0),
            Duration::from_secs(3)
        ));
    }

    #[tokio::test(start_paused = true)]
    async fn sequence_monotonic_and_interval_respected() {
        let continuity = Arc::new(ContinuityState::default());
        let (mut rx, handle) = spawn_capture_loop(
            MockSource::with(vec![]),
            settings_watch(3),
            continuity,
            CaptureLoopConfig {
                wake_interval: Duration::from_millis(100),
                queue_capacity: 16,
                offload_capture: false,
            },
        );

        let first = rx.recv().await.expect("首条采样");
        assert_eq!(first.sequence, 1);
        assert_eq!(first.continuity_epoch, 0);

        // 间隔 3 秒：100ms 唤醒下，第二个样本不会立刻出现。
        let second = tokio::time::timeout(Duration::from_millis(500), rx.recv()).await;
        assert!(second.is_err(), "未到 sampling interval 不得产生第二条");

        tokio::time::advance(Duration::from_secs(3)).await;
        let second = tokio::time::timeout(Duration::from_millis(100), rx.recv())
            .await
            .expect("到达间隔后的第二条")
            .expect("第二条样本");
        assert_eq!(second.sequence, 2);
        assert!(second.captured_monotonic_ms >= first.captured_monotonic_ms);
        drop(rx);
        handle.await.unwrap();
    }

    #[tokio::test(start_paused = true)]
    async fn full_queue_drops_new_and_bumps_epoch() {
        let continuity = Arc::new(ContinuityState::default());
        let (mut rx, handle) = spawn_capture_loop(
            MockSource::with(vec![]),
            settings_watch(1),
            continuity.clone(),
            CaptureLoopConfig {
                wake_interval: Duration::from_millis(50),
                queue_capacity: 1,
                offload_capture: false,
            },
        );

        // 容量 1：第一条消费后，第二条占住队列，第三条起全部 drop-new。
        let first = rx.recv().await.expect("首条入队");
        assert_eq!(first.continuity_epoch, 0);

        // 第二次采样入队（队列空），第三次、第四次因队列满被丢弃。
        tokio::time::advance(Duration::from_millis(1_100)).await;
        tokio::task::yield_now().await;
        tokio::time::advance(Duration::from_millis(1_100)).await;
        tokio::task::yield_now().await;
        tokio::time::advance(Duration::from_millis(1_100)).await;
        tokio::task::yield_now().await;

        let dropped = continuity.dropped_capture_count();
        assert!(dropped >= 1, "队列满必须累计 drop 计数，实际 {dropped}");
        assert_eq!(
            continuity.current_epoch(),
            dropped,
            "epoch 必须等于 drop 次数"
        );

        // 取出滞留消息后，下一条新样本必须携带累计后的 epoch。
        let _stale = rx.recv().await.expect("滞留样本");
        tokio::time::advance(Duration::from_millis(1_100)).await;
        let fresh = tokio::time::timeout(Duration::from_secs(2), rx.recv())
            .await
            .expect("消费后应有新样本")
            .expect("新样本");
        assert_eq!(
            fresh.continuity_epoch,
            continuity.dropped_capture_count(),
            "新样本必须携带 drop 后的最新 epoch"
        );
        assert!(fresh.continuity_epoch >= 1);
        drop(rx);
        let _ = handle.await;
    }
}
