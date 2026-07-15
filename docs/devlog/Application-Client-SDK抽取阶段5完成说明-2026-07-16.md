# Application / Client SDK 抽取阶段 5 完成说明

日期：2026-07-16  
状态：已完成  
实施分支：`codex/application-client-sdk`

## 1. 实施范围

本次完成《Application / Client SDK 抽取方案》的阶段 5：

- 在 Application 定义 AppSettings 和 Agent Options 存储端口；
- 将 `SettingsService` 从 WPF App 迁入 Application，并改为依赖存储端口；
- 在 Client 增加 Windows 设置存储适配器，绑定现有 JSON store 与 channel 路径；
- 将登录自启注册、Run Key 抽象、命令构建和启动参数解析从 App 迁入 Client；
- 保持 `StartupRegistrationDisplayModel` 与 `WindowStartupPolicy` 在 WPF App；
- 将 WPF ViewModel 改为消费 Application 设置接口和 Client 启动注册接口；
- 扩展设置、启动注册、prod/dev channel 和架构边界测试。

本次未实施阶段 6 及后续工作。统一 `IWujiClient` facade、Client composition factory、App composition root 收口、项目引用清理、测试项目拆分、Bridge 与发布流程保持在后续阶段。

## 2. Application 设置端口

新增 `ApplicationLayer.Abstractions.Settings`：

- `IAppSettingsStore`
  - 读取 AppSettings；
  - 保存 AppSettings。
- `IAgentOptionsStore`
  - 读取 WindowsAgentOptions；
  - 普通保存；
  - 带备份保存；
  - 恢复备份。

这些端口只使用 Core 设置模型、`Task` 和 `CancellationToken`，不接收或返回：

- `WindowsAgentPaths`；
- 本地绝对路径；
- Infrastructure store；
- Registry 类型；
- WPF、WinForms、Dispatcher 或 UI 控件。

Application 项目继续为 `net8.0` 且只引用 Core。

## 3. Application 设置用例

新增：

- `ISettingsService`
- `SettingsService`

`SettingsService` 保留迁移前的行为：

- 设置文件不存在时返回默认 `AppSettings`；
- Agent options 文件不存在时返回默认 `WindowsAgentOptions`；
- 保存前继续执行 null 检查；
- 普通保存、带备份保存和备份恢复仍是三个独立操作；
- CancellationToken 原样传递给底层存储；
- 不捕获或包装底层异常，由 WPF ViewModel 保持现有安全错误展示流程。

Application 不再自行拼接 `config/app-settings.json` 或 `windows-agent.json` 路径。

## 4. Client 设置存储适配器

新增 `WindowsSettingsStoreAdapter`，实现 Application 的两个设置存储端口，并复用：

- Infrastructure `AppSettingsStore`；
- Infrastructure `WindowsAgentOptionsStore`；
- Core `WindowsAgentPaths`。

适配关系：

```text
IAppSettingsStore
  -> AppSettingsStore
  -> <channel root>/config/app-settings.json

IAgentOptionsStore
  -> WindowsAgentOptionsStore
  -> <channel root>/config/windows-agent.json
```

路径属性只作为 Client 内部实现和测试可见信息，不进入 Application 合同或前端 SDK DTO。

现有 JSON 序列化、临时文件替换、`.bak` 备份、失败清理与备份恢复算法未修改。

## 5. 登录自启迁入 Client

以下类型由 App `Services` 迁入 `QuantifiedSelf.Windows.Client.Startup`：

- `IStartupRegistry`
- `RegistryStartupRegistry`
- `IStartupRegistrationService`
- `StartupRegistrationService`
- `StartupRegistrationStatus`
- `StartupRegistrationState`
- `StartupCommandBuilder`
- `StartupLaunchOptions`
- `LaunchMode`

这些类型形成 Windows Client SDK 可复用的启动边界：

- `RegistryStartupRegistry` 只访问当前用户 HKCU Run Key；
- `StartupRegistrationService` 只操作指定的 WUJI value，不修改其他启动项；
- `StartupRegistrationStatus` 返回 `Enabled`、`Disabled`、`Mismatch`、`Error` 或 `UnsupportedInCurrentLaunchMode`；
- 错误状态继续只返回安全固定文本，不暴露注册表原值、路径或原始异常；
- `StartupCommandBuilder` 继续拒绝 `dotnet.exe`、DLL、空路径和无效 executable；
- 命令比较继续忽略路径大小写并要求完整的 `--from-autostart`、`--start-hidden` 和 channel 参数；
- `StartupLaunchOptions` 继续解析 autostart、hidden、channel、preview 和 Agent console 参数。

## 6. prod/dev 兼容性

阶段 5 保持现有 channel 隔离：

| 项目 | prod | dev |
|---|---|---|
| 数据根目录 | `%LOCALAPPDATA%/WUJI/WindowsAgent` | `%LOCALAPPDATA%/WUJI-Dev/WindowsAgent` |
| AppSettings | `config/app-settings.json` | `config/app-settings.json`，位于 dev root |
| Agent options | `config/windows-agent.json` | `config/windows-agent.json`，位于 dev root |
| Run Key value | `WUJI` | `WUJI Dev` |
| 自启命令 | `--from-autostart --start-hidden` | 同左并追加 `--channel dev` |

