这份阶段 11「开机自启 MVP」计划整体**可行，可以作为下一阶段正式计划**。方向是对的：阶段 10 已经完成托盘常驻和后台运行，下一步做“当前用户登录后自动启动 WPF App 到托盘”比安装包、Windows Service、趋势图或导出更自然。计划中也明确区分了 `StartAppOnWindowsLogin` 和已有的 `AutoStartAgentWhenAppStarts`，这是阶段 11 最关键的设计点。

---

# 总体审核结论

建议执行，但在拆成 11.1–11.6 提示词前，建议补强几个关键边界。

这份计划的目标很清楚：

```text id="0qtt9t"
让 WUJI 支持当前用户登录 Windows 后自动启动到托盘，
并继续复用已有 AutoStartAgentWhenAppStarts 决定是否随 App 启动 Agent。
```

范围也控制得比较稳：

```text id="jjdjzb"
不做安装包
不做 Windows Service
不做 HKLM / 管理员权限自启
不做计划任务高级配置
不做多用户服务化
不做自动更新
不改 IPC
不改 AgentStateMachine
不改 PruneData / ClearHistory
```

这个边界是合理的。

---

# 我认可的关键设计

## 1. 当前用户级自启，而不是服务化

计划优先使用：

```text id="s4n4eg"
HKCU\Software\Microsoft\Windows\CurrentVersion\Run
```

这是合理的 MVP 方案。

优点是：

```text id="d960p2"
不需要管理员权限
不影响其他 Windows 用户
容易启用 / 禁用
容易诊断
适合当前桌面 App 阶段
```

不建议现在做 Windows Service。这个项目需要读取当前用户桌面前台窗口，服务化会牵涉交互式会话、权限、部署和调试复杂度，不适合这个阶段。

---

## 2. App 自启和 Agent 自启分开

这是整份计划里最重要的一点。

建议新增：

```text id="8vuq64"
StartAppOnWindowsLogin: bool
```

保留已有：

```text id="2vcl36"
AutoStartAgentWhenAppStarts: bool
```

两者语义必须严格分开：

```text id="4u1nxc"
StartAppOnWindowsLogin:
    Windows 登录后是否启动 WPF App。

AutoStartAgentWhenAppStarts:
    WPF App 启动后是否自动启动 Agent。
```

这个设计非常正确。否则用户会混淆“开机打开控制台”和“打开控制台后启动采集进程”。

---

## 3. 自启默认进入托盘

计划要求自启参数：

```text id="it4uud"
--from-autostart
--start-hidden
```

并且自启启动时：

```text id="m4uirm"
创建 MainWindow / ViewModel / TrayService
启动 status polling
不弹主窗口
托盘图标可见
托盘 Show 可恢复窗口
```

这很合理。阶段 10 已经解决了托盘常驻，如果登录后还弹主窗口，就会破坏产品体验。

---

## 4. StartupRegistrationService 边界清楚

计划中 `StartupRegistrationService` 只负责：

```text id="ar1o30"
读取 HKCU Run Key
注册当前 App 自启
取消注册当前 App 自启
检测 Enabled / Disabled / Mismatch / Error
返回安全状态和错误文案
```

不负责：

```text id="7bsrka"
启动 Agent
停止 Agent
创建托盘
刷新页面
读写 SQLite
弹 UI
```

这个边界很好。自启注册是 OS 集成层，不应该和 Agent 控制混在一起。

---

# 建议补强 1：明确当前 App exe 路径获取策略

这是阶段 11 最大的技术风险之一。

Run Key 里要写：

```text id="g9e3mm"
"<current app executable path>" --from-autostart --start-hidden
```

但“当前 app executable path”在不同运行方式下可能不一样：

```text id="u8ecub"
dotnet run
Debug build
Release build
framework-dependent exe
single-file publish
self-contained publish
```

如果错误地使用 `Assembly.Location`，可能拿到 `.dll`；如果在某些调试场景使用 `ProcessPath`，也可能拿到 `dotnet.exe`。

建议在计划里加一条：

```text id="m3lcgt"
StartupCommandBuilder 必须集中封装 App 可执行路径解析；优先使用 Environment.ProcessPath / Process.MainModule.FileName，但需要检测是否指向当前 WPF App 的 .exe。若检测到 dotnet.exe 或非 App exe，应返回 Error / UnsupportedInCurrentLaunchMode，而不是注册错误路径。
```

并要求测试覆盖：

