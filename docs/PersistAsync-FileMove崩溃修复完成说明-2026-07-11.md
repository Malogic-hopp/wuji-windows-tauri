# PersistAsync FileMove 崩溃修复完成说明

> 日期：2026-07-11
> 关联：Stale 根因修复与可观测性增强方案（[方案](./Stale根因修复与可观测性增强方案-2026-07-10.md) / [完成说明](./Stale修复完成说明-2026-07-10.md)）

## 故障摘要

**时间**：2026-07-10 晚约 22:03（北京时间）

**现象**：App 显示 Agent 为 Stale，自昨晚 22 点起持续。Diagnostics 未提示明显异常。

**根因**（高度疑似，由事件链路支持）：Agent 写 `runtime_state.json` 时，`File.Move(.tmp → .json)` 因文件访问冲突抛出 `UnauthorizedAccessException`。`RuntimeStateStore.ReadAsync` 原本使用 `File.OpenRead`（仅 `FileShare.Read`），与 `File.Move(…, overwrite: true)` 并发时存在竞态风险。Windows 事件日志显示崩溃点恰在 `FileSystem.MoveFile`，且 App 侧 IPC 不可用状态下正在以 2 秒间隔执行 file fallback 读取。异常未被 PersistAsync 捕获，沿调用链上抛至 `BackgroundService`，导致 Agent 进程直接退出。旧的 `runtime_state.json` 残留 Running 状态，App 读后发现进程不存在 → 显示 Stale。

## 推断的崩溃链路

```
App (2 秒轮询)                          Agent (1 秒 tick)
    │                                        │
    ├─ RuntimeStateStore.ReadAsync()          │
    │   └─ File.OpenRead(runtime_state.json)  │
    │      → FileShare.Read ONLY ─────────────────┐
    │                                        │    │  读句柄阻止 Move 操作
    │                                        ├─ PersistAsync()
    │                                        │   ├─ WriteAsync → .tmp 写成功 ✓
    │                                        │   └─ File.Move(.tmp → .json)
    │                                        │       └─ ❌ UnauthorizedAccessException
    │                                        ├─ 异常未被 catch
    │                                        └─ BackgroundService 崩溃 → Agent 退出
    ├─ 读到旧 runtime_state.json              │
    │  PID 36600 已不存在                     │
    └─ 显示 Stale                             │
```

**Windows 事件日志佐证**：

```text
2026-07-10 22:03:41  .NET Runtime
BackgroundService failed
System.UnauthorizedAccessException: Access to the path is denied.
   at System.IO.FileSystem.MoveFile(...)
   at RuntimeStateStore.WriteAsync(...)
   at AgentStateMachine.PersistAsync(...)
   at AgentStateMachine.TickAsync(...)
```

## 修复方案

三层防御，从根因到兜底逐层拦截：

| 层 | 文件 | 修复 |
|---|---|---|
| 1 读端 | [RuntimeStateStore.cs](../src/QuantifiedSelf.Windows.Infrastructure/RuntimeState/RuntimeStateStore.cs) | `ReadAsync`: `File.OpenRead` → `FileStream(..., FileShare.ReadWrite\|FileShare.Delete)` |
| 1 读端 | [AgentHealthStateStore.cs](../src/QuantifiedSelf.Windows.Infrastructure/RuntimeState/AgentHealthStateStore.cs) | 同上 |
| 2 写端 | [RuntimeStateStore.cs](../src/QuantifiedSelf.Windows.Infrastructure/RuntimeState/RuntimeStateStore.cs) | `WriteAsync`: `File.Move(overwrite:true)` → `MoveWithRetryAsync`（`File.Delete` + `File.Move`，3 次重试，间隔 50ms） |
| 2 写端 | [AgentHealthStateStore.cs](../src/QuantifiedSelf.Windows.Infrastructure/RuntimeState/AgentHealthStateStore.cs) | 同上 |
| 3 调用端 | [AgentStateMachine.cs](../src/QuantifiedSelf.Windows.Agent/State/AgentStateMachine.cs) | `PersistAsync` 内 `WriteAsync` 调用加 try/catch（放行 `OperationCanceledException`），异常记日志不传播 |
| 兜底 | [Worker.cs](../src/QuantifiedSelf.Windows.Agent/Worker.cs) | `TickAsync` 调用加 try/catch（放行 `OperationCanceledException`），未处理异常只记日志不杀进程 |

### 关键代码变化

#### 读端：FileShare 放开写和删除权限

```csharp
// Before
await using var stream = File.OpenRead(path);

// After
await using var stream = new FileStream(
    path, FileMode.Open, FileAccess.Read,
    FileShare.ReadWrite | FileShare.Delete);
```

#### 写端：Delete + Move 替换 Move(overwrite:true)

