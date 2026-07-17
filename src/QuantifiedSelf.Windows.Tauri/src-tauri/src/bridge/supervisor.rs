use std::{collections::HashMap, path::PathBuf, process::Stdio, time::Duration};

use serde::{Serialize, de::DeserializeOwned};
use serde_json::{Value, json};
use tauri::{AppHandle, Emitter};
use tokio::{
    io::{AsyncBufRead, AsyncBufReadExt, AsyncWrite, AsyncWriteExt, BufReader, Lines},
    process::{Child, ChildStdin, ChildStdout, Command},
    sync::{mpsc, oneshot, watch},
    time::{Instant, interval, sleep, timeout},
};
use uuid::Uuid;

use crate::contracts::{
    BridgeHelloResult, BridgeRequestEnvelope, BridgeRequestMeta, BridgeResponseEnvelope,
    ClientInitializeResult, SettingsFieldError,
};

const API_VERSION: &str = "1.0";
const JSON_RPC_VERSION: &str = "2.0";
const MAX_MESSAGE_BYTES: usize = 1024 * 1024;
const RESTART_BACKOFF: Duration = Duration::from_millis(350);
const STARTUP_TIMEOUT: Duration = Duration::from_secs(10);
const REQUEST_TIMEOUT: Duration = Duration::from_secs(12);
const READ_ONLY_QUERY_TIMEOUT: Duration = Duration::from_secs(12);
const SETTINGS_READ_TIMEOUT: Duration = Duration::from_secs(12);
const SETTINGS_UPDATE_TIMEOUT: Duration = Duration::from_secs(18);
const STOP_TIMEOUT: Duration = Duration::from_secs(22);
const SHUTDOWN_TIMEOUT: Duration = Duration::from_secs(5);
const STABILITY_WINDOW: Duration = Duration::from_secs(30);

pub const BRIDGE_AVAILABILITY_EVENT: &str = "bridge://availability";

#[derive(Debug, Clone, Serialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct CommandError {
    pub code: String,
    pub message: String,
    pub retryable: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub correlation_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub field_errors: Option<Vec<SettingsFieldError>>,
}

impl CommandError {
    fn unavailable() -> Self {
        Self {
            code: "bridge_unavailable".into(),
            message: "无法连接本地服务，请稍后重试。".into(),
            retryable: true,
            correlation_id: None,
            field_errors: None,
        }
    }

    fn protocol() -> Self {
        Self {
            code: "bridge_protocol_error".into(),
            message: "本地服务返回了无法识别的响应。".into(),
            retryable: true,
            correlation_id: None,
            field_errors: None,
        }
    }

    fn shutting_down() -> Self {
        Self {
            code: "bridge_shutting_down".into(),
            message: "应用正在退出，无法接受新请求。".into(),
            retryable: false,
            correlation_id: None,
            field_errors: None,
        }
    }

