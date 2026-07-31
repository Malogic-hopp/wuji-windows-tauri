# WUJI v2 迁移实施状态

状态：实施状态记录（不定义设计）
基线日期：2026-07-18
最近更新：2026-07-31（Heatmap 周导航、防串周语义与 v0.1 日常功能演进纪律对齐）
本次核对方式：仓库结构与源码检查 + 本轮提交前根 workspace `fmt`/`check`/`clippy`/`cargo test --workspace` 实跑；2026-07-22 的旧 .NET 与 soak 结果仅保留为历史证据，不代表覆盖本轮工作区
当前实施依据：[09-Tauri-Rust-Rebuild-v0.1实施基线.md](./09-Tauri-Rust-Rebuild-v0.1实施基线.md)（含 §16 审核整改增补合同）
审核报告：[Rebuild-v0.1-代码与验收审核报告.md](./Rebuild-v0.1-代码与验收审核报告.md)
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

WUJI 旧系统（WPF App、C# Agent、.NET Bridge 与 React/Tauri 过渡 dev shell）已于 2026-07-29 经产品负责人决策从仓库整体移除，`rebuild/` 目录同日扁平化至仓库根。仓库当前只保留 Rebuild v0.1 目标链路 React → Tauri Rust Host → Rust Agent → SQLite（根目录 `apps/`、`crates/`）与根目录 `rebuild-*.ps1` 入口脚本。批准例外、远程可达冻结提交与恢复方式见 [ADR-003](./ADR-003-Rebuild-only仓库转换与旧系统源码退役.md)。

按当前第四阶段整改计划核对后的状态：

