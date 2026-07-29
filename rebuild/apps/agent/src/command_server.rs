//! CommandServer：Named Pipe IPC（09 §8.1、§8.2）。
//!
//! 单行 UTF-8 JSON envelope、hello 握手、64 KiB 上限、3 秒 timeout、
//! request ID 幂等（in-progress/completed）、Capture 状态机与稳定错误码。

use std::collections::HashMap;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use serde::{Deserialize, Serialize};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::sync::watch;
use wuji_core::error::{SafeError, SafeErrorCode};
use wuji_core::settings::Settings;

use crate::capture_coordinator::CaptureCoordinator;
use crate::capture_loop::now_utc_ms;
use crate::shared::SharedState;

pub const PROTOCOL_VERSION: u32 = 1;
pub const MAX_PAYLOAD_BYTES: usize = 64 * 1024;
pub const REQUEST_TIMEOUT: Duration = Duration::from_secs(3);
const REQUEST_ID_TTL: Duration = Duration::from_secs(600);
const REQUEST_ID_CAPACITY: usize = 1024;
/// InProgress 总量上限（S2-06）：超过即拒绝全新 ID（稳定错误，不接受不执行）。
const IN_PROGRESS_CAPACITY: usize = 256;
/// 客户端等待执行结果的最大年龄。到期后只发布稳定的“结果未知”响应；后台执行记录
/// 仍保持 Active、继续占用并发容量且不可被 TTL/LRU 驱逐，直到真实 dispatch 结束。
const IN_PROGRESS_MAX_AGE: Duration = Duration::from_secs(20);

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct RequestEnvelope {
    protocol_version: u32,
    request_id: String,
    command: String,
    sent_at_utc_ms: String,
    #[serde(default)]
    payload: serde_json::Value,
}

/// hello 的强类型 payload（09 §8.1、审核 R05：逐命令 deny_unknown_fields）。
#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct HelloPayload {
    desktop_version: String,
    protocol_version: u32,
    channel: String,
}

/// settings_reload 的强类型 payload（09 §8.4）。
#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct SettingsReloadPayload {
    saved_revision: String,
    content_digest: String,
}

/// request ID 必须是 ULID（26 位 Crockford Base32，09 §8.1）。
fn valid_request_id(request_id: &str) -> bool {
    ulid::Ulid::from_string(request_id).is_ok()
}

/// sentAtUtcMs 必须是十进制毫秒字符串（i64 经字符串传输避免 JS 精度问题）。
fn valid_sent_at(sent_at_utc_ms: &str) -> bool {
    !sent_at_utc_ms.is_empty() && sent_at_utc_ms.bytes().all(|b| b.is_ascii_digit())
}

/// 无 payload 命令只接受缺省、null 或空对象（审核 R05）。
fn ensure_empty_payload(payload: &serde_json::Value) -> Result<(), SafeError> {
    match payload {
        serde_json::Value::Null => Ok(()),
        serde_json::Value::Object(map) if map.is_empty() => Ok(()),
        _ => Err(SafeError::new(
            SafeErrorCode::IpcInvalidMessage,
            "该命令不接受 payload 字段",
        )),
    }
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct ResponseEnvelope<'a> {
    protocol_version: u32,
    request_id: &'a str,
    agent_version: &'a str,
    ok: bool,
    result: serde_json::Value,
    error: Option<SafeError>,
}

fn ok_response<'a>(
    request_id: &'a str,
    agent_version: &'a str,
    result: serde_json::Value,
) -> ResponseEnvelope<'a> {
    ResponseEnvelope {
        protocol_version: PROTOCOL_VERSION,
        request_id,
        agent_version,
        ok: true,
        result,
        error: None,
    }
}

fn err_response<'a>(
    request_id: &'a str,
    agent_version: &'a str,
    error: SafeError,
) -> ResponseEnvelope<'a> {
    ResponseEnvelope {
        protocol_version: PROTOCOL_VERSION,
        request_id,
        agent_version,
        ok: false,
        result: serde_json::Value::Null,
        error: Some(error),
    }
}

enum RequestState {
    /// 已接受、后台仍在执行。`visible_response` 只改变客户端可见结果，不代表执行完成。
    Active {
        receiver: watch::Receiver<Option<String>>,
        created_at: Instant,
        generation: u64,
        visible_response: Option<String>,
    },
    /// 后台执行已经真实结束的稳定终态。
    Completed {
        response: String,
        last_accessed: Instant,
    },
}

