# Rebuild v0.1 第二轮代码与验收审核报告

状态：第二轮审核完成，整改尚未通过复核  
审核日期：2026-07-23  
审核对象：第一轮审核后的未提交整改工作区（`HEAD b42d2e2` + working tree）  
整改回应：[Rebuild-v0.1-审核整改回应.md](./Rebuild-v0.1-审核整改回应.md)  
第一轮报告：[Rebuild-v0.1-代码与验收审核报告.md](./Rebuild-v0.1-代码与验收审核报告.md)  
实施状态：[migration-status.md](./migration-status.md)  
审核基线：[09-Tauri-Rust-Rebuild-v0.1实施基线.md](./09-Tauri-Rust-Rebuild-v0.1实施基线.md)

## 1. 审核结论

本轮整改取得了实质进展。Today 聚合、严格 IPC 解码与字段校验、Settings 启动恢复、Int64String、Timeline reporting timezone、Diagnostics 轮询时间、真实队列深度、forced-colors、安装版启动检查和 soak 优雅退出等修改方向基本正确；本轮实跑 Rust 119 项、React 22 项和旧 .NET 660 项测试均通过。

但当前仍不能接受“R02–R10 全部完成”或“自动门禁已全部关闭”的结论。本轮发现：

- 1 项 Blocker：为实现 Settings last-known-good 而新增的 `settings_revisions.content_json` 会把排除 App 名原文写入行为数据库，直接触发 09 §12.3 隐私一票否决；
- 4 项 High：既有 v0.1 dev 数据库与新 DDL 不兼容；真实 Lock/Sleep 事件链没有建立可靠采集边界；sequence watermark 和 Settings effectivity 仍存在无法到达或错配问题；证据包没有唯一绑定当前源码状态；
- 3 项 Medium：IPC in-progress 请求可能永久残留；soak 的 RSS 判定布尔条件与书面合同不一致；安全错误码恢复后可能长期残留；
- 09 §12.2 手工矩阵和 disk-full 注入仍为 Pending，此点整改回应已如实披露。

建议当前状态改为：

> Rebuild v0.1 大部分第一轮问题已整改，自动检查可运行；隐私、生命周期、Schema 兼容和证据追踪仍阻断整改关闭。V01-8 保持打开，不得宣称实现已全部完成或自动门禁已全部关闭。

## 2. 审核范围与方法

本轮审核覆盖：

1. 逐项核对整改回应中 R02–R10 的代码、测试和文档修改；
2. 对照 09 §12、§16 与 `migration-status.md` 复核合同一致性；
3. 审查 Storage、Settings、Capture/Writer、IPC、Win32 事件泵、React UI、package/soak 脚本；
4. 实跑 Rust、React 与旧 .NET 自动检查；
5. 核对证据包、Cargo.lock、release Desktop/Agent 和 installer 的 SHA-256；
6. 检查整改前 v0.1 DDL 与当前 DDL 的兼容关系。

本轮没有重新执行：

- 完整 package 构建、静默安装和安装版启动流程；
- 新的 8 小时 soak；
- 锁屏/休眠、DPI、High Contrast、键盘、读屏和 30 分钟受控对照；
- disk-full 人工故障注入。

## 3. 第一轮问题复核结果

| 第一轮问题 | 第二轮状态 | 结论 |
|---|---|---|
| R01 完成结论与门禁冲突 | 部分关闭 | 文档已改为“实现完成、手工验收 Pending”，不再宣称全部验收通过；但当前仍使用若干 `Verified` 状态，而证据只标识旧 HEAD + 未提交工作区，且本轮仍有 Blocker/High 实现问题 |
| R02 Today 聚合少算 | 已关闭 | 总时长与 Top 20 分离、drop 改为 `SUM(event_count)`，21+ App、跨午夜和合并 gap 测试通过 |
| R03 Sleep/Lock 与 watermark | 未关闭 | 已增加事件泵和 watermark 测试，但真实事件链未冻结 Capture，Sleep 的 message-only window 接收方式也不可靠；watermark 仍可能引用永远不会入队的 sequence |
| R04 Settings 恢复与生效边界 | 未关闭 | last-known-good、降级/digest 对账和 reconciler 已实现；但完整 JSON 持久化造成隐私一票否决，旧 Schema 不兼容，effectivity 仍可能错配 |
| R05 IPC timeout 与严格协议 | 大部分关闭 | 非法 UTF-8、ULID、时间戳、未知字段、同 ID 重试已补齐；仍有 in-progress 永久等待和缓存泄漏边界 |
| R06 soak/package 证据 | 部分关闭 | 优雅退出、WAL/心跳、旧库存在性和安装目录 Agent 检查已补；RSS 判据实现错误，且证据没有唯一绑定整改源码 |
| R07 Int64String | 已关闭 | branded type、双副本 drift 检查和 BigInt 显示换算已落实 |
| R08 Timeline reporting timezone | 已关闭 | 首次日期来自 Rust/DB reporting timezone，并有 React 测试 |
| R09 Diagnostics 时间与队列 | 主问题已关闭 | 当前时间随轮询更新，队列深度有原子计数；但安全错误码恢复后清理仍不完整，见 S2-08 |
| R10 High Contrast/无障碍 | 自动部分关闭 | forced-colors 和 transition 可访问性已修改；实机 High Contrast、键盘和读屏仍按计划 Pending |

