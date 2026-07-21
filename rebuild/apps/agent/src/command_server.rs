//! CommandServer：Named Pipe IPC（09 §8.1、§8.2）。
//!
//! 单行 UTF-8 JSON envelope、hello 握手、64 KiB 上限、3 秒 timeout、
//! request ID 幂等（in-progress/completed）、Capture 状态机与稳定错误码。

use std::collections::HashMap;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use serde::{Deserialize, Serialize};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::sync::{mpsc, oneshot, watch};
use wuji_core::domain::CaptureState;
use wuji_core::error::{SafeError, SafeErrorCode};
use wuji_core::settings::Settings;

use crate::activity::EngineEvent;
use crate::capture_loop::now_utc_ms;
use crate::shared::SharedState;
use crate::writer_task::WriterControl;

pub const PROTOCOL_VERSION: u32 = 1;
pub const MAX_PAYLOAD_BYTES: usize = 64 * 1024;
pub const REQUEST_TIMEOUT: Duration = Duration::from_secs(3);
const REQUEST_ID_TTL: Duration = Duration::from_secs(600);
const REQUEST_ID_CAPACITY: usize = 1024;

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct RequestEnvelope {
    protocol_version: u32,
    request_id: String,
    command: String,
    #[allow(dead_code)]
    sent_at_utc_ms: String,
    #[serde(default)]
    payload: serde_json::Value,
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
    InProgress(watch::Receiver<Option<String>>),
    Completed(String, Instant),
}

struct RequestIdCache {
    entries: HashMap<String, (u64, RequestState)>,
}

impl RequestIdCache {
    fn new() -> Self {
        Self {
            entries: HashMap::new(),
        }
    }

    fn purge_expired(&mut self) {
        let now = Instant::now();
        self.entries.retain(|_, (_, state)| match state {
            RequestState::InProgress(_) => true,
            RequestState::Completed(_, at) => now.duration_since(*at) < REQUEST_ID_TTL,
        });
        if self.entries.len() > REQUEST_ID_CAPACITY {
            // 简单容量保护：清空最老的 Completed 条目。
            let mut completed: Vec<(String, Instant)> = self
                .entries
                .iter()
                .filter_map(|(id, (_, state))| match state {
                    RequestState::Completed(_, at) => Some((id.clone(), *at)),
                    _ => None,
                })
                .collect();
            completed.sort_by_key(|(_, at)| *at);
            for (id, _) in completed
                .into_iter()
                .take(self.entries.len() - REQUEST_ID_CAPACITY)
            {
                self.entries.remove(&id);
            }
        }
    }
}

/// IPC 服务端共享上下文。
pub struct CommandServerContext {
    pub shared: Arc<SharedState>,
    pub control_tx: mpsc::Sender<WriterControl>,
    pub capture_state_tx: watch::Sender<CaptureState>,
    pub settings_tx: watch::Sender<Settings>,
    pub settings_path: std::path::PathBuf,
    pub settings_digest_for: fn(&Settings) -> String,
    pub shutdown_tx: watch::Sender<bool>,
    pub channel: String,
}

/// Capture 状态机（09 §8.2 转换表）。
pub fn capture_transition(
    current: CaptureState,
    command: &str,
) -> Result<CaptureState, Option<CaptureState>> {
    match (command, current) {
        ("capture_start", CaptureState::Stopped) => Ok(CaptureState::Running),
        ("capture_start", CaptureState::Running) => Err(Some(CaptureState::Running)),
        ("capture_pause", CaptureState::Running) => Ok(CaptureState::Paused),
        ("capture_pause", CaptureState::Paused) => Err(Some(CaptureState::Paused)),
        ("capture_resume", CaptureState::Paused) => Ok(CaptureState::Running),
        ("capture_resume", CaptureState::Running) => Err(Some(CaptureState::Running)),
        ("capture_stop", CaptureState::Running | CaptureState::Paused) => Ok(CaptureState::Stopped),
        ("capture_stop", CaptureState::Stopped) => Err(Some(CaptureState::Stopped)),
        _ => Err(None),
    }
}

