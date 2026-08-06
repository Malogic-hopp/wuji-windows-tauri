# Rebuild v0.1 代码与验收审核报告

状态：审核完成，存在阻断验收问题  
审核日期：2026-07-22  
审核范围：`origin/main..main` 的 12 个本地提交（`b12349f`–`b42d2e2`）  
审核基线：[09-Tauri-Rust-Rebuild-v0.1实施基线.md](./09-Tauri-Rust-Rebuild-v0.1实施基线.md)  
实施状态参考：[migration-status.md](./migration-status.md)

## 1. 审核结论

Rebuild v0.1 已经形成可运行的 React → Tauri Rust Host → Rust Agent → SQLite 端到端开发链路，Bridge-free workspace、Agent、四个页面、NSIS 打包和 8 小时运行数据均已存在。Rust workspace 的 88 项测试、`fmt`、`clippy -D warnings`，以及 React 的 typecheck、lint、20 项 Vitest 和 production build 在本次审核中均通过。

但是，当前证据不足以支持“V01-1–V01-8 全部交付并验收通过”的结论。审核发现：

- 1 项验收状态阻断：实施基线仍为 Draft，必要手工门禁尚未完成，`migration-status.md` 内部状态互相矛盾；
- 5 组高优先级实现或验收缺陷：Today 聚合、生命周期边界、Settings 恢复与生效、IPC 超时语义、soak 证据有效性；
- 多项自动门禁缺少与基线逐项对应的测试证据；
- 若干 DTO、时区、诊断和无障碍问题需要在最终关闭 V01-8 前处理或明确记录。

因此，本报告建议将当前状态调整为：

> Rebuild v0.1 端到端工程实现已完成；V01-8 验收重新打开，待阻断问题修复、自动门禁补齐、手工矩阵完成并形成可审计证据后关闭。

本结论不否定现有工程成果，也不要求回退 Rebuild 链路。旧 WPF/C#/Bridge 系统仍应按基线保留为独立回滚入口。

## 2. 审核方法与范围

本次审核采用以下方式：

1. 对照 09 实施基线的成功定义、运行合同、算法语义、IPC、Settings、UI 与验收门禁；
2. 检查 `migration-status.md` 的结论、能力矩阵、阶段状态和测试记录是否一致；
3. 审查 12 个提交引入的 Rust workspace、Agent、Storage、Win32、Tauri、React、打包与 soak 脚本；
4. 搜索未实现分支、panic/unwrap、敏感字段、生命周期事件、故障注入与门禁测试；
5. 实跑 Rust/React 自动检查；
6. 只读检查本地 dev package manifest 与 soak report。

本次审核未执行：

- 重新构建 NSIS installer；
- 重新执行 8 小时 soak；
- 锁屏、休眠、DPI、High Contrast、键盘和 30 分钟受控对照；
- 旧 .NET 解决方案完整 restore/build/test。

## 3. 验证结果

| 验证项 | 本次结果 | 说明 |
|---|---|---|
| `cargo fmt --all -- --check` | Passed | 无格式漂移 |
| `cargo clippy --workspace --all-targets -- -D warnings` | Passed | clippy 零警告 |
| `cargo test --workspace` | Passed | 88 项通过；Agent/Desktop 集成测试需允许隔离 `%LOCALAPPDATA%`、Named Pipe 与测试 Run Key |
| `pnpm typecheck` | Passed | TypeScript 检查通过 |
| `pnpm lint` | Passed | ESLint 零警告 |
| `pnpm test` | Passed | 5 个文件、20 项 Vitest 通过 |
| `pnpm build` | Passed | Vite production build 成功 |
| 旧 `.NET` build/test | NotRun | `--no-restore` 因缺少多个 `project.assets.json` 无法执行；未将其判定为源码失败 |
| 8 小时 soak | NotRun | 只读检查已有本地报告和脚本；未重新执行 |
| 手工 UI/系统矩阵 | NotRun | 仍需实际执行并保存证据 |

首次在文件沙箱内运行 Agent e2e 时，4 项子进程测试因无法在 `%LOCALAPPDATA%` 创建测试数据库而超时；允许测试使用隔离的用户数据目录后，4 项全部通过。该现象属于执行环境限制，不作为代码缺陷。

## 4. 阻断验收问题

### R01：完成结论与基线门禁、状态矩阵冲突

严重度：Blocker  
影响范围：V01-8 关闭结论、验收可信度、后续版本基线

