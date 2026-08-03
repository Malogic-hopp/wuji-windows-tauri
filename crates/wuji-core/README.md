# wuji-core — 核心领域与合同 crate

WUJI Rebuild v0.1 的**唯一真相来源**。定义了 Rust Agent、Tauri Host、React 前端三方共享的所有类型、规则和固定命名空间。

**为什么需要这个 crate？** 项目有三层——React 前端（TypeScript）、Tauri 桌面壳（Rust）、Agent 后台进程（Rust）。它们通过 Tauri command 和 Named Pipe 通信，但必须对"一条 Observation 长什么样"、"错误码有哪些"、"设置字段怎么验证"达成一致。wuji-core 就是这份三方**永不漂移的合同**——Rust 侧手写定义，TypeScript 侧机器生成镜像，CI 自动检查两边是否逐字节一致。

**边界（09 §4）**：不依赖 Tauri、Win32 或 rusqlite。这条由 [tests/boundary.rs](tests/boundary.rs) 自动检查。

合同来源：[docs/dev/09-Tauri-Rust-Rebuild-v0.1实施基线.md](../../docs/dev/09-Tauri-Rust-Rebuild-v0.1%E5%AE%9E%E6%96%BD%E5%9F%BA%E7%BA%BF.md)（下称 09）。

---

## 目录结构

```
crates/wuji-core/
├── Cargo.toml              ← crate 身份证（依赖列表 + 边界约束）
├── src/                    ← Rust 源代码（手写，唯一真相来源）
│   ├── lib.rs              ← crate 入口，声明全部 7 个 public 模块
│   ├── domain.rs           ← 领域枚举（与 SQLite schema CHECK 严格一致）
│   ├── dto.rs              ← 数据传输对象（三方通信的唯一格式）
│   ├── error.rs            ← 错误合同（冻结错误码 + 安全错误结构）
│   ├── settings.rs         ← 设置模型（默认值、验证器、digest、进程名规范化）
│   ├── pipeline.rs         ← 采集流水线（隐私过滤核心逻辑）
│   ├── runtime_names.rs    ← 固定命名空间（编译期常量，进程/资源隔离）
│   └── bindings.rs         ← TypeScript 代码生成器（Rust 类型 → .ts 文件）
├── bindings/               ← TypeScript 生成产物（机器生成，禁止手改）
│   └── wuji-core.ts        ← 从 src/ 的 Rust 类型自动翻译的 TS 类型声明
└── tests/                  ← 集成测试
    └── boundary.rs         ← 依赖边界门禁（检查 Cargo.toml 无被禁依赖）
```

---

## 模块详解

### lib.rs — crate 入口

仅 7 行 `pub mod`，无逻辑。所有模块通过这里对外暴露。

### domain.rs — 业务状态枚举

描述"这条数据是什么状态"的标签集合，比如用户正在活动还是空闲、采集在运行还是暂停、中断是因为锁屏还是休眠。9 个枚举，**每个值的字符串形式直接写入 SQLite 数据库的 CHECK 约束**——改值就是改持久化规则，v0.1 内不允许。

