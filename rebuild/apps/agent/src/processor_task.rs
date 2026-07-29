//! ObservationProcessor 任务：读取 capture queue、应用隐私过滤与状态判定、
//! 输出到 writer data lane 前身的 bounded queue（09 §5、§6.1）。
//!
//! 本任务不写数据库、不写原始日志；被排除的进程名只存在于入站消息生命周期内。

use std::sync::Arc;

use tokio::sync::{mpsc, watch};
use wuji_core::pipeline::{ObservationProcessor, ProcessorOutput};
use wuji_core::settings::Settings;

use crate::capture_loop::{ContinuityState, WRITER_DATA_QUEUE_CAPACITY};

/// 启动 Processor 任务；Writer data lane（V01-5）将消费返回的接收端。
/// 队列满时同样 drop-new 并 bump epoch（09 §5.2 writer lane 侧）。
/// S2-04 返修：Processor 从单个 FIFO 读取 CapturePipelineItem。
/// BarrierToken 原样透传到 writer data lane（阻塞发送，不可丢弃）。
pub fn spawn_observation_processor(
    pipeline_rx: mpsc::Receiver<wuji_core::pipeline::CapturePipelineItem>,
    settings_rx: watch::Receiver<Settings>,
    continuity: Arc<ContinuityState>,
    health: &Arc<crate::pipeline_health::PipelineHealth>,
) -> (mpsc::Receiver<ProcessorOutput>, tokio::task::JoinHandle<()>) {
    spawn_observation_processor_with_capacity(
        pipeline_rx,
        settings_rx,
        continuity,
        WRITER_DATA_QUEUE_CAPACITY,
        health,
    )
}

/// 可注入 writer data lane 容量的版本（测试用小容量；生产用默认值）。
pub fn spawn_observation_processor_with_capacity(
    mut pipeline_rx: mpsc::Receiver<wuji_core::pipeline::CapturePipelineItem>,
    settings_rx: watch::Receiver<Settings>,
    continuity: Arc<ContinuityState>,
    writer_queue_capacity: usize,
    health: &Arc<crate::pipeline_health::PipelineHealth>,
) -> (mpsc::Receiver<ProcessorOutput>, tokio::task::JoinHandle<()>) {
    let (tx, out_rx) = mpsc::channel(writer_queue_capacity);
    // 第二次复审 P1：注册在 tokio::spawn 之前同步完成；guard move 进 future，
    // 即使首次 poll 前 abort 也会随 future 销毁而 Drop → Dead。
    let health_guard = health.register_processor();
    let handle = tokio::spawn(async move {
        let _health_guard = health_guard;
        while let Some(item) = pipeline_rx.recv().await {
            match item {
                wuji_core::pipeline::CapturePipelineItem::Sample(raw) => {
                    continuity.note_capture_dequeue();
                    let settings = settings_rx.borrow().clone();
                    // 阶段 4.4（P1-04）：业务处理前校验 sample revision 与当前
                    // settings watch revision。错配是内部协议不变量破坏——不得
                    // 继续用当前 Settings 处理、不得静默丢弃、不得把 revision
                    // 重标为当前值：发出显式违例消息（与 Barrier 同级的可靠
                    // 阻塞发送，绝不走 try_send 丢弃路径）后退出。Writer 收到
                    // 违例统一 fail-closed；任务退出使 health guard Drop，
                    // pipeline supervisor 以同一 Coordinator 兜底 fail-closed。
                    let sample_rev = raw.settings_revision;
                    let current_rev = settings.revision.parse::<i64>().unwrap_or(-1);
                    if sample_rev != current_rev {
                        let _ = tx
                            .send(ProcessorOutput::SettingsRevisionMismatch {
                                sequence: raw.sequence,
                                continuity_epoch: raw.continuity_epoch,
                                sample_revision: sample_rev,
                                current_revision: current_rev,
                            })
                            .await;
                        break;
                    }
                    let output = ObservationProcessor::process(raw, &settings);
                    match tx.try_send(output) {
                        Ok(()) => continuity.note_writer_enqueue(),
                        Err(mpsc::error::TrySendError::Full(_)) => continuity.note_writer_drop(),
                        Err(mpsc::error::TrySendError::Closed(_)) => break,
                    }
                }
                wuji_core::pipeline::CapturePipelineItem::Barrier(token) => {
                    // Barrier 必须送达 writer（阻塞发送）。
                    if tx.send(ProcessorOutput::Barrier(token)).await.is_err() {
                        break;
                    }
                }
            }
        }
    });
    (out_rx, handle)
}

