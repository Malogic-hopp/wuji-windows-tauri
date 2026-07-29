//! R05 回归：IPC 严格协议校验与 timeout 不取消副作用。

use std::sync::{Arc, Mutex};

use tempfile::TempDir;
use tokio::sync::{mpsc, watch};
use wuji_core::domain::CaptureState;
use wuji_core::dto::RuntimeId;
use wuji_core::settings::Settings;
use wuji_rebuild_agent::capture_coordinator::CaptureCoordinator;
use wuji_rebuild_agent::command_server::{
    CommandServerContext, RequestIdCache, handle_request_line,
};
use wuji_rebuild_agent::pipeline_health::PipelineHealth;
use wuji_rebuild_agent::shared::SharedState;
use wuji_rebuild_agent::writer_task::WriterControl;

struct Harness {
    context: Arc<CommandServerContext>,
    request_ids: Arc<Mutex<RequestIdCache>>,
    control_rx: mpsc::Receiver<WriterControl>,
    settings_path: std::path::PathBuf,
    // watch 接收端必须存活（复审 P1-02：无消费者的发布会失败）。
    _capture_rx: watch::Receiver<CaptureState>,
    _settings_rx: watch::Receiver<Settings>,
    // 三任务健康守卫（第二次复审 P1：注册在 spawn 前同步完成）。
    _guards: (
        wuji_rebuild_agent::pipeline_health::TaskHealthGuard,
        wuji_rebuild_agent::pipeline_health::TaskHealthGuard,
        wuji_rebuild_agent::pipeline_health::TaskHealthGuard,
    ),
    _dir: TempDir,
}

fn harness() -> Harness {
    harness_with_digest(|settings: &Settings| settings.content_digest())
}

fn harness_with_digest(digest: fn(&Settings) -> String) -> Harness {
    let dir = TempDir::new().unwrap();
    let settings_path = dir.path().join("settings.json");
    let shared = Arc::new(SharedState::new("0.1.0".to_string(), RuntimeId::new()));
    let (control_tx, control_rx) = mpsc::channel(8);
    let (capture_state_tx, capture_rx) = watch::channel(CaptureState::Stopped);
    let (settings_tx, settings_rx) = watch::channel(Settings::default());
    let (shutdown_tx, _) = watch::channel(false);
    let (barrier_request_tx, mut barrier_rx) =
        wuji_rebuild_agent::barrier::barrier_request_channel(4);
    // 模拟 Capture Loop：BarrierRequest 立即确认（本测试不涉及 Writer drain）。
    tokio::spawn(async move {
        while let Some(request) = barrier_rx.recv().await {
            let _ = request.injected_ack.send(Ok(()));
        }
    });
    // 第二次复审 P1：harness 同步注册三任务（模拟生产任务已启动）。
    let health = PipelineHealth::new();
    let guards = (
        health.register_capture(),
        health.register_processor(),
        health.register_writer(),
    );
    // 阶段 4.3：CommandServer 只持有唯一 Coordinator 与只读依赖。
    let coordinator = Arc::new(CaptureCoordinator::new(
        barrier_request_tx,
        capture_state_tx,
        control_tx,
        shared.clone(),
        settings_tx,
        CaptureState::Stopped,
        health,
    ));
    let context = Arc::new(CommandServerContext {
        shared,
        coordinator,
        settings_path: settings_path.clone(),
        settings_digest_for: digest,
        shutdown_tx,
        channel: "test-channel".to_string(),
    });
    Harness {
        context,
        request_ids: Arc::new(Mutex::new(RequestIdCache::new("0.1.0".to_string()))),
        control_rx,
        settings_path,
        _capture_rx: capture_rx,
        _settings_rx: settings_rx,
        _guards: guards,
        _dir: dir,
    }
}

fn envelope(request_id: &str, command: &str, payload: serde_json::Value) -> String {
    serde_json::json!({
        "protocolVersion": 1,
        "requestId": request_id,
        "command": command,
        "sentAtUtcMs": "1784332800000",
        "payload": payload,
    })
    .to_string()
}

