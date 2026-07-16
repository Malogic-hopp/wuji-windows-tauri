# ADR-001：Tauri 2 + React 19 Bridge 边界

日期：2026-07-16

状态：已接受

关联规划：`docs/design/Tauri2-React19前端实施规划-2026-07-16.md`

## 1. 决策

WUJI 的第一个替代 UI 采用以下进程与依赖结构：

```text
React 19 WebView
    -> 白名单 Tauri commands/events
Tauri 2 Rust Shell
    -> 私有 stdin/stdout NDJSON JSON-RPC
QuantifiedSelf.Windows.Client.Bridge
    -> IWujiClient
Application + Client + Core
    -> Infrastructure / Agent
```

Bridge、Tauri 宿主和 Agent 是三个独立生命周期。Bridge 或 Tauri 退出时默认不停止 Agent；只有显式 `agent.stop` 用例可以改变 Agent 的运行生命周期。

## 2. 选择理由

- 现有 `IWujiClient` 已封装 SQLite、Named Pipe、文件、进程和注册表实现；
- stdio 只存在于父子进程之间，不新增可被同机进程探测的 TCP 或 Named Pipe 服务端点；
- Rust 只承担 Windows 桌面宿主职责，不复制 Application 业务规则；
- schema-first 合同能为 C#、Rust、TypeScript 生成同源类型，避免三端字段漂移；
- WPF 可继续作为稳定参考实现和回滚入口。

## 3. 强制边界

- React 不得直接获得 shell、文件系统、SQL、HTTP 或任意进程执行能力；
- Rust 不得访问 SQLite、Agent Named Pipe、设置/runtime 文件、注册表或 Agent executable；
- Bridge 不得直接引用 Infrastructure、Agent、App 或 UI 框架；
- Bridge 只能通过 `IWujiClient`/feature client 执行应用用例；
- Application/Core DTO 必须显式投影为安全 Bridge DTO，不得直接序列化整个内部对象；
- stdout 只输出协议消息，日志只写 stderr；
- runtime channel 由宿主启动参数决定，React 不得传入 channel、data root 或可执行文件路径。

## 4. 阶段 1 合同范围

阶段 1 只实现：

| 方法 | 用途 | 副作用 |
|---|---|---:|
| `bridge.hello` | 协商 API 版本和能力 | 否 |
| `client.initialize` | 初始化 `IWujiClient` 并返回安全上下文 | 幂等 |
| `bridge.shutdown` | 结束 Bridge，不停止 Agent | 幂等 |

阶段 2 才加入 Agent status/start/pause/resume/stop；阶段 4、5 才加入 Dashboard 与 Settings。阶段 1 不提前暴露通用 method forwarding。

### 4.1 阶段 2 合同扩展

阶段 2 已按原决策增加以下白名单方法：

| 方法 | `IWujiClient` 用例 | 副作用 | timeout |
|---|---|---:|---:|
| `agent.getStatus` | `Agent.Status.GetStatusAsync` | 否 | 5 秒 |
| `agent.start` | `Agent.Process.StartAgentAsync` | 是 | 15 秒 |
| `agent.pause` | `Agent.Control.RequestPauseAsync` | 是 | 10 秒 |
| `agent.resume` | `Agent.Control.RequestResumeAsync` | 是 | 10 秒 |
| `agent.stop` | `Agent.Process.StopAgentAsync` 统一停止用例 | 是 | 20 秒 |

Agent 内部对象必须投影为 schema 生成的安全 `AgentStatus`/`CommandResult`。合同不包含 PID、用户名、机器名、路径、Pipe 名、内部 request id、raw exception 或内部 runtime/health 对象。

### 4.2 阶段 4A 合同扩展

阶段 4A 增加一个只读白名单方法：

| 方法 | `IWujiClient` 用例 | 副作用 | timeout |
|---|---|---:|---:|
| `activity.getOverview` | `Activity.Overview` 的今日摘要、Top Apps、最近会话 | 否 | 10 秒 |

Bridge 同时启动三个彼此独立的 Overview 查询并聚合为一次响应，不直接访问 Apps/Sessions feature client 或底层存储。安全 DTO 只包含显示名、UTC 时间和时长/计数；不包含会话数据库 ID、进程名、窗口标题、路径、数据库信息或内部异常。应用显示名即使意外包含路径，也会在 Bridge mapper 中去掉目录部分并限制长度。

### 4.3 阶段 4B Rust command 扩展

