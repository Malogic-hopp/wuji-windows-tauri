# Dashboard 补充图表开发规格（2026-07-07）

## 文档目的

本文档供后续 Agent 开发使用，明确 WUJI Dashboard 在已迁移到 LiveCharts2 的基础上，还可以补充哪些可视化图表。

文档包含：

- 当前已落地图表状态
- 推荐补充图表的详细规格（数据、类型、样式、边界情况、验收标准）
- 建议开发顺序与阶段边界
- 通用实现约定与风险

开发前请先阅读：

- `docs/LiveCharts2图表替换变更记录-2026-07-07.md`（LiveCharts2 已落地内容）
- `src/QuantifiedSelf.Windows.App/ViewModels/DashboardViewModel.cs`
- `src/QuantifiedSelf.Windows.App/ViewModels/HourActivityHeatmapViewModel.cs`
- `src/QuantifiedSelf.Windows.App/MainWindow.xaml`

---

## 当前已落地图表

| 图表 | 位置 | LiveCharts2 类型 | 数据 |
|---|---|---|---|
| **Activity Heatmap** | Dashboard / Today Insight | `HeatSeries<WeightedPoint>` | `HourActivityHeatmapViewModel` |
| **7-Day Trend** | Dashboard / Today Insight | `ColumnSeries<double>` | `DashboardViewModel.ActiveTrendSeries` |

其余区域（Top Apps / Top Windows / 指标卡 / Suggestions）目前仍是文本/列表形式。

---

## 推荐补充图表清单

### P1：应用排行水平条形图（Top Apps Horizontal Bar）

**价值**

把当前 Dashboard 底部的 Top Apps 文本卡片改成条形图，让用户一眼看出今日各应用占用时间差异。

**推荐位置**

替换 Dashboard 中现有的 “Top Apps” 文本卡片；或在其上方新增图表区域。

**数据**

直接使用 `DashboardViewModel.TopApps`（`ObservableCollection<AppUsageSummary>`）。

需要字段：

- `DisplayName`（展示名称，空时 fallback 到 `ProcessName`）
- `ActiveDurationSeconds`（用于排序和条形长度）

**Tooltip 与数据标签**

- 必须自定义 tooltip formatter，显示：`应用名 + 活跃时长`。
- 不要在 tooltip 中暴露原始 `double` 秒数或小数。
- 示例：`Chrome · 2h 15m`

**LiveCharts2 类型**

- `RowSeries<double>`：每个条代表一个应用
- 或 `ColumnSeries<double>` 旋转坐标轴实现，但推荐 `RowSeries` 更适合应用名长标签

**推荐 XAML**

```xml
<Border Background="{StaticResource SurfaceAltBrush}" CornerRadius="12" Padding="14" Margin="0,12,0,0">
    <StackPanel>
        <TextBlock Text="Top Apps" FontSize="14" FontWeight="SemiBold" Margin="0,0,0,8"
                   Foreground="{StaticResource MutedTextBrush}" />
        <lvc:CartesianChart Height="220"
                            Background="Transparent"
                            Series="{Binding DashboardViewModel.TopAppsSeries}"
                            XAxes="{Binding DashboardViewModel.TopAppsXAxes}"
                            YAxes="{Binding DashboardViewModel.TopAppsYAxes}"
                            LegendPosition="Hidden"
                            TooltipPosition="Top" />
    </StackPanel>
</Border>
```

**ViewModel 新增**

```csharp
private ISeries[] _topAppsSeries = [];
private Axis[] _topAppsXAxes = [];
private Axis[] _topAppsYAxes = [];

public ISeries[] TopAppsSeries { get => _topAppsSeries; private set => SetProperty(ref _topAppsSeries, value); }
public Axis[] TopAppsXAxes { get => _topAppsXAxes; private set => SetProperty(ref _topAppsXAxes, value); }
public Axis[] TopAppsYAxes { get => _topAppsYAxes; private set => SetProperty(ref _topAppsYAxes, value); }
```

**样式约定**

