# 下一步计划：安装包与发布体验 MVP（2026-07-07）

本文档作为 `下一步计划-2026-07-04-开机自启MVP.md` 阶段 11「开机自启 MVP」完成后的下一阶段正式计划。

上一阶段已经完成：

```text
开机自启 MVP（阶段 11.1–11.6）
```

当前项目已经从：

```text
可随用户登录自动启动到托盘的本地采集控制台
```

推进到需要回答下一个产品化问题：

```text
普通用户如何获得、安装和升级 WUJI？
```

下一阶段目标命名为：

```text
安装包与发布体验 MVP
```

一句话目标：

```text
让普通 Windows 用户可以通过安装包稳定安装 WUJI，安装后托盘常驻、开机自启可靠工作，卸载时可选择清理用户数据，为后续功能迭代建立可发布的基线。
```

---

## 前置条件

进入本阶段前，必须先完成并验收通过：

```text
阶段 11.6：手动验收、长跑验证与收口
```

具体要求：

```text
1. 阶段 11.1–11.6 的 build/test 全部通过。
2. 阶段 11 手动验收 21 项已人工勾选确认。
3. 阶段 11 长跑验证 6 项已人工勾选确认。
4. 《阶段11-完成说明-YYYY-MM-DD.md》已落盘。
5. 阶段 10/9/8/7 测试无回归。
```

如果阶段 11.6 重复启动行为验收出现异常（双托盘 / 双窗口 / 状态漂移），建议将「轻量 single-instance」提升为 12.1 之前的前置小阶段，而不是直接进入安装包。本计划默认该风险已通过或记录为低优先级后续项。

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
SettingsView App / Agent 配置展示、编辑、校验、保存、ReloadConfig
PruneData / ClearHistory 数据清理
Maintenance 状态
Named Pipe IPC 主控制通道
agent_control.json file fallback
Diagnostics IPC 状态展示
RefreshService 状态刷新协调
2 秒 IPC GetStatus 轻量状态轮询
status refresh / page refresh 独立 gate
MainWindow / Settings 共用 AgentStatusSnapshot 更新按钮状态
Diagnostics RefreshHealth 展示
系统托盘图标
CloseToTray / MinimizeToTray
托盘 Show / Exit / Start / Pause / Resume / Stop
托盘状态 tooltip 与菜单可用性
Stale / NotRunning / IPC fallback / Maintenance 托盘恢复语义
HKCU Run Key 开机自启注册 / 取消注册
StartupLaunchOptions 启动参数解析
自启启动默认隐藏到托盘
Diagnostics 自启状态展示
```

阶段 11 已完成验收（实际）：

```text
dotnet build
    通过，0 warnings / 0 errors

dotnet test
    通过，356/356

手动验收
    21/21 通过

长跑验证
    6/6 通过
```

当前 WUJI 已经是一个可日常后台运行的本地采集控制台，但仍存在发布层面的缺口：

```text
1. 普通用户无法从源码 / dotnet run 获得可执行程序。
2. 开发路径变化会导致已注册 HKCU Run Key 出现 Mismatch。
3. 没有统一目录结构，安装后 App、Agent、数据、日志位置不明确。
4. 卸载时可能残留 HKCU Run Key、数据文件或配置。
5. 没有版本号、发布说明和升级策略。
6. 没有正式应用图标和品牌标识。
```

---

## Review 吸收结论

阶段 11 的下一阶段建议明确指向：

```text
阶段 12：安装包与发布体验 MVP
```

阶段 11 已经解决：

```text
登录后自动启动 WPF App
自启参数解析
HKCU Run Key 注册 / 取消注册 / Mismatch 检测
Settings 自启开关与保存同步
自启启动隐藏到托盘
Diagnostics 自启状态展示
```

所以本阶段不需要重新设计自启逻辑，而是把自启和托盘体验封装成可发布、可安装、可卸载的产品形态。

本阶段吸收以下边界：

```text
1. 安装包优先面向当前用户，不默认要求管理员权限。
2. HKCU Run Key 自启可在安装时可选启用，也可安装后由用户在 Settings 中开启。
3. 不引入 Windows Service、计划任务、系统级服务。
4. 不引入自动更新机制，但预留版本号和升级检测接口。
5. 不迁移数据存储位置到 Program Files；数据继续放在 %LocalAppData%\WUJI。
6. 不改变阶段 10 CloseToTray / MinimizeToTray / Exit App 语义。
7. 不改变阶段 11 StartAppOnWindowsLogin / AutoStartAgentWhenAppStarts 语义。
8. 不改变阶段 8 IPC 协议和阶段 9 RefreshService 语义。
9. 不改变阶段 7 PruneData / ClearHistory 规则。
10. 安装包不得破坏 Agent 是唯一 SQLite 写入者的原则。
```

---

## 下一阶段目标

下一阶段目标命名为：

```text
安装包与发布体验 MVP
```

一句话目标：

```text
让 WUJI 可以通过安装包在 Windows 上稳定安装、自启、常驻托盘，并能干净卸载，为普通用户提供可发布的桌面体验。
```

这一阶段重点补齐：

```text
发布配置
    self-contained win-x64 发布
    输出目录规范化
    版本号统一
    单文件输出为可选目标

