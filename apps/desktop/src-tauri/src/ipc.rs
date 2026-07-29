//! Agent Named Pipe 客户端（09 §8.1、§8.2）。
//!
//! 连接惰性建立：首个调用时 connect + hello 握手；传输错误后丢弃连接，
//! 下次调用重连。React 不接触 Pipe 名与 envelope（09 §8.3）。

use std::time::Duration;

use serde_json::{Value, json};
use tokio::io::{AsyncReadExt, AsyncWriteExt, BufReader};
use tokio::net::windows::named_pipe::{ClientOptions, NamedPipeClient};
use tokio::sync::Mutex;
use wuji_core::error::{SafeError, SafeErrorCode};

use crate::paths;

const REQUEST_TIMEOUT: Duration = Duration::from_secs(3);
const MAX_PAYLOAD_BYTES: usize = 64 * 1024;
const PROTOCOL_VERSION: u32 = 1;

pub struct AgentIpcClient {
    pipe_name: String,
    channel: String,
    desktop_version: String,
    connection: Mutex<Option<Connection>>,
}

struct Connection {
    reader: BufReader<tokio::io::ReadHalf<NamedPipeClient>>,
    writer: tokio::io::WriteHalf<NamedPipeClient>,
}

impl AgentIpcClient {
    pub fn new(channel: &str, desktop_version: &str) -> Result<Self, String> {
        let (pipe_name, _) = paths::channel_names(channel)?;
        Ok(Self {
            pipe_name,
            channel: channel.to_string(),
            desktop_version: desktop_version.to_string(),
            connection: Mutex::new(None),
        })
    }

    /// 测试与诊断用 Pipe 名。
    #[allow(dead_code)]
    pub fn pipe_name_for_tests(&self) -> &str {
        &self.pipe_name
    }

    async fn connect_locked(&self) -> Result<Connection, SafeError> {
        let unavailable = || SafeError::new(SafeErrorCode::InternalSafeError, "无法连接 Agent");
        let client = ClientOptions::new()
            .open(&self.pipe_name)
            .map_err(|_| unavailable())?;
        let (read_half, write_half) = tokio::io::split(client);
        let mut connection = Connection {
            reader: BufReader::new(read_half),
            writer: write_half,
        };
        let hello = json!({
            "desktopVersion": self.desktop_version,
            "protocolVersion": PROTOCOL_VERSION,
            "channel": self.channel,
        });
        let hello_request_id = ulid::Ulid::generate().to_string();
        let response = self
            .roundtrip_on(&mut connection, "hello", hello, &hello_request_id)
            .await?;
        if !response["ok"].as_bool().unwrap_or(false) {
            let code = response["error"]["code"].as_str().unwrap_or_default();
            let mapped = match code {
                "IPC_CHANNEL_MISMATCH" => SafeErrorCode::IpcChannelMismatch,
                "IPC_PROTOCOL_UNSUPPORTED" => SafeErrorCode::IpcProtocolUnsupported,
                _ => SafeErrorCode::InternalSafeError,
            };
            return Err(SafeError::new(mapped, "Agent 握手被拒绝"));
        }
        Ok(connection)
    }

    async fn roundtrip_on(
        &self,
        connection: &mut Connection,
        command: &str,
        payload: Value,
        request_id: &str,
    ) -> Result<Value, SafeError> {
        let transport = || SafeError::new(SafeErrorCode::InternalSafeError, "与 Agent 的传输失败");
        let sent_at_utc_ms = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|elapsed| elapsed.as_millis().to_string())
            .unwrap_or_else(|_| "0".to_string());
        let envelope = json!({
            "protocolVersion": PROTOCOL_VERSION,
            "requestId": request_id,
            "command": command,
            "sentAtUtcMs": sent_at_utc_ms,
            "payload": payload,
        });
        let mut line = serde_json::to_vec(&envelope)
            .map_err(|_| SafeError::new(SafeErrorCode::InternalSafeError, "序列化失败"))?;
        line.push(b'\n');
        if line.len() > MAX_PAYLOAD_BYTES {
            return Err(SafeError::new(
                SafeErrorCode::IpcPayloadTooLarge,
                "请求超过 64 KiB 上限",
            ));
        }
        tokio::time::timeout(REQUEST_TIMEOUT, async {
            connection
                .writer
                .write_all(&line)
                .await
                .map_err(|_| transport())?;
            connection.writer.flush().await.map_err(|_| transport())?;
            let mut buffer = Vec::new();
            let mut chunk = [0_u8; 4096];
            loop {
                if let Some(position) = buffer.iter().position(|b| *b == b'\n') {
                    let response_line = buffer.drain(..=position).collect::<Vec<u8>>();
                    let response: Value = serde_json::from_slice(&response_line).map_err(|_| {
                        SafeError::new(SafeErrorCode::InternalSafeError, "响应不是合法 JSON")
                    })?;
                    return Ok(response);
                }
                if buffer.len() > MAX_PAYLOAD_BYTES {
                    return Err(SafeError::new(
                        SafeErrorCode::InternalSafeError,
                        "响应超过上限",
                    ));
                }
                let read = connection
                    .reader
                    .read(&mut chunk)
                    .await
                    .map_err(|_| transport())?;
                if read == 0 {
                    return Err(SafeError::new(
                        SafeErrorCode::InternalSafeError,
                        "连接已断开",
                    ));
                }
                buffer.extend_from_slice(&chunk[..read]);
            }
        })
        .await
        .map_err(|_| SafeError::new(SafeErrorCode::InternalSafeError, "命令处理超时"))?
    }

    /// 调用 Agent 命令；传输层失败后下次调用自动重连。
    /// timeout/断线后用同一 request ID 重试一次（审核 R05）：服务端 request cache
    /// 对已接受命令返回真实结果，不会重复执行副作用。
    pub async fn call(&self, command: &str, payload: Value) -> Result<Value, SafeError> {
        let request_id = ulid::Ulid::generate().to_string();
        let mut guard = self.connection.lock().await;
        if guard.is_none() {
            *guard = Some(self.connect_locked().await?);
        }
        let connection = guard.as_mut().expect("connection just established");
        match self
            .roundtrip_on(connection, command, payload.clone(), &request_id)
            .await
        {
            Ok(response) => Ok(response),
            Err(first_error) => {
                *guard = None;
                let mut fresh = match self.connect_locked().await {
                    Ok(connection) => connection,
                    Err(_) => return Err(first_error),
                };
                match self
                    .roundtrip_on(&mut fresh, command, payload, &request_id)
                    .await
                {
                    Ok(response) => {
                        *guard = Some(fresh);
                        Ok(response)
                    }
                    Err(_) => Err(first_error),
                }
            }
        }
    }

    /// 解析后的便捷方法：status_get 返回 (ok, result)。
    pub async fn status(&self) -> Result<Value, SafeError> {
        self.call("status_get", json!({})).await
    }

    /// 主动释放缓存连接。Agent 接受 graceful shutdown 后调用，避免 Desktop
    /// 继续持有旧 Pipe，并确保后续 Start 重新握手到新的 runtime。
    pub async fn disconnect(&self) {
        *self.connection.lock().await = None;
    }
}