- 主题色：`#0F766E`（AccentBrush）
- 最多显示 5 个应用
- Y 轴显示应用名，X 轴显示时长
- 数据标签格式化：`FormatDurationLong(seconds)`

**边界情况**

- `TopApps` 为空：清空 Series，并显示空状态文本（复用已有空状态 Visibility 绑定）。
- 应用名过长：Y 轴标签自动截断或设置 `TextSize` 较小。
- 时长为 0：仍然显示条形长度为 0，但保留标签。
- 刷新失败：保留上一份成功的图表数据，不要用空 Series 覆盖旧图。

**验收标准**

- [ ] 打开 Dashboard 后，Top Apps 区域以水平条形图展示
- [ ] 鼠标悬停显示应用名 + 活跃时长 tooltip
- [ ] 数据为空时显示中文提示，不崩溃
- [ ] Top Apps 图中排名第一的应用显示在最上方；如果 LiveCharts `RowSeries` 默认顺序相反，需要反转 labels/values
- [ ] 至少 1 个单元测试断言 `TopAppsSeries` 类型和数据点数量

---

### P1：今日 24h 堆叠柱状图（Today 24h Stacked Bar）

**价值**

展示今天每个小时的时间构成（Active / Idle / Unknown），弥补 Heatmap 只看“活跃强度”的不足，帮助用户理解一天的时间分配。

**推荐位置**

Today Insight 中，放在 Activity Heatmap 和 7-Day Trend 之间。

**数据**

有两种获取方式，优先推荐方案 A：

**方案 A：复用 `DailyStatsService` 扩展**

在 `DailyStatsService.GetTodaySummaryAsync` 返回的 `DailyActivitySummary` 中新增每小时聚合：

```csharp
public IReadOnlyList<HourlyActivity> HourlyActivity { get; init; } = [];

public record HourlyActivity(
    int Hour,           // 0-23
    long ActiveSeconds,
    long IdleSeconds,
    long UnknownSeconds
);
```

**注意**：`DashboardViewModel` 不直接查询 Samples。所有按小时聚合逻辑必须下沉到 `DailyStatsService` 或专门的 query service。

**LiveCharts2 类型**

- `StackedColumnSeries<double>` 三个 Series：Active / Idle / Unknown

**推荐 XAML**

```xml
<Border Background="{StaticResource SurfaceAltBrush}" CornerRadius="12" Padding="14" Margin="0,12,0,0">
    <StackPanel>
        <TextBlock Text="Today 24h" FontSize="14" FontWeight="SemiBold" Margin="0,0,0,8"
                   Foreground="{StaticResource MutedTextBrush}" />
        <lvc:CartesianChart Height="220"
                            Background="Transparent"
                            Series="{Binding DashboardViewModel.HourlyStackedSeries}"
                            XAxes="{Binding DashboardViewModel.HourlyStackedXAxes}"
                            YAxes="{Binding DashboardViewModel.HourlyStackedYAxes}"
                            LegendPosition="Right"
                            TooltipPosition="Top" />
    </StackPanel>
</Border>
```

**ViewModel 新增**

```csharp
private ISeries[] _hourlyStackedSeries = [];
private Axis[] _hourlyStackedXAxes = [];
private Axis[] _hourlyStackedYAxes = [];

public ISeries[] HourlyStackedSeries { get => _hourlyStackedSeries; private set => SetProperty(ref _hourlyStackedSeries, value); }
public Axis[] HourlyStackedXAxes { get => _hourlyStackedXAxes; private set => SetProperty(ref _hourlyStackedXAxes, value); }
public Axis[] HourlyStackedYAxes { get => _hourlyStackedYAxes; private set => SetProperty(ref _hourlyStackedYAxes, value); }
```

**样式约定**

- Active：`#0F766E`
- Idle：`#94A3B8`（MutedTextBrush 偏灰）
- Unknown：`#E2E8F0`（浅灰）
- X 轴：0-23 点，标签稀疏显示（如每 3 小时一个）
- Y 轴：小时，格式化 `xh` 或 `0-60m`

**边界情况**

