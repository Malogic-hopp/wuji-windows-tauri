# WUJI Bridge 合同

`v1/bridge.schema.json` 是 Client Bridge 跨语言合同的唯一来源。

生成 C#、Rust、TypeScript staging 类型：

```powershell
dotnet run --project .\tools\QuantifiedSelf.Windows.Bridge.ContractGen -- --write
```

检查生成物是否与 schema 一致：

```powershell
dotnet run --project .\tools\QuantifiedSelf.Windows.Bridge.ContractGen -- --check
```

生成文件不得手工修改：

- C#：`src/QuantifiedSelf.Windows.Client.Bridge/Generated/BridgeContracts.g.cs`；
- TypeScript：`contracts/wuji-bridge/v1/generated/typescript/bridge-contracts.generated.ts`；
- Rust：`contracts/wuji-bridge/v1/generated/rust/bridge_contracts.generated.rs`。

方法演进必须遵循：先修改 schema、重新生成三端类型、补合同/协议测试，再实现 Bridge handler。

当前 v1 白名单包含：

- Bridge 生命周期：`bridge.hello`、`client.initialize`、`bridge.shutdown`；
- Agent 生命周期：`agent.getStatus`、`agent.start`、`agent.pause`、`agent.resume`、`agent.stop`；
- Dashboard：`activity.getOverview`。
- Settings：`settings.get`、`settings.update`。

`activity.getOverview` 只返回今日摘要、Top Apps 和最近会话的安全投影，不包含窗口标题、进程名、数据库 ID、路径或内部异常。

Settings v1 只允许主题、刷新间隔、UI 启动 Agent，以及 Agent 的五个数值参数和四个布尔开关。`settings.get` 同时返回当前安全快照与由 Core/Application 投影的默认快照，前端不复制默认值。Windows 登录启动、导航状态、托盘策略、模拟采集、排除进程/标题、路径、注册表、数据库和任意键值配置不进入合同。`settings.update` 必须通过 Application 校验，字段错误只返回固定字段名和安全消息。