fn capture_lifecycle_event(command: &str, at_utc_ms: i64) -> Option<EngineEvent> {
    match command {
        "capture_pause" => Some(EngineEvent::CapturePaused { at_utc_ms }),
        "capture_stop" => Some(EngineEvent::CaptureStopped { at_utc_ms }),
        _ => None,
    }
}

/// 请求分发（同步；在连接任务内调用，带 3 秒 timeout 由外层包裹）。
async fn dispatch(
    context: &CommandServerContext,
    request: &RequestEnvelope,
) -> Result<serde_json::Value, SafeError> {
    let now = now_utc_ms();
    match request.command.as_str() {
        "status_get" => Ok(serde_json::to_value(context.shared.status_dto()).unwrap()),
        "capture_start" | "capture_pause" | "capture_resume" | "capture_stop" => {
            let current = context.shared.capture_state();
            match capture_transition(current, &request.command) {
                Ok(next) => {
                    if let Some(event) = capture_lifecycle_event(&request.command, now) {
                        let (ack_tx, ack_rx) = oneshot::channel();
                        context
                            .control_tx
                            .send(WriterControl::Lifecycle { event, ack: ack_tx })
                            .await
                            .map_err(|_| {
                                SafeError::new(SafeErrorCode::InternalSafeError, "控制通道不可用")
                            })?;
                        ack_rx
                            .await
                            .map_err(|_| {
                                SafeError::new(SafeErrorCode::InternalSafeError, "控制确认失败")
                            })?
                            .map_err(|error| SafeError::new(error.code, "采集状态变更提交失败"))?;
                    }
                    let _ = context.capture_state_tx.send(next);
                    context.shared.set_capture_state(next);
                    Ok(serde_json::to_value(context.shared.status_dto()).unwrap())
                }
                Err(Some(state)) => {
                    // 幂等成功（09 §8.2 转换表）。
                    let _ = state;
                    Ok(serde_json::to_value(context.shared.status_dto()).unwrap())
                }
                Err(None) => Err(SafeError::new(
                    SafeErrorCode::CaptureInvalidState,
                    "当前状态不能执行该采集命令",
                )),
            }
        }
        "settings_reload" => {
            let saved_revision = request
                .payload
                .get("savedRevision")
                .and_then(|v| v.as_str())
                .unwrap_or_default()
                .to_string();
            let expected_digest = request
                .payload
                .get("contentDigest")
                .and_then(|v| v.as_str())
                .unwrap_or_default()
                .to_string();
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
            let (ack_tx, ack_rx) = oneshot::channel();
            context
                .control_tx
                .send(WriterControl::SettingsApplied {
                    settings: settings.clone(),
                    at_utc_ms: now,
                    ack: ack_tx,
                })
                .await
                .map_err(|_| SafeError::new(SafeErrorCode::InternalSafeError, "控制通道不可用"))?;
            let applied = ack_rx
                .await
                .map_err(|_| SafeError::new(SafeErrorCode::InternalSafeError, "控制确认失败"))?
                .map_err(|error| SafeError::new(error.code, error.message))?;
            // 应用成功：采集循环切换到新 settings（09 §9.1：只影响未来数据）。
            let _ = context.settings_tx.send(settings);
            Ok(serde_json::json!({ "appliedRevision": applied.to_string() }))
        }
        "agent_shutdown_dev" => {
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
    let channel_ok = hello
        .payload
        .get("channel")
        .and_then(|v| v.as_str())
        .is_some_and(|channel| channel == context.channel);
    if !channel_ok {
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

async fn handle_request_line(
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

    // request ID 幂等（09 §8.2）。
    let payload_hash = {
        use std::hash::{Hash, Hasher};
        let mut hasher = std::collections::hash_map::DefaultHasher::new();
        request.command.hash(&mut hasher);
        request.payload.to_string().hash(&mut hasher);
        hasher.finish()
    };
    enum CacheEntry {
        Fresh(watch::Sender<Option<String>>),
        Wait(watch::Receiver<Option<String>>),
        Done(String),
        Conflict,
    }
    let entry = {
        let mut cache = request_ids.lock().expect("request id cache");
        cache.purge_expired();
        match cache.entries.get(&request.request_id) {
            Some((hash, RequestState::Completed(response, _))) if *hash == payload_hash => {
                CacheEntry::Done(response.clone())
            }
            Some((hash, _)) if *hash != payload_hash => CacheEntry::Conflict,
            Some((_, RequestState::InProgress(receiver))) => CacheEntry::Wait(receiver.clone()),
            Some((_, RequestState::Completed(response, _))) => CacheEntry::Done(response.clone()),
            None => {
                let (tx, rx) = watch::channel(None);
                cache.entries.insert(
                    request.request_id.clone(),
                    (payload_hash, RequestState::InProgress(rx)),
                );
                CacheEntry::Fresh(tx)
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
            // 相同 ID + 相同 payload：等待原任务结果（09 §8.2）。
            let _ = receiver.changed().await;
            receiver.borrow().clone().unwrap_or_else(|| {
                serialize_response(&err_response(
                    &request.request_id,
                    &agent_version,
                    SafeError::new(SafeErrorCode::InternalSafeError, "原命令未完成"),
                ))
            })
        }
        CacheEntry::Fresh(tx) => {
            let response = match tokio::time::timeout(REQUEST_TIMEOUT, dispatch(context, &request))
                .await
            {
                Ok(Ok(result)) => {
                    serialize_response(&ok_response(&request.request_id, &agent_version, result))
                }
                Ok(Err(error)) => {
                    serialize_response(&err_response(&request.request_id, &agent_version, error))
                }
                Err(_) => serialize_response(&err_response(
                    &request.request_id,
                    &agent_version,
                    request_timeout_result(),
                )),
            };
            {
                let mut cache = request_ids.lock().expect("request id cache");
                cache.entries.insert(
                    request.request_id.clone(),
                    (
                        payload_hash,
                        RequestState::Completed(response.clone(), Instant::now()),
                    ),
                );
            }
            let _ = tx.send(Some(response.clone()));
            response
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
            let line = String::from_utf8_lossy(&line)
                .trim_end_matches(['\r', '\n'])
                .to_string();
            return ReadOutcome::Line(line);
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
    let request_ids = Arc::new(Mutex::new(RequestIdCache::new()));
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
mod tests {
    use super::*;

    #[test]
    fn capture_transition_table_matches_baseline() {
        // start：stopped → running；running 幂等；paused 拒绝。
        assert_eq!(
            capture_transition(CaptureState::Stopped, "capture_start"),
            Ok(CaptureState::Running)
        );
        assert_eq!(
            capture_transition(CaptureState::Running, "capture_start"),
            Err(Some(CaptureState::Running))
        );
        assert_eq!(
            capture_transition(CaptureState::Paused, "capture_start"),
            Err(None)
        );
        // pause：running → paused；paused 幂等；stopped 拒绝。
        assert_eq!(
            capture_transition(CaptureState::Running, "capture_pause"),
            Ok(CaptureState::Paused)
        );
        assert_eq!(
            capture_transition(CaptureState::Paused, "capture_pause"),
            Err(Some(CaptureState::Paused))
        );
        assert_eq!(
            capture_transition(CaptureState::Stopped, "capture_pause"),
            Err(None)
        );
        // resume：paused → running；running 幂等；stopped 拒绝。
        assert_eq!(
            capture_transition(CaptureState::Paused, "capture_resume"),
            Ok(CaptureState::Running)
        );
        assert_eq!(
            capture_transition(CaptureState::Running, "capture_resume"),
            Err(Some(CaptureState::Running))
        );
        assert_eq!(
            capture_transition(CaptureState::Stopped, "capture_resume"),
            Err(None)
        );
        // stop：running/paused → stopped；stopped 幂等。
        assert_eq!(
            capture_transition(CaptureState::Running, "capture_stop"),
            Ok(CaptureState::Stopped)
        );
        assert_eq!(
            capture_transition(CaptureState::Paused, "capture_stop"),
            Ok(CaptureState::Stopped)
        );
        assert_eq!(
            capture_transition(CaptureState::Stopped, "capture_stop"),
            Err(Some(CaptureState::Stopped))
        );
    }

    #[test]
    fn unknown_command_has_no_transition() {
        assert_eq!(capture_transition(CaptureState::Running, "nope"), Err(None));
    }
}
