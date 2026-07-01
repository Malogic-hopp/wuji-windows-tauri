我审核后认为：**这份「Named Pipe 控制通道 MVP」计划可以执行，阶段边界也比较合理**。它没有急着上 gRPC、托盘、开机自启或状态流，而是先把当前 `agent_control.json` 主控制通道升级为 **Named Pipe 请求-响应主通道 + 文件 fallback**，这个顺序是对的。计划里也明确了：先 `Ping / GetStatus`，再迁移 `Pause / Resume / Stop / ReloadConfig`，最后迁移 `PruneData / ClearHistory`，这能很好地降低回归风险。

---

# 总体结论

这份计划可以作为阶段 8 正式执行文档。

当前 WUJI 已经具备：

```text
采集
诊断
数据浏览
配置应用
数据清理
```

所以现在做控制通道升级是合适的。以前不建议太早做 IPC，是因为主功能链路还没稳定；现在命令已经包括 `Pause / Resume / Stop / ReloadConfig / PruneData / ClearHistory`，继续只靠文件投递和轮询推断，反馈链路会越来越绕。

阶段 8 的目标很准确：

```text
IPC 请求响应 + 文件 fallback
```

而不是：

```text
彻底删除文件 fallback
直接上 gRPC
直接做状态流订阅
顺手做托盘 / 自启 / 安装包
```

这个边界很好。

---

# 我认可的关键设计

## 1. Named Pipe 作为主通道，agent_control.json 保留 fallback

这个设计非常重要。
不要在阶段 8 里删除 `agent_control.json`。

正确过渡应该是：

```text
优先 Named Pipe
失败 / 超时 / Agent 未运行时 fallback 到 agent_control.json
Diagnostics 记录 fallbackUsed
```

这样即使 IPC 新链路有问题，也不会破坏现有稳定控制链路。

---

## 2. AgentStateMachine 仍是命令语义中心

计划里明确：

```text
Named Pipe request
    ↓
AgentCommandServer
    ↓
AgentStateMachine.ProcessCommandAsync
    ↓
AgentCommandResult
```

这点必须坚持。
Named Pipe server 只负责通信，不要重新实现 `Pause / Resume / Stop / ReloadConfig / PruneData / ClearHistory` 语义。

否则会出现：

```text
文件通道一套语义
IPC 通道一套语义
状态机逐渐失控
```

---

## 3. 阶段拆分顺序合理

推荐顺序是对的：

```text
8.1 IPC 协议与 Named Pipe 基础设施
8.2 AgentCommandServer + Ping / GetStatus
8.3 WPF AgentControlClient + fallback 策略
8.4 Pause / Resume / Stop / ReloadConfig 迁移到 IPC
8.5 PruneData / ClearHistory 迁移到 IPC
8.6 Diagnostics IPC 状态展示
8.7 验收、断连测试与收口
```

这个顺序先打通低风险状态查询，再迁移基础命令，最后才迁移维护命令。不要调整成“一次性迁移所有命令”。

---

# 建议补强 1：第一批提交再收窄一点

阶段 8.1 建议只做：

```text
Core/Ipc 协议模型
AgentPipeName
NamedPipeProtocol length-prefixed JSON
协议 roundtrip 测试
pipe name 安全测试
非法 payload 安全失败测试
```

不要在 8.1 就创建真正 server/client。

第一批提交建议：

```text
feat(ipc): add named pipe protocol contracts
```

这样第一批是纯协议层，风险最低。

---

# 建议补强 2：先做 in-process / fake transport 测试，再做真实 NamedPipe

Named Pipe 的真实 IO 测试在 Windows 上容易受时序、超时、句柄释放影响。建议结构上抽一层接口：

```text
IAgentIpcClient
IAgentIpcServer
```

或者至少让 `NamedPipeProtocol` 可以基于 `Stream` 测试。

这样 8.1 可以用：

```text
MemoryStream
PipeStream fake
```

先锁住：

```text
length prefix
JSON 序列化
非法消息处理
半截消息处理
超大 payload 拒绝
```

再在 8.2 / 8.3 做真实 Named Pipe 集成测试。

---

# 建议补强 3：协议需要加最大 payload 限制

计划里写了 length-prefixed JSON，这是对的。
但还应该加一个最大长度限制，防止异常 length 导致内存分配过大。

建议：

```text
MaxPayloadBytes = 64 KB
```

或者更保守：

```text
MaxPayloadBytes = 16 KB
```

MVP 的命令请求很小，16 KB 足够。

验收补一条：

```text
NamedPipeProtocol_RejectsPayloadOverMaxSize
```

错误码可以是：

```text
IpcPayloadTooLarge
```

---

# 建议补强 4：requestId 去重要谨慎，不要一开始做复杂持久化

计划的风险里提到：

```text
所有命令带 requestId
AgentStateMachine 保留 lastProcessedRequestId 防重
```

这里建议第一版不要做“复杂持久化防重”，可以先做**短期内存防重**：

```text
最近 N 个 requestId
或最近 5 分钟 requestId
```

避免这种情况：

```text
IPC 成功执行命令
WPF 因 response 超时误以为失败
又 fallback 写 agent_control.json
Agent 再执行一次
```

但不要做太复杂的磁盘级命令队列。阶段 8 MVP 可以先做：

```text
ProcessedRequestCache in memory
capacity = 100
ttl = 10 minutes
```

如果命中重复 requestId：

```text
accepted = true
completed = true
message = Duplicate request ignored
errorCode = DuplicateRequest
```

也可以先只在 `AgentControlService` 层保证“只有 IPC 明确失败才 fallback”，不做 Agent 端防重。
但考虑到 `ClearHistory` 这种命令风险高，我建议至少做内存防重。

