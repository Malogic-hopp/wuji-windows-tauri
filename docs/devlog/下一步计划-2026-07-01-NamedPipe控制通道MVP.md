# 下一步计划：Named Pipe 控制通道 MVP（2026-07-01）

本文档作为 `下一步计划-2026-06-24-PruneDataClearHistory数据清理MVP.md` 阶段 7「PruneData / ClearHistory 数据清理 MVP」完成后的下一阶段正式计划。

上一阶段已经完成：

```text
PruneData / ClearHistory 数据清理 MVP
```

当前项目已经从：

```text
能采集、能诊断、能浏览、能配置、能清理
```

推进到：

```text
具备本地产品 MVP 的核心闭环
```

下一步不建议马上进入托盘、开机自启、安装包、图表、趋势、导出或应用分类。  
更优先的是把当前文件控制通道升级为稳定的请求-响应式控制通道：

```text
Named Pipe 控制通道 MVP
```

一句话目标：

```text
把 agent_control.json 从主控制通道降级为 fallback，让 WPF 能通过 Named Pipe 向 Agent 发送命令，并得到明确的 accepted / completed / failed 响应。
```

---

## 当前状态

当前 WUJI 已具备：

```text
真实 Win32 前台窗口采样
idle / active 判断
采集阶段隐私过滤
foreground_samples 落库
app_sessions 合并
Pause / Resume / Stop 控制
Dashboard 今日统计
Diagnostics 最近事件 / 最近错误
agent_events SQLite 查询索引
agent_events_YYYYMMDD.jsonl 审计日志
SamplesView 最近样本浏览
SessionsView 会话浏览和 close_reason 筛选
AppsView 今日应用排行
SettingsView App / Agent 配置展示、编辑、校验、保存、ReloadConfig
PruneData / ClearHistory 数据清理
Maintenance 状态
DataPruned / HistoryCleared 诊断事件
```

阶段 7 已完成验收：

```text
dotnet build QuantifiedSelf.Windows.sln --no-restore
    通过，0 warnings / 0 errors

dotnet test QuantifiedSelf.Windows.sln --no-restore
    通过，142/142

手动验收与长跑验证通过
```

当前控制链路仍以文件为主：

```text
WPF 写 runtime/agent_control.json
Agent 定时 tick 读取 command
Agent 写 agent_events / runtime_state / health_state
WPF 再通过轮询状态和 Diagnostics 推断命令结果
```

这条链路已经可用，但在命令越来越多之后，开始出现明显局限：

```text
1. UI 不能立即知道 Agent 是否收到命令。
2. 命令 accepted / completed / failed 需要绕到 Diagnostics 或状态文件确认。
3. ClearHistory / PruneData 这种高风险命令更适合请求-响应语义。
4. Maintenance 中拒绝、命令超时、重复点击等语义需要更直接的返回值。
5. 后续托盘、开机自启、状态流订阅都依赖更稳定的控制通道。
```

---

## Review 吸收结论

阶段 7 的下一阶段建议和完整重构方案都指向：

```text
V0:
    agent_control.json 跑通闭环

V1:
    Named Pipe / gRPC over Named Pipes 作为主控制通道
    agent_control.json 仅保留为 fallback 和 desired state 持久化来源

V2:
    Agent 支持双向状态流
    WPF 订阅 Agent 状态变化，减少轮询
```

本阶段吸收这些结论，但把范围收紧为：

```text
先做 Named Pipe + JSON 请求响应协议
暂不引入 gRPC
暂不做状态流订阅
暂不删除 agent_control.json fallback
```

原因：

```text
1. 当前项目已有 AgentControlCommand / AgentCommandResult，可直接复用为 JSON 协议。
2. Named Pipe 是 Windows 本地 IPC 的自然选择，部署成本低。
3. gRPC over Named Pipes 会引入额外协议栈和依赖，本阶段收益不如先打通 IPC 主链路。
4. 保留 file fallback 可以降低迁移风险，便于故障恢复和手动排查。
```

本计划吸收 review 后进一步明确：

```text
1. 阶段 8.1 第一批提交只做协议模型、pipe name 和 Stream 级协议读写，不创建真实 server/client。
2. NamedPipeProtocol 必须限制最大 payload，默认 MaxPayloadBytes = 16 KB。
3. timeout 区分 ConnectTimeout 和 RequestTimeout。
4. Stop 命令必须避免 Agent 退出过快导致 UI 误报失败。
5. requestId 防重先做轻量内存缓存，不做磁盘级命令队列。
6. Diagnostics IPC 状态第一版先存在 App service 内存，不新增 runtime 持久化文件。
7. pipe name 区分 FullPipeName 和 DisplayPipeName，UI 只显示安全短名。
8. AgentCommandServer 异常不能拖垮采样循环。
9. ClearHistory 的二次确认仍由 WPF 保障，confirmation token 作为后续增强记录。
```

---

## 下一阶段目标

下一阶段目标命名为：

```text
Named Pipe 控制通道 MVP
```

一句话目标：

```text
WPF 优先通过 Named Pipe 控制 Agent；Named Pipe 不可用时自动降级到 agent_control.json fallback；Diagnostics 能看到 IPC 状态和 fallback 使用情况。
```

这一阶段重点补齐：