/// request ID 幂等缓存（协议级测试可见；条目含 in-progress/completed 两态）。
///
/// S2-06 完成守卫：
/// - Active execution 直到真实 dispatch 结束前始终占 active capacity，且不可 TTL/LRU 驱逐；
/// - stale Active 只发布客户端可见的稳定“结果未知”，绝不伪装成 Completed；
/// - 后台结果只允许结束同一 generation 的 Active（防止旧任务覆盖新条目）；
/// - Completed 采用访问时刻刷新的 TTL/LRU，总条目数严格不超过 REQUEST_ID_CAPACITY。
pub struct RequestIdCache {
    entries: HashMap<String, (u64, RequestState)>,
    agent_version: String,
    next_generation: u64,
}

impl RequestIdCache {
    /// 空缓存（agent_version 用于结果未知响应 envelope）。
    pub fn new(agent_version: String) -> Self {
        Self {
            entries: HashMap::new(),
            agent_version,
            next_generation: 0,
        }
    }

    /// 当前真实仍在执行的请求数（测试与诊断口径；unknown 仍计入）。
    pub fn active_count(&self) -> usize {
        self.entries
            .values()
            .filter(|(_, state)| matches!(state, RequestState::Active { .. }))
            .count()
    }

    /// Active + Completed 总条目数，恒不超过 REQUEST_ID_CAPACITY。
    pub fn len(&self) -> usize {
        self.entries.len()
    }

    pub fn is_empty(&self) -> bool {
        self.entries.is_empty()
    }

    /// 过期清理（now 显式传入：测试可注入未来时刻，确定性覆盖 TTL/最大年龄）。
    pub fn purge_expired_at(&mut self, now: Instant) {
        // 1) stale Active 只附加客户端可见的“结果未知”；后台 execution record 不变。
        for (id, (_, state)) in &mut self.entries {
            if let RequestState::Active {
                created_at,
                visible_response,
                ..
            } = state
                && visible_response.is_none()
                && now.duration_since(*created_at) >= IN_PROGRESS_MAX_AGE
            {
                *visible_response = Some(serialize_response(&err_response(
                    id,
                    &self.agent_version,
                    SafeError::new(
                        SafeErrorCode::InternalSafeError,
                        "原命令仍在后台执行，当前结果未知",
                    ),
                )));
            }
        }
        // 2) 只有真正 Completed 的条目才参与 TTL；Active 永不由清理路径驱逐。
        self.entries.retain(|_, (_, state)| match state {
            RequestState::Active { .. } => true,
            RequestState::Completed { last_accessed, .. } => {
                now.duration_since(*last_accessed) < REQUEST_ID_TTL
            }
        });
        debug_assert!(self.entries.len() <= REQUEST_ID_CAPACITY);
    }

    /// 为一个全新 ID 预留槽位。只淘汰真正 Completed 的 LRU；Active 永不驱逐。
    fn make_room_for_new(&mut self) -> bool {
        if self.entries.len() < REQUEST_ID_CAPACITY {
            return true;
        }
        let oldest = self
            .entries
            .iter()
            .filter_map(|(id, (_, state))| match state {
                RequestState::Completed { last_accessed, .. } => Some((id.clone(), *last_accessed)),
                RequestState::Active { .. } => None,
            })
            .min_by_key(|(_, at)| *at)
            .map(|(id, _)| id);
        if let Some(id) = oldest {
            self.entries.remove(&id);
            true
        } else {
            false
        }
    }

    fn purge_expired(&mut self) {
        self.purge_expired_at(Instant::now());
    }
}

/// 写入真实执行终态。只允许结束同一 generation 的 Active execution record。
fn complete_request(
    request_ids: &Mutex<RequestIdCache>,
    request_id: &str,
    payload_hash: u64,
    generation: u64,
    response: String,
) -> String {
    let mut cache = request_ids.lock().expect("request id cache");
    let stable_response = match cache.entries.get(request_id) {
        Some((
            _,
            RequestState::Active {
                generation: active_generation,
                visible_response,
                ..
            },
        )) if *active_generation == generation => {
            // 一旦 max-age 已向任一客户端发布“结果未知”，该 request ID 的
            // 客户端可见终态必须保持稳定；迟到真实结果只结束 Active/释放配额，
            // 不允许把同一 ID 从失败翻转为成功或另一种失败。
            visible_response.clone().unwrap_or(response)
        }
        _ => return response,
    };
    if matches!(
        cache.entries.get(request_id),
        Some((_, RequestState::Active { generation: g, .. })) if *g == generation
    ) {
        cache.entries.insert(
            request_id.to_string(),
            (
                payload_hash,
                RequestState::Completed {
                    response: stable_response.clone(),
                    last_accessed: Instant::now(),
                },
            ),
        );
    }
    stable_response
}

