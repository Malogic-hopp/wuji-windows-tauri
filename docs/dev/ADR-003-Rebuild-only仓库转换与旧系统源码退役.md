# ADR-003：Rebuild-only 仓库转换与旧系统源码退役

状态：Accepted

决策日期：2026-07-29

记录日期：2026-07-30

决策人：产品负责人

## 1. 背景

Rebuild v0.1 已形成独立的 React → Tauri Rust Host → Rust Agent → SQLite 链路。继续让根目录同时承载旧 WPF/C#/Bridge 系统与 `rebuild/` workspace，会造成构建入口、Agent 规则、文档权威和打包路径长期双轨。

09 基线原本要求旧源码保留到 G-RETIRE，以确保 Rebuild 验证期间有独立回滚入口。到 2026-07-29，产品负责人决定把当前仓库转换为 Rebuild-only，并接受“仓库源码先退役、生产安装与旧数据仍不退役”的例外。

## 2. 决策

1. 将原 `rebuild/` 中的 `Cargo.toml`、`apps/`、`crates/` 和 `scripts/` 提升到仓库根目录。
2. 当前仓库只维护 Rebuild 链路；删除工作树中的旧 WPF App、C# Agent、Bridge、旧合同、旧测试、旧工具，以及旧系统专用的历史分析/清理脚本。
3. 过渡期 C# Bridge + React 文档保存在 `docs/dev/archive/`；其他旧文档通过冻结 Git 提交查阅。
4. 该决定只改变源码维护边界，不代表 G-PROD 或 G-RETIRE 已通过，也不授权修改、迁移或删除用户机器上的旧数据库和旧安装。
5. Rebuild 继续使用独立命名空间；旧 production/dev channel 不由 Rebuild 接管。

本 ADR 是 09 §3.2、§11、§13、§14 中“旧源码必须继续位于同一工作树”约束的正式、限范围例外。production cutover、数据导入、旧安装停产和旧数据库退役仍受 07/08 的长期 Gate 约束。

## 3. 冻结来源与恢复

旧系统不得只依赖本机 reflog 或不可达对象。以下两个远程可达提交作为恢复入口：

| 来源 | 冻结提交 | 用途 |
|---|---|---|
| `origin`：`Malogic-hopp/wuji-windows-tauri` | `a51f04860eb161b7b1c376156ef5ad4192aa77a5` | 本仓库删除前的完整旧树与 Rebuild 子目录快照 |
| `source`：`Malogic-hopp/wuji-windows` | `3a7010e882046f1b2c4bf7129f829888a681d8e7` | 独立旧 WPF/C# 仓库的冻结参考快照 |

需要恢复或审查旧系统时，应克隆到独立目录并切换到上述提交；不得在当前脏工作树中使用 destructive checkout 覆盖 Rebuild 修改。例如：

```powershell
git clone https://github.com/Malogic-hopp/wuji-windows.git <legacy-review-dir>
git -C <legacy-review-dir> switch --detach 3a7010e882046f1b2c4bf7129f829888a681d8e7
```

若远程仓库所有权、可见性或历史保存策略变化，必须先建立新的不可变归档或 tag，再移除上述恢复入口。

## 4. 数据与运行时保护

- `%LOCALAPPDATA%\WUJI\WindowsAgent` 和 `%LOCALAPPDATA%\WUJI-Dev\WindowsAgent` 及其数据库不得由 Rebuild 写入或删除。
- 打包/验收脚本只允许读取旧数据库 checksum，并在前后验证不变。
- 旧系统恢复运行时必须使用其原命名空间；不得让旧 Agent 打开 Rebuild 数据库，也不得让 Rebuild Agent 打开旧数据库。
- 正式 importer、production migration 和旧数据退役必须另立版本与 Gate。

## 5. 影响

正面影响：仓库入口单一，Rust workspace、Tauri、脚本和文档路径更直接；不再混用旧 C# 规则。

代价与风险：旧系统不再能从当前工作树直接启动；旧 `.NET` 回归测试不再属于本仓库自动门禁；回滚依赖冻结远程提交。因此 release 说明不得把“源码已移除”表述为“生产退役已完成”。

## 6. 验收要求

- 根目录提供 Rebuild 专用 `README.md`、`AGENTS.md` 和 `ARCHITECTURE.md`；
- 09 基线和 migration-status 引用本 ADR并统一目录/退役语义；
- Rust、React 自动门禁通过；
- 至少一次 NSIS dev package 构建与安装烟测通过，旧数据库 checksum 不变；
- 暂存后检查 rename，确认原 `rebuild/` 文件无丢失且删除范围与本 ADR 一致。