```text
IPC 协议
    Ping / GetStatus / command request / command response

AgentCommandServer
    Agent 内部启动 Named Pipe server
    收到请求后调用 AgentStateMachine

AgentControlClient
    WPF 通过 Named Pipe 发送命令
支持 timeout / cancellation / fallback

命令迁移
    先 GetStatus / Ping
    再 Pause / Resume / Stop / ReloadConfig
    最后 PruneData / ClearHistory

Diagnostics
    显示 IPC enabled / pipe name / last success / last error / fallback used
```

阶段完成后应满足：

```text
1. Agent 启动后创建当前用户专属 Named Pipe。
2. WPF 能通过 Named Pipe Ping Agent。
3. WPF 能通过 Named Pipe 获取 Agent status。
4. Pause / Resume / Stop / ReloadConfig 能通过 IPC 执行并返回 AgentCommandResult。
5. PruneData / ClearHistory 能通过 IPC 执行并返回 AgentCommandResult。
6. IPC 不可用、超时或连接失败时，WPF 自动 fallback 到 agent_control.json。
7. fallback 行为不破坏现有文件控制链路。
8. CommandSource 能区分 NamedPipe / FileFallback。
9. Diagnostics 能显示 IPC 状态和最近 IPC 错误。
10. IPC 超时不会卡 UI。
11. Agent 退出后 WPF 能恢复到 NotRunning / fallback 状态。
12. Named Pipe 只允许当前 Windows 用户访问。
13. Named Pipe server 异常不会停止采样循环。
14. 超大 IPC payload 会被拒绝，不会造成大内存分配。
```

---

## 为什么现在做这个

阶段 4 到阶段 7 已经补齐：

```text
AgentEvents / Diagnostics
Samples / Sessions / Apps 数据浏览
Settings 与配置应用
PruneData / ClearHistory 数据清理
```

现在 WPF 能触发的命令已经包括：

```text
Start
Pause
Resume
Stop
ReloadConfig
PruneData
ClearHistory
```

其中 `ReloadConfig`、`PruneData`、`ClearHistory` 都不再只是简单状态切换，而是需要：

```text
明确接收
明确成功
明确失败
明确错误码
明确是否进入 fallback
```

继续只依赖 `agent_control.json` 会让 UI 状态反馈越来越绕。  
完成本阶段后，WUJI 的控制模型将从：

```text
文件投递 + 轮询推断
```

升级为：

```text
IPC 请求响应 + 文件 fallback
```

这为后续阶段继续做托盘、开机自启、状态流订阅和更复杂的 UI 状态反馈打基础。

---

## 本阶段不做

本阶段暂不做：

```text
gRPC over Named Pipes
Agent 状态流订阅
托盘图标
开机自启
安装包
Windows Service
多用户控制
远程控制
复杂权限管理 UI
维护任务取消
PruneData / ClearHistory 进度条
命令队列持久化
```

本阶段也不删除：

```text
agent_control.json
runtime_state.json
health_state.json
```

它们仍然作为：

```text
fallback 控制通道
状态快照
故障排查入口
```

---

## 架构原则

### 1. WPF 不直接操作 Agent 内部状态

WPF 只能通过：

```text
Named Pipe IPC
agent_control.json fallback
```

向 Agent 发送命令。

WPF 不直接：

```text
修改 runtime_state
修改 health_state
调用 AgentStateMachine
删除 SQLite / JSONL
```

### 2. AgentStateMachine 仍是命令语义中心

Named Pipe server 不重新实现命令逻辑。

正确链路应为：

```text
Named Pipe request
    ↓
AgentCommandServer
    ↓
AgentStateMachine.ProcessCommandAsync
    ↓
AgentCommandResult
    ↓
Named Pipe response
```

文件 fallback 链路仍然为：

```text
agent_control.json
    ↓
AgentControlFileStore
    ↓
AgentStateMachine.ProcessCommandAsync
```

两条路径最终都进入同一个状态机。

### 3. IPC 是主通道，文件是 fallback

WPF 发送命令时：

```text
先尝试 Named Pipe
    成功：返回 IPC result
    失败 / 超时：写 agent_control.json fallback
```

fallback 不应该悄悄吞掉错误，应在 UI / Diagnostics 中留下证据：

```text
fallbackUsed = true
lastIpcError = <safe message>
commandSource = FileFallback
```

### 4. 协议要小而稳定

第一版协议只承载：

```text
requestId
command
desiredState
requestedBy
requestedAtUtc
waitForCompletion
timeoutMilliseconds
```

响应只承载：

```text
requestId
accepted
completed
actualState
message
errorCode
startedAtUtc
completedAtUtc
```

不要把：

```text
窗口标题
完整路径
SQL
异常原文
原始 JSON
```

放入 IPC response 或 Diagnostics payload。

### 5. 当前用户安全边界

Named Pipe 必须限制当前用户：

```text
Pipe 名称包含当前用户 SID hash
Agent 创建 pipe 时限制 ACL
WPF 只连接当前用户 pipe
不允许跨用户控制 Agent
```

MVP 至少要做到：

```text
pipe name 不使用固定全局裸名称
pipe name 包含当前用户稳定标识 hash
server 端尽量设置 PipeSecurity / ACL
测试覆盖 pipe name 生成规则
```

如果当前 .NET / Windows API 在测试环境中不方便稳定验证 ACL，至少要在实现和文档中明确安全边界，并补静态测试。

