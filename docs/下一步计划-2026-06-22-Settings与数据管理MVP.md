# 下一步计划：Settings 与配置应用 MVP（2026-06-22，review 后修订版）

本文档作为 `下一步计划-2026-06-22-SamplesSessionsApps数据浏览MVP.md` 完成后的下一阶段正式计划。

上一阶段已经完成：

```text
Samples / Sessions / Apps 数据浏览 MVP
```

当前项目已经从：

```text
能采集、能诊断
```

推进到：

```text
能浏览、能筛选、能解释历史使用明细
```

下一步不建议马上进入 Named Pipe / gRPC、托盘、安装包、7 天趋势、图表、应用分类或数据清理。  
更优先的是让用户能安全查看、修改、校验并应用配置，尤其是采集参数和隐私规则。

---

## 当前状态

当前 WUJI 已具备：

```text
真实 Win32 前台窗口采样
idle / active 判断
采集阶段隐私过滤
foreground_samples 落库
app_sessions 合并
Pause / Resume / Stop 控制
Dashboard 今日统计
Diagnostics 最近事件 / 最近错误
agent_events SQLite 查询索引
agent_events_YYYYMMDD.jsonl 审计日志
SamplesView 最近样本浏览
SessionsView 会话浏览和 close_reason 筛选
AppsView 今日应用排行
Dashboard Top Apps 与 AppsView Today 统计口径一致
```

上一阶段已完成验收：

```text
dotnet build QuantifiedSelf.Windows.sln --no-restore -p:BaseOutputPath=.codex\build-output\
    通过，0 warnings / 0 errors

dotnet test QuantifiedSelf.Windows.sln --no-restore -p:BaseOutputPath=.codex\test-output\
    通过，55/55
```

这说明采集、诊断和数据浏览三条主链路已经具备继续扩展 Settings 与配置应用能力的基础。

---

## Review 吸收结论

已吸收的建议：

```text
1. 阶段 6 收紧为 Settings / 配置校验 / ReloadConfig / 隐私规则生效闭环
2. PruneData / ClearHistory 后移为阶段 7 数据清理 MVP
3. 增加 AgentOptionsValidator，共享给 WPF 和 Agent
4. 增加 Save / Backup / Restore 配置文件链路
5. 明确 Agent Running / Paused / NotRunning 时 Save / Reload 的 UI 语义
6. App Settings 第一版不展示托盘和主题假入口
7. 隐私规则编辑增加归一化预览
8. ReloadConfig 验收增加“后续采集真实生效”
9. ClearHistory 双确认和 Maintenance 状态要求移入阶段 7 风险约束
```

本轮复审后继续补充：

```text
1. retentionDays 阶段 6 只保存配置，不立即清理历史数据
2. enableJsonlJournal / enableAgentEventJournal 开工前必须核对现有字段语义
3. ReloadConfig 真实生效优先验收 idleThresholdSeconds 和 excludedProcesses
4. Restore Backup 只恢复文件，不自动宣称 Agent 已应用
```

暂不直接吸收为阶段 6 必做项：

```text
windows-agent.pending.json
```

原因：

```text
pending 文件机制方向正确，但会扩大阶段 6 实施面。
阶段 6 MVP 先采用共享 Validator + .bak + 临时文件原子替换 + Restore Backup。
如果后续发现 ReloadConfig 失败恢复体验仍不够稳，再升级为 pending 文件机制。
```

---

## 下一阶段目标

下一阶段目标命名为：

```text
Settings 与配置应用 MVP
```

一句话目标：

```text
让用户能安全修改采集配置和隐私规则，并能确认这些修改被 Agent 应用。
```

这一阶段重点补齐：

```text
SettingsView
    展示和编辑 App / Agent 配置

配置校验
    避免无效配置写坏正式文件

ReloadConfig
    保存配置后让 Agent 明确应用或明确失败

隐私规则编辑
    修改 maskWindowTitles、excludedProcesses、excludedTitlePatterns

Save / Backup / Restore
    避免无效配置覆盖正式配置
```