    fn timeout(method: &str) -> Self {
        let side_effect = matches!(
            method,
            "agent.start" | "agent.pause" | "agent.resume" | "agent.stop" | "settings.update"
        );
        Self {
            code: "request_timeout".into(),
            message: match method {
                "settings.update" => "保存请求超时；设置可能已经保存，请先重新读取设置。",
                _ if side_effect => "请求超时；命令可能已经执行，请先刷新 Agent 状态。",
                _ => "请求超时，请稍后重试。",
            }
            .into(),
            retryable: !side_effect,
            correlation_id: None,
            field_errors: None,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
enum Availability {
    Starting,
    Ready,
    Unavailable,
    CircuitOpen,
    ShuttingDown,
}

#[derive(Debug, Clone, Copy, Serialize)]
#[serde(rename_all = "camelCase")]
struct AvailabilityEvent {
    state: Availability,
    generation: u64,
}

pub struct BridgeSupervisor {
    command_tx: mpsc::Sender<WorkerCommand>,
    _availability: watch::Receiver<Availability>,
}

impl BridgeSupervisor {
    pub fn start(path: PathBuf, app_handle: AppHandle) -> Self {
        let (command_tx, command_rx) = mpsc::channel(32);
        let (availability_tx, availability) = watch::channel(Availability::Starting);
        tauri::async_runtime::spawn(supervisor_worker(
            command_rx,
            availability_tx,
            path,
            app_handle,
        ));
        Self {
            command_tx,
            _availability: availability,
        }
    }

    pub async fn request<T>(&self, method: &'static str) -> Result<T, CommandError>
    where
        T: DeserializeOwned,
    {
        self.request_with_params(method, json!({})).await
    }

    pub async fn request_with_params<T, P>(
        &self,
        method: &'static str,
        params: P,
    ) -> Result<T, CommandError>
    where
        T: DeserializeOwned,
        P: Serialize,
    {
        let params = serde_json::to_value(params).map_err(|_| CommandError::protocol())?;
        let value = self.request_value(method, params).await?;
        serde_json::from_value(value).map_err(|_| CommandError::protocol())
    }

    pub async fn retry(&self) -> Result<ClientInitializeResult, CommandError> {
        let (response_tx, response_rx) = oneshot::channel();
        self.command_tx
            .send(WorkerCommand::Retry { response_tx })
            .await
            .map_err(|_| CommandError::unavailable())?;
        response_rx.await.map_err(|_| CommandError::unavailable())?
    }

    pub async fn shutdown(&self) -> Result<(), CommandError> {
        let (response_tx, response_rx) = oneshot::channel();
        self.command_tx
            .send(WorkerCommand::Shutdown { response_tx })
            .await
            .map_err(|_| CommandError::shutting_down())?;
        response_rx.await.map_err(|_| CommandError::unavailable())?
    }

    async fn request_value(
        &self,
        method: &'static str,
        params: Value,
    ) -> Result<Value, CommandError> {
        let (response_tx, response_rx) = oneshot::channel();
        self.command_tx
            .send(WorkerCommand::Request {
                method,
                params,
                response_tx,
            })
            .await
            .map_err(|_| CommandError::unavailable())?;
        response_rx.await.map_err(|_| CommandError::unavailable())?
    }
}

enum WorkerCommand {
    Request {
        method: &'static str,
        params: Value,
        response_tx: oneshot::Sender<Result<Value, CommandError>>,
    },
    Retry {
        response_tx: oneshot::Sender<Result<ClientInitializeResult, CommandError>>,
    },
    Shutdown {
        response_tx: oneshot::Sender<Result<(), CommandError>>,
    },
}

struct PendingRequest {
    method: &'static str,
    deadline: Instant,
    response_tx: oneshot::Sender<Result<Value, CommandError>>,
}

struct ShutdownRequest {
    id: String,
    deadline: Instant,
    response_tx: oneshot::Sender<Result<(), CommandError>>,
}

struct BridgeSession {
    child: Child,
    stdin: ChildStdin,
    stdout: Lines<BufReader<ChildStdout>>,
    initialization: ClientInitializeResult,
}

enum SessionEnd {
    Crashed(Duration),
    Retry(oneshot::Sender<Result<ClientInitializeResult, CommandError>>),
    Shutdown,
}

enum RecoveryAction {
    Retry(oneshot::Sender<Result<ClientInitializeResult, CommandError>>),
    Shutdown,
}

async fn supervisor_worker(
    mut command_rx: mpsc::Receiver<WorkerCommand>,
    availability_tx: watch::Sender<Availability>,
    path: PathBuf,
    app_handle: AppHandle,
) {
    let mut automatic_restarts = 0_u8;
    let mut retry_response: Option<oneshot::Sender<Result<ClientInitializeResult, CommandError>>> =
        None;
    let mut generation = 0_u64;

    loop {
        publish_availability(
            &availability_tx,
            &app_handle,
            Availability::Starting,
            generation,
        );
        match spawn_initialized_bridge(&path).await {
            Ok(session) => {
                generation += 1;
                let initialization = session.initialization.clone();
                if let Some(response_tx) = retry_response.take() {
                    let _ = response_tx.send(Ok(initialization));
                }
                publish_availability(
                    &availability_tx,
                    &app_handle,
                    Availability::Ready,
                    generation,
                );

                match run_session(
                    session,
                    &mut command_rx,
                    &availability_tx,
                    &app_handle,
                    generation,
                )
                .await
                {
                    SessionEnd::Shutdown => return,
                    SessionEnd::Retry(response_tx) => {
                        automatic_restarts = 0;
                        retry_response = Some(response_tx);
                    }
                    SessionEnd::Crashed(uptime) => {
                        if can_automatically_restart(automatic_restarts, uptime) {
                            automatic_restarts = 1;
                            sleep(RESTART_BACKOFF).await;
                        } else {
                            publish_availability(
                                &availability_tx,
                                &app_handle,
                                Availability::CircuitOpen,
                                generation,
                            );
                            match wait_for_recovery(&mut command_rx).await {
                                RecoveryAction::Retry(response_tx) => {
                                    automatic_restarts = 0;
                                    retry_response = Some(response_tx);
                                }
                                RecoveryAction::Shutdown => return,
                            }
                        }
                    }
                }
            }
            Err(error) => {
                if let Some(response_tx) = retry_response.take() {
                    let _ = response_tx.send(Err(error.clone()));
                }
                if automatic_restarts == 0 {
                    automatic_restarts += 1;
                    sleep(RESTART_BACKOFF).await;
                    continue;
                }

                publish_availability(
                    &availability_tx,
                    &app_handle,
                    Availability::Unavailable,
                    generation,
                );
                match wait_for_recovery(&mut command_rx).await {
                    RecoveryAction::Retry(response_tx) => {
                        automatic_restarts = 0;
                        retry_response = Some(response_tx);
                    }
                    RecoveryAction::Shutdown => return,
                }
            }
        }
    }
}

async fn wait_for_recovery(command_rx: &mut mpsc::Receiver<WorkerCommand>) -> RecoveryAction {
    while let Some(command) = command_rx.recv().await {
        match command {
            WorkerCommand::Request { response_tx, .. } => {
                let _ = response_tx.send(Err(CommandError::unavailable()));
            }
            WorkerCommand::Retry { response_tx } => return RecoveryAction::Retry(response_tx),
            WorkerCommand::Shutdown { response_tx } => {
                let _ = response_tx.send(Ok(()));
                return RecoveryAction::Shutdown;
            }
        }
    }
    RecoveryAction::Shutdown
}

fn publish_availability(
    availability_tx: &watch::Sender<Availability>,
    app_handle: &AppHandle,
    state: Availability,
    generation: u64,
) {
    let _ = availability_tx.send(state);
    let _ = app_handle.emit(
        BRIDGE_AVAILABILITY_EVENT,
        AvailabilityEvent { state, generation },
    );
}

async fn spawn_initialized_bridge(path: &PathBuf) -> Result<BridgeSession, CommandError> {
    let mut command = Command::new(path);
    command
        .arg("--channel")
        .arg("dev")
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .kill_on_drop(true);

    #[cfg(windows)]
    command.creation_flags(0x0800_0000);

    let mut child = command.spawn().map_err(|_| CommandError::unavailable())?;
    let mut stdin = child.stdin.take().ok_or_else(CommandError::unavailable)?;
    let stdout = child.stdout.take().ok_or_else(CommandError::unavailable)?;
    let stderr = child.stderr.take().ok_or_else(CommandError::unavailable)?;
    let mut stdout = BufReader::new(stdout).lines();

    tauri::async_runtime::spawn(async move {
        let mut lines = BufReader::new(stderr).lines();
        while let Ok(Some(line)) = lines.next_line().await {
            eprintln!(
                "Bridge emitted a diagnostic line ({} bytes).",
                line.len().min(4096)
            );
        }
    });

    let hello: BridgeHelloResult =
        exchange(&mut stdin, &mut stdout, "bridge.hello", STARTUP_TIMEOUT).await?;
    if !compatible_api(&hello.api_version)
        || !hello
            .capabilities
            .iter()
            .any(|item| item == "client.initialize")
    {
        let _ = child.kill().await;
        return Err(CommandError::protocol());
    }

    let initialization = exchange(
        &mut stdin,
        &mut stdout,
        "client.initialize",
        STARTUP_TIMEOUT,
    )
    .await?;
    Ok(BridgeSession {
        child,
        stdin,
        stdout,
        initialization,
    })
}

async fn exchange<T, W, R>(
    stdin: &mut W,
    stdout: &mut Lines<R>,
    method: &'static str,
    duration: Duration,
) -> Result<T, CommandError>
where
    T: DeserializeOwned,
    W: AsyncWrite + Unpin,
    R: AsyncBufRead + Unpin,
{
    let (id, payload) = create_request(method, json!({}))?;
    write_request(stdin, &payload).await?;
    let line = timeout(duration, stdout.next_line())
        .await
        .map_err(|_| CommandError::timeout(method))?
        .map_err(|_| CommandError::unavailable())?
        .ok_or_else(CommandError::unavailable)?;
    let value = decode_response(&id, &line)?;
    serde_json::from_value(value).map_err(|_| CommandError::protocol())
}

async fn run_session(
    session: BridgeSession,
    command_rx: &mut mpsc::Receiver<WorkerCommand>,
    availability_tx: &watch::Sender<Availability>,
    app_handle: &AppHandle,
    generation: u64,
) -> SessionEnd {
    let started_at = Instant::now();
    let BridgeSession {
        mut child,
        mut stdin,
        mut stdout,
        initialization: _,
    } = session;
    let mut pending = HashMap::<String, PendingRequest>::new();
    let mut ticker = interval(Duration::from_millis(250));
    let mut shutdown: Option<ShutdownRequest> = None;

    loop {
        tokio::select! {
            command = command_rx.recv() => {
                match command {
                    Some(WorkerCommand::Request { method, params, response_tx }) if shutdown.is_none() => {
                        match create_request(method, params) {
                            Ok((id, payload)) => {
                                if write_request(&mut stdin, &payload).await.is_err() {
                                    let _ = response_tx.send(Err(CommandError::unavailable()));
                                    fail_pending(&mut pending, CommandError::unavailable());
                                    return SessionEnd::Crashed(started_at.elapsed());
                                }
                                let timeout = request_timeout(method);
                                pending.insert(id, PendingRequest {
                                    method,
                                    deadline: Instant::now() + timeout,
                                    response_tx,
                                });
                            }
                            Err(error) => { let _ = response_tx.send(Err(error)); }
                        }
                    }
                    Some(WorkerCommand::Request { response_tx, .. }) => {
                        let _ = response_tx.send(Err(CommandError::shutting_down()));
                    }
                    Some(WorkerCommand::Retry { response_tx }) => {
                        fail_pending(&mut pending, CommandError::unavailable());
                        terminate_child(&mut child).await;
                        return SessionEnd::Retry(response_tx);
                    }
                    Some(WorkerCommand::Shutdown { response_tx }) => {
                        publish_availability(
                            availability_tx,
                            app_handle,
                            Availability::ShuttingDown,
                            generation,
                        );
                        fail_pending(&mut pending, CommandError::shutting_down());
                        match create_request("bridge.shutdown", json!({})) {
                            Ok((id, payload)) if write_request(&mut stdin, &payload).await.is_ok() => {
                                shutdown = Some(ShutdownRequest {
                                    id,
                                    deadline: Instant::now() + SHUTDOWN_TIMEOUT,
                                    response_tx,
                                });
                            }
                            _ => {
                                terminate_child(&mut child).await;
                                let _ = response_tx.send(Ok(()));
                                return SessionEnd::Shutdown;
                            }
                        }
                    }
                    None => {
                        terminate_child(&mut child).await;
                        return SessionEnd::Shutdown;
                    }
                }
            }
            line = stdout.next_line() => {
                let line = match line {
                    Ok(Some(value)) if value.len() <= MAX_MESSAGE_BYTES => value,
                    _ => {
                        fail_pending(&mut pending, CommandError::unavailable());
                        terminate_child(&mut child).await;
                        return SessionEnd::Crashed(started_at.elapsed());
                    }
                };
                let envelope = match parse_response(&line) {
                    Ok(value) => value,
                    Err(error) => {
                        fail_pending(&mut pending, error);
                        terminate_child(&mut child).await;
                        return SessionEnd::Crashed(started_at.elapsed());
                    }
                };

                if shutdown.as_ref().is_some_and(|request| request.id == envelope.id) {
                    let request = shutdown.take().expect("shutdown response must exist");
                    let result = response_result(envelope).map(|_| ());
                    let _ = request.response_tx.send(result);
                    wait_or_kill(&mut child).await;
                    return SessionEnd::Shutdown;
                }

                let Some(request) = pending.remove(&envelope.id) else {
                    fail_pending(&mut pending, CommandError::protocol());
                    terminate_child(&mut child).await;
                    return SessionEnd::Crashed(started_at.elapsed());
                };
                let _ = request.response_tx.send(response_result(envelope));
            }
            status = child.wait() => {
                let _ = status;
                fail_pending(&mut pending, CommandError::unavailable());
                if let Some(request) = shutdown.take() {
                    let _ = request.response_tx.send(Ok(()));
                    return SessionEnd::Shutdown;
                }
                return SessionEnd::Crashed(started_at.elapsed());
            }
            _ = ticker.tick() => {
                let now = Instant::now();
                let expired = pending.iter()
                    .filter_map(|(id, request)| (request.deadline <= now).then_some(id.clone()))
                    .collect::<Vec<_>>();
                for id in expired {
                    if let Some(request) = pending.remove(&id) {
                        let _ = request.response_tx.send(Err(CommandError::timeout(request.method)));
                    }
                }

                if shutdown.as_ref().is_some_and(|request| request.deadline <= now) {
                    let request = shutdown.take().expect("shutdown timeout must exist");
                    terminate_child(&mut child).await;
                    let _ = request.response_tx.send(Ok(()));
                    return SessionEnd::Shutdown;
                }
            }
        }
    }
}

fn create_request(method: &'static str, params: Value) -> Result<(String, Vec<u8>), CommandError> {
    let id = Uuid::new_v4().simple().to_string();
    let request = BridgeRequestEnvelope {
        jsonrpc: JSON_RPC_VERSION.into(),
        id: id.clone(),
        method: method.into(),
        params,
        meta: BridgeRequestMeta {
            api_version: API_VERSION.into(),
            correlation_id: id.clone(),
        },
    };
    let payload = serde_json::to_vec(&request).map_err(|_| CommandError::protocol())?;
    Ok((id, payload))
}

async fn write_request<W>(stdin: &mut W, payload: &[u8]) -> Result<(), CommandError>
where
    W: AsyncWrite + Unpin,
{
    stdin
        .write_all(payload)
        .await
        .map_err(|_| CommandError::unavailable())?;
    stdin
        .write_all(b"\n")
        .await
        .map_err(|_| CommandError::unavailable())?;
    stdin.flush().await.map_err(|_| CommandError::unavailable())
}

fn decode_response(expected_id: &str, line: &str) -> Result<Value, CommandError> {
    let envelope = parse_response(line)?;
    if envelope.id != expected_id {
        return Err(CommandError::protocol());
    }
    response_result(envelope)
}

fn parse_response(line: &str) -> Result<BridgeResponseEnvelope, CommandError> {
    if line.len() > MAX_MESSAGE_BYTES {
        return Err(CommandError::protocol());
    }
    let response: BridgeResponseEnvelope =
        serde_json::from_str(line).map_err(|_| CommandError::protocol())?;
    if response.jsonrpc != JSON_RPC_VERSION || response.id.is_empty() {
        return Err(CommandError::protocol());
    }
    if response.result.is_some() == response.error.is_some() {
        return Err(CommandError::protocol());
    }
    Ok(response)
}

fn response_result(response: BridgeResponseEnvelope) -> Result<Value, CommandError> {
    if let Some(error) = response.error {
        return Err(CommandError {
            code: error.code,
            message: error.message,
            retryable: error.data.retryable,
            correlation_id: Some(error.data.correlation_id),
            field_errors: error.data.field_errors,
        });
    }
    response.result.ok_or_else(CommandError::protocol)
}

fn compatible_api(value: &str) -> bool {
    value.split('.').next() == Some("1")
}

fn can_automatically_restart(previous_restarts: u8, uptime: Duration) -> bool {
    previous_restarts == 0 || uptime >= STABILITY_WINDOW
}

fn request_timeout(method: &str) -> Duration {
    match method {
        "activity.getOverview" => READ_ONLY_QUERY_TIMEOUT,
        "settings.get" => SETTINGS_READ_TIMEOUT,
        "settings.update" => SETTINGS_UPDATE_TIMEOUT,
        "agent.stop" => STOP_TIMEOUT,
        _ => REQUEST_TIMEOUT,
    }
}

fn fail_pending(pending: &mut HashMap<String, PendingRequest>, error: CommandError) {
    for (_, request) in pending.drain() {
        let _ = request.response_tx.send(Err(error.clone()));
    }
}

async fn wait_or_kill(child: &mut Child) {
    if timeout(Duration::from_secs(2), child.wait()).await.is_err() {
        terminate_child(child).await;
    }
}

async fn terminate_child(child: &mut Child) {
    let _ = child.kill().await;
    let _ = child.wait().await;
}

pub fn fixed_bridge_path() -> PathBuf {
    #[cfg(debug_assertions)]
    {
        PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .join("sidecars")
            .join("bridge")
            .join("QuantifiedSelf.Windows.Client.Bridge.exe")
    }

    #[cfg(not(debug_assertions))]
    {
        std::env::current_exe()
            .ok()
            .and_then(|path| path.parent().map(PathBuf::from))
            .unwrap_or_else(|| PathBuf::from("."))
            .join("bridge")
            .join("QuantifiedSelf.Windows.Client.Bridge.exe")
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use tokio::io::{AsyncWriteExt, BufReader, duplex, split};

    #[test]
    fn fixed_path_cannot_be_supplied_by_webview() {
        let path = fixed_bridge_path();
        assert!(path.ends_with("sidecars/bridge/QuantifiedSelf.Windows.Client.Bridge.exe"));
    }

    #[test]
    fn rejects_unknown_or_ambiguous_response() {
        let invalid = r#"{"jsonrpc":"2.0","id":"one","result":{},"error":{"code":"x","message":"x","data":{"kind":"internal","retryable":false,"correlationId":"one"}}}"#;
        assert_eq!(
            parse_response(invalid).unwrap_err().code,
            "bridge_protocol_error"
        );
    }

    #[test]
    fn side_effect_timeout_is_not_retryable() {
        assert!(!CommandError::timeout("agent.start").retryable);
        assert!(!CommandError::timeout("settings.update").retryable);
        assert!(CommandError::timeout("agent.getStatus").retryable);
        assert!(CommandError::timeout("activity.getOverview").retryable);
        assert!(CommandError::timeout("settings.get").retryable);
    }

    #[test]
    fn activity_overview_uses_the_bounded_read_only_timeout() {
        assert_eq!(
            request_timeout("activity.getOverview"),
            READ_ONLY_QUERY_TIMEOUT
        );
        assert_eq!(READ_ONLY_QUERY_TIMEOUT, Duration::from_secs(12));
        assert_eq!(request_timeout("agent.stop"), STOP_TIMEOUT);
    }

    #[test]
    fn settings_commands_use_distinct_read_and_update_timeouts() {
        assert_eq!(request_timeout("settings.get"), SETTINGS_READ_TIMEOUT);
        assert_eq!(SETTINGS_READ_TIMEOUT, Duration::from_secs(12));
        assert_eq!(request_timeout("settings.update"), SETTINGS_UPDATE_TIMEOUT);
        assert_eq!(SETTINGS_UPDATE_TIMEOUT, Duration::from_secs(18));
        assert_ne!(SETTINGS_READ_TIMEOUT, SETTINGS_UPDATE_TIMEOUT);
    }

    #[test]
    fn settings_update_params_are_forwarded_without_rewriting() {
        let params = json!({
            "settings": {
                "appSettings": {
                    "theme": "dark",
                    "refreshIntervalSeconds": 30,
                    "autoStartAgentWhenAppStarts": true
                },
                "agentOptions": {
                    "samplingIntervalSeconds": 3,
                    "idleThresholdSeconds": 300,
                    "heartbeatIntervalSeconds": 5,
                    "staleThresholdSeconds": 30,
                    "retentionDays": 90,
                    "enableJsonlJournal": false,
                    "enableAgentEventJournal": true,
                    "enableSessionMerge": true,
                    "maskWindowTitles": true
                }
            }
        });

        let (_, payload) = create_request("settings.update", params.clone())
            .expect("settings request should serialize");
        let request: BridgeRequestEnvelope =
            serde_json::from_slice(&payload).expect("request should be valid JSON-RPC");

        assert_eq!(request.method, "settings.update");
        assert_eq!(request.params, params);
    }

    #[test]
    fn settings_validation_field_errors_are_preserved_for_the_webview() {
        let response: BridgeResponseEnvelope = serde_json::from_value(json!({
            "jsonrpc": "2.0",
            "id": "settings-1",
            "error": {
                "code": "validation_failed",
                "message": "部分设置值无效。",
                "data": {
                    "kind": "validation",
                    "retryable": false,
                    "correlationId": "settings-1",
                    "fieldErrors": [
                        {
                            "field": "agentOptions.staleThresholdSeconds",
                            "message": "设置值无效。"
                        }
                    ]
                }
            }
        }))
        .expect("Bridge error should match generated contracts");

        let error = response_result(response).expect_err("validation should be an error");

        assert_eq!(error.code, "validation_failed");
        assert!(!error.retryable);
        assert_eq!(
            error
                .field_errors
                .expect("field errors should be preserved")[0]
                .field,
            "agentOptions.staleThresholdSeconds"
        );
    }

    #[test]
    fn only_consecutive_crashes_open_the_circuit() {
        assert!(can_automatically_restart(0, Duration::ZERO));
        assert!(!can_automatically_restart(1, Duration::from_secs(2)));
        assert!(can_automatically_restart(1, STABILITY_WINDOW));
    }

    #[tokio::test]
    async fn fake_bridge_completes_the_typed_hello_exchange() {
        let (shell_stream, bridge_stream) = duplex(4096);
        let (shell_read, mut shell_write) = split(shell_stream);
        let (bridge_read, mut bridge_write) = split(bridge_stream);

        let fake_bridge = tokio::spawn(async move {
            let request_line = BufReader::new(bridge_read)
                .lines()
                .next_line()
                .await
                .expect("fake bridge should read request")
                .expect("fake bridge should receive one request");
            let request: BridgeRequestEnvelope =
                serde_json::from_str(&request_line).expect("request should be valid JSON-RPC");
            assert_eq!(request.method, "bridge.hello");

            let response = json!({
                "jsonrpc": "2.0",
                "id": request.id,
                "result": {
                    "apiVersion": "1.0",
                    "bridgeVersion": "0.1.0",
                    "capabilities": ["client.initialize"]
                }
            });
            bridge_write
                .write_all(format!("{response}\n").as_bytes())
                .await
                .expect("fake bridge should write response");
        });

        let mut response_lines = BufReader::new(shell_read).lines();
        let hello: BridgeHelloResult = exchange(
            &mut shell_write,
            &mut response_lines,
            "bridge.hello",
            Duration::from_secs(1),
        )
        .await
        .expect("supervisor should accept fake bridge response");

        assert_eq!(hello.api_version, "1.0");
        assert_eq!(hello.capabilities, ["client.initialize"]);
        fake_bridge.await.expect("fake bridge task should finish");
    }
}