/// 在 dispatch future 第一次 poll 之前创建并移入 task 的完成守卫。
/// 即使 dispatch task 被 abort/cancel，Drop 也会把 Active execution 收敛为稳定失败；
/// 正常路径通过 `finish` 写入真实结果并解除守卫。
struct ExecutionCompletionGuard {
    request_ids: Arc<Mutex<RequestIdCache>>,
    request_id: String,
    payload_hash: u64,
    generation: u64,
    agent_version: String,
    result_tx: watch::Sender<Option<String>>,
    armed: bool,
}

impl ExecutionCompletionGuard {
    fn finish(&mut self, response: String) {
        let stable_response = complete_request(
            &self.request_ids,
            &self.request_id,
            self.payload_hash,
            self.generation,
            response,
        );
        let _ = self.result_tx.send(Some(stable_response));
        self.armed = false;
    }
}

impl Drop for ExecutionCompletionGuard {
    fn drop(&mut self) {
        if !self.armed {
            return;
        }
        let response = serialize_response(&err_response(
            &self.request_id,
            &self.agent_version,
            SafeError::new(
                SafeErrorCode::InternalSafeError,
                "命令处理被中断，后台执行已结束",
            ),
        ));
        let stable_response = complete_request(
            &self.request_ids,
            &self.request_id,
            self.payload_hash,
            self.generation,
            response,
        );
        let _ = self.result_tx.send(Some(stable_response));
    }
}

/// IPC 服务端共享上下文（阶段 4.3：只持有唯一 Coordinator 与只读依赖；
/// 不再持有 capture_lock、control/barrier 通道或 capture/settings watch）。
pub struct CommandServerContext {
    pub shared: Arc<SharedState>,
    /// 唯一控制入口：capture 命令、settings_reload 全部经它串行化。
    pub coordinator: Arc<CaptureCoordinator>,
    pub settings_path: std::path::PathBuf,
    pub settings_digest_for: fn(&Settings) -> String,
    pub shutdown_tx: watch::Sender<bool>,
    pub channel: String,
}

/// 请求分发。在独立任务中执行：调用方 3 秒 timeout 只结束等待，
/// 不取消已接受命令的副作用（审核 R05）。
async fn dispatch(
    context: Arc<CommandServerContext>,
    request: RequestEnvelope,
) -> Result<serde_json::Value, SafeError> {
    let now = now_utc_ms();
    match request.command.as_str() {
        "status_get" => {
            ensure_empty_payload(&request.payload)?;
            Ok(serde_json::to_value(context.shared.status_dto()).unwrap())
        }
        "capture_start" | "capture_pause" | "capture_resume" | "capture_stop" => {
            ensure_empty_payload(&request.payload)?;
            // 阶段 4.3：全部 capture 转换只经唯一 Coordinator（串行化、冻结、
            // Barrier 注入确认与 Writer ack 都在 Coordinator 内完成）。
            context
                .coordinator
                .apply_capture_command(&request.command, now)
                .await?;
            Ok(serde_json::to_value(context.shared.status_dto()).unwrap())
        }
        "settings_reload" => {
            let payload: SettingsReloadPayload = serde_json::from_value(request.payload.clone())
                .map_err(|_| {
                    SafeError::new(
                        SafeErrorCode::IpcInvalidMessage,
                        "settings_reload payload 必须是 savedRevision 与 contentDigest",
                    )
                })?;
            let saved_revision = payload.saved_revision;
            let expected_digest = payload.content_digest;
            let raw = std::fs::read_to_string(&context.settings_path).map_err(|_| {
                SafeError::new(
                    SafeErrorCode::SettingsSavedNotApplied,
                    "设置文件不可读，Agent 保持上一 revision",
                )
            })?;
            let settings: Settings = serde_json::from_str(&raw).map_err(|_| {
                SafeError::new(SafeErrorCode::SettingsInvalid, "设置文件不是合法 JSON")
            })?;
            if settings.revision != saved_revision {
                return Err(SafeError::new(
                    SafeErrorCode::SettingsConflict,
                    "设置 revision 与保存请求不一致",
                ));
            }
            let digest = (context.settings_digest_for)(&settings);
            if digest != expected_digest {
                return Err(SafeError::new(
                    SafeErrorCode::SettingsSavedNotApplied,
                    "设置文件摘要与请求不一致，Agent 保持上一 revision",
                ));
            }
            if let Err(errors) = settings.validate() {
                let message = errors
                    .first()
                    .map(|e| e.message.clone())
                    .unwrap_or_else(|| "设置字段不合法".to_string());
                return Err(SafeError::new(SafeErrorCode::SettingsInvalid, message));
            }
            // revision 单调性预检（R04）：低于已应用值的文件快速拒绝；
            // Coordinator 在 transition lock 内复检，关闭并发窗口。
            let applied_revision = context.shared.applied_settings_revision();
            match settings.revision.parse::<i64>() {
                Ok(revision) if revision >= applied_revision => {}
                _ => {
                    return Err(SafeError::new(
                        SafeErrorCode::SettingsConflict,
                        "设置 revision 低于已应用值，Agent 保持上一 revision",
                    ));
                }
            }
            // 阶段 4.3：与 reconciler 共用唯一 Coordinator（同一 transition lock、
            // 同一条 Barrier → control → ack 路径），不再存在 settings_reload 双路径。
            let applied = context.coordinator.apply_settings(settings, now).await?;
            Ok(serde_json::json!({ "appliedRevision": applied.to_string() }))
        }
        "agent_shutdown_dev" => {
            ensure_empty_payload(&request.payload)?;
            let _ = context.shutdown_tx.send(true);
            Ok(serde_json::json!({ "willExit": true }))
        }
        _ => Err(SafeError::new(SafeErrorCode::IpcInvalidMessage, "未知命令")),
    }
}

