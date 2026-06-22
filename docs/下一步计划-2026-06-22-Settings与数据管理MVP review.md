我审核后认为：**这份「Settings 与数据管理 MVP」计划方向正确，可以作为下一阶段正式执行文档**。它承接阶段 5 很自然：现在 WUJI 已经完成采集、诊断、数据浏览，下一步确实应该进入“可配置、可维护、可清理”的产品闭环，而不是急着做 IPC、托盘、安装包或图表。计划里也明确了本阶段目标是让用户安全修改配置、应用隐私规则，并通过 Agent 管理历史数据。

不过这份计划里有几处我建议收紧，主要是为了降低数据清理和配置写坏的风险。

---

# 总体评价

这份计划比较成熟，优点主要有四个：

```text
1. 先读后写：先做 Settings 读取与展示，再做编辑。
2. 先低风险后高风险：先 App Settings，再 Agent Options，再 ReloadConfig，再隐私规则，最后 PruneData / ClearHistory。
3. 边界明确：WPF 不直接删除 SQLite / JSONL / runtime 文件，清理必须由 Agent 执行。
4. 风险意识充分：配置写坏、数据清理、隐私回归都单独列出来了。
```

尤其是这个顺序是对的：

```text
阶段 6.1：Settings 读取与展示
阶段 6.2：App Settings 编辑
阶段 6.3：Agent Options 校验与编辑
阶段 6.4：ReloadConfig 应用链路
阶段 6.5：隐私规则编辑
阶段 6.6：PruneData / ClearHistory
阶段 6.7：验收、稳定化与收口
```

这能避免一上来就碰 `ClearHistory` 这种高风险功能。

---

# 建议调整 1：PruneData / ClearHistory 可以拆到下一个阶段

计划里把 `PruneData / ClearHistory` 放在 6.6，这个从产品闭环角度是合理的，但从实现风险看，它可能会让阶段 6 变得过重。

我建议你考虑两种做法：

## 更稳妥方案

阶段 6 只做到：

```text
Settings 读取
App Settings 编辑
Agent Options 编辑
ReloadConfig
隐私规则编辑
```

然后单独开阶段 7：

```text
PruneData / ClearHistory 数据清理 MVP
```

这样阶段 6 可以聚焦“配置闭环”，阶段 7 专门处理“数据清理闭环”。

## 如果仍放在阶段 6

那就建议只做：

```text
PruneData MVP
```

`ClearHistory` 先留接口和文档，不做真实清理。

原因是：

```text
PruneData = 按保留天数清旧数据，风险可控
ClearHistory = 清空 SQLite / JSONL / runtime 历史，误操作和状态恢复风险更高
```

我更建议：**阶段 6 做配置和 ReloadConfig，阶段 7 再做 PruneData / ClearHistory**。

---

# 建议调整 2：ReloadConfig 应该先有“预校验”语义

你计划里写：

```text
WPF 编辑表单
↓
本地校验
↓
写入配置文件，保留备份
↓
发送 ReloadConfig
↓
Agent 重新读取并校验配置
```

这里有一个风险：**WPF 已经把 `windows-agent.json` 写了，Agent 再发现配置无效**。虽然有备份，但此时正式配置已经被无效内容污染。

建议改成更稳的两阶段语义：

```text
WPF 本地校验
    ↓
写入 windows-agent.pending.json
    ↓
发送 ReloadConfig 或 ApplyAgentOptions
    ↓
Agent 读取 pending 文件并校验
    ↓
校验通过：Agent 原子替换 windows-agent.json，写 ConfigReloaded
    ↓
校验失败：Agent 保留原正式配置，写 CommandFailed
```

如果你觉得 `pending` 机制这阶段太重，至少应做到：

```text
WPF 写正式文件前，先用与 Agent 共用的 AgentOptionsValidator 校验
写入前生成 .bak
写入后如果 ReloadConfig 失败，UI 提供 Restore Backup
```

建议把 `AgentOptionsValidator` 放在 Core 或 Infrastructure 的共享层，WPF 和 Agent 都用同一套规则，避免两边校验口径不一致。

---

# 建议调整 3：Agent 未运行时的保存语义要更明确

计划里写得对：

```text
如果 Agent 未运行，WPF 可以保存配置文件，但必须提示“将在 Agent 下次启动或 ReloadConfig 后生效”。
```

这里建议再明确 UI 状态：

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

否则用户在 Agent 未运行时点击 Save and Reload，可能会误以为已经被 Agent 应用了。

---

# 建议调整 4：App Settings 里先不要出现托盘相关可编辑项

计划里已经说：

```text
minimizeToTray / closeToTray / theme MVP 中只读或占位
```

我建议更进一步：**Settings UI 第一版不要显示托盘可配置项**，最多在文档里保留。

原因是：

```text
托盘本阶段不做
显示 closeToTray / minimizeToTray 会让用户以为功能可用
```