Tauri Rust Shell 增加单一语义 command `activity_get_overview`，React 白名单使用相同固定名称。command 不接收 method、路径、channel 或查询参数，只执行：

```text
supervisor.request("activity.getOverview")
```

Rust 将响应直接反序列化为 schema 生成的 `ActivityOverviewResult`。Bridge 内层 timeout 保持 10 秒，Rust 外层只读查询 timeout 为 12 秒，为 Bridge 返回安全 timeout 和 stdio 调度保留边界；该 timeout 可重试。Rust 不累加 duration、不排序 Top Apps、不推导 Agent/会话状态，也不改变 Bridge 返回的数组顺序。

BridgeSupervisor 每次进入 `ready` 都发布带 generation 的固定 availability 事件。React 收到 `ready` 后使 `['activity', 'overview']` query 失效；手动重连成功也执行同一规则，避免继续展示旧 Bridge generation 的 Dashboard 缓存。失效只触发重新读取，不在 React 中重算业务数据。

### 4.4 阶段 4C React Dashboard 状态与刷新

React Dashboard 只通过 `bridgeClient.getActivityOverview()` 读取生成的 `ActivityOverviewResult`，不直接调用 `invoke`，也不接收路径、SQL、原始窗口标题或进程名。页面状态固定为：

```text
Loading | Empty | Ready | Error
```

Empty 仅在摘要的全部 duration/session count 为零且 Top Apps、最近会话都为空时成立。Ready 按 Bridge 返回顺序呈现 Top Apps，不在 TypeScript 重排；duration 的时/分/秒拆分和 UTC 到本地时间转换仅属于 locale-aware 显示，不改变业务数值。

页面可见时 Overview 每 15 秒刷新；`document.visibilityState` 为 hidden 时降至 60 秒，并允许 background interval 继续以低频运行。手动刷新在已 initialize 时只重取 Overview；initialize 失败时先恢复 initialize 再查询。Loading 使用 polite status，Error 使用 alert 和安全错误文案，刷新完成通过 live region 通知读屏。原生 button、全局 focus-visible、forced-colors 和 prefers-reduced-motion 规则覆盖键盘、High Contrast 与减少动画要求。

## 5. 协议与版本

- JSON-RPC 版本固定为 `2.0`；
- Bridge API 初始版本为 `1.0`；
- UTF-8 NDJSON，一行一条消息；
- 单条消息默认最大 1 MiB；
- request 使用字符串 id 和 correlation id；
- 每个请求有宿主取消和超时；
- 相同 request id + method 在 5 分钟去重窗口内返回缓存响应，不重复执行副作用；
- 相同 request id 被用于不同 method 时拒绝；
- 未完成 hello 前拒绝 initialize；
- 不兼容 API major 立即返回稳定错误。

## 6. 稳定错误模型

阶段 1 固定以下错误码：

| code | kind | retryable | 含义 |
|---|---|---:|---|
| `parse_error` | validation | false | 非法 JSON |
| `invalid_request` | validation | false | 包络或参数非法 |
| `payload_too_large` | validation | false | 超过消息上限 |
| `method_not_found` | unsupported | false | 方法不在白名单 |
| `handshake_required` | conflict | true | 尚未完成 hello |
| `initialization_required` | conflict | true | Agent 方法调用前尚未 initialize |
| `unsupported_api_version` | unsupported | false | API major 不兼容 |
| `request_timeout` | transient | true | 请求超时 |
| `request_cancelled` | transient | true | 宿主取消请求 |
| `internal_error` | internal | true | 未分类安全错误 |

错误 message 是安全中文说明。前端未来只能按 code/kind/retryable 决策，不解析 message 文本。

## 7. 敏感字段 denylist

Bridge 合同、错误和日志不得包含：

- 绝对路径、`WujiClient.Paths` 和 data root；
- Windows SID、用户名、机器名；
- Named Pipe 完整名称；
- Agent executable、SQLite、日志、runtime/control 文件路径；
- 注册表 command/value；
- raw exception、stack trace、SQL；
- 未遮罩窗口标题；
- 完整 settings payload。

阶段 1 initialize 只返回：channel name、产品显示名、是否默认 channel、API version 和 capability 名称。

## 8. dev-only 决策

阶段 0～7 的 Bridge/Tauri 验证只允许 `dev` channel：

