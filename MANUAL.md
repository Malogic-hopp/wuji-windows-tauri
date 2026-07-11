# WUJI（吾迹）用户手册

> 版本：2026-07-11

本文档详细介绍 WUJI 桌面应用的每个界面、布局和功能，方便用户和协作者了解产品全貌。

---

## 主窗口布局

WUJI 主窗口分为三个区域：

```
┌────────────────────────────────────────────────────────────────┐
│                         标题栏                                  │
│  WUJI 吾迹                        [Agent 状态指示器] [状态消息]   │
├────────────────────────────────────────────────────────────────┤
│                         工具栏                                  │
│  [Start Agent] [Stop Agent] [Pause] [Resume] [Refresh]  [Open Settings] │
├────────────────────────────────────────────────────────────────┤
│                         页面标签区                               │
│  [Dashboard] [Apps] [Sessions] [Samples] [Diagnostics] [Insights] [Settings] │
│                                                                 │
├────────────────────────────────────────────────────────────────┤
│                                                                 │
│                        页面内容区域                               │
│                                                                 │
└────────────────────────────────────────────────────────────────┘
```

### 标题栏

- **左侧**：显示品牌名称「WUJI 吾迹」。
- **右侧**：
  - **Agent 状态指示器**（圆角药丸形）：显示当前 Agent 运行状态，如 `Running`、`Paused`、`Stopped`、`Stale` 等。背景色随状态变化。
  - **状态消息**：副文本，显示更详细的状态信息，如最后一次心跳时间。

### 工具栏

7 个按钮控制 Agent 和页面：

| 按钮 | 功能 |
|------|------|
| **Start Agent** | 启动 Agent 进程，开始后台采样 |
| **Stop Agent** | 停止 Agent 进程，关闭未结束的 Session |
| **Pause** | 暂停采集（Agent 保持运行但不记录新样本） |
| **Resume** | 从暂停恢复采集 |
| **Refresh** | 手动刷新当前页面的数据 |
| **Open Settings** | 跳转到 Settings 标签页 |

所有按钮会根据当前 Agent 状态自动启用/禁用（例如 Agent 未运行时只显示 Start Agent，Pause 仅在 Running 状态下可选）。

---

## 1. Dashboard（仪表盘）

首页总览，展示今日活动摘要和趋势。内容从上到下依次为：

### 1.1 顶部概览卡片（5 列）

| 列 | 显示内容 |
|----|----------|
| **Agent state** | 当前 Agent 状态文本（Running / Paused / Stopped / Stale 等） |
| **Last heartbeat** | Agent 最近一次心跳时间 |
| **Last sample** | 最近一次成功采样的时间 |
| **Today Active** | 今日活跃时长统计 |
| **Agent process** | Agent 进程 PID |

### 1.2 Today Insight（今日洞察）

**左侧摘要**：一段自然语言描述今日的活动概况，例如「你今天比较专注，最长连续工作块达到 45 分钟」。

**右上 6 个指标卡片（2 行 × 3 列）**：

| 指标 | 说明 |
|------|------|
| **Active** | 今日 Active 状态总时长 |
| **Sessions** | 今日 Session 数量 |
| **Samples** | 今日采样次数 |
| **任务切换** | 有效任务上下文切换次数 |
| **Longest Focus** | 今日最长连续专注时长 |
| **Time Range** | 数据覆盖的时间范围 |

**建议列表**：页面给出多条基于统计规则的个性化建议，每条包含：
- 标题、正文说明、证据数据、行动建议
- 颜色标记：绿色为正面洞察、橙色为提醒、灰色为中性信息

### 1.3 Activity Heatmap（活动热力图）

24 小时 × N 天的二维热力图，横轴为小时，纵轴为日期，颜色深浅表示该小时的活动密度。直观展示日间工作节奏和活跃模式。

### 1.4 Today 24h（今日 24 小时时间线）

当天的逐小时活跃时长堆叠柱状图，一眼看出今天的高峰和低谷时段。

### 1.5 7-Day Trend（7 日趋势）

折线图展示过去 7 天每天的活跃时长变化。下方附文字总结：
- 今日 vs 昨日活跃时长对比
- 本周 vs 上周活跃时长对比
- 今日 vs 昨日专注时长对比
- 今日 vs 昨日切换次数对比

### 1.6 Top Apps（应用排行）

过去 7 天内按使用时长排名的 Top 应用柱状图。同时显示**应用占比饼图**（App Share）。