安装包
    选型与实现
    安装 App + Agent 到固定目录
    开始菜单快捷方式
    可选：安装时启用开机自启
    可选：桌面快捷方式

卸载体验
    清理 HKCU Run Key
    可选：保留或删除 %LocalAppData%\WUJI 数据
    清理开始菜单 / 桌面快捷方式

品牌与资产
    正式应用图标
    窗口图标
    托盘图标统一

发布说明
    版本号
    最低系统要求
    已知问题
    升级建议

验收
    安装后 App 可启动
    安装后 Agent 可启动
    安装后托盘常驻
    安装后开机自启可正常工作
    卸载后无残留注册项
    升级后配置和数据可保留
```

阶段完成后应满足：

```text
1. 可通过安装包在干净 Windows 环境安装 WUJI。
2. 安装目录固定且可预测（例如 %LocalAppData%\Programs\WUJI 或用户选择路径）。
3. 安装后 App.exe 路径稳定，开机自启注册不再因路径变化出现 Mismatch。
4. 安装包可选择是否在安装时启用 StartAppOnWindowsLogin。
5. 安装后 Agent.exe 与 App.exe 相对位置固定，AgentProcessService 可正确找到 Agent。
6. 卸载时清理 HKCU Run Key，避免残留错误自启项。
7. 卸载时保留 %LocalAppData%\WUJI 数据作为默认行为，或提供可选删除。
8. 应用图标、窗口图标、托盘图标统一为正式 .ico 资源。
9. 发布说明包含版本号、最低系统要求、.NET 运行时依赖说明、已知问题。
10. 阶段 11 开机自启、阶段 10 托盘、阶段 9 RefreshService、阶段 8 IPC、阶段 7 数据清理测试不回归。
```

---

## 为什么现在做这个

阶段 10 和阶段 11 已经把 WUJI 打造成一个可后台常驻、可开机自启的桌面应用。此时产品体验的下一个自然问题是：

```text
我能不能下载一个安装包，像普通 Windows 软件一样安装？
我卸载后会不会留下垃圾注册表项？
我升级后原来的数据还在吗？
```

如果跳过安装包，后续即使有趋势图、导出、分类，普通用户也无法稳定获得和使用这些功能。安装包是以下功能的前置：

```text
1. 稳定可执行路径（解决自启 Mismatch 的根因）。
2. 品牌图标和快捷方式。
3. 版本号和发布说明。
4. 干净的卸载和升级体验。
5. 后续自动更新的基础。
```

安装包完成后，WUJI 会从：

```text
可随用户登录自动常驻的本地采集控制台
```

进一步变成：

```text
可发布、可安装、可卸载的 Windows 桌面产品
```

---

## 本阶段不做

阶段 12 暂不做：

```text
自动更新
Windows Service
管理员权限自启
计划任务高级配置
多用户服务化
云端同步
崩溃日志上报服务器
商店上架（Microsoft Store）
复杂许可协议 / 激活机制
数据加密
本地数据备份/恢复（除安装包保留数据外）
趋势图
应用分类
数据导出 CSV
分页 SamplesView
全局快捷键
多窗口
正式网站 / 下载页
签名证书（如无法获得，使用未签名安装包并记录风险）
```

本阶段不删除：

```text
MainWindow 顶部控制按钮
Settings 数据管理入口
Diagnostics 页面
agent_control.json fallback
runtime_state.json / health_state.json
Named Pipe IPC
2 秒 status polling
CloseToTray / MinimizeToTray
托盘 Show / Exit / Start / Pause / Resume / Stop
HKCU Run Key 自启机制
```

安装包是新增发布入口，不是替代手动启动或开发调试流程。

---

## 架构原则

### 1. 安装包不改变双进程架构

WPF App 和 Agent 仍然是两个独立可执行文件。当前项目默认输出名为：

```text
QuantifiedSelf.Windows.App.exe
QuantifiedSelf.Windows.Agent.exe
```

品牌目标名可设为：

```text
WUJI.exe
WUJI.Agent.exe
```

阶段 12.1 需要先确认当前 publish 实际输出名，再决定是否通过 `AssemblyName` / publish 配置改名。安装包负责把它们放到同一安装目录，并创建开始菜单快捷方式。WPF App 仍通过相对路径或固定安装目录找到 Agent。

### 2. 数据与程序分离

程序文件放在安装目录：

```text
%LocalAppData%\Programs\WUJI
    或
