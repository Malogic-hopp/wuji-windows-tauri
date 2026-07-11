# 开机自启 MVP 阶段拆分

本文档夹由主计划拆分而来：

```text
docs/下一步计划-2026-07-04-开机自启MVP.md
```

## 阶段列表

```text
01-阶段11.1-AppSettings与启动参数基础.md
02-阶段11.2-StartupRegistrationService与HKCURunKey.md
03-阶段11.3-Settings自启开关与保存同步.md
04-阶段11.4-登录自启启动到托盘.md
05-阶段11.5-Diagnostics自启状态与错误展示.md
06-阶段11.6-手动验收长跑验证与收口.md
```

## 阶段 11 总目标

```text
让 WUJI 支持当前用户登录 Windows 后自动启动到托盘，并可继续复用已有 AutoStartAgentWhenAppStarts 决定是否随 App 启动 Agent。
```

## 硬约束

```text
1. 阶段 11 使用当前用户级自启，MVP 优先 HKCU Run Key。
2. 不做 Windows Service、HKLM、管理员权限自启、计划任务高级配置或安装包。
3. StartAppOnWindowsLogin 与 AutoStartAgentWhenAppStarts 必须严格区分。
4. StartupCommandBuilder 必须集中封装 App exe 路径解析和 Run Key command 构建。
5. 检测到 dotnet.exe、dll、空路径或非 WUJI App exe 时不得注册 Run Key。
6. Run Key command 比较必须做规范化解析，不只做字符串完全相等。
7. Settings IsDirty=true 时不得覆盖 StartAppOnWindowsLogin 草稿，也不得触发注册表写入或删除。
8. 自启隐藏到托盘由启动策略决定，不通过 Window.Closing / CloseToTray 模拟。
9. AutoStartHidden 模式下不得弹阻塞式 MessageBox。
10. 阶段 11 不改变阶段 10 CloseToTray / MinimizeToTray / Exit App / Tray 命令语义。
11. 阶段 11 不改变阶段 8 IPC 协议、AgentStateMachine、PruneData / ClearHistory 规则。
12. 不强制实现 single-instance，但 11.6 必须手动验证重复启动行为。
```

## 不做范围

```text
安装包
Windows Service
管理员权限自启
计划任务高级配置
Startup 文件夹快捷方式主实现
多用户服务化
自动更新
系统级后台服务
真正 Named Pipe 状态流 / subscribe 协议
托盘复杂通知中心
托盘 Exit App + Stop Agent 组合操作
正式 .ico 设计
```

## 注意事项

### build/test 临时输出路径

`dotnet build` / `dotnet test` 使用 `-p:BaseOutputPath=...` 指定临时输出目录时，**路径分隔符必须用正斜杠 `/`**，不能用反斜杠 `\`。

在 bash shell 下，反斜杠会被转义吃掉，导致目录名合并：

```powershell
# 错误 —— 反斜杠在 bash 下被吃掉，目录变成 .codexbuild-output 2Debug
dotnet build QuantifiedSelf.Windows.sln --no-restore -p:BaseOutputPath=.codex\build-output\

# 正确 —— 正斜杠在 bash 和 PowerShell 下都正常工作
dotnet build QuantifiedSelf.Windows.sln --no-restore -p:BaseOutputPath=.codex/build-output/
dotnet test QuantifiedSelf.Windows.sln --no-restore -p:BaseOutputPath=.codex/test-output/
```

残留目录清理（如果已经生成了错误命名的目录）：

```bash
find src tests -maxdepth 3 -type d -name ".codex*" -exec rm -rf {} +
```
