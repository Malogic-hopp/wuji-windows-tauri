//! 生产任务健康状态（阶段 4.3 复审 P1-02；第二次复审 P1 补修）。
//!
//! 状态机：`NotStarted → Alive → Dead`（不可逆）：
//! - 初始 `NotStarted`：装配函数返回但任务尚未注册时，
//!   Coordinator 不得允许 Running（不存在"初始即健康"窗口）；
//! - 注册（`register_*`）在 `tokio::spawn` 之前**同步**完成
//!   （`compare_exchange(NotStarted → Alive)`），并返回 RAII guard；
//! - guard 被 move 进任务 future：正常返回、panic 展开、poll 后 abort、
//!   **首次 poll 前 abort**（future 被销毁时连同捕获的 guard 一起 Drop）
//!   都会把状态写为 `Dead`；
//! - `Dead` 不得复活；重复注册（含死亡后重注册）panic 于明确的内部不变量。
//!
//! Coordinator 发布 Running 前在 transition lock 内检查 `all_alive()`，
//! 不允许仅凭 SharedState 推断任务存活。

use std::sync::Arc;
use std::sync::atomic::{AtomicU8, Ordering};

use tokio::sync::mpsc;

const NOT_STARTED: u8 = 0;
const ALIVE: u8 = 1;
const DEAD: u8 = 2;

/// 单个生产任务的生命周期（`NotStarted → Alive → Dead`，不可逆）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TaskLifecycle {
    /// 已装配但尚未注册（任务从未启动）。
    NotStarted,
    /// 已注册且 guard 存活。
    Alive,
    /// guard 已 Drop（任务退出/panic/abort，含首次 poll 前 abort）。
    Dead,
}

impl TaskLifecycle {
    fn from_raw(raw: u8) -> Self {
        match raw {
            NOT_STARTED => Self::NotStarted,
            ALIVE => Self::Alive,
            _ => Self::Dead,
        }
    }
}

/// 三个生产任务（Capture Loop / Processor / Writer）的健康状态。
#[derive(Debug)]
pub struct PipelineHealth {
    capture: AtomicU8,
    processor: AtomicU8,
    writer: AtomicU8,
    exit_tx: Option<mpsc::UnboundedSender<PipelineTask>>,
}

impl PipelineHealth {
    /// 三个任务初始均为 `NotStarted`（第二次复审 P1：不得初始化为 Alive）。
    pub fn new() -> Arc<Self> {
        Arc::new(Self {
            capture: AtomicU8::new(NOT_STARTED),
            processor: AtomicU8::new(NOT_STARTED),
            writer: AtomicU8::new(NOT_STARTED),
            exit_tx: None,
        })
    }

    /// 创建带退出事件的生产健康状态。事件使用无界通道是刻意的：三项任务
    /// 每项至多从 Alive 进入 Dead 一次，Drop 不能阻塞，也不能因队列容量丢失
    /// fail-closed 通知。
    pub fn with_exit_events() -> (Arc<Self>, mpsc::UnboundedReceiver<PipelineTask>) {
        let (exit_tx, exit_rx) = mpsc::unbounded_channel();
        (
            Arc::new(Self {
                capture: AtomicU8::new(NOT_STARTED),
                processor: AtomicU8::new(NOT_STARTED),
                writer: AtomicU8::new(NOT_STARTED),
                exit_tx: Some(exit_tx),
            }),
            exit_rx,
        )
    }

    pub fn capture_state(&self) -> TaskLifecycle {
        TaskLifecycle::from_raw(self.capture.load(Ordering::Acquire))
    }

    pub fn processor_state(&self) -> TaskLifecycle {
        TaskLifecycle::from_raw(self.processor.load(Ordering::Acquire))
    }

    pub fn writer_state(&self) -> TaskLifecycle {
        TaskLifecycle::from_raw(self.writer.load(Ordering::Acquire))
    }

    pub fn capture_alive(&self) -> bool {
        self.capture_state() == TaskLifecycle::Alive
    }

    pub fn processor_alive(&self) -> bool {
        self.processor_state() == TaskLifecycle::Alive
    }

    pub fn writer_alive(&self) -> bool {
        self.writer_state() == TaskLifecycle::Alive
    }