fn request_timeout_result() -> SafeError {
    SafeError::new(SafeErrorCode::InternalSafeError, "命令处理超时")
}

/// 单连接处理：hello 握手 → 命令循环。
async fn serve_connection(
    stream: tokio::net::windows::named_pipe::NamedPipeServer,
    context: Arc<CommandServerContext>,
    request_ids: Arc<Mutex<RequestIdCache>>,
) {
    let (mut reader, mut writer) = tokio::io::split(stream);
    let mut buffer = Vec::with_capacity(4096);

    // hello 握手：首条消息必须是 hello 且 channel/protocol 匹配（09 §8.1）。
    let hello = match read_line_capped(&mut reader, &mut buffer).await {
        ReadOutcome::Line(line) => line,
        ReadOutcome::InvalidUtf8 => {
            let _ = write_raw_line(
                &mut writer,
                &serialize_response(&err_response(
                    "",
                    &context.shared.agent_version(),
                    SafeError::new(SafeErrorCode::IpcInvalidMessage, "消息不是合法 UTF-8"),
                )),
            )
            .await;
            return;
        }
        ReadOutcome::Oversized => {
            let _ = write_raw_line(
                &mut writer,
                &serialize_response(&err_response(
                    "",
                    &context.shared.agent_version(),
                    SafeError::new(SafeErrorCode::IpcPayloadTooLarge, "消息超过 64 KiB 上限"),
                )),
            )
            .await;
            return;
        }
        ReadOutcome::Closed => return,
    };
    let hello: RequestEnvelope = match serde_json::from_str::<RequestEnvelope>(&hello) {
        Ok(envelope) if envelope.command == "hello" => envelope,
        _ => return,
    };
    if hello.protocol_version != PROTOCOL_VERSION {
        let _ = write_raw_line(
            &mut writer,
            &serialize_response(&err_response(
                &hello.request_id,
                &context.shared.agent_version(),
                SafeError::new(SafeErrorCode::IpcProtocolUnsupported, "协议版本不受支持"),
            )),
        )
        .await;
        return;
    }
    // hello 全字段校验（审核 R05）：ULID、sentAtUtcMs、desktopVersion、payload protocol、channel。
    let hello_payload: Result<HelloPayload, _> = serde_json::from_value(hello.payload.clone());
    let hello_payload = match hello_payload {
        Ok(payload)
            if valid_request_id(&hello.request_id)
                && valid_sent_at(&hello.sent_at_utc_ms)
                && !payload.desktop_version.is_empty() =>
        {
            payload
        }
        _ => {
            let _ = write_raw_line(
                &mut writer,
                &serialize_response(&err_response(
                    &hello.request_id,
                    &context.shared.agent_version(),
                    SafeError::new(SafeErrorCode::IpcInvalidMessage, "hello 字段不完整或不合法"),
                )),
            )
            .await;
            return;
        }
    };
    if hello_payload.protocol_version != PROTOCOL_VERSION {
        let _ = write_raw_line(
            &mut writer,
            &serialize_response(&err_response(
                &hello.request_id,
                &context.shared.agent_version(),
                SafeError::new(SafeErrorCode::IpcProtocolUnsupported, "协议版本不受支持"),
            )),
        )
        .await;
        return;
    }
    if hello_payload.channel != context.channel {
        let _ = write_raw_line(
            &mut writer,
            &serialize_response(&err_response(
                &hello.request_id,
                &context.shared.agent_version(),
                SafeError::new(SafeErrorCode::IpcChannelMismatch, "channel 不匹配"),
            )),
        )
        .await;
        return;
    }
    let hello_result = serde_json::json!({
        "agentVersion": context.shared.agent_version(),
        "protocolVersion": PROTOCOL_VERSION,
        "schemaVersion": 1,
        "runtimeId": context.shared.runtime_id().as_str(),
        "captureState": format!("{:?}", context.shared.capture_state()).to_lowercase(),
    });
    if write_raw_line(
        &mut writer,
        &serialize_response(&ok_response(
            &hello.request_id,
            &context.shared.agent_version(),
            hello_result,
        )),
    )
    .await
    .is_err()
    {
        return;
    }

    loop {
        let line = match read_line_capped(&mut reader, &mut buffer).await {
            ReadOutcome::Line(line) => line,
            ReadOutcome::InvalidUtf8 => {
                let _ = write_raw_line(
                    &mut writer,
                    &serialize_response(&err_response(
                        "",
                        &context.shared.agent_version(),
                        SafeError::new(SafeErrorCode::IpcInvalidMessage, "消息不是合法 UTF-8"),
                    )),
                )
                .await;
                return;
            }
            ReadOutcome::Oversized => {
                let _ = write_raw_line(
                    &mut writer,
                    &serialize_response(&err_response(
                        "",
                        &context.shared.agent_version(),
                        SafeError::new(SafeErrorCode::IpcPayloadTooLarge, "消息超过 64 KiB 上限"),
                    )),
                )
                .await;
                return;
            }
            ReadOutcome::Closed => return,
        };
        let response = handle_request_line(&line, &context, &request_ids).await;
        if write_raw_line(&mut writer, &response).await.is_err() {
            return;
        }
    }
}