#[cfg(test)]
mod tests {
    use super::*;
    use wuji_core::domain::{ActivityState, CaptureQuality};
    use wuji_core::pipeline::{IdleReading, RawCapture};

    fn raw(sequence: u64, name: Option<&str>, idle: IdleReading) -> RawCapture {
        RawCapture {
            sequence,
            continuity_epoch: 0,
            captured_at_utc_ms: 1_784_332_800_000,
            captured_monotonic_ms: sequence * 3_000,
            process_file_name: name.map(str::to_string),
            idle,
            settings_revision: 0,
        }
    }

    fn settings_with_excluded(excluded: &[&str]) -> Settings {
        Settings {
            excluded_process_names: excluded.iter().map(|s| s.to_string()).collect(),
            ..Settings::default()
        }
    }

    #[tokio::test]
    async fn processor_filters_states_and_privacy() {
        let (tx, rx) = mpsc::channel::<wuji_core::pipeline::CapturePipelineItem>(8);
        let (settings_tx, settings_rx) = watch::channel(settings_with_excluded(&["keepass.exe"]));
        let continuity = Arc::new(ContinuityState::default());
        let (mut out_rx, handle) = spawn_observation_processor(
            rx,
            settings_rx,
            continuity,
            &crate::pipeline_health::PipelineHealth::new(),
        );

        tx.send(wuji_core::pipeline::CapturePipelineItem::Sample(raw(
            1,
            Some("code.exe"),
            IdleReading::Seconds(5),
        )))
        .await
        .unwrap();
        tx.send(wuji_core::pipeline::CapturePipelineItem::Sample(raw(
            2,
            Some("keepass.exe"),
            IdleReading::Seconds(5),
        )))
        .await
        .unwrap();
        tx.send(wuji_core::pipeline::CapturePipelineItem::Sample(raw(
            3,
            Some("code.exe"),
            IdleReading::Seconds(600),
        )))
        .await
        .unwrap();
        tx.send(wuji_core::pipeline::CapturePipelineItem::Sample(raw(
            4,
            Some("code.exe"),
            IdleReading::Unavailable,
        )))
        .await
        .unwrap();
        tx.send(wuji_core::pipeline::CapturePipelineItem::Sample(raw(
            5,
            None,
            IdleReading::Seconds(5),
        )))
        .await
        .unwrap();
        drop((tx, settings_tx));

        let mut outputs = Vec::new();
        while let Some(output) = out_rx.recv().await {
            outputs.push(output);
        }
        handle.await.unwrap();

        assert_eq!(outputs.len(), 5);
        let ProcessorOutput::Observation(obs) = &outputs[0] else {
            panic!("应为 Observation")
        };
        assert_eq!(obs.activity_state, ActivityState::Active);
        assert_eq!(obs.normalized_process_name, "code.exe");
        assert!(matches!(
            outputs[1],
            ProcessorOutput::PrivacyExcluded { .. }
        ));
        let ProcessorOutput::Observation(obs_idle) = &outputs[2] else {
            panic!("应为 Observation")
        };
        assert_eq!(obs_idle.activity_state, ActivityState::Idle);
        let ProcessorOutput::Observation(obs_unknown) = &outputs[3] else {
            panic!("应为 Observation")
        };
        assert_eq!(obs_unknown.activity_state, ActivityState::Unknown);
        assert_eq!(obs_unknown.quality, CaptureQuality::IdleUnavailable);
        assert!(matches!(outputs[4], ProcessorOutput::CaptureError { .. }));
    }