```text id="x5cl4e"
StartupCommandBuilder_RejectsDotnetHostPath
StartupCommandBuilder_UsesCurrentAppExePath
StartupRegistrationService_DoesNotRegisterWhenExecutablePathInvalid
```

这能避免把 Run Key 注册成：

```text id="5yj5qy"
"C:\Program Files\dotnet\dotnet.exe" --from-autostart --start-hidden
```

这种明显错误的命令。

---

# 建议补强 2：明确 command 比较要做规范化

计划里已经提到 Mismatch，但建议更具体。

Run Key command 比较时，下面这些都可能影响判断：

```text id="itw3ga"
路径大小写
引号
额外空格
参数顺序
.exe 路径的长短路径差异
```

建议补充：

```text id="z4m83h"
StartupCommandBuilder 应提供 Normalize / Parse 方法，用于比较 registered command 是否指向当前 App 且包含必要参数；不要只做简单字符串完全相等。
```

但也不要过度复杂。MVP 可以定义为：

```text id="a37tya"
exe 路径规范化后相等
必须包含 --from-autostart
必须包含 --start-hidden
其他未知参数可以忽略或判定 Mismatch，需文档明确
```

---

# 建议补强 3：Settings 保存语义要避免“AppSettings 与 OS 状态不一致”

计划里写“启用时先注册再保存，失败则不宣称启用成功”，这个很好。

建议进一步明确：

```text id="f3421g"
StartAppOnWindowsLogin 的 UI 开关值是用户草稿；
真实状态以 StartupRegistrationStatus 为准；
保存失败时 AppSettings 不应写成 true；
刷新时如果 AppSettings=true 但 OS Run Key 缺失，应显示 Mismatch / Disabled with warning，而不是静默改 UI。
```

也就是说，阶段 11 要防止出现：

```text id="bcmzyr"
Settings 显示已启用
但 HKCU Run Key 实际不存在
```

这个比普通配置保存更敏感。

---

# 建议补强 4：Settings dirty 规则要写得再硬一点

计划已经说 Settings dirty 不覆盖草稿。建议强化为：

```text id="fgg2as"
Settings IsDirty=true 时，status polling / diagnostics refresh 可以更新只读 StartupRegistrationStatusText，但不得覆盖 StartAppOnWindowsLogin 草稿值，也不得触发注册表写入或删除。
```

这样可以避免用户正在编辑开关时，后台刷新把值改回 OS 状态。

---

# 建议补强 5：自启到托盘不要依赖 CloseToTray

自启隐藏窗口应该是启动策略，而不是模拟点击关闭。

建议加一句：

```text id="wf8jk6"
--from-autostart --start-hidden 启动时，不应通过触发 Window.Closing / CloseToTray 来隐藏窗口；应在启动流程中根据 StartupLaunchOptions 直接决定是否 Show 主窗口。
```

否则可能绕进阶段 10 的 Closing 逻辑，导致生命周期复杂化。

推荐语义：

```text id="urlr0y"
Manual:
    window.Show()

AutoStartHidden:
    创建 window + viewModel + tray + polling
    不调用 window.Show()
    或确保无闪烁地隐藏
```

---

# 建议补强 6：自启模式下错误提示不要弹阻塞对话框

计划已经写了不弹阻塞式错误对话框。建议更具体：

```text id="j1zb91"
AutoStartHidden 模式下，启动期错误应写入安全状态文本 / Diagnostics / 日志，不弹 MessageBox 阻塞用户登录流程。
```

如果 App 启动时注册状态读取失败，也不应该打断登录体验。

---

# 建议补强 7：多实例风险需要尽早定策略

计划把 single-instance 放到候补，这可以接受。但开机自启一做，多实例风险会明显上升：

```text id="ualn77"
Windows 登录自启启动了一个 App
用户又手动点了一次 App
可能出现两个托盘图标、两个 status polling、两个控制台
```

如果当前项目已经有单实例机制，必须复用。
如果没有，我建议阶段 11 至少加入一个“轻量风险记录 + 手动验收”，但不一定立刻实现。

建议补充：

```text id="ohsnt9"
阶段 11 不强制实现 single-instance，但必须在 11.6 手动验收中验证重复启动行为；如出现两个托盘图标或状态漂移，应记录为阶段 12 前置风险，或在 11.x 中以最小 named mutex 修复。
```

如果实现 single-instance，必须小步做，不要影响自启主线。

---

# 建议补强 8：Diagnostics 不显示完整路径，但 Settings 可能需要“可理解状态”