    /// 只有三个任务全部为 `Alive` 才返回 true（`NotStarted` 不算健康）。
    pub fn all_alive(&self) -> bool {
        self.capture_alive() && self.processor_alive() && self.writer_alive()
    }

    /// 注册 Capture Loop（必须在 `tokio::spawn` 之前同步调用）。
    pub fn register_capture(self: &Arc<Self>) -> TaskHealthGuard {
        self.register(PipelineTask::Capture)
    }

    /// 注册 Processor（必须在 `tokio::spawn` 之前同步调用）。
    pub fn register_processor(self: &Arc<Self>) -> TaskHealthGuard {
        self.register(PipelineTask::Processor)
    }

    /// 注册 Writer（必须在 `tokio::spawn` 之前同步调用）。
    pub fn register_writer(self: &Arc<Self>) -> TaskHealthGuard {
        self.register(PipelineTask::Writer)
    }

    /// 同步注册：`compare_exchange(NotStarted → Alive)`；
    /// 重复注册或死亡后复活一律 panic（内部不变量，绝不静默恢复）。
    fn register(self: &Arc<Self>, task: PipelineTask) -> TaskHealthGuard {
        task.slot(self)
            .compare_exchange(NOT_STARTED, ALIVE, Ordering::AcqRel, Ordering::Acquire)
            .expect("PipelineHealth 任务重复注册或死亡后复活（内部不变量）");
        TaskHealthGuard {
            health: self.clone(),
            task,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PipelineTask {
    Capture,
    Processor,
    Writer,
}

impl PipelineTask {
    fn slot(self, health: &PipelineHealth) -> &AtomicU8 {
        match self {
            Self::Capture => &health.capture,
            Self::Processor => &health.processor,
            Self::Writer => &health.writer,
        }
    }
}

/// RAII 健康守卫：由任务 future 捕获；Drop（正常返回/panic 展开/abort 取消，
/// 含首次 poll 前 abort 时随 future 一起销毁）把状态写为 `Dead`。
pub struct TaskHealthGuard {
    health: Arc<PipelineHealth>,
    task: PipelineTask,
}

impl Drop for TaskHealthGuard {
    fn drop(&mut self) {
        self.task.slot(&self.health).store(DEAD, Ordering::Release);
        if let Some(exit_tx) = &self.health.exit_tx {
            let _ = exit_tx.send(self.task);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 第二次复审 P1：初始三任务均为 NotStarted，all_alive 为 false。
    #[test]
    fn initial_state_is_not_started_and_not_alive() {
        let health = PipelineHealth::new();
        assert_eq!(health.capture_state(), TaskLifecycle::NotStarted);
        assert_eq!(health.processor_state(), TaskLifecycle::NotStarted);
        assert_eq!(health.writer_state(), TaskLifecycle::NotStarted);
        assert!(!health.all_alive());
    }

    /// 三任务依次注册后才 all_alive；任一 guard Drop 后对应 Dead。
    #[test]
    fn all_alive_only_after_all_registered_and_drop_marks_dead() {
        let health = PipelineHealth::new();
        let capture = health.register_capture();
        assert_eq!(health.capture_state(), TaskLifecycle::Alive);
        assert!(!health.all_alive());
        let processor = health.register_processor();
        assert!(!health.all_alive());
        let _writer = health.register_writer();
        assert!(health.all_alive());

        drop(processor);
        assert_eq!(health.processor_state(), TaskLifecycle::Dead);
        assert_eq!(health.capture_state(), TaskLifecycle::Alive);
        assert!(!health.all_alive());
        drop(capture);
        assert_eq!(health.capture_state(), TaskLifecycle::Dead);
    }

    /// 重复注册显式 panic（内部不变量）。
    #[test]
    #[should_panic(expected = "PipelineHealth 任务重复注册或死亡后复活")]
    fn duplicate_registration_panics() {
        let health = PipelineHealth::new();
        let _guard = health.register_capture();
        let _guard2 = health.register_capture();
    }

    /// Dead 不得复活。
    #[test]
    #[should_panic(expected = "PipelineHealth 任务重复注册或死亡后复活")]
    fn dead_task_cannot_be_reregistered() {
        let health = PipelineHealth::new();
        let guard = health.register_writer();
        drop(guard);
        assert_eq!(health.writer_state(), TaskLifecycle::Dead);
        let _guard2 = health.register_writer();
    }
}
