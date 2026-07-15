# docs/ — WUJI 文档目录

## 快速导航

| 目录 | 说明 | 面向读者 |
|------|------|----------|
| [design/](#design) | 系统设计与方案文档 | 开发者 |
| [specs/](#specs) | 功能需求规格 | 产品 / 开发 |
| [fixes/](#fixes) | Bug 修复记录 | 开发者 |
| [qa/](#qa) | 测试与验收清单 | QA / 开发 |
| [prompts/](#prompts) | AI 提示词归档 | 开发者 |
| [project/](#project) | 项目管理与进度分析 | PM / 全员 |
| [devlog/](#devlog) | 阶段计划、实施说明与验收记录（开发历史） | 开发者 |
| [MANUAL.md](../MANUAL.md) | App 每个页面的文字版详细介绍 | 用户 / 全员 |

---

## design

系统架构与方案设计文档。

| 文件 | 简介 |
|------|------|
| `QuantifiedSelf Windows 端完整重构方案.md` | 项目整体重构设计方案（完整版，50KB） |
| `Application-Client-SDK抽取方案-2026-07-16.md` | 抽取框架无关 Application 与 Windows Client SDK，使 WPF App 收敛为纯 UI，并为 WinUI/Avalonia/Tauri/Electron 提供统一接入边界 |
| `Dashboard补充图表开发规格-2026-07-07.md` | Dashboard 图表的开发规格说明 |
| `专注中断洞察独立页面方案-2026-07-07.md` | Insights 专注中断分析功能设计方案 |
| `调研-WorkReview与Hindsight借鉴-2026-07-06.md` | 竞品调研：WorkReview 与 Hindsight 的借鉴分析 |

---

## specs

产品需求与功能规格。

| 文件 | 简介 |
|------|------|
| `WUJI产品命名与前台显示需求-2026-06-19.md` | 产品命名从 QuantifiedSelf → WUJI（吾迹），以及前台窗口显示名的映射规则 |
| `Agent终端输出中文直白化需求-2026-06-19.md` | Agent 终端输出全部改用中文直白可读日志 |

---

## fixes

线上问题修复说明，包含根因分析、修复方案、验证结果。按时间倒序排列。

| 文件 | 日期 | 问题 |
|------|------|------|
| `PersistAsync-FileMove崩溃修复完成说明-2026-07-11.md` | 2026-07-11 | PersistAsync 写状态文件时 FileMove 崩溃的根因与修复 |
| `Stale修复完成说明-2026-07-10.md` | 2026-07-10 | Agent Stale 判定修复的完成说明 |
| `Stale根因修复与可观测性增强方案-2026-07-10.md` | 2026-07-10 | Stale 根因分析、修复方案与 Tick 诊断项设计 |
| `Agent Start按钮无响应问题记录-2026-07-02.md` | 2026-07-02 | Start Agent 按钮点击无响应问题的排查记录 |

---

## qa

测试与验收相关文档。

| 文件 | 简介 |
|------|------|
| `真实采样稳定性手动验收清单-2026-06-19.md` | 真实环境下采样功能的手动验收 Checklist |

---

## prompts

AI 协作时的关键提示词存档，用于重现同类任务的初始上下文。

| 文件 | 简介 |
|------|------|
| `专注中断洞察独立页面实施提示词-2026-07-07.md` | Insights 页面实施时使用的 AI 提示词 |

---

## project

项目管理与进度跟踪。

| 文件 | 简介 |
|------|------|
| `项目进度分析-基于完整重构方案-2026-07-06.md` | 对照重构方案的项目总体进度分析报告 |

---

## devlog

开发过程记录：按时间排列的 MVP 阶段计划、代码审查报告、完成说明、验收清单。

每个阶段通常包含三个文档：
- **主文档**（`.md`）：分阶段的实施方案和需求描述
- **审查报告**（`review.md`）：对该方案的 Code Review 意见
- **子目录**（`阶段名/`）：逐阶段的实施完成说明、验收清单、下一阶段计划建议

### 阶段列表（按时序）

| 阶段 | 日期 | 主题 |
|------|------|------|
| 初始计划 | 2026-06-17 | [下一步计划.md](./devlog/下一步计划.md) — 项目第一个里程碑计划 |
| 阶段 1 | 2026-06-18 | 真实采样闭环（TickAsync 主循环、Win32 采集、SQLite 写入、Session 聚合） |
| 阶段 2 | 2026-06-19 | AgentEvents 与 Diagnostics MVP（事件基础设施、事件写链路、Diagnostics 增强） |
| 阶段 3 | 2026-06-22 | Samples / Sessions / Apps 数据浏览 MVP（查询服务、三个页面的表格视图） |
| 阶段 4 | 2026-06-22 | Settings 与数据管理 MVP（Agent Options 编辑、AppSettings、ReloadConfig） |
| 阶段 5 | 2026-06-24 | PruneData / ClearHistory 数据清理 MVP（数据保留、历史清空、二次确认 UI） |
| 阶段 6 | 2026-07-01 | Named Pipe 控制通道 MVP（IPC 协议、命令服务端/客户端、fallback 策略） |
| 阶段 7 | 2026-07-01 | Agent 状态流订阅 RefreshService 优化 MVP（状态轮询、事件驱动刷新） |
| 阶段 8 | 2026-07-03 | 托盘图标与后台运行 MVP（NotifyIcon、CloseToTray / MinimizeToTray） |
| 阶段 9 | 2026-07-04 | 开机自启 MVP（注册表启动注册、自动启动参数） |
| 阶段 10 | 2026-07-07 | 安装包与发布体验 MVP（self-contained 发布、publish.ps1、可执行文件校验） |
| 阶段 11 | 2026-07-07 | 个人洞察与统计分析 MVP（Insights 页面、专注中断分析、工作块检测） |
| 其他 | 2026-07-07 | [LiveCharts2图表替换变更记录](./devlog/LiveCharts2图表替换变更记录-2026-07-07.md) — 图表库迁移记录 |
| 其他 | 2026-07-07 | [专注中断洞察独立页面实施完成说明](./devlog/专注中断洞察独立页面实施完成说明-2026-07-07.md) |