### 1.7 Top Windows（窗口排行）

过去 7 天内采样次数最多的前 10 个窗口标题，以卡片列表的形式展示，每个卡片包含窗口标题、所属进程名、采样次数。

### 1.8 Messages（消息日志）

页面底部显示来自 ViewModel 的状态消息列表，方便调试和排错。

---

## 2. Apps（应用统计）

按应用聚合的使用数据，以表格呈现。

### 工具栏

- 左侧显示标题「Apps Today」及状态文本。
- 右侧有 **Refresh** 按钮。

### 数据表格

| 列名 | 说明 |
|------|------|
| **Rank** | 排名 |
| **Display name** | 应用的友好显示名称（通过 `ProductDisplayNameResolver` 映射） |
| **Process name** | 原始进程名 |
| **Active** | Active 状态累计时长 |
| **Total** | 使用总时长（含 Idle / Unknown） |
| **Idle** | Idle 状态累计时长 |
| **Unknown** | Unknown 状态累计时长 |
| **Sessions** | 该应用的 Session 数量 |
| **Last used local time** | 最近一次使用时间 |

无数据时显示空状态提示文本。

---

## 3. Sessions（会话记录）

按 Session 聚合的历史记录，展示每一次连续使用的时间段。

### 工具栏

- 左侧显示标题「Sessions」及状态文本。
- 右侧提供两个筛选下拉框和一个 Refresh 按钮：
  - **时间范围筛选**：Today / Yesterday / Last 7 days / Last 30 days / All
  - **关闭原因筛选**：All / Open / Paused / Stopped / PrivacyExcluded / etc.
  - 如时间范围为 Today 且正在运行，会自动刷新。

### 数据表格

| 列名 | 说明 |
|------|------|
| **Started local time** | Session 起始时间（本地时间，精确到秒） |
| **Ended local time** | Session 结束时间（本地时间）；未结束的显示为空 |
| **Process** | 进程名 |
| **Display name** | 应用显示名 |
| **Total** | 总时长 |
| **Active** | 活跃时长 |
| **Idle** | 空闲时长 |
| **Unknown** | 未知状态时长 |
| **Close reason** | Session 结束原因（Open / Paused / Stopped / 隐私规则等） |
| **Session id** | 数据库记录 ID |

---

## 4. Samples（原始采样记录）

Agent 每次采集的前台窗口原始记录，按时间倒序排列。

### 工具栏

- 左侧显示标题「Recent Samples」及状态文本。
- 右侧提供**活动状态筛选**下拉框（All / Active / Idle / Unknown）和 Refresh 按钮。

### 数据表格

| 列名 | 说明 |
|------|------|
| **Local time** | 采样时间（本地时间，精确到秒） |
| **Display name** | 应用显示名 |
| **Process** | 进程名 |
| **Window title** | 窗口标题（脱敏后） |
| **Idle seconds** | 空闲秒数 |
| **Activity** | 活动状态：Active / Idle / Unknown |
| **Sample id** | 数据库记录 ID |

窗口标题在 `maskWindowTitles=true` 时会做脱敏处理。无数据时显示空状态提示。

---

## 5. Diagnostics（诊断）

Agent 内部状态和运行健康的全面视图，帮助排错和性能分析。

### 5.1 Diagnostics Overview（诊断总览）

**左侧**：关键运行信息和路径，包括：
- 当前 JSONL 日志文件路径
- 当前 Session ID
- SQLite 写事件最近一次错误
- JSONL 写事件最近一次错误
- IPC 通道状态（可用 / 不可用 / fallback 次数）
- 页面刷新健康状态

**右上卡片**：
- SQLite write state（正常 / 异常）
- JSONL write state（正常 / 异常）

### 5.2 Tick Diagnostics（Tick 诊断）

用于 Stale 根因分析，实时展示 Agent 每个 tick 循环的性能指标：

| 字段 | 说明 |
|------|------|
| **Last phase** | 最近一次 tick 卡在哪个阶段（ControlRead / Capture / SampleInsert / SessionAggregation / Persist / PruneData / ClearHistory） |
| **Tick** | 整个 tick 循环耗时 |
| **Capture** | 前台窗口采集耗时 |
| **Persist** | 状态文件持久化耗时 |
| **Maintenance** | 数据清理操作耗时 |
| **Last tick error** | 最近一次错误的 errorCode 和消息；成功时清空 |