## 4. 第二轮阻断与高优先级发现

### S2-01：last-known-good 将排除 App 名原文写入 SQLite

严重度：Blocker  
关联：R04、09 §6.1、§12.1、§12.3、§16.1

当前 `Settings::canonical_json()` 会序列化完整 `excludedProcessNames`。`ActivityEngine::apply_settings()` 将该 JSON 交给 `ensure_settings_revision()`，最终写入 `settings_revisions.content_json`。

这与 09 中仍然有效的两条合同直接冲突：

- §12.1：DB、WAL、log、DTO 不出现排除 App 名；
- §12.3：数据库或日志出现排除 App，属于一票否决。

新增测试 `privacy_canary_never_persists_to_db_wal_or_dto` 没有覆盖这条路径。它只把 canary 放进 Processor 的 `watch::Settings` 以验证 Observation 被过滤，Writer 内的 `ActivityEngine` 仍使用默认 Settings；测试没有发送 `WriterControl::SettingsApplied`，所以 canary 从未进入 `settings_revisions.content_json`。测试通过不能证明生产 Settings 持久化符合隐私合同。

修改建议：

1. 不要在行为数据库中保存完整排除列表；
2. last-known-good 放到独立的私密 Settings 存储，例如原子双槽/版本化备份文件；SQLite 只保留 revision 与 digest；
3. 如果需要额外静态保护，可对 Settings 备份使用 Windows 用户范围的数据保护，但不能用“加密后写入行为库”替代基线评审；
4. 新增真实路径测试：通过 Desktop/Agent 应用含 canary 的 Settings，checkpoint、关闭并重开数据库后扫描 DB/WAL/DTO；
5. 在问题关闭前，将 `migration-status.md` 的“隐私内存边界”改为 Blocked，不得写“排除进程名只存在于入站消息生命周期”。

### S2-02：同为 Schema version 1 的整改前数据库无法被当前 Agent 打开

严重度：High  
关联：R04、数据兼容、dev 安装升级

`HEAD b42d2e2` 创建的 v0.1 数据库没有 `settings_revisions.content_json`；整改在同一个 `schema_version = 1` 上直接新增 `NOT NULL` 列。`Writer::open_existing()` 只校验 `schema_meta.schema_version` 和 WAL，随后 `main.rs` 调用 `latest_settings_content()` 查询新列。

因此，整改前已经运行过的默认 `rebuild-v01-dev` 数据库会先通过 version 1 检查，再以“no such column”失败。当前 storage 测试、package 和 soak 都使用新建数据库或唯一 test channel，没有覆盖这一升级路径。

09 §16.6 写明“不提供旧 dev 库迁移”，但这不能让两个结构不同的数据库继续共用同一个 schema version 并静默落入普通 DB 错误。

修改建议：

1. 若保留既有 dev 数据，提升 Schema version 并提供离线、显式、可回滚的迁移；
2. 若明确允许丢弃 v0.1 开发数据，则变更 channel/数据库命名空间，或提供明确确认的重置工具，不能让运行时代码自动删除；
3. 无论选择哪条路径，都应在打开连接时进行结构/manifest 校验，并返回 `DB_SCHEMA_UNSUPPORTED`，而不是在 Settings 查询阶段失败；
4. 增加“整改前 v0.1 fixture → 当前版本打开”的测试。