阶段完成后应满足：

```text
1. 用户能看到当前 App 配置和 Agent 配置
2. 用户能编辑基础采集参数
3. 用户能编辑标题脱敏和排除规则
4. 配置保存前有本地校验
5. ReloadConfig 成功 / 失败在 Diagnostics 可见
6. 无效配置不会破坏正式配置
7. Agent 未运行时保存语义清晰，不误导用户
8. 隐私规则更新后能证明对后续采集生效
9. Dashboard / Apps / Sessions / Samples / Diagnostics 不回归
```

---

## 为什么现在做这个

阶段 5 之后，用户已经能看到：

```text
最近采样
会话边界
应用排行
今日统计
诊断事件
```

用户下一步自然会需要控制这些数据如何产生：

```text
采样间隔是否太密？
idle 阈值是否合适？
窗口标题是否应该脱敏？
哪些应用应该完全排除？
哪些标题应该排除？
数据保留多久？
配置修改后 Agent 是否真的生效？
```

如果没有 Settings 与配置应用能力，WUJI 仍然更像一个开发期工具。  
完成本阶段后，它才具备更完整的本地 MVP 产品闭环：

```text
采集
诊断
浏览
配置
```

---

## 本阶段不做

本阶段暂不做：

```text
Named Pipe / gRPC 主控制通道
Agent 状态流订阅
托盘 TrayService
开机自启
安装包
Windows Service
7 天趋势
复杂图表
CSV / Excel 导出
PruneData
ClearHistory
应用分类编辑
浏览器网页级识别
完整主题系统
复杂 Settings 分组和搜索
```

这些属于后续产品化、体验增强或通信通道升级。  
当前阶段只做“能安全配置并确认配置生效”的 MVP。  
数据清理能力单独后移到阶段 7，避免把配置闭环和高风险删除链路混在一起。

---

## 设计原则

```text
1. Agent 仍然是采集域配置的最终应用者
2. WPF 可以编辑配置表单，但不能假装配置已经被 Agent 应用
3. app-settings.json 属于 WPF App，可由 WPF 直接保存
4. windows-agent.json 属于 Agent 采集域，保存和热加载必须有清晰结果
5. 无效配置不能覆盖正式配置
6. 保存前先校验，保存时先写临时文件或备份
7. AgentOptionsValidator 应放在 Core 或可被 WPF / Agent 共同引用的层
8. WPF 和 Agent 使用同一套 Agent Options 校验规则
9. ReloadConfig 必须产生可诊断结果
10. ReloadConfig 不只验证事件写入，还要验证后续采集使用新配置
11. 隐私规则必须在采集阶段生效，UI 展示层脱敏不能替代采集阶段脱敏
12. Settings 页不应破坏 Dashboard / Apps / Sessions / Samples / Diagnostics 的现有刷新语义
13. 所有错误提示避免暴露本机敏感路径，必要时走 DiagnosticMessageSanitizer
14. retentionDays 阶段 6 只作为配置项保存，不触发实际历史数据清理
15. Restore Backup 只恢复配置文件，不自动 Reload
16. PruneData / ClearHistory 暂不进入阶段 6，后续阶段仍必须由 Agent 执行
```

---

## 配置边界

### App 配置

App 配置属于 WPF 自己：

```text
config/app-settings.json
```

第一版建议支持：

```text
refreshIntervalSeconds
lastSelectedPage
autoStartAgentWhenAppStarts
```

MVP 中可编辑：

```text
refreshIntervalSeconds
autoStartAgentWhenAppStarts
```

MVP 中只读展示：

```text
lastSelectedPage
```

原因：

```text
托盘和主题系统本阶段不做。
minimizeToTray / closeToTray / theme 第一版不出现在 Settings UI 中，避免出现无实际效果的假入口。
```