---

## 建议新增或调整

建议新增 Core 层协议模型：

```text
src/QuantifiedSelf.Windows.Core/Ipc/AgentIpcRequest.cs
src/QuantifiedSelf.Windows.Core/Ipc/AgentIpcResponse.cs
src/QuantifiedSelf.Windows.Core/Ipc/AgentIpcCommand.cs
src/QuantifiedSelf.Windows.Core/Ipc/AgentIpcStatus.cs
src/QuantifiedSelf.Windows.Core/Ipc/AgentPipeName.cs
src/QuantifiedSelf.Windows.Core/Ipc/IProcessedRequestCache.cs
```

建议新增 Infrastructure 层 Named Pipe 实现：

```text
src/QuantifiedSelf.Windows.Infrastructure/Ipc/IAgentIpcClient.cs
src/QuantifiedSelf.Windows.Infrastructure/Ipc/IAgentIpcServer.cs
src/QuantifiedSelf.Windows.Infrastructure/Ipc/NamedPipeAgentControlClient.cs
src/QuantifiedSelf.Windows.Infrastructure/Ipc/NamedPipeAgentCommandServer.cs
src/QuantifiedSelf.Windows.Infrastructure/Ipc/NamedPipeProtocol.cs
src/QuantifiedSelf.Windows.Infrastructure/Ipc/ProcessedRequestCache.cs
```

建议新增 Agent 层托管服务：

```text
src/QuantifiedSelf.Windows.Agent/Services/AgentCommandServerHostedService.cs
```

建议调整 App 层服务：

```text
src/QuantifiedSelf.Windows.App/Services/AgentControlService.cs
    优先 IPC，失败 fallback 到 agent_control.json

src/QuantifiedSelf.Windows.App/Services/AgentStatusService.cs
    可优先 IPC GetStatus，失败继续读 runtime_state / health_state

src/QuantifiedSelf.Windows.App/Services/DiagnosticsDataService.cs
    增加 IPC 状态读取或展示数据来源
```

建议调整 Diagnostics / Settings 展示：

```text
Diagnostics:
    IPC enabled
    Display pipe name
    Last IPC success
    Last IPC error
    Last fallback used

Settings / Data Management:
    不改变用户操作入口
    状态文案可显示 queued via IPC / queued via fallback
```

---

## 协议建议

MVP 可以使用一行一个 JSON 或长度前缀 JSON。建议优先：

```text
length-prefixed UTF-8 JSON
```

原因：

```text
1. message 中未来可能包含换行。
2. 比 newline-delimited JSON 更稳。
3. 实现仍然足够简单。
```

协议必须限制 payload 大小：

```text
MaxPayloadBytes = 16 KB
```

原因：

```text
MVP 的命令请求和响应都很小，16 KB 足够。
length prefix 如果不设上限，异常长度可能导致过大内存分配。
```

超过上限时：

```text
accepted = false
completed = false
errorCode = IpcPayloadTooLarge
message = IPC payload too large
```

实现建议：

```text
NamedPipeProtocol 基于 Stream 实现读写
阶段 8.1 用 MemoryStream / fake stream 测试协议
阶段 8.2 / 8.3 再做真实 Named Pipe 集成测试
```

请求示例：

```json
{
  "protocolVersion": 1,
  "requestId": "ipc-...",
  "command": "Pause",
  "desiredState": "Paused",
  "requestedBy": "QuantifiedSelf.Windows.App",
  "requestedAtUtc": "2026-07-01T10:00:00.0000000Z",
  "waitForCompletion": true,
  "timeoutMilliseconds": 5000
}
```

响应示例：

```json
{
  "protocolVersion": 1,
  "requestId": "ipc-...",
  "accepted": true,
  "completed": true,
  "actualState": "Paused",
  "message": "Pause completed",
  "errorCode": null,
  "startedAtUtc": "2026-07-01T10:00:00.1000000Z",
  "completedAtUtc": "2026-07-01T10:00:00.5000000Z"
}
```

协议版本第一版固定：

```text
protocolVersion = 1
```

收到未知版本：

```text
accepted = false
completed = false
errorCode = UnsupportedProtocolVersion
```

超时字段分两层：

```text
ConnectTimeoutMilliseconds = 1000
RequestTimeoutMilliseconds = command.timeoutMilliseconds 或默认 5000
MaintenanceCommandTimeoutMilliseconds = 30000
```

语义：

```text
ConnectTimeout:
    连接 pipe 超时，通常可以 fallback

RequestTimeout:
    request 已发送但 response 超时，需要谨慎 fallback，避免重复命令

MaintenanceCommandTimeout:
    PruneData / ClearHistory 这类维护命令允许更长等待
```

---

## 命令语义

### Ping

用途：

```text
确认 pipe 可连接
确认 AgentCommandServer 在线
```

响应：

```text
accepted = true
completed = true
message = Pong
actualState = 当前 AgentActualState
```

### GetStatus

用途：

```text
取代一部分 runtime_state / health_state 轮询
```

响应至少包含：

```text
actualState
desiredState
processId
startedAtUtc
lastHeartbeatUtc
lastSampleUtc
currentSessionId
version
isHealthy
```

MVP 可以继续保留 App 侧现有 `AgentStatusSnapshot` 聚合逻辑，只把 IPC status 作为优先来源。

