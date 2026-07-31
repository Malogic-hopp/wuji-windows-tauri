# WUJI Tauri + Rust Rebuild v0.1 实施基线

状态：Draft；v0.1 合同与 §16 增补为实施对齐点，实现/验收结论以 migration-status 与证据包为准；Draft 的正式接受留待产品评审
版本：v0.1
最后更新：2026-07-31（Heatmap 周导航合同与日常功能演进纪律）
目标技术栈：React 19 + TypeScript + Tauri 2 + Rust Agent/Core + SQLite
交付属性：dev-only 工程里程碑，不是用户生产发布
长期规划：[ADR-002](./ADR-002-React-Tauri-Rust目标架构.md) 与 [01–08](./README.md#1-文档层级与适用范围)

仓库转换决策：[ADR-003](./ADR-003-Rebuild-only仓库转换与旧系统源码退役.md)。该 ADR 经产品负责人批准，仅覆盖本文原有“旧源码继续位于同一工作树”的约束；production cutover、旧安装/旧数据退役和 G-RETIRE 仍未完成。

## 1. 本文用途

本文是 Rebuild v0.1 的唯一实施范围入口，回答“第一版现在具体做什么、做到什么程度算完成”。

01–08、ADR-002 和三轮审核回应保留为长期架构与风险规划，不要求 v0.1 一次实现其中所有机制。本文没有否定长期设计，而是选择一个可运行、可验证、可替换 `.NET Bridge` 的最小子集。

### 1.1 合同演进纪律

v0.1 已进入日常使用后的增量功能可以在产品负责人明确提出并确认范围后纳入本文，但实现代码或 `migration-status.md` 不能自行扩大合同。新增或改变用户功能时，同一逻辑改动必须同步更新本文相关的固定 Tauri command 白名单（§8.3）、输入与 DTO 语义（§8.4）、UI 行为（§10）、退出条件（§11）及确定性测试；未完成这些同步的功能不视为合同内完成。

`migration-status.md` 只记录实际实现和验证状态，不授权功能、协议或架构例外。若需求改变 Agent 单写、Tauri 只读、固定命令白名单、单一 Coordinator/Barrier 路径、数据目录或进程身份等架构边界，仍须先形成并接受 ADR；普通页面、只读查询和交互增强不得借“日常迭代”绕开这些边界。

v0.1 的首要任务是完成技术栈和运行链路替换：

```text
React
→ Tauri Rust Host
→ Rust Agent
→ 新建 SQLite v0.1 数据库
```

v0.1 不以完整行为分析、旧数据迁移或生产退役为完成条件。“首版”仅指新架构第一条可运行开发链路；如果目标是向用户交付生产版本，必须另立 production hardening 版本，补齐安装升级、正式 Schema migration、进程认证、发布和 cutover 门禁。

## 2. v0.1 成功定义

同时满足以下条件才算完成：

- React/Tauri 不再通过 `.NET Bridge` 获取 v0.1 数据或控制 v0.1 Agent；
- 独立 Rust Agent 能采集前台 App 和用户 idle，经过隐私过滤后写入全新 SQLite；
- Agent 是行为数据库唯一写入者；Tauri 对行为数据库严格只读，同时负责设置文件写入、Agent 进程管理和经 IPC 发出的运行控制；
- 能生成 App Activity Segment 和 Work Block，而不是只展示离散采样；
- Today、Timeline、Heatmap、Settings、Diagnostics 五条最小用户路径可用；
- Desktop 退出不停止 Agent；“暂停记录”只暂停 Capture、Agent 继续在线；“停止 Agent”先提交 CaptureStop 边界，再请求 Agent graceful shutdown；
- Rebuild 与旧系统使用完全不同的进程、Pipe、mutex、数据目录和数据库；
- 旧 WPF/C# 源码按 ADR-003 从当前工作树移除，通过远程可达冻结提交恢复；用户机器上的旧安装与旧数据库保持原样；
- v0.1 验收项全部通过，dev 包中不再携带或启动 Bridge sidecar。

## 3. 首版明确范围

### 3.1 必须实现

| 范围 | v0.1 能力 |
|---|---|
| Desktop | React 19、Tauri 2、窗口、托盘、单实例、Agent 进程发现/启动 |
| Agent | 独立 Rust 进程、单实例、Capture/Processor/Writer/IPC/Heartbeat |
| Capture | 前台 HWND、PID、标准化进程名、用户 idle；阻塞 Win32 调用不占 Tokio worker |
| Privacy | 原始路径不落库；v0.1 不持久化窗口标题；排除 App 不产生 Observation |
| Activity | Observation → 单状态 App Activity Segment；Active/Idle/Unknown |
| Work | 基于 Active、长 Idle、gap、Pause/Stop/Sleep 的 Work Block |
| SQLite | 全新空库初始化、WAL、外键、单写、只读查询、最小小时/日读模型 |
| IPC | 同用户 dev channel Named Pipe、固定命令、长度限制、request ID、超时 |
| Settings | Tauri 原子写 JSON；Agent 完整 reload；采样、Idle、Work 和排除 App 设置 |
| UI | Today、Timeline、Heatmap、Settings、Diagnostics；Loading/Empty/Ready/Error |
| 诊断 | Agent 状态、最后采集、最后写入、安全错误码、队列深度、drop count |

### 3.2 明确延期

以下内容不进入 v0.1，也不得阻挡 v0.1 完成：

- Context Segment、Project Hint、Interruption、Effective Context Switch；
- Focus、碎片时间、洞察解释和机器学习；
- 原始或脱敏窗口标题持久化、完整路径持久化；
- Segmentation/Work/Analysis Generation；
- Result Set、Query Snapshot、Snapshot Slice、W0/W1/W2；
- Snapshot Lease、Result Set GC、NativeV2Summary、LegacySummary；
- Identity Generation/Resolution 和跨盐历史合并；
- `schema-v2-manifest.yaml` 和完整机器 Schema 平台；
- v1→v2 数据导入、原地 Schema migration、数据库 pointer 切换；
- Clear History、导出、隐私削弱等破坏性命令；
- production Desktop 签名清单、逐帧 session capability 和 updater；
- 12 周趋势、复杂 Insights、跨世代历史重建；
- prod cutover、旧安装停产、旧数据退役和 G-RETIRE；旧源码从当前工作树移除是 ADR-003 已批准的仓库维护例外，不表示这些 Gate 已通过。

延期功能对应的长期设计继续保存在 01–08。实现某项延期能力前，再从长期规划中提取当期最小合同，不提前搭建框架。

## 4. 目标目录与运行命名空间

v0.1 使用一个小型 Rust workspace，不映射现有 C# 项目数量：

```text
Cargo.toml                  Rust workspace 入口
apps/
  desktop/
    src/                    React + TypeScript
    src-tauri/              Tauri Host
  agent/
    src/                    Rust Agent binary
crates/
  wuji-core/
    src/                    domain、settings、DTO、error
  wuji-storage/
    schema/schema.sql       v0.1 空库 Schema
    src/                    writer、queries、bootstrap
  wuji-windows/
    src/                    foreground、idle、process、pipe
scripts/                    package、soak、验收脚本
docs/dev/                   当前基线、ADR、审核与证据
```

约束：

- `wuji-core` 不依赖 Tauri、Win32 或 rusqlite；
- `wuji-storage` 只依赖 Core；
- `wuji-windows` 封装 Win32 和 Named Pipe；
- Agent 依赖三个 crate；
- Tauri Host 依赖 Core、Storage 的只读 Query 和 Windows IPC client；
- v0.1 不再拆 service/interface crate。

旧 `src/QuantifiedSelf.Windows.Tauri` 只可从 ADR-003 冻结提交作为 UI 和托盘行为参考；当前 `apps/desktop` 不复制 `src/bridge` 和 `BridgeSupervisor`。

### 4.1 固定运行命名空间

现有 C# 系统已经使用 `dev`、`WUJI Dev` 和 `WUJI-Dev`。v0.1 不复用这些名称，也不接受 React 或命令行传入任意路径/标识：

| 项目 | v0.1 固定值 |
|---|---|
| Channel | `rebuild-v01-dev` |
| Desktop executable | `wuji-rebuild-desktop-v01.exe` |
| Agent executable | `wuji-rebuild-agent-v01.exe` |
| Tauri identifier | `com.wuji.rebuild.v01.dev` |
| Product name | `吾迹 Rebuild v0.1（开发）` |
| Local data root | `%LOCALAPPDATA%\WUJI-Rebuild-V01\dev` |
| Database | `<data-root>\data\wuji-rebuild-v0.1.db` |
| Settings | `<data-root>\config\settings.json` |
| Logs | `<data-root>\logs` |
| Pipe | `\\.\pipe\WUJI.Rebuild.V01.Dev.<user-scope>` |
| Agent mutex | `Local\WUJI.Rebuild.V01.Dev.Agent.<user-scope>` |
| Desktop mutex | `Local\WUJI.Rebuild.V01.Dev.Desktop.<user-scope>` |
| Run Key | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` / `WUJI Rebuild v0.1 Dev` |

`<user-scope>` 是当前用户 SID UTF-8 表示的 SHA-256 前 16 个小写十六进制字符；原始 SID 不写日志、数据库或 Pipe 名。路径由可信 Rust Path Resolver 生成，WebView 不接触这些值。测试只能在显式 `rebuild-v01-test-<ulid>` channel 下派生隔离命名空间，release/dev binary 拒绝其他 channel。

## 5. 最小运行架构

```text
ForegroundCaptureLoop
  每 1 秒检查调度
  到达 sampling interval 后调用 Win32
        ↓ bounded mpsc<RawCapture>

ObservationProcessor
  标准化进程名
  应用排除规则
  判断 Active/Idle/Unknown
  丢弃原始路径和标题
        ↓ bounded mpsc<WriterMessage>

SingleSQLiteWriter
  observation
  activity segment
  work block
  hourly/daily projection
  runtime heartbeat

独立：CommandServer / HeartbeatLoop / MaintenanceLite
```

`MaintenanceLite` v0.1 只执行 WAL checkpoint 和安全日志轮换，不做 prune、rebuild、compaction 或数据库替换。

### 5.1 默认参数

| 参数 | v0.1 默认值 | 允许值 |
|---|---:|---|
| Capture Loop wake | 1 秒 | 固定 |
| Sampling interval | 3 秒 | 1、3、5、10 秒 |
| Idle threshold | 60 秒 | 30–1800 秒 |
| Work break idle | 300 秒 | 60–3600 秒，且大于 Idle threshold |
| Observation gap cap | `max(3 × sampling interval, 15 秒)` | 派生值 |
| Capture queue capacity | 256 | 编译期常量；容量测试后再改 |
| Writer data queue capacity | 512 | 编译期常量；容量测试后再改 |
| Writer control queue capacity | 64 | 固定预留；控制消息不得使用 `try_send` 丢弃 |
| IPC request timeout | 3 秒 | 固定首版值 |
| IPC payload ceiling | 64 KiB | 固定首版值 |

这些值属于 `algorithm_version = "rebuild-v0.1"`。Settings 变化只影响未来数据，不触发历史重建。

### 5.2 Single Writer、优先级与背压

Agent 只有一个 `rusqlite::Connection` 以读写方式打开行为数据库。所有 Observation、Segment、Work、Projection、Settings applied revision、runtime heartbeat 和 checkpoint 都由 Writer 执行；`MaintenanceLite` 只能发送 `WriterControl::Checkpoint`，不得自行打开第二个读写连接。

Writer 有两条有界入口：

- data lane，容量 512：处理已过滤 Observation；满时 drop-new；
- control lane，容量 64：处理 Pause/Stop/Sleep/Lock、settings applied、heartbeat、checkpoint 和 shutdown；发送方等待容量，不得静默丢弃；Writer 使用 biased select 优先消费 control lane。

Capture queue 或 Writer data lane drop-new 时，生产者必须原子增加共享 `continuity_epoch` 和对应 drop counter。每条后续 data message 都携带 epoch；Writer 每秒 heartbeat control message 也携带 epoch。Writer 一旦观察到 epoch 增长，必须先关闭 open Segment/Work 并建立相应 gap，再处理后续 Observation。因此，即使“queue drop 通知”本身来不及入队，也不能跨 drop 延续计时。

Writer 自身也会因 `clock_changed` 增加 epoch（见 6.5 第 5 条）。此后队列中仍可能滞留 bump 前入队、携带更旧 epoch 的消息：Writer 对任何 `message.epoch != 当前 epoch` 的消息一律按不连续处理（关闭 open 行并记 gap 后处理），不得把当前 epoch 反向回退，也不得跨该消息延续 Segment/Work。

故障策略固定如下：

| 故障 | Agent 行为 | 恢复 |
|---|---|---|
| `SQLITE_BUSY/LOCKED` | `busy_timeout=750ms`，再以 100/250ms 间隔额外重试 2 次；Writer 标记 `degraded`，不确认未提交消息 | 最坏约 2.6 秒，保持在 IPC 3 秒 timeout 内；成功后回到 `healthy`，持续失败则停止 Capture 并进入 `faulted` |
| disk full / I/O | 停止 Capture，关闭连续性；Agent 保持 IPC 在线并报告安全错误码 | 只允许用户释放空间后重启；不在内存无限缓存 |
| corruption / FK failure | 停止 Capture，Writer `faulted`；禁止自动删除、替换或修复数据库 | 保留现场，等待显式诊断处理 |
| checkpoint busy | 保留 WAL，记录安全诊断，不阻断正常写入 | 下一个维护周期重试 |

IPC 只有在 Writer 成功提交控制状态事务后才返回 Capture 状态变更成功。Writer `faulted` 时历史只读 Query 仍可尝试打开数据库；失败则返回稳定错误，不把原始 SQLite 文本交给 React。

## 6. 最小行为语义

### 6.1 Observation

一次已通过隐私过滤的前台 App 采样。只保存：时间、App Identity、Activity State、采集质量和设置 revision。

Observation 本身不代表固定时长。只有下一条 Observation 在同一 runtime、同一 App/状态且未超过 gap cap 时，二者间隔才能归属给前一条。

v0.1 不读取窗口标题。进程路径只在 Capture 调用栈内用于取文件名，随后立即丢弃；PID 也不越过 Processor。`normalized_process_name` 固定为文件名经过 trim、Unicode NFKC、Unicode lowercase 后的结果并保留 `.exe`；无法得到非空文件名时不创建 App/Observation，只记录不含进程信息的 `capture_error` gap。`app_key = "proc:" + sha256(normalized_process_name)`；排除规则与 App switch 都使用同一规范名。`display_name` 使用首次成功采集的原文件名去掉末尾 `.exe`，只作为展示值。Idle API 成功时，低于阈值为 `active`、达到阈值为 `idle`；API 失败时为 `unknown + idle_unavailable`，不得沿用上一状态。已知限制：App 身份只到进程文件名，共享宿主进程（如 UWP 的 `ApplicationFrameHost.exe`、脚本解释器）会把多个应用合并为同一 App；这与当前 C# 系统一致，v0.1 不做窗口级或包级区分。

### 6.2 Activity Segment

相邻 Observation 满足以下条件时属于同一 Segment：

- App 相同；
- Activity State 相同；
- 间隔不超过 gap cap；
- 中间没有隐私排除、queue drop、Pause、Stop、Sleep 或进程重启。

v0.1 Segment 是 App 级，不是窗口级。App 或状态变化立即切段。

### 6.3 Work Block

- 第一段 Active Activity 开始 Work Block；
- 短 Idle 可以留在 Work Block 中，但不计入 active duration；
- Idle 达到 Work break 阈值时，Work Block 回溯结束于 Idle 开始；
- gap、隐私排除、Pause、Stop、Sleep、Agent 重启立即结束 Work Block；
- App 切换不结束 Work Block；
- v0.1 不把 Work Block 命名为“专注”。

### 6.4 保守计时

- 不使用 sampling interval 补算缺失时长；
- queue 满时丢弃新采样、增加 drop count，并关闭连续性；
- Agent crash 后不计算最后 Observation 到重启之间的时间；
- Unknown、gap 和隐私排除不计为 active；
- 所有持续时间使用整数毫秒和 UTC，展示日期使用数据库创建时固定的 reporting time zone。

### 6.5 精确归属与切段算法

Writer 按 `(runtime_id, capture_sequence)` 处理 Observation，并同时校验 UTC 与同一 runtime 内的 monotonic time：

1. 第一条 Observation 创建 `start=end=captured_at`、`duration=0` 的 open Activity Segment；单条 Observation 因此会保留零时长 Segment，但不贡献 projection duration。
2. 下一条 Observation 只有在 continuity epoch、App、Activity State 均相同，且 UTC/monotonic delta 都为正、delta 不超过 gap cap 时，才把整个 delta 归给前一 Segment。
3. App 或 Activity State 改变时，旧 Segment 在上一条 Observation 时刻关闭，新 Segment 在当前 Observation 时刻以零时长开始；两次采样之间不归给任一 App，并记录 `sampling_transition` quality gap。该 gap 不结束 Work Block，也不计 active/idle。
4. delta 超过 gap cap、epoch 改变或出现显式生命周期边界时，关闭 Segment 和 Work Block；缺口不补算。App/状态变化与超时、epoch 变化或生命周期边界同时成立时，按本条处理（边界优先于 transition），不记录 `sampling_transition`，该相邻对也不构成 raw app switch。
5. 若 `utc_delta <= 0`，或 `abs(utc_delta - monotonic_delta) > 2000ms`，增加 continuity epoch 并按 `clock_changed` 处理，不跨边界归属时间。UTC 回拨时 `clock_changed` 保存为旧端点上的零长度 gap，避免制造负区间；新时间轴从当前 Observation 重新开始。已知取舍：回拨后新 Segment 的 UTC 起点早于旧端点时，重叠区间在投影交集求和中会被双计，幅度不超过回拨幅度；该事件由 `clock_changed` gap 标记，黄金样本固定此行为，不在 v0.1 引入旧 Segment 的回缩改写。

Idle 起点固定为“第一条 `idle_seconds >= idleThresholdSeconds` 的 Observation 时刻”，不使用 `last_input` 反推，也不回填阈值前的采样间隔。该规则的真实数量级是：idle 起点比真实 last-input 时刻至多晚 `idleThresholdSeconds + 一个采样间隔`；相应地，每次进入 idle 时，阈值内无输入的时间仍按 active 归属，active 时长至多高估一个 idle threshold。这是 v0.1 为保持 open Segment 只向前更新（见 7.3）而做的明确取舍：少计的是 idle，多计的是 active 的宽限期。长期模型的 last-input 反推（03 §4）需要回缩已归属的 open Segment，留待后续版本评估，不属于 v0.1。

### 6.6 Work Block 精确状态

- 第一段产生正 active duration 时创建 Work Block；零时长 Active Observation 单独存在时不创建；
- Idle 进入 pending 状态：若在 work-break 阈值前恢复 Active，将已可靠归属的 Idle duration 计为 `short_idle_duration_ms`；达到阈值则把 Work Block 结束点固定在 Idle Segment 起点，整段 Idle 不进入该 Work Block；
- Unknown、capture delayed、隐私排除、queue drop、Pause、Stop、Sleep、Lock、Agent restart、clock changed 和 shutdown 立即结束 Work Block；
- `sampling_transition` 不结束 Work Block，但该间隔不计入 active 或 short idle；
- App 改变本身不结束 Work Block。

取舍说明：单条 Unknown Observation（如 idle API 偶发失败）即结束 Work Block，会使 Work Block 偏碎。v0.1 明确保留该保守方向——不在 Unknown 两侧虚构工作连续性；若未来数据显示碎片化影响可用性，必须以新算法语义另行评审，不得在 v0.1 实现中静默放宽为跨 Unknown 延续。

`raw_app_switch_count` 定义为：同一 runtime、同一 continuity epoch 内，两条相邻 Observation 的 App 不同，二者 state 均不是 `unknown`，且正向 delta 不超过 gap cap。事件归到后一条 Observation 的 local date；Pause/Stop/gap/restart 两侧不计 switch。

### 6.7 恢复、系统边界与分桶

Agent 启动事务先处理遗留 open 行：Activity Segment 保持其最后已提交 `end_at` 并以 `agent_restart` 关闭；Work Block同样关闭；从旧 runtime 最后提交时刻到新 runtime 首条有效 Observation 记录 `agent_restart` gap，不计时。上一 runtime 遗留的 open gap 也在同一事务内处理：以该 runtime 最后已提交写入时刻为 `end_at`、按原 kind 关闭（不虚构其持续期间的新 kind），随后再按上句记录 `agent_restart` gap。

任意时刻全库至多一个 open gap（由 Schema 部分唯一索引兜底）。新边界事件到达且已存在 open gap 时按以下规则序列化，不得并存两个 open gap：kind 相同且区间相邻时 `event_count + 1` 并延伸；kind 不同时以事件时刻关闭现有 gap（保留原 kind），立即以新 kind 打开新 gap。Sleep/Lock 事件发生在 `capture_paused` 或 `capture_stopped` 的 open gap 期间时，采集本已停止，不改变该 open gap，仅记录安全诊断计数。

Sleep/Lock 由 Windows session/power event 产生 control boundary；Writer 在最后已可靠归属端点关闭 open 行，在第一条 Resume/Unlock 后的有效 Observation 处关闭 gap。若事件丢失，monotonic/UTC 差异和 gap cap 仍提供保守兜底。

Projection 把可靠区间按“下一个 UTC 小时边界”和“固定 reporting time zone 的下一个 local midnight”中更早者拆分：

- `hourly_app_usage` 的 `local_date`、`local_hour`、`local_utc_offset_minutes` 恒等于 `utc_hour_start_ms` 按固定 reporting time zone 换算的本地桶起点。当 reporting time zone 的 UTC 偏移不是整小时（如 UTC+5:30、+5:45）时，一个 UTC 小时桶可能横跨两个 local date；此时 local 字段只描述桶起点，日聚合一律以 `daily_*` 表为准，不得用 hourly 行的 local 字段拼日。
- DST fallback 的两个同名 local hour 以不同 `utc_hour_start_ms + local_utc_offset_minutes` 保存，不合并；
- DST spring-forward 不生成不存在的 local hour；
- daily Work Block count 是当日具有正 active 交集的 distinct Work Block 数；longest 值是单个 Work Block 在该日的 active 交集；
- Reporting time zone 由 `iana-time-zone` 在建库时解析为 IANA ID，所有换算统一使用锁定版本的 `chrono-tz`；无法解析则以 `TIME_ZONE_UNAVAILABLE` 放弃建库，不静默使用 UTC。时区在 v0.1 建库后不可修改。

## 7. SQLite v0.1

### 7.1 新数据库策略

v0.1 不迁移旧数据库。Agent 在独立 dev data root 创建新的 `wuji-rebuild-v0.1.db`：

```text
旧 WPF/C# 数据库：rebuild 不打开、不修改
新 Rust v0.1 数据库：根据内嵌 schema.sql 从零创建
```

数据库首次创建严格执行 7.2 的 bootstrap。失败只删除尚未发布的临时新文件，不碰旧数据库。

v0.1 不提供 migration runner。开发期间 Schema 不兼容变化直接重建 dev 数据库；在新架构进入 prod 前再冻结生产 Schema，并为后续版本引入 migration。

### 7.2 可执行 Schema 合同

[schema.sql](../../crates/wuji-storage/schema/schema.sql) 是 V01-2 的字段、类型、PK/FK、CHECK、索引、枚举和 PRAGMA 权威，不再由实现者根据“最小表说明”自行推导。它只用于创建全新数据库，不是 migration。V01-2 已将该文件落地为 `crates/wuji-storage/schema/schema.sql` 并同步本链接，仓库不保留第二份可漂移的 DDL。

Bootstrap 必须在同一临时文件中：

1. 执行该 SQL；
2. 插入唯一 `schema_meta`；
3. 插入 `settings_revisions(revision=0)`，digest 是 Core 内建默认设置规范 JSON 的 SHA-256；
4. 插入首个 `agent_runtime`；
5. 执行 `foreign_key_check`、`quick_check` 和最小事务回滚测试；
6. 执行 `wal_checkpoint(TRUNCATE)`，关闭所有句柄后再原子改名。

生产代码不得在运行中执行 `ALTER TABLE`、猜测缺失列或自动删除不兼容数据库。发现 `schema_version != 1` 时返回 `DB_SCHEMA_UNSUPPORTED`。

PRAGMA 按连接生效而非库级持久：`schema/schema.sql` 中的 `foreign_keys`、`busy_timeout` 只影响执行 bootstrap 的那一条连接。Writer 与只读 reader 每次打开连接都必须执行各自的连接 bootstrap：Writer 设置 `foreign_keys=ON`、`busy_timeout=750`、`synchronous=NORMAL`、`trusted_schema=OFF` 并验证 `journal_mode=WAL`；reader 按 7.3 设置只读参数。任何连接不得假设 PRAGMA 已被其他连接持久化。

### 7.3 Projection 一致性与幂等

Observation、Activity/Work 更新以及受影响读模型必须在同一个 Writer 事务提交。v0.1 禁止对 projection 做“收到消息就盲加 delta”：每次提交都从 source Segment/Work 对本次触及的 UTC hour/local date 桶重新聚合，然后以确定值 UPSERT；没有来源的旧桶行在同一事务删除。

重放同一个 `(runtime_id, capture_sequence)` 会命中唯一约束并返回已处理结果，不得再次累计。重算必须满足：

```text
每个 App/状态的 hourly 总和 = 同一 UTC 范围内可靠 Segment 交集总和
每天 daily_app_usage 总和 = 同一 local day 内可靠 Segment 交集总和
daily_work_metrics.active = 当日 Work Block active 交集总和
```

计数类字段同样从来源重算，不递增累计：

- `segment_count`（hourly/daily）= 与该桶有正时长交集的 Activity Segment 数；
- `daily_work_metrics.short_idle_duration_ms` = 落在某个 Work Block 区间内的 idle Activity Segment 与该 local date 的交集总和；被 `idle_break` 截断在 Work Block 之外的 idle 不计；
- `raw_app_switch_count` = 满足 6.6 定义且后一条 Observation 落在该 local date 的相邻对数；重算范围覆盖本次触及的 local date，跨日边界对只按后一条 Observation 的日期计一次；
- `data_gap_count` = `start_at_utc_ms` 落在该 local date 的 capture gap 数，不含 `sampling_transition`——它是正常切换的归属标记，不是数据质量问题；`sampling_transition` 同样不计入 Today/Timeline 的质量提示计数（见 8.4）。

Agent 持有唯一读写连接；Tauri reader 使用 SQLite URI `mode=ro`、`query_only=ON`，并验证 `schema_version=1`。reader 打开失败（数据库不存在、WAL 共享内存无法建立等）返回稳定 `DB_UNAVAILABLE`，不得以 `immutable=1` 降级强开可能存在未 checkpoint 数据的库；Agent 离线时 reader 仍可尝试只读历史（见 12.2 手工门禁），失败同样返回稳定错误。open Segment/Work 可以向前更新，closed 行不可再次打开。App Identity 的 first/last seen 只按 `MIN(existing, capture)` / `MAX(existing, capture)` 更新，避免时钟回拨破坏约束。不保存 PID、完整路径、原始标题、SID、用户名或机器名。

## 8. IPC 与 Tauri commands

### 8.1 Agent Pipe commands

```text
hello
status_get
capture_start
capture_pause
capture_resume
capture_stop
settings_reload
agent_shutdown
agent_shutdown_dev
```

约束：同用户 dev channel DACL、channel/mutex/Pipe 隔离、固定 JSON DTO、ULID request ID、64 KiB 上限和 3 秒 timeout。不提供任意方法转发、文件、SQL、shell、路径或控制文件 fallback。每个连接必须先完成 `hello`，协议或 channel 不一致立即断开。

v0.1 是 dev-only，不把长期规划中的 production binary 签名清单和逐帧 session capability 作为完成条件；因此 v0.1 不允许 production cutover，也不暴露 Clear/Export 等高风险命令。

`agent_shutdown` 是 Desktop“停止 Agent”流程使用的正式退出命令；Agent 必须先返回 `{ willExit: true }`，再进入 graceful shutdown。Desktop 不允许绕过 Capture 状态机直接调用它：完整顺序固定为 `capture_stop` 提交并确认边界 → `agent_shutdown` → 断开旧 Pipe → 等待 runtime 状态落为 `stopped`。`agent_shutdown_dev` 保留给 dev 工具、测试与 soak 脚本在同用户 DACL 内直连 Pipe 使用，不暴露为 Tauri command。关闭 Desktop 窗口不触发上述流程，Agent 继续按原状态运行。

### 8.2 协议 envelope、幂等与状态机

UTF-8、单行 JSON request/response 固定为：

```json
{
  "protocolVersion": 1,
  "requestId": "01J...ULID",
  "command": "capture_pause",
  "sentAtUtcMs": "1784300000000",
  "payload": {}
}
```

```json
{
  "protocolVersion": 1,
  "requestId": "01J...ULID",
  "agentVersion": "0.1.0",
  "ok": false,
  "result": null,
  "error": { "code": "CAPTURE_INVALID_STATE", "message": "当前状态不能暂停采集" }
}
```

`hello` payload 必须包含 `desktopVersion`、`protocolVersion=1`、`channel=rebuild-v01-dev`；响应包含 `agentVersion`、`protocolVersion`、`schemaVersion=1` 和当前状态。消息超过 64 KiB、出现 DTO 未声明字段、非 UTF-8 或非单个 JSON object 均拒绝。

Agent 保存最近 10 分钟、最多 1024 个 request ID 的 LRU；条目在执行前先标为 in-progress。相同 ID 和相同规范 payload hash 在执行中等待原任务，完成后返回原响应；相同 ID 但 payload 不同返回 `IPC_REQUEST_ID_REUSED`，不得并行执行两次。稳定错误码至少包括：

```text
IPC_PROTOCOL_UNSUPPORTED
IPC_CHANNEL_MISMATCH
IPC_INVALID_MESSAGE
IPC_PAYLOAD_TOO_LARGE
IPC_REQUEST_ID_REUSED
INVALID_ARGUMENT
CAPTURE_INVALID_STATE
AGENT_WRITER_DEGRADED
AGENT_WRITER_FAULTED
DB_UNAVAILABLE
DB_SCHEMA_UNSUPPORTED
TIME_ZONE_UNAVAILABLE
SETTINGS_CONFLICT
SETTINGS_INVALID
SETTINGS_SAVED_NOT_APPLIED
STARTUP_REGISTRY_FAILED
STARTUP_RECONCILIATION_REQUIRED
VERSION_INCOMPATIBLE
INTERNAL_SAFE_ERROR
```

Capture 转换合同：

| Command | `stopped` | `running` | `paused` |
|---|---|---|---|
| `capture_start` | → `running` | 幂等成功 | `CAPTURE_INVALID_STATE` |
| `capture_pause` | `CAPTURE_INVALID_STATE` | → `paused` | 幂等成功 |
| `capture_resume` | `CAPTURE_INVALID_STATE` | 幂等成功 | → `running` |
| `capture_stop` | 幂等成功 | → `stopped` | → `stopped` |

### 8.3 React 可调用的 Tauri commands

```text
agent_get_status
capture_start
capture_pause
capture_resume
agent_process_stop
activity_get_today
activity_get_timeline
activity_get_heatmap
settings_get
settings_update
settings_resync_login_startup
diagnostics_get_summary
```

React 不直接连接 Pipe、不查询 SQLite、不传入 channel/path，也不计算时长和聚合。`capture_start` 在 Tauri 内先确保固定位置的 Agent 在线，再发送 Capture start；因此 Agent 离线时同一“启动并记录”动作会重新拉起 Agent 并开始采集。`agent_process_stop` 在 Tauri 内执行 `capture_stop` 边界提交、`agent_shutdown`、旧 Pipe 断开和 runtime stopped 确认；它不是 `capture_stop` 的别名。`settings_resync_login_startup` 对应 9.2 的 Diagnostics 修复动作：Tauri 按当前 Settings 重放 Run Key 同步流程，返回最终一致状态，不接受任何参数。

v0.1 的实时刷新使用安全低频轮询（Today 约 5 秒、当前周 Heatmap 约 15 秒、Diagnostics 约 2 秒，页面隐藏时停止），不实现事件推送。Heatmap 历史周是静态视图，不启动轮询。轮询失败只影响实时性，不影响历史查询。

### 8.4 Query DTO 与 TypeScript 表示

Rust DTO 以 `serde` 类型为唯一来源，通过 `specta`/`tauri-specta` 在 build/check 阶段生成并校验 TypeScript 声明；禁止手写第二套同名 interface。生成文件进入源码管理，CI 发现 drift 即失败。

跨边界表示规则：

- 所有数据库 ID、UTC millisecond、duration millisecond 和计数器使用十进制字符串，例如 `"1784300000000"`；TypeScript 使用 branded `Int64String`，不得转为 `number` 做计算；
- ULID 和 opaque cursor 使用 string；local date 使用严格 `YYYY-MM-DD`；枚举使用小写稳定字符串；
- `activity_get_today` 无任意日期参数，以 DB reporting time zone 的当前 local date 查询；
- `activity_get_timeline` 输入 `{ localDate, cursor?, limit? }`，`limit` 默认 200、最大 500，只允许单个 local day；
- `activity_get_heatmap` 输入 `{ days?, weekOffset? }`，默认分别为 `7` 和 `0`；`days` 只允许 `1..=31`，`weekOffset` 只允许 `-520..=0`（当前周和最多 520 个历史周，不查询未来周）；查询范围终点为 reporting time zone 下真实 `today + weekOffset × 7 天`，只读取 hourly projection，不扫描 Observation；
- cursor 是由 `(start_at_utc_ms, item_kind, id)` 编码的 opaque base64url 字符串，`item_kind ∈ {segment, gap}`、`id` 为 `segment_id` 或 `gap_id`；排序固定为 `(start_at_utc_ms, item_kind, id)` 升序（segment 先于 gap），保证 Segment 与 Gap 混合序列的分页可重现；
- Today 返回 `activeDurationMs`、current/last app、longest Work Block、block count、Top Apps、raw switch count 和 quality summary；Timeline 返回 Segment/Gaps 两种 discriminated item 及 `nextCursor`，返回全部 gap kind（含 `sampling_transition`），UI 可以默认折叠该 kind 但不得改变其数据语义；Heatmap 稀疏返回时长大于 0 的小时格，强度等级由 Rust 在结果集内归一化为 `0..=4`，React 不得重算；
- Query 单次最多返回 500 items、Top Apps 最多 20；超限返回稳定参数错误，不静默截断日期范围。

最小 DTO 字段冻结如下；`Int64String` 均遵循上述十进制字符串规则：

```text
AppDto { appId, displayName }

AgentStatusDto {
  agentVersion, protocolVersion, schemaVersion,
  processState, captureState, writerState,
  runtimeId, heartbeatAtUtcMs?, lastObservationAtUtcMs?, lastWriteAtUtcMs?,
  captureQueueDepth, writerQueueDepth,
  droppedCaptureCount, droppedWriterCount, safeErrorCode?
}

TodayDto {
  localDate, reportingTimeZoneId, activeDurationMs,
  currentApp?, lastApp?, longestWorkBlockActiveMs,
  workBlockCount, rawAppSwitchCount,
  topApps[{ app, activeDurationMs }],
  quality{ isComplete, gapCount, droppedCount }
}

TimelineSegmentDto {
  kind: "segment", segmentId, app, activityState,
  startAtUtcMs, endAtUtcMs, durationMs, status
}

TimelineGapDto {
  kind: "gap", gapId, gapKind,
  startAtUtcMs, endAtUtcMs?, status, eventCount
}

TimelinePageDto {
  localDate, reportingTimeZoneId,
  items: (TimelineSegmentDto | TimelineGapDto)[], nextCursor?
}

HeatmapCellDto {
  localDate, localHour,
  activeDurationMs, idleDurationMs, unknownDurationMs,
  intensityLevel
}

HeatmapDto {
  today, rangeEndLocalDate, reportingTimeZoneId,
  days, cells: HeatmapCellDto[]
}

SettingsDto {
  schemaVersion: 1, revision, persisted, appliedRevision,
  samplingIntervalSeconds, idleThresholdSeconds,
  workBreakIdleSeconds, excludedProcessNames[], startCaptureOnLogin
}
```

字段口径：`currentApp` 取当前 open Segment 的 App（无 open Segment 时为空），`lastApp` 取最后一个 closed Segment 的 App；`longestWorkBlockActiveMs`、`workBlockCount`、`rawAppSwitchCount`、`topApps` 一律来自 daily 读模型；`quality.gapCount` 与 `daily_work_metrics.data_gap_count` 同口径（不含 `sampling_transition`，见 7.3），`quality.isComplete` 定义为该 local date 无非 `sampling_transition` gap 且 `droppedCount` 为 0。

Pipe command payload/result 也固定：`status_get` 和四个 Capture command 使用空 payload 并返回 `AgentStatusDto`；`settings_reload` 使用 `{ savedRevision, contentDigest }` 并返回 `{ appliedRevision }`；`agent_shutdown` 与 `agent_shutdown_dev` 均使用空 payload，并在关闭 Pipe 前返回 `{ willExit: true }`。

## 9. Settings v0.1

Settings JSON 由 Tauri 唯一写入，使用临时文件 + flush + 原子替换。字段只包含：

```text
schemaVersion
revision
samplingIntervalSeconds
idleThresholdSeconds
workBreakIdleSeconds
excludedProcessNames[]
startCaptureOnLogin
```

`schemaVersion` 是 JSON number `1`；`revision` 是十进制 string。其余数值设置是受范围约束的 JSON number，进程名数组是规范化后的小写 string。

Agent reload 必须整份验证；失败时旧设置全部继续生效。排除 App 使用规范化进程名匹配，命中后不写 Observation，只写不含 App 信息的 `PrivacyExcluded` gap。

v0.1 不支持标题规则、路径规则、正则表达式、手工 Context 规则或报告时区修改。

### 9.1 默认值、CAS 与应用状态

Core 提供唯一内建默认值。Settings 文件不存在时，Agent 使用 revision 0 的内建默认值但不创建文件；Tauri `settings_get` 返回 `{ revision: "0", persisted: false, appliedRevision: "0" }`。首次保存产生 revision 1。

`settings_update` 必须携带 `expectedRevision`。Tauri 在进程内独占设置锁下比较当前 revision，冲突返回 `SETTINGS_CONFLICT`；成功 revision 恰好加一。Tauri 先用同一 Core validator 验证，再以 temporary file + flush + atomic replace 保存。Agent 完整 reload 成功并由 Writer 提交 `settings_revisions` 后，`appliedRevision` 才前进。

JSON 已保存但 Agent 离线或 reload 失败时不回滚普通设置：返回 `SETTINGS_SAVED_NOT_APPLIED`，UI 同时显示 saved/applied revision，Agent 保持上一 revision，后续 heartbeat/reload 自动重试。Settings 文件解析失败时 Agent 同样保留最后已应用值。文件被外部删除、revision 低于已应用值、或同 revision 但 digest 不一致时，Agent 不应用该文件：保持最后已应用 revision，按安全诊断上报，等待下一次合法保存或人工处理，不自动回滚到旧 revision。

### 9.2 登录启动的一致性

`startCaptureOnLogin=true` 的精确定义是：Run Key 启动固定 Agent 路径，并传入 `--channel rebuild-v01-dev --capture-on-start`；它不启动 Desktop。设为 false 只删除 Run Key，不停止当前 Agent 或当前 Capture。

修改该字段时，Tauri 在替换 Settings 前先修改 Run Key；Settings 替换失败则尽力恢复旧 Run Key。Run Key 修改失败时不替换 Settings，返回 `STARTUP_REGISTRY_FAILED`。补偿也失败时返回安全的 partial-state 错误，Diagnostics 提供“按当前 Settings 重新同步登录启动”操作；不得声称保存完全成功。

### 9.3 Agent 脱离、版本与打包

Desktop 只从自身安装目录的固定位置 `<desktop-exe-dir>\Agent\wuji-rebuild-agent-v01.exe` 启动 Agent。Windows 启动使用 `CreateProcessW`，设置 `DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP | CREATE_NO_WINDOW`，不继承 Desktop handles，不加入会随 Desktop 关闭的 Job；启动后通过 `hello` 验证固定 channel/version，而不是把 child handle 当作运行状态。React 调用 `capture_start` 时，由 Tauri 在内部确保 Agent 在线；重新拉起的新 Agent 不传 `--capture-on-start`、初始为 `stopped`，随后才发送 `capture_start`。普通 Desktop 启动不自动拉起 Agent；打包自动验收可以在固定 `rebuild-v01-test-*` channel 设置 package-smoke 环境开关，调用同一 `AgentController::ensure_running` 验证安装目录链路，该开关对正常 channel 无效。只有 Run Key 登录启动明确传入 `--capture-on-start`。Desktop 关闭不停止 Agent；用户显式调用 `agent_process_stop` 才执行 8.1/8.3 的边界提交与 graceful shutdown。v0.1 不做 Agent crash 自动重启。

Desktop 与 Agent 的 protocol major、Schema version 任一不兼容时，禁止运行控制并显示 `VERSION_INCOMPATIBLE`；只读历史仅在 Desktop 明确支持 `schema_version=1` 时开放。不得自动启动另一个版本覆盖正在运行的 Agent。

V01-8 必须把 Tauri `bundle.active` 改为 `true`，将 Agent 放入固定 `Agent` 目录，并生成包含 Desktop/Agent version 与 SHA-256 的 dev package manifest。v0.1 不用该 hash 充当生产身份认证，但安装/验收会校验缺失、错版和意外 `.NET`/Bridge 资产。当前 `bundle.active=false` 只是 V01-1–V01-7 开发态，不满足 V01-8。

## 10. UI v0.1

### 10.1 Today

- 今日 active duration；
- 当前/最后 App；
- 最长 Work Block；
- Work Block 数；
- Top Apps；
- 今日 App switch 数；
- 数据不完整时显示 gap/drop 提示。

### 10.2 Timeline

- 按时间显示 App Activity Segment；
- 区分 Active/Idle/Unknown；
- 显示 gap、Pause 和 Stop 边界；
- 不显示窗口标题、Context、Interruption 或 Focus。

### 10.3 Heatmap

- 固定日期轴的 `days × 24` 小时网格，缺失的稀疏格补零展示；
- 支持本周至前 520 周的周导航，非法、未来或越界 URL 参数规范化回本周；
- `today` 始终表示 DB reporting time zone 下真实今天，`rangeEndLocalDate` 表示查询范围终点；历史周不得伪装“今天”或“现在”；
- 当前周约 15 秒低频轮询，历史周不轮询；切周请求使用目标与 generation 身份隔离，迟到响应不得串周；
- 格子点击、Enter 或 Space 跳转 Timeline 对应日期与小时，键盘焦点遵循 roving tabindex。

### 10.4 Settings

- v0.1 六个设置字段；
- 保存成功与 Agent 已应用分开显示；
- 字段错误使用中文安全提示。

### 10.5 Diagnostics

- Agent 连接/采集状态；
- 最后 heartbeat、capture、write；
- queue depth、drop count、safe error；
- 高级信息默认折叠且路径脱敏。

离线判定：`status_get` 失败且数据库 `heartbeat_at_utc_ms` 距今超过 `max(3 × heartbeat 间隔, 15 秒)` 时，显示“无法连接 Agent，最后记录于 …”；仅有 SQLite heartbeat 不得显示实时 Running（口径沿用长期合同 06 §10）。

五个页面统一使用 `Loading | Empty | Ready | Error`。普通 UI 不出现“任务切换”“上下文”“专注”或尚未实现的长期指标。

## 11. 实施顺序

本文件保持 Draft，避免把尚未评审的细节伪装成已接受合同。V01-1 可以立即开始；V01-2 开始前必须接受第 7 节和 `schema/schema.sql`，V01-3–V01-5 前必须接受第 5–6、8 节，V01-6–V01-8 前必须接受第 4、8–9 节。接受只冻结 v0.1 合同，不提升长期 01–08 或 ADR-002 状态。

| 阶段 | 工作 | 退出条件 |
|---|---|---|
| V01-1 Workspace | 建立 Rust workspace、Core/Error/Settings/DTO 和固定 runtime names | `cargo test`；Core 无 Tauri/Win32/SQLite 依赖；生成 TS DTO 无 drift |
| V01-2 Storage | 按 `schema/schema.sql` 落地 bootstrap、Writer/Query、临时库测试 | Schema 原样执行；FK/只读/事务/重算幂等测试通过 |
| V01-3 Capture | Win32 foreground/process/idle、隐私过滤、bounded queue | 真实 Windows 捕获和卡死/退出进程测试通过 |
| V01-4 Activity | 精确 Activity/Work 状态机、gap 和小时/日重算 | switch/idle/crash/DST 固定输入黄金样本守恒通过 |
| V01-5 Agent | 双 lane Writer、CommandServer、heartbeat、单实例和恢复 | drop epoch、disk fault、Desktop exit、Agent restart、Capture 状态机通过 |
| V01-6 Desktop | Tauri Query/IPC client、CAS Settings、detached Agent、新 Desktop 不携带 bridge 代码与 BridgeSupervisor（旧树按 ADR-003 在冻结提交保留） | handshake/DTO/版本错误；React→Tauri→Rust Agent/SQLite 端到端通过 |
| V01-7 UI | Today、Timeline、Heatmap、Settings、Diagnostics | 五页四态、键盘和基础主题验收通过；Heatmap 周边界、稀疏日期轴与请求防串周测试通过 |
| V01-8 Dev package | 启用 bundle、固定 Agent 布局、dev manifest、soak、旧系统隔离 | 包内无 Bridge/.NET；8 小时 soak；旧库 checksum 不变 |

替代链路验证期间不得先删除旧 C#/WPF/Bridge。该约束在 2026-07-22 dev package 与 soak 已形成历史通过证据后满足；2026-07-29 产品负责人通过 ADR-003 批准从当前工作树移除旧源码。此次移除不关闭当前工作区的 V01-8 重验收，也不表示旧安装、旧数据或生产系统已经退役。

## 12. v0.1 验收门禁

### 12.1 自动门禁

- Rust：`fmt`、`clippy -D warnings`、workspace tests；
- React：typecheck、ESLint、Vitest；
- SQLite：`schema/schema.sql` 原样执行、空库 bootstrap、FK/CHECK、事务回滚、只读 reader、并发 WAL、触及桶重算幂等；
- 领域：零时长首样本、采样切换不归属、Idle pending、Work break、app switch、restart、clock change、UTC/local/DST 固定样本；
- 守恒交叉验证：同一 fixture 上 Today（daily 读模型）与 Timeline（Segment 交集）的时长/计数总和一致；黄金样本期望值必须先经人工评审再固化为断言，不得按实现结果反写；
- 隐私：DB、WAL、log、DTO 不出现测试标题、完整路径或排除 App 名；
- Writer：两条 queue 满载、continuity epoch、control 优先、busy/disk-full/corruption/checkpoint 故障注入；
- IPC：hello/version、非法 command、超长消息、timeout、不同 channel、重复/冲突 request ID 和所有 Capture 状态转换；
- Settings：revision CAS、saved-not-applied、Agent reload、Run Key 失败与补偿；
- 生命周期：Desktop exit、detached Agent、Agent restart/open-row recovery、CaptureStop、重复启动和版本不兼容；
- 打包：固定命名空间和 Agent 布局正确；Bridge sidecar、C# runtime 和旧合同不在 rebuild dev 包中。

### 12.2 手工门禁

- Windows 前台切换、锁屏、sleep/resume 和进程快速退出；
- Today/Timeline 与受控 30 分钟脚本记录一致；
- 960×640、1280×800，100%/150%/200% DPI；
- Light/Dark、键盘导航、焦点可见；
- Agent 离线时可以读取已有历史并显示安全状态；
- 8 小时持续运行无 crash、死锁或明显无界内存/WAL 增长。

### 12.3 一票否决

- 写入或修改旧数据库；
- React 获得 SQL、路径、Pipe、shell 或原始标题；
- 数据库或日志出现原始标题、完整路径或排除 App；
- 同时存在两个 v0.1 Writer；
- Desktop 退出导致 Agent 意外退出；
- gap/drop/crash 时按采样周期补算工作时长；
- rebuild dev 包仍依赖 `.NET Bridge`。

## 13. 回滚与数据策略

v0.1 不需要复杂数据库回滚：

```text
停止 rebuild Agent
→ 退出 rebuild Desktop
→ 从 ADR-003 冻结提交恢复并启动原 WPF/C# 系统，或使用仍存在的旧安装
```

两套系统不共享数据库，因此回滚不转换数据，也不会让旧 Agent 打开新库。恢复旧源码必须在独立 clone/worktree 中进行，不得覆盖当前脏工作树。v0.1 dev 数据可以保留作诊断或在用户确认后删除；不得自动删除旧系统数据。

如果未来决定导入旧历史，应新增独立版本计划和离线 importer；它不是 v0.1 的补丁任务。

## 14. v0.1 完成后的下一步

完成 v0.1 后再按实际产品需要选择，而不是一次全部启动：

1. v0.2：规则 Context Segment 和 daily context usage；
2. v0.3：Context Switch/Interruption 与解释；
3. v0.4：生产 Schema、签名认证、安装升级和正式 migration；
4. 后续：历史重建、多世代发布、Snapshot/Lease/GC；只有真实需求证明必要时才实现。

旧安装停产、旧数据退役和生产 G-RETIRE 仍必须等到生产版本达到所需功能、数据和稳定性门禁。ADR-003 只批准旧源码从当前工作树提前移除，v0.1 完成不等于上述生产退役已经完成。

## 15. 附录：v0.1 与长期模型的语义偏离登记

v0.1 对长期文档（01–06）做了以下有意的语义简化。本表是实现与评审的唯一对齐点：实现 v0.1 时不得从长期文档反推这些行为；未来向长期模型回归时按“回归点”逐条评审。

| 主题 | 长期模型（来源） | v0.1 选择（本文位置） | 理由 | 未来回归点 |
|---|---|---|---|---|
| Activity 去抖 | 候选确认需 `confirm_duration + min_samples` 双门槛，边界回溯到第一候选，tail 重写（02 §6.2、03 §4） | App/状态变化立即切段，候选间隔两侧均不归属（§6.2、§6.5） | 去掉去抖与 tail 重写机制 | v0.2+ 评审抖动噪声后再引入 |
| Idle 起点 | 由 `capturedAtUtc - reportedIdleDuration` 反推并裁剪（03 §4） | 首条达阈值 Observation 时刻，不反推（§6.5） | 保持 open Segment 只向前更新 | 评估 active 宽限期后可接受性 |
| open 行持久化 | 持久化层不存在 open 行，开放尾部只在 Writer 内存/tail state（02 §6.3） | open Segment/Work/gap 持久化，启动事务关闭遗留行（§6.7、Schema） | 避免引入 tail delete/rebuild 机制 | 长期模型落地时统一 |
| Raw App Switch 粒度 | 相邻有效 Activity Segment（01 §3.9） | 相邻 Observation（§6.6） | 无去抖时二者等价，Observation 粒度更简单 | 引入去抖时回到 Segment 粒度 |
| Observation gap cap | 候选 60 s（01 §5） | `max(3 × sampling interval, 15 秒)`（§5.1） | dev 阶段收紧归因上限 | 黄金样本校准后统一 |
| Work break idle | 候选 600 s（01 §5） | 默认 300 s（§5.1） | dev 阶段取值 | 产品接受后统一 |
| Writer lane | Control/Capture/Maintenance/Exclusive 四 lane（05 §6） | data/control 双 lane，checkpoint 并入 control，无 Exclusive（§5.2） | v0.1 无 Clear/migration/prune | 引入破坏性命令时恢复 |
| agent_runtime 形态 | 单行最后已知快照（04 §9.1） | 每 runtime 一行（Schema） | Observation/Segment 需要 FK 目标 | 长期模型落地时统一 |
| busy_timeout | 5000 ms（04 §3） | 750 ms + 有限重试（§5.2） | 卡进 IPC 3 秒 timeout | 长期模型落地时统一 |
| Settings 生效 | Effectivity Interval + Profile/Generation 投影（02 §14、05 §12） | 只影响未来数据，无 Effectivity 表（§5.1、§9） | v0.1 无历史重建 | 长期模型落地时统一 |

## 16. 附录：审核整改增补合同（2026-07-22）

本附录是 v0.1 合同的一部分，源自《Rebuild-v0.1-代码与验收审核报告》R02–R10 的整改。与上文冲突时以本附录为准。

### 16.1 Settings last-known-good 持久化与启动对账

`settings_revisions` 增加 `content_json`（规范 JSON 全文）。每次成功应用 revision 时与 digest 同事务写入，作为跨进程重启的 last-known-good。

Agent 启动对账（`reconcile_startup_settings`）：

- 文件缺失且 DB 最大已应用 revision 为 0：允许 revision 0 内建默认值（全新库）。
- 文件缺失且最大已应用 revision > 0：从 DB `content_json` 恢复并上报 `SETTINGS_INVALID` 诊断；不得静默回 revision 0。
- 文件损坏/验证失败：同上从 DB 恢复，上报 `SETTINGS_INVALID`。
- 文件 revision 低于已应用值，或同 revision 但 digest 冲突：拒绝该文件，从 DB 恢复，上报 `SETTINGS_CONFLICT`。
- DB 中 last-known-good 内容自身不可恢复（解析/验证/digest 任一失败）：禁止进入 Running，`capture_start` 返回 `SETTINGS_INVALID`，Agent 保持 IPC 在线等待人工处理。

revision 单调性在三个位置强制：启动对账、IPC `settings_reload`（低于 `appliedRevision` 拒绝）、引擎 `apply_settings`（低于当前内存 revision 拒绝）。后台 reconciler 每 2 秒检查文件，仅在文件 revision 严格大于已应用值时经 control lane 重新应用（saved-not-applied 的自动重试）。

Desktop 侧：`settings_get`/`settings_update` 遇到损坏文件返回 `SETTINGS_INVALID`，不得伪装成默认值；`settings_resync_login_startup` 返回的 `appliedRevision` 来自数据库最大已应用 revision，不得误报为 saved revision。

### 16.2 生命周期与 Settings 的 sequence watermark

`ContinuityState.latest_sequence` 记录采集循环最近一次分配的 Capture Sequence。CommandServer 接受 Pause/Stop 时：先冻结 capture watch，再取 `watermark = latest_sequence`，随 Lifecycle 控制进入 control lane。Writer 先把 data lane 排到有 watermark 为止（seq ≤ watermark 的 backlog 全部按边界前提交；迟到样本按 09 §6.7 作为边界后首条 Observation 关闭 gap），才应用边界。处理侧失联时 Writer 最多等待 1.5 秒，记录安全诊断后保守放行。

`settings_reload` 与 reconciler 同样携带 watermark：seq ≤ watermark 的 Observation 保持旧 settings revision，之后采集的样本使用新 revision（“只影响未来数据”的可执行定义）。

### 16.3 IPC 副作用与严格协议

- 副作用命令（capture 状态转换、settings_reload、shutdown）在独立任务中执行；3 秒 timeout 只结束本次等待，不取消已接受命令。request cache 只在任务真正完成后写入 Completed；timeout 响应不落 cache。相同 ID 重试等待原任务结果；Desktop 在 timeout/断线后保存并用同一 request ID 重试一次。
- 严格校验：消息必须是合法 UTF-8（拒绝替换解码）；`requestId` 必须是 ULID（26 位 Crockford Base32）；`sentAtUtcMs` 必须是十进制字符串；hello 校验 `desktopVersion` 非空、envelope 与 payload 的 `protocolVersion` 均为 1、channel 匹配；逐命令强类型 payload 并 `deny_unknown_fields`（无 payload 命令只接受缺省/null/空对象）。
- Agent 任务内禁止阻塞 runtime：session/power 事件泵的 `std::sync::mpsc::Receiver::recv` 经专用桥接线程转发进 tokio 通道（current_thread runtime 上任何阻塞调用都会冻结全部任务）。

### 16.4 checkpoint busy 的判定

`PRAGMA wal_checkpoint(TRUNCATE)` 的 busy 通过结果行第一列返回，不以 SQL 错误出现。Writer 必须读取结果行：busy ≠ 0 视为 `AGENT_WRITER_DEGRADED` 安全诊断，下周期重试；不得把 execute_batch 的 Ok 当作 checkpoint 成功。

### 16.5 soak 可执行判据

`scripts/soak.py` 的判据（脚本内 CRITERIA 常量为实现对齐点）：

- 无 crash：进程全程存活；结束时先 hello 再 `agent_shutdown_dev`，校验两次响应 ok 且 `willExit=true`；进程在 20 秒内以 exit code 0 退出；任何强杀判失败。
- RSS 有界：增长 < 64 MiB 且 < 50%。
- WAL 趋势收敛：结束时 WAL ≤ 4 MiB，且末段（后 1/3 采样）均值 ≤ 前段均值 ×2 + 1 MiB。
- 心跳严格单调推进；≥ 1 分钟的 soak 至少 2 个有效心跳采样。
- writer 全程（任一采样点）不得为 faulted。
- `PRAGMA quick_check = ok`。
- 旧库隔离：prod/dev 两个候选各自记录存在性；存在的旧库 checksum 前后不变；不存在时报告 `not_verifiable_no_old_db_present`，不得声称 checksum 已验证。
- 证据脱敏：报告不含用户名与本机绝对路径，包含 git commit、命令、OS、二进制 SHA-256、Cargo.lock SHA-256、采样摘要与判据文本，可直接提交到 evidence 目录。

### 16.6 Schema 增补

`settings_revisions` 增加 `content_json TEXT NOT NULL CHECK (length(content_json) > 2)`（§16.1）。v0.1 是全新数据库，直接修改内嵌 DDL；不提供旧 dev 库迁移。