---

# 建议补强 5：IPC timeout 分两层

建议区分：

```text
ConnectTimeout
RequestTimeout
```

不要只用一个总 timeout。

推荐：

```text
ConnectTimeoutMilliseconds = 1000
RequestTimeoutMilliseconds = command.TimeoutMilliseconds 或默认 5000
```

对于 `PruneData / ClearHistory`，执行时间可能比普通命令长。这里有两种设计：

## 方案 A：IPC 等待完成

```text
waitForCompletion = true
ClearHistory 直到完成才返回
```

优点：语义简单。
缺点：长任务容易超时。

## 方案 B：IPC 只返回 accepted，结果看 Diagnostics

```text
accepted = true
completed = false
actualState = Maintenance
```

我建议 MVP 采用折中：

```text
Pause / Resume / ReloadConfig：waitForCompletion = true
PruneData / ClearHistory：允许返回 accepted + actualState = Maintenance，再由 Diagnostics / status 观察完成
```

如果你想维持阶段 7 的简单性，也可以先让 Prune/Clear 等待完成，但要把 timeout 放宽，比如：

```text
MaintenanceCommandTimeoutMilliseconds = 30000
```

这点建议在计划里补充清楚。

---

# 建议补强 6：Stop 命令要特别小心

`Stop` 通过 IPC 执行时，可能出现：

```text
Agent 收到 Stop
开始退出
response 还没写回 pipe
server 已经释放
client 看到 pipe broken
```

这会导致 UI 误判为失败。

建议 Stop 语义改成：

```text
Agent 收到 Stop 后先返回 accepted/completed 或 accepted
再异步退出
```

或者：

```text
Stop response 如果 pipe broken，但随后进程消失 / runtime_state = Stopped，也视为成功
```

阶段 8.4 里建议单独写验收：

```text
Stop over IPC does not show false failure when Agent exits quickly.
```

---

# 建议补强 7：ClearHistory 的 IPC 返回不要绕过二次确认

计划已经写了：

```text
ClearHistory 的 CLEAR 确认仍在 WPF UI 层完成
```

这对 UI 入口足够，但从安全角度，Agent 端接到 ClearHistory 命令其实无法知道 UI 有没有确认。

MVP 可以接受，因为本地 IPC 只允许当前用户，且 WPF 是唯一客户端。
但建议在命令里加一个字段，后续可用：

```text
confirmationToken = "CLEAR"
```

或者：

```text
reason = "User confirmed CLEAR"
```

阶段 8 不一定要实现 Agent 校验，但计划里可以写成后续增强。
如果要更稳，Agent 端可以要求 ClearHistory IPC request 里带：

```text
confirmationText = "CLEAR"
```

否则拒绝。文件 fallback 也要保持一致。
这会稍微扩大范围，你可以本阶段不做，但我建议至少记录为风险点。

---

# 建议补强 8：Diagnostics 的 IPC 状态先用 App 内存即可

计划里提到可以新增：

```text
AgentIpcRuntimeState
```

我建议 MVP 不要一开始写新的 runtime 文件。先用 App service 内存保存：

```text
lastIpcSuccessUtc
lastIpcError
lastFallbackUsedUtc
lastCommandSource
pipeName
```

Diagnostics 展示“本次 App 会话状态”即可。

等阶段 9 状态流订阅时，再考虑持久化或统一 runtime 模型。

---

# 建议补强 9：Pipe name 显示要区分内部名和 UI 安全名

计划里要求不显示完整 SID，这是对的。

建议实现两个属性：

```text
FullPipeName
DisplayPipeName
```

内部使用：

```text
QuantifiedSelf.Windows.Agent.<sidHash>
```

UI 显示可以是：

```text
QuantifiedSelf.Windows.Agent.<hash-prefix>
```

例如只显示前 8-12 位 hash。
这样既能排查，又不暴露过多标识。

---

# 建议补强 10：AgentCommandServer 异常不要拖垮采样循环

Named Pipe server 必须旁路化。
如果 IPC server 出错，Agent 采样不应该崩。

建议：

```text
AgentCommandServerHostedService 捕获循环异常
写 health / agent_events
短暂 delay 后重启 listen loop
严重错误时 IPC unavailable，但采样继续
```

验收补一条：

```text
NamedPipeAgentCommandServer_FailureDoesNotStopSampling
```

---

# 推荐修改后的执行重点

我建议你把阶段 8 的重点压成这几条：

```text
1. 协议先行，server/client 后接。
2. Ping/GetStatus 先行，命令迁移后接。
3. Pause/Resume/ReloadConfig 先迁移，Prune/Clear 后迁移。
4. agent_control.json 永远保留 fallback。
5. IPC 失败必须安全超时，不能卡 UI。
6. Diagnostics 要能说明当前用的是 IPC 还是 fallback。
7. Pipe name 和错误信息必须脱敏。
```

---

# 最终审核结论

这份计划可以执行。
建议补充以下小项后开工：

```text
1. NamedPipeProtocol 增加 MaxPayloadBytes。
2. timeout 区分 connect timeout 和 request timeout。
3. Stop 命令单独定义响应语义，避免退出太快导致误报失败。
4. requestId 做轻量内存防重，至少覆盖 IPC 成功但 fallback 重发的风险。
5. Diagnostics IPC 状态先用 App 内存，不急着持久化。
6. AgentCommandServer 异常不能拖垮采样。
```

第一阶段建议从 **8.1：IPC 协议与 Named Pipe 基础设施** 开始，第一批提交只做协议模型和 `NamedPipeProtocol`，不要先接 Agent 或 WPF。
