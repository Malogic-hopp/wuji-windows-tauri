//! ForegroundCaptureLoop：1 秒调度唤醒、按 sampling interval 采样、
//! bounded queue 与 continuity epoch（09 §5、§5.1、§5.2）。
//!
//! 阻塞 Win32 调用通过 spawn_blocking 离开 Tokio worker（09 §3.1）。

use std::sync::Arc;
use std::sync::atomic::{AtomicI64, AtomicU64, Ordering};
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
/// latest_sequence 为生命周期 watermark 提供"冻结时刻"（R03）。
#[derive(Debug, Default)]
pub struct ContinuityState {
    epoch: AtomicU64,
    dropped_capture: AtomicU64,
    dropped_writer: AtomicU64,
    latest_sequence: AtomicU64,
    /// capture→processor 队列当前积压（真实表：入队 +1、出队 -1，审核 R09）。
    capture_queue_depth: AtomicI64,
    /// processor→writer 队列当前积压。
    writer_queue_depth: AtomicI64,
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

    /// 生命周期命令接受时的冻结水位：所有 seq <= 该值的采样都属于控制命令之前（R03）。
    pub fn latest_sequence(&self) -> u64 {
        self.latest_sequence.load(Ordering::Acquire)
    }

    pub fn store_sequence(&self, sequence: u64) {
        self.latest_sequence.store(sequence, Ordering::Release);
    }

    pub fn capture_queue_depth(&self) -> i64 {
        self.capture_queue_depth.load(Ordering::Acquire)
    }

    pub fn writer_queue_depth(&self) -> i64 {
        self.writer_queue_depth.load(Ordering::Acquire)
    }

    pub fn note_capture_enqueue(&self) {
        self.capture_queue_depth.fetch_add(1, Ordering::AcqRel);
    }

    pub fn note_capture_dequeue(&self) {
        // 防御负值（异常时序下宁可显示 0，不显示负深度）。
        let _ = self
            .capture_queue_depth
            .fetch_update(Ordering::AcqRel, Ordering::Acquire, |v| {
                Some((v - 1).max(0))
            });
    }

    pub fn note_writer_enqueue(&self) {
        self.writer_queue_depth.fetch_add(1, Ordering::AcqRel);
    }

    pub fn note_writer_dequeue(&self) {
        let _ = self
            .writer_queue_depth
            .fetch_update(Ordering::AcqRel, Ordering::Acquire, |v| {
                Some((v - 1).max(0))
            });
    }

    pub fn note_capture_drop(&self) {
        self.epoch.fetch_add(1, Ordering::AcqRel);
        self.dropped_capture.fetch_add(1, Ordering::AcqRel);
    }

    pub fn note_writer_drop(&self) {
        self.epoch.fetch_add(1, Ordering::AcqRel);
        self.dropped_writer.fetch_add(1, Ordering::AcqRel);
    }