    /// 阶段 4.4（P1-04）：revision 错配必须显式失败——Processor 发出唯一
    /// SettingsRevisionMismatch 消息后退出；不输出正常数据、不用当前
    /// Settings 误处理、不静默丢弃、不重标 revision。
    #[tokio::test]
    async fn revision_mismatch_emits_violation_and_exits() {
        let (tx, rx) = mpsc::channel::<wuji_core::pipeline::CapturePipelineItem>(8);
        // watch 当前为 revision 1，样本仍携带 revision 0（协议违例注入）。
        let (settings_tx, settings_rx) = watch::channel(Settings {
            revision: "1".to_string(),
            ..Settings::default()
        });
        let continuity = Arc::new(ContinuityState::default());
        let (mut out_rx, handle) = spawn_observation_processor(
            rx,
            settings_rx,
            continuity,
            &crate::pipeline_health::PipelineHealth::new(),
        );

        tx.send(wuji_core::pipeline::CapturePipelineItem::Sample(raw(
            1,
            Some("code.exe"),
            IdleReading::Seconds(5),
        )))
        .await
        .unwrap();
        // 错配后的后续样本绝不产生后续旧 revision 输出。
        tx.send(wuji_core::pipeline::CapturePipelineItem::Sample(raw(
            2,
            Some("code.exe"),
            IdleReading::Seconds(5),
        )))
        .await
        .unwrap();
        drop((tx, settings_tx));

        let first = out_rx.recv().await.expect("违例消息必须送达");
        assert_eq!(
            first,
            ProcessorOutput::SettingsRevisionMismatch {
                sequence: 1,
                continuity_epoch: 0,
                sample_revision: 0,
                current_revision: 1,
            },
            "错配必须产生显式违例消息（仅 sequence/revision 诊断，不含进程名）"
        );
        assert!(
            out_rx.recv().await.is_none(),
            "违例后 Processor 必须退出，不得产生任何后续输出"
        );
        handle.await.unwrap();
    }

    #[tokio::test]
    async fn queue_depth_gauges_track_backlog() {
        let (tx, rx) = mpsc::channel::<wuji_core::pipeline::CapturePipelineItem>(8);
        let (settings_tx, settings_rx) = watch::channel(Settings::default());
        let continuity = Arc::new(ContinuityState::default());
        let (mut out_rx, handle) = spawn_observation_processor(
            rx,
            settings_rx,
            continuity.clone(),
            &crate::pipeline_health::PipelineHealth::new(),
        );

        // 模拟 capture loop：入队 3 条。
        for i in 0..3_u64 {
            continuity.note_capture_enqueue();
            tx.send(wuji_core::pipeline::CapturePipelineItem::Sample(raw(
                i + 1,
                Some("code.exe"),
                IdleReading::Seconds(1),
            )))
            .await
            .unwrap();
        }
        // processor 消费后：capture 深度归零，writer 深度为 3（审核 R09：不再恒 0）。
        let deadline = std::time::Instant::now() + std::time::Duration::from_secs(2);
        while continuity.writer_queue_depth() != 3 {
            assert!(std::time::Instant::now() < deadline, "writer 深度未达到 3");
            tokio::time::sleep(std::time::Duration::from_millis(10)).await;
        }
        assert_eq!(continuity.capture_queue_depth(), 0);

        // writer 侧消费：出队递减；再减不得出现负值。
        for _ in 0..3 {
            out_rx.recv().await.unwrap();
            continuity.note_writer_dequeue();
        }
        assert_eq!(continuity.writer_queue_depth(), 0);
        continuity.note_writer_dequeue();
        assert_eq!(continuity.writer_queue_depth(), 0, "深度不得为负");

        drop((tx, settings_tx));
        handle.await.unwrap();
    }

    #[tokio::test(start_paused = true)]
    async fn full_writer_lane_drops_and_bumps_epoch() {
        // 用小容量替换默认 lane 容量以触发 drop。此测试有自有 inline processor，不用 spawn_observation_processor。
        let (tx, rx) = mpsc::channel::<RawCapture>(8);
        let (settings_tx, settings_rx) = watch::channel(Settings::default());
        let continuity = Arc::new(ContinuityState::default());

        let (out_tx, mut out_rx) = mpsc::channel(1);
        let continuity_task = continuity.clone();
        let processor = tokio::spawn(async move {
            let mut rx = rx;
            while let Some(raw) = rx.recv().await {
                let settings = settings_rx.borrow().clone();
                let output = ObservationProcessor::process(raw, &settings);
                if out_tx.try_send(output).is_err() {
                    continuity_task.note_writer_drop();
                }
            }
        });

        for i in 0..5_u64 {
            tx.send(raw(i + 1, Some("code.exe"), IdleReading::Seconds(1)))
                .await
                .unwrap();
        }
        drop((tx, settings_tx));
        processor.await.unwrap();

        assert!(continuity.dropped_writer_count() >= 1);
        assert_eq!(
            continuity.current_epoch(),
            continuity.dropped_writer_count()
        );
        assert!(out_rx.recv().await.is_some());
    }
}