### Agent 配置

Agent 配置属于采集域：

```text
config/windows-agent.json
```

第一版建议支持：

```text
samplingIntervalSeconds
idleThresholdSeconds
heartbeatIntervalSeconds
staleThresholdSeconds
retentionDays
enableJsonlJournal
enableSessionMerge
maskWindowTitles
excludedProcesses
excludedTitlePatterns
```

保存语义：

```text
WPF 编辑表单
    ↓
使用 AgentOptionsValidator 本地校验
    ↓
写入配置文件，保留备份，或写入 pending 文件
    ↓
发送 ReloadConfig 命令
    ↓
Agent 使用同一个 AgentOptionsValidator 重新读取并校验配置
    ↓
成功：ConfigReloaded / CommandCompleted
失败：CommandFailed + errorCode
```

更稳妥的长期语义：

```text
WPF 本地校验
    ↓
写入 windows-agent.pending.json
    ↓
发送 ApplyAgentOptions 或 ReloadConfig
    ↓
Agent 读取 pending 文件并校验
    ↓
校验通过：Agent 原子替换 windows-agent.json，写 ConfigReloaded
    ↓
校验失败：Agent 保留原正式配置，写 CommandFailed
```

MVP 可以暂不实现 pending 文件，但必须做到：

```text
1. WPF 和 Agent 共享 AgentOptionsValidator
2. 写正式文件前创建 .bak
3. 写正式文件使用临时文件 + 原子替换
4. ReloadConfig 失败时 UI 保留错误提示，并提供 Restore Backup
```

注意：

```text
Agent Running:
    Save and Reload 可用

Agent Paused:
    Save and Reload 可用，Reload 后保持 Paused 或按当前状态继续

Agent NotRunning:
    Save 可用
    Reload 不可用
    显示“将在下次启动时生效”
```

---

## 推荐开发顺序

建议分成 7 个小阶段：

```text
阶段 6.1：Settings 读取与展示
阶段 6.2：App Settings 编辑
阶段 6.3：Agent Options Validator 与编辑
阶段 6.4：Save / Backup / Restore 配置文件链路
阶段 6.5：ReloadConfig 应用链路
阶段 6.6：隐私规则编辑与生效验收
阶段 6.7：验收、稳定化与收口
```

这个顺序先做低风险读取，再做 WPF 自有配置，然后再进入采集域配置和隐私规则。  
PruneData / ClearHistory 后移为阶段 7 数据清理 MVP。

---

# 阶段 6.1：Settings 读取与展示

## 阶段目标

让 Settings 页能清晰展示当前配置和数据路径，但暂不编辑。

## 建议新增或调整

建议拆出：

```text
src/QuantifiedSelf.Windows.App/Views/SettingsView.xaml
src/QuantifiedSelf.Windows.App/ViewModels/SettingsViewModel.cs
```

如果暂时不拆完整 Diagnostics，本阶段也至少应把 Settings 从 `MainWindow.xaml` 中继续收口为独立 UserControl。

## 页面内容

第一版展示：

```text
App Settings
    app-settings.json 路径
    refreshIntervalSeconds
    autoStartAgentWhenAppStarts
    lastSelectedPage

Agent Options
    windows-agent.json 路径
    samplingIntervalSeconds
    idleThresholdSeconds
    heartbeatIntervalSeconds
    staleThresholdSeconds
    retentionDays
    maskWindowTitles
    excludedProcesses
    excludedTitlePatterns

Local Data
    data root
    database path
    logs directory
    runtime directory
```

## 交互

第一版支持：

```text
Refresh
Open Data Folder
Open Logs Folder
Open Config Folder
```

## 验收标准

- SettingsView 独立 UserControl。
- SettingsViewModel 能读取 App Settings。
- SettingsViewModel 能读取 Agent Options。
- 配置缺失时显示默认值，不报错。
- JSON 读取失败时显示安全错误文本。
- 打开数据 / 日志 / 配置目录按钮可用。
- Dashboard / Apps / Sessions / Samples / Diagnostics 不回归。