09 §2 规定所有成功条件和验收项同时满足才算完成，09 §11 又规定 V01-2 以后必须先接受对应合同；但 09 当前仍标记为 Draft。09 §12.2 的以下手工门禁尚未完成：

- 真实锁屏、sleep/resume 和快速退出进程；
- Today/Timeline 与 30 分钟受控脚本对照；
- 960×640、1280×800 和 100%/150%/200% DPI；
- Light/Dark、键盘导航和焦点可见；
- Agent 离线读取历史；
- 其他明确保留的 High Contrast/读屏检查。

`migration-status.md` 第 2、4、5、6、8 节同时存在新旧两套状态。例如总述称八阶段全部完成，但矩阵仍写 V01-3 以后未开始、Rust Agent 未接入 Desktop、dev cutover 仍依赖 Bridge、M1 Rust Workspace 未开始。这使该文件无法可靠回答“仓库现在实际做到哪里”。

修改建议：

1. 把总状态改为“实现完成，验收待关闭”；
2. 为 V01-1–V01-8 建立独立阶段表，分别记录 `Implemented`、`Verified`、`Pending`；
3. 在 09 或独立接受记录中写明合同接受范围、日期、责任角色和适用 commit；
4. 统一更新能力矩阵和长期 M0–M11 状态，明确区分 Rebuild v0.1 dev 链路与长期 production 路线；
5. 在手工门禁与本报告阻断项关闭前，不再使用“验收全部通过”。

## 5. 高优先级实现问题

### R02：Today 活跃时长在应用超过 20 个时少算

严重度：High  
证据：`crates/wuji-storage/src/reader.rs`

`Reader::today` 查询 `daily_app_usage` 时使用 `LIMIT 20`，随后只对这 20 行累加 `total_active`。当天出现 21 个或更多应用时，`TodayDto.activeDurationMs` 会漏掉 Top 20 之外的活跃时长。函数已经读取 `daily_work_metrics.active_duration_ms`，但将其保存为 `_work_active` 后没有使用。

同一函数的 `dropped_count_of_date` 只按 queue-drop gap 行数加一，没有累加合并 gap 的 `event_count`；连续多次 drop 合并为一行后会被少计。

修改建议：

- `activeDurationMs` 使用 `daily_work_metrics.active_duration_ms`，或对全部 `daily_app_usage` 单独执行 `SUM`；
- Top Apps 查询继续保留 `LIMIT 20`；
- drop count 改为按日期范围 `SUM(event_count)`；
- 增加 21 个应用、连续多次 drop、跨午夜和 DST 日期的回归测试；
- 在守恒测试中加入 `Today.activeDurationMs == 当日 Segment active 交集 == daily active`。

### R03：真实 Sleep/Lock 未接入，Pause/Stop 存在时间水位竞争

严重度：High  
证据：`apps/agent/src/activity.rs`、`writer_task.rs`、`command_server.rs`

ActivityEngine 定义并测试了 `SystemSleep` 和 `SessionLocked`，但 Agent 主流程和 `wuji-windows` 没有注册 Windows power/session notification，也没有把真实事件送入 control lane。当前代码无法证明真实锁屏/休眠门禁可通过。

Pause/Stop 的执行顺序也存在竞争：Writer 收到生命周期 control 后先持续 `try_recv` 排空 data lane，Capture 状态直到事务完成后才更新。排空期间 Capture 仍可能生成新样本，导致晚于控制命令时间的 Observation 先写入，再以更早的时间写 Pause/Stop 边界。

修改建议：

- 增加 Windows session/power event pump，并映射 Lock/Unlock、Sleep/Resume；
- 控制命令被接受时先冻结 Capture，生成固定 sequence/watermark；
- Writer 只排空至该 watermark，再提交生命周期边界；
- 对 Capture 状态机命令进行串行化，避免不同 request ID 的并发状态转换；
- 增加满队列 Pause/Stop、真实或可注入 Sleep/Lock、事件与采样同刻竞争测试。

### R04：Settings 恢复、revision 单调性和生效边界不完整

严重度：High  
证据：`apps/agent/src/main.rs`、`settings_store.rs`、`activity.rs`、`apps/desktop/src-tauri/src/settings_service.rs`