### Pause / Resume / Stop

语义沿用 AgentStateMachine：

```text
Pause:
    Running -> Paused

Resume:
    Paused -> Running

Stop:
    Running / Paused -> Stopped
```

IPC response 应直接反映：

```text
accepted
completed
actualState
errorCode
```

Stop 要特殊处理：

```text
Agent 收到 Stop 后可能很快退出，pipe response 可能来不及写完。
MVP 优先策略：
    AgentStateMachine 先完成 Stop 状态落盘和 response 写回
    再由 Worker / Host 执行退出

兜底策略：
    如果 client 看到 pipe broken，但随后进程消失或 runtime_state = Stopped
    UI 可视为 Stop 成功，而不是直接显示 IPC 失败
```

阶段 8.4 必须单独验收：

```text
Stop over IPC does not show false failure when Agent exits quickly.
```

### ReloadConfig

语义沿用阶段 6：

```text
Agent Running / Paused:
    可执行

Agent NotRunning:
    WPF fallback 文案仍可提示下次启动生效

Agent Maintenance:
    拒绝或暂不可用
```

失败时：

```text
errorCode = ConfigReloadFailed / ConfigValidationFailed / ConfigReadFailed 等
message = safe message
```

### PruneData / ClearHistory

语义沿用阶段 7：

```text
PruneData:
    成功后恢复清理前状态

ClearHistory:
    成功后 Paused
```

维护命令的 IPC 等待策略：

```text
Pause / Resume / ReloadConfig:
    默认 waitForCompletion = true
    RequestTimeoutMilliseconds = 5000

PruneData / ClearHistory:
    MVP 可以等待完成，但必须使用 MaintenanceCommandTimeoutMilliseconds = 30000
    如果后续清理耗时变长，可改为 accepted + actualState = Maintenance，再由 Diagnostics / status 观察完成
```

IPC 不改变二次确认：

```text
ClearHistory 的 CLEAR 确认仍在 WPF UI 层完成
Agent 不接受 UI 绕过确认这件事由客户端入口保障
```

风险记录：

```text
Agent 端目前无法证明 ClearHistory 是否真的经过 WPF 二次确认。
MVP 可以接受，因为 pipe 限制当前用户，且 WPF 是唯一正式客户端。
后续增强可让 ClearHistory request 带 confirmationText = CLEAR 或 reason = User confirmed CLEAR，
Agent 端缺失确认字段时拒绝执行。
```

Agent 侧仍需要守卫：

```text
Maintenance 中拒绝重复 PruneData / ClearHistory
```

---

## Diagnostics 要求

阶段 8 完成后，Diagnostics 至少能看见：

```text
IPC status: Enabled / Unavailable / Fallback
Display pipe name: QuantifiedSelf.Windows.Agent.<hash-prefix>
Last IPC success time
Last IPC error
Last fallback used time
Recent command source
```

agent_events payload 建议补充：

```text
commandSource = NamedPipe / FileFallback
fallbackUsed = true / false
ipcErrorCode = <safe code>
```

注意：

```text
不要记录完整 pipe 路径
不要记录完整 Windows 用户 SID
不要记录异常原文
不要记录本机路径
```

---

## 失败处理

必须覆盖：

```text
Agent 未运行
Pipe 不存在
Pipe 连接超时
Pipe request 超时
Pipe response 不是合法 JSON
协议版本不支持
命令执行失败
Maintenance 中拒绝重复命令
WPF 取消请求
Agent 处理中退出
```

建议错误码：

```text
IpcUnavailable
IpcConnectTimeout
IpcRequestTimeout
IpcProtocolError
IpcPayloadTooLarge
UnsupportedProtocolVersion
IpcServerError
FallbackUsed
DuplicateRequest
```

WPF 侧行为：

```text
IPC 失败但 fallback 写入成功:
    Accepted = true
    Message = Command queued via file fallback
    FallbackUsed = true

IPC 失败且 fallback 写入失败:
    Accepted = false
    ErrorCode = IpcAndFallbackFailed
    Message = safe message
```

fallback 前必须区分失败类型：

```text
连接失败 / pipe 不存在:
    可以 fallback

request 尚未写入成功:
    可以 fallback

request 已写入成功但 response 超时:
    谨慎 fallback，必须依赖 requestId 防重，避免重复执行 ClearHistory / PruneData
```

## requestId 防重

阶段 8 MVP 不做磁盘级命令队列，但需要轻量内存防重：

```text
ProcessedRequestCache
capacity = 100
ttl = 10 minutes
```

AgentCommandServer / AgentStateMachine 处理命令前检查 requestId：

```text
命中重复 requestId:
    accepted = true
    completed = true
    errorCode = DuplicateRequest
    message = Duplicate request ignored
    不重复执行命令副作用
```

用途：

```text
防止 IPC 已执行成功但 response 超时后，WPF fallback 写入 agent_control.json 造成二次执行。
```

---

## 安全要求

Named Pipe 安全边界：

```text
1. pipe name 包含当前用户 SID hash。
2. 不使用用户明文 SID 作为 UI 可见完整标识。
3. server 尽量设置当前用户 ACL。
4. client 只连接当前用户 pipe name。
5. 所有 request 都带 requestedBy。
6. Agent 仍用单实例 mutex 限制当前用户下多实例。
```