fn ulid() -> String {
    ulid::Ulid::generate().to_string()
}

fn error_code(response: &str) -> String {
    let value: serde_json::Value = serde_json::from_str(response).unwrap();
    value["error"]["code"]
        .as_str()
        .unwrap_or_default()
        .to_string()
}

#[tokio::test]
async fn invalid_request_id_is_rejected() {
    let harness = harness();
    let response = handle_request_line(
        &envelope("not-a-ulid", "status_get", serde_json::json!({})),
        &harness.context,
        &harness.request_ids,
    )
    .await;
    assert_eq!(error_code(&response), "IPC_INVALID_MESSAGE");
}

#[tokio::test]
async fn non_decimal_sent_at_is_rejected() {
    let harness = harness();
    let line = serde_json::json!({
        "protocolVersion": 1,
        "requestId": ulid(),
        "command": "status_get",
        "sentAtUtcMs": "12a3",
        "payload": {},
    })
    .to_string();
    let response = handle_request_line(&line, &harness.context, &harness.request_ids).await;
    assert_eq!(error_code(&response), "IPC_INVALID_MESSAGE");
}

#[tokio::test]
async fn unknown_payload_fields_are_rejected_per_command() {
    let harness = harness();
    // status_get 不接受任何 payload 字段。
    let response = handle_request_line(
        &envelope(
            &ulid(),
            "status_get",
            serde_json::json!({ "unexpected": true }),
        ),
        &harness.context,
        &harness.request_ids,
    )
    .await;
    assert_eq!(error_code(&response), "IPC_INVALID_MESSAGE");

    // settings_reload 带未知字段同样拒绝（deny_unknown_fields）。
    let response = handle_request_line(
        &envelope(
            &ulid(),
            "settings_reload",
            serde_json::json!({
                "savedRevision": "1",
                "contentDigest": "x",
                "extra": "nope",
            }),
        ),
        &harness.context,
        &harness.request_ids,
    )
    .await;
    assert_eq!(error_code(&response), "IPC_INVALID_MESSAGE");
}

/// timeout 只结束等待：原任务继续执行并在完成后写入 cache；
/// 相同 ID 重试获得真实结果而不是再次执行（R05 核心）。
#[tokio::test]
async fn timeout_does_not_cancel_side_effect_and_retry_returns_real_result() {
    let mut harness = harness();
    let settings = Settings {
        revision: "1".to_string(),
        ..Settings::default()
    };
    std::fs::write(&harness.settings_path, settings.canonical_json()).unwrap();
    let payload = serde_json::json!({
        "savedRevision": "1",
        "contentDigest": settings.content_digest(),
    });
    let request_id = ulid();
    let line = envelope(&request_id, "settings_reload", payload);

    // control lane 先不消费：dispatch 会等待 ack，第一次调用必然 timeout（3 秒真实时间）。
    let first = handle_request_line(&line, &harness.context, &harness.request_ids).await;
    assert_eq!(error_code(&first), "INTERNAL_SAFE_ERROR");
    assert!(first.contains("超时"), "第一次应为 timeout 响应: {first}");

    // timeout 响应不得写入 cache：现在放行原任务（消费 control 消息并 ack）。
    let Some(WriterControl::SettingsApplied { ack, .. }) = harness.control_rx.recv().await else {
        panic!("原任务必须已发出 SettingsApplied 副作用");
    };
    ack.send(Ok(1)).unwrap();

    // 同一 ID 重试：等待原任务结果，返回真实成功而不是再次执行。
    let second = handle_request_line(&line, &harness.context, &harness.request_ids).await;
    let value: serde_json::Value = serde_json::from_str(&second).unwrap();
    assert_eq!(value["ok"], true, "重试必须返回原任务真实结果: {second}");
    assert_eq!(value["result"]["appliedRevision"], "1");
    assert_eq!(
        harness.context.shared.applied_settings_revision(),
        0,
        "测试绕过了 WriterTask，applied 不应被测试本身更新"
    );
}

