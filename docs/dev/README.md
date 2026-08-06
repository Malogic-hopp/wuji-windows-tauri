# WUJI v2 开发设计索引

状态：**Rebuild v0.1 开发验收完成（2026-08-05，dev-only）**；09 Draft 正式接受留待产品评审
最后更新：2026-08-05

## 0. 文档生命周期与目录导航

本文档集按生命周期分层（每阶段收口后归档，`git mv` 保留历史）：

| 层 | 路径 | 内容 | 维护规则 |
|---|---|---|---|
| 活跃权威 | `docs/dev/` 根 | 01–09（长期规划 + v0.1 基线）、10/11（统计主页）、ADR-002/003、migration-status、本 README | 只放当前生效文档；变更走评审/ADR |
| 过程归档 | `docs/dev/archive/<工作线>-<日期>/` | 已收口工作线的审核/评审/整改/完成说明（历史审核、统计主页评审、设计评审、第四阶段整改-2026-07-23 等） | 阶段收口后移入；不修改，只读 |
| 验收证据 | `docs/dev/evidence/<版本>/` | soak-report、package manifest 等验收产物 | 验收产物归位，不留在 dist |

活跃文档：01–09、[10 统计主页设计定稿](./10-统计主页设计定稿.md)（冻结）、[11 统计主页实施方案](./11-统计主页实施方案.md)（已完成）、[ADR-002](./ADR-002-React-Tauri-Rust目标架构.md)、[ADR-003](./ADR-003-Rebuild-only仓库转换与旧系统源码退役.md)、[migration-status](./migration-status.md)。

**未来阶段入口**（v0.1 后路线，见 §7）：09 §14 路线（v0.2 Context Segment → v0.3 → v0.4 生产化）、migration-status 已记录的 v0.2 候选（惯性周间对比、热力图 >31 天）、production hardening（09 §33：签名认证/安装升级/Schema migration/cutover，需产品评审立项）。不预建空目录/空框架（09 要求）。

## 1. 文档层级与适用范围

当前实施使用两层文档：

| 层级 | 文档 | 用途 |
|---|---|---|
| 当前实施基线 | [09-Tauri-Rust-Rebuild-v0.1实施基线.md](./09-Tauri-Rust-Rebuild-v0.1实施基线.md) + [schema.sql](../../crates/wuji-storage/schema/schema.sql) | 冻结 dev-only v0.1 做什么、运行/算法/协议合同、可执行空库 Schema 和验收顺序 |
| 仓库转换决策 | [ADR-003-Rebuild-only仓库转换与旧系统源码退役.md](./ADR-003-Rebuild-only仓库转换与旧系统源码退役.md) | 记录 Rebuild workspace 提升、旧源码提前移除的批准例外、冻结提交和数据保护边界 |
| 长期规划 | ADR-002、01–08 | 保存完整目标语义、生产安全、版本化重建、迁移和退役风险 |

在 v0.1 范围内，09 是实施范围与运行合同权威，`crates/wuji-storage/schema/schema.sql` 是其可执行存储附件。ADR-003 只对 09 中“旧源码继续位于同一工作树”的要求建立正式例外，不改变 production cutover、旧数据保护和 G-RETIRE 门禁。01–08 中被 09 明确延期的 Generation、Result Set、Snapshot、Lease/GC、Importer、生产认证等内容不得成为 v0.1 的前置条件，也不得提前搭建空框架。09 没有重新定义的基础不变量——React 白名单、双进程、Agent 单写行为数据库、Tauri 对行为数据库只读、隐私先于落库、新旧数据隔离——仍遵循 ADR-002。

长期规划内部仍按分域权威管理：

文档不采用一个会把架构不变量置于最下位的线性优先级。每份文档只在自己的范围内权威：