/// 单行请求处理（协议级测试直接驱动；副作用经独立任务执行，R05）。
pub async fn handle_request_line(
    line: &str,
    context: &Arc<CommandServerContext>,
    request_ids: &Arc<Mutex<RequestIdCache>>,
) -> String {
    let agent_version = context.shared.agent_version();
    let parsed: std::result::Result<RequestEnvelope, _> = serde_json::from_str(line);
    let request = match parsed {
        Ok(request) => request,
        Err(_) => {
            return serialize_response(&err_response(
                "",
                &agent_version,
                SafeError::new(SafeErrorCode::IpcInvalidMessage, "消息不是合法 envelope"),
            ));
        }
    };
    if request.protocol_version != PROTOCOL_VERSION {
        return serialize_response(&err_response(
            &request.request_id,
            &agent_version,
            SafeError::new(SafeErrorCode::IpcProtocolUnsupported, "协议版本不受支持"),
        ));
    }
    // ULID 与 sentAtUtcMs 严格校验（审核 R05）。
    if !valid_request_id(&request.request_id) || !valid_sent_at(&request.sent_at_utc_ms) {
        return serialize_response(&err_response(
            "",
            &agent_version,
            SafeError::new(
                SafeErrorCode::IpcInvalidMessage,
                "requestId 必须是 ULID，sentAtUtcMs 必须是十进制字符串",
            ),
        ));
    }

    // request ID 幂等（09 §8.2 + S2-06 完成守卫）。
    let payload_hash = {
        use std::hash::{Hash, Hasher};
        let mut hasher = std::collections::hash_map::DefaultHasher::new();
        request.command.hash(&mut hasher);
        request.payload.to_string().hash(&mut hasher);
        hasher.finish()
    };
    enum CacheEntry {
        Fresh(watch::Sender<Option<String>>, u64),
        Wait(watch::Receiver<Option<String>>),
        Done(String),
        Conflict,
    }
    let entry = {
        let mut cache = request_ids.lock().expect("request id cache");
        cache.purge_expired();
        match cache.entries.get_mut(&request.request_id) {
            Some((hash, _)) if *hash != payload_hash => CacheEntry::Conflict,
            Some((
                _,
                RequestState::Active {
                    visible_response: Some(response),
                    ..
                },
            )) => CacheEntry::Done(response.clone()),
            Some((_, RequestState::Active { receiver, .. })) => CacheEntry::Wait(receiver.clone()),
            Some((
                _,
                RequestState::Completed {
                    response,
                    last_accessed,
                    ..
                },
            )) => {
                *last_accessed = Instant::now();
                CacheEntry::Done(response.clone())
            }
            None => {
                // Active 总量上限：拒绝全新 ID（稳定错误，不接受、不执行）。
                if cache.active_count() >= IN_PROGRESS_CAPACITY {
                    return serialize_response(&err_response(
                        &request.request_id,
                        &agent_version,
                        SafeError::new(
                            SafeErrorCode::InternalSafeError,
                            "服务端并发请求过多，请稍后重试",
                        ),
                    ));
                }
                // 总缓存严格不超过 1024。需要空间时只淘汰 Completed LRU。
                if !cache.make_room_for_new() {
                    return serialize_response(&err_response(
                        &request.request_id,
                        &agent_version,
                        SafeError::new(
                            SafeErrorCode::InternalSafeError,
                            "请求缓存容量已满，请稍后重试",
                        ),
                    ));
                }
                let generation = cache.next_generation;
                cache.next_generation += 1;
                let (tx, rx) = watch::channel(None);
                cache.entries.insert(
                    request.request_id.clone(),
                    (
                        payload_hash,
                        RequestState::Active {
                            receiver: rx,
                            created_at: Instant::now(),
                            generation,
                            visible_response: None,
                        },
                    ),
                );
                debug_assert!(cache.entries.len() <= REQUEST_ID_CAPACITY);
                CacheEntry::Fresh(tx, generation)
            }
        }
    };

    match entry {
        CacheEntry::Done(response) => response,
        CacheEntry::Conflict => serialize_response(&err_response(
            &request.request_id,
            &agent_version,
            SafeError::new(
                SafeErrorCode::IpcRequestIdReused,
                "request ID 以不同 payload 重用",
            ),
        )),
        CacheEntry::Wait(mut receiver) => {
            // 相同 ID + 相同 payload：有界等待原任务结果（S2-06：不得永久等待；
            // 超时只结束本次等待，条目保留，后续重试继续等待或被 max-age 收口）。
            match tokio::time::timeout(REQUEST_TIMEOUT, receiver.changed()).await {
                Ok(_) => receiver.borrow().clone().unwrap_or_else(|| {
                    serialize_response(&err_response(
                        &request.request_id,
                        &agent_version,
                        SafeError::new(SafeErrorCode::InternalSafeError, "原命令未完成"),
                    ))
                }),
                Err(_) => serialize_response(&err_response(
                    &request.request_id,
                    &agent_version,
                    request_timeout_result(),
                )),
            }
        }
        CacheEntry::Fresh(tx, generation) => {
            // 副作用在独立任务中执行（审核 R05）：3 秒 timeout 只结束本次等待，
            // 不取消已接受命令；完成守卫保证任务最终写入稳定终态（S2-06）。
            let mut wait_rx = tx.subscribe();
            let timeout_request_id = request.request_id.clone();
            {
                let context = context.clone();
                let request_ids = request_ids.clone();
                let request_id = request.request_id.clone();
                let agent_version = agent_version.clone();
                // 守卫必须在 spawn 之前同步创建，使首次 poll 前 abort 也能完成缓存条目。
                let mut completion = ExecutionCompletionGuard {
                    request_ids,
                    request_id: request_id.clone(),
                    payload_hash,
                    generation,
                    agent_version: agent_version.clone(),
                    result_tx: tx,
                    armed: true,
                };
                tokio::spawn(async move {
                    // dispatch 与完成守卫位于同一个 task：客户端 timeout 不会取消它；
                    // task panic/abort/runtime shutdown 会直接 drop 守卫并收敛 Active，
                    // 不存在“supervisor 被取消但内层 dispatch 仍 detached 执行”的窗口。
                    let response = match dispatch(context, request).await {
                        Ok(result) => {
                            serialize_response(&ok_response(&request_id, &agent_version, result))
                        }
                        Err(error) => {
                            serialize_response(&err_response(&request_id, &agent_version, error))
                        }
                    };
                    completion.finish(response);
                });
            }
            match tokio::time::timeout(REQUEST_TIMEOUT, wait_rx.changed()).await {
                Ok(_) => wait_rx.borrow().clone().unwrap_or_else(|| {
                    serialize_response(&err_response(
                        "",
                        &agent_version,
                        SafeError::new(SafeErrorCode::InternalSafeError, "原命令未完成"),
                    ))
                }),
                // timeout 响应不写入 cache：同 ID 重试将等待原任务的真实结果。
                Err(_) => serialize_response(&err_response(
                    &timeout_request_id,
                    &agent_version,
                    request_timeout_result(),
                )),
            }
        }
    }
}