/// 相同 ID + 不同 payload → IPC_REQUEST_ID_REUSED（幂等冲突，09 §8.2）。
#[tokio::test]
async fn conflicting_payload_with_same_id_is_rejected() {
    let harness = harness();
    let request_id = ulid();
    let first = handle_request_line(
        &envelope(&request_id, "status_get", serde_json::json!({})),
        &harness.context,
        &harness.request_ids,
    )
    .await;
    assert!(serde_json::from_str::<serde_json::Value>(&first).unwrap()["ok"] == true);

    let conflict = handle_request_line(
        &envelope(&request_id, "capture_start", serde_json::json!({})),
        &harness.context,
        &harness.request_ids,
    )
    .await;
    assert_eq!(error_code(&conflict), "IPC_REQUEST_ID_REUSED");
}

/// dispatch panic 后完成守卫写入稳定失败终态；
/// 同 ID 重试返回同一稳定结果且绝不重新执行（digest 调用恰好一次）。
static PANICKING_DIGEST_CALLS: std::sync::atomic::AtomicUsize =
    std::sync::atomic::AtomicUsize::new(0);

fn panicking_digest(_: &Settings) -> String {
    PANICKING_DIGEST_CALLS.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
    panic!("注入的 digest panic（S2-06 完成守卫测试）");
}

#[tokio::test]
async fn dispatch_panic_completes_with_stable_failure_and_no_reexecution() {
    PANICKING_DIGEST_CALLS.store(0, std::sync::atomic::Ordering::SeqCst);
    let harness = harness_with_digest(panicking_digest);
    let settings = Settings {
        revision: "1".to_string(),
        ..Settings::default()
    };
    std::fs::write(&harness.settings_path, settings.canonical_json()).unwrap();
    let payload = serde_json::json!({
        "savedRevision": "1",
        "contentDigest": settings.content_digest(),
    });
    let request_id = ulid();
    let line = envelope(&request_id, "settings_reload", payload);

    let first = handle_request_line(&line, &harness.context, &harness.request_ids).await;
    let first_value: serde_json::Value = serde_json::from_str(&first).unwrap();
    assert_eq!(first_value["ok"], false, "panic 必须返回稳定失败: {first}");
    assert_eq!(
        first_value["error"]["code"], "INTERNAL_SAFE_ERROR",
        "panic 必须归约为稳定错误码: {first}"
    );

    // 同 ID 重试：返回 cache 中的同一稳定结果，绝不重新执行。
    let second = handle_request_line(&line, &harness.context, &harness.request_ids).await;
    assert_eq!(second, first, "同 ID 必须返回同一稳定结果");
    assert_eq!(
        PANICKING_DIGEST_CALLS.load(std::sync::atomic::Ordering::SeqCst),
        1,
        "失败后的同 ID 重试不得重新执行副作用"
    );
}