| 枚举 | 含义 | 值（按序列化格式） |
|------|------|---------------------|
| `ActivityState` | 每条 Observation 的活动判定：用户正在操作 / 空闲 / 无法判断 | `active` / `idle` / `unknown` |
| `CaptureState` | Agent 采集开关：已停止 / 采集中 / 已暂停 | `stopped` / `running` / `paused` |
| `ProcessState` | Agent 进程整体健康度：启动中 / 正常 / 降级 / 故障 / 关闭中 / 已停止 | `starting` / `running` / `degraded` / `faulted` / `shutting_down` / `stopped` |
| `WriterState` | SQLite 写入器健康度：正常 / 降级 / 故障。故障时 Agent 会 fail-closed | `healthy` / `degraded` / `faulted` |
| `CaptureQuality` | 每条 Observation 的质量标记：正常采样 / 进程名降级 / idle API 不可用 | `normal` / `process_name_fallback` / `idle_unavailable` |
| `GapKind` | 时间轴上"缺口"的原因，例如采样空隙、暂停、锁屏、休眠、时钟变化（共 12 种） | `sampling_transition` / `capture_delayed` / ... |
| `RowStatus` | 时间区间记录的开关标记：`open` = 这段还在进行中，结束时间未知；`closed` = 已封存，结束时间已确定。用在缺口、活动片段、工作块三张表 | `open` / `closed` |
| `SegmentCloseReason` | Activity Segment 为什么结束：切应用 / 暂停采集 / 锁屏 / 休眠 / Agent 重启等（共 13 种） | `app_changed` / `state_changed` / ... |
| `WorkBlockCloseReason` | Work Block 为什么结束：空闲中断 / 暂停 / 锁屏 / 休眠等（共 13 种） | `idle_break` / `capture_error` / ... |

**零依赖**——不引用 crate 内任何其他模块，是最底层类型。

### dto.rs — 数据传输对象

React ↔ Tauri ↔ Agent **三方通信的唯一格式**。所有 struct 用 `#[serde(rename_all = "camelCase")]` 输出 JS 风格字段名。

**基础类型**：

| 类型 | 说明 |
|------|------|
| `Int64String` | i64 的十进制字符串表示。serde 只接受/输出字符串，防止 JS number 精度丢失（JS 只能安全表示 ±2^53）。TS 侧是 branded type `string & { __brand: "Int64String" }` |
| `LocalDate` | 严格 `YYYY-MM-DD` 格式，拒绝斜杠、无前导零等变体 |
| `RuntimeId` | 26 字符 ULID，Agent 每次启动生成新值 |

**主要 DTO**：

| 结构体 | Tauri command | 说明 |
|--------|---------------|------|
| `AgentStatusDto` | `agent_get_status` | Agent 心跳/健康/队列深度/错误码 |
| `TodayDto` | `activity_get_today` | 今日概览：活跃时长、当前应用、top apps、质量 |
| `TimelinePageDto` | `activity_get_timeline` | 时间轴分页：segment + gap 混合列表 + opaque cursor |
| `HeatmapDto` | `activity_get_heatmap` | 热力图：days×24h 稀疏格子，intensity_level 由 Rust 归一化 |
| `SettingsDto` | `settings_get` | 设置：区分 `revision`（已保存）与 `appliedRevision`（已生效） |

### error.rs — 错误合同

**`SafeErrorCode`**：19 个冻结错误码（`SCREAMING_SNAKE_CASE` 序列化），增删改都是合同变更。

| 类别 | 错误码 |
|------|--------|
| IPC | `IpcProtocolUnsupported`、`IpcChannelMismatch`、`IpcInvalidMessage`、`IpcPayloadTooLarge`、`IpcRequestIdReused` |
| 参数 | `InvalidArgument` |
| 采集 | `CaptureInvalidState` |
| Writer | `AgentWriterDegraded`、`AgentWriterFaulted` |
| 数据库 | `DbUnavailable`、`DbSchemaUnsupported` |
| 设置 | `SettingsConflict`、`SettingsInvalid`、`SettingsSavedNotApplied` |
| 启动 | `StartupRegistryFailed`、`StartupReconciliationRequired` |
| 其他 | `TimeZoneUnavailable`、`VersionIncompatible`、`InternalSafeError` |

**`SafeError`**：`{ code, message }`——message 是面向用户的**中文安全文本**，严禁泄露原始异常、路径、SQL、SID、窗口标题。

**`ErrorSource`** + **`ErrorSet`**：按来源（Writer / Checkpoint / Settings / IPC / LifecyclePump）管理错误，互不覆盖。`format_error_set()` 将当前错误合并为逗号分隔字符串写入 DB heartbeat。

### settings.rs — 设置模型