fn serialize_response<T: serde::Serialize>(response: &T) -> String {
    serde_json::to_string(response).unwrap_or_else(|_| {
        "{\"protocolVersion\":1,\"ok\":false,\"error\":{\"code\":\"INTERNAL_SAFE_ERROR\",\"message\":\"内部错误\"}}".to_string()
    })
}

enum ReadOutcome {
    Line(String),
    /// 非合法 UTF-8：拒绝替换解码，回稳定错误后断开（审核 R05）。
    InvalidUtf8,
    Oversized,
    Closed,
}

/// 读取一行（\n 结尾）；超过 64 KiB 未换行返回 Oversized，由调用方回复错误后断开。
async fn read_line_capped(
    reader: &mut tokio::io::ReadHalf<tokio::net::windows::named_pipe::NamedPipeServer>,
    buffer: &mut Vec<u8>,
) -> ReadOutcome {
    loop {
        if let Some(position) = buffer.iter().position(|b| *b == b'\n') {
            let line = buffer.drain(..=position).collect::<Vec<u8>>();
            if line.len() > MAX_PAYLOAD_BYTES {
                return ReadOutcome::Oversized;
            }
            return match String::from_utf8(line) {
                Ok(line) => ReadOutcome::Line(line.trim_end_matches(['\r', '\n']).to_string()),
                Err(_) => ReadOutcome::InvalidUtf8,
            };
        }
        if buffer.len() > MAX_PAYLOAD_BYTES {
            return ReadOutcome::Oversized;
        }
        let mut chunk = [0_u8; 4096];
        match reader.read(&mut chunk).await {
            Ok(0) | Err(_) => return ReadOutcome::Closed,
            Ok(read) => buffer.extend_from_slice(&chunk[..read]),
        }
    }
}