- 今天无数据：24 个柱子高度均为 0，或显示空状态覆盖。
- Today 24h 只统计本地当天 00:00-24:00 内的样本；跨天会话只取落在今天范围内的样本。
- 堆叠总和为 0 的柱子：不显示 tooltip。

**依赖改造**

- 必须同步修改：
  - `DailyActivitySummary`（Core 模型）
  - `DailyStatsService`
  - 相关测试

**验收标准**

- [ ] Dashboard 显示 24 根堆叠柱
- [ ] Active / Idle / Unknown 三部分颜色区分明显
- [ ] 图例显示三种状态
- [ ] 空数据时显示中文提示
- [ ] 至少 1 个测试断言 24 个数据点和三种 Series 存在

---

### P2：应用占比环形图（App Share Donut）

**价值**

快速展示今日时间分配比例，适合希望一眼看到“大头在哪”的场景。

**推荐位置**

与 Top Apps 水平条形图二选一，或放在 Today Insight 侧边小卡片中。

**数据**

复用 `TopApps`，取前 5 个，其余合并为 “Other”。

计算：

```csharp
var total = TopApps.Sum(a => a.ActiveDurationSeconds);
var top5 = TopApps.Take(5).Select(a => new { Label = a.DisplayNameOrProcessName, Value = a.ActiveDurationSeconds });
var other = total - top5.Sum(x => x.Value);
```

**LiveCharts2 类型**

- `PieSeries<double>` 配置 `InnerRadius` 做成 Donut 图

**推荐 XAML**

```xml
<lvc:PieChart Height="220"
              Background="Transparent"
              Series="{Binding DashboardViewModel.AppShareSeries}"
              LegendPosition="Right"
              TooltipPosition="Top" />
```

**ViewModel 新增**

```csharp
private ISeries[] _appShareSeries = [];
public ISeries[] AppShareSeries { get => _appShareSeries; private set => SetProperty(ref _appShareSeries, value); }
```

**样式约定**

- 使用与 App 条形图一致的颜色，或 LiveCharts2 默认调色板
- 中心可显示总活跃时长（可选）

**边界情况**

- 无数据：显示空状态，不渲染 PieChart
- 只有 1 个应用：整个环为该应用颜色

**验收标准**

- [ ] 环形图正确显示 Top 5 + Other
- [ ] 鼠标悬停显示应用名 + 占比 + 时长
- [ ] 空数据时不崩溃

---


## 开发顺序建议

考虑到阶段 12 的关键路径是**安装包/发布体验**，建议按以下顺序渐进补充，避免阻塞发布：

### 阶段边界建议

**第一轮只做 Top Apps 水平条形图：**

- 不做 Today 24h
- 不做 Donut
- 不新增服务查询
- 不改 Dashboard 整体布局
- 不移除现有 Top Apps 文本列表，除非图表验收稳定后再替换

Top Apps 图是纯 ViewModel + XAML 改造，风险最低，最适合作为 LiveCharts2 落地后的第一张补充图表。

### 完整顺序

1. **Top Apps 水平条形图（P1）**
   - 纯 UI 改造，不改动 Service
   - 风险最低，收益明显

2. **Today 24h 堆叠柱状图（P1）**
   - 需要 Service 扩展，但 DailyStatsService 已有今日聚合基础
   - 必须下沉到 Service，不在 ViewModel 中直接查询 Samples

3. **App Share Donut（P2）**
   - 与 Top Apps 水平条形图二选一即可，避免信息重复
   - 实现成本低，可作为可选增强

---

## 通用实现约定

### 1. ViewModel 属性模式

所有 Series / Axes 都应为 `ISeries[]` / `Axis[]` 类型，并通过 `SetProperty` 通知更新。

```csharp
private ISeries[] _xxxSeries = [];
public ISeries[] XxxSeries { get => _xxxSeries; private set => SetProperty(ref _xxxSeries, value); }
```

### 2. 空状态与刷新失败处理

区分两种场景：

- **正常空数据**：清空 Series，并显示空状态文本。
- **刷新失败**：保留上一份成功的图表数据，不要用空 Series 覆盖旧图。

已有模式：