当前 Agent 在 Settings 文件缺失、不可读、JSON 损坏或验证失败时使用 revision 0 内建默认值。若数据库此前已应用更高 revision，Agent 重启后仍可能用 revision 0 写新 Observation。这与“保留最后已应用值、拒绝文件删除/降级/同 revision digest 冲突”的合同不一致。

Desktop 的 `load_current` 也会把损坏文件伪装成默认设置。`settings_resync_login_startup` 仅重放 Run Key，却把返回 DTO 的 `appliedRevision` 直接写成当前保存 revision，可能误报 Agent 已应用。

代码注释和 UI 声称 saved-not-applied 会自动重试，但当前没有后台重试任务或重连对账。ProcessorOutput 也没有携带实际 settings revision；control lane 先应用新 Settings 后，旧 backlog 可能被写成新 revision。

修改建议：

- 持久化 last-known-good Settings 完整内容、revision 和 digest；
- 启动时与数据库最大 applied revision 对账，拒绝降级和 digest 冲突；
- 无法恢复时停止 Capture 并返回 `SETTINGS_INVALID`，不要静默切到默认值；
- 在 RawCapture/ProcessorOutput 或明确的 Writer watermark 中保留实际 settings revision；
- 增加后台 reload/reconciliation；
- 修正 `settings_resync_login_startup` 的 appliedRevision 来源；
- 增加文件删除、损坏、revision 降级、同 revision digest 冲突、Agent 离线后重连和 backlog effectivity 测试。

### R05：IPC timeout 会取消副作用，严格协议校验未落实

严重度：High  
证据：`apps/agent/src/command_server.rs`、`apps/desktop/src-tauri/src/ipc.rs`

CommandServer 直接以 `tokio::time::timeout` 包裹 `dispatch`。timeout 会取消 future。对于 Pause/Stop/Settings 等副作用，Writer 可能已经提交事务，但 future 尚未更新共享内存或返回结果，最终形成数据库状态、Capture watch 和 request cache 不一致。

协议实现还有以下偏差：

- 非 UTF-8 使用 `String::from_utf8_lossy` 替换解码，而不是拒绝；
- hello 只严格校验 envelope protocol 和 channel，没有完整验证 `desktopVersion` 与 payload protocol；
- command payload 使用通用 `serde_json::Value`，未逐命令拒绝未知字段；
- request ID 未验证为 ULID，`sentAtUtcMs` 未验证为十进制字符串；
- Desktop 每次调用创建新 request ID，没有副作用 timeout 后复用原 ID 的恢复路径。

修改建议：

- 将副作用作为独立任务执行，timeout 只结束等待，不取消已接受命令；
- request cache 在任务真正完成后写入最终响应；
- Desktop 保存 request ID，timeout/断线后用同一 ID 重连查询或重试；
- 使用逐命令强类型 payload 和 `deny_unknown_fields`；
- 使用严格 `String::from_utf8`，并验证 ULID、时间戳和 hello 所有字段；
- 增加慢 Writer、timeout、断线重连、重复/冲突 ID、非法 UTF-8 和未知 payload 字段测试。

### R06：8 小时 soak 报告不能证明其声明的全部结论

严重度：High  
证据：`scripts/soak.py`、本地 `dist/soak-report.json`

已有报告记录了 28821 秒、240 个采样点、RSS 约 14.3→16.3 MB、WAL 峰值约 1.1 MB、7904 条 Observation、drop=0 和 `quick_check=ok`。这些数据能够证明 Agent 在该时段持续运行并写入。

但脚本存在以下证据缺口：

1. `ipc_shutdown` 未先发送必须的 hello，而是直接发送 `agent_shutdown_dev`；服务端会断开连接，脚本仍可能返回成功，随后超时强杀 Agent；
2. 报告未校验优雅退出响应、退出码和是否发生强杀；
3. WAL 只记录峰值，没有趋势或上限失败条件；
4. heartbeat 只检查最后值非空，没有检查持续推进；
5. writer 只检查最后状态，不检查运行期间是否曾 faulted；
6. 两个旧数据库都不存在时，空 checksum 集合前后相等也会判稳定；
7. 安装版 Desktop 启动并拉起安装版 Agent 不在 `build_dev_package.py` 的自动流程中；
8. manifest 与 soak report 位于被 `.gitignore` 排除的 `dist/`，仓库内没有可复核证据；
9. 当前报告包含本机绝对路径，不能直接提交。

修改建议：