/// 阶段 4.3.1 §四C+§四D：Writer ack 永不返回（paused）——dispatch 在 operation
/// deadline 后完成（AGENT_WRITER_FAULTED），同 ID 重试返回同一稳定结果。
#[tokio::test(start_paused = true)]
async fn writer_ack_never_returns_completes_with_fault_and_retry_returns_it() {
    let mut harness = harness();
    let settings = Settings {
        revision: "1".to_string(),
        ..Settings::default()
    };
    std::fs::write(&harness.settings_path, settings.canonical_json()).unwrap();
    let payload = serde_json::json!({
        "savedRevision": "1",
        "contentDigest": settings.content_digest(),
    });
    let request_id = ulid();
    let line = envelope(&request_id, "settings_reload", payload);

    // control 永不 ack：首次调用 3s timeout（paused 自动推进），副作用继续。
    let first = handle_request_line(&line, &harness.context, &harness.request_ids).await;
    assert!(first.contains("超时"), "首次应为 timeout 响应: {first}");
    // 同 ID 重试：Wait 分支有界等待，再次 timeout（不永久等待）。
    let second = handle_request_line(&line, &harness.context, &harness.request_ids).await;
    assert!(second.contains("超时"), "重试应为有界 timeout: {second}");

    // paused 推进超过 8s operation deadline：原任务完成（提交结果未知 → fault）。
    tokio::time::advance(std::time::Duration::from_secs(6)).await;
    let third = handle_request_line(&line, &harness.context, &harness.request_ids).await;
    let third_value: serde_json::Value = serde_json::from_str(&third).unwrap();
    assert_eq!(third_value["ok"], false, "终态必须稳定失败: {third}");
    assert_eq!(
        third_value["error"]["code"], "AGENT_WRITER_FAULTED",
        "超时未决必须是 AGENT_WRITER_FAULTED: {third}"
    );
    // 再次重试：返回同一稳定结果，不重新执行（control lane 仍只有一条 control）。
    let fourth = handle_request_line(&line, &harness.context, &harness.request_ids).await;
    assert_eq!(fourth, third, "同 ID 必须返回同一稳定结果");
    assert!(harness.control_rx.recv().await.is_some());
    assert!(
        harness.control_rx.try_recv().is_err(),
        "不得产生第二次 control 副作用"
    );
}

/// stale Active 只发布客户端可见的稳定“结果未知”，后台 execution record 继续占位；
/// 迟到的真实结果按同 generation 完成该 Active。
#[tokio::test(start_paused = true)]
async fn stale_active_stays_non_evictable_until_late_result_completes_it() {
    let mut harness = harness();
    let settings = Settings {
        revision: "1".to_string(),
        ..Settings::default()
    };
    std::fs::write(&harness.settings_path, settings.canonical_json()).unwrap();
    let payload = serde_json::json!({
        "savedRevision": "1",
        "contentDigest": settings.content_digest(),
    });
    let request_id = ulid();
    let line = envelope(&request_id, "settings_reload", payload);

    let first = handle_request_line(&line, &harness.context, &harness.request_ids).await;
    assert!(first.contains("超时"), "首次应为 timeout: {first}");

    // 注入未来时刻：stale Active 发布结果未知，但执行记录保持 Active（确定性，不等真实 20s）。
    harness
        .request_ids
        .lock()
        .unwrap()
        .purge_expired_at(std::time::Instant::now() + std::time::Duration::from_secs(25));
    let unknown = handle_request_line(&line, &harness.context, &harness.request_ids).await;
    let unknown_value: serde_json::Value = serde_json::from_str(&unknown).unwrap();
    assert_eq!(unknown_value["ok"], false, "结果未知响应必须稳定失败");
    assert_eq!(
        unknown_value["error"]["code"], "INTERNAL_SAFE_ERROR",
        "结果未知必须是稳定内部安全错误: {unknown}"
    );
    assert!(
        unknown.contains("结果未知"),
        "响应必须说明结果未知: {unknown}"
    );
    assert_eq!(
        harness.request_ids.lock().unwrap().active_count(),
        1,
        "客户端可见 unknown 不得释放真实执行配额"
    );
    // 结果未知后不重新执行：control lane 仍只有第一次的一条。
    // 持有该 control（含 ack sender），保证 coordinator 走完 deadline 而不是 ack 断开。
    let _held_control = harness.control_rx.recv().await;
    assert!(harness.control_rx.try_recv().is_err());

    // 即使跨过 Completed TTL，后台仍未结束的 Active execution 也不得被清理或释放配额；
    // 同 ID 仍返回同一“结果未知”，绝不能作为 Fresh 再执行。
    harness
        .request_ids
        .lock()
        .unwrap()
        .purge_expired_at(std::time::Instant::now() + std::time::Duration::from_secs(700));
    let after_ttl = handle_request_line(&line, &harness.context, &harness.request_ids).await;
    assert_eq!(after_ttl, unknown, "Active 未决结果跨 TTL 后必须保持稳定");
    assert!(
        harness.control_rx.try_recv().is_err(),
        "跨 TTL 的同 ID 不得重新执行 control 副作用"
    );

    // 迟到的真实结果（coordinator deadline 触发 fault）按同 generation 结束 Active、
    // 释放执行配额，但客户端可见终态必须保持此前发布的“结果未知”。
    tokio::time::advance(std::time::Duration::from_secs(9)).await;
    // 驱动 dispatch 完成其超时后的写入路径（paused 时钟下 advance 后需 poll）。
    for _ in 0..10 {
        tokio::task::yield_now().await;
    }
    let late = handle_request_line(&line, &harness.context, &harness.request_ids).await;
    assert_eq!(late, unknown, "迟到结果不得让同一 request ID 翻转响应");
    assert_eq!(
        harness.request_ids.lock().unwrap().active_count(),
        0,
        "真实 dispatch 结束后才释放 Active 配额"
    );
}

