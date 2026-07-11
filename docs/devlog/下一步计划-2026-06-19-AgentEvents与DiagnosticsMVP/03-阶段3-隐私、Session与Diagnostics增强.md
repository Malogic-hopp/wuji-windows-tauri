# 阶段 3：隐私、Session 与 Diagnostics 增强

## 阶段目标

把事件系统补齐到“能解释关键异常”的程度，同时让 Diagnostics 页面真正能用于排错。

这阶段重点不是增加更多噪声事件，而是把隐私命中、采样异常、Session 变化和最近错误展示清楚。

## 解决的问题

- 需要知道为什么某些窗口没有被采样。
- 需要知道为什么某些命令被拒绝或被隔离。
- 需要在 Diagnostics 中快速看到最近事件和最近错误。

## 交付物

- `PrivacyFiltered` / `CaptureFailed` 事件接入
- `SessionStarted` / `SessionClosed` 事件接入
- 事件限流
- payload 白名单清洗
- Diagnostics 的 Recent Events / Recent Errors

## 实施范围

### 隐私和采样异常

必须保证：

- 不写真实窗口标题
- 不写原始路径、命令行、完整异常堆栈
- `PrivacyFiltered` 只写泛化原因
- `CaptureFailed` 只写 `errorCode` / `exceptionType` / `shortMessage`

### Session

建议只记录轻量事件：

- `SessionStarted`
- `SessionClosed`

不要新增独立的 `ProcessChanged` 事件类型；如果需要表达进程变化，写入 `SessionClosed` 的 `closeReason` 即可。

### Diagnostics 增强

建议新增展示：

- Recent Events
- Recent Errors
- SQLite 写入状态
- JSONL 写入状态
- 当前 JSONL 文件路径
- 最近一次 SQLite 写入错误
- 最近一次 JSONL 写入错误
- 打开日志目录按钮

## 验收标准

- Diagnostics 能展示最近事件
- Diagnostics 能展示最近错误
- 隐私事件不会泄露原始标题和敏感路径
- `PrivacyFiltered` / `CaptureFailed` 能被限流
- Session 变化能在事件中看见
- JSONL 只显示路径或入口，不在 UI 中解析

## 不做什么

- 不做 JSONL 浏览器
- 不做复杂图表
- 不做完整 Settings 编辑页
- 不做 IPC