| 范围 | 权威文档 | 不得被其他文档覆盖的内容 |
|---|---|---|
| 进程、所有权、安全边界 | [ADR-002](./ADR-002-React-Tauri-Rust目标架构.md) | 双进程、Agent 单写行为数据库、Desktop 对行为数据库只读、React 能力边界 |
| 产品术语、指标、UI 门禁 | [01](./01-产品语义与指标词典.md) | Work/Context/质量/时区的用户语义 |
| 领域依赖和不变量 | [02](./02-行为分析领域模型.md) | 事实、派生、世代、发布对象的依赖方向 |
| 算法 | [03](./03-上下文识别与算法版本规范.md) | 去抖、分类、事件判定、版本触发 |
| 持久化 | [04](./04-SQLite-v2与持久化读模型.md) | Schema、FK、事务、保留、发布和查询 |
| Agent 运行时 | [05](./05-Rust-Agent运行时设计.md) | 任务、lane、背压、恢复和进程状态 |
| 本地合同 | [06](./06-本地接口与错误合同.md) | IPC、Tauri command、Settings、兼容和升级 |
| 迁移、切换与退役 | [07](./07-迁移、切换与旧系统退役计划.md) | 阶段顺序、shadow/import、切换、回滚和旧资产删除条件 |
| 验收与发布门禁 | [08](./08-验收门禁与测试矩阵.md) | Gate、测试矩阵、通过条件、证据和阻断级别 |

长期规划跨范围冲突不得按“较后文档覆盖较前文档”处理：先停止实现，指出冲突双方，通过修改对应权威文档或新增 ADR 同时闭合。09 对长期能力的延期是版本范围选择，不是对长期语义的覆盖。

[第一轮设计审核回应.md](./archive/设计评审/第一轮设计审核回应.md)、[第二轮设计审核回应.md](./archive/设计评审/第二轮设计审核回应.md)和[第三轮设计审核回应.md](./archive/设计评审/第三轮设计审核回应.md)是审核跟踪记录，不定义产品语义。

[migration-status.md](./migration-status.md)只记录仓库实际实施与验证状态，不定义设计，也不得用“当前尚未实现”覆盖目标规范。该文件必须随实现、验证证据和 Gate 状态变化更新。

审核回应、阶段提示词和 `evidence/` 中的命令/路径是历史证据，可能保留目录扁平化前的 `rebuild/` 或 `docs/rebuild/` 字样；当前可执行路径一律以仓库根 `README.md`、09 和脚本为准，不应按历史 evidence 改写当前工作区。

## 2. 状态和接受门禁

| 文档 | 当前状态 | 转为 Accepted 的最低门禁 |
|---|---|---|
| 产品语义与指标词典 | Draft | 产品负责人接受术语、时区、质量门禁和延期项 |
| 行为分析领域模型 | Draft | 领域不变量测试设计通过评审 |
| 上下文识别与算法规范 | Draft | 候选参数经脱敏黄金样本校准 |
| SQLite v2 设计 | Draft | migration DDL、drift gate 和故障测试方案通过评审 |
| Agent 运行时设计 | Draft | 背压、故障、维护和 soak 方案通过评审 |
| 本地接口与错误合同 | Draft | 兼容、安全与超时恢复测试通过评审 |
| 迁移、切换与旧系统退役计划 | Draft | dev/prod 切换、回滚、数据导入和退役责任通过联合评审 |
| 验收门禁与测试矩阵 | Draft | Gate 责任、fixture、证据格式和 P0/P1 阻断规则可执行 |
| Tauri + Rust Rebuild v0.1 实施基线 | Draft；**v0.1 开发验收完成（2026-08-05）** | v0.1 范围、运行命名、DDL、算法、Writer、协议、Settings/打包合同按阶段获得批准；**正式接受仍留待产品评审** |
| 统计主页设计定稿 | 冻结（设计权威） | 10 号文，仅接受实现纠错；11 号实施方案阶段零~七已完成 |
| ADR-002 | Proposed | 上述依赖规范至少形成可实施的 Accepted 基线 |
| ADR-003 | Accepted | 2026-07-29 产品负责人批准；冻结提交、恢复方式和旧数据库保护边界已记录 |

`Draft` 或 `Proposed` 内容不得被描述成已完成能力。候选参数不得成为不可版本化的硬编码常量。