- 按正式 hello → shutdown 请求顺序调用 IPC，并解析 `ok/result`；
- 要求 Agent 在时限内以 exit code 0 退出，强杀必须判失败；
- 检查 heartbeat 单调推进、所有采样的 writer 状态和 WAL 时间序列；
- 明确 RSS/WAL 预算或趋势判据；
- 旧库隔离验证必须记录两个候选库是否存在、各自脱敏标识与前后 digest；缺失不得被表述为“两库 checksum 不变”；
- 自动启动安装目录内的 Desktop，验证其拉起安装目录内的 Agent；
- 生成脱敏、可提交的证据包，包含 commit、命令、OS、二进制和 installer hash、锁文件 hash、采样摘要和失败判据；
- 修复后重新执行完整 8 小时 soak，再关闭 V01-8。

## 6. 中优先级问题

### R07：Int64String 不是 branded type，UI 违反整数边界合同

严重度：Medium

生成的 TypeScript 中 `Int64String` 实际为 `type Int64String = string`，并不具备品牌隔离。React 的 `format.ts` 又通过 `Number(msText)` 计算时长和时间，与 09 §8.4 的“不转为 number 做计算”冲突。

Rust crate 和 React 目录各保存一份 `wuji-core.ts`，当前 drift 测试只检查 crate 内副本，没有保证 React 使用的文件同步。

修改建议：

- 生成真正的 opaque/branded `Int64String`；
- 时长计算使用 `BigInt`；时间戳转 `Date` 前执行明确的安全范围校验；
- React 直接消费单一生成文件，或在自动门禁中同时 hash/比较两个文件。

### R08：Timeline 默认日期没有使用数据库 reporting timezone

严重度：Medium

Timeline 首次加载以浏览器/操作系统当前日期生成 `YYYY-MM-DD`。数据库 reporting timezone 在建库时固定；用户修改系统时区后，Today 与 Timeline 可能查询不同 local date。

修改建议：

- 增加 `activity_get_timeline_today`，由 Rust 根据 DB reporting timezone 解析日期；或
- 先从 Rust 获取 reporting local date，再请求 Timeline；
- 增加“建库后切换 Windows 时区”的回归测试。

### R09：Diagnostics 的时间和队列深度不可信

严重度：Medium

DiagnosticsView 使用 `useState(() => Date.now())`，相对时间基准在组件生命周期内不再更新。Heartbeat 生成 queue depth 时又从上一份 SharedState 读取深度，随后写回自身；没有任何代码读取真实 mpsc channel 占用量，因此正常情况下队列深度会长期显示 0。

此外，持久化 heartbeat 总是把 `safe_error_code` 写为 `None`，离线 DB fallback 也不会返回持久化安全错误码。

修改建议：

- 使用随轮询更新的当前时间；
- 在 Capture/Processor/Writer sender 处维护原子 queue depth，或使用 channel capacity 差值；
- 将可安全持久化的错误码写入 runtime heartbeat；
- 增加非零 queue depth 和离线诊断回归测试。

### R10：High Contrast 与无障碍仍只能视为待验收

严重度：Medium

当前 UI 已有基本语义标签和 `:focus-visible`，但未发现专门的 `forced-colors`/High Contrast 适配，也没有可提交的键盘、读屏、DPI 与主题证据。Timeline 中用户选择显示的 transition 行仍带 `aria-hidden=true`，读屏不会感知该信息。

修改建议：

- 增加 Windows forced-colors token/边界处理；
- 检查按钮、徽章、表单、焦点和 gap 状态在 High Contrast 下不依赖颜色；
- 决定 transition 是否应对读屏可见；
- 完整执行并保存尺寸、DPI、Light/Dark/High Contrast、键盘和读屏清单。

## 7. 自动门禁覆盖缺口

虽然 88 项 Rust 测试和 20 项 React 测试全部通过，但尚未发现与 09 §12.1 以下条目完整对应的自动证据：

- Writer busy、disk-full、corruption/FK、checkpoint busy 故障注入；
- control lane 在 data lane 满载下的优先级和固定水位；
- DB、WAL、日志、DTO 的敏感测试标题、完整路径和排除 App 扫描；
- IPC timeout、非法 UTF-8、不同 channel 的完整 envelope、重复/冲突 request ID 的 in-progress 路径；
- Settings 文件删除、revision 降级、digest 冲突、Run Key 补偿失败和自动重试；
- Desktop 退出后 Agent 保持运行的真实进程级断言；
- 安装版 Desktop/Agent 固定路径的端到端启动；
- Agent 离线时读取已有历史；
- 21+ 应用、跨午夜、时区切换条件下 Today/Timeline 守恒。