### S2-03：真实 Lock/Sleep 事件没有形成可靠的采集冻结与恢复边界

严重度：High  
关联：R03、09 §6.7、§12.2

当前 Windows 事件泵和 Agent 桥接存在三个问题：

1. `session_power.rs` 创建的是 `HWND_MESSAGE` message-only window；它不参与广播窗口集合，而 `WM_POWERBROADCAST` 是广播型通知，因此不能据此证明真实 Suspend/Resume 可达；
2. `main.rs` 收到 Lock/Sleep 后只发送 `WriterControl::Lifecycle { watermark: None }`，没有改变 `capture_state_tx`。锁屏后 Capture 仍可继续采样，第一条有效 Observation 会立即关闭刚创建的 `session_locked` gap；
3. 事件泵启动失败被 `Err(_) => None` 静默吞掉，Diagnostics 不会提示生命周期监视已经失效。

现有 `sleep_and_lock_events_close_rows_with_matching_kinds` 只直接注入 EngineEvent；`pump_starts_and_yields_receiver` 只证明窗口和 receiver 可创建，都没有证明真实 Windows 消息、Capture 冻结、Unlock/Resume 恢复的完整链路。

修改建议：

1. 使用可接收电源通知的隐藏顶层窗口，或使用受支持的 suspend/resume 注册 API；
2. 为用户 Pause/Stop 与系统 Lock/Sleep 分离状态，Lock/Sleep 先冻结 Capture，再通过 pipeline barrier 提交边界；
3. Unlock/Resume 显式解除系统冻结，但不得覆盖用户主动 Pause/Stop；
4. 事件泵失败必须进入安全诊断并使相关手工门禁失败；
5. 增加可注入 OS adapter 的进程级测试，并保留真实锁屏/休眠人工验收。

### S2-04：sequence watermark 可指向永远不会到达 Writer 的消息

严重度：High  
关联：R03、R04

Capture 在阻塞采集开始前就递增并发布 `latest_sequence`。以下两种情况都会使该 sequence 永远无法到达 Writer：

- Pause/Stop 在 `spawn_blocking` 期间冻结 Capture，返回后的迟到样本被直接丢弃；
- Capture 或 Writer queue 满时采用 drop-new。

Writer 的 `drain_to_watermark()` 等不到该 sequence 时，1.5 秒后记录 `INTERNAL_SAFE_ERROR` 并继续提交生命周期或 Settings 边界。此时 watermark 既不是严格屏障，也不是失败；正常 Pause/Stop 在特定竞争下会制造伪诊断。

Settings 还存在额外 effectivity 问题：`settings_reload` 在 Writer 应用新 revision 前没有冻结 Capture，也没有在 `RawCapture/ProcessorOutput` 中携带实际 Settings revision。watermark 之后、`settings_tx.send()` 之前产生的样本可能按旧隐私/阈值处理，却在 Engine 已切换后被写成新 revision。

修改建议：

1. 不使用“最新尝试序号”推断 pipeline 已排空；在 Capture→Processor→Writer 发送显式 barrier；
2. barrier 与数据走同一有序路径，Writer 收到 barrier 后才提交生命周期/Settings 控制；
3. `RawCapture/ProcessorOutput` 携带实际 Settings revision，Writer拒绝或正确处理错配；
4. barrier 超时不得按成功放行；应返回安全错误并维持冻结状态；
5. 增加 in-flight Win32 capture、Capture queue drop、Writer queue drop、Settings 应用期间排除列表变化的竞争测试。

### S2-05：证据包没有唯一绑定当前整改源码

严重度：High  
关联：R01、R06、`migration-status.md` 的 Verified 定义

证据包记录：

```text
headCommit = b42d2e2...
worktree = 审核整改 R02–R10 全部修改未提交
```

soak 也只记录 `gitCommit = b42d2e2...`。但 R02–R10 的约 40 个已跟踪文件修改和多个新增 Rust 文件均不在该 commit 中；证据没有记录完整 patch/tree hash，也没有列出 untracked 文件内容。因此，“HEAD + 工作区”不是可唯一复现的源码身份。

本轮只读核对确认 Cargo.lock、release Agent、release Desktop 和 installer 的 SHA-256 与证据相符。这能证明本机现有工件与 JSON 记录一致，但不能证明这些工件来自以后将要提交的最终源码。