pipe name 建议拆成两个属性：

```text
FullPipeName:
    QuantifiedSelf.Windows.Agent.<sidHash>
    仅内部连接使用

DisplayPipeName:
    QuantifiedSelf.Windows.Agent.<hash-prefix>
    只显示前 8-12 位 hash，用于 Diagnostics
```

测试建议：

```text
AgentPipeName_GeneratesStableNameForUserSid
AgentPipeName_DoesNotExposeRawSid
AgentPipeName_UsesDifferentNamesForDifferentUsers
```

如果 ACL 自动化测试成本高，可以先做：

```text
NamedPipeServerOptions / PipeSecurity 构造逻辑单测
手动验收记录当前用户可连接
```

---

## 阶段拆分

建议拆成：

```text
阶段 8.1：IPC 协议与 Named Pipe 基础设施
阶段 8.2：AgentCommandServer + Ping / GetStatus
阶段 8.3：WPF AgentControlClient + fallback 策略
阶段 8.4：Pause / Resume / Stop / ReloadConfig 迁移到 IPC
阶段 8.5：PruneData / ClearHistory 迁移到 IPC
阶段 8.6：Diagnostics IPC 状态展示
阶段 8.7：验收、断连测试与收口
```

这个顺序先建立协议和只读状态，再迁移低风险命令，最后迁移高风险维护命令。

第一批提交必须很小：

```text
只做 Core/Ipc 协议模型
只做 AgentPipeName
只做 NamedPipeProtocol length-prefixed JSON
只做 Stream / MemoryStream 协议测试
不创建真实 server/client
不接 Agent / WPF
```

建议提交信息：

```text
feat(ipc): add named pipe protocol contracts
```

---

# 阶段 8.1：IPC 协议与 Named Pipe 基础设施

## 阶段目标

定义 IPC 协议模型、pipe 命名规则和基础读写协议，但暂不接 Agent / WPF 业务命令。

## 建议新增

```text
src/QuantifiedSelf.Windows.Core/Ipc/AgentIpcRequest.cs
src/QuantifiedSelf.Windows.Core/Ipc/AgentIpcResponse.cs
src/QuantifiedSelf.Windows.Core/Ipc/AgentIpcCommand.cs
src/QuantifiedSelf.Windows.Core/Ipc/AgentIpcStatus.cs
src/QuantifiedSelf.Windows.Core/Ipc/AgentPipeName.cs
src/QuantifiedSelf.Windows.Infrastructure/Ipc/NamedPipeProtocol.cs
```

## 验收标准

- IPC request / response 可以 JSON 序列化和反序列化。
- protocolVersion 固定为 1。
- pipe name 对同一 user SID 稳定。
- pipe name 不暴露原始 user SID。
- pipe name 对不同 user SID 不同。
- pipe name 能区分 FullPipeName / DisplayPipeName。
- NamedPipeProtocol 能读写 length-prefixed JSON。
- NamedPipeProtocol 拒绝超过 16 KB 的 payload。
- 非法 JSON / 半截消息安全失败，不泄露原始内容。
- 本阶段只用 Stream / MemoryStream 测试协议，不创建真实 server/client。

## 建议测试

```text
AgentIpcRequest_RoundTripsJson
AgentIpcResponse_RoundTripsJson
AgentPipeName_GeneratesStableNameForUserSid
AgentPipeName_DoesNotExposeRawSid
AgentPipeName_UsesDifferentNamesForDifferentUsers
AgentPipeName_ExposesSafeDisplayName
NamedPipeProtocol_RoundTripsLengthPrefixedJson
NamedPipeProtocol_RejectsInvalidPayloadSafely
NamedPipeProtocol_RejectsPayloadOverMaxSize
NamedPipeProtocol_RejectsTruncatedPayloadSafely
```

## 不做什么

- 不启动 Named Pipe server。
- 不创建真实 NamedPipe client。
- 不改 WPF 控制命令。
- 不改 AgentStateMachine。
- 不做 fallback。

---

# 阶段 8.2：AgentCommandServer + Ping / GetStatus

## 阶段目标

在 Agent 中启动 Named Pipe server，先支持低风险的 `Ping` 和 `GetStatus`。

## 建议新增

```text
src/QuantifiedSelf.Windows.Agent/Services/AgentCommandServerHostedService.cs
src/QuantifiedSelf.Windows.Infrastructure/Ipc/NamedPipeAgentCommandServer.cs
```

## 行为要求

```text
Agent 启动:
    创建当前用户 pipe
    监听 request

Ping:
    返回 Pong

GetStatus:
    返回 Agent 当前 runtime snapshot / health summary
```

## 验收标准

- Agent 启动时 Named Pipe server 不阻塞采样 tick。
- Agent 停止时 server 能取消监听并释放 pipe。
- Ping 能返回 completed。
- GetStatus 能返回 actualState / heartbeat / processId。
- 非法 request 返回安全错误，不崩 Agent。
- Pipe server 异常写入 health / diagnostics 安全文案。
- Pipe server 监听循环异常后能短暂 delay 并重启监听。
- Pipe server 失败不会停止采样循环。

## 建议测试