### 5.3 Login Startup（登录启动）

显示开机自启相关状态：
- **Login startup**：当前注册表注册状态（已注册 / 未注册）
- **Launch mode**：本次启动模式（用户手动启动 / 系统开机自启）
- **Startup registration**：启动注册详情及最近一次注册错误

### 5.4 状态文件实时查看（3 列）

| 文件 | 内容 |
|------|------|
| **runtime_state.json** | 完整 JSON 原文，含 PID、状态、心跳、版本等 |
| **health_state.json** | 完整 JSON 原文，含健康指标、Tick 诊断、错误码等 |
| **agent_control.json** | 当前待处理控制命令的 JSON 原文 |

每个文件在独立的等宽字体文本框中实时显示。

### 5.5 Recent Events（最近事件）

最近 50 条 Agent 事件 SQLite 记录。每条显示：
- 事件时间（UTC）
- 事件类型（AgentStarted / SessionStarted / SessionClosed / PrivacyFiltered / CommandAccepted 等）
- 事件级别（Info / Warning / Error / Critical）
- 消息内容
- errorCode（如有）
- requestId（如有）

无数据时显示提示：「暂无最近事件。Agent 运行后，事件会显示在这里。」

### 5.6 Recent Errors（最近错误）

最近 50 条 Warning / Error / Critical 级别事件。结构与 Events 相同，便于快速定位问题。无告警时显示：「暂无告警或错误。」

---

## 6. Insights（专注洞察）

按天查询的深度行为分析页面，提供日期导航、统计卡片和细分视图。

### 日期导航栏

工具栏左侧显示选中日期，右侧提供：
- **上一天** / **下一天** 按钮
- **DatePicker** 日历控件
- **今天** 快捷按钮
- **刷新** 按钮

选择未来日期时「下一天」按钮自动禁用。

### 统计卡片行（6 列）

| 指标 | 说明 |
|------|------|
| **任务切换** | 当日有效任务上下文切换次数 |
| **工具跳转** | 原始窗口切换次数（不计 Idle 间切换） |
| **最长工作块** | 当日最长连续工作段的持续时长 |
| **首要中断** | 打断专注的最主要应用 |
| **活跃样本** | 当日活跃采样数 |
| **预估活跃** | 当日预估活跃总时长 |

### 今日洞察（摘要卡片）

数据存在时显示自然语言摘要和一条 💡 操作建议（绿色背景）。无数据时显示空状态提示。

### 工作块（Work Blocks）

按时间顺序列出当日每个连续工作时段。每个工作块卡片包含：
- **时间段**：`HH:mm – HH:mm` + 时长（分钟）
- **标签**：绿色「专注 ✓」或黄色「碎片化」
- **详情**：主要上下文（编码 / 浏览 / 写作等）+ 主要应用 + 切换次数 + 平均切换间隔
- **说明文字**：解释为何被标记为专注或碎片化
- **内部中断列表**：被哪些应用打断及次数

### 中断来源（Interruption Sources）

列出打断专注的 Top 应用，每个应用显示：
- 应用名
- 中断次数 × 中断上下文类型

### 任务切换方向（Context Transitions）

展示最常见的上下文切换路径，例如「编码 → 聊天 → 编码」。每条记录显示：
- 来源上下文 → 目标上下文
- 切换次数和占比

---

## 7. Settings（设置）

分为 App Settings、Agent Options、Data Management、Local Data 四个区域。

### 7.1 工具栏

四个快捷按钮：
- **Refresh**：重新加载所有配置
- **Open Data Folder**：在资源管理器中打开数据目录
- **Open Logs Folder**：在资源管理器中打开日志目录
- **Open Config Folder**：在资源管理器中打开配置目录

### 7.2 App Settings（左栏）

显示 `app-settings.json` 文件路径和可编辑项：

| 设置项 | 类型 | 默认值 | 说明 |
|--------|------|--------|------|
| **refreshIntervalSeconds** | 文本框 | 15 | 页面数据刷新间隔（秒） |
| **WUJI 启动后自动启动 Agent** | 复选框 | 未勾选 | App 启动时自动启动 Agent 并开始采样 |
| **登录 Windows 后启动 WUJI** | 复选框 | 未勾选 | Windows 登录后自动启动 WUJI App（后台托盘模式） |
| **lastSelectedPage** | 只读文本 | Dashboard | 最后浏览的页面（用于下次启动恢复） |

