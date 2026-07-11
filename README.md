# WUJI（吾迹）

> Windows 桌面端自动活动追踪 —— 你的一天去了哪里，让数据说话。

WUJI 在后台静默记录前台窗口活动（正在用什么软件、浏览器里看什么网页），自动聚合成工作时间段（Session），并通过 Dashboard、趋势分析、专注洞察等页面帮你回顾与优化每一天的时间分配。

**本地优先**：所有数据只保存在你的电脑里，无需账号，无需联网。Agent 与 UI 分离运行，即使主界面关闭，后台也会持续记录。

[![.NET 8](https://img.shields.io/badge/.NET-8-512BD4?logo=dotnet)](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%2F11-0078D4?logo=windows)](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)
[![Tests](https://img.shields.io/badge/tests-xUnit-green)](./tests/QuantifiedSelf.Windows.Tests/)

---

## 系统需求

- **操作系统**：Windows 10 19041 (20H1) 或更高 / Windows 11
- **架构**：x64
- **运行时**：无需预装 .NET —— 发布包为 [self-contained](https://learn.microsoft.com/en-us/dotnet/core/deploying/#publish-self-contained) 部署，所有依赖内嵌

## 功能概览

> 浏览器窗口标题采集支持 Chrome、Edge、Firefox 等主流 Chromium / Gecko 浏览器（通过窗口标题读取，不读取 URL）。

| 模块 | 做什么 |
|------|--------|
| **自动采样** | 后台 Agent 每 3 秒采集当前前台窗口和浏览器标签页标题，支持隐私过滤与标题脱敏 |
| **会话聚合** | 把连续的同一窗口活动自动合并成工作 Session，记录起止时间及各状态时长 |
| **Dashboard** | 今日总览卡片、活动热力图、24 小时时间线、7 日趋势、Top Apps / Windows，以及基于统计规则的每日洞察建议 |
| **Apps / Sessions / Samples** | 按应用排行、按时间段浏览 Session、原始采样记录逐条可查 |
| **Insights** | 专注度分析、任务切换频率、趋势对比、中断检测 |
| **Diagnostics** | Agent 进程状态、Tick 耗时诊断、IPC 通道状态、最新事件与告警 |
| **Settings** | 采样间隔、隐私规则、数据保留天数、页面刷新频率等全部可调节 |
| **系统托盘** | 最小化到托盘、后台常驻、开机自启（可选） |

## 快速开始

```bash
# 1. 克隆仓库
git clone <repo-url>
cd wuji/Win

# 2. 还原依赖
dotnet restore QuantifiedSelf.Windows.sln

# 3. 构建
dotnet build QuantifiedSelf.Windows.sln

# 4. 运行测试（覆盖状态机、数据流、并发 I/O 等核心路径）
dotnet test

# 5. 运行 App
dotnet run --project src/QuantifiedSelf.Windows.App/
```

> 运行 App 后，点击 **Start Agent** 开始采样（如需自动启动，可在 Settings 中启用 `AutoStartAgentWhenAppStarts`）。Agent 运行时可在系统托盘找到图标，右键管理 Agent 状态。

## 发布

```powershell
# self-contained win-x64 文件夹发布（含 Agent 嵌入与产物校验）
.\publish\scripts\publish.ps1
```

发布产物输出到 `publish/release/App/`，其中 `publish/release/App/Agent/` 子目录包含独立的 Agent 可执行文件与依赖，可直接复制到目标机器运行。

## 架构

```
┌──────────────┐  Named Pipe (IPC)   ┌──────────────┐
│   WPF App    │ ◄─────────────────► │    Agent     │
│  (UI + 托盘)  │     fallback:        │  (后台采样)   │
│              │     文件系统读写       │              │
└──────┬───────┘                     └──────┬───────┘
       │                                    │
       │         SQLite (WAL)               │
       └──────────────┬────────────────────┘
                      │
              ┌───────▼───────────────┐
              │     本地数据库         │
              │  foreground_samples   │
              │  app_sessions         │
              │  agent_events         │
              └───────────────────────┘
```

详细架构说明见 **[ARCHITECTURE.md](./ARCHITECTURE.md)**。

## 项目结构

```
Win/
├── src/
│   ├── QuantifiedSelf.Windows.Core/            # 领域模型、配置、枚举（net8.0，跨平台）
│   ├── QuantifiedSelf.Windows.Infrastructure/  # 数据层、Win32、IPC 传输、状态持久化
│   ├── QuantifiedSelf.Windows.Agent/           # Agent 后台进程（Worker SDK，BackgroundService）
│   └── QuantifiedSelf.Windows.App/             # WPF 桌面应用（UI + 系统托盘）
├── tests/
│   └── QuantifiedSelf.Windows.Tests/           # xUnit 测试
├── publish/
│   └── scripts/publish.ps1                     # 发布脚本
├── docs/                                       # 设计文档与开发历史
├── scripts/                                    # Python 辅助分析脚本
└── ARCHITECTURE.md                             # 系统架构详细说明
```

## 技术栈

| 层 | 技术 |
|----|------|
| 运行时 | .NET 8 |
| UI | WPF + LiveChartsCore |
| MVVM | CommunityToolkit.Mvvm |
| 数据库 | SQLite (Microsoft.Data.Sqlite, WAL 模式) |
| IPC | Windows Named Pipe（协议 v1） |
| 宿主 | .NET Generic Host / BackgroundService |
| 测试 | xUnit |
| 发布 | dotnet publish self-contained win-x64 |

## 配置

Agent 行为通过 `windows-agent.json` 配置（在 App 的 Settings 页面可视化编辑）。

配置文件位于运行时的数据目录（可在 Settings 页面查看具体路径）：

| 参数 | 默认值 | 说明 |
|------|--------|------|
| `SamplingIntervalSeconds` | 3 | 前台窗口采样间隔 |
| `IdleThresholdSeconds` | 60 | 判定为 Idle 的无操作时长 |
| `HeartbeatIntervalSeconds` | 3 | 心跳文件写入间隔 |
| `StaleThresholdSeconds` | 15 | 判定 Agent 不健康的心跳超时 |
| `RetentionDays` | 30 | 数据保留天数 |
| `ExcludedProcesses` | KeePass, 1Password, Bitwarden, explorer | 不记录的进程 |
| `MaskWindowTitles` | true | 是否脱敏窗口标题 |

完整配置项见 [ARCHITECTURE.md#配置](./ARCHITECTURE.md#配置windowsagentoptions)。

## 卸载与数据清理

- 在 Settings 页面点击「清空所有历史数据」可删除所有采样记录与聚合数据。
- **数据目录**：可在 Settings 页面查看实际路径。默认为 `%LOCALAPPDATA%\WUJI\WindowsAgent`（含数据库 `data\`、配置文件 `config\`、运行时状态 `runtime\`）。删除此目录即可彻底清除所有本地数据。

## 文档索引

- **[ARCHITECTURE.md](./ARCHITECTURE.md)** — 系统架构完整说明：进程模型、IPC 通信、数据流、状态管理、数据库表结构、多进程文件读写保护、技术决策
- **[MANUAL.md](./MANUAL.md)** — App 每个页面的文字版详细介绍（用户手册）
- **[docs/design/](./docs/design/)** — 系统设计与方案文档（重构方案、调研报告）
- **[docs/specs/](./docs/specs/)** — 功能需求规格
- **[docs/fixes/](./docs/fixes/)** — Bug 修复记录与根因分析
- **[docs/devlog/](./docs/devlog/)** — 各阶段 MVP 计划、实施说明与验收清单
- **[docs/](./docs/)** — 完整文档目录索引

## 反馈

遇到问题或有功能建议，欢迎通过仓库 Issue 或开发反馈渠道提交。

## License

本软件及相关源代码保留所有权利（All Rights Reserved）。
未经授权，不得转载、修改、分发或用于商业用途。
仓库根目录 `LICENSE` 文件包含完整条款。