**`Settings` 结构体**：7 个字段。Tauri 是唯一写入者，Agent 只读。双方共用本模块的同一份默认值、验证器、digest 算法，避免两端漂移（09 §9.1）。

| 字段 | 默认值 | 约束 |
|------|--------|------|
| `schema_version` | 1 | 必须等于 `SETTINGS_SCHEMA_VERSION` |
| `revision` | `"0"` | 十进制数字字符串，每次保存 +1 |
| `sampling_interval_seconds` | 3 | 只能是 1/3/5/10 |
| `idle_threshold_seconds` | 60 | 30 ~ 1800 |
| `work_break_idle_seconds` | 300 | 60 ~ 3600，且必须大于 idle_threshold |
| `excluded_process_names` | `[]` | 每个必须是规范化后的小写文件名（如 `keepass.exe`） |
| `start_capture_on_login` | false | — |

**关键方法**：
- `validate()` → `Result<(), Vec<FieldError>>`：整份验证，失败时**旧设置全部继续生效，不允许部分应用**
- `canonical_json()` → `String`：按字段声明序、无空白序列化（两端 digest 比对的基础）
- `content_digest()` → `String`：规范 JSON 的 SHA-256 小写十六进制
- `next_revision()` → `Option<String>`：当前 revision + 1

**公共函数**：
- `sha256_hex(bytes)` — app_key 与 settings digest 共用
- `normalize_process_name(raw)` — trim → Unicode NFKC → lowercase，返回 `Option<String>`

### pipeline.rs — 采集流水线

定义采集管道的所有数据结构 + **隐私过滤核心逻辑** `ObservationProcessor::process()`。

**处理流程**（固定顺序，09 §5）：

```
RawCapture（原始采样，含进程文件名）
  │
  ▼ ObservationProcessor::process(raw, settings)
  │
  ├─ 1. 进程名为 None/空白 → CaptureError（不含进程信息）
  ├─ 2. 规范化后为空/超长 → CaptureError
  ├─ 3. 命中 excluded_process_names → PrivacyExcluded（不含进程信息）
  ├─ 4. idle ≥ idle_threshold → ActivityState::Idle + Normal
  ├─ 5. idle < idle_threshold → ActivityState::Active + Normal
  └─ 6. idle Unavailable → ActivityState::Unknown + IdleUnavailable
  │
  ▼
ProcessorOutput（五类：Observation / PrivacyExcluded / CaptureError / Barrier / SettingsRevisionMismatch）
```

**隐私承诺**：`RawCapture` 的原始进程路径在 wuji-windows 层已丢弃，到本模块时只剩文件名；`process()` 输出后连原始文件名也不存在——只有 `normalized_process_name`。

**五类输出**：`ProcessorOutput` 枚举的每个变体，加上 `continuity_epoch()`、`sequence()`、`settings_revision()` 统一访问方法。

**辅助函数**：
- `app_key_for(normalized_name)` → `"proc:" + sha256(name)`
- `display_name_of(raw_file_name)` → 去掉末尾 `.exe`（保留大小写）

### runtime_names.rs — 固定命名空间

**编译期常量**，不接受 React 或命令行传入的任意路径/标识。原始 SID 只用于派生 scope 哈希，不写入任何产物。

| 常量 | 值 |
|------|-----|
| `CHANNEL` | `rebuild-v01-dev` |
| `AGENT_EXE_NAME` | `wuji-rebuild-agent-v01.exe` |
| `DESKTOP_EXE_NAME` | `wuji-rebuild-desktop-v01.exe` |
| `TAURI_IDENTIFIER` | `com.wuji.rebuild.v01.dev` |
| `PRODUCT_NAME` | `吾迹 Rebuild v0.1（开发）` |
| `DATA_ROOT_RELATIVE` | `WUJI-Rebuild-V01\dev` |
| `DATABASE_RELATIVE` | `data\wuji-rebuild-v0.1.db` |
| `SETTINGS_RELATIVE` | `config\settings.json` |
| `LOGS_RELATIVE` | `logs` |