## 建议测试

```text
SettingsViewModel_LoadsAppSettings
SettingsViewModel_LoadsAgentOptions
SettingsViewModel_ReturnsDefaultsWhenFilesMissing
SettingsViewModel_RedactsInvalidJsonLoadFailure
```

---

# 阶段 6.2：App Settings 编辑

## 阶段目标

先支持 WPF 自有配置编辑，建立 Settings 表单、校验、保存和状态提示模式。

## MVP 编辑字段

```text
refreshIntervalSeconds
autoStartAgentWhenAppStarts
```

第一版不展示：

```text
theme
minimizeToTray
closeToTray
```

原因：

```text
托盘和主题系统本阶段不做。
不要在 Settings UI 中出现用户以为可用、实际无效果的假入口。
```

## 校验规则

```text
refreshIntervalSeconds
    最小 5
    最大 300

autoStartAgentWhenAppStarts
    bool
```

## 保存语义

```text
点击 Save App Settings
    ↓
校验
    ↓
保存 app-settings.json
    ↓
立即更新 MainWindow 刷新间隔
    ↓
显示保存成功
```

## 验收标准

- 修改 `refreshIntervalSeconds` 后能保存到 `app-settings.json`。
- 刷新间隔在当前 App 进程内生效。
- 无效值不会写入。
- 保存失败时显示安全错误文本。

## 建议测试

```text
SettingsViewModel_SavesAppSettings
SettingsViewModel_RejectsInvalidRefreshInterval
MainWindowViewModel_AppliesRefreshIntervalAfterSettingsSave
```

---

# 阶段 6.3：Agent Options Validator 与编辑

## 阶段目标

先建立 WPF 和 Agent 共享的 Agent Options 校验口径，再支持编辑 Agent 基础采集配置。

不要让 WPF 和 Agent 各写一套校验规则。  
否则会出现 WPF 认为合法、Agent ReloadConfig 后拒绝的口径漂移。

## 建议新增

建议新增：

```text
src/QuantifiedSelf.Windows.Core/Options/AgentOptionsValidator.cs
```

或放在同等可共享层，要求：

```text
WPF SettingsViewModel 使用
AgentStateMachine ReloadConfig 使用
测试直接覆盖
```

## MVP 编辑字段

```text
samplingIntervalSeconds
idleThresholdSeconds
heartbeatIntervalSeconds
staleThresholdSeconds
retentionDays
enableJsonlJournal
enableAgentEventJournal
enableSessionMerge
maskWindowTitles
```

## 校验规则建议

```text
samplingIntervalSeconds
    最小 1
    最大 60

idleThresholdSeconds
    最小 10
    最大 3600

heartbeatIntervalSeconds
    最小 1
    最大 60

staleThresholdSeconds
    必须大于 heartbeatIntervalSeconds
    最大 600

retentionDays
    最小 1
    最大 3650
    阶段 6 只保存配置，不立即清理历史数据
    实际清理在阶段 7 的 PruneData 中执行
```

建议额外校验：

```text
idleThresholdSeconds >= samplingIntervalSeconds
staleThresholdSeconds >= heartbeatIntervalSeconds * 2
excludedProcesses 不允许路径
excludedTitlePatterns 不允许空白项
```

## 编辑策略

本阶段只负责：

```text
表单绑定
本地校验
显示校验错误
生成规范化后的 WindowsAgentOptions 对象
```

真实写文件、备份和恢复放到阶段 6.4。

## 字段语义核对

开工前必须核对 `WindowsAgentOptions` 的真实字段：

```text
EnableJsonlJournal
    控制 foreground samples JSONL 或通用 JSONL journal

EnableAgentEventJournal
    控制 agent_events_YYYYMMDD.jsonl
```