```xml
<TextBlock Text="暂无..."
           Visibility="{Binding TopApps.Count, Converter={StaticResource CountToVisibilityConverter}, ConverterParameter=Invert}" />
```

### 3. 颜色常量

建议新增或复用以下颜色：

| 语义 | 色值 |
|---|---|
| Active / 主题 | `#0F766E` |
| Idle | `#94A3B8` |
| Unknown | `#E2E8F0` |
| 警告/切换 | `#F59E0B` |
| 文字 | `#102A43` |
| 辅助文字 | `#52606D` |

### 4. 格式化函数

复用 `DashboardViewModel.FormatDurationLong(long)` 和 `FormatHoursCompact(double)`。

如有新需求，在 `DashboardViewModel` 中新增 `private static` 方法。

### 5. 测试要求

每个新增图表至少补充一个单元测试：

```csharp
// 示例断言
Assert.IsType<RowSeries<double>>(viewModel.TopAppsSeries[0]);
Assert.Equal(5, viewModel.TopAppsSeries[0].Values?.Count() ?? 0);
```

### 6. 动画约定

所有 Dashboard 图表默认关闭动画：

```csharp
new RowSeries<double>
{
    Values = values,
    AnimationsSpeed = TimeSpan.Zero
}
```

- 不要在自动刷新（定时器、切换 Tab、后台同步）时播放图表动画。
- 如后续需要手动刷新动画，必须区分刷新来源后单独实现。

### 7. Tooltip 约定

- 所有图表必须自定义 tooltip formatter，显示用户可读的文本（如 `应用名 + 时长`）。
- 禁止在 tooltip 中直接暴露原始 `double` 秒数或英文默认 label。

### 8. 命名空间注意

由于 App 项目启用 `UseWindowsForms=true`，`Padding` 会产生歧义。使用 LiveCharts2 的 Padding 时必须全限定：

```csharp
new LiveChartsCore.Drawing.Padding(2)
```

---

## 风险与注意事项

1. **阶段 12 边界**：当前关键路径是安装包/发布体验，新增图表不应破坏发布流程。优先做纯 UI 改造（Top Apps 条形图）。

2. **Dashboard 性能**：Today Insight 已经包含 Heatmap 和 7-Day Trend，新增图表过多会导致首次加载变慢。建议每个图表独立 Service 调用，并在 `LoadAsync` 中并行或后台加载。

3. **数据为空**：所有图表都必须处理无数据场景，避免 `NullReferenceException` 或空白区域。

4. **ScrollViewer 高度**：Dashboard 在 TabItem 内使用 `ScrollViewer`，新增图表会增加总高度。建议单张图表高度控制在 160-240 之间。

5. **旧字段兼容**：`TrendDayItem.BarHeightPixels` 等旧手写图表字段暂时保留，新增图表不要再依赖这些字段。

6. **SkiaSharp 依赖**：新增任何 LiveCharts2 图表都不要引入额外 NuGet 包，复用已有的 `LiveChartsCore.SkiaSharpView.WPF`。

---

## 预期变更文件

| 文件 | 变更内容 |
|---|---|
| `src/QuantifiedSelf.Windows.App/MainWindow.xaml` | 新增图表 XAML |
| `src/QuantifiedSelf.Windows.App/ViewModels/DashboardViewModel.cs` | 新增 Series/Axes 属性和构建方法 |
| `src/QuantifiedSelf.Windows.Core/Models/DailyActivitySummary.cs` | 如需新增 HourlyActivity 等字段 |
| `src/QuantifiedSelf.Windows.App/Services/DailyStatsService.cs` | 如需扩展今日聚合 |
| `tests/QuantifiedSelf.Windows.Tests/...` | 新增图表相关单元测试 |

---

## 参考文档

- `docs/LiveCharts2图表替换变更记录-2026-07-07.md`
- `src/QuantifiedSelf.Windows.App/ViewModels/DashboardViewModel.cs`
- `src/QuantifiedSelf.Windows.App/ViewModels/HourActivityHeatmapViewModel.cs`
- `src/QuantifiedSelf.Windows.App/MainWindow.xaml`