async fn write_raw_line(
    writer: &mut tokio::io::WriteHalf<tokio::net::windows::named_pipe::NamedPipeServer>,
    line: &str,
) -> std::io::Result<()> {
    writer.write_all(line.as_bytes()).await?;
    writer.write_all(b"\n").await?;
    writer.flush().await
}

/// 启动 CommandServer accept 循环。
pub async fn run_command_server(
    pipe_name: String,
    context: Arc<CommandServerContext>,
) -> std::io::Result<()> {
    let request_ids = Arc::new(Mutex::new(RequestIdCache::new(
        context.shared.agent_version(),
    )));
    loop {
        let server = wuji_windows::create_pipe_server(&pipe_name)?;
        if server.connect().await.is_err() {
            continue;
        }
        let context = context.clone();
        let request_ids = request_ids.clone();
        tokio::spawn(async move {
            serve_connection(server, context, request_ids).await;
        });
    }
}

/// 最小 Pipe 客户端（供 e2e 测试与 V01-6 Desktop 参考实现使用）。
pub mod client {
    use super::*;
    use std::fs::OpenOptions;
    use std::io::{BufRead, BufReader, Write};

    pub struct PipeClient {
        reader: BufReader<std::fs::File>,
        writer: std::fs::File,
    }

    impl PipeClient {
        pub fn connect(pipe_name: &str) -> std::io::Result<Self> {
            let file = OpenOptions::new().read(true).write(true).open(pipe_name)?;
            Ok(Self {
                reader: BufReader::new(file.try_clone()?),
                writer: file,
            })
        }

        pub fn call(
            &mut self,
            request_id: &str,
            command: &str,
            payload: serde_json::Value,
        ) -> serde_json::Value {
            self.call_with_protocol(request_id, command, payload, PROTOCOL_VERSION)
        }

        pub fn call_with_protocol(
            &mut self,
            request_id: &str,
            command: &str,
            payload: serde_json::Value,
            protocol_version: u32,
        ) -> serde_json::Value {
            let envelope = serde_json::json!({
                "protocolVersion": protocol_version,
                "requestId": request_id,
                "command": command,
                "sentAtUtcMs": "0",
                "payload": payload,
            });
            let mut line = serde_json::to_vec(&envelope).expect("envelope");
            line.push(b'\n');
            self.writer.write_all(&line).expect("write");
            self.writer.flush().expect("flush");
            let mut response = String::new();
            self.reader.read_line(&mut response).expect("read");
            serde_json::from_str(&response).expect("response json")
        }

        pub fn hello(&mut self, channel: &str) -> serde_json::Value {
            self.call(
                &ulid::Ulid::generate().to_string(),
                "hello",
                serde_json::json!({
                    "desktopVersion": "0.1.0",
                    "protocolVersion": PROTOCOL_VERSION,
                    "channel": channel,
                }),
            )
        }
    }
}

#[cfg(test)]
mod request_cache_tests {
    use super::*;

    fn completed(response: &str, last_accessed: Instant) -> RequestState {
        RequestState::Completed {
            response: response.to_string(),
            last_accessed,
        }
    }