如果代码中两个字段语义不同，Settings UI 必须写清楚。  
如果后续发现实际代码只有一个字段，不要在 UI 中凭空新增另一个配置项。

## 验收标准

- SettingsView 能编辑基础 Agent Options。
- AgentOptionsValidator 能拦截无效间隔。
- WPF 和 Agent 能复用同一个 AgentOptionsValidator。
- `maskWindowTitles` 修改不会在 UI 层造成标题泄露。

## 建议测试

```text
AgentOptionsValidator_AcceptsValidOptions
AgentOptionsValidator_RejectsInvalidIntervals
AgentOptionsValidator_RejectsInvalidRetentionDays
SettingsViewModel_RejectsInvalidAgentOptions
SettingsViewModel_BuildsValidAgentOptionsDraft
```

---

# 阶段 6.4：Save / Backup / Restore 配置文件链路

## 阶段目标

在不破坏正式配置的前提下，把合法 Agent Options 保存到磁盘。

本阶段只解决：

```text
怎么安全保存
怎么备份
怎么恢复
怎么避免无效配置覆盖正式文件
```

不要求 Agent 立即应用，应用链路放到阶段 6.5。

## 保存策略

MVP 必须做到：

```text
1. 保存前调用 AgentOptionsValidator
2. 写入 windows-agent.json 前创建 .bak
3. 写入使用临时文件 + 原子替换
4. 保存后不直接宣称已生效
5. 保存后显示“等待 ReloadConfig / 下次启动生效”
6. 保存失败时保留旧配置
7. 提供 Restore Backup
```

长期更稳语义：

```text
windows-agent.pending.json
```

可以后置。MVP 中只要 `.bak + validator + 原子替换 + restore` 做扎实即可。

## Agent 状态语义

```text
Agent Running:
    Save 可用
    Save and Reload 可用

Agent Paused:
    Save 可用
    Save and Reload 可用
    Reload 后应保持 Paused 或按当前状态继续

Agent NotRunning:
    Save 可用
    Reload 不可用
    Save and Reload 不可用
    显示“将在下次启动时生效”
```

## Restore Backup 语义

```text
Restore Backup 只恢复 windows-agent.json 文件
不会自动宣称运行中的 Agent 已应用恢复后的配置
恢复后仍需 ReloadConfig 或等待 Agent 下次启动
```

## 验收标准

- 合法 Agent Options 能保存到 `windows-agent.json`。
- 保存前会创建 `.bak`。
- 无效配置不会覆盖正式文件。
- 保存失败时旧配置仍可读取。
- Restore Backup 能恢复上一份配置。
- Restore Backup 后 UI 提示“已恢复文件，等待 ReloadConfig / 下次启动生效”。
- Agent 未运行时不会显示“Reload 已生效”。

## 建议测试

```text
SettingsViewModel_SavesAgentOptionsWithBackup
SettingsViewModel_DoesNotOverwriteAgentOptionsWhenInvalid
SettingsViewModel_RestoresAgentOptionsBackup
SettingsViewModel_DisablesReloadWhenAgentNotRunning
```

---

# 阶段 6.5：ReloadConfig 应用链路

## 阶段目标

让用户保存 Agent Options 后，可以明确请求 Agent 重新加载配置，并在 UI / Diagnostics 中看到结果。

## 当前基础

已有控制命令：

```text
ReloadConfig
```

已有事件基础：

```text
CommandDetected
CommandAccepted
CommandCompleted
CommandFailed
ConfigReloaded
```

## 交互

Settings 页建议提供：

```text
Save Agent Options
Reload Agent Config
Save and Reload
```

按钮可用性：

```text
Agent Running / Paused:
    Reload Agent Config 可用
    Save and Reload 可用

Agent NotRunning:
    Reload Agent Config 不可用
    Save and Reload 不可用
    只允许 Save
```

## 状态反馈

UI 应显示：