```text
NamedPipeAgentCommandServer_RespondsToPing
NamedPipeAgentCommandServer_ReturnsStatus
NamedPipeAgentCommandServer_RejectsUnsupportedProtocolVersion
NamedPipeAgentCommandServer_HandlesInvalidJsonWithoutCrashing
AgentCommandServerHostedService_StartsAndStopsCleanly
NamedPipeAgentCommandServer_FailureDoesNotStopSampling
AgentCommandServerHostedService_RestartsListenLoopAfterFailure
```

## 不做什么

- 不迁移 Pause / Resume / Stop。
- 不迁移 ReloadConfig。
- 不迁移 PruneData / ClearHistory。
- 不改 Settings UI。

---

# 阶段 8.3：WPF AgentControlClient + fallback 策略

## 阶段目标

在 WPF 侧新增 Named Pipe client，并让 App 服务具备：

```text
优先 IPC
失败 fallback 到 agent_control.json
```

但本阶段先只接 `Ping` / `GetStatus` 或内部 smoke check，不迁移高风险命令。

## 建议新增

```text
src/QuantifiedSelf.Windows.Infrastructure/Ipc/NamedPipeAgentControlClient.cs
src/QuantifiedSelf.Windows.App/Services/AgentIpcStatusService.cs
```

`AgentIpcStatusService` 第一版只保存本次 App 会话内存状态：

```text
lastIpcSuccessUtc
lastIpcError
lastFallbackUsedUtc
lastCommandSource
fullPipeName
displayPipeName
```

本阶段不新增 IPC runtime 持久化文件。

## 建议调整

```text
src/QuantifiedSelf.Windows.App/Services/AgentStatusService.cs
    优先 IPC GetStatus
    IPC 不可用时沿用 runtime_state / health_state

src/QuantifiedSelf.Windows.App/Services/AgentControlService.cs
    增加 IPC client 依赖
    暂不迁移所有命令
```

## fallback 规则

```text
IPC 成功:
    使用 IPC response

IPC 不可用:
    使用既有文件状态 / 文件命令
    记录 fallbackUsed

IPC 超时:
    不阻塞 UI
    返回 safe message
```

timeout 规则：

```text
ConnectTimeoutMilliseconds = 1000
RequestTimeoutMilliseconds = 5000
MaintenanceCommandTimeoutMilliseconds = 30000
```

## 验收标准

- App 在 Agent 运行时能通过 IPC 获取状态。
- Agent 未运行时 App 不崩溃。
- IPC 不可用时仍能通过 runtime_state / health_state 显示状态。
- UI 状态文案能显示 IPC / fallback 状态。
- IPC 超时不会卡住 Refresh。
- App 内存中能记录 last IPC success / error / fallback used。

## 建议测试

```text
NamedPipeAgentControlClient_PingReturnsPong
NamedPipeAgentControlClient_TimesOutSafely
AgentStatusService_UsesIpcStatusWhenAvailable
AgentStatusService_FallsBackToRuntimeStateWhenIpcUnavailable
AgentControlService_DoesNotBlockUiWhenIpcUnavailable
AgentIpcStatusService_RecordsLastSuccessAndFallback
NamedPipeAgentControlClient_UsesSeparateConnectAndRequestTimeouts
```

## 不做什么

- 不迁移数据清理命令。
- 不删除文件 fallback。
- 不做 Diagnostics 完整 UI。

---

# 阶段 8.4：Pause / Resume / Stop / ReloadConfig 迁移到 IPC

## 阶段目标

将低风险控制命令迁移为 IPC 优先：

```text
Pause
Resume
Stop
ReloadConfig
```

## 行为要求

```text
WPF 点击命令
    ↓
尝试 IPC
    ↓
IPC 成功:
    使用 AgentCommandResult
IPC 失败:
    写 agent_control.json fallback
```

Agent server 收到 command 后：

```text
构造 AgentControlCommand
调用 AgentStateMachine.ProcessCommandAsync
返回 AgentCommandResult
```

## 验收标准

- Pause 通过 IPC 成功进入 Paused。
- Resume 通过 IPC 成功进入 Running。
- Stop 通过 IPC 成功进入 Stopped。
- Stop 通过 IPC 时不会因 Agent 快速退出而误报失败。
- ReloadConfig 通过 IPC 成功写 ConfigReloaded / CommandCompleted。
- ReloadConfig 失败通过 IPC 返回 errorCode 和安全 message。
- IPC 不可用时上述命令能 fallback 到 agent_control.json。
- fallback 使用情况可被记录。
- 重复 requestId 不会重复执行有副作用命令。

## 建议测试

```text
AgentCommandServer_PauseCommandReturnsCompleted
AgentCommandServer_ResumeCommandReturnsCompleted
AgentCommandServer_StopCommandReturnsCompleted
AgentCommandServer_ReloadConfigReturnsFailureForInvalidConfig
AgentControlService_PauseUsesIpcWhenAvailable
AgentControlService_PauseFallsBackToFileWhenIpcUnavailable
AgentControlService_ReloadConfigPreservesExistingNotRunningMessage
AgentControlService_StopDoesNotShowFalseFailureWhenPipeBreaksAfterExit
ProcessedRequestCache_SuppressesDuplicateRequestIds
```

## 不做什么

- 不迁移 PruneData / ClearHistory。
- 不做状态流订阅。
- 不做托盘。

---

# 阶段 8.5：PruneData / ClearHistory 迁移到 IPC

## 阶段目标

将阶段 7 的维护命令迁移为 IPC 优先：

