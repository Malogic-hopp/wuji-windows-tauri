# 下一步计划：Settings 与配置应用 MVP

这是一份把 `docs/下一步计划-2026-06-22-Settings与数据管理MVP.md` 拆成可独立实施、可独立验收的阶段版计划。

说明：目录名延续主计划文件名中的“Settings 与数据管理 MVP”，但 review 后本阶段实际边界已收紧为：

```text
Settings 与配置应用 MVP
```

`PruneData / ClearHistory` 已后移为阶段 7 数据清理 MVP，不进入本阶段实施范围。

## 总目标

让用户能安全查看、修改、校验并应用配置，尤其是采集参数和隐私规则。

阶段完成后，WUJI 应具备：

```text
配置可见
配置可编辑
配置可校验
配置可备份 / 恢复
配置可 ReloadConfig
能证明 Agent 已应用新配置
```

## 当前基础

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
SamplesView / SessionsView / AppsView 数据浏览
Dashboard Top Apps 与 AppsView Today 统计口径一致
```

上一阶段验证结果：

```text
dotnet build QuantifiedSelf.Windows.sln --no-restore -p:BaseOutputPath=.codex\build-output\
    通过，0 warnings / 0 errors

dotnet test QuantifiedSelf.Windows.sln --no-restore -p:BaseOutputPath=.codex\test-output\
    通过，55 / 55 tests
```

## 拆分原则

1. 先只读展示，再做编辑。
2. 先 WPF 自有配置，再 Agent 采集域配置。
3. WPF 和 Agent 必须共享 `AgentOptionsValidator`。
4. 无效配置不能覆盖正式配置。
5. 保存 Agent 配置必须有 `.bak`、临时文件原子替换和 Restore Backup。
6. `ReloadConfig` 不只看事件，还要验证后续采集真实生效。
7. 隐私规则编辑必须有归一化预览。
8. `retentionDays` 本阶段只保存配置，不触发实际清理。
9. `PruneData / ClearHistory` 后移阶段 7。

## 阶段目录

- [阶段 6.1：Settings 读取与展示](./01-阶段6.1-Settings读取与展示.md)
- [阶段 6.2：App Settings 编辑](./02-阶段6.2-AppSettings编辑.md)
- [阶段 6.3：Agent Options Validator 与编辑](./03-阶段6.3-AgentOptionsValidator与编辑.md)
- [阶段 6.4：Save / Backup / Restore 配置文件链路](./04-阶段6.4-SaveBackupRestore配置文件链路.md)
- [阶段 6.5：ReloadConfig 应用链路](./05-阶段6.5-ReloadConfig应用链路.md)
- [阶段 6.6：隐私规则编辑与生效验收](./06-阶段6.6-隐私规则编辑与生效验收.md)
- [阶段 6.7：验收、稳定化与收口](./07-阶段6.7-验收稳定化与收口.md)

## 暂缓事项

本阶段不做：

- Named Pipe / gRPC 主控制通道
- Agent 状态流订阅
- 托盘 TrayService
- 开机自启
- 安装包
- 7 天趋势和图表
- 应用分类
- 浏览器网页级识别
- 数据导出
- `PruneData`
- `ClearHistory`