测试明确验证 dev 注册不会读写 prod value，且 dev 命令必须包含精确的 channel 参数。

## 7. WPF 保留边界与接线

以下 UI 类型继续留在 App：

- `StartupRegistrationDisplayModel`：把结构化状态投影为安全显示文本；
- `WindowStartupPolicy`：决定启动时显示窗口还是保持隐藏；
- `SettingsViewModel`、`MainWindowViewModel`；
- XAML、窗口、托盘和 Dispatcher scheduler。

WPF 消费边界调整为：

- `MainWindowViewModel` 依赖 Application `ISettingsService`；
- `SettingsViewModel` 依赖 Application `ISettingsService`；
- 两个 ViewModel 的启动注册边界使用 Client `IStartupRegistrationService`；
- `App.xaml.cs` 创建 `WindowsSettingsStoreAdapter + SettingsService`，并继续按 runtime channel 创建启动注册服务。

ViewModel 未增加注册表、文件路径或同步 I/O；现有 async command、状态文本、错误清洗和 UI 线程行为保持不变。

Legacy/Preview 双 shell、`--channel dev --ui-preview` gate 和启动顺序未改变。把所有底层对象创建移出 `App.xaml.cs` 属于阶段 6。

## 8. 从 App 删除的实现

以下文件已从 App `Services` 删除并迁入 Application 或 Client：

- `SettingsService.cs`
- `IStartupRegistry.cs`
- `RegistryStartupRegistry.cs`
- `IStartupRegistrationService.cs`
- `StartupRegistrationService.cs`
- `StartupRegistrationStatus.cs`
- `StartupCommandBuilder.cs`
- `StartupLaunchOptions.cs`

以下类型有意保留在 App：

- `StartupRegistrationDisplayModel.cs`
- `WindowStartupPolicy.cs`

## 9. 测试迁移与架构门禁

新增 `SettingsTestServices`，统一组装：

```text
Infrastructure JSON stores
  -> Client WindowsSettingsStoreAdapter
  -> Application SettingsService
```

既有设置和 ViewModel 测试已切换到这条真实端口链路，因此继续覆盖：

- 默认设置读取；
- AppSettings 保存与重新读取；
- Agent options 保存、规范化和 reload 流程；
- 带备份写入；
- 写入失败时保留原文件；
- `.bak` 恢复和备份缺失错误；
- Settings/Privacy/MainWindow ViewModel 状态更新；
- 登录自启注册、注销、幂等、mismatch、unsupported 和错误脱敏。

架构测试新增并固化：

1. 设置 store ports 与 `ISettingsService` 必须属于 Application assembly；
2. `WindowsSettingsStoreAdapter` 必须属于 Client assembly 并实现两个 store port；
3. 启动注册、命令构建和启动参数类型必须属于 Client assembly；
4. App Services 不得重新出现已迁移的八个设置/启动文件；
5. `StartupRegistrationDisplayModel` 与 `WindowStartupPolicy` 必须继续属于 App；
6. WPF 设置消费者必须使用 Application/Client 接口；
7. Application、Client 与 Infrastructure 继续不得引用 WPF、WinForms、LiveCharts、Skia 或 Dispatcher。

架构边界测试现为 26/26 通过。

## 10. 验证结果

```text
dotnet build QuantifiedSelf.Windows.sln -c Release --no-restore
结果：0 warnings，0 errors

ArchitectureBoundaryTests
结果：26/26 passed

Category=Fast
结果：118/118 passed

Category=Integration
结果：403/403 passed

Category=Wpf
结果：没有匹配测试，命令成功退出

Full suite
结果：531/531 passed
```

全量测试由阶段 4 完成时的 525 增加到 531；新增 6 个执行项来自阶段 5 架构门禁与 channel 兼容测试。

本阶段没有 XAML 或可见 UI 改动，因此未执行 preview UI 视觉验收。测试使用内存注册表 fake 验证 value name 与命令，不修改当前用户真实 Run Key。

## 11. 未修改边界

本次未修改：

- AppSettings 和 WindowsAgentOptions JSON schema；
- `.tmp`、`.bak`、原子替换和备份恢复算法；
- Agent options 校验、规范化与 ReloadConfig UI 流程；
- Named Pipe、control/runtime/health 文件或 Agent 进程控制；
- SQLite schema、SQL 查询和 Activity 用例；
- Agent state machine、采样循环或后台服务；
- Legacy/Preview shell 选择；
- `publish/scripts/publish.ps1` 和发布目录结构。

## 12. 后续工作

下一步应实施阶段 6：建立 `IWujiClient`/feature clients 和 Client composition factory，把 store、query adapter、Named Pipe、Agent process controller 与设置/启动服务的创建从 `App.xaml.cs` 移入 Client。

阶段 6 完成后，App 应移除对 Core、Infrastructure 和 Agent 的直接项目引用，`App.xaml.cs` 只保留 WPF host、窗口、托盘、主题、Dispatcher 和 ViewModel 接线，形成真正可替换的纯 UI 层。
