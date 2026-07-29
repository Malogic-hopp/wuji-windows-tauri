//! 可靠 Barrier 注入协议（阶段 4.2，审核 P1-03；阶段 4.3.1，S2-06 有界化）。
//!
//! `BarrierRequest` 携带 token 与 injected ack：Capture Loop 只在 Barrier
//! 成功写入 CapturePipelineItem FIFO 后确认。请求 channel 关闭、FIFO 关闭、
//! Capture Loop/Processor/Writer 退出都会向等待方返回稳定失败；
//! 注入失败时调用方不得发送对应 WriterControl（无悬挂等待）。
//!
//! S2-06 有界失败（阶段 4.3.1）：
//! - request 发送：`timeout + Sender::reserve`——期限内在 permit 获得前超时，
//!   保证请求未进入 channel，绝不悬挂发送；
//! - injected ack：独立服务端 deadline——超时不能断言"Barrier 未注入"
//!   （可能已进入 FIFO 而 ack 丢失），调用方不得发送 WriterControl，
//!   可能遗留的 Barrier 由 Writer pending TTL/冲突规则清理。

use std::time::Duration;

use tokio::sync::{mpsc, oneshot};
use wuji_core::pipeline::BarrierToken;

/// request 发送的服务端期限（S2-06；与 IPC 客户端 3 秒 timeout 无关）。
pub const BARRIER_REQUEST_SEND_TIMEOUT: Duration = Duration::from_secs(2);
/// injected ack 的服务端 deadline（S2-06；与 IPC 客户端 3 秒 timeout 分离命名）。
pub const BARRIER_INJECT_ACK_TIMEOUT: Duration = Duration::from_secs(3);

/// 注入请求：token + injected ack（ack 只表示"已写入 FIFO"，不表示 Writer 已消费）。
pub struct BarrierRequest {
    pub token: BarrierToken,
    pub injected_ack: oneshot::Sender<Result<(), BarrierInjectError>>,
}

/// 注入失败（稳定分类）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum BarrierInjectError {
    /// 请求 channel 已关闭（Capture Loop 不存在或已退出）。
    RequestClosed,
    /// FIFO 写入失败或 ack 通道中断（Capture Loop/Processor/Writer 已退出）。
    Closed,
    /// request 发送在 `BARRIER_REQUEST_SEND_TIMEOUT` 内未获得 permit
    /// （channel 满且消费端不前进；请求保证未进入 channel）。
    SendTimeout,
    /// injected ack 在 `BARRIER_INJECT_ACK_TIMEOUT` 内未返回。
    /// 不能断言 Barrier 未注入（可能已进入 FIFO 而 ack 丢失）：
    /// 调用方不得发送 WriterControl，遗留 Barrier 由 pending TTL/冲突规则清理。
    AckTimeout,
}

/// 创建 barrier 请求 channel。
pub fn barrier_request_channel(
    capacity: usize,
) -> (mpsc::Sender<BarrierRequest>, mpsc::Receiver<BarrierRequest>) {
    mpsc::channel(capacity)
}

/// 可靠注入：等待 Capture Loop 确认 Barrier 已写入 FIFO。
/// 任何失败路径都返回错误（不存在静默吞掉，也不存在永久等待——S2-06）。
pub async fn inject_barrier(
    tx: &mpsc::Sender<BarrierRequest>,
    token: BarrierToken,
) -> Result<(), BarrierInjectError> {
    let (ack_tx, ack_rx) = oneshot::channel();
    // 有界发送：timeout 发生在 permit 获得前 → 请求未进入 channel。
    let permit = match tokio::time::timeout(BARRIER_REQUEST_SEND_TIMEOUT, tx.reserve()).await {
        Ok(Ok(permit)) => permit,
        Ok(Err(_)) => return Err(BarrierInjectError::RequestClosed),
        Err(_) => return Err(BarrierInjectError::SendTimeout),
    };
    permit.send(BarrierRequest {
        token,
        injected_ack: ack_tx,
    });
    // 有界 ack：Capture Loop 只在 FIFO 写入成功后确认。
    match tokio::time::timeout(BARRIER_INJECT_ACK_TIMEOUT, ack_rx).await {
        Ok(Ok(Ok(()))) => Ok(()),
        Ok(Ok(Err(error))) => Err(error),
        Ok(Err(_)) => Err(BarrierInjectError::Closed),
        Err(_) => Err(BarrierInjectError::AckTimeout),
    }
}