第一版 App Settings 只显示和编辑：

```text
refreshIntervalSeconds
autoStartAgentWhenAppStarts
lastSelectedPage 只读
```

`theme` 也先不展示，避免假入口。

---

# 建议调整 5：隐私规则编辑要先做“文本归一化预览”

`excludedProcesses` 和 `excludedTitlePatterns` 用多行文本是合理的。建议在保存前展示归一化结果：

```text
去空行
trim
去重
排序或保持输入顺序
非法项提示
```

特别是 `excludedProcesses`，建议规则更明确：

```text
只允许进程名，不允许路径
自动去掉 .exe 可选，但内部统一为不带 .exe 或统一小写
大小写不敏感
```

否则用户可能输入：

```text
notepad.exe
Notepad
C:\Windows\System32\notepad.exe
```

这三种需要有明确处理规则。

建议 MVP 采用：

```text
禁止路径
允许 .exe，但保存时归一化为不带 .exe
比较时大小写不敏感
```

---

# 建议调整 6：ReloadConfig 不只是配置读取，还要验证后续采集生效

阶段 6.4 验收里建议补一条：

```text
ReloadConfig 后，后续 sample 使用新配置
```

具体可以手动测：

```text
把 idleThresholdSeconds 改小
Save and Reload
等待超过新阈值
确认后续 sample 进入 Idle
```

隐私规则也一样：

```text
添加 notepad 到 excludedProcesses
Save and Reload
打开 Notepad
确认 samples / sessions 不出现 Notepad
Diagnostics 出现 PrivacyFiltered 泛化事件
```

这样才能证明 ReloadConfig 不是只写了 `ConfigReloaded` 事件，而是真的影响采集链路。

---

# 建议调整 7：数据管理命令需要 Maintenance 状态先落地

如果你阶段 6.6 仍然做 `PruneData / ClearHistory`，那我建议先加一个前置任务：

```text
阶段 6.6.0：Maintenance 状态与清理命令骨架
```

先做到：

```text
PruneData 命令进入 Maintenance
写 DataPruned 或 CommandCompleted 占位
finally 恢复 Running / Paused
失败时写 CommandFailed
```

再做真实删除。

不要直接从 UI 按钮进入删除逻辑。
这样能先验证状态流和事件链路。

---

# 建议调整 8：ClearHistory 需要“双确认 + 输入确认词”

`ClearHistory` 风险很高。建议计划里明确：

```text
第一次确认：普通弹窗说明会清除历史数据
第二次确认：输入 WUJI 或 CLEAR
```

MVP 可以用：

```text
输入 CLEAR 才允许继续
```

并且按钮文案不要叫“清理”，要叫：

```text
清空所有历史数据
```

避免和 `PruneData` 混淆。

---

# 建议调整 9：阶段 6.7 验收标准需要拆分“配置闭环”和“清理闭环”

现在 6.7 验收标准把所有东西放一起。建议拆成两组：

```text
配置闭环验收
    Settings 读取
    App Settings 保存
    Agent Options 保存
    ReloadConfig
    隐私规则生效

数据管理验收
    PruneData
    ClearHistory
    Maintenance
    清理后空态
```

这样如果你决定把 Prune/Clear 后置，也不会影响阶段 6 收口。

---

# 我建议修改后的阶段安排

更推荐这样：

```text
阶段 6.1：Settings 读取与展示
阶段 6.2：App Settings 编辑
阶段 6.3：Agent Options Validator 与编辑
阶段 6.4：Save / Backup / Restore 配置文件链路
阶段 6.5：ReloadConfig 应用链路
阶段 6.6：隐私规则编辑与生效验收
阶段 6.7：验收、稳定化与收口
```

然后把：

```text
PruneData / ClearHistory
```

后移成：

```text
阶段 7：数据清理 MVP
```

阶段 7 再分：

```text
7.1 Maintenance 状态
7.2 PruneData
7.3 ClearHistory
7.4 清理后 UI 空态和 Diagnostics 验收
```

这样更稳。

---

# 最终审核结论

这份计划可以执行，但我建议你**收紧阶段 6 的范围**：

```text
阶段 6 专注 Settings 与 ReloadConfig
阶段 7 单独做 PruneData / ClearHistory
```

如果你坚持在阶段 6 做数据清理，也建议只先做 `PruneData`，`ClearHistory` 后置。

我的最终建议：

```text
当前计划方向：正确
最大风险：配置写坏、ClearHistory 误删、ReloadConfig 假生效
主要修改：pending/backup/validator、Agent 未运行语义、隐私规则归一化、ClearHistory 后置
推荐下一步：先做阶段 6.1 Settings 读取与展示，不要先做编辑和清理
```

第一批提交建议：

```text
feat(settings): add settings read-only view
test(settings): load app and agent settings with safe defaults
```

这一步先做低风险、可回退的 Settings 展示层。