修改建议：

1. 先关闭本报告的代码问题并提交最终源码；
2. 证据记录完整 commit SHA、clean worktree、Cargo.lock、Desktop/Agent/installer hash；
3. package 至少在最终 commit 上重建和重跑；
4. 由于 S2-01–S2-04 会改变 Agent 关键路径，最终修复后必须重新执行 8 小时 soak；
5. 最终证据形成前，`wuji-core`、`wuji-storage` 等条目最高标为 Implemented，不应标为 Verified。

## 5. 中优先级发现

### S2-06：IPC in-progress 请求可能永久残留

严重度：Medium

`RequestIdCache::purge_expired()` 永远保留 `InProgress`。相同 ID 的 `CacheEntry::Wait` 直接等待 `receiver.changed()`，没有服务端 timeout。若独立 dispatch 任务 panic、Writer ack 永不返回或任务异常退出，该 ID 将永久占用缓存；每次重试还会留下等待中的连接任务。容量清理也只删除 Completed，无法约束大量 InProgress。

建议为独立任务增加完成守卫：无论成功、错误、panic 或取消，都把条目转为稳定失败结果；给 Wait 设置有界 timeout；对 in-progress 设置最大年龄和总量，并增加 panic/ack 丢失测试。

### S2-07：soak 的 RSS 失败条件与书面判据不一致

严重度：Medium

脚本和证据写的是：

```text
RSS 增长 < 64 MiB 且 < 50%
```

但代码只有在“字节增长超过 64 MiB **且** 比例超过 50%”时才失败。这会放过只违反其中一个上限的运行。失败条件应为：

```text
growth_bytes >= 64 MiB 或 growth_ratio >= 50%
```

当前 8 小时报告的实际增长约 2.6 MB、17%，两条都满足，所以这一布尔错误不推翻该次 RSS 数据本身；但脚本不能据此被称为合同已正确实现。应补边界单元测试。

### S2-08：安全错误码恢复后可能长期显示陈旧故障

严重度：Medium

busy 写入恢复后代码把 `writer_state` 从 degraded 改回 healthy，但不清除对应 `safe_error_code`；checkpoint busy 后下一次 checkpoint 成功也不清除旧的 `AGENT_WRITER_DEGRADED`。心跳会继续把该错误持久化，Diagnostics 可能同时显示“Writer 正常”和旧错误码。

建议将诊断改为按来源管理的当前故障集合，或至少只在确认同来源恢复时清除对应错误，避免一次成功写入误清 Settings/IPC 等无关故障。

## 6. 本轮自动验证结果

| 验证项 | 本轮结果 | 说明 |
|---|---|---|
| `cargo fmt --all -- --check` | Passed | 无格式漂移 |
| `cargo clippy --workspace --all-targets -- -D warnings` | Passed | 零警告 |
| `cargo test --workspace` | Passed | 119 项通过；沙箱内首次运行因 `%LOCALAPPDATA%`/Named Pipe 权限导致 6 个 Agent e2e 无法启动，允许隔离 test channel 的系统资源访问后全过 |
| `pnpm typecheck` | Passed | TypeScript 检查通过 |
| `pnpm lint` | Passed | ESLint 零警告 |
| `pnpm test` | Passed | 5 个文件、22 项通过 |
| `pnpm build` | Passed | Vite production build 成功 |
| `dotnet test QuantifiedSelf.Windows.sln --no-build` | Passed | 660 项通过；本轮未重新 restore/build |
| `git diff --check` | Passed | 只有行尾转换提示，无 whitespace error |
| Cargo.lock SHA-256 | Match | 与证据包一致 |
| release Agent SHA-256 | Match | 与 package/soak 证据一致 |
| release Desktop SHA-256 | Match | 与 package 证据一致 |
| installer SHA-256 | Match | 与 package 证据一致 |
| package 全流程 | NotRun | 只核对现有证据与工件 |
| 8 小时 soak | NotRun | 只审查脚本、现有摘要和工件 hash |

自动测试通过说明整改代码具有较好的基本回归质量，但不能覆盖本报告指出的合同矛盾和真实系统事件边界。

## 7. 文档与状态一致性意见

### 7.1 `migration-status.md`

建议修改：