C:\Users\<User>\AppData\Local\Programs\WUJI
```

用户数据继续放在：

```text
%LocalAppData%\WUJI\WindowsAgent
```

这样卸载时可以不删除用户数据，升级时数据自动保留。

### 3. 自启注册由 App 自己管理

安装包不直接写 HKCU Run Key（除非用户明确选择安装时启用自启）。推荐默认行为：

```text
安装时不写 Run Key。
用户首次启动 App 后，在 Settings 中开启 StartAppOnWindowsLogin。
App 启动后按阶段 11 的 StartupRegistrationService 注册 Run Key。
```

如果安装包提供"开机自启"选项，也应调用 App 提供的命令行接口或注册表写入逻辑，而不是安装包自己硬编码路径。

### 4. 卸载清理最小化

卸载时必须清理：

```text
HKCU\Software\Microsoft\Windows\CurrentVersion\Run 中的 WUJI value
开始菜单快捷方式
桌面快捷方式（如果创建过）
安装目录
```

默认保留：

```text
%LocalAppData%\WUJI\WindowsAgent 数据目录
```

可选项：

```text
是否同时删除用户数据目录
```

### 5. 图标资源统一

MVP 阶段至少提供一套内嵌 .ico 资源：

```text
App 窗口图标
任务栏图标
托盘图标
安装包图标
```

图标风格保持简洁，不阻塞安装包主流程。如果已有品牌图标资源，优先复用。

### 6. 版本号统一

建议统一版本号来源：

```text
Directory.Build.props 或共享 AssemblyInfo.cs
安装包版本
App 关于页版本
Agent 版本（与 App 保持一致或独立管理）
```

MVP 阶段至少保证 App 和安装包版本一致。

---

## 建议新增或调整

建议新增：

```text
build/publish.ps1 或 MSBuild 目标
    先 publish App，再 publish Agent，再复制到同一目录
    自包含发布
    单文件输出
    版本号注入
    确保 Agent 来自 Release / win-x64 / self-contained publish，不是 Debug fallback

installer/
    安装包项目（WiX / MSIX / Inno Setup 等）
    安装包脚本

src/QuantifiedSelf.Windows.App/Resources/wuji.ico
    应用图标

src/QuantifiedSelf.Windows.App/Views/AboutWindow.xaml（可选）
    版本号、项目链接、许可声明
```

建议调整：

```text
src/QuantifiedSelf.Windows.App/App.xaml.cs
    处理安装后首次启动路径
    确保 Agent exe 路径解析兼容安装目录