```text
PruneData
ClearHistory
```

## 行为要求

```text
PruneData:
    IPC 成功返回 DataPruned 结果语义
    IPC 失败 fallback 到 agent_control.json

ClearHistory:
    WPF 仍必须先输入 CLEAR
    确认后 IPC 发送 ClearHistory
    IPC 失败 fallback 到 agent_control.json
```

timeout 规则：

```text
PruneData / ClearHistory 使用 MaintenanceCommandTimeoutMilliseconds = 30000。
如果 response 超时后需要 fallback，必须依赖 requestId 防重，避免重复清理。
```

维护命令必须保持：

```text
Maintenance 状态
重复维护命令拒绝
DataPruned / HistoryCleared 事件
CommandFailed 脱敏
ClearHistory 后 Paused
```

## 验收标准

- PruneData 通过 IPC 成功执行。
- ClearHistory 通过 IPC 成功执行。
- ClearHistory 仍无法绕过 WPF 二次确认。
- Maintenance 中重复 PruneData / ClearHistory 通过 IPC 被拒绝。
- IPC 失败 fallback 后命令仍能被 Agent 文件通道处理。
- Diagnostics 能看到命令来源。
- PruneData / ClearHistory response 超时时不会造成重复清理。
- ClearHistory 二次确认仍在 WPF 层生效；confirmation token 作为后续增强记录。

## 建议测试

```text
AgentCommandServer_PruneDataReturnsCompleted
AgentCommandServer_ClearHistoryReturnsCompletedAndPaused
AgentCommandServer_RejectsMaintenanceCommandDuringMaintenance
SettingsViewModel_ClearHistoryStillRequiresConfirmationWithIpc
AgentControlService_PruneDataFallsBackToFileWhenIpcUnavailable
AgentControlService_ClearHistoryFallsBackToFileWhenIpcUnavailable
AgentControlService_DoesNotDuplicateClearHistoryAfterIpcRequestTimeout
SettingsViewModel_ClearHistoryConfirmationToken_RemainsFutureEnhancement
```

## 不做什么

- 不做清理进度条。
- 不做取消维护任务。
- 不做更复杂数据清理策略。

---

# 阶段 8.6：Diagnostics IPC 状态展示

## 阶段目标

让用户能在 Diagnostics 中判断当前控制通道状态：

```text
IPC 正常
IPC 不可用
fallback 被使用
最近 IPC 错误
```

## 建议新增或调整

```text
src/QuantifiedSelf.Windows.App/ViewModels/MainWindowViewModel.cs
src/QuantifiedSelf.Windows.App/Services/DiagnosticsDataService.cs
src/QuantifiedSelf.Windows.App/Services/AgentIpcStatusService.cs
```

MVP 明确不新增 IPC runtime 持久化文件。  
IPC 状态先存在 App service 内存中，并在 Diagnostics 显示本次 App 会话状态。

## 展示字段

```text
IPC status
Display pipe name
Last IPC success
Last IPC error
Last fallback used
Last command source
```

## 验收标准

- Diagnostics 能显示 IPC enabled / unavailable。
- IPC 成功后 Last IPC success 更新。
- IPC 失败后 Last IPC error 显示安全文案。
- fallback 后 Last fallback used 更新。
- 不显示完整 SID / 完整路径 / 异常原文。
- 只显示 DisplayPipeName，不显示 FullPipeName。
- Diagnostics 旧功能不回归。

## 建议测试

```text
DiagnosticsViewModel_ShowsIpcAvailable
DiagnosticsViewModel_ShowsIpcFallbackUsed
DiagnosticsViewModel_RedactsIpcErrorMessage
DiagnosticsViewModel_DoesNotExposeRawSidInPipeName
DiagnosticsViewModel_ShowsAppSessionIpcStatusOnly
```

## 不做什么

- 不做实时状态流。
- 不做图表。
- 不做复杂 IPC 日志页面。

---

# 阶段 8.7：验收、断连测试与收口

## 自动化验收

完成后应满足：

```text
1. build 0 warning / 0 error
2. test 全部通过
3. IPC 协议模型 roundtrip 测试通过
4. pipe name 安全规则测试通过
5. MaxPayloadBytes / invalid payload / truncated payload 测试通过
6. Ping / GetStatus 测试通过
7. Pause / Resume / Stop IPC 测试通过
8. Stop 快速退出不误报失败测试通过
9. ReloadConfig IPC 成功 / 失败测试通过
10. PruneData / ClearHistory IPC 测试通过
11. fallback 到 agent_control.json 测试通过
12. requestId 轻量防重测试通过
13. IPC timeout 不阻塞 UI 测试通过
14. Diagnostics IPC 状态测试通过
15. CommandFailed / IPC error 脱敏测试通过
16. AgentCommandServer 异常不影响采样测试通过
17. 阶段 7 清理相关测试不回归
```

## 手动验收流程

建议手动验证：

