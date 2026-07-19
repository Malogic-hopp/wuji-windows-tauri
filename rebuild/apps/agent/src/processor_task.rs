//! ObservationProcessor 任务：读取 capture queue、应用隐私过滤与状态判定、
//! 输出到 writer data lane 前身的 bounded queue（09 §5、§6.1）。
//!
//! 本任务不写数据库、不写原始日志；被排除的进程名只存在于入站消息生命周期内。

use std::sync::Arc;

use tokio::sync::{mpsc, watch};
use wuji_core::pipeline::{ObservationProcessor, ProcessorOutput, RawCapture};
use wuji_core::settings::Settings;

use crate::capture_loop::{ContinuityState, WRITER_DATA_QUEUE_CAPACITY};

/// 启动 Processor 任务；Writer data lane（V01-5）将消费返回的接收端。
/// 队列满时同样 drop-new 并 bump epoch（09 §5.2 writer lane 侧）。
pub fn spawn_observation_processor(
    mut rx: mpsc::Receiver<RawCapture>,
    settings_rx: watch::Receiver<Settings>,
    continuity: Arc<ContinuityState>,
) -> (mpsc::Receiver<ProcessorOutput>, tokio::task::JoinHandle<()>) {
    let (tx, out_rx) = mpsc::channel(WRITER_DATA_QUEUE_CAPACITY);
    let handle = tokio::spawn(async move {
        while let Some(raw) = rx.recv().await {
            let settings = settings_rx.borrow().clone();
            let output = ObservationProcessor::process(raw, &settings);
            match tx.try_send(output) {
                Ok(()) => {}
                Err(mpsc::error::TrySendError::Full(_)) => continuity.note_writer_drop(),
                Err(mpsc::error::TrySendError::Closed(_)) => break,
            }
        }
    });
    (out_rx, handle)
}

#[cfg(test)]
mod tests {
    use super::*;
    use wuji_core::domain::{ActivityState, CaptureQuality};
    use wuji_core::pipeline::IdleReading;

    fn raw(sequence: u64, name: Option<&str>, idle: IdleReading) -> RawCapture {
        RawCapture {
            sequence,
            continuity_epoch: 0,
            captured_at_utc_ms: 1_784_332_800_000,
            captured_monotonic_ms: sequence * 3_000,
            process_file_name: name.map(str::to_string),
            idle,
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
        let (tx, rx) = mpsc::channel(8);
        let (settings_tx, settings_rx) = watch::channel(settings_with_excluded(&["keepass.exe"]));
        let continuity = Arc::new(ContinuityState::default());
        let (mut out_rx, handle) = spawn_observation_processor(rx, settings_rx, continuity);

        tx.send(raw(1, Some("code.exe"), IdleReading::Seconds(5)))
            .await
            .unwrap();
        tx.send(raw(2, Some("keepass.exe"), IdleReading::Seconds(5)))
            .await
            .unwrap();
        tx.send(raw(3, Some("code.exe"), IdleReading::Seconds(600)))
            .await
            .unwrap();
        tx.send(raw(4, Some("code.exe"), IdleReading::Unavailable))
            .await
            .unwrap();
        tx.send(raw(5, None, IdleReading::Seconds(5)))
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

    #[tokio::test(start_paused = true)]
    async fn full_writer_lane_drops_and_bumps_epoch() {
        // 用小容量替换默认 lane 容量以触发 drop。
        let (tx, rx) = mpsc::channel(8);
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