**核心函数**：
- `user_scope(windows_sid)` → SID 的 SHA-256 前 16 个小写十六进制——pipe/mutex 名不含原始 SID，但不同用户天然隔离
- `pipe_name(scope)` → `\\.\pipe\WUJI.Rebuild.V01.Dev.{scope}`
- `agent_mutex_name(scope)` → `Local\WUJI.Rebuild.V01.Dev.Agent.{scope}`
- `desktop_mutex_name(scope)` → `Local\WUJI.Rebuild.V01.Dev.Desktop.{scope}`
- `is_allowed_channel(channel)` → 只允许固定 dev channel 或测试 channel `rebuild-v01-test-<26字符ULID>`

### bindings.rs — TypeScript 代码生成器

用 specta 库从 Rust 类型自动生成 TypeScript 声明。**Rust 类型是唯一来源，TS 是生成产物。**

**生成目标**（两个文件必须逐字节一致）：
- `crates/wuji-core/bindings/wuji-core.ts`
- `apps/desktop/src/types/wuji-core.ts`

**品牌替换**：specta 原始输出 `export type Int64String = string`，自动替换为 `export type Int64String = string & { readonly __brand: "Int64String" }`，防止 TS 侧把任意 string 当 i64 用。

**更新命令**：
```powershell
WUJI_UPDATE_BINDINGS=1 cargo test -p wuji-core bindings
```

**drift 门禁**：`bindings_have_no_drift()` 测试在 CI 自动运行，生成内容与入库文件不一致则失败。

---

## 依赖分层

```
lib.rs（入口）
  ├─ domain.rs       ← 零 crate 内依赖，纯枚举
  ├─ error.rs        ← 零 crate 内依赖，纯错误码
  ├─ settings.rs     ← 依赖外部 sha2、unicode_normalization
  ├─ runtime_names.rs← 依赖外部 sha2
  ├─ pipeline.rs     ← 依赖 domain + settings
  ├─ dto.rs          ← 依赖 domain + error + settings
  └─ bindings.rs     ← 读取 dto、error、settings 的类型注册 → 导出 TS
```

- **最底层**：`domain.rs`、`error.rs`——不依赖 crate 内任何东西
- **中间层**：`settings.rs`、`runtime_names.rs`——只依赖外部库
- **组合层**：`pipeline.rs`（组合 domain+settings）、`dto.rs`（组合 domain+error+settings）
- **导出层**：`bindings.rs`（读取所有类型，输出 TS）

---

## 怎么用

### 改 Rust 类型（src/*.rs）

这是你**唯一需要手写**的地方。改完 DTO、错误码、领域枚举后，跑：

```powershell
# 1. 确保编译通过
cargo check -p wuji-core
cargo clippy -p wuji-core

# 2. 重新生成 TypeScript 绑定
WUJI_UPDATE_BINDINGS=1 cargo test -p wuji-core bindings

# 3. 确认 drift 门禁通过
cargo test -p wuji-core
```

### 不要手改的文件

- `bindings/wuji-core.ts` — 生成产物，改了也会被 drift 门禁拦截
- `apps/desktop/src/types/wuji-core.ts` — 同上，是 bindings 的副本

### tests/ 目录

`boundary.rs` 只做依赖边界检查。日常开发基本不需要往这里加东西——单元测试写在每个 `src/*.rs` 底部的 `#[cfg(test)] mod tests` 块里。

---

## 相关文档

- [09 实施基线](../../docs/dev/09-Tauri-Rust-Rebuild-v0.1%E5%AE%9E%E6%96%BD%E5%9F%BA%E7%BA%BF.md) — v0.1 实现合同（领域模型、DTO、错误码的权威来源）
- [migration-status.md](../../docs/dev/migration-status.md) — 实现与验证状态追踪
- [ADR-002](../../docs/dev/ADR-002) — 长期架构决策
- [ADR-003](../../docs/dev/ADR-003) — rebuild-only 仓库决策