/// 阶段 4.3.1 §四D：InProgress 达总量上限后拒绝全新 ID（稳定错误，不接受不执行）；
/// 已接受条目不受拒绝影响（同 ID 重试仍返回原任务结果语义）。
#[tokio::test(start_paused = true)]
async fn in_progress_capacity_rejects_new_ids_without_reexecution() {
    let mut harness = harness();
    let settings = Settings {
        revision: "1".to_string(),
        ..Settings::default()
    };
    std::fs::write(&harness.settings_path, settings.canonical_json()).unwrap();
    let payload = serde_json::json!({
        "savedRevision": "1",
        "contentDigest": settings.content_digest(),
    });

    // 并发发起 257 个不同 ID 的 settings_reload（control 永不 ack → 全部 InProgress）。
    let mut tasks = Vec::new();
    for _ in 0..257 {
        let context = harness.context.clone();
        let request_ids = harness.request_ids.clone();
        let line = envelope(&ulid(), "settings_reload", payload.clone());
        tasks.push(tokio::spawn(async move {
            handle_request_line(&line, &context, &request_ids).await
        }));
    }
    let mut capacity_rejections = 0;
    let mut timeout_responses = 0;
    for task in tasks {
        let response = task.await.expect("任务不 panic");
        if response.contains("并发请求过多") {
            capacity_rejections += 1;
        } else if response.contains("超时") {
            timeout_responses += 1;
        } else {
            panic!("非预期响应: {response}");
        }
    }
    assert_eq!(capacity_rejections, 1, "第 257 个全新 ID 必须被容量拒绝");
    assert_eq!(
        timeout_responses, 256,
        "已接受条目必须是 timeout（InProgress）"
    );

    // 分批容量证据：把第一批 Active 标记为客户端可见“结果未知”并跨过 Completed TTL，
    // 仍不得释放 256 个后台 active execution slots；第二批全新 ID 继续被拒绝。
    harness
        .request_ids
        .lock()
        .unwrap()
        .purge_expired_at(std::time::Instant::now() + std::time::Duration::from_secs(700));
    for _ in 0..16 {
        let second_batch = handle_request_line(
            &envelope(&ulid(), "status_get", serde_json::json!({})),
            &harness.context,
            &harness.request_ids,
        )
        .await;
        assert!(
            second_batch.contains("并发请求过多"),
            "stale Active 不得释放并发配额: {second_batch}"
        );
    }
    // 被拒绝的 ID 未被接受：对它重试不会产生重复执行（仍然被拒绝或全新执行）。
    // 已接受条目不因拒绝而重复执行：control lane 中没有第二条 control。
    assert!(harness.control_rx.recv().await.is_some());
    assert!(
        harness.control_rx.try_recv().is_err(),
        "拒绝路径不得产生任何重复副作用"
    );
}