src/QuantifiedSelf.Windows.App/Services/AgentProcessService.cs
    Agent exe 路径解析兼容发布目录和开发目录
    查找顺序：AppContext.BaseDirectory > 环境变量 > 开发目录 fallback
    引用的 Agent exe 文件名与实际 publish 输出一致
    不新增 App -> Agent 项目引用（ReferenceOutputAssembly=false 保持不变）

src/QuantifiedSelf.Windows.App/Services/StartupCommandBuilder.cs
    安装后 App exe 路径固定，注册 Run Key 更稳定
    引用的 App exe 文件名与实际 publish 输出一致

Directory.Build.props
    统一版本号

global.json / nuget.config
    确认发布环境 SDK 版本
```

可选新增：

```text
scripts/build-installer.ps1
    一键构建 + 打包

README.md
    安装、卸载、升级说明
```

---

## 安装包选型建议

推荐按以下顺序评估：

```text
1. MSIX（推荐优先评估）
   优点：Windows 原生、支持干净卸载、自动更新预留、权限模型清晰。
   缺点：对 .NET 自包含包体积敏感、签名证书要求、沙箱行为可能限制。

2. WiX / MSI
   优点：成熟、企业场景友好、卸载干净。
   缺点：学习曲线、XML 配置复杂、对 .NET 自包含包体积敏感。

3. Inno Setup
   优点：轻量、脚本简单、社区成熟、不要求签名。
   缺点：非微软官方、自定义卸载逻辑需脚本实现。

4. 单文件压缩包 + 手动安装脚本
   优点：最简单、无安装器依赖。
   缺点：无开始菜单快捷方式、卸载靠文档说明、体验差。
```

MVP 阶段建议：

```text
1. 阶段 12.3 必须先做选型 spike，验证以下 4 点：
   - 是否支持当前用户安装（默认无需管理员权限）。
   - 是否能满足托盘常驻、HKCU Run Key 自启、自定义卸载清理。
   - 是否支持可选删除 %LocalAppData%\WUJI 数据目录。
   - 未签名情况下安装体验如何（SmartScreen、杀软误报、用户信任）。
2. MSIX / WiX 优先评估，但如果出现以下情况，允许直接 fallback 到 Inno Setup：
   - 当前用户安装受限或需要额外配置。
   - 自定义卸载逻辑（保留 / 删除数据选项）实现困难。
   - 未签名包无法被用户正常安装。
   - 自启注册命令调用受沙箱或权限限制。
3. 最终选型必须在阶段 12.3 完成说明中记录理由和 spike 验证结果。
```

---

## 阶段拆分

建议拆成：

```text
阶段 12.1：发布配置与 self-contained win-x64 输出
阶段 12.2：应用图标与品牌资源
阶段 12.3：安装包选型与最小安装包
阶段 12.4：安装时可选自启与卸载清理
阶段 12.5：安装包验收与发布说明
阶段 12.6：手动验收、长跑验证与收口
```

第一批提交必须很小：

```text
只添加 Directory.Build.props 版本号。
只添加 publish 配置。
不引入 MSIX / WiX / Inno Setup 配置，不新增 installer/ 目录。
不改安装包。
不改 App 启动流程。
不大改 Agent 路径解析（如必须修复，只做最小兼容）。
不改阶段 11 自启逻辑。
```

建议提交信息：

```text
build: add self-contained publish profile and version unified
```

---

## 子阶段文档

- [01-阶段12.1-发布配置与self-contained-win-x64输出.md](./01-阶段12.1-发布配置与self-contained-win-x64输出.md)
- [02-阶段12.2-应用图标与品牌资源.md](./02-阶段12.2-应用图标与品牌资源.md)
- [03-阶段12.3-安装包选型与最小安装包.md](./03-阶段12.3-安装包选型与最小安装包.md)
- [04-阶段12.4-安装时可选自启与卸载清理.md](./04-阶段12.4-安装时可选自启与卸载清理.md)
- [05-阶段12.5-安装包验收与发布说明.md](./05-阶段12.5-安装包验收与发布说明.md)
- [06-阶段12.6-手动验收长跑验证与收口.md](./06-阶段12.6-手动验收长跑验证与收口.md)