下方有 **Save App Settings** 按钮和保存状态反馈（成功 / 失败 / 验证错误）。

### 7.3 Agent Options（右栏）

显示 `windows-agent.json` 文件路径和可编辑项：

**核心数值参数：**

| 参数 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `samplingIntervalSeconds` | 文本框 | 3 | 两次采样之间的间隔秒数 |
| `idleThresholdSeconds` | 文本框 | 60 | 无操作超过此时长判定为 Idle |
| `heartbeatIntervalSeconds` | 文本框 | 3 | 心跳文件写入间隔 |
| `staleThresholdSeconds` | 文本框 | 15 | App 判定 Agent 不健康的心跳超时 |
| `retentionDays` | 文本框 | 30 | PruneData 时的数据保留天数 |

**开关参数（复选框）：**

| 参数 | 默认值 | 说明 |
|------|--------|------|
| `enableJsonlJournal` | 勾选 | 是否同时写 JSONL 日志文件 |
| `enableAgentEventJournal` | 勾选 | 是否在 SQLite 中记录 Agent 事件 |
| `enableSessionMerge` | 勾选 | 是否将相邻同应用 Session 自动合并 |
| `maskWindowTitles` | 勾选 | 是否脱敏窗口标题（隐私保护） |

**列表参数（多行文本框，每行一条）：**

| 参数 | 默认值 | 说明 |
|------|--------|------|
| `excludedProcesses` | KeePass / 1Password / Bitwarden / explorer | 不采样的进程名列表 |
| `excludedTitlePatterns` | InPrivate | 不采样的窗口标题匹配模式（包含即忽略） |

操作按钮行：
- **Save Agent Options**：保存配置到文件
- **Save and Reload**：保存并通知 Agent 重载配置
- **Reload Agent Config**：通知 Agent 从文件重新加载配置
- **Restore Backup**：从备份文件恢复配置
- **Validate Agent Options**：校验当前编辑的配置是否合法
- **Reset Agent Options**：重置编辑器到已保存的配置内容

反馈区域显示验证结果、保存状态、重载状态。错误时红色文字提示具体原因。

**Normalized preview（归一化预览）**：展示经过校验器归一化后的实际生效值，包括 `excludedProcesses` / `excludedTitlePatterns` 的归一化列表、`idleSummaryIntervalMinutes`、`useMockCapture` 等只读字段。

### 7.4 Data Management（数据管理）

| 操作 | 说明 |
|------|------|
| **Prune Old Data** | 按 `retentionDays` 清理过期数据（foreground_samples、app_sessions、agent_events、JSONL 日志） |
| **Clear All History** | 清空所有历史数据。需要输入 "CLEAR" 二次确认后方可执行 |

每次操作后显示结果反馈（删除行数、错误信息等）。

### 7.5 Local Data（本地数据路径）

纯展示区域，列出所有关键目录和文件的实际路径：
- data root（`%LOCALAPPDATA%\WUJI\WindowsAgent`）
- config directory
- database path
- logs directory
- runtime directory

---

## 8. 系统托盘

Agent 运行时，系统托盘显示 WUJI 图标。托盘功能：

### 托盘菜单

```
┌──────────────┐
│ WUJI 吾迹    │  ← 双击打开 / 恢复主窗口
│ Agent: xxx   │  ← 当前 Agent 状态（Running / Paused / Stopped 等）
├──────────────┤
│ Show Window  │  ← 显示主窗口
│ Start Agent  │  ← 启动 Agent（仅在 Agent 未运行时可用）
│ Stop Agent   │  ← 停止 Agent（仅在 Agent 运行时可用）
│ Pause        │  ← 暂停采样（仅在 Running 时可用）
│ Resume       │  ← 恢复采样（仅在 Paused 时可用）
├──────────────┤
│ Exit         │  ← 退出 WUJI（停止 Agent 并关闭应用）
└──────────────┘
```

### 托盘行为

- **双击托盘图标**：显示/恢复主窗口。
- **CloseToTray**（默认开启）：关闭主窗口时最小化到托盘而非退出应用。
- **MinimizeToTray**（默认开启）：最小化主窗口时隐藏到托盘。
- **Auto-start 隐藏启动**：如果配置了开机自启且以 `--from-autostart --start-hidden` 启动，主窗口不会自动弹出，Agent 静默运行在后台。

从托盘退出时会同时停止 Agent 进程（如果有且正在运行）。