当前实施只按 09 开展 rebuild v0.1。V01-1 可以立即实施；Storage、Agent、Desktop/打包分别以 09 规定的阶段合同接受为开始条件。长期规划中的高级原型不再是 v0.1 前置；`schema-v2-manifest.yaml`、Importer、Snapshot/Lease、production 认证和破坏性命令继续延期。

## 3. 规范用语

- **必须 / 不得**：强制要求；实现不满足即不合格；
- **应当**：默认要求；偏离必须新增 ADR 并说明风险；
- **可以**：可选实现，不构成兼容承诺；
- **候选**：尚未接受，不得作为稳定产品语义对外展示。

长期逻辑 Schema 章节中的字段默认都是“必须字段”；明确标记为“延期”或“候选”的字段不进入未来生产 Schema。v0.1 直接建库的字段以 `crates/wuji-storage/schema/schema.sql` 为准。禁止用“建议字段”描述事务、外键或查询实际依赖的结构。

## 4. 统一术语与命名

正文使用带空格的英文领域名，代码/字段使用对应标识符：

| 正文术语 | Rust 类型示例 | SQL 示例 |
|---|---|---|
| Foreground Observation | `ForegroundObservation` | `foreground_observations` |
| Activity Segment | `ActivitySegment` | `activity_segments` |
| Context Segment | `ContextSegment` | `context_segments` |
| Work Block | `WorkBlock` | `work_blocks` |
| Effective Context Switch | `EffectiveContextSwitch` | `context_switch_events` |
| Segmentation Generation | `SegmentationGeneration` | `segmentation_generations` |
| Analysis Generation | `AnalysisGeneration` | `analysis_generations` |
| Work Generation | `WorkGeneration` | `work_generations` |
| Result Set | `ResultSet` | `result_sets` |
| Snapshot Slice | `QuerySnapshotSlice` | `query_snapshot_slices` |
| Query Snapshot | `QuerySnapshot` | `query_snapshots` |
| Fact Cursor | `FactCursor` | `fact_cursor` |
| Fact Boundary | `FactBoundary` | `coverage_*_epoch_ordinal + coverage_*_utc_ms + coverage_*_fact_cursor` |
| Identity Resolution Generation | `IdentityResolutionGeneration` | `identity_resolution_generations` |

时间字段在产品文档写作 `derivedAtUtc`，在 SQL 中写作 `derived_at_utc_ms`；二者是不同表示层的同一语义，不是两个字段。

## 5. 长期 v2 产品范围

长期 v2 规划包括：Observation、Activity Segment、Work Block、规则分类、Context Segment、Interruption、Effective Context Switch、小时/日 App 与 Context 使用、工作与切换指标、数据质量指标、版本化重建、显式报告时区。

长期 v2 仍明确不包括：Focus Block、`fragmented_seconds`、Plaintext 标题持久化、机器学习分类、云同步和跨设备身份。Rebuild v0.1 的更小范围以 09 为准。

## 6. 长期设计变更流程

1. 先修改最上位的受影响文档；
2. 记录需要提升的规则、算法、特征、日历或 Schema 版本；
3. 更新下位模型、migration 映射和黄金样本；
4. 通过对应测试门禁；
5. 若会改变已展示的历史含义，创建新 generation 并重建，不覆盖旧结果。

## 7. v0.1 后路线（未来阶段入口）

v0.1 开发验收完成（dev-only），后续方向以 09 §14 与 migration-status 为准：

| 方向 | 内容 | 前置 |
|---|---|---|
| 产品评审 | 09 Draft 正式接受（Accepted） | 产品负责人评审 |
| v0.2 | 规则 Context Segment / daily context usage；统计主页候选（惯性周间对比、热力图 >31 天，见 migration-status） | 09 §14 |
| v0.3 | Context Switch / Interruption 与解释 | v0.2 |
| v0.4 / production hardening | 生产 Schema、签名认证、安装升级、正式 migration、cutover | 产品评审立项（09 §33） |
| 长期 | 历史重建、多世代发布、Snapshot/Lease/GC | 真实需求 |

新阶段启动时：按本 README §0 建立阶段目录/完成说明，收口后归档到 `archive/<工作线>-<日期>/`。
