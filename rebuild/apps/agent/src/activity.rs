//! Activity/Work 精确状态机（09 §6.5、§6.6、§6.7）。
//!
//! 每个事件一个 Writer 事务：Observation/Segment/Work/gap/projection 同事务提交（09 §7.3）。
//! 本模块只做判定，存储不变量由 wuji-storage 执行；隐私过滤已在 Processor 完成。

use std::sync::Arc;

use wuji_core::domain::{ActivityState, GapKind};
use wuji_core::dto::{LocalDate, RuntimeId};
use wuji_core::pipeline::FilteredObservation;
use wuji_core::settings::Settings;
use wuji_storage::error::{Result, StorageError};
use wuji_storage::timeutil::local_date_of;
use wuji_storage::{ObservationInsert, Writer};

use crate::capture_loop::ContinuityState;

/// 09 §6.5 第 5 条：UTC 与 monotonic delta 的允许偏差。
const CLOCK_SKEW_TOLERANCE_MS: i64 = 2_000;

/// 引擎输入事件（Processor 输出与生命周期边界的统一表示）。
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum EngineEvent {
    Observation(FilteredObservation),
    /// 命中排除规则（09 §6.1：不写 Observation，只写不含 App 信息的 gap）。
    PrivacyExcluded {
        captured_at_utc_ms: i64,
    },
    /// 无法得到非空进程文件名（09 §6.1：capture_error gap，不含进程信息）。
    CaptureError {
        captured_at_utc_ms: i64,
    },
    CapturePaused {
        at_utc_ms: i64,
    },
    CaptureStopped {
        at_utc_ms: i64,
    },
    SystemSleep {
        at_utc_ms: i64,
    },
    SessionLocked {
        at_utc_ms: i64,
    },
    /// 受控退出：关闭 open 行，不新开 gap，不补算。
    Shutdown {
        at_utc_ms: i64,
    },
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Boundary {
    QueueDrop(GapKind),
    ClockChanged,
    CaptureDelayed,
}

#[derive(Debug, Clone, Copy)]
struct SegmentState {
    segment_id: i64,
    app_id: i64,
    activity_state: ActivityState,
    continuity_epoch: u64,
    start_at_utc_ms: i64,
    end_at_utc_ms: i64,
}

#[derive(Debug, Clone, Copy)]
struct WorkState {
    work_block_id: i64,
    end_at_utc_ms: i64,
    active_duration_ms: i64,
    short_idle_duration_ms: i64,
    last_segment_id: i64,
}

/// 引擎可回滚状态快照（09 §5.2 busy 重试）。
#[derive(Debug, Clone, Copy)]
pub struct EngineSnapshot {
    open_segment: Option<SegmentState>,
    open_work: Option<WorkState>,
    pending_idle_start_ms: Option<i64>,
    pending_idle_ms: i64,
    open_gap_kind: Option<GapKind>,
    last_point: Option<(i64, u64)>,
    last_message_epoch: Option<u64>,
    last_capture_drops: u64,
    last_writer_drops: u64,
}

/// Activity/Work 状态机。open 行的内存镜像 + 规则判定；DB 是唯一真相。
pub struct ActivityEngine {
    runtime_id: RuntimeId,
    settings: Settings,
    gap_cap_ms: i64,
    work_break_ms: i64,
    settings_revision: i64,
    tz: Option<chrono_tz::Tz>,
    continuity: Arc<ContinuityState>,
    open_segment: Option<SegmentState>,
    open_work: Option<WorkState>,
    /// pending Idle：起点（首个 idle Segment 开始）与已累计可靠 idle 毫秒。
    pending_idle_start_ms: Option<i64>,
    pending_idle_ms: i64,
    open_gap_kind: Option<GapKind>,
    /// 最后一条已处理 Observation 的 (utc_ms, monotonic_ms)；边界后清空。
    last_point: Option<(i64, u64)>,
    last_message_epoch: Option<u64>,
    last_capture_drops: u64,
    last_writer_drops: u64,
}

impl ActivityEngine {
    pub fn new(
        runtime_id: RuntimeId,
        settings: Settings,
        continuity: Arc<ContinuityState>,
    ) -> Result<Self> {
        let gap_cap_ms = (i64::from(settings.sampling_interval_seconds) * 3_000).max(15_000);
        let work_break_ms = i64::from(settings.work_break_idle_seconds) * 1_000;
        let settings_revision = settings
            .revision
            .parse::<i64>()
            .map_err(|_| StorageError::internal("settings revision 非数字"))?;
        Ok(Self {
            runtime_id,
            settings,
            gap_cap_ms,
            work_break_ms,
            settings_revision,
            tz: None,
            continuity,
            open_segment: None,
            open_work: None,
            pending_idle_start_ms: None,
            pending_idle_ms: 0,
            open_gap_kind: None,
            last_point: None,
            last_message_epoch: None,
            last_capture_drops: 0,
            last_writer_drops: 0,
        })
    }

    pub fn runtime_id(&self) -> &RuntimeId {
        &self.runtime_id
    }

    /// 应用新 Settings（09 §9.1、§5.1）：先提交 settings_revisions，再切换内存值；
    /// 设置只影响未来数据，open 行与 gap 状态不变。
    /// 重启对账幂等：revision 已存在且 digest 相同则跳过插入；digest 冲突返回 SETTINGS_CONFLICT。
    pub fn apply_settings(
        &mut self,
        writer: &mut Writer,
        settings: Settings,
        applied_at_utc_ms: i64,
    ) -> Result<()> {
        let revision = settings
            .revision
            .parse::<i64>()
            .map_err(|_| StorageError::internal("settings revision 非数字"))?;
        {
            let tx = writer.transaction()?;
            let outcome = tx.ensure_settings_revision(
                revision,
                &settings.content_digest(),
                applied_at_utc_ms,
            )?;
            if outcome == wuji_storage::writer::SettingsRevisionOutcome::ConflictDigest {
                return Err(StorageError::new(
                    wuji_core::error::SafeErrorCode::SettingsConflict,
                    "设置文件与已应用 revision 摘要冲突",
                ));
            }
            tx.commit()?;
        }
        self.gap_cap_ms = (i64::from(settings.sampling_interval_seconds) * 3_000).max(15_000);
        self.work_break_ms = i64::from(settings.work_break_idle_seconds) * 1_000;
        self.settings_revision = revision;
        self.settings = settings;
        Ok(())
    }

    /// 故障重试用的引擎状态快照（09 §5.2 busy 重试：失败后整体回滚内存镜像）。
    pub fn snapshot(&self) -> EngineSnapshot {
        EngineSnapshot {
            open_segment: self.open_segment,
            open_work: self.open_work,
            pending_idle_start_ms: self.pending_idle_start_ms,
            pending_idle_ms: self.pending_idle_ms,
            open_gap_kind: self.open_gap_kind,
            last_point: self.last_point,
            last_message_epoch: self.last_message_epoch,
            last_capture_drops: self.last_capture_drops,
            last_writer_drops: self.last_writer_drops,
        }
    }

    pub fn restore(&mut self, snapshot: &EngineSnapshot) {
        self.open_segment = snapshot.open_segment;
        self.open_work = snapshot.open_work;
        self.pending_idle_start_ms = snapshot.pending_idle_start_ms;
        self.pending_idle_ms = snapshot.pending_idle_ms;
        self.open_gap_kind = snapshot.open_gap_kind;
        self.last_point = snapshot.last_point;
        self.last_message_epoch = snapshot.last_message_epoch;
        self.last_capture_drops = snapshot.last_capture_drops;
        self.last_writer_drops = snapshot.last_writer_drops;
    }

    /// 启动恢复（09 §6.7 + gap 叠加规则）：关闭遗留 open 行、开 agent_restart gap。
    /// 遗留 open gap 按原 kind 在该 runtime 最后已提交写入时刻关闭。
    pub fn recover_startup(&mut self, writer: &mut Writer, now_utc_ms: i64) -> Result<()> {
        self.ensure_tz(writer)?;
        let legacy = writer.latest_runtime()?;
        let legacy_open_gap = writer.find_open_gap()?;
        let legacy_open_segment = writer.find_open_segment()?;
        let legacy_open_work = writer.find_open_work_block()?;

        // 参考时刻：旧 runtime 最后已提交写入 > 心跳，且不得早于任何 open 行
        // 已提交的端点（open gap 自身的 start 也是已提交写入）。
        let mut reference = legacy
            .as_ref()
            .map(|r| r.last_write_at_utc_ms.unwrap_or(r.heartbeat_at_utc_ms))
            .unwrap_or(now_utc_ms);
        if let Some(segment) = &legacy_open_segment {
            reference = reference.max(segment.end_at_utc_ms);
        }
        if let Some(gap) = &legacy_open_gap {
            reference = reference.max(gap.start_at_utc_ms);
        }

        let mut touched: Option<(i64, i64)> = None;
        {
            let tx = writer.transaction()?;
            if let Some(gap) = &legacy_open_gap {
                tx.close_open_gap(reference)?;
                touched = Some((gap.start_at_utc_ms, reference));
            }
            if legacy_open_segment.is_some() {
                tx.close_open_segment("agent_restart")?;
            }
            if legacy_open_work.is_some() {
                tx.close_open_work_block("agent_restart")?;
            }
            if let Some(legacy) = legacy.as_ref().filter(|r| r.ended_at_utc_ms.is_none()) {
                let legacy_id = RuntimeId::parse(&legacy.runtime_id)
                    .map_err(|e| StorageError::internal(e.message))?;
                tx.mark_runtime_ended(&legacy_id, legacy.heartbeat_at_utc_ms)?;
            }
            tx.insert_runtime(&self.runtime_id, now_utc_ms)?;
            tx.open_gap(&self.runtime_id, GapKind::AgentRestart, reference)?;
            self.open_gap_kind = Some(GapKind::AgentRestart);
            if let Some((start, end)) = touched {
                self.recompute_touched(&tx, start, end)?;
            }
            tx.commit()?;
        }
        Ok(())
    }

    /// 处理一个事件；内部单事务提交。
    pub fn handle(&mut self, writer: &mut Writer, event: EngineEvent) -> Result<()> {
        self.ensure_tz(writer)?;
        match event {
            EngineEvent::Observation(obs) => self.on_observation(writer, &obs),
            EngineEvent::PrivacyExcluded { captured_at_utc_ms } => self.on_gap_event(
                writer,
                captured_at_utc_ms,
                "privacy_excluded",
                "privacy_excluded",
                GapKind::PrivacyExcluded,
                false,
            ),
            EngineEvent::CaptureError { captured_at_utc_ms } => self.on_gap_event(
                writer,
                captured_at_utc_ms,
                "capture_error",
                "capture_error",
                GapKind::CaptureError,
                false,
            ),
            EngineEvent::CapturePaused { at_utc_ms } => self.on_gap_event(
                writer,
                at_utc_ms,
                "capture_paused",
                "capture_paused",
                GapKind::CapturePaused,
                false,
            ),
            EngineEvent::CaptureStopped { at_utc_ms } => self.on_gap_event(
                writer,
                at_utc_ms,
                "capture_stopped",
                "capture_stopped",
                GapKind::CaptureStopped,
                false,
            ),
            EngineEvent::SystemSleep { at_utc_ms } => self.on_gap_event(
                writer,
                at_utc_ms,
                "system_sleep",
                "system_sleep",
                GapKind::SystemSleep,
                true,
            ),
            EngineEvent::SessionLocked { at_utc_ms } => self.on_gap_event(
                writer,
                at_utc_ms,
                "session_locked",
                "session_locked",
                GapKind::SessionLocked,
                true,
            ),
            EngineEvent::Shutdown { at_utc_ms } => self.on_shutdown(writer, at_utc_ms),
        }
    }

    fn on_observation(&mut self, writer: &mut Writer, obs: &FilteredObservation) -> Result<()> {
        let tx = writer.transaction()?;

        // 有效 Observation 首先关闭任何 open gap（09 §6.7）。
        if self.open_gap_kind.is_some() {
            tx.close_open_gap(obs.captured_at_utc_ms)?;
            self.open_gap_kind = None;
        }

        // App Identity upsert（first/last seen 由 MIN/MAX 保护）。
        let app_id = tx.upsert_app_identity(
            &obs.app_key,
            &obs.display_name,
            &obs.normalized_process_name,
            obs.captured_at_utc_ms,
        )?;

        // 插入 Observation；重放命中唯一约束 → 不再处理（09 §7.3）。
        let insert = tx.insert_observation(
            &self.runtime_id,
            obs.sequence as i64,
            obs.continuity_epoch as i64,
            obs.captured_at_utc_ms,
            obs.captured_monotonic_ms as i64,
            app_id,
            obs.activity_state,
            obs.quality,
            self.settings_revision,
        )?;
        let observation_id = match insert {
            ObservationInsert::Inserted(id) => id,
            ObservationInsert::AlreadyProcessed => {
                tx.commit()?;
                return Ok(());
            }
        };

        let boundary = self.classify_boundary(obs);
        let touched = match boundary {
            Some(Boundary::QueueDrop(kind)) => {
                self.close_rows(&tx, "queue_drop", "queue_drop")?;
                let start = self
                    .last_point
                    .map(|(utc, _)| utc)
                    .unwrap_or(obs.captured_at_utc_ms);
                tx.insert_closed_gap(&self.runtime_id, kind, start, obs.captured_at_utc_ms)?;
                self.last_point = None;
                Some((start, obs.captured_at_utc_ms))
            }
            Some(Boundary::ClockChanged) => {
                self.close_rows(&tx, "clock_changed", "clock_changed")?;
                let start = self
                    .last_point
                    .map(|(utc, _)| utc)
                    .unwrap_or(obs.captured_at_utc_ms);
                // UTC 回拨时保存旧端点上的零长度 gap，避免负区间（09 §6.5 第 5 条）。
                let (gs, ge) = if obs.captured_at_utc_ms < start {
                    (start, start)
                } else {
                    (start, obs.captured_at_utc_ms)
                };
                tx.insert_closed_gap(&self.runtime_id, GapKind::ClockChanged, gs, ge)?;
                self.last_point = None;
                Some((gs, ge.max(obs.captured_at_utc_ms)))
            }
            Some(Boundary::CaptureDelayed) => {
                self.close_rows(&tx, "capture_delayed", "capture_delayed")?;
                let start = self
                    .last_point
                    .map(|(utc, _)| utc)
                    .unwrap_or(obs.captured_at_utc_ms);
                tx.insert_closed_gap(
                    &self.runtime_id,
                    GapKind::CaptureDelayed,
                    start,
                    obs.captured_at_utc_ms,
                )?;
                self.last_point = None;
                Some((start, obs.captured_at_utc_ms))
            }
            None => self.apply_observation(&tx, obs, app_id, observation_id)?,
        };

        if boundary.is_some() {
            // 边界后首条 Observation：以零时长新 Segment 重新开始（09 §6.5）。
            self.start_new_segment(&tx, obs, app_id, observation_id)?;
        }

        self.last_point = Some((obs.captured_at_utc_ms, obs.captured_monotonic_ms));
        // writer 侧时钟 bump 后，后续比较一律使用当前 epoch（09 §5.2 修订）。
        self.last_message_epoch = Some(self.continuity.current_epoch().max(obs.continuity_epoch));
        if let Some((start, end)) = touched {
            self.recompute_touched(&tx, start, end)?;
        }
        tx.commit()
    }

    /// 规则 1–3：归属、切段、sampling_transition（09 §6.5）。
    /// 返回触及的 UTC 区间供投影重算。
    fn apply_observation(
        &mut self,
        tx: &wuji_storage::writer::StorageTransaction<'_>,
        obs: &FilteredObservation,
        app_id: i64,
        observation_id: i64,
    ) -> Result<Option<(i64, i64)>> {
        let Some(segment) = self.open_segment else {
            // 规则 1：第一条 Observation → 零时长 open Segment。
            self.start_new_segment(tx, obs, app_id, observation_id)?;
            return Ok(Some((obs.captured_at_utc_ms, obs.captured_at_utc_ms)));
        };

        let same_identity = segment.app_id == app_id
            && segment.activity_state == obs.activity_state
            && segment.continuity_epoch == obs.continuity_epoch;
        if same_identity {
            // 规则 2：同 epoch/App/状态、delta 为正且不超 gap cap → 整个 delta 归前一段。
            let utc_delta = obs.captured_at_utc_ms - segment.end_at_utc_ms;
            if utc_delta > 0 && utc_delta <= self.gap_cap_ms {
                tx.update_open_segment(segment.segment_id, obs.captured_at_utc_ms, observation_id)?;
                self.open_segment.as_mut().unwrap().end_at_utc_ms = obs.captured_at_utc_ms;
                self.attribute_delta(tx, segment, utc_delta, obs.captured_at_utc_ms)?;
                return Ok(Some((segment.end_at_utc_ms, obs.captured_at_utc_ms)));
            }
        }

        // 规则 3：App/状态变化 → 旧段在上一条时刻关闭，新段零时长开始；
        // 间隔归 sampling_transition（不结束 Work Block，不计 active/idle）。
        let close_reason = if segment.app_id != app_id {
            "app_changed"
        } else {
            "state_changed"
        };
        tx.close_open_segment(close_reason)?;
        tx.insert_closed_gap(
            &self.runtime_id,
            GapKind::SamplingTransition,
            segment.end_at_utc_ms,
            obs.captured_at_utc_ms,
        )?;

        if segment.activity_state != obs.activity_state {
            self.on_state_change(tx, obs, segment)?;
        }

        self.start_new_segment(tx, obs, app_id, observation_id)?;
        Ok(Some((segment.end_at_utc_ms, obs.captured_at_utc_ms)))
    }

    /// 规则 2 归属后的 Work 记账（09 §6.6）。
    fn attribute_delta(
        &mut self,
        tx: &wuji_storage::writer::StorageTransaction<'_>,
        segment: SegmentState,
        delta_ms: i64,
        obs_utc_ms: i64,
    ) -> Result<()> {
        match segment.activity_state {
            ActivityState::Active => {
                if self.open_work.is_none() {
                    // 第一段产生正 active duration 时创建 Work Block（09 §6.6）。
                    let work_block_id = tx.open_work_block(
                        &self.runtime_id,
                        segment.start_at_utc_ms,
                        segment.segment_id,
                    )?;
                    self.open_work = Some(WorkState {
                        work_block_id,
                        end_at_utc_ms: segment.start_at_utc_ms,
                        active_duration_ms: 0,
                        short_idle_duration_ms: 0,
                        last_segment_id: segment.segment_id,
                    });
                }
                let work = self.open_work.as_mut().unwrap();
                work.active_duration_ms += delta_ms;
                work.end_at_utc_ms = obs_utc_ms;
                work.last_segment_id = segment.segment_id;
                let (id, end, active, idle, last_seg) = (
                    work.work_block_id,
                    work.end_at_utc_ms,
                    work.active_duration_ms,
                    work.short_idle_duration_ms,
                    work.last_segment_id,
                );
                tx.update_open_work_block(id, end, active, idle, last_seg)?;
            }
            ActivityState::Idle => {
                if self.open_work.is_some() {
                    if self.pending_idle_start_ms.is_none() {
                        self.pending_idle_start_ms = Some(segment.start_at_utc_ms);
                        self.pending_idle_ms = 0;
                    }
                    self.pending_idle_ms += delta_ms;
                    // pending 期间 Work Block 端点随 idle 延伸（09 §6.6 pending 语义）。
                    let work = self.open_work.as_mut().unwrap();
                    work.end_at_utc_ms = obs_utc_ms;
                    work.last_segment_id = segment.segment_id;
                    let (id, end, active, idle, last_seg) = (
                        work.work_block_id,
                        work.end_at_utc_ms,
                        work.active_duration_ms,
                        work.short_idle_duration_ms,
                        work.last_segment_id,
                    );
                    tx.update_open_work_block(id, end, active, idle, last_seg)?;

                    if self.pending_idle_ms >= self.work_break_ms {
                        // 达到 Work break：回溯结束于 Idle 开始，整段 Idle 不进入（09 §6.6）。
                        let idle_start = self.pending_idle_start_ms.unwrap();
                        tx.close_open_work_block_with_end("idle_break", idle_start)?;
                        self.open_work = None;
                        self.pending_idle_start_ms = None;
                        self.pending_idle_ms = 0;
                    }
                }
            }
            ActivityState::Unknown => {
                // Unknown 的状态变化已在规则 3 结束 Work Block；延续不另记账。
            }
        }
        Ok(())
    }

    /// 规则 3 中状态变化对 Work Block 的影响（09 §6.6）。
    fn on_state_change(
        &mut self,
        tx: &wuji_storage::writer::StorageTransaction<'_>,
        obs: &FilteredObservation,
        previous: SegmentState,
    ) -> Result<()> {
        match (previous.activity_state, obs.activity_state) {
            (_, ActivityState::Unknown) => {
                // Unknown 立即结束 Work Block（09 §6.6）。
                if self.open_work.is_some() {
                    tx.close_open_work_block("unknown")?;
                    self.open_work = None;
                }
                self.pending_idle_start_ms = None;
                self.pending_idle_ms = 0;
            }
            (ActivityState::Idle, ActivityState::Active) => {
                // 阈值前恢复 Active：已可靠归属的 Idle 计为 short idle（09 §6.6）。
                if self.pending_idle_start_ms.is_some()
                    && let Some(work) = self.open_work.as_mut()
                {
                    work.short_idle_duration_ms += self.pending_idle_ms;
                    work.last_segment_id = previous.segment_id;
                    let (id, end, active, idle, last_seg) = (
                        work.work_block_id,
                        work.end_at_utc_ms,
                        work.active_duration_ms,
                        work.short_idle_duration_ms,
                        work.last_segment_id,
                    );
                    tx.update_open_work_block(id, end, active, idle, last_seg)?;
                }
                self.pending_idle_start_ms = None;
                self.pending_idle_ms = 0;
            }
            (ActivityState::Active, ActivityState::Idle) => {
                // 进入 pending：下一条 idle 归属时开始累计（attribute_delta 处理）。
                self.pending_idle_start_ms = None;
                self.pending_idle_ms = 0;
            }
            _ => {}
        }
        Ok(())
    }

    fn start_new_segment(
        &mut self,
        tx: &wuji_storage::writer::StorageTransaction<'_>,
        obs: &FilteredObservation,
        app_id: i64,
        observation_id: i64,
    ) -> Result<()> {
        // writer 侧 bump 后新时间轴使用当前 epoch（09 §6.5 第 5 条）。
        let segment_epoch = self.continuity.current_epoch().max(obs.continuity_epoch);
        let segment_id = tx.open_segment(
            &self.runtime_id,
            segment_epoch as i64,
            app_id,
            obs.activity_state,
            obs.captured_at_utc_ms,
            observation_id,
        )?;
        self.open_segment = Some(SegmentState {
            segment_id,
            app_id,
            activity_state: obs.activity_state,
            continuity_epoch: segment_epoch,
            start_at_utc_ms: obs.captured_at_utc_ms,
            end_at_utc_ms: obs.captured_at_utc_ms,
        });
        Ok(())
    }

    /// 边界分类（09 §5.2、§6.5）：queue drop > stale epoch > 时钟异常 > 超时。
    fn classify_boundary(&mut self, obs: &FilteredObservation) -> Option<Boundary> {
        if let Some(last_epoch) = self.last_message_epoch {
            if obs.continuity_epoch > last_epoch {
                // 新 drop：按计数器增量判定 lane。
                let capture_drops = self.continuity.dropped_capture_count();
                let writer_drops = self.continuity.dropped_writer_count();
                let kind = if capture_drops > self.last_capture_drops {
                    GapKind::CaptureQueueDrop
                } else if writer_drops > self.last_writer_drops {
                    GapKind::WriterQueueDrop
                } else {
                    GapKind::CaptureQueueDrop
                };
                self.last_capture_drops = capture_drops;
                self.last_writer_drops = writer_drops;
                return Some(Boundary::QueueDrop(kind));
            }
            if obs.continuity_epoch < last_epoch {
                // 滞留旧 epoch 消息：按时钟异常处理，不回退当前 epoch（09 §5.2 修订）。
                return Some(Boundary::ClockChanged);
            }
        }
        if let Some((prev_utc, prev_mono)) = self.last_point {
            let utc_delta = obs.captured_at_utc_ms - prev_utc;
            let mono_delta = obs.captured_monotonic_ms as i64 - prev_mono as i64;
            if utc_delta <= 0 || (utc_delta - mono_delta).abs() > CLOCK_SKEW_TOLERANCE_MS {
                // Writer 自身增加 epoch（09 §6.5 第 5 条），不累计 drop。
                self.continuity.bump_epoch();
                return Some(Boundary::ClockChanged);
            }
            if utc_delta > self.gap_cap_ms {
                return Some(Boundary::CaptureDelayed);
            }
        }
        None
    }

    fn close_rows(
        &mut self,
        tx: &wuji_storage::writer::StorageTransaction<'_>,
        segment_reason: &str,
        work_reason: &str,
    ) -> Result<()> {
        if self.open_segment.is_some() {
            tx.close_open_segment(segment_reason)?;
            self.open_segment = None;
        }
        if self.open_work.is_some() {
            tx.close_open_work_block(work_reason)?;
            self.open_work = None;
        }
        self.pending_idle_start_ms = None;
        self.pending_idle_ms = 0;
        Ok(())
    }

    /// 生命周期/隐私/错误类边界：关闭 open 行并按叠加规则维护 open gap（09 §6.7）。
    /// `respect_pause_stop_absorb`：Sleep/Lock 在 paused/stopped open gap 期间维持原 gap。
    fn on_gap_event(
        &mut self,
        writer: &mut Writer,
        at_utc_ms: i64,
        segment_reason: &str,
        work_reason: &str,
        kind: GapKind,
        respect_pause_stop_absorb: bool,
    ) -> Result<()> {
        if respect_pause_stop_absorb
            && matches!(
                self.open_gap_kind,
                Some(GapKind::CapturePaused | GapKind::CaptureStopped)
            )
        {
            // 采集本已停止：不改变 open gap，仅关闭 open 行（09 §6.7 叠加规则）。
            let tx = writer.transaction()?;
            self.close_rows(&tx, segment_reason, work_reason)?;
            self.last_point = None;
            tx.commit()?;
            return Ok(());
        }

        // touched 以边界前最后一个已归属点为起点（last_point 在边界清空）。
        let start = self.last_point.map(|(utc, _)| utc).unwrap_or(at_utc_ms);
        let tx = writer.transaction()?;
        self.close_rows(&tx, segment_reason, work_reason)?;
        self.last_point = None;
        match self.open_gap_kind {
            Some(existing) if existing == kind => {
                tx.extend_open_gap()?;
            }
            Some(_) => {
                tx.close_open_gap(at_utc_ms)?;
                tx.open_gap(&self.runtime_id, kind, at_utc_ms)?;
            }
            None => {
                tx.open_gap(&self.runtime_id, kind, at_utc_ms)?;
            }
        }
        self.open_gap_kind = Some(kind);
        self.recompute_touched(&tx, start, at_utc_ms)?;
        tx.commit()
    }

    fn on_shutdown(&mut self, writer: &mut Writer, at_utc_ms: i64) -> Result<()> {
        let start = self.last_point.map(|(utc, _)| utc).unwrap_or(at_utc_ms);
        let tx = writer.transaction()?;
        self.close_rows(&tx, "agent_shutdown", "agent_shutdown")?;
        if self.open_gap_kind.is_some() {
            tx.close_open_gap(at_utc_ms)?;
            self.open_gap_kind = None;
        }
        tx.mark_runtime_ended(&self.runtime_id, at_utc_ms)?;
        self.last_point = None;
        self.recompute_touched(&tx, start, at_utc_ms)?;
        tx.commit()
    }

    fn ensure_tz(&mut self, writer: &Writer) -> Result<()> {
        if self.tz.is_none() {
            self.tz = Some(writer.schema_meta().reporting_tz()?);
        }
        Ok(())
    }

    /// 触及桶重算：range 覆盖的 UTC 小时桶与 local date（09 §7.3）。
    fn recompute_touched(
        &self,
        tx: &wuji_storage::writer::StorageTransaction<'_>,
        start_utc_ms: i64,
        end_utc_ms: i64,
    ) -> Result<()> {
        let tz = self.tz.expect("tz 已在 handle/recover 前加载");
        let first_hour = start_utc_ms - start_utc_ms.rem_euclid(3_600_000);
        let last_hour = end_utc_ms - end_utc_ms.rem_euclid(3_600_000);
        let mut hours = Vec::new();
        let mut hour = first_hour;
        while hour <= last_hour {
            hours.push(hour);
            hour += 3_600_000;
        }
        let start_date = LocalDate::parse(&local_date_of(&tz, start_utc_ms)?)
            .map_err(|e| StorageError::internal(e.message))?;
        let end_date = LocalDate::parse(&local_date_of(&tz, end_utc_ms)?)
            .map_err(|e| StorageError::internal(e.message))?;
        let mut dates = vec![start_date.clone()];
        if end_date != start_date {
            dates.push(end_date);
        }
        tx.recompute_hours(&tz, &hours)?;
        tx.recompute_dates(&tz, &dates, self.gap_cap_ms)?;
        Ok(())
    }
}