```text
配置已保存，等待 ReloadConfig
ReloadConfig command queued
ConfigReloaded observed
ReloadConfig failed: <safe message>
Agent 未运行，配置将在下次启动时读取
```

## 验收标准

- 点击 ReloadConfig 能写入控制命令。
- Diagnostics 能看到 ReloadConfig 相关事件。
- 成功时能看到 `ConfigReloaded` 或 `CommandCompleted`。
- 失败时能看到 `CommandFailed` 和 `errorCode`。
- ReloadConfig 后，后续 sample 使用新配置。
- 修改 `idleThresholdSeconds` 后，后续 idle 判断使用新阈值。
- 修改 `excludedProcesses` 后，对应进程不再进入 samples / sessions，并能在 Diagnostics 看到泛化的 `PrivacyFiltered`。
- 坏配置不会让 UI 崩溃。
- 错误文本不泄露敏感路径。

优先手动验收：

```text
idleThresholdSeconds
    改小后，等待超过新阈值，确认后续 sample 更快进入 Idle

excludedProcesses
    添加 notepad，ReloadConfig 后打开 Notepad
    确认 Notepad 不进入 samples / sessions
    Diagnostics 出现 PrivacyFiltered，且只显示泛化原因
```

`samplingIntervalSeconds` 可以作为补充验收项，但它受 timer 周期和刷新时机影响，优先级低于 idle / privacy。

## 建议测试

```text
SettingsViewModel_QueuesReloadConfigCommand
AgentStateMachine_ReloadConfigAppliesValidOptions
AgentStateMachine_ReloadConfigReportsInvalidOptions
AgentStateMachine_ReloadConfigAffectsSubsequentSampling
ReloadConfigFailure_DoesNotLeakRawPath
```

---

# 阶段 6.6：隐私规则编辑与生效验收

## 阶段目标

让用户能编辑最关键的隐私规则：

```text
maskWindowTitles
excludedProcesses
excludedTitlePatterns
```

## UI 建议

第一版简单即可：

```text
maskWindowTitles
    Toggle

excludedProcesses
    多行文本，每行一个 process name

excludedTitlePatterns
    多行文本，每行一个 wildcard pattern
```

保存前显示归一化预览：

```text
Normalized excluded processes
Normalized title patterns
Validation errors
```

暂不做：

```text
复杂规则编辑器
per-app 隐私规则
规则优先级
正则表达式模式
规则命中预览
```

## 校验规则

```text
excludedProcesses
    去空行
    去重
    trim
    不允许路径
    不允许过长条目
    允许 .exe 输入，但保存时归一化为不带 .exe
    比较时大小写不敏感

excludedTitlePatterns
    去空行
    去重
    trim
    限制单条长度
    限制总数量
```

## 验收标准

- 修改 `maskWindowTitles=true` 后，后续 sample 不包含真实标题。
- 修改 `excludedProcesses` 后，对应进程不再进入 samples / sessions。
- 修改 `excludedTitlePatterns` 后，命中标题不落库。
- Diagnostics 的 `PrivacyFiltered` 只显示泛化原因。
- UI 不在提示文案中回显敏感窗口标题。
- 输入 `notepad.exe` 可归一化为 `notepad`。
- 输入 `C:\Windows\System32\notepad.exe` 会被拒绝为路径。

## 建议测试

```text
PrivacyRuleEditor_NormalizesExcludedProcesses
PrivacyRuleEditor_RejectsProcessPaths
PrivacyRuleEditor_NormalizesTitlePatterns
AgentStateMachine_AppliesUpdatedExcludedProcesses
AgentStateMachine_AppliesUpdatedTitlePatterns
PrivacyFiltered_DoesNotLeakMatchedTitleAfterSettingsUpdate
```

---

# 阶段 6.7：验收、稳定化与收口

## 配置闭环自动化验收

完成后应满足：