- “R02–R10 完成”改为“R02、R07、R08 已关闭；R03、R04、R06 未关闭；R05/R09 主问题关闭但有第二轮遗留”；
- “隐私内存边界”改为 Blocked，并明确 `content_json` 当前包含排除列表；
- Rust Agent、Storage、Win32 事件泵不得标为 Verified；
- V01-8 保持打开，同时将“自动部分已关闭”改为“自动检查已通过，但自动验收存在阻断”；
- 链接本报告并列出 S2-01–S2-08。

### 7.2 `09-Tauri-Rust-Rebuild-v0.1实施基线.md`

§16.1/§16.6 与 §12.3 当前互相冲突。不得只通过新增合同文字覆盖隐私一票否决。应先决定：

- last-known-good 是否移出行为数据库；
- dev 旧库是否迁移、重命名 channel，或由用户明确重置；
- Lock/Sleep 的冻结、恢复和用户 Pause/Stop 叠加状态；
- barrier 超时究竟是失败还是允许降级。

这些决定会改变运行合同，修订后需要重新评审，而不是仅作为实现注释补入。

### 7.3 整改回应与证据包

整改回应应将“R02–R10 全部完成”改为逐项状态。证据包目录名不应继续使用 `b42d2e2-remediation` 表示一个并不存在于该 commit 的源码状态；最终应以实际整改 commit 或不可变版本标识重新生成。

## 8. 剩余人工门禁

整改回应和 `manual-checklist.md` 对以下 Pending 项披露基本一致，本轮不判为虚假完成：

- 真实锁屏、休眠/恢复；
- Today/Timeline 与 30 分钟受控脚本对照；
- 960×640、1280×800；
- 100%、150%、200% DPI；
- Light、Dark、Windows High Contrast；
- 键盘导航、焦点可见、屏幕阅读器；
- Agent 离线时 UI 读取历史与安全状态；
- disk-full 故障注入。

其中真实锁屏/休眠不能只作为普通手工待办：S2-03 表明实现路径本身需要先修复，修复后再执行人工门禁。

## 9. 建议整改顺序

### 第一批：解除一票否决与兼容问题

1. 移除行为数据库中的完整 Settings/排除 App 原文；
2. 重新设计 last-known-good 私密存储；
3. 决定旧 v0.1 dev DB 的迁移、命名空间切换或明确重置策略；
4. 增加真实 Settings 持久化隐私测试和旧 Schema fixture。

### 第二批：修正生命周期与 effectivity

1. 用可靠 Windows API 接收 Suspend/Resume；
2. Lock/Sleep 冻结、Unlock/Resume 恢复，并保持用户 Pause/Stop 优先级；
3. 用 pipeline barrier 替代不可达 sequence watermark；
4. 给数据携带实际 Settings revision；
5. 补齐竞争和故障测试。

### 第三批：收紧 IPC、诊断和验收脚本

1. 为 in-progress 请求增加完成守卫、过期和容量上限；
2. 修复 RSS `and`/`or` 条件并添加脚本单测；
3. 清理已恢复的来源化安全错误。

### 第四批：重建可信证据

1. 提交最终代码，确保 clean worktree；
2. 在最终 commit 上实跑 Rust、React、旧系统回归；
3. 重建 package 并重跑安装版启动；
4. 重跑 8 小时 soak；
5. 完成全部人工矩阵和 disk-full；
6. 更新证据包与 `migration-status.md` 后再发起第三轮审核。

## 10. 第三轮准入条件

建议满足以下条件后再申请关闭整改：

- S2-01–S2-05 全部修复并有针对性回归测试；
- S2-06–S2-08 已修复，或有明确接受记录和延期边界；
- 09 内部不再存在 last-known-good 与隐私、Schema version 与旧库策略的冲突；
- 最终源码已提交，证据唯一绑定该 commit；
- 最终 commit 的 package 和 8 小时 soak 通过；
- 真实锁屏/休眠门禁通过，且可证明事件确实到达 Agent、冻结 Capture、形成正确 gap 并恢复；
- 其他 §12.2 手工矩阵与 disk-full 全部完成；
- `migration-status.md` 中 Implemented、Verified、Pending、Blocked 与证据一致；
- 旧 WPF/C#/Bridge 和两个旧数据库继续保持隔离、可回滚。

在这些条件满足前，本轮审核结论为：**整改不通过，V01-8 继续打开。**