    #[test]
    fn active_unknown_survives_ttl_and_capacity_pressure() {
        let mut cache = RequestIdCache::new("0.1.0".to_string());
        let now = Instant::now();
        let (_tx, rx) = watch::channel(None);
        cache.entries.insert(
            "active".to_string(),
            (
                1,
                RequestState::Active {
                    receiver: rx,
                    created_at: now - IN_PROGRESS_MAX_AGE - Duration::from_secs(1),
                    generation: 7,
                    visible_response: None,
                },
            ),
        );
        for index in 0..(REQUEST_ID_CAPACITY - 1) {
            cache
                .entries
                .insert(format!("completed-{index}"), (2, completed("done", now)));
        }

        cache.purge_expired_at(now);
        assert_eq!(cache.active_count(), 1);
        assert_eq!(cache.entries.len(), REQUEST_ID_CAPACITY);
        assert!(matches!(
            cache.entries.get("active"),
            Some((
                _,
                RequestState::Active {
                    visible_response: Some(_),
                    ..
                }
            ))
        ));

        // Completed TTL 到期后 Active 仍保留；重新施加满容量压力也只能淘汰 Completed。
        cache.purge_expired_at(now + REQUEST_ID_TTL + Duration::from_secs(1));
        assert_eq!(cache.entries.len(), 1);
        assert!(cache.entries.contains_key("active"));
        for index in 0..(REQUEST_ID_CAPACITY - 1) {
            cache.entries.insert(
                format!("refill-{index}"),
                (3, completed("done", now + REQUEST_ID_TTL)),
            );
        }
        assert!(cache.make_room_for_new());
        assert_eq!(cache.entries.len(), REQUEST_ID_CAPACITY - 1);
        assert!(cache.entries.contains_key("active"));
    }

    #[test]
    fn capacity_is_strict_and_completed_eviction_is_lru() {
        let mut cache = RequestIdCache::new("0.1.0".to_string());
        let now = Instant::now();
        let base = now - Duration::from_secs(10);
        for index in 0..REQUEST_ID_CAPACITY {
            cache.entries.insert(
                format!("id-{index}"),
                (
                    index as u64,
                    completed("done", base + Duration::from_millis(index as u64)),
                ),
            );
        }
        // 模拟一次缓存命中刷新 id-0，使原本最老的 id-0 不再是 LRU。
        let Some((_, RequestState::Completed { last_accessed, .. })) =
            cache.entries.get_mut("id-0")
        else {
            panic!("id-0 must be completed");
        };
        *last_accessed = now;

        assert!(cache.make_room_for_new());
        assert_eq!(cache.entries.len(), REQUEST_ID_CAPACITY - 1);
        assert!(
            cache.entries.contains_key("id-0"),
            "cache hit must refresh LRU"
        );
        assert!(
            !cache.entries.contains_key("id-1"),
            "oldest untouched entry is evicted"
        );
        cache
            .entries
            .insert("new-id".to_string(), (9999, completed("new", now)));
        assert_eq!(cache.entries.len(), REQUEST_ID_CAPACITY);
    }

    #[tokio::test]
    async fn abort_before_first_poll_completes_active_execution() {
        let request_ids = Arc::new(Mutex::new(RequestIdCache::new("0.1.0".to_string())));
        let (tx, mut rx) = watch::channel(None);
        request_ids.lock().unwrap().entries.insert(
            "cancelled".to_string(),
            (
                42,
                RequestState::Active {
                    receiver: rx.clone(),
                    created_at: Instant::now(),
                    generation: 9,
                    visible_response: None,
                },
            ),
        );
        // 和生产路径相同：guard 在 spawn 前创建，随后不 yield 立即 abort。
        let guard = ExecutionCompletionGuard {
            request_ids: request_ids.clone(),
            request_id: "cancelled".to_string(),
            payload_hash: 42,
            generation: 9,
            agent_version: "0.1.0".to_string(),
            result_tx: tx,
            armed: true,
        };
        let task = tokio::spawn(async move {
            let _guard = guard;
            std::future::pending::<()>().await;
        });
        task.abort();
        assert!(task.await.unwrap_err().is_cancelled());

        rx.changed()
            .await
            .expect("guard publishes cancellation result");
        let response = rx.borrow().clone().expect("stable cancellation response");
        assert!(response.contains("INTERNAL_SAFE_ERROR"));
        assert!(response.contains("命令处理被中断"));
        assert!(matches!(
            request_ids.lock().unwrap().entries.get("cancelled"),
            Some((_, RequestState::Completed { .. }))
        ));
    }
}