- **阶段 4.1–4.4 已复审通过。** Settings 恢复/双槽备份、可靠 Barrier、唯一 CaptureCoordinator、control/ack 有界失败、pipeline supervisor、Settings effectivity/revision 一致性已带确定性回归；P1-04 已关闭。阶段 4.4 最终基线为 workspace 265/265，关键 E11c 连续 10/10，Agent 残留 0/0。
- **阶段 4.5 自动化实现与第六轮缺口补修完成，等待复审；P1-05/S2-03 尚未完整关闭。** Lock/Sleep/Unlock/Resume 已通过唯一 Coordinator 形成 desired + per-source suppression + effective gate；隐藏顶层窗口在 `WM_NCCREATE` 安装上下文，受监督 pump/bridge/consumer、事务式启动回滚、可靠 Barrier/Writer ack、首次事件时间重试与有界 shutdown 均已接入。L01–L20、Windows pump/bridge 启动失败、shutdown stop 失败/pump timeout/bridge timeout/consumer timeout/panic 已有确定性测试。真实 Windows 锁屏/休眠人工验收仍为关闭条件，不能仅凭自动门禁关闭 S2-03。当前说明见[阶段 4.5 完成说明 §15](./下一步计划-2026-07-23-Rebuild-v0.1第二轮审核第四阶段整改/阶段4.5-完成说明-2026-07-27.md#15-第六轮复审缺口补修2026-07-29)。
- **V01-8 仍为重新打开，但当前工作区 package 已重验收。** 2026-07-30 已从新根目录完成 release、React/Tauri/NSIS、静默安装、固定 Agent 布局、禁资产、安装版 Desktop→AgentController→安装目录 Agent 启动、隔离 bootstrap、manifest 与旧 prod/dev 数据库 checksum 前后不变门禁。8 小时 soak 仍只有 2026-07-22 的历史证据；当前工作区尚未重跑，09 §12.2 与 disk-full 人工项也仍 Pending。
- 09 基线仍为 Draft，已补 §16 审核整改增补合同（last-known-good、watermark、IPC 副作用、checkpoint busy、soak 判据、Schema 增补）；按审核 §10，v0.1 当前只宣称**实现完成**，不宣称验收完成。
- 旧系统（WPF/C#/Bridge）已于 2026-07-29 从仓库移除（提前于 G-RETIRE，由产品负责人按 ADR-003 批准；归档与回滚来源为两项远程可达冻结提交）；Rebuild 不接管 production channel；旧 `.NET` 回归入口随移除失效，2026-07-22 的 `dotnet restore/build/test` 结果仅作历史证据。用户机器上的旧数据库（prod/dev channel）未受影响，仍按 09 §12.3 只读保护。

ADR-002 仍为 Proposed，01–08 仍为 Draft；Fact Boundary、Generation/Result Set/Snapshot、Identity Resolution、Lease/GC、production binary/session 认证、Importer 和旧系统退役继续作为长期 Design only，不阻挡 dev-only v0.1，但在未来 production cutover 前仍需重新进入对应门禁。

## 3. 仓库证据摘要

- 旧系统仓库资产（`src/`、`tests/`、`tools/`、`contracts/`、`publish/`、`QuantifiedSelf.Windows.sln` 及过渡 Tauri dev shell）已于 2026-07-29 移除；移除前的结构与实现证据见 git 历史；
- Rebuild 目标实现位于仓库根目录（`crates/wuji-core`、`crates/wuji-storage`、`crates/wuji-windows`、`apps/agent`、`apps/desktop`），门禁证据见 [evidence/v0.1](./evidence/v0.1/)。

## 4. 能力迁移矩阵

本矩阵同时保存 v0.1 当前目标和长期目标；标为“长期”的行不是 v0.1 阻断项。

| 能力/边界 | 当前状态 | 当前证据 | 目标 | 主要差距 | 下一门禁 |
|---|---|---|---|---|---|
| v0.1 实施基线 | Design only | [09](./09-Tauri-Rust-Rebuild-v0.1实施基线.md)（Draft + §1.1 演进纪律 + §16 增补）定义范围、运行/算法/协议合同、阶段和验收；Heatmap 固定命令、DTO 与 UI 合同已同步；[DDL](../../crates/wuji-storage/schema/schema.sql) 可执行且已内嵌 | dev-only bridge-free React/Tauri/Rust Agent/SQLite 链路 | 合同 Draft 的正式接受留待产品评审 | 产品评审接受 |
| 产品语义与指标 | Design only | [01](./01-产品语义与指标词典.md) 为 Draft | Accepted 的 Observation/Activity/Context/Work/质量/时区词典 | 产品接受、延期项和候选阈值尚未签署 | G-ADR / ALG golden review |
| 领域模型 | Design only | [02](./02-行为分析领域模型.md) 为 Draft | 事实、派生、Generation、Result Set、Snapshot 不变量可执行 | 尚无 Rust 类型与属性测试 | DOM-001–005 |
| 目标架构 ADR | Blocked | [ADR-002](./ADR-002-React-Tauri-Rust目标架构.md) 状态 Proposed | Accepted 并取代当前过渡 ADR 的最终架构 | 依赖规范尚未形成 Accepted 基线 | G-ADR |
| React 19 UI 基座 | Implemented | `apps/desktop`：Today/Timeline/Heatmap/Settings/Diagnostics 五页 + 四态 + 顶栏 Agent 控制 + 令牌主题 + forced-colors 适配；71 项 Vitest 通过；Timeline 日期使用 DB reporting 时区（R08），今天 5s 轮询整体重取（UI 不分页，最新在顶部，含到底/到顶悬浮按钮；超长一天明确截断提示；请求 generation 防串日 + 同目标防重入），支持 ?date/?hour 定位与日期导航、历史日期静态不轮询、小时定位排除切换标记并按查看日期裁剪；Heatmap 7 天 × 24 小时固定日期轴读 hourly 投影、强度 Rust 归一化 0-4（days 校验下沉 Reader）、?week= 周翻页（真实 `today` 与 `rangeEndLocalDate` 分离，`weekOffset` 仅允许 -520..0 并下沉 Reader，非法/未来 URL 规范化回本周，历史周静态不轮询且不伪装今天/现在标记），请求以 target + generation 防 A→B→A 串周，格子 Enter/Space/点击跳转时间线对应日期小时（UI-005），HC 五级系统色可区分；Diagnostics 时间基准随轮询更新（R09） | Today/Timeline/Heatmap/Settings/Diagnostics 使用 09 v0.1 DTO；09 §8.3/§8.4/§10 已同步 | 手工矩阵（尺寸/DPI/HC/键盘/读屏）Pending | 手工矩阵 |
| Tauri 2 Desktop shell | Implemented | `apps/desktop/src-tauri`：IPC client（副作用 timeout 后同 ID 重试）、Query、Settings CAS（损坏文件上报、resync appliedRevision 取自 DB MAX）、detached Agent 控制、托盘、单实例、13 语义命令、集成测试 | 直接使用 Rust Query/IPC/Settings/Process Controller | — | V01-8 重验收 |
| Bridge-free Tauri | Verified（2026-07-30 package 基线） | 2026-07-30 从新根目录实跑 dev 包脚本：固定 Agent 布局、包内无 Bridge/.NET/旧合同、Agent 二进制 byte 级一致、manifest 含版本+SHA-256、安装版 Desktop 经 package-smoke test channel 调用同一 AgentController 拉起安装目录 Agent；普通 Desktop 启动不自动拉起 Agent；旧 prod/dev 数据库 checksum 前后不变。本轮未改变目录、bundling、sidecar 或 release path，未重跑 package | 安装包与运行时不含 `.NET Bridge` | package 基线已通过；8h soak 与人工矩阵仍待重跑 | V01-8 收口 |
| Rust workspace / `wuji-core` | Verified | `crates/wuji-core`：schema 对齐领域枚举、Settings 默认值/验证/digest、21 个稳定错误码（含 `as_str`/`from_code`）、固定命名空间、DTO + specta branded TS drift 门禁（Int64String 品牌 + crate/desktop 双副本一致性，R07）；`cargo test -p wuji-core` 通过 | 纯领域、Settings、Privacy、Analytics、Protocol、Error | 长期 Privacy/Analytics 部分待后续版本 | 持续回归 |
| Rust `wuji-storage` | Verified | `crates/wuji-storage`：唯一内嵌 DDL（含 `settings_revisions.content_json` last-known-good）、bootstrap 自检、Writer 行操作、触及桶重算、只读 Reader、Today SUM 聚合与 drop event_count 修正（R02）、checkpoint busy 结果行判定（§16.4）；21 项测试通过（含 21+ 应用/跨午夜/DST/幂等/恢复） | v0.1 Single Writer、只读 Query、空库 bootstrap 和最小 projection | — | 持续回归 |
| Rust Agent binary | Partial | `apps/agent`：双 lane Writer、可靠 Barrier/唯一 Coordinator、Settings 恢复/effectivity、CommandServer、心跳、MaintenanceLite、reconciler、单实例与启动恢复已接入；阶段 4.5 已加入 Lock/Sleep 双 suppression、Unlock/Resume 恢复、monitor fault、事务式事件链启动与有界 shutdown | 独立 Rust Agent 长期进程 | 阶段 4.5 自动化补修等待复审；真实 Lock/Sleep 人工验证 Pending | 阶段 4.5 复审 + 人工门禁 |
| Rust Win32 Capture Adapter | Partial | `crates/wuji-windows` 的 foreground/process/idle 字段级降级适配器已有测试；session/power 使用隐藏顶层窗口 + 自定义 WndProc，`WM_NCCREATE` 安装上下文后再注册 WTS；pump 支持结构化 stop、thread-id `WM_QUIT` fallback、RAII exited、启动失败有界回滚与只在 finished 后 join | v0.1 Rust foreground/process/idle adapter + 可靠 session/power adapter | 自动测试证明启动、启动失败回滚、正常关闭与故障编排；真实 Windows 电源广播可达性仍需人工 Lock/Sleep | 阶段 4.5 复审 + 人工门禁 |
| 隐私内存边界 | Implemented | 排除进程名只存在于入站消息生命周期；DB/WAL/DTO 字节级 canary 扫描测试通过（v0.1 不写日志文件，stderr 仅静态中文安全串）；长期 SEC-002 仍 Design only | 原始标题/路径在 Rust Agent 持久化前过滤 | production 威胁模型审查属长期 | 长期 |
| SQLite v0.1 Schema | Implemented | [schema.sql](../../crates/wuji-storage/schema/schema.sql) 为唯一 DDL 并已编译期内嵌；空库执行、STRICT/FK/CHECK/单 open 行/WAL 经探针与临时库集成测试验证 | 内嵌同一 SQL 从零创建独立 dev DB | — | 持续回归 |
| SQLite 长期 Schema | Design only | [04](./04-SQLite-v2与持久化读模型.md) 有完整逻辑字段 | production migration + manifest | v0.1 明确延期 | 后续 G-DDL |
| Fact Cursor | Design only | 02/04 定义数据库全局水位 | 与事实同事务、跨 runtime 的持久水位 | 当前模型仍以旧 Sample/Session 与 Tick 流程为主 | DOM-001 / DB-005 |
| Segmentation Generation | Design only | 02–04 定义 | Rust staging + immutable Segmentation Result Set | 无代码、表、job 或发布器 | M5 / DB-006–008 |
| Work Generation | Design only | 第二轮修订已与 Context 解耦 | 独立 Work Profile/Generation/Result Set | 无实现与解耦回归 | DOM-004 / ALG-003 |
| Analysis Generation | Design only | 02–04 定义 Context/Event 世代 | 规则版本化、可重建、可解释 | 无规则引擎、evidence 或黄金样本实现 | ALG-002/004 |
| Result Set / Query Snapshot Slice | Design only | 02/04/05 定义具体组件 FK、复合 Fact Boundary、空 Snapshot 和不可变 Slice | W0/W1/W2 原子发布、Projection→具体 Set 一致、稳定读取 | 无 Schema、Publisher、Query Service、GC | DB-006–010、DB-014–016 |
| Identity Resolution | Design only | 01–04 定义跨世代可信 Link 与不可变 Resolution Generation | Apps/Top Apps/Hourly/Daily 按固定 canonical identity 聚合 | 无表、映射器、App 投影或同名分离 UI | DOM-006 / M5 |
| 小时/日持久化读模型 | Implemented（v0.1 形态） | `wuji-storage` hourly/daily projection + 触及桶重算幂等；Today/Timeline/Heatmap 读模型查询；04 的长期表族仍 Design only | Today/Trends/Heatmap 不扫描 Observation | 长期表族与 Generation 发布属 M5 | M5 / PERF-001–002 |
| Named Pipe v2 | Partial | `wuji-windows/pipe.rs` 同用户 DACL + agent CommandServer：hello 全字段校验、严格 UTF-8/ULID/sentAt、逐命令 payload、64 KiB/3s、副作用不取消 + request ID 幂等、Capture 状态机、稳定错误码、e2e 覆盖 | DACL + Desktop binary/signature manifest + 内存 session capability、版本握手、幂等 receipt | production binary 认证与 capability 属于长期（09 §8.1 已延期） | 长期 |
| 可信原生确认 | Design only | ADR-002、06、08 已冻结 React 无 token/consume 能力 | Clear/导出/隐私削弱由 Win32 原生确认后在 Rust 同流执行 | 当前目标 command/TrustedActionCapability/proof 均未实现 | SEC-003 / M6–M7 |
| Process/Capture 生命周期分离 | Implemented | React/Tauri 暴露 `capture_start/pause/resume` 与 `agent_process_stop`：`capture_start` 在 Host 内先确保 detached Agent 在线再开始采集；Pause 只暂停 Capture；`agent_process_stop` 依次提交 CaptureStop 边界、请求 `agent_shutdown`、确认 Pipe 断开与 runtime stopped。Desktop 退出后 Agent 存活有进程级 e2e 断言 | Capture Pause 与 Agent Process Stop 语义分离 | — | V01-8 重验收 |
| Settings Revision/Profile/Effectivity | Partial（v0.1 代码已复审） | v0.1：CAS + saved/applied 分离 + crash-consistent 双槽恢复 + 单调 revision/digest 对账 + 唯一 Barrier 生效边界 + reconciler；4.4 的 Processor/Writer 统一 revision 防线与真实拓扑 effectivity 测试已复审通过。Profile/Effectivity Interval 属长期 | Desktop 单写，Agent 对账，按边界后首条事实采用新 revision | v0.1 当前代码仍待 4.7 package/soak；Profile/Effectivity Interval 按 09 延期 | 阶段 4.7 / 长期 |
| 数据库 pointer / reader lifecycle | Design only | 04/06 定义版本文件、pointer、DatabaseReady | Windows 可恢复 major migration 切换 | 无 trusted pointer/migrator/reader close 实现 | DB-011 / REL-003 |
| v1→v2 importer | Not started | 04/07 只有规则 | 离线、幂等、可恢复的导入与 Legacy Summary | 无 fixture、import job、报告或工具 | M8 / DB-012 |
| Shadow / parity | Not started | 当前有 Bridge 阶段页面/lifecycle parity 脚本 | 同输入或 v1 快照的 v1/v2 语义/守恒比较 | 没有 Rust v2 输出，无法开展目标 parity | M8 / Parity gate |
| dev v2 cutover（v0.1 dev 链路） | Implemented | rebuild dev 链路桥接自由运行（dev-only，不等于 G-DEV 的正式 cutover） | dev 默认 bridge-free Tauri + Rust Agent | 正式 G-DEV 仍 Blocked（见下行） | V01-8 重验收 |
| 正式 dev cutover（G-DEV） | Blocked | 旧 dev shell（依赖 Bridge）已随旧系统移除；rebuild dev 链路 bridge-free 但 V01-8 未重验收 | dev 默认 bridge-free Tauri + Rust Agent | M1–M8 尚未完成 | G-DEV |
| prod v2 cutover | Blocked | 旧 C#/WPF 参考资产已移至 git 历史；用户机器上的 prod 安装与数据库未受影响 | prod canary 后 Rust v2 默认 | G-DEV、canary、恢复与产品批准均未完成 | G-PROD |
| 旧系统退役 | Partial | 仓库内旧系统资产（WPF/C# Agent/Infrastructure/Application/Client/Bridge、合同与工具）已于 2026-07-29 按 ADR-003 提前移除；两项远程可达冻结提交为源码归档；旧安装与旧数据库仍保留 | 停产、归档后按依赖顺序移除 | G-RETIRE 联合评审未进行；v1 数据导入（M8）与生产侧退役未完成 | G-RETIRE |

## 5. 长期阶段状态

以下 M0–M11 只用于长期 production 路线；v0.1 当前进度使用 09 的 V01-1–V01-8。

| 阶段 | 状态 | 已有内容 | 未满足的退出条件 |
|---|---|---|---|
| M0 设计与测试基座 | Partial | 01–08、ADR-002 和三轮审核回应已形成 Draft 集；当前仓库有 rebuild 多层测试基座 | 文档尚未 Accepted；Schema manifest、正式 golden fixture/证据模板和安全威胁 fixture 未落地 |
| M1 Rust Workspace/Core | Not started | `wuji-core` 已接入 rebuild workspace（见 §4） | `wuji-core` 边界、领域类型、错误/协议和测试的长期范围 |
| M2 Win32 Capture Adapter | Not started | C# Win32 实现可从 git 历史取得作行为参考 | Rust adapter、隐私边界和 Windows 故障测试的长期范围 |
| M3 SQLite v2 基座 | Not started | 只有逻辑设计 | manifest、DDL、migration harness、Fact Cursor、pointer |
| M4 Rust Agent 流水线 | Not started | C# Agent 可从 git 历史取得作参考实现 | 独立 Rust binary 和完整异步流水线的长期范围 |
| M5 派生与查询发布 | Not started | 只有领域/存储设计 | Generation、Result Set、Snapshot、读模型和查询服务 |
| M6 IPC 与 Settings | Not started | 旧 C# IPC/Settings 和 Bridge 可从 git 历史取得作兼容参考 | Named Pipe v2、revision/effectivity、稳定 DTO 的长期范围 |
| M7 Tauri 去 Bridge | Not started | rebuild `apps/desktop` 已 bridge-free | 直连 Rust v2、目标页面与 bridge-free package 的长期验收 |
| M8 v1 导入与 shadow | Not started | 旧 Bridge parity 工具已随旧系统移除（git 历史可查） | importer、Legacy、目标 shadow/parity 报告 |
| M9 dev cutover | Blocked | dev channel/preview 隔离经验可复用 | G-DEV 前置全部未完成 |
| M10 prod canary/cutover | Blocked | 旧系统参考/回滚来源为 git 历史 | G-PROD 前置全部未完成 |
| M11 旧系统退役 | Partial | 仓库内旧系统资产已于 2026-07-29 移除（git 历史归档）；旧数据库仍只读保留 | 生产稳定期、覆盖接替、恢复归档与明确批准 |

## 6. 当前测试与验证状态

| 验证项 | 仓库能力 | 本基线结果 | 说明 |
|---|---|---|---|
| Rebuild `cargo fmt --all -- --check` | 命令存在 | Passed（2026-07-31，本轮提交前工作区） | 本轮提交前工作区通过 |
| Rebuild `cargo check --workspace --all-targets` | 命令存在 | Passed（2026-07-31，本轮提交前工作区） | 0 errors |
| Rebuild `cargo clippy --workspace --all-targets -- -D warnings` | 命令存在 | Passed（2026-07-31，本轮提交前工作区） | 0 Clippy warning |
| Rebuild `cargo test --workspace` | 命令存在 | Passed（2026-07-31，308/308） | 普通用户权限全量通过；Agent E2E 8/8，测试前后 Agent 残留 0；受限环境不能写 `%LOCALAPPDATA%`，不作为代码失败；真实 Lock/Sleep 仍 Pending |
| Rebuild Desktop `pnpm typecheck` / `pnpm lint` / `pnpm test` / `pnpm build` | package scripts 存在 | Passed（2026-07-31，Vitest 71/71、零警告、dist 产出） | 含 R07 品牌夹具、R08 时区、R09 诊断、R10 切换间隔、Timeline 防串日，以及 Heatmap 范围锚点/周边界/A→B→A 防串周断言 |
| Rebuild dev package（整改后脚本） | `scripts/build_dev_package.py` | Passed（2026-07-30，目录整理与打包路径基线） | release、React/Tauri/NSIS、静默安装、固定布局、禁资产、Agent byte 一致、安装版启动、manifest、旧 prod/dev 数据库 checksum stable 全部通过；本轮无目录/bundling/sidecar/release-path 改动，未重跑 package；生成产物位于被忽略的 `target/` 与 `dist/`，不提交二进制 |
| 8 小时 soak（整改后脚本） | `scripts/soak.py` | Historical Passed（2026-07-22）；当前工作区 NotRun | 历史 verdict=pass 仅作脚本/旧基线证据；4.7 对最终代码重新执行 |
| 09 §12.2 手工门禁（锁屏/休眠、30 分钟对照、尺寸/DPI/主题/键盘/读屏、离线历史显示） | 手工流程 | Pending | 只能人工执行；未执行完不得声称 v0.1 验收完成 |
| 旧 .NET `dotnet restore` / `build` / `test` | 已移除（2026-07-29） | Historical Passed（2026-07-22：restore 成功；build 0 错误 0 警告；test 失败 0） | 回归入口随旧系统移除失效；结果仅作历史证据 |
| 08 的 G-ADR/G-DDL/G-DEV/G-PROD/G-RETIRE | 文档已定义 | Blocked | 尚无 Gate 证据包和批准记录 |

## 7. 旧系统资产处置记录与仍受保护资产

2026-07-29，经产品负责人决策，旧系统仓库资产提前于 G-RETIRE 从仓库移除。决策边界、冻结提交和安全恢复流程见 [ADR-003](./ADR-003-Rebuild-only仓库转换与旧系统源码退役.md)。移除范围：

- `src/`（WPF App、C# Agent/Agent.Runtime、Infrastructure、Core、Application、Client、Client.Bridge、过渡 Tauri dev shell）；
- `tests/`、`tools/`（含 Bridge ContractGen 与 parity 工具）、`contracts/`（Bridge 合同）、`publish/`，以及旧系统专用的 `scripts/` 历史分析/清理工具；
- `QuantifiedSelf.Windows.sln`、`Directory.Build.props`、`global.json`、`nuget.config`；
- 旧系统专属文档（WPF 架构/手册与旧 design/devlog/fixes/qa/project/prompts）；过渡架构（C# Bridge + React）文档归档至 [archive/](./archive/)。

移除后仍受保护、不得删除或修改：

- 用户机器上的旧数据库与运行时数据（prod `%LOCALAPPDATA%\WUJI`、dev `%LOCALAPPDATA%\WUJI-Dev`）：v1 导入与恢复支持期限内长期保留，`build_dev_package.py` 的 09 §12.3 checksum 一票否决继续生效；
- git 历史中的 v1 Schema、脱敏 fixture 和最后兼容 artifact：v1 导入设计（M8）的唯一来源；
- rebuild 目标实现与证据：`apps/`、`crates/`、`scripts/`、`dist/` 与 `docs/dev/`（含 evidence 与 archive）。

不得让 rebuild dev 链路默认接管 prod channel。

## 8. 下一步可执行清单

v0.1 剩余事项（按审核 §10 的 V01-8 重新关闭准入）：

1. 复审[阶段 4.5 第六轮缺口补修](./下一步计划-2026-07-23-Rebuild-v0.1第二轮审核第四阶段整改/阶段4.5-完成说明-2026-07-27.md#15-第六轮复审缺口补修2026-07-29)，并执行真实 Lock/Unlock/Sleep/Resume 人工验收；自动化通过不能替代该门禁；
2. 完成 4.6 真实拓扑/E2E 稳定化后，在 4.7 对最终工作区重新构建安装包并归档 `package-validation.json`；
3. 在 4.7 对最终工作区重新执行 8 小时 soak 并归档脱敏 `soak-summary.json`；
4. 完成 09 §12.2 手工门禁：真实锁屏/休眠、30 分钟受控对照、960×640/1280×800、100%/150%/200% DPI、Light/Dark/High Contrast、键盘导航与读屏、离线历史显示（全部 Pending）；
5. 完成 disk-full 手工注入核对（busy/corruption/checkpoint 已自动覆盖；Pending）；
6. 全部自动与手工项关闭后复核 `migration-status.md` 内部一致，才允许重新声明 Rebuild v0.1 验收完成；09 Draft 的正式接受仍留待产品评审。

长期方向（v0.1 验收后不自动启动）：按 09 §14 选择 v0.2/v0.3/v0.4；长期 manifest、Importer、Snapshot/Lease 和 production cutover 保持延期；旧系统回滚来源为 ADR-003 冻结提交。

期间不得修改旧数据库；旧 C#/WPF/Bridge 仓库资产已于 2026-07-29 移除，如需参考必须按 ADR-003 从冻结提交恢复到独立目录。