建议将 09 §12.1 转为一张可执行映射表：每个门禁对应测试名、命令、证据文件、最后通过 commit 和日期。测试总数只能作为摘要，不能替代逐项门禁证据。

## 8. 文档修改建议

### 8.1 `migration-status.md`

建议立即修订：

- “V01-1–V01-8 全部完成”改为“V01-1–V01-7 实现完成；V01-8 验收重新打开”；
- Bridge-free Tauri、Rust Agent、Desktop、UI、dev Rebuild 链路按实际状态更新；
- 长期 M0–M11 与 v0.1 V01 阶段分表，不复用含义含混的“dev v2 cutover”；
- 自动测试标明本次审核日期和实际命令；
- 手工门禁全部保留 Pending；
- 链接本审核报告和后续证据包。

### 8.2 `09-Tauri-Rust-Rebuild-v0.1实施基线.md`

建议：

- 保持 Draft 时不得声称后续阶段合同已经接受；或新增正式接受记录后更新状态；
- 明确 Settings 在 Agent 重启且文件损坏时如何恢复 last-known-good；
- 明确生命周期 control 的 sequence/watermark；
- 明确副作用 IPC timeout 不取消已经接受的操作；
- 为 soak 定义可执行的 RSS、WAL、heartbeat、退出和旧库存在性判据。

### 8.3 验收证据目录

建议新增：

```text
docs/dev/evidence/v0.1/<commit>/
  manifest.json
  automated-gates.md
  soak-summary.json
  package-validation.json
  manual-checklist.md
  screenshots/
```

证据必须脱敏，不得包含用户名、SID、原始窗口标题、真实应用隐私数据或本机绝对私有路径。

## 9. 建议修复顺序

### 第一批：修正结论与数据正确性

1. 重新打开 V01-8，修订 `migration-status.md`；
2. 修复 Today Top 20 少算和 drop event_count；
3. 添加对应回归测试。

### 第二批：关闭运行时边界

1. 实现生命周期 watermark 和真实 Sleep/Lock 事件；
2. 修复 Settings last-known-good、revision 单调性和 effectivity；
3. 修复 IPC timeout/幂等和严格 envelope；
4. 补齐 fault injection、隐私扫描和生命周期测试。

### 第三批：重新形成验收证据

1. 修复 package/soak 脚本；
2. 重新构建安装包并验证安装路径端到端启动；
3. 重新执行 8 小时 soak；
4. 完成锁屏/休眠、30 分钟对照、尺寸/DPI/主题/键盘/读屏矩阵；
5. 提交脱敏证据包并更新状态矩阵。

### 第四批：最终关闭

1. 运行 Rust、React 和旧系统回归；
2. 确认无未关闭 Blocker/High 问题；
3. 记录技术、产品和测试接受；
4. 创建新的 V01-8 关闭提交，替代当前缺乏充分证据的完成表述。

## 10. V01-8 重新关闭准入条件

只有同时满足以下条件，才建议重新声明 Rebuild v0.1 完成：

- R01–R06 全部修复并有回归测试；
- 09 §12.1 自动门禁逐项有当前 commit 的通过证据；
- 09 §12.2 手工门禁全部完成，或由责任人书面缩减基线范围；
- 修订后的 8 小时 soak 通过且确认为优雅退出；
- 安装版 Desktop/Agent 固定布局和启动链路通过；
- 两个旧数据库的存在性和 checksum 隔离证据明确；
- `migration-status.md` 内部一致；
- 09 v0.1 合同已接受，或文档明确只宣称实现完成而非验收完成；
- 旧 WPF/C#/Bridge 仍可独立运行，且没有被 Rebuild 默认接管 production channel。

## 11. 最终意见

这 12 个提交已经完成了有价值的架构替换原型，并建立了较好的 Rust 类型、SQLite 约束、状态机测试、Tauri 最小能力集和 React 四页基础。当前主要问题不是工程方向错误，而是完成结论领先于边界实现和验收证据。

建议保留 V01-1–V01-7 的工程成果，撤回或修订提交 `b42d2e2` 中“Rebuild v0.1 全部完成”的验收含义。完成本报告列出的高优先级修复并重新形成证据后，再以新的关闭提交确认 V01-8 和 Rebuild v0.1。