- Bridge 默认 channel 为 `dev`；
- 阶段 1 明确拒绝 `prod` 和自定义 channel；
- React 不能传入 channel；
- production 支持必须经过阶段 7 promotion gate 后单独修改；
- 本阶段不读取生产数据、不创建生产目录、不修改生产启动项。

## 9. 工具链版本策略

当前基线：

| 工具 | 仓库/本机状态 | 决策 |
|---|---|---|
| .NET SDK | `global.json` 8.0.422；本机 8.0.423 补丁前滚 | Bridge/生成器使用 .NET 8 |
| Node.js | 本机 24.14.0 | 阶段 3 用仓库文件锁定 Node LTS |
| Corepack | 本机 0.34.6 | 阶段 3 锁定 pnpm 与 lockfile |
| Rust/Cargo | 当前未安装 | 阶段 3 前安装并以 `rust-toolchain.toml` 锁定 stable |
| Tauri | 尚未引入 | 阶段 3 锁定 Tauri 2 minor 与 Cargo.lock |
| React | 尚未引入 | 阶段 3 锁定 React 19 minor 与 pnpm-lock.yaml |

阶段 1 的 Rust/TypeScript 文件是合同生成 staging artifact，不要求本机 Rust/Node 编译。合同生成器本身只使用 .NET 8 BCL，不增加外部生成器包或网络构建依赖。

## 10. 合同生成决策

- `contracts/wuji-bridge/v1/bridge.schema.json` 是唯一协议来源；
- schema 使用 JSON Schema 2020-12 的 `$defs` 描述 DTO，并附带 method/error 元数据；
- 仓库内 .NET 生成器读取受控 JSON Schema 子集，确定性生成 C#、Rust、TypeScript；
- 生成文件提交到仓库，支持离线构建和审查；
- `--write` 更新生成物；
- `--check` 发现漂移时返回非零退出码；
- 生成物不得手工修改；
- 如果后续 DTO 需要超出当前生成器支持的 schema 能力，先扩展生成器和测试，不退回三端手写。

这个决定替代规划中的 NJsonSchema/typify/json-schema-to-typescript 候选 spike：阶段 1 合同很小，仓库内生成器能避免引入三个额外工具链和不确定的跨版本输出；未来如改用成熟生成器，必须保持 schema 为唯一来源并通过 ADR 记录迁移。

## 11. 生命周期

正常启动：Bridge 解析 dev channel → 创建一个 `IWujiClient` → hello → initialize。

正常退出：收到 shutdown 或 stdin EOF → 停止接收请求 → `IWujiClient.DisposeAsync()` → Bridge 退出。

取消/异常：宿主取消、输入中断或未处理错误 → 取消 in-flight request → `IWujiClient.DisposeAsync()` → Bridge 退出。

所有路径都禁止隐式调用 Agent stop、kill、pause 或其他控制命令。

## 12. 当前基线与验证门禁

阶段 7 已收口为独立提交，基线为：

- Build：0 warning / 0 error；
- Architecture：35/35；
- Fast：114/114；
- Integration：421/421；
- Wpf：15/15；
- Full：560/560。

阶段 1 完成时必须重新运行完整 solution build/test，并新增：

- Bridge 合同漂移检查；
- Bridge 项目引用/UI marker 架构门禁；
- hello/initialize/shutdown；
- 非法 JSON、未知方法、超大 payload、版本不兼容；
- 重复 request id；
- timeout、宿主 cancellation、EOF；
- stdout purity 与 stderr 日志隔离；
- shutdown/Dispose 不访问 Agent 控制。

阶段 2 已完成 Agent Bridge method、统一 stop、安全 DTO 和自动门禁：Release Build 0 warning / 0 error，Full 607/607。WPF 可见 UI、Dashboard、Settings 与托盘实现仍未修改。WPF dev GUI 对照 smoke 已由人工确认通过，覆盖 Agent 生命周期、页面刷新、托盘退出、UI/Agent 生命周期独立和 dev/prod 隔离；阶段 3 前置门禁已解除。

## 13. 后果

正面结果：

- 第一版 Bridge 无网络监听面；
- 跨语言合同可离线、可审查、可检测漂移；
- Bridge 生命周期不污染 Agent；
- 后续 Rust/React 可以在稳定协议上增量实现。

代价：

- 仓库需要维护一个受控 JSON Schema 子集生成器；
- Application/Core 到 Bridge DTO 需要显式 mapper；
- Rust 工具链仍是阶段 3 的外部前置条件；
- production channel、Tauri 打包和可见 UI 尚未实现。
