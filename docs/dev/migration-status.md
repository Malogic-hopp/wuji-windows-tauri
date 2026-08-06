# WUJI v2 迁移实施状态

状态：实施状态记录（不定义设计）
基线日期：2026-07-18
最近更新：2026-08-03（统计主页（10/11）阶段一、二实施收口，见 §2。2026-08-01 记录：第二轮审核 P1 整改与评审优化：启动决策三态枚举且 package-smoke 优先只拉起不自动开录、Settings 与 Desktop 偏好按 dirty 状态独立提交、托盘按进程状态分别建模且故障态保留停止、偏好 I/O 错误与文件缺失区分且严格拒绝未知字段；启动动作升级为 Coordinator 内原子的内部 `capture_ensure_recording`（Stopped→开始/Paused→恢复/Running→幂等），启动结果经 `auto_start_status` 顶栏可见化，文案改为“启动吾迹时自动开始记录”；第三轮复审补修：ensure 区分 lifecycle monitor 永久故障（显式失败）与 Lock/Sleep 临时抑制（Ok(Paused) 自动恢复）、手动重试成功清除失败提示、打包烟测真实断言 smoke 保持 capture_state=stopped 且零 Observation，全部对齐 09 §8.2/§8.3/§9.3/§9.4）
本次核对方式：仓库结构与源码检查 + 本轮提交前根 workspace `fmt`/`check`/`clippy`/`cargo test --workspace` 实跑；2026-07-22 的旧 .NET 与 soak 结果仅保留为历史证据，不代表覆盖本轮工作区
当前实施依据：[09-Tauri-Rust-Rebuild-v0.1实施基线.md](./09-Tauri-Rust-Rebuild-v0.1实施基线.md)（含 §16 审核整改增补合同）
审核报告：[Rebuild-v0.1-代码与验收审核报告.md](./archive/历史审核/Rebuild-v0.1-代码与验收审核报告.md)
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
- **阶段 4.5 自动化实现与第六轮缺口补修完成，2026-08-04 独立复审通过（假测试排查 + 逐路径核对 + 实跑门禁全绿）；真实锁屏→恢复、睡眠→唤醒人工验收通过；P1-05/S2-03 的阶段 4.5 部分关闭。** Lock/Sleep/Unlock/Resume 已通过唯一 Coordinator 形成 desired + per-source suppression + effective gate；隐藏顶层窗口在 `WM_NCCREATE` 安装上下文，受监督 pump/bridge/consumer、事务式启动回滚、可靠 Barrier/Writer ack、首次事件时间重试与有界 shutdown 均已接入。L01–L20、Windows pump/bridge 启动失败、shutdown stop 失败/pump timeout/bridge timeout/consumer timeout/panic 已有确定性测试。真实 Windows 锁屏/休眠人工验收仍为关闭条件，不能仅凭自动门禁关闭 S2-03。当前说明见[阶段 4.5 完成说明 §15](./archive/第四阶段整改-2026-07-23/下一步计划-2026-07-23-Rebuild-v0.1第二轮审核第四阶段整改/阶段4.5-完成说明-2026-07-27.md#15-第六轮复审缺口补修2026-07-29)。
- **V01-8 仍为重新打开，但当前工作区 package 已重验收。** 2026-07-30 已从新根目录完成 release、React/Tauri/NSIS、静默安装、固定 Agent 布局、禁资产、安装版 Desktop→AgentController→安装目录 Agent 启动、隔离 bootstrap、manifest 与旧 prod/dev 数据库 checksum 前后不变门禁。8 小时 soak 已重跑通过（2026-08-05，480 分钟全判据、旧库 verified_stable）；09 §12.2 手工门禁已通过（锁屏/睡眠、30 分钟对照、键盘/读屏、离线历史；尺寸/DPI/HC 按用户决定跳过）；disk-full 人工项仍 Pending（可选）。
- 09 基线仍为 Draft，已补 §16 审核整改增补合同（last-known-good、watermark、IPC 副作用、checkpoint busy、soak 判据、Schema 增补）；按审核 §10，v0.1 当前只宣称**实现完成**，不宣称验收完成。
- **统计主页（10 设计 / 11 实施方案）阶段一、二已实现并收口。** 阶段一（DTO + 枚举 + 纯函数 + specta 双副本 drift 门禁）与阶段二（`ReaderSnapshot` 读事务快照 + 7 个统计投影原语）已通过各自 DoD：`cargo test -p wuji-core` 57 项、`cargo test -p wuji-storage` 49 项全绿（阶段二原有 48 + 阶段三 cutoff 计数器回归 1；另有 `compile_fail` doctest 1）；DST 三类（含 fall-back 无匹配取较早、歧义窗口先断言防假通过）、cutoff LEFT JOIN 零活动日、**重复日期去重（不翻倍不归零）**、工作块跨午夜/未闭合计数（与 `recompute_dates` 统一相交口径）、惯性有效日（`daily_work_metrics` 口径 + gap-only 日计入分母）、WAL 写并发同快照一致、行类型跨 crate 导出纠错等回归齐备。
- **统计主页阶段三（QueryService + 双命令）已通过复审收口。** `stats_assembly`（固定槽位分配 + 日/周构成桶聚合）与 `stats_get_home`/`stats_get_status` 两命令已接入（命令总数 15 → 17）：命令级 `ReaderSnapshot` 读事务快照、**命令级单批次 cutoff**（`build_cutoff_plan` 统一收集今日/昨日/近 7 有效日/上周同周序日，一次 `stats_cutoff_series` 建索引供 live/周进度/月度/今日趋势点共用——修复了此前 4 次 cutoff 调用、8 条 SQL 的 P1；`ReaderSnapshot` 快照级计数在全部子查询完成后与同一快照 DTO 原子返回，不使用跨命令共享状态；home/status e2e 断言每命令恰 1 次，0→1→2 单测证明第二次调用可被门禁发现）、统一 daily 超集（摘要与 `days` 无关）、周进度公式（仅今日/上周同周序日 cutoff）、月度当前月口径（recordedDays/均值不含今日）、轻量轮询响应含报告时区 localDate 且不含摘要。**固定时钟注入**（`stats_home_at`/`stats_status_at(now)`）：全部阶段三测试用固定 UTC 时刻（上海 12:00，会话全饱和）→ 断言精确无容差、无跨午夜错位，另补跨午夜边界用例（查询 07-19 00:00 本地 → 报告日期换日）。**五态全部确定性钉住**：Up（今日长于昨日同起会话 +33%）、Down（今日起始晚于昨日 -50%）、Stable（同墙钟会话）、UpFromZero（昨日 gap-only）、Unavailable/InsufficientSamples（空库）；上周同期公式精确值（完整日前缀 18000s + 同周序日 cutoff 0）、月度总量含今日 cutoff/均值排除今日均有数值断言；`CutoffIndex` 缺键显式内部错误（0 是合法业务值不静默伪装）。`compile_fail` doctest 证明快照存活期间 Reader 本体不可再借用（E0502）。最终 `cargo test --workspace` 411 项、0 失败，阶段三 DoD 已全部勾选。
- **统计主页阶段四（前端静态布局 + fixture）已实现，待复审。** 全部图表组件（StatusCard/TrendChart/WeeklyChart/WeekProgressCard/InertiaCurve/AppComposition/Milestones）为纯 CSS/SVG 展示、props 只接 DTO 子集；`statsModel` 纯函数（五态文案含 upFromZero"新增 N 分钟"、摘要双窗口句式、槽位→令牌、覆盖标签、紧凑时长）；fixture 覆盖正常/空状态/惯性 reliability null/全零曲线/firstRecordedMonth null 等边界；路由 `/` → 统计主页、`/today` → 今日，导航新增"主页"置首；`bridge/client.ts` 已加 `statsGetHome`/`statsGetStatus` 签名（暂不调用）；图表令牌（`--chart-app-1/2/3`、`--chart-other`、`--chart-in-progress`、`--chart-no-data`、`--chart-ref-line`、`--chart-ma-line`）三主题块齐备，forced-colors 用系统色保留可区分性。门禁：`pnpm typecheck`、`pnpm lint`（max-warnings 0）、`pnpm test` 108 项（15 文件）、`pnpm build` 全绿。手工矩阵（浅/深/HC/DPI/键盘）为阶段四 DoD 手工门禁，待人工执行。阶段四按实施评审整改收口：今日趋势柱"截至 HH:MM"标签、当前周两段式（实心已完成 + 今日弱化）与 completedRecordedDays=0 提示"本周进行中，暂无稳定参考"、图表柱/段键盘 focus（tabIndex + :focus-visible）、构成图例槽位色（color 而非 borderColor）、forced-colors 按槽位映射系统色（Highlight/CanvasText/GrayText 保留条纹与边框，不再抹平）、缺数据日最小斜纹占位（与"有记录但活跃 0"区分）、30 天周桶 fixture（含当前不完整周）+ stable 3% 边界入 fixture 本体、③ 长期收敛为组合卡（一张卡内左右分区，区别于 ④ 双列独立卡）、惯性补缺失日期数与 6/12/18/24 小时刻度、上周参照锚点、切换器作用范围提示与 hover、柱 hover 提亮、双列卡窄屏塌缩、图表高度三档收敛、主数字 tabular-nums。门禁 `pnpm typecheck`/`lint`/`test` 112 项/`build` 全绿。复审收口补修：当前周两段式高度按**当前周总量归一**（槽高=总量/max、段内按总量分摊——修复双缩放导致柱顶空档的比例失真；completedRecordedDays=0 时整柱为今日弱化，不再退化消失；两段均 0 时不渲染避免 2px 细线；新增高度回归测试锁定 87%/76%/24% 与 100% 退化场景）；当前周 tooltip 改用 formatDeltaMs（不再暴露原始毫秒）；WeekProgressCard 去掉内层 `.card`（③ 组合卡单一卡片视觉）；30 天视图 fixture 日期改用 shiftLocalDate 生成合法日期（isToday 落在 07-18）；空 fixture 月度月份修正为 2026-02..07（当前月 07 与 localDate 一致）；惯性刻度改为按列对齐（hour/24×100% 绝对定位）；月度补可见"当前月进行中"图例（与趋势/周图一致）；惯性 hover 提亮收敛为单柱（不再整图提亮）。复审收口补修（几何与可访问性盲区）：①**百分比高度移至柱体 inline、槽位恒 100%**（趋势/历史周/月度柱不再退化成 2px，几何可直接 DOM 断言——jsdom 不解析 CSS 布局，故由槽位百分比改为柱体百分比，P1-01）；②构成桶级可访问语义（桶 tabIndex + aria-label 携带进行中"截至 HH:MM"/当日无记录数据，缺数据桶可见斜纹占位，P1-02）；③纵轴纳入叠加数据（趋势 max 含均线值、周图 max 含参考值×7——均线高于全部柱不再负 y、参考值高于历史最大周按真实相对高度不截断，P2-01）；④fixture 口径自洽（当前周总量=已完成 45M+今日 13.32M=58.32M、当前月 recordedDays=17 且总量含今日、里程碑 138 天 ≤ 3/1-7/17 自然日 139，新增 statsFixture 合同测试 5 项，P2-02）。门禁 `pnpm typecheck`/`lint`/`test` 123 项/`build` 全绿。UI 布局外审收口（`docs/dev/11-统计主页实施-阶段四-UI布局-review.md`）：P0 惯性"有效天数（缺失 N 天）"改"**有效样本日 N/M（N 天未纳入）**"（记录日≠惯性有效样本，不再暗示缺记录）；P1 两张趋势图标题右侧加**轻量数值锚点**（趋势"日均 3h30m · 最高 4h53m"随 7/14/30 切换同步、周图"周均 18h3m"排除进行中周），惯性卡加副标题"按有效样本日平均的 24 小时活跃分布"，双列卡比例 0.95/1.05 → **0.88/1.12** + 惯性图高 160→140px，周参考线"参考值"改"**本周日均推算**"+悬停给出具体推算值（"按已完成记录日的日均值推算：约 17h30m"）、图例删"上周参照"（柱顶标注保留）；P2 长期记录"近 6 月月度活跃（每有效日均值）"从图下移到**图表上方**；代码层：`activeDays` 默认固定 14（与返回点数解耦）、30 天 fixture 分支加 TODO（接后端前移除）、构成段比例改 **flex-grow 按原始毫秒权重**（消除逐段四舍五入累计误差）、构成段级 tabIndex 移除（每桶仅一个焦点，aria 补总时长）、顶栏主题按钮按**实际生效主题**（prefers-color-scheme）决定文案。门禁 `pnpm typecheck`/`lint`/`test` 126 项/`build` 全绿。UI 布局外审二轮收口（`11-统计主页实施-阶段四-UI布局-review-2.md`）：①**切换器方案 A 解耦**——7/14/30 只控制活跃趋势，应用构成固定近 14 天（不再"局部位置、跨区块生效"；构成卡内部范围控制留待后续）；②**主卡排版收紧**——工作块并入截止行（"截至 15:20 · 8 个工作块"），本周进度改"本周累计"标签 + 22px 主数字（低于今日 30px），upFromZero 比较改紧凑格式"新增 16h12m"（弃长格式"新增 16 小时 12 分钟"）；③**构成日期移到条形左侧**（Grid 三列 date|bar|total，沿左侧纵向扫描），无记录日总时长显示 "—"（区别于有记录零活跃的 0m）；④**双列等高**（align-items: stretch，底边对齐）+ 惯性图高 140→170px + 横轴补 0 刻度 [0,6,12,18,24]；次级：应用图例**文字中性色**（色块类别色走 --chip-color 变量、文字统一 --text-dim，HC 仍系统色）、长期记录加**"长期记录"标题**并简化文案（"始于 2026 年 3 月 · 最长连续记录 67 天 · 近 6 个月日均活跃（按有效记录日）"）。未采纳（产品建议，超出 v0.1 统计主页范围）：活跃时长颜色统一蓝体系（review 标注"不是必须"）、顶栏"停止 Agent"下沉（09 顶栏行为合同，移动需单独评审）。门禁 `pnpm typecheck`/`lint`/`test` 126 项/`build` 全绿。**阶段四 DoD 自动项已打勾**（① typecheck+lint、② 组件/模型 fixture 测试全覆盖〔含 upFromZero"新增 N"紧凑时长——review-2 将长格式改为紧凑，DoD 文案随实现纠错同步〕、③ pnpm test 126 项）；④ 手工矩阵已执行通过（2026-08-04 视检：浅色/深色/forced-colors 可读、键盘导航可达），阶段四 DoD 四项全部打勾收口。阶段五（前端接入真实命令 + 双命令轮询）已实现：新增 `useStatsHome` 双命令状态机（home 通道首次进入/days 切换/跨日重查 `stats_get_home(days)`、status 通道 5s 轮询 `stats_get_status` 只替换 live；首次 home 即 ready；双通道 generation 防串；跨日 status.localDate≠home.localDate 显式双失效自动重查；范围切换失败保留旧图+恢复范围+非阻塞提示；仅首次失败进整页 error+重试），StatsPage 改为真实命令渲染并按 F-4 合并 `live.todayTrendPoint` 覆盖今日柱、`live.weekProgress.currentActiveMs` 覆盖当前周柱，`statsGetHome/statsGetStatus` 桥接签名已启用（阶段四仅签名）。阶段五 Vitest 11 项（mock bridge + fake timers：首次 ready、轮询只换 live、跨日自动重查、切范围走 home、慢 home 防串、今日柱/当前周柱 live 覆盖、status 失败保留、范围失败恢复、空状态、首次 error+重试）+ 既有组件测试，`pnpm typecheck`/`lint`/`test` 133 项/`build` 全绿，**阶段五 DoD 已打勾**。**阶段五 DoD 已打勾**。阶段五外部审核两处 P1 逻辑缺陷修复（探针实证：临时回退对应测试变红、恢复变绿）：①**跨日连锁重查竞态**——`refreshStatus` 的 `prev` 在 await 前捕获，跨日重查在途时 home.localDate 仍旧日，下次轮询再命中跨日会取消重启在途查询造成饥饿；新增 `pendingCrossDayRef`（在途目标 localDate）三键判定：已在途时跳过重复触发，home 落地/同日轮询时清除（回归测试：慢 home 在途跨日推进两个轮询周期调用次数不增）；②**跨日失败 suppress 泄漏**——跨日重查 days 未变却走"恢复 days+suppress"分支，同值 bailout 使 suppress 永不消费、吞掉下次范围切换；catch 分支区分 `days !== prev.days`（范围切换失败才恢复+suppress），跨日失败只提示不恢复（回归测试：跨日失败后切 7 天正常、横幅清除）。另补 status 侧"跨日双失效后旧 gen 迟到响应被丢弃"回归测试；方案 11 §4.2 upFromZero 文案与实现统一为紧凑时长并登记待回写设计 10 §4.1。门禁 `pnpm typecheck`/`lint`/`test` 136 项/`build` 全绿。门禁 `pnpm typecheck`/`lint`/`test` 136 项/`build` 全绿。自查结合设计 10 §5.4 发现并修复一处合同遗漏：**页面重新聚焦（visibilitychange false→true）时随 `stats_get_home` 刷新低频快照**（应用构成当前桶/月度当前月，设计 10 §5.4"轻量轮询同步边界"）——原实现聚焦只跑 status 轮询（跨日检查），同日不重查 home；新增聚焦重查 effect（与 status 跨日触发经 generation 机制合并），回归测试（聚焦后 home 调用 +1）经探针实证（无修复→红）。另验证 StrictMode double-mount 安全（cancelled+generation 守卫，double-effect 下 home 查询两次只应用一次）。门禁 `pnpm typecheck`/`lint`/`test` 137 项/`build` 全绿。
- **统计主页阶段四布局按外部评审定稿优化（2026-08-04）。** 页头改"活动概览" + 轻量胶囊覆盖文案（"近 14 天记录 12 天"格式）；①本周进度前移入主卡右侧（左今日状态 2.4fr | 右本周进度 0.8fr 分隔线，StatusCard 改为无卡片外壳的内容组件，信息层级三级化：标签 + 大号紧凑数字 + 同行工作块 / 截至时刻 + 比较 / 摘要句）；②趋势与③近 12 周均改全宽开放区块（无卡片），图表标题带范围（"近 N 天活跃趋势/应用构成"）；④双列独立卡 0.95fr/1.05fr 顶对齐；切换器静态行为一致化（7 天取主 fixture 尾部 7 点日桶、14 天原样、30 天整体换用 statsHomeWeekFixture 30 点 + 周桶，删除作用范围提示句）；趋势柱下稀疏时间锚点（首点/中间点 MM-DD/今天）、周柱下月份边界刻度（"N月"）、惯性底部图例色块改紧凑信息条、构成日桶行补当日总时长且当前行弱化只作用于堆叠条。门禁 `pnpm typecheck`/`lint`（--max-warnings 0）/`test`/`build` 全绿。**与 11 方案阶段四 4.1 / 设计 10 §3.2 的"③ 组合卡"描述有偏差**：本周进度前移入主卡右侧（符合设计第一层视觉权重原文），③ 周图独立全宽；待阶段六文档同步时统一回写。
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
| React 19 UI 基座 | Implemented | `apps/desktop`：Today/Timeline/Heatmap/Settings/Diagnostics 五页 + 四态 + 顶栏 Agent 控制 + 令牌主题 + forced-colors 适配；145 项 Vitest 通过（含统计主页阶段零~七）；Settings 页“启动吾迹时自动开始记录”复选框走独立 Desktop 偏好（09 §9.4，损坏显式上报；保存按 dirty 状态独立提交——只改偏好不推进 Settings revision、Settings 失败不阻断偏好保存，部分失败矩阵有回归）；顶栏消费 `auto_start_status`：启动编排期间显示“正在开始记录…”瞬态，失败显示可见提示（不伪装成功），手动重试成功后提示消失（审核 P2 闭环）；Timeline 日期使用 DB reporting 时区（R08），今天 5s 轮询整体重取（UI 不分页，最新在顶部，含到底/到顶悬浮按钮；超长一天明确截断提示；请求 generation 防串日 + 同目标防重入），支持 ?date/?hour 定位与日期导航、历史日期静态不轮询、小时定位排除切换标记并按查看日期裁剪；Heatmap 7 天 × 24 小时固定日期轴读 hourly 投影、强度 Rust 归一化 0-4（days 校验下沉 Reader）、?week= 周翻页（真实 `today` 与 `rangeEndLocalDate` 分离，`weekOffset` 仅允许 -520..0 并下沉 Reader，非法/未来 URL 规范化回本周，历史周静态不轮询且不伪装今天/现在标记），请求以 target + generation 防 A→B→A 串周，格子 Enter/Space/点击跳转时间线对应日期小时（UI-005），HC 五级系统色可区分；Diagnostics 时间基准随轮询更新（R09） | Today/Timeline/Heatmap/Settings/Diagnostics 使用 09 v0.1 DTO；09 §8.3/§8.4/§10 已同步 | 09 §12.2 手工门禁已通过（2026-08-05；尺寸/DPI/HC 按用户决定跳过，阶段四 fixture 视检已覆盖 HC） | 收口复核 |
| Tauri 2 Desktop shell | Implemented | `apps/desktop/src-tauri`：IPC client（副作用 timeout 后同 ID 重试）、Query、Settings CAS（损坏文件上报、resync appliedRevision 取自 DB MAX）、detached Agent 控制、托盘、单实例、17 语义命令（含统计主页 stats_get_home/stats_get_status）、集成测试；顶栏与托盘共用 `ControlService`（ok=false 解析、in-flight 互斥、Stop 等 runtime 终态）；Desktop 本地偏好独立文件（09 §9.4：偏好开启时启动即自动开始记录——ensure_running 后提交内部 `capture_ensure_recording`，Coordinator 内原子 Stopped→开始/Paused→恢复/Running→幂等；三态启动决策枚举且 package-smoke 优先 `SmokeEnsureOnly` 只拉起不自动开录；旧键名读取兼容、损坏/未知字段显式上报、NotFound 之外 I/O 故障返回稳定错误且启动决策失败关闭；启动结果 `AutoStartOutcome` 经 `auto_start_status` 顶栏可见化——ensure 区分 lifecycle monitor 永久故障（显式失败）与 Lock/Sleep 临时抑制（Ok(Paused) 自动恢复），手动重试成功清除 failed；托盘按进程状态分别建模——Degraded/Faulted 显示异常并保留“停止 Agent”，Starting/ShuttingDown 为无动作瞬态） | 直接使用 Rust Query/IPC/Settings/Process Controller | — | V01-8 重验收 |
| Bridge-free Tauri | Verified（2026-07-30 基线 + 2026-08-05 最终工作区重跑） | 2026-07-30 从新根目录实跑 dev 包脚本：固定 Agent 布局、包内无 Bridge/.NET/旧合同、Agent 二进制 byte 级一致、manifest 含版本+SHA-256、安装版 Desktop 经 package-smoke test channel 调用同一 AgentController 拉起安装目录 Agent；普通 Desktop 启动按本地偏好默认自动开始记录（三态启动决策，package-smoke 优先 `SmokeEnsureOnly` 只拉起不自动开录，09 §9.3/§9.4）；旧 prod/dev 数据库 checksum 前后不变。2026-08-01 启动/打包路径变更后按审核要求重新运行 dev 包脚本，全部通过；烟测新增**真实断言**：最新 runtime capture_state=stopped 且 Observation 数 0（把“只拉起不自动开录”从说明文字变成可执行门禁） | 安装包与运行时不含 `.NET Bridge` | package 基线已通过；8h soak 与人工矩阵仍待重跑 | V01-8 收口 |
| Rust workspace / `wuji-core` | Verified | `crates/wuji-core`：schema 对齐领域枚举、Settings 默认值/验证/digest、21 个稳定错误码（含 `as_str`/`from_code`）、固定命名空间、DTO + specta branded TS drift 门禁（Int64String 品牌 + crate/desktop 双副本一致性，R07）；`cargo test -p wuji-core` 通过 | 纯领域、Settings、Privacy、Analytics、Protocol、Error | 长期 Privacy/Analytics 部分待后续版本 | 持续回归 |
| Rust `wuji-storage` | Verified | `crates/wuji-storage`：唯一内嵌 DDL（Settings LKG 完整内容存独立双槽备份文件，不入 SQLite——09 §16.1 回写后）、bootstrap 自检、Writer 行操作、触及桶重算、只读 Reader、Today SUM 聚合与 drop event_count 修正（R02）、checkpoint busy 结果行判定（§16.4）；21 项测试通过（含 21+ 应用/跨午夜/DST/幂等/恢复） | v0.1 Single Writer、只读 Query、空库 bootstrap 和最小 projection | — | 持续回归 |
| Rust Agent binary | Partial | `apps/agent`：双 lane Writer、可靠 Barrier/唯一 Coordinator、Settings 恢复/effectivity、CommandServer、心跳、MaintenanceLite、reconciler、单实例与启动恢复已接入；阶段 4.5 已加入 Lock/Sleep 双 suppression、Unlock/Resume 恢复、monitor fault、事务式事件链启动与有界 shutdown | 独立 Rust Agent 长期进程 | 阶段 4.5 自动化补修**已复审通过（2026-08-04 独立复审：L01–L20 全映射、无假测试、实跑全绿）**；**真实锁屏→恢复、睡眠→唤醒均已人工验证（2026-08-04：时间线如实标记缺口、唤醒后记录恢复）**；**4.6 已收口（2026-08-04：P2-01 十类核心失败场景真实拓扑映射完备〔见阶段4.6-完成说明〕、P2-02 由 TestAgentGuard DuplicateHandle 句柄身份确定性化、连续两次 `cargo test --workspace` 411/411 0 失败）** | 阶段 4.5 复审 + 人工门禁 |
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
| Rebuild `cargo test --workspace` | 命令存在 | Passed（2026-08-05，412/412 两次全量 + bindings 无 drift） | 普通用户权限全量通过；Host 确定性断言覆盖永久 lifecycle monitor fault 下手动 Start 返回 Paused 仍保留失败、顶栏/托盘成功重试共用结果对账，以及 spawn 前同步发布 `starting`；Agent E2E 8/8，测试前后 Agent 残留 0；受限环境不能写 `%LOCALAPPDATA%`，不作为代码失败；真实锁屏/睡眠已验证（2026-08-04） |
| Rebuild Desktop `pnpm typecheck` / `pnpm lint` / `pnpm test` / `pnpm build` | package scripts 存在 | Passed（2026-08-01，Vitest 81/81、零警告、dist 产出） | 含 R07 品牌夹具、R08 时区、R09 诊断、R10 切换间隔、Timeline 防串日、Heatmap 范围锚点/周边界/A→B→A 防串周断言，以及 Desktop 偏好独立提交（只改偏好不推进 revision、Settings 失败不阻断偏好保存）、I/O 错误区分、未知字段拒绝、偏好损坏自愈后清除警告、托盘故障态建模断言、顶栏“正在开始记录…”瞬态、自动启动状态 generation 防迟到覆盖，以及启动失败可见提示与手动重试成功后提示消失闭环 |
| Rebuild dev package（整改后脚本） | `scripts/build_dev_package.py` | Passed（2026-08-01，启动/打包路径变更后按审核要求重跑） | release、React/Tauri/NSIS、静默安装、固定布局、禁资产、Agent byte 一致、安装版启动、manifest、旧 prod/dev 数据库 checksum stable 全部通过；安装版 smoke 先确认 `process=running`，再连续 5 个样本/4 秒保持 `capture=stopped` 且 Observation=0；清理使用 path+channel 双重交付并升级为稳定 Windows 进程句柄，不再裸 PID；脚本确定性测试 6/6；生成产物位于被忽略的 `target/` 与 `dist/`，不提交二进制 |
| 8 小时 soak（整改后脚本） | `scripts/soak.py` | Historical Passed（2026-07-22）；当前工作区 NotRun | 历史 verdict=pass 仅作脚本/旧基线证据；4.7 对最终代码重新执行 |
| 09 §12.2 手工门禁（锁屏/休眠、30 分钟对照、尺寸/DPI/主题/键盘/读屏、离线历史显示） | 手工流程 | 通过（2026-08-05：锁屏/休眠、30 分钟对照、键盘/读屏、离线历史；尺寸/DPI/HC 用户决定跳过） | 剩余 disk-full 可选 |
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

1. ~~复审[阶段 4.5 第六轮缺口补修]~~ 已复审通过（2026-08-04）；真实 Lock/Unlock/Sleep/Resume 人工验收已完成（锁屏→恢复、睡眠→唤醒均通过）；
2. ~~在最终工作区重新构建安装包并归档 `package-validation.json`~~（2026-08-05：`rebuild-package.ps1` DEV PACKAGE OK——旧库 verified_stable、资产扫描无 Bridge/.NET、package-smoke 真实断言 capture_state=stopped 且 Observation=0、manifest 已生成）；
3. ~~在 4.7 对最终工作区重新执行 8 小时 soak~~（2026-08-05 完成：480 分钟/480 采样、受控退出七项全过、RSS+2.3MiB/WAL 收敛 8KB/心跳单调/零 dropped/quick_check ok、旧库 verified_stable、verdict=pass，报告 `dist/soak-report.json`）；
4. ~~完成 09 §12.2 手工门禁~~（2026-08-04：真实锁屏/休眠已验证；30 分钟受控对照、键盘导航/读屏、离线历史显示五页通过；尺寸/DPI/HC 按用户决定跳过——统计主页阶段四 fixture 视检已覆盖 HC）；
5. ~~disk-full 手工注入核对~~（2026-08-05 用户决定跳过：busy/corruption/checkpoint 判定已有自动测试覆盖，风险低；如实记录）；
6. ~~全部自动与手工项关闭后复核 `migration-status.md` 内部一致~~（2026-08-05 复核完成：统计主页阶段零~七、第四阶段整改 4.1–4.7、8h soak、正式 package、09 §12.2 手工门禁全部关闭；状态表 8 处过时项已修正）。

**Rebuild v0.1 开发验收完成声明（2026-08-05，产品负责人确认）**：以上全部自动与手工项关闭、migration-status 复核内部一致——v0.1（dev-only bridge-free React/Tauri/Rust Agent/SQLite 链路）开发验收完成。边界如实保留：09 Draft 的正式接受仍留待产品评审；production hardening（二进制/进程签名认证、正式安装升级、Schema migration、prod cutover、旧数据退役）属独立生产化线（09 §33/§14），未随 v0.1 验收达成；正式发布需产品评审后另立生产版本。

长期方向（v0.1 验收后不自动启动）：按 09 §14 选择 v0.2/v0.3/v0.4；长期 manifest、Importer、Snapshot/Lease 和 production cutover 保持延期；旧系统回滚来源为 ADR-003 冻结提交。
**主页新增 ⑥ 缩小版热力图（2026-08-04 产品扩展，已实施）**：长期记录区之后新增 24 小时 × N 天缩略热力图（行高 6px、总高约 170px），数据来自 activity 域 `activity_get_heatmap`（默认范围）独立低频拉取，不参与 stats 双命令 5s 轮询；失败仅本区块轻提示、不阻塞主页；复用 heatmapModel 纯函数与全局 heatmap-level 颜色，今天列高亮，不带键盘格子导航（缩略图语义）。注意：该区块超出设计 10 §5 五区块冻结范围（stats 域混入 activity 域快照），已随用户产品决策实施；设计 10 回写与正式纳入统计主页合同留待产品评审/阶段六确认。

**v0.2 候选功能（2026-08-04 记录，暂不实施）**：工作惯性卡周间对比——当前周 24 小时活跃曲线与上周同期对比，增加/降低时段用不同颜色标注（中性暖/冷色，非红绿——设计 10 禁止强烈红绿评价）。阻塞点：①数据源不存在（惯性为近 14 天均值，无上周 24h 曲线，需 Rust 新增上周窗口查询 + DTO 扩展 + specta 重生成绑定）；②超出设计 10 冻结的 v0.1 范围（仅接受实现纠错），需产品负责人确认 + 设计定稿修订（窗口口径、颜色语义、低样本处理）；③完整 Rust→绑定→前端链路。纳入 v0.2 产品迭代评审候选，v0.1 验收后评估。

**阶段六（文档回写 + 全量门禁，2026-08-04）已收口**：①设计 10（权威）合同修订回写——`StatusDto` 拆出 `LiveStatusDto`（实时五字段不含摘要）、`StatsStatusDto` 由 `status` 改为 `liveStatus` 并携带 `localDate`/`reportingTimeZoneId`（P0-1/P0-2）、`CompositionBucketDto.hasData`、§4.1 `upFromZero` 文案"新增 N 分钟"改紧凑时长"新增 N"，头部追加阶段六回写说明；②09 基线 §8.3 命令表 15 → 17（`stats_get_home`/`stats_get_status`）+ 统计轮询 5s 同拍说明 + 新增 §10.6 统计主页合同（六区块、双命令刷新、语义、四态）+ "五页面"改"全部页面"；③CLAUDE.md 命令 15→17、页面增统计主页；④migration-status 本段。全量门禁全部通过：`cargo fmt/check/clippy` 0 告警、`cargo test --workspace` 411 项 0 失败、bindings 双跑（生成后无 drift）、`pnpm typecheck/lint` 0 告警、`pnpm test` 143 项、`pnpm build`、`git diff --check`、`rebuild-package.ps1` dev 包烟测真实断言通过（process_state=running、capture_state=stopped、Observation=0、稳定观察 4.0s、旧库 checksum 不变、无残留进程）——**阶段六 DoD 已打勾**。

**阶段 4.7（Soak 判据、文档同步与收口，2026-08-04）完成**：①`scripts/soak.py` 修复 P2-03 假阳性——`ipc_graceful_shutdown` 返回结构化 `(hello_ok, shutdown_ok, will_exit, note)`，退出判定提取为纯函数 `controlled_exit_failures`（shutdown_attempted/hello_ok/shutdown_ok/will_exit/exit_code/forced_kill/agent_exited_early 七项），**任一受控退出条件失败即 verdict=fail、提前 exit 0 也不通过**；报告 `gracefulShutdown` 显式记录 7 项；新增 `scripts/tests/test_soak_verdict.py`（unittest 9 项：成功/IPC 失败/未尝试/超时强杀/提前 exit 0）；②09 §16.1 回写（Settings LKG：`content_json` → DB metadata + 双槽完整内容，隐私不入 SQLite）、§16.2 回写（sequence watermark → BarrierId + injected ack + Coordinator）、§16.6 Schema 增补同步；③新增 `Rebuild-v0.1-第二轮审核第四阶段整改回应.md` 逐项映射 P1-01~P1-05/P2-01~P2-04；④历史 evidence 保留但明确不覆盖当前代码；⑤正式 package、8h soak、真实 Lock/Sleep（已人工验证）、UI 人工门禁保持 NotRun/Pending。结论仅"第四阶段整改完成，等待复审"，不宣称 S2-03/S2-04 最终关闭或 v0.1 验收完成。

**统计主页阶段七验收纠错（2026-08-04）：惯性开工/收工改为主时段语义。** 原线性"首个/最后一个活跃占比 ≥ 峰值 30%"在熬夜场景错误：凌晨 0-5 点的熬夜尾巴被当"开工 0:00"、分离次段计入收工得"收工 23:00"。修复为**环形连续活跃段**（`wuji-core::derive_inertia`）：从峰值向两端找 < 30%×peak 的断点，开工 = 峰值前最近断点下一小时、收工 = 峰值后最近断点前一小时；跨午夜时凌晨段与晚上段相连（熬夜 20:00→次日 3:00 得开工 20 / 收工 3），分离次段不再计入收工；全圈活跃退化到开工=收工=peak。新增跨午夜测试（开工 20/收工 3），既有两段分离测试断言同步（收工从最晚活动改为主时段结束）。设计 10 §4.4 已回写；wuji-core 59 项、wuji-storage 50 项全绿（改动仅影响 wuji-core 纯函数，无其他 crate 断言依赖惯性标注）。

**统计主页阶段七验收进度（2026-08-05，真实数据）**：观察清单——①均线平滑✅（手工视检）②惯性标注✅（环形主时段算法修复后视检接受）③当前周参考值✅④±5% 噪声遮蔽✅⑤零基线✅（自动核验：阶段三 e2e UpFromZero + statsModel 单测）⑥轮询一致性✅（自动核验：Rust 同快照同值 today_active_ms + 阶段五覆盖测试）⑦跨日✅（2026-08-05 真实观察：页面保持可见跨午夜，主页有相应自动换日动作；逻辑另有 15 项测试锁定）⑧空状态✅（自动核验）。手工矩阵——浅色/深色✅、键盘 Tab✅；forced-colors/DPI 按用户决定跳过（阶段四 fixture 视检已覆盖 HC 系统色）。09 §12.2 全页面 UI 门禁——30 分钟受控对照✅、键盘导航/读屏✅、离线历史显示✅；尺寸/DPI/HC 按用户决定跳过。期间修复：惯性开工/收工环形主时段算法（2026-08-05 已提交）、热力图 60s 轮询、惯性刻度对齐。8 小时 soak ✅（2026-08-05：480 分钟全判据通过，`dist/soak-report.json`，基线 gitCommit 6d25e90 含惯性算法修复）。**阶段七观察清单全部通过，阶段七 DoD 已打勾（见方案 11）**。
期间不得修改旧数据库；旧 C#/WPF/Bridge 仓库资产已于 2026-07-29 移除，如需参考必须按 ADR-003 从冻结提交恢复到独立目录。