计划要求不显示完整路径，这对隐私是对的。
但用户需要知道 mismatch 是怎么回事。

建议文案分层：

```text id="fgcyp2"
Settings:
    Startup registration: Enabled / Disabled / Needs repair / Error

Diagnostics:
    Login startup: Mismatch
    Reason: Registered command does not match current app
```

不要显示完整路径，但可以显示安全原因：

```text id="gj8he9"
Registered command points to a different app location.
Required startup arguments are missing.
Registration unavailable.
```

这样既安全，又能诊断。

---

# 建议补强 9：11.1 不要提前动 Settings UI

计划里 11.1 是 AppSettings 与启动参数基础。建议明确：

```text id="fak9g5"
11.1 只加配置字段、启动参数解析和测试；不改 Settings UI，不写注册表，不改变启动显示行为。
```

这样 11.1 会非常稳。Settings UI 放到 11.3 更合适。

---

# 建议补强 10：11.4 测试不要强行启动真实 WPF App

计划里写如果直接测试 `App.xaml.cs` 困难，可抽协调器，这很好。建议明确：

```text id="l9d7ve"
11.4 优先抽出 AppStartupCoordinator / WindowStartupPolicy 做纯逻辑测试；不要为了自动化测试真实 WPF startup 而引入脆弱 UI 测试。
```

真实自启到托盘留给手动验收。

---

# 可直接追加到计划里的补充段

建议把下面这段加入“架构原则”或“风险与对策”：

```text id="ir4x5y"
补充硬约束：

1. StartupCommandBuilder 必须集中封装 App 可执行路径解析；若检测到当前路径不是 WUJI App .exe（例如 dotnet.exe / dll / 空路径），不得注册 Run Key，应返回安全 Error 状态。
2. Run Key command 比较应使用规范化解析，不要只做简单字符串相等；至少校验 exe 路径指向当前 App，且包含 --from-autostart 与 --start-hidden。
3. StartAppOnWindowsLogin 的 UI 开关是用户草稿，StartupRegistrationStatus 是 OS 真实状态；保存失败时不得让 AppSettings 显示已启用但 OS 未注册。
4. Settings IsDirty=true 时，可以更新只读 StartupRegistrationStatusText，但不得覆盖 StartAppOnWindowsLogin 草稿，也不得触发注册表写入或删除。
5. --from-autostart --start-hidden 启动时，不应通过触发 Window.Closing / CloseToTray 来隐藏窗口；应在启动流程中直接决定是否显示主窗口。
6. AutoStartHidden 模式下，启动期错误不得弹 MessageBox 阻塞用户登录流程，应进入安全状态文本 / Diagnostics。
7. 阶段 11 不强制实现 single-instance，但必须在 11.6 手动验收中验证重复启动行为；如果出现双托盘图标或状态漂移，需记录为后续前置风险或最小修复项。
8. 11.1 不改 Settings UI、不写注册表、不改变启动显示行为；只做 AppSettings 字段、启动参数解析和测试。
```

---

# 阶段拆分评价

你的拆分是合理的：

```text id="is8qzo"
11.1 AppSettings 与启动参数基础
11.2 StartupRegistrationService 与 HKCU Run Key
11.3 Settings 自启开关与保存同步
11.4 登录自启启动到托盘
11.5 Diagnostics 自启状态与错误展示
11.6 手动验收、长跑验证与收口
```

我建议保持这个顺序。

尤其不要把 11.2 和 11.3 合并。
先把注册服务和测试替身做稳，再接 Settings UI，风险会低很多。

---

# 下一步执行建议

可以先从 11.1 开始，提示词重点是：

```text id="e7id0a"
新增 AppSettings.StartAppOnWindowsLogin，默认 false；
新增 StartupLaunchOptions；
识别 --from-autostart / --start-hidden；
旧配置兼容；
不写注册表；
不改 Settings UI；
不改变启动显示行为。
```

11.1 应该是一个很小的提交。完成后再进入 11.2。

---

# 最终结论

这份阶段 11「开机自启 MVP」计划**可以执行**。

我建议补强后采用。最重要的三个补强点是：

```text id="q1myvh"
1. 明确 App exe 路径解析和 Run Key command 规范化，避免注册错误路径。
2. 明确 AppSettings 设置值与 OS 注册真实状态的关系，避免 UI 假启用。
3. 明确自启隐藏到托盘应由启动策略决定，不要模拟 CloseToTray。
```

补完后，这份计划就可以作为阶段 11 的正式实施计划。