`File.Move(..., overwrite: true)` 在 Windows 上使用 `MoveFileEx(MOVEFILE_REPLACE_EXISTING)`，即使读端已授予 `FileShare.Delete` 仍可能因目标文件存在打开句柄而失败。改为先 `File.Delete`（利用 `FileShare.Delete` 移除目录项）再 `File.Move`，配合重试：

```csharp
// Before
File.Move(tempPath, path, overwrite: true);

// After — MoveWithRetryAsync 内部
File.Delete(targetPath);       // 读端有 FileShare.Delete 时成功移除目录项
File.Move(tempPath, targetPath); // 目标路径已释放，无需 overwrite
// 重试 3 次，间隔 50ms，捕获 IOException / UnauthorizedAccessException
```

说明：`File.Delete` + `File.Move` 不是严格的原子替换，中间存在目标路径短暂不存在的窗口。这里接受该取舍，是因为 `runtime_state.json` / `health_state.json` 属于可重写状态快照；App 端读不到文件时已有 fallback / stale 判定，Agent 端写失败也会由 `PersistAsync` 兜底并在下一轮 tick 重试。

#### 调用端：PersistAsync 不传播异常（放行取消）

```csharp
// Before
await _runtimeStateStore.WriteAsync(_paths.RuntimeStatePath, runtimeState, cancellationToken);

// After
try
{
    await _runtimeStateStore.WriteAsync(_paths.RuntimeStatePath, runtimeState, cancellationToken);
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    _logger.LogWarning(ex, "写入 runtime_state.json 失败：{Path}", _paths.RuntimeStatePath);
}
```

#### 兜底：Worker 不因一次 tick 异常退出（放行取消）

```csharp
// Before
var keepRunning = await _stateMachine.TickAsync(stoppingToken);

// After
try
{
    var keepRunning = await _stateMachine.TickAsync(stoppingToken);
    ...
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    _logger.LogError(ex, "TickAsync 未处理异常，Agent 跳过本次 tick 继续运行");
}
```

## 构建验证

```
dotnet build  → 0 Warnings, 0 Errors
dotnet test   → 468 passed, 0 failed, 0 skipped（含 2 个新增并发 I/O 测试）
```

## 影响范围

- **读端**：App 状态轮询、Diagnostics 页面数据加载均通过 `ReadAsync`，FileShare 变更是纯放宽权限，不影响功能
- **写端**：正常成功路径的业务效果不变，仍是以新快照替换旧快照；但 `File.Delete` + `File.Move` 并非严格原子替换，若极短窗口内 App 读取目标文件，可能得到“文件不存在/本次读失败”的短暂结果，随后下一轮轮询恢复
- **PersistAsync**：写失败时 Agent 继续运行，但本轮心跳未落盘，下一轮成功落盘后恢复。极端情况下若持久化长期失败，App 端会因心跳过期判定 Stale（属于正确行为——Agent 确实无法正常写状态文件）
- **Worker**：只捕获异常不退出，正常 Stop 路径（`TickAsync` 返回 `false`）不受影响；取消异常正常传播，不影响 shutdown 流程

---

## 审核修复（R3）

> 审核日期：2026-07-11

### 发现

1. **P1**：`PersistAsync` 和 `Worker` 中 `catch (Exception)` 会吞掉 `OperationCanceledException`，正常 shutdown/stop 时可能产生误报日志。
2. **P2**：缺少针对并发读写的测试覆盖；测试运行后还暴露了 `File.Move(overwrite: true)` 与 `FileShare.Delete` 的兼容问题。
3. **P3**：完成说明中将根因 100% 归因于 App 读取句柄，措辞过于确定。

### 修复

| # | 修复 | 影响文件 |
|---|------|----------|
| 1 | 所有 catch (Exception) 加 `when (ex is not OperationCanceledException)` 过滤，取消异常正常传播 | `AgentStateMachine.cs`, `Worker.cs` |
| 2 | 新增 `RuntimeStateStore_WriteCanReplaceFile_WhileReaderHoldsOpenHandle` 和 `AgentHealthStateStore_WriteCanReplaceFile_WhileReaderHoldsOpenHandle` 测试；`MoveWithRetryAsync` 从 `File.Move(overwrite:true)` 改为 `File.Delete` + `File.Move` 以兼容 `FileShare.Delete` | `RuntimeStateStore.cs`, `AgentHealthStateStore.cs`, `DataFlowTests.cs` |
| 3 | 文档根因措辞收敛为「高度疑似，由事件链路支持」，崩溃链路标题改为「推断的崩溃链路」 | `PersistAsync-FileMove崩溃修复完成说明-2026-07-11.md` |

### 构建验证

```
dotnet build  → 0 Warnings, 0 Errors
dotnet test   → 468 passed, 0 failed, 0 skipped
```