```text
1. 启动 Agent，确认 Diagnostics 显示 IPC 可用。
2. 点击 Refresh，确认状态刷新正常。
3. 执行 Pause，确认通过 IPC 进入 Paused。
4. 执行 Resume，确认通过 IPC 进入 Running。
5. 执行 ReloadConfig，确认 ConfigReloaded / CommandCompleted 可见。
6. 执行 PruneData，确认 DataPruned 可见。
7. 执行 ClearHistory，确认仍需输入 CLEAR，成功后 Paused。
8. 停止 Agent，确认 IPC unavailable，UI 不崩溃。
9. Agent 停止时尝试 ReloadConfig，确认沿用“下次启动生效”语义。
10. 临时禁用 / 模拟 IPC 失败，确认 fallback 到 agent_control.json。
11. Diagnostics 中确认 fallback used 和 last IPC error 文案安全。
12. Diagnostics 中确认只显示 DisplayPipeName，不显示完整 SID。
13. 重启 Agent，确认 IPC 自动恢复。
```

## 长跑验证

建议至少做：

```text
1. Agent Running 15-30 分钟，期间多次 IPC GetStatus / Refresh。
2. Pause / Resume 循环 3-5 次，确认状态不漂移。
3. ReloadConfig 1-2 次，确认配置应用不回归。
4. PruneData 后继续运行 10-15 分钟。
5. ClearHistory 后 Resume，再运行 5-10 分钟。
6. 中途关闭 / 重启 Agent，确认 WPF 不崩溃，IPC 能恢复。
```

## 收口文档

阶段完成后建议新增：

```text
docs/下一步计划-2026-07-01-NamedPipe控制通道MVP/
    01-阶段8.1-IPC协议与NamedPipe基础设施.md
    02-阶段8.2-AgentCommandServer与PingGetStatus.md
    03-阶段8.3-WPF-AgentControlClient与Fallback策略.md
    04-阶段8.4-基础控制命令迁移到IPC.md
    05-阶段8.5-维护命令迁移到IPC.md
    06-阶段8.6-Diagnostics-IPC状态展示.md
    07-阶段8.7-验收断连测试与收口.md
    阶段8-验收清单-YYYY-MM-DD.md
    阶段8-完成说明-YYYY-MM-DD.md
```

---

## 风险与对策

### IPC 卡 UI

风险：

```text
Named Pipe connect / read / write 卡住 UI
```

对策：

```text
所有 IPC 调用必须 async
ConnectTimeout 和 RequestTimeout 分开
维护命令使用更长 MaintenanceCommandTimeout
支持 CancellationToken
超时后 fallback
```

### 双通道重复命令

风险：

```text
IPC 成功后又 fallback，导致命令重复执行
```

对策：

```text
只有 IPC 明确失败 / 超时才 fallback
所有命令带 requestId
ProcessedRequestCache 做轻量内存防重
capacity = 100
ttl = 10 minutes
测试覆盖 fallback 不重复
```

### IPC 与 file fallback 语义不一致

风险：

```text
IPC 返回 completed，但 file fallback 只返回 queued
UI 文案混乱
```

对策：

```text
AgentCommandResult 增加或复用 source / completed 语义
UI 明确显示 queued via fallback
Diagnostics 记录 commandSource
```

### 跨用户控制风险

风险：

```text
同一机器其它用户连接 pipe 控制 Agent
```

对策：

```text
pipe name 包含当前用户 SID hash
server ACL 限制当前用户
不使用全局裸 pipe name
Diagnostics 只显示 DisplayPipeName
```

### 破坏现有稳定链路

风险：

```text
IPC 引入后 Pause / Resume / ReloadConfig 回归
```

对策：

```text
agent_control.json fallback 保留
先 Ping / GetStatus
再迁移低风险命令
最后迁移维护命令
每阶段跑完整测试
第一批提交只做协议模型和 Stream 级 NamedPipeProtocol
```

### Diagnostics 泄露敏感信息

风险：

```text
IPC error message 暴露 SID、路径、异常原文
```

对策：

```text
继续使用 DiagnosticMessageSanitizer
payload 白名单
pipe name 只显示 hash 后的安全名称
测试覆盖脱敏
```

### AgentCommandServer 拖垮采样

风险：

```text
Named Pipe server 监听循环异常，连带影响 Agent 采样 tick
```

对策：

```text
AgentCommandServerHostedService 捕获循环异常
写 health / agent_events 安全文案
短暂 delay 后重启 listen loop
严重错误时仅 IPC unavailable，采样继续
```

---

## 后续候补

Named Pipe 控制通道 MVP 完成后，再考虑：

```text
1. Agent 状态流订阅 / RefreshService 优化
2. WPF Runtime Smoke Test 自动化
3. 托盘图标
4. 开机自启
5. 安装包
6. 最近 7 天趋势和图表
7. 应用分类
8. 分页 SamplesView
9. 数据导出 CSV
10. 本地数据备份与恢复
11. 本地数据加密
12. Windows Service / 计划任务 / 高权限 Agent 方案评估
```

其中最自然的下一步是：

```text
阶段 9：Agent 状态流订阅 / RefreshService 优化
```

但前提是阶段 8 先把请求-响应 IPC 主通道稳定下来。

---

## 最终结论

阶段 7 完成后，WUJI 已经具备：

```text
本地采集
本地诊断
本地浏览
本地配置
本地清理
```

下一步最自然、最有价值的是：

```text
Named Pipe 控制通道 MVP
```

这一阶段完成后，WUJI 的控制能力将从：

```text
文件投递 + 状态轮询
```

升级为：

```text
IPC 请求响应 + 文件 fallback
```

这会让后续托盘、开机自启、状态流订阅和产品化体验都有更稳的地基。