```text
1. dotnet build 0 warning / 0 error
2. dotnet test 全部通过
3. SettingsView 能显示 App 配置
4. SettingsView 能显示 Agent 配置
5. SettingsView 能编辑 App 配置
6. SettingsView 能编辑 Agent 基础配置
7. 配置校验能拦截无效值
8. 保存失败不破坏旧配置
9. ReloadConfig 成功 / 失败有 Diagnostics 证据
10. 隐私规则修改后对后续采集生效
11. Agent 未运行时 Reload 不可用且提示下次启动生效
12. Dashboard / Apps / Sessions / Samples / Diagnostics 不回归
```

## 手动验收流程

建议手动验证：

```text
1. 打开 Settings，确认 App Settings / Agent Options 显示正常
2. 修改 refreshIntervalSeconds，保存后确认自动刷新间隔变化
3. 修改 samplingIntervalSeconds，Save and Reload
4. 观察 Diagnostics 中 ReloadConfig / ConfigReloaded
5. 修改 idleThresholdSeconds，观察后续 idle 判断变化
6. 开启 maskWindowTitles，确认 Samples 不显示真实标题
7. 添加 excludedProcesses，确认对应进程不进入 samples / sessions
8. 添加 excludedTitlePatterns，确认命中标题不落库
```

## 收口文档

阶段完成后建议新增：

```text
docs/下一步计划-2026-06-22-Settings与数据管理MVP/完成情况说明.md
docs/下一步计划-2026-06-22-Settings与数据管理MVP/阶段6.7-验收清单.md
```

---

## 后续候补

Settings 与配置应用 MVP 完成后，再进入：

```text
1. 阶段 7：PruneData / ClearHistory 数据清理 MVP
2. Named Pipe / gRPC over Named Pipes 主控制通道
3. Agent 状态流订阅 / RefreshService 优化
4. 托盘
5. 开机自启
6. 安装包
7. 7 天趋势和图表
8. 应用分类
9. 浏览器网页级识别
10. 数据导出
11. 本地数据加密
```

---

## 风险与注意事项

### 配置写坏风险

必须避免：

```text
无效配置覆盖 windows-agent.json
ReloadConfig 失败但 UI 显示成功
Agent 读取坏配置后进入不可恢复状态
```

建议：

```text
保存前校验
保存前备份
保存使用临时文件
ReloadConfig 失败时保留错误提示
必要时提供 Restore Backup
```

### 阶段 7 数据清理风险

阶段 6 不实施真实数据清理。  
阶段 7 开始前必须先补 Maintenance 状态与清理命令骨架，再做真实删除。

阶段 7 必须避免：

```text
WPF 直接删除数据库
Agent 写入过程中数据库被清理
ClearHistory 后 runtime_state 丢失导致 UI 显示 Stale
清理失败后 Agent 卡在 Maintenance
```

建议：

```text
清理命令先进入 Maintenance
清理只由 Agent 执行
清理前关闭当前 session
清理期间进入 Maintenance
清理成功或失败都写事件
finally 中恢复可观测状态
ClearHistory 使用双确认 + 输入 CLEAR 确认词
ClearHistory 按钮文案使用“清空所有历史数据”
```

### 隐私回归风险

必须避免：

```text
Settings 页面回显敏感窗口标题
错误提示泄露本机路径
PrivacyFiltered payload 泄露真实标题
SamplesView 展示完整 executable_path
```

建议：

```text
沿用 DiagnosticMessageSanitizer
继续执行 payload 白名单
隐私命中事件只写泛化原因
数据浏览页不展示完整路径
```

---

## 最终结论

阶段 5 完成后，WUJI 已经具备：

```text
采集
诊断
数据浏览
```

下一步最自然、最有价值的是：

```text
Settings 与配置应用 MVP
```

这一阶段完成后，WUJI 将从“可观察的本地采集工具”推进到“可配置、可验证配置生效的本地使用分析产品”。