    /// Writer 侧时钟异常增加 epoch（09 §6.5 第 5 条）：不累计 drop 计数。
    pub fn bump_epoch(&self) {
        self.epoch.fetch_add(1, Ordering::AcqRel);
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
    /// UTC 毫秒时钟（Observation 事实时间戳来源）。生产为系统时钟
    /// （`now_utc_ms`）；测试注入确定性时钟以精确控制样本时间戳，
    /// 保证引擎 gap/阈值语义可确定性验证（阶段 4.4）。
    pub utc_now_ms: std::sync::Arc<dyn Fn() -> i64 + Send + Sync>,
}

impl Default for CaptureLoopConfig {
    fn default() -> Self {
        Self {
            wake_interval: CAPTURE_WAKE_INTERVAL,
            queue_capacity: CAPTURE_QUEUE_CAPACITY,
            offload_capture: true,
            utc_now_ms: std::sync::Arc::new(now_utc_ms),
        }
    }
}

/// S2-04 返修：Capture Loop 是 CapturePipelineItem FIFO 的唯一生产者。
/// 正常采样产生 Sample；barrier 请求到达时（biased select 优先），
/// 丢弃 in-flight 样本（若状态已变），再将 Barrier 写入同一 FIFO。
/// 阶段 4.2：Barrier 只有在 FIFO 写入成功后才发送 injected ack；
/// 写入失败显式返回 Closed 并退出（等待方收到稳定失败）。
pub fn spawn_capture_loop<S: CaptureSource>(
    source: S,
    settings_rx: watch::Receiver<Settings>,
    capture_state_rx: watch::Receiver<wuji_core::domain::CaptureState>,
    continuity: Arc<ContinuityState>,
    config: CaptureLoopConfig,
    mut barrier_request_rx: mpsc::Receiver<crate::barrier::BarrierRequest>,
    health: &Arc<crate::pipeline_health::PipelineHealth>,
) -> (
    mpsc::Receiver<wuji_core::pipeline::CapturePipelineItem>,
    tokio::task::JoinHandle<()>,
) {
    let source = Arc::new(source);
    let (tx, rx) = mpsc::channel(config.queue_capacity);
    // 第二次复审 P1：注册在 tokio::spawn 之前同步完成；guard move 进 future，
    // 即使首次 poll 前 abort 也会随 future 销毁而 Drop → Dead。
    let health_guard = health.register_capture();
    let handle = tokio::spawn(async move {
        let _health_guard = health_guard;
        let base = Instant::now();
        let mut sequence = 0_u64;
        let mut last_attempt: Option<Instant> = None;
        let mut ticker = tokio::time::interval(config.wake_interval);

        loop {
            if tx.is_closed() {
                break;
            }
            tokio::select! {
                biased;
                // Barrier 请求优先：避免采样超车。
                request = barrier_request_rx.recv() => {
                    match request {
                        Some(request) => {
                            // 只在 FIFO 写入成功后确认（阶段 4.2）。
                            if tx
                                .send(wuji_core::pipeline::CapturePipelineItem::Barrier(
                                    request.token,
                                ))
                                .await
                                .is_ok()
                            {
                                let _ = request.injected_ack.send(Ok(()));
                            } else {
                                let _ = request
                                    .injected_ack
                                    .send(Err(crate::barrier::BarrierInjectError::Closed));
                                break;
                            }
                        }
                        None => break,
                    }
                }
                _ = ticker.tick() => {
                    if *capture_state_rx.borrow() != wuji_core::domain::CaptureState::Running {
                        continue;
                    }
                    let interval =
                        Duration::from_secs(u64::from(settings_rx.borrow().sampling_interval_seconds));
                    let now = Instant::now();
                    if !sample_due(now, last_attempt, interval) {
                        continue;
                    }
                    sequence += 1;
                    continuity.store_sequence(sequence);
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
                    // 状态在捕获期间改变（Pause/Stop）：S2-04 返修明确丢弃 in-flight 样本。
                    if *capture_state_rx.borrow() != wuji_core::domain::CaptureState::Running {
                        continue;
                    }
                    let settings_revision = settings_rx.borrow().revision.parse::<i64>().unwrap_or(0);
                    let raw = RawCapture {
                        sequence,
                        continuity_epoch: continuity.current_epoch(),
                        captured_at_utc_ms: (config.utc_now_ms)(),
                        captured_monotonic_ms: base.elapsed().as_millis() as u64,
                        process_file_name: sample.process_file_name,
                        idle: sample.idle,
                        settings_revision,
                    };
                    match tx.try_send(wuji_core::pipeline::CapturePipelineItem::Sample(raw)) {
                        Ok(()) => {
                            continuity.note_capture_enqueue();
                        }
                        Err(mpsc::error::TrySendError::Full(_)) => {
                            continuity.note_capture_drop();
                        }
                        Err(mpsc::error::TrySendError::Closed(_)) => break,
                    }
                }
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

    /// S2-04 返修：从 CapturePipelineItem 中提取 RawCapture（测试辅助）。
    fn unpack_sample(item: wuji_core::pipeline::CapturePipelineItem) -> RawCapture {
        match item {
            wuji_core::pipeline::CapturePipelineItem::Sample(raw) => raw,
            wuji_core::pipeline::CapturePipelineItem::Barrier(_) => {
                panic!("测试中不应出现 Barrier")
            }
        }
    }

    fn running_watch() -> watch::Receiver<wuji_core::domain::CaptureState> {
        let (tx, rx) = watch::channel(wuji_core::domain::CaptureState::Running);
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
        let (_barrier_req_tx, barrier_req_rx) = crate::barrier::barrier_request_channel(4);
        let (mut rx, handle) = spawn_capture_loop(
            MockSource::with(vec![]),
            settings_watch(3),
            running_watch(),
            continuity,
            CaptureLoopConfig {
                wake_interval: Duration::from_millis(100),
                queue_capacity: 16,
                offload_capture: false,
                ..CaptureLoopConfig::default()
            },
            barrier_req_rx,
            &crate::pipeline_health::PipelineHealth::new(),
        );

        let first = unpack_sample(rx.recv().await.expect("首条采样"));
        assert_eq!(first.sequence, 1);
        assert_eq!(first.continuity_epoch, 0);

        // 间隔 3 秒：100ms 唤醒下，第二个样本不会立刻出现。
        let second = tokio::time::timeout(Duration::from_millis(500), rx.recv()).await;
        assert!(second.is_err(), "未到 sampling interval 不得产生第二条");

        tokio::time::advance(Duration::from_secs(3)).await;
        let second = unpack_sample(
            tokio::time::timeout(Duration::from_millis(100), rx.recv())
                .await
                .expect("到达间隔后的第二条")
                .expect("第二条样本"),
        );
        assert_eq!(second.sequence, 2);
        assert!(second.captured_monotonic_ms >= first.captured_monotonic_ms);
        drop(rx);
        handle.await.unwrap();
    }

    #[tokio::test(start_paused = true)]
    async fn full_queue_drops_new_and_bumps_epoch() {
        let continuity = Arc::new(ContinuityState::default());
        let (_barrier_req_tx, barrier_req_rx) = crate::barrier::barrier_request_channel(4);
        let (mut rx, handle) = spawn_capture_loop(
            MockSource::with(vec![]),
            settings_watch(1),
            running_watch(),
            continuity.clone(),
            CaptureLoopConfig {
                wake_interval: Duration::from_millis(50),
                queue_capacity: 1,
                offload_capture: false,
                ..CaptureLoopConfig::default()
            },
            barrier_req_rx,
            &crate::pipeline_health::PipelineHealth::new(),
        );

        // 容量 1：第一条消费后，第二条占住队列，第三条起全部 drop-new。
        let first = unpack_sample(rx.recv().await.expect("首条入队"));
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
        let _stale = unpack_sample(rx.recv().await.expect("滞留样本"));
        tokio::time::advance(Duration::from_millis(1_100)).await;
        let fresh_raw = tokio::time::timeout(Duration::from_secs(2), rx.recv())
            .await
            .expect("消费后应有新样本")
            .expect("新样本");
        let fresh = unpack_sample(fresh_raw);
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
