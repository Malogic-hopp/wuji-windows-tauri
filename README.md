# WUJI（吾迹）Windows Desktop

WUJI 是一个 local-first 的 Windows 活动记录桌面应用。当前仓库只维护 Rebuild 链路：React 19 UI、Tauri 2 Desktop Host、Rust Agent 与 SQLite v0.1 数据库。

> 当前版本是 dev-only 的 Rebuild v0.1 工程里程碑，不等同于生产发布。验收状态以 [migration-status.md](docs/dev/migration-status.md) 为准。

## 目录

```text
apps/
  desktop/          React + TypeScript UI 与 Tauri Rust Host
  agent/            独立 Rust Agent 进程
crates/
  wuji-core/        领域、Settings、DTO 与错误合同
  wuji-storage/     SQLite Schema、Writer 与只读查询
  wuji-windows/     Win32、Named Pipe 与进程封装
scripts/            打包、soak 和验收脚本
docs/dev/           当前实施基线、ADR、审核与验收状态
```

架构边界见 [ARCHITECTURE.md](ARCHITECTURE.md)，实施合同见 [09-Tauri-Rust-Rebuild-v0.1 实施基线](docs/dev/09-Tauri-Rust-Rebuild-v0.1实施基线.md)。

## 开发环境

- Windows 10/11；
- Rust 1.97（由 `rust-toolchain.toml` 固定）；
- Node.js 24.14.0；
- pnpm 11.9.0；
- Python 3（打包与 soak 脚本）。

在仓库根目录执行：

```powershell
cargo build --workspace
cargo test --workspace

Push-Location .\apps\desktop
pnpm install --frozen-lockfile
pnpm typecheck
pnpm lint
pnpm test
Pop-Location
```

启动方式：

```powershell
# 仅启动 Tauri dev；不会主动构建或启动 Agent
.\rebuild-tauri-dev.ps1

# 单独构建并启动 debug Agent
.\rebuild-agent.ps1

# 构建并验收 NSIS dev 安装包
.\rebuild-package.ps1
```

## 进程语义

- “暂停记录”只暂停 Capture，Agent 进程继续在线；
- “停止 Agent”先提交 CaptureStop 边界，再请求 Agent graceful shutdown；
- Desktop 退出不会隐式终止 Agent；
- `capture_start` 会在 Tauri Host 内确保固定位置的 Agent 在线，再开始采集。

## 数据安全

Rebuild 使用独立的进程名、Pipe、mutex、数据目录和数据库。开发、测试、打包与 soak 不得修改旧 WUJI/WUJI-Dev 数据库；不得提交本机数据库、日志、安装产物、`target/`、`dist/` 或 `node_modules/`。

旧 WPF/C#/Bridge 源码的退役决策、冻结提交与恢复方式见 [ADR-003](docs/dev/ADR-003-Rebuild-only仓库转换与旧系统源码退役.md)。
