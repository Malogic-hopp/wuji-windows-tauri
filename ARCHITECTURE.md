# WUJI Rebuild 架构概览

当前仓库采用双进程、local-first 架构：

```text
React UI
  │ 固定 Tauri command allowlist
  ▼
Tauri Rust Host ── read-only ──► SQLite v0.1
  │ Named Pipe IPC
  ▼
Rust Agent ── single writer ───► SQLite v0.1
  │
  └─ Capture → Privacy → Processor → Writer
```

## 所有权

- React：页面状态、交互和展示；不连接 Pipe、不查询 SQLite、不计算领域聚合。
- Tauri Host：只读 Query、Settings 文件写入、Agent 进程管理和 IPC client。
- Rust Agent：Capture、隐私过滤、Activity/Work 状态机、唯一 SQLite Writer、运行控制和诊断。
- SQLite：保存 v0.1 事实、Activity/Work、Settings revision、小时/日读模型与诊断；旧系统数据库不参与该链路。

## 控制边界

Capture、Settings 和 Lock/Sleep 生命周期通过唯一 `CaptureCoordinator` 串行化。需要改变数据生效边界的操作遵循：

```text
transition lock → freeze → Barrier injected ack
→ WriterControl → Writer ack → publish/watch → restore effective state
```

Writer 提交结果未知、Pipeline 任务死亡或生命周期监视链故障时必须 fail-closed，不能自动恢复采集。

## 运行与数据隔离

Rebuild 使用固定的独立进程名、Pipe、mutex、channel 和 `%LOCALAPPDATA%\WUJI-Rebuild-V01` 数据根。开发测试使用唯一 test channel。旧 WUJI/WUJI-Dev 数据库只允许在打包验收中读取 checksum。

## 权威文档

- v0.1 实施合同：[09 实施基线](docs/dev/09-Tauri-Rust-Rebuild-v0.1实施基线.md)
- 长期架构：[ADR-002](docs/dev/ADR-002-React-Tauri-Rust目标架构.md)
- Rebuild-only 仓库决策：[ADR-003](docs/dev/ADR-003-Rebuild-only仓库转换与旧系统源码退役.md)
- 当前实现与验收状态：[migration-status.md](docs/dev/migration-status.md)
