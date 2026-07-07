# LiveCharts2 图表替换变更记录（2026-07-07）

## 背景

Dashboard 前期的 `7-Day Trend` 和 `Activity Heatmap` 都是 WPF 原生控件手写实现：

```text
7-Day Trend:
    ItemsControl + UniformGrid + Border 手写柱状图

Activity Heatmap:
    ItemsControl + UniformGrid + Border 手写 24 x 7 热力图
```

这些实现能满足 MVP，但后续要继续做统计分析、趋势对比、更多图表类型时，手写图表会带来几个问题：

```text
1. 图表能力扩展成本高，例如 tooltip、坐标轴、缩放、图例、动画等都需要自己补。
2. Dashboard XAML 复杂度继续上升，维护成本变高。
3. 后续引入更多统计视图时，缺少统一图表方案。
```

因此，本次正式引入 LiveCharts2，并先替换 Dashboard 中已有的两块图表。

## 改动范围

### 1. 引入 LiveCharts2

修改文件：

```text
src/QuantifiedSelf.Windows.App/QuantifiedSelf.Windows.App.csproj
tests/QuantifiedSelf.Windows.Tests/QuantifiedSelf.Windows.Tests.csproj
```

主要改动：

```xml
<PackageReference Include="LiveChartsCore.SkiaSharpView.WPF" Version="2.0.5" />
```

同时将 App/Test 项目目标框架从：

```text
net8.0-windows
```

调整为：

```text
net8.0-windows10.0.19041
```

原因：

```text
LiveCharts2 WPF 依赖 SkiaSharp WPF。
使用 net8.0-windows10.0.19041 可以避免 SkiaSharp 退回旧兼容资产导致的 NU1701 warning。
Agent / Infrastructure 暂未调整，仍保持 net8.0-windows。
```

### 2. 替换 7-Day Trend 手写柱状图

修改文件：

```text
src/QuantifiedSelf.Windows.App/MainWindow.xaml
src/QuantifiedSelf.Windows.App/ViewModels/DashboardViewModel.cs
```

原实现：

```text
ItemsControl
  UniformGrid Columns=7
    Border 背景柱
    Border 活跃柱
    TextBlock 日期/时长
```

新实现：

```text
LiveCharts2 CartesianChart
  ColumnSeries<double>
  XAxes: 日期/星期标签
  YAxes: 活跃时长
```

保留兼容：

```text
DashboardViewModel.TrendDays 继续保留。
TrendDayItem.BarWidthRatio / BarHeightRatio / BarHeightPixels 继续保留。
```

原因：

```text
1. 避免一次性大改测试和旧绑定。
2. 保留已有趋势计算语义。
3. 后续如需彻底移除旧手写图表字段，可以单独做清理。
```

新增 ViewModel 属性：

```csharp
public ISeries[] ActiveTrendSeries { get; }
public Axis[] ActiveTrendXAxes { get; }
public Axis[] ActiveTrendYAxes { get; }
```

### 3. 替换 Activity Heatmap 手写热力图

修改文件：

```text
src/QuantifiedSelf.Windows.App/MainWindow.xaml
src/QuantifiedSelf.Windows.App/ViewModels/HourActivityHeatmapViewModel.cs
```

原实现：

```text
ItemsControl DateLabels
ItemsControl HourLabels
ItemsControl Cells
  UniformGrid Columns=7
  Border Background="{Binding Background}"
```

新实现：

```text
LiveCharts2 CartesianChart
  HeatSeries<WeightedPoint>
  XAxes: 7 日日期标签
  YAxes: 睡觉 / 上午 / 下午 / 晚上分段标签
```

保留兼容：

```text
HourActivityHeatmapViewModel.Cells 继续保留。
DateLabels / HourLabels 继续保留。
HeatmapCellViewModel.InterpolateColor 继续保留。
```

原因：

```text
1. 现有 heatmap 计算和测试已经比较完整。
2. 保留 Cells 可以继续验证 168 个小时格子的语义。
3. LiveCharts2 只接管展示层，不改变热力图数据计算。
```

新增 ViewModel 属性：

```csharp
public ISeries[] HeatmapSeries { get; }
public Axis[] HeatmapXAxes { get; }
public Axis[] HeatmapYAxes { get; }
```

## 关键风险与处理

### 风险 1：TargetFramework 变化影响 App 输出目录

App 输出目录从：

```text
src/QuantifiedSelf.Windows.App/bin/Debug/net8.0-windows/
```

变为：

```text
src/QuantifiedSelf.Windows.App/bin/Debug/net8.0-windows10.0.19041/
```

这会影响开发态 `AgentProcessService.ResolveAgentExecutablePath` 原本固定向上 5 层推导仓库根的逻辑。

处理：

```text
AgentProcessService 不再硬编码向上 5 层。
改为从 AppContext.BaseDirectory 向上查找 QuantifiedSelf.Windows.sln 或 src/QuantifiedSelf.Windows.Agent。
找不到时再 fallback 到旧的向上 5 层策略。
```

新增测试：

```text
ResolveAgentExecutablePath_FindsDevelopmentPath_WhenTargetFrameworkDepthChanges
```

### 风险 2：LiveCharts2 数据绑定没有覆盖测试

处理：

```text
Dashboard_ShowsSevenDayTrend 增加断言：
    ActiveTrendSeries 为 ColumnSeries<double>
    series values 有 7 个点
    至少一个点大于 0
    X axis 有 7 个 label
```

### 风险 3：Heatmap 替换后丢失 168 格语义

处理：

```text
Dashboard_HeatmapLoadsWithTrend 增加断言：
    HeatmapSeries 为 HeatSeries<WeightedPoint>
    weighted points 数量为 168
    至少一个 Weight > 0
    XAxes / YAxes 存在
```

旧的 `Cells.Count == 168` 断言继续保留。

### 风险 4：Windows Forms Padding 命名冲突

由于 App 项目启用了 `UseWindowsForms=true`，`Padding` 会在：

```text
LiveChartsCore.Drawing.Padding
System.Windows.Forms.Padding
```

之间产生歧义。

处理：

```csharp
PointPadding = new LiveChartsCore.Drawing.Padding(2)
```

## 验证结果

已执行：

```powershell
dotnet restore .\QuantifiedSelf.Windows.sln
dotnet build .\QuantifiedSelf.Windows.sln --no-restore
dotnet test .\QuantifiedSelf.Windows.sln --no-build
```

结果：

```text
Build: 0 warning / 0 error
Test: 440 / 440 passed
```

额外说明：

```text
首次 restore 需要联网下载 LiveCharts2 / SkiaSharp / OpenTK 相关依赖。
在 sandbox 网络受限环境下，需要允许 dotnet restore 访问 NuGet 源。
```

## 当前状态

```text
LiveCharts2 已正式进入 App 项目。
Dashboard 现有两块图表已由 LiveCharts2 渲染。
旧手写图表数据字段暂时保留，用于兼容测试和后续渐进清理。
```

## 后续建议

```text
1. 手动打开 Dashboard，目视确认 7-Day Trend 和 Activity Heatmap 的高度、标签、tooltip 和颜色是否符合使用习惯。
2. 如果视觉密度不理想，优先微调 chart 高度、axis label、bar width、heatmap color stops。
3. 后续新增统计图表时，默认优先使用 LiveCharts2，避免继续新增手写图表。
4. 等 Dashboard 图表稳定后，可以单独做一次清理：移除 TrendDayItem 中仅服务旧手写图表的 BarHeightPixels 等字段。
```
