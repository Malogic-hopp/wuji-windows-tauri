# WUJI v2 迁移实施状态

状态：实施状态记录（不定义设计）
基线日期：2026-07-18
最近更新：2026-07-19（V01-1、V01-2、V01-3、V01-4、V01-5、V01-6、V01-7 完成）
本次核对方式：仓库结构与源码只读检查 + rebuild workspace `cargo test`/`clippy`/`fmt` 实跑；旧系统 build、test、smoke、soak 未重新运行
当前实施依据：[09-Tauri-Rust-Rebuild-v0.1实施基线.md](./09-Tauri-Rust-Rebuild-v0.1实施基线.md)
长期目标依据：[ADR-002](./ADR-002-React-Tauri-Rust目标架构.md) 与 [01–08](./README.md#1-文档层级与适用范围)

## 1. 使用规则

本文件回答“仓库现在实际做到哪里”，不能接受或修改产品语义、领域模型、Schema、运行时、协议或迁移门禁。若本文件与权威设计冲突，应修正本文件，不得用实施现状覆盖目标设计。

状态定义：

| 状态 | 含义 |
|---|---|
| Not started | 未发现目标实现或可执行产物 |
| Design only | 仅有 Draft/Proposed 文档，尚无目标代码或正式 manifest |
| Prototype | 有隔离原型，尚未接入目标端到端运行路径 |
| Partial | 目标的一部分已接入，但仍依赖旧边界或缺少关键能力 |
| Implemented | 目标代码已接入预期路径，尚未完成对应验收门禁 |
| Verified | 已实现，并有当前 commit/版本对应的有效测试证据 |
| Blocked | 明确被尚未满足的设计或验收硬门禁阻断 |

“已有历史完成说明”不自动等于 Verified。每次实现变化都必须更新证据链接、最后验证日期和下一门禁；没有当次可复现结果时保持 Implemented/Partial，并把测试写为 NotRun。

## 2. 当前结论

WUJI 当前是已经存在 React 19/Tauri 2 dev shell、但核心能力仍通过 `.NET Bridge` 进入 C# 实现的过渡架构：

```text
React 19 / TypeScript
→ Tauri 2 Rust Host
→ Rust BridgeSupervisor
→ QuantifiedSelf.Windows.Client.Bridge (.NET sidecar)
→ C# Client / Application / Infrastructure
→ C# Agent / SQLite v1
```

目标架构的端到端链路已经打通并经真实窗口验证：React → Tauri Rust Host → Rust Agent → SQLite 完全脱离 .NET Bridge 运行。V01-1 建立 `rebuild/` workspace 与 `wuji-core`；V01-2 落地 `wuji-storage`（唯一内嵌 DDL、bootstrap、Writer、重算、只读 Reader）；V01-3 落地 Win32 采集与隐私过滤；V01-4 落地 Activity/Work 精确状态机；V01-5 落地完整 Agent 运行时（双 lane Writer、Named Pipe CommandServer、心跳、单实例、恢复）；V01-6 落地 Desktop Host（IPC client、只读 Query、Settings CAS、detached Agent、12 语义命令、托盘/单实例，不含 Bridge 代码）；V01-7 落地四个正式页面：Today（指标/Top Apps/gap 提示）、Timeline（Segment/Gap 渲染、cursor 分页、切换间隔默认折叠）、Settings（六字段、CAS、saved/applied 分离、中文错误）、Diagnostics（运行健康、最后活动、resync 修复、折叠脱敏高级信息），统一 Loading/Empty/Ready/Error 四态，顶栏 Agent 状态与控制按状态切换按钮，Light/Dark 令牌主题与可见焦点。真实窗口冒烟（vite dev + desktop exe）验证：按钮点击开始/停止采集、真实前台应用写入 SQLite、Today/Timeline/Diagnostics 实时刷新。Rust 88 项测试、React 20 项测试、`clippy -D warnings`、`fmt --check`、typecheck/lint/build 全部通过。dev bundle 与 V01-8 验收尚未实现。

ADR-002 仍为 Proposed，01–08 仍为 Draft；Fact Boundary、Generation/Result Set/Snapshot、Identity Resolution、Lease/GC、production binary/session 认证、Importer 和旧系统退役继续作为长期 Design only，不阻挡 dev-only v0.1，但在未来 production cutover 前仍需重新进入对应门禁。

## 3. 仓库证据摘要

- [Tauri package.json](../../src/QuantifiedSelf.Windows.Tauri/package.json) 固定 React `19.2.7`、Tauri CLI `2.11.4`，同时仍包含 `bridge:prepare`、Bridge parity smoke 和 lifecycle soak；
- [Tauri Cargo.toml](../../src/QuantifiedSelf.Windows.Tauri/src-tauri/Cargo.toml) 是 Rust edition 2024 的 Tauri 2 Host；
- [Tauri lib.rs](../../src/QuantifiedSelf.Windows.Tauri/src-tauri/src/lib.rs) 启动 `BridgeSupervisor`，注册的仍是 `agent_start/pause/resume/stop`、`activity_get_overview`、`settings_get/update` 等过渡 commands；
- [Bridge supervisor](../../src/QuantifiedSelf.Windows.Tauri/src-tauri/src/bridge/supervisor.rs) 仍负责 `.NET Bridge` sidecar 生命周期和调用；
- React 页面当前集中在 [DashboardPage.tsx](../../src/QuantifiedSelf.Windows.Tauri/src/pages/DashboardPage.tsx) 与 [SettingsPage.tsx](../../src/QuantifiedSelf.Windows.Tauri/src/pages/SettingsPage.tsx)，客户端合同位于 `src/bridge/`；
- [C# Agent Program](../../src/QuantifiedSelf.Windows.Agent/Program.cs) 仍注册 ForegroundSample repositories、Win32 provider、隐私过滤和 SessionAggregator；
- [AgentStateMachine.cs](../../src/QuantifiedSelf.Windows.Agent.Runtime/State/AgentStateMachine.cs) 仍承担当前 Agent 的大 Tick/状态机流程；
- [SessionAggregator.cs](../../src/QuantifiedSelf.Windows.Agent.Runtime/Services/SessionAggregator.cs) 和 C# Infrastructure 仍实现当前 Session/SQLite 路径；
- [WPF App 项目](../../src/QuantifiedSelf.Windows.App/QuantifiedSelf.Windows.App.csproj)、[.NET Bridge 项目](../../src/QuantifiedSelf.Windows.Client.Bridge/QuantifiedSelf.Windows.Client.Bridge.csproj) 与 [解决方案](../../QuantifiedSelf.Windows.sln) 仍在当前构建拓扑内。

## 4. 能力迁移矩阵

本矩阵同时保存 v0.1 当前目标和长期目标；标为“长期”的行不是 v0.1 阻断项。

| 能力/边界 | 当前状态 | 当前证据 | 目标 | 主要差距 | 下一门禁 |
|---|---|---|---|---|---|
| v0.1 实施基线 | Design only | [09](./09-Tauri-Rust-Rebuild-v0.1实施基线.md) 已定义范围、运行/算法/协议合同、阶段和验收；[DDL](../../rebuild/crates/wuji-storage/schema/schema.sql) 可执行且已内嵌 | dev-only bridge-free React/Tauri/Rust Agent/SQLite 链路 | 合同仍为 Draft；V01-1/V01-2 已完成，其余阶段未开始 | V01-3 前接受第 5–6、8 节 |
| 产品语义与指标 | Design only | [01](./01-产品语义与指标词典.md) 为 Draft | Accepted 的 Observation/Activity/Context/Work/质量/时区词典 | 产品接受、延期项和候选阈值尚未签署 | G-ADR / ALG golden review |
| 领域模型 | Design only | [02](./02-行为分析领域模型.md) 为 Draft | 事实、派生、Generation、Result Set、Snapshot 不变量可执行 | 尚无 Rust 类型与属性测试 | DOM-001–005 |
| 目标架构 ADR | Blocked | [ADR-002](./ADR-002-React-Tauri-Rust目标架构.md) 状态 Proposed | Accepted 并取代当前过渡 ADR 的最终架构 | 依赖规范尚未形成 Accepted 基线 | G-ADR |
| React 19 UI 基座 | Implemented | `rebuild/apps/desktop`：Today/Timeline/Settings/Diagnostics 四页 + 四态 + 顶栏 Agent 控制 + 令牌主题；20 项 Vitest 通过；真实窗口冒烟（按钮驱动采集、真实前台数据回显） | Today/Timeline/Settings/Diagnostics 使用 v0.1 DTO | 手工矩阵（尺寸/DPI/HC/读屏）待 V01-8 验收 | V01-8 |
| Tauri 2 Desktop shell | Implemented | `rebuild/apps/desktop/src-tauri`：IPC client、Query、Settings CAS、detached Agent 控制、托盘、单实例、12 语义命令、集成测试 | 直接使用 Rust Query/IPC/Settings/Process Controller | — | V01-7 页面接入 |
| Bridge-free Tauri | Partial | rebuild 链路 React→Tauri→Rust Agent→SQLite 全程无 Bridge（集成测试证明）；旧 `src/QuantifiedSelf.Windows.Tauri` 仍含 BridgeSupervisor（回滚入口） | 安装包与运行时不含 `.NET Bridge` | dev bundle 未验证（V01-8） | REL-001（V01-8） |
| Rust workspace / `wuji-core` | Verified | `rebuild/crates/wuji-core`（commit `c2ca961`）：schema 对齐领域枚举、Settings 默认值/验证/digest、21 个稳定错误码、固定命名空间、DTO + specta branded TS drift 门禁；`cargo test -p wuji-core` 21 项通过 | 纯领域、Settings、Privacy、Analytics、Protocol、Error | 长期 Privacy/Analytics 部分待后续版本 | V01-2 起持续回归 |
| Rust `wuji-storage` | Verified | `rebuild/crates/wuji-storage`：唯一内嵌 DDL、六步 bootstrap 自检、Writer 行操作、触及桶重算、只读 Reader；`cargo test -p wuji-storage` 18 项通过（含 DST/幂等/分页/恢复） | v0.1 Single Writer、只读 Query、空库 bootstrap 和最小 projection | — | V01-4 状态机接入后回归 |
| Rust Agent binary | Implemented | `rebuild/apps/agent`：独立进程真实采集写库；双 lane Writer、CommandServer、心跳、MaintenanceLite、单实例与启动恢复全部接入；4 项 e2e 子进程测试通过 | 独立 Rust Agent 长期进程 | Desktop 进程管理与 UI 路径未接入 | V01-6 |
| Rust Win32 Capture Adapter | Verified | `rebuild/crates/wuji-windows`：GetForegroundWindow/GetWindowThreadProcessId/QueryFullProcessImageNameW/GetLastInputInfo 字段级降级适配器；真实 Windows 集成测试含卡死与退出进程路径 | v0.1 Rust foreground/process/idle adapter | — | V01-5 Agent 接入后回归 |
| 隐私内存边界 | Design only | ADR-002、03、05 定义；当前 C# 有 PrivacyFilter | 原始标题/路径在 Rust Agent 持久化前过滤 | 尚无 Rust 实现、DB/WAL/log/DTO 扫描 | SEC-002 |
| SQLite v0.1 Schema | Implemented | [schema.sql](../../rebuild/crates/wuji-storage/schema/schema.sql) 为唯一 DDL 并已编译期内嵌；空库执行、STRICT/FK/CHECK/单 open 行/WAL 经探针与临时库集成测试验证 | 内嵌同一 SQL 从零创建独立 dev DB | — | V01-4/V01-5 接入后回归 |
| SQLite 长期 Schema | Design only | [04](./04-SQLite-v2与持久化读模型.md) 有完整逻辑字段 | production migration + manifest | v0.1 明确延期 | 后续 G-DDL |
| Fact Cursor | Design only | 02/04 定义数据库全局水位 | 与事实同事务、跨 runtime 的持久水位 | 当前模型仍以旧 Sample/Session 与 Tick 流程为主 | DOM-001 / DB-005 |
| Segmentation Generation | Design only | 02–04 定义 | Rust staging + immutable Segmentation Result Set | 无代码、表、job 或发布器 | M5 / DB-006–008 |
| Work Generation | Design only | 第二轮修订已与 Context 解耦 | 独立 Work Profile/Generation/Result Set | 无实现与解耦回归 | DOM-004 / ALG-003 |
| Analysis Generation | Design only | 02–04 定义 Context/Event 世代 | 规则版本化、可重建、可解释 | 无规则引擎、evidence 或黄金样本实现 | ALG-002/004 |
| Result Set / Query Snapshot Slice | Design only | 02/04/05 定义具体组件 FK、复合 Fact Boundary、空 Snapshot 和不可变 Slice | W0/W1/W2 原子发布、Projection→具体 Set 一致、稳定读取 | 无 Schema、Publisher、Query Service、GC | DB-006–010、DB-014–016 |
| Identity Resolution | Design only | 01–04 定义跨世代可信 Link 与不可变 Resolution Generation | Apps/Top Apps/Hourly/Daily 按固定 canonical identity 聚合 | 无表、映射器、App 投影或同名分离 UI | DOM-006 / M5 |
| 小时/日持久化读模型 | Design only | 04 定义 hourly/daily 表族 | Today/Trends/Heatmap 不扫描 Observation | 当前页面仍依赖旧 overview/session 查询 | M5 / PERF-001–002 |
| Named Pipe v2 | Partial | `wuji-windows/pipe.rs` 同用户 DACL + agent CommandServer：hello、envelope、64 KiB/3s、request ID 幂等、Capture 状态机、稳定错误码、e2e 覆盖 | DACL + Desktop binary/signature manifest + 内存 session capability、版本握手、幂等 receipt | production binary 认证与 capability 属于长期（09 §8.1 已延期） | V01-6 接入 Desktop |
| 可信原生确认 | Design only | ADR-002、06、08 已冻结 React 无 token/consume 能力 | Clear/导出/隐私削弱由 Win32 原生确认后在 Rust 同流执行 | 当前目标 command/TrustedActionCapability/proof 均未实现 | SEC-003 / M6–M7 |
| Process/Capture 生命周期分离 | Implemented | `agent_process_ensure_running`（detached，初始 stopped）与 `capture_start/pause/resume/stop`（IPC + FSM）已分离并经集成测试 | StartAgentProcess 与 CaptureStart/Stop 分离 | — | V01-8 包体验证 |
| Settings Revision/Profile/Effectivity | Partial | v0.1 CAS（expectedRevision）+ 原子写 + saved/applied 分离 + Run Key 补偿 + Agent 对账已实现并通过集成测试 | Desktop 单写，Agent 对账，按首条事实生效 | Effectivity/Profile 属于长期（09 已延期） | 长期 |
| 数据库 pointer / reader lifecycle | Design only | 04/06 定义版本文件、pointer、DatabaseReady | Windows 可恢复 major migration 切换 | 无 trusted pointer/migrator/reader close 实现 | DB-011 / REL-003 |
| v1→v2 importer | Not started | 04/07 只有规则 | 离线、幂等、可恢复的导入与 Legacy Summary | 无 fixture、import job、报告或工具 | M8 / DB-012 |
| Shadow / parity | Not started | 当前有 Bridge 阶段页面/lifecycle parity 脚本 | 同输入或 v1 快照的 v1/v2 语义/守恒比较 | 没有 Rust v2 输出，无法开展目标 parity | M8 / Parity gate |
| dev v2 cutover | Blocked | 现有 Tauri 明确是 dev-only，但仍依赖 Bridge | dev 默认 bridge-free Tauri + Rust Agent | M1–M8 尚未完成 | G-DEV |
| prod v2 cutover | Blocked | 当前稳定/参考资产仍是 C# 与 WPF 路径 | prod canary 后 Rust v2 默认 | G-DEV、canary、恢复与产品批准均未完成 | G-PROD |
| 旧系统退役 | Blocked | WPF/C# Agent/Infrastructure/Application/Client/Bridge 仍被使用 | 停产、归档后按依赖顺序移除 | 尚无可替代的 Rust 端到端实现或稳定观察期 | G-RETIRE |

## 5. 长期阶段状态

以下 M0–M11 只用于长期 production 路线；v0.1 当前进度使用 09 的 V01-1–V01-8。

| 阶段 | 状态 | 已有内容 | 未满足的退出条件 |
|---|---|---|---|
| M0 设计与测试基座 | Partial | 01–08、ADR-002 和三轮审核回应已形成 Draft 集；当前仓库有多层测试基座 | 文档尚未 Accepted；Schema manifest、正式 golden fixture/证据模板和安全威胁 fixture 未落地 |
| M1 Rust Workspace/Core | Not started | Tauri Host 已使用 Rust，但不是目标 Core workspace | `wuji-core` 边界、领域类型、错误/协议和测试 |
| M2 Win32 Capture Adapter | Not started | C# Win32 实现可作行为参考 | Rust adapter、隐私边界和 Windows 故障测试 |
| M3 SQLite v2 基座 | Not started | 只有逻辑设计 | manifest、DDL、migration harness、Fact Cursor、pointer |
| M4 Rust Agent 流水线 | Not started | C# Agent 是参考实现 | 独立 Rust binary 和完整异步流水线 |
| M5 派生与查询发布 | Not started | 只有领域/存储设计 | Generation、Result Set、Snapshot、读模型和查询服务 |
| M6 IPC 与 Settings | Not started | 旧 C# IPC/Settings 和 Bridge 可作兼容参考 | Named Pipe v2、revision/effectivity、稳定 DTO |
| M7 Tauri 去 Bridge | Not started | React/Tauri UI 外壳已存在 | 直连 Rust v2、目标页面与 bridge-free package |
| M8 v1 导入与 shadow | Not started | 现有 Bridge parity 工具只能验证过渡架构 | importer、Legacy、目标 shadow/parity 报告 |
| M9 dev cutover | Blocked | dev channel/preview 隔离经验可复用 | G-DEV 前置全部未完成 |
| M10 prod canary/cutover | Blocked | 旧系统仍提供稳定参考/回滚 | G-PROD 前置全部未完成 |
| M11 旧系统退役 | Blocked | 无 | 生产稳定期、覆盖接替、恢复归档与明确批准 |

## 6. 当前测试与验证状态

| 验证项 | 仓库能力 | 本基线结果 | 说明 |
|---|---|---|---|
| Rebuild `cargo test --workspace` / `cargo clippy -D warnings` / `cargo fmt --check` | 命令存在（`rebuild/`） | Passed（2026-07-19，88 项测试全过、零警告） | 覆盖 wuji-core、wuji-storage、wuji-windows、Agent 单元/黄金样本/e2e、Desktop 单元与集成测试；随 V01 阶段扩展 |
| Rebuild Desktop `pnpm typecheck` / `pnpm lint` / `pnpm test` / `pnpm build` | `rebuild/apps/desktop/package.json` | Passed（2026-07-19，Vitest 20 项通过、零警告、dist 产出） | 四页组件与顶栏门禁；手工矩阵待 V01-8 |
| C# build / full xUnit | 命令和项目存在 | NotRun | 不能引用历史记录作为 2026-07-18 当前结果 |
| React typecheck/lint/Vitest | package scripts 存在 | NotRun | 只覆盖当前 Bridge 阶段 UI，不覆盖 v2 Gate |
| Tauri/Rust tests | Cargo tests 存在 | NotRun | 主要覆盖 Host/Bridge/lifecycle，不是 Rust Agent/Core/Storage |
| Bridge contract drift | `contracts:check` 存在 | NotRun | 迁移期可保护过渡合同，最终应随 Bridge 退役 |
| Dashboard/Settings/Lifecycle parity | smoke scripts 存在 | NotRun | 当前用于 WPF/Bridge/Tauri parity，不等于 v1/v2 领域 parity |
| lifecycle soak / 24h | scripts 存在 | NotRun | 需为 Rust Agent v2 重建并保存新证据 |
| WPF UI 手工矩阵 | 仓库规范存在 | NotRun | Tauri/WebView2 仍需独立执行完整矩阵 |
| 08 的 G-ADR/G-DDL/G-DEV/G-PROD/G-RETIRE | 文档已定义 | Blocked | 尚无 Gate 证据包和批准记录 |

## 7. 当前不得删除的迁移资产

在 G-RETIRE 通过前，以下资产不得因“目标架构已写入文档”而删除：

- `QuantifiedSelf.Windows.App`：当前 WPF 稳定参考、UI parity 和回滚入口；
- `QuantifiedSelf.Windows.Agent` 与 `QuantifiedSelf.Windows.Agent.Runtime`：当前采集、状态机和生命周期实现；
- `QuantifiedSelf.Windows.Infrastructure`：当前 SQLite、Win32、IPC、settings/events/runtime persistence；
- `QuantifiedSelf.Windows.Core`、`Application`、`Client`：当前模型、服务和客户端边界；
- `QuantifiedSelf.Windows.Client.Bridge`：当前 Tauri 到 C# 的运行链路；
- Bridge 合同与 ContractGen：当前三端 drift 防护；
- 现有 publish/installer/startup 脚本：目标打包链替代并验证前仍是发布参考；
- C#、Bridge、Tauri、WPF 测试与 parity/soak 工具：新测试接替同类风险前继续保留；
- v1 Schema、脱敏 fixture 和最后兼容 artifact：v1 导入和恢复支持期限内长期保留。

允许在隔离分支/原型目录新增 Rust 目标结构，但不得让原型默认接管 prod channel。

## 8. 下一步可执行清单

当前直接按 v0.1 顺序推进：

1. ~~创建 `rebuild/` Rust workspace 与 `wuji-core`~~（V01-1 已完成，commit `c2ca961`）；
2. ~~按 `schema/schema.sql` 落地 bootstrap、Writer/Query 与触及桶重算幂等测试~~（V01-2 已完成）；
3. ~~实现 Win32 foreground/process/idle、隐私过滤、bounded queue 与真实采集测试~~（V01-3 已完成）；
4. ~~实现 Activity/Work 精确状态机与黄金样本守恒测试~~（V01-4 已完成）；
5. ~~实现双 lane Writer、CommandServer、heartbeat、单实例与恢复~~（V01-5 已完成）；
6. ~~实现 Tauri Query/IPC client、CAS Settings、detached Agent、无 Bridge Desktop 与端到端集成测试~~（V01-6 已完成）；
7. ~~实现 Today、Timeline、Settings、Diagnostics 四页与四态验收~~（V01-7 已完成）；
8. 完成 dev bundle、固定 Agent 布局、dev manifest、soak、旧系统隔离验收（V01-8），并更新本文件。

期间不得修改旧数据库或删除旧 C#/WPF/Bridge。长期 manifest、Importer、Snapshot/Lease 和 production cutover 保持延期。
