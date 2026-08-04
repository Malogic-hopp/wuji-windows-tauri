有明显提升，而且这版已经从“统计功能堆放”进入了**可以作为正式主页继续收口**的阶段。整体信息架构基本成立，不建议再大改版式，也不建议继续增加指标卡或图表。

## 总体判断

这版最成功的地方，是已经形成了清楚的阅读路径：

> 今日状态 → 近期趋势 → 周期趋势 → 工作模式 → 长期记录

同时也比较好地落实了之前确定的原则：

* 今日状态与本周进度合并为一张主卡，没有形成 KPI 卡片墙。
* 日趋势、周趋势采用开放式区块，页面显得轻。
* 只有“工作惯性”和“应用构成”使用独立卡片，卡片使用比较克制。
* 长期记录放在最下方，并降低了视觉权重。
* 今日、当前周、当前月都使用进行中样式。
* 无记录日期、移动均线断点、当前周参考值等表达比较诚实。
* 蓝、紫、绿分别承担短期、周期和长期层级，颜色虽然不完全统一，但逻辑是成立的。

从实际截图看，页面已经具备比较明显的产品感，不再像调试仪表盘。

---

# 做得比较好的地方

## 1. 首屏主卡明显比拆成多个小卡更好

现在的结构：

* 左侧突出“今日活跃”
* 右侧补充“本周截至今日”
* 中间用分隔线建立层级

这是合理的。今日数据仍然是主角，本周信息作为背景参照，没有抢占一个完整卡片。

“3h42m”也足够醒目，而“8 个工作块”“截至 15:20”“较昨日”等都保持在辅助层级，整体没有过度强调。

## 2. 两张主趋势图已经成为页面骨架

“近 14 天活跃趋势”和“近 12 周活跃总量”都不加卡片边框，是这版视觉上最正确的选择。

尤其是：

* 14 天趋势负责回答最近状态；
* 12 周趋势负责回答是否形成长期变化；
* 当前数据用斜纹而不是当作完整周期；
* 缺失数据没有伪装成 0；
* 均线遇到缺失值会断开。

这些都比单纯追求漂亮更重要。

## 3. 页面避免了“每个模块都套卡片”

目前只有三处明显卡片：

1. 今日状态与本周进度；
2. 工作惯性；
3. 应用构成。

长期区块没有再包卡片，趋势图也没有卡片化。现在页面的轻重关系已经比较自然，建议保持。

## 4. 实现层面的完整度较高

代码中有一些值得保留的细节：

* 数字使用 `tabular-nums`，轮询更新时不容易左右跳动；
* 有 `focus-visible`；
* 图表处理了空数据、零数据和进行中状态；
* 双列在窄屏下可以折叠；
* 已考虑 Windows High Contrast；
* 颜色集中在 design token 中；
* 当前周拆分为“已完成部分 + 今日部分”；
* 周参考值会纳入纵轴最大值，不会被裁掉。

因此，这版不是只有截图好看，组件结构也已经相对扎实。

---

# 仍需要优先优化的问题

## P0：修正“记录日、有效日、缺失日”的语义冲突

截图中同时出现：

* 顶部：`近 14 天记录 13 天`
* 工作惯性：`有效天数 11/14（缺失 3 天）`

普通用户会自然理解成：

> 上面说只缺 1 天，为什么下面又说缺 3 天？

代码中的原因是：

```ts
const missingDays = inertia.totalDays - inertia.effectiveDays;
```

然后直接显示：

```tsx
有效天数 11/14（缺失 3 天）
```

但“没有成为惯性有效样本”不等于“没有记录”。其中可能包含：

* 完全没有记录的日期；
* 有少量记录，但不足以作为有效样本的日期；
* 有应用记录，但没有对应的工作统计投影。

因此这里不应该叫“缺失 3 天”。

### 最低成本修改

改为：

> 有效样本日 11/14（3 天未纳入）

或者：

> 基于 11 个有效样本日

更完整的版本是让 DTO 分别提供：

* `recordedDays`
* `effectiveDays`
* `noDataDays`
* `excludedDays`

然后显示：

> 有效样本日 11 天 · 1 天无记录 · 2 天未达到样本条件

这是目前最应该优先修正的地方，因为它关系到统计可信度，而不只是文案美观。

---

## P1：趋势图还缺少一个“数值解释锚点”

现在两张趋势图形状很清楚，但用户只能知道高低变化，不容易回答：

* 近 14 天平均每天多少？
* 最高的一天多少？
* 近 12 周平均每周多少？
* 本周预计处于什么水平？

我不建议加完整纵轴，会让页面变重。更适合在标题右侧加入一行很轻的汇总值。

例如：

```text
近 14 天活跃趋势                  日均 3h28m · 最高 4h30m
```

```text
近 12 周活跃总量                 周均 22h16m
```

这样既保留极简图表，也让图表拥有可以直接读取的量级。

7/14/30 天切换时，右侧汇总值同步更新即可。

---

## P1：“工作惯性”的名称和图形仍有一点不匹配

当前图实际表达的是：

> 按有效样本日平均后的 24 小时活跃分布。

但“工作惯性”这个词更容易让人想到：

* 连续工作倾向；
* 工作时间是否稳定；
* 工作节律是否具有重复性；
* 开始工作后是否容易持续。

目前的柱状图主要显示“在哪些小时活跃”，严格来说更接近“日内节律”或“典型工作时段”。

不一定必须改掉“工作惯性”，可以加一个解释性副标题：

> 工作惯性（近 14 天）
> 按有效样本日平均的 24 小时活跃分布

或者把标题改成更直接的：

> 日内工作节律（近 14 天）

从产品可理解性来说，我更偏向“日内工作节律”；从品牌化指标来说，也可以保留“工作惯性”，但需要解释。

---

## P1：左右两张卡片的视觉重量仍不够平衡

截图中：

* 左侧工作惯性图比较稀疏；
* 右侧应用构成有 14 行数据，明显更密集；
* 当前 `0.95fr / 1.05fr` 的宽度差异太小。

可以调整为：

```css
.stats-block--split {
  grid-template-columns: minmax(0, 0.82fr) minmax(0, 1.18fr);
}
```

或者稍保守一些：

```css
grid-template-columns: minmax(0, 0.88fr) minmax(0, 1.12fr);
```

同时将惯性图高度从 `160px` 降到约 `135–145px`。这样左卡会更紧凑，应用名称、日期和时长也能获得更多横向空间。

不建议为了填满左卡再增加新指标。

---

## P1：周趋势中的“参考值”表述过于抽象

当前参考线写的是：

> 参考值

但它实际表示：

> 按本周已完成记录日的日均值延展到 7 天得到的预测性参照。

“参考值”不足以告诉用户它是怎么来的。

可以改为：

> 按当前日均推算

或者更短：

> 本周日均推算

最好再在悬停信息中给出具体数值：

> 按已完成记录日的日均值推算：约 24h30m

另外，“上周”已经直接标注在柱顶，图例里再放“上周参照”有一点重复。可以保留直接标注，删除这一项图例，使图例更简洁。

---

## P2：长期记录图的指标说明出现得太晚

现在用户先看到：

> 累计记录 138 天 · 自 2026 年 3 月起 · 最长连续数据区间 67 天

然后看到柱图，直到图表下方才知道它表示：

> 近 6 月月度活跃（每有效日均值）

这会导致用户第一眼不知道柱高到底是：

* 月总量；
* 活跃天数；
* 每日均值；
* 有效日均值。

建议把指标说明移到图表上方：

```text
累计记录 138 天 · 自 2026 年 3 月起 · 最长连续数据区间 67 天
近 6 个月活跃水平 · 每有效日均值
[图表]
```

下方图例只保留：

* 当前月进行中
* 无有效记录

这样信息解释顺序会更自然。

---

# 代码层面还需要处理的几个问题

## 1. `activeDays` 不应从返回点数推导

现在是：

```ts
const [activeDays, setActiveDays] = useState(stats.trend.length);
```

选择范围和实际返回了多少个点，是两个不同概念。

例如后端返回异常、测试数据只有 13 个点，可能出现：

* 标题显示“近 13 天”；
* 7、14、30 三个按钮都没有选中。

建议固定默认选择：

```ts
const [activeDays, setActiveDays] = useState<7 | 14 | 30>(14);
```

后端返回多少有效记录，通过 coverage 单独说明。

## 2. 30 天模式仍然直接读取 fixture

目前：

```ts
activeDays === 30
  ? statsHomeWeekFixture.trend
```

静态阶段可以接受，但接真实命令前一定要移除。否则页面可能出现：

* 今日状态是真实数据；
* 14 天图是真实数据；
* 切到 30 天突然变成演示数据。

这个风险很高，建议在接后端前加入显式 TODO 或禁止生产构建保留 fixture 分支。

## 3. 应用构成的百分比不宜逐段四舍五入

当前每段使用：

```ts
Math.round(duration / total * 100)
```

多段分别取整后，总和可能是 99% 或 101%，导致空隙或挤压。

更稳妥的方式是直接用浮点百分比，或者使用 `flex-grow`：

```tsx
style={{ flexGrow: Number(entry.activeDurationMs) }}
```

再配合：

```css
.comp-seg {
  flex-basis: 0;
}
```

这样比例不会产生累计取整误差。

## 4. 应用构成产生了过多键盘焦点

目前：

* 每一行可聚焦；
* 每一段应用又可聚焦。

14 天 × 多个应用，很容易产生几十个 Tab 停靠点。对静态统计图来说，这会让键盘导航非常疲劳。

建议二选一：

* 每一行只保留一个焦点，在 `aria-label` 中概括日期、总时长和主要应用；
* 或者整张图只保留一个可访问摘要，并提供隐藏的数据表。

每个色块并不是操作控件，没有必要全部进入 Tab 顺序。

## 5. 系统深色模式下主题按钮状态可能不正确

`theme === null` 时，页面遵循系统主题；但按钮文案仍由：

```tsx
theme === 'dark' ? '浅色' : '深色'
```

决定。

因此系统本身为深色时：

* 页面已经是深色；
* 按钮仍显示“深色”；
* 第一次点击可能只是显式设置为深色，视觉上没有变化。

需要读取 `prefers-color-scheme` 后计算实际主题，或者将模式设计为：

* 跟随系统
* 浅色
* 深色

这不是统计主页本身的问题，但截图顶部已经能够看到该控件，最好一起收口。

---

# 建议保持不动的部分

以下内容我认为不应再大幅调整：

* 不要把日趋势、周趋势重新套进卡片。
* 不要增加“总时长、平均时长、工作块、应用数”等四五张 KPI 卡。
* 不要改成饼图或环形图展示应用构成。
* 不要为每张图补齐完整坐标轴和网格线。
* 不要给“上升、下降”添加强烈红绿评价。
* 不要提高长期记录区块的视觉权重。
* 不要为填充空白而增加新的统计指标。

## 最终评价

这版的提升是明确的。当前可以大致评价为：

* **信息架构：成熟**
* **视觉层级：基本成立**
* **统计语义：整体较好，但有效日文案有明显问题**
* **图表可读性：良好，缺少轻量数值锚点**
* **页面克制程度：良好**
* **实现完整度：较高**

下一轮不需要再做“重设计”，建议集中收口这五项：

1. 修正记录日／有效日／缺失日的表达；
2. 为两张趋势图增加轻量数值摘要；
3. 解释或调整“工作惯性”的名称；
4. 调整双列宽度和惯性图高度；
5. 把长期图的指标定义移到图表上方。

完成这些后，这个主页就可以进入真实数据接入与交互验收阶

# 11. 统计主页实施方案

状态：实施方案（2026-08-03，基于 [10-统计主页设计定稿](./10-统计主页设计定稿.md) 冻结版本）。2026-08-03 合同修订 v2（按实施评审 P0 清单收口）+ v3（接口级修订：均线输入类型、`ReaderSnapshot` 快照、cutoff LEFT JOIN、摘要窗口超集、精确阈值判定、周进度公式）后冻结，修订要点见下方"阶段零：合同收口（本版已落实）"。

> 本版修订属于设计允许的**实现纠错**（10 §1："后续仅接受实现纠错，不再扩展 v0.1 产品范围"）。涉及对设计 §5.3/§5.4 的合同修订：`StatsStatusDto` 拆出 `LiveStatusDto` 并携带 `localDate`/`reportingTimeZoneId`、`CompositionBucketDto` 增加 `hasData`。这些修订须在阶段六随 09 §10.6 与 migration-status 同步回设计，不得让设计与实施方案长期分叉。

## 架构概览

```
Rust: wuji-core (DTO + 纯函数) → wuji-storage (Reader/SQL) → desktop/src-tauri (QueryService → 2 命令)
TS:   自动生成类型 → bridge/client.ts → StatsPage + 图表组件（纯 CSS/SVG）
```

**刷新策略**（设计 §5.4 修订）：

| 命令                     | 时机                       | 返回                                                                                                                 |
| ------------------------ | -------------------------- | -------------------------------------------------------------------------------------------------------------------- |
| `stats_get_home(days)` | 进入页面、跨日期、切换范围 | `StatsHomeDto`（全量，含 `status.summary`）                                                                      |
| `stats_get_status()`   | 与顶栏同一拍轮询（5s）     | `StatsStatusDto`（仅 liveStatus + weekProgress + todayTrendPoint，**不含摘要**，不触发惯性/月度/里程碑查询） |

**命令级一致性**：每个命令打开**一个**只读 Reader，并在**单一读事务快照**内完成全部子查询（见阶段三 3.1）。`stats_get_status` 的 cutoff 聚合为**单批次 SQL**，不做逐日 N+1 查询（见阶段二 2.3）。

---

## 阶段零：合同收口（本版已落实）

本阶段只收口合同，不写代码。以下决策已直接落实到后续阶段，作为实施冻结依据：

1. **`LiveStatusDto` 拆分**（P0-1）：`SummaryDto.primaryPeriod` 依赖 14 日惯性、`direction` 依赖趋势窗口，`stats_get_status` 若要重组完整 `StatusDto` 必然重查稳定区块。修订为：`StatsStatusDto` 只携带 `LiveStatusDto`（实时数字 + 同时刻比较，**不含摘要**）；`StatusDto.summary` 只存在于 `StatsHomeDto.status`。前端状态卡 = 实时部分用 `live.status`，摘要用最近一次 `home.status.summary`。
2. **`StatsStatusDto` 携带报告日期**（P0-2）：响应含 `localDate` + `reportingTimeZoneId`。每次轮询后 `status.localDate !== home.localDate` → 触发 `stats_get_home(days)`，页面保持可见跨午夜也能自动换日。
3. **home/status 双通道 generation**（P0-3）：home 按 `generation + requestedDays + requestedLocalDate` 判定，status 按独立 `statusGeneration` 判定。普通 5 秒轮询**不得**废弃正在执行的主页查询；仅跨日显式双失效。
4. **惯性有效日来源**（P0-4）：`effectiveDays` 来自 `daily_work_metrics`（存在当日工作统计投影的日期），**不是** `COUNT(DISTINCT local_date) FROM hourly_app_usage`（小时表行只在有 segment 的小时存在，两表不等价——已用 recompute.rs 投影逻辑核对）。
5. **`compare_direction` 增加比较策略**（P0-5）：`ComparisonPolicy::DirectBaseline`（昨日/上周同期：缺失 → NoData）与 `HistoricalAverage { min_samples }`（近 7 日：不足 → InsufficientSamples）；百分比用 `i128` 中间值 + 统一四舍五入函数。
6. **DST 缺失/重复时间规则**（P0-6）：不采用"钳制到日末"。缺失时间 → 缺口后第一个合法时刻；重复时间 → 优先与当前 UTC offset 相同的实例，否则较早实例；结果限制在该本地日 UTC 范围内。
7. **命令级读快照**（Q-1）与 **cutoff 批次查询**（Q-2）：一次命令 = 一个 Reader + 一个读事务快照；`recent_recorded_dates` 一次取日期 + VALUES CTE 一次聚合全部 cutoff。
8. **`workBlockCount` 权威来源**（L-1）：今日工作块数 = 从 `work_blocks` 按 `recompute_dates` 同一计数规则在 `[day_start, cutoff]` 计算（含未闭合块），不在统计主页重新定义口径。
9. **前端模型**（F-1~F-4）：`ready` 直接由首次 home 派生 `live`；渲染选择器合并 `live.todayTrendPoint` 覆盖今日柱、`live.weekProgress.currentActiveMs` 覆盖当前周柱；范围切换失败保留旧图走 `refreshState: 'error'`；`upFromZero` 新增时长由 `mapDirectionDisplay(comparison, currentActiveMs)` 用当前值表达（采纳方案 B，不加 DTO 字段）。
10. **模块归属**（五）：纯函数全部入 `crates/wuji-core/src/stats.rs`；依赖 `wuji-storage` 行类型（不在 wuji-core）的槽位分配与构成桶组装留在 desktop query 组装层，不引入跨 crate 公共行类型。

### 接口级修订（v3，同版收口）

框架不变。以下接口级阻塞点在开工前一并冻结（已落实到各阶段，详见对应章节）：

1. **均线输入类型**：`compute_moving_avg7` 改用 `wuji-core` 纯输入 `DailyMetricSample`（activeDurationMs/hasData/isToday），不引用 Reader 私有 `DayMetric`（阶段一 1.2）；
2. **快照结构**：改为 `ReaderSnapshot` 包装（`unchecked_transaction` 独占借用 `Reader.conn`），统计投影原语全部在快照上执行；原"事务 + `&Reader` 回调"写法存在借用冲突无法编译，弃用（阶段二 2.3、阶段三 3.1）；
3. **cutoff 零活动日**：VALUES CTE 用 **LEFT JOIN**，零活动日期返回 0（有效基线）而非缺失；按输入 dates 补齐、顺序确定；活动时长与工作块各一条批量 SQL（允许两条、禁止逐日期）（阶段二 2.3）；
4. **摘要窗口数据充足**：`stats_home` 统一读取 daily 超集 `read_days = max(days + 6, 15)`（含今日）；摘要恒用 `[today-14, today-1]` 两个 7 日窗口，与 `days` 切换无关（阶段三 3.1）；
5. **精确阈值判定**：5%/10% 方向与摘要档位用精确比例（i128 交叉相乘），`deltaPercent` 四舍五入只用于显示（阶段一 1.2）；
6. **周进度公式**：仅"今日"与"上周同周序日"用 cutoff，其余周序日取完整 `daily_work_metrics` 值（阶段三 3.1 stats_weekly）。

---

## 阶段一：数据合同（DTO + 枚举 + 纯函数 + specta 生成）

所有"口径在 Rust"的合同层工件一次性完成，后续阶段只做 SQL 投影和组装。

### 1.1 DTO 与枚举（`crates/wuji-core/src/dto.rs`）

**约束**：全部 DTO 统一 derive `PartialEq, Eq`；所有比例字段整数表达，不引入 `f64`。

新增类型严格按设计 §5.3 + 阶段零修订，完整清单：

```
ComparisonDirection   { Up, Down, Stable, UpFromZero, Unavailable }
UnavailableReason    { NoData, InsufficientSamples }
SummaryDirection     { Up, UpSlight, Flat, DownSlight, Down }
PeriodKind           { Morning, Afternoon, Evening, Night }
ReliabilityKind      { Preliminary, Normal }
BucketKind           { Day, Week }

SameTimeComparisonDto  { activeDurationMs: Option<Int64String>, deltaPercent: Option<i32>,
                          direction: ComparisonDirection, sampleDays: i32,
                          unavailableReason: Option<UnavailableReason> }
LiveStatusDto          { todayActiveMs, workBlockCount, cutoffLocalTime,
                          yesterdaySame: SameTimeComparisonDto,
                          last7AvgSame: SameTimeComparisonDto }
// 轻量轮询载荷；不含摘要。摘要只在 StatsHomeDto.status 中返回。
StatusDto              { todayActiveMs, workBlockCount, cutoffLocalTime,
                          yesterdaySame: SameTimeComparisonDto,
                          last7AvgSame: SameTimeComparisonDto, summary: SummaryDto }
// 保持设计 §5.3 原形（实时五字段 + summary），仅用于 StatsHomeDto；
// stats_get_status 不返回本类型，其实时部分以 LiveStatusDto 承载（liveStatus）
SummaryDto             { direction: Option<SummaryDirection>, primaryPeriod: Option<PeriodKind> }
TrendPointDto          { localDate, activeDurationMs, workBlockCount, hasData, isToday,
                          movingAvg7ActiveMs: Option<Int64String>, movingAvg7SampleDays: i32 }
WeeklyPointDto         { weekStartDate, activeDurationMs, isCurrentWeek,
                          completedRecordedDays: i32,
                          currentWeekDailyAvgMs: Option<Int64String> }
WeekProgressDto        { currentActiveMs, lastWeekSame: SameTimeComparisonDto,
                          recordedDays: i32, cutoffLocalTime }
TopEntryDto            { app: AppDto, activeDurationMs }
CompositionBucketDto   { startDate, endDate, bucketKind: BucketKind, isCurrent: bool,
                          hasData: bool,
                          apps: Vec<TopEntryDto>, othersActiveMs }
// hasData：日桶 = 该自然日是否存在当日工作统计投影（daily_work_metrics 有行）；
// 30 天周桶 = 桶内至少一个自然日存在 daily_work_metrics 行。
// 与趋势缺数据语义一致："当日无记录数据"与"当日有记录但应用活跃为 0"可区分。
// 7/14 天日桶按完整日期骨架每个自然日一个桶；30 天周桶按 ISO 周。
AppPaletteEntryDto     { app: AppDto, slot: u32 }
// slot 构造器保证 0..3（allocate_slots 内断言/截断），DTO 保持 u32 不加枚举；
// 前端对非法槽位回退到 --chart-other（纵深防御）。
HourlyPointDto         { localHour: u32, avgActiveMs: Int64String }
InertiaDto             { startHour: Option<i32>, peakHour: Option<i32>,
                          endHour: Option<i32>, lunchLowestHour: Option<i32>,
                          effectiveDays: i32, totalDays: i32,
                          reliability: Option<ReliabilityKind> }
MilestoneDto           { totalRecordedDays: Int64String,
                          longestConsecutiveDays: Int64String,
                          firstRecordedMonth: Option<String> }
MonthlyPointDto        { month, activeDurationMs, recordedDays: i32,
                          isCurrentMonth: bool,
                          avgActiveMsPerRecordedDay: Option<Int64String> }

StatsHomeDto           { hasAnyData: bool, localDate, reportingTimeZoneId,
                          status: StatusDto, trend, weekly, weekProgress,
                          composition, palette, hourlyProfile, inertia,
                          milestone, monthly }
StatsStatusDto         { localDate, reportingTimeZoneId,
                          liveStatus: LiveStatusDto, weekProgress: WeekProgressDto,
                          todayTrendPoint: TrendPointDto }
```

### 1.2 纯函数（`crates/wuji-core/src/stats.rs` 新模块）

**模块归属已定死**：以下纯函数不依赖数据库、不依赖 storage 行类型，全部放 `wuji-core`，可独立编写和测试：

**整数百分比**（先乘 100 可能溢出、先除会截断；统一 `i128` 中间值）：

```rust
fn rounded_percent_delta(current_ms: i64, baseline_ms: i64) -> i32
// (current - baseline) * 100 / baseline 的 i128 四舍五入；
// 结果超出 i32 可表示范围时钳制到 i32::MAX / i32::MIN（百分比只用于文案与阈值，
// 大基数下钳制不会改变五态判定——判定只关心 >5%、≤5% 与符号）。
```

**比较方向判定**（设计 §4.1 统一规则 + 阶段零修订）：

```rust
enum ComparisonPolicy {
    DirectBaseline,                    // 昨日、上周同期：缺失 → NoData
    HistoricalAverage { min_samples: i32 },  // 近 7 日：不足 → InsufficientSamples
}

fn compare_direction(current_ms: i64, baseline_ms: Option<i64>, sample_days: i32,
                     policy: ComparisonPolicy)
  -> (ComparisonDirection, Option<i32>, Option<UnavailableReason>)
```

- 不可用原因**由策略决定**，不依赖参数判断顺序：`DirectBaseline` 且基线为 None → `Unavailable + NoData`；`HistoricalAverage` 且 `sampleDays < min_samples` → `Unavailable + InsufficientSamples`（基线为 None 且场景是近 7 日均值时同样归因 `InsufficientSamples`）；
- 基线 = 0 且 current = 0 → Stable, deltaPercent = 0
- 基线 = 0 且 current > 0 → UpFromZero, deltaPercent = null
- 基线 > 0：**方向判定用精确比例，四舍五入只用于显示**（设计 §4.1：实际变化绝对值 > 5% 即 Up/Down，恰 = 5% 仍 Stable）：
  - `exceeds = diff.abs() * 100 > baseline * 5`（diff / baseline 均为 `i128` 交叉相乘，避免浮点与先除截断）；是则 Up/Down，否则 Stable；
  - `deltaPercent = rounded_percent_delta(current, baseline)` 只用于文案显示，**不得用四舍五入后的整数百分比做阈值判断**（如实际 +5.4% → 显示 +5% 仍必须判 Up）

**摘要方向**（设计 §5.3 SummaryDto.direction）：

```rust
fn summary_direction(recent_avg: Option<i64>, prior_avg: Option<i64>)
  -> Option<SummaryDirection>
```

- 任一窗口有效日 < 3 → None；零基线适用 §4.1；
- 阈值判定同样用**精确比例**（i128 交叉相乘，prior > 0 时，`delta = recent - prior`）：`delta*100 > prior*10` → Up；`prior*5 < delta*100 ≤ prior*10` → UpSlight；`|delta|*100 ≤ prior*5` → Flat；对称 Down/DownSlight——不用四舍五入的整数百分比判档（与比较方向一致）

**时段映射**：

```rust
fn period_of_hour(hour: u32) -> PeriodKind  // [6,12)m, [12,18)a, [18,24)e, [0,6)n
```

**摘要组装**：

```rust
fn build_summary(direction: Option<SummaryDirection>, peak_hour: Option<u32>,
                 reliability: Option<ReliabilityKind>)
  -> SummaryDto
```

- reliability 为 null 或全零曲线 → primaryPeriod = null

**均线计算**（纯计算输入类型在 wuji-core 定义，不依赖 storage 行类型）：

```rust
pub struct DailyMetricSample {
    pub active_duration_ms: i64,
    pub has_data: bool,
    pub is_today: bool,
}
// 纯函数输入（非 DTO，无需 specta/serde derive）；
// Query/组装层把 ReaderSnapshot 的 DayMetric 映射为 DailyMetricSample 后再调用。

pub fn compute_moving_avg7(points: &[DailyMetricSample], idx: usize) -> (Option<i64>, i32)
```

- 从 idx 向前 7 个自然日窗口；仅完整历史日（hasData=true 且 !isToday）；有效点 < 3 → (null, count)

**惯性派生**（设计 §4.4 + §9 P0-8）：

```rust
fn derive_inertia(profile: &[i64; 24], effective_days: u32) -> InertiaDto
```

- **reliability 为 null（有效日 < 3）时派生字段同样全部 null**（设计 §4.4/§9 P0-8）——即使曲线非零；
- 24 小时全零 → 所有派生字段 null
- peakHour = argmax（并列取最早）
- startHour = 首个 fraction ≥ 30%×peak（并列取最早）
- endHour = 最后一个 fraction ≥ 30%×peak（并列取最晚）
- lunchLowestHour = **候选小时写死为 {12, 13, 14} 三个整点**（比较边界是 11 点与 15 点均值），区间内均值最小且**严格低于** 11 点与 15 点均值（否则 null；并列取最早）
- reliability: <3 → null; 3-6 → Preliminary; ≥7 → Normal

**连续天数**：

```rust
fn longest_consecutive(dates: &[String]) -> i64
```

**归一化**：

```rust
fn normalize_days(days: Option<i32>) -> u32  // 7|14|30 → 7|14|30；缺失/非法 → 14
```

**留在 desktop 组装层（`apps/desktop/src-tauri/src/stats_assembly.rs`，不进 wuji-core）**：

- `allocate_slots(totals: &[AppTotalRow], top_n: u32) -> Vec<AppPaletteEntryDto>`
- `bucketize_composition(app_rows: &[AppDayRow], palette, days, today) -> Vec<CompositionBucketDto>`

原因：`AppTotalRow`/`AppDayRow` 是 `wuji-storage` 导出的组装层行类型（不在 wuji-core），`wuji-core` 无法直接使用；为避免为纯函数测试引入跨 crate 公共行类型，槽位分配与 ISO 周桶组装属于组装层（仍在 Rust，满足"槽位分配在 Rust 计算"）。

### 1.3 specta 生成

在 `bindings.rs` 的 `type_collection()` 中注册所有新 DTO + 枚举。运行 `WUJI_UPDATE_BINDINGS=1 cargo test -p wuji-core bindings` 重新生成双副本。

### 阶段一 Definition of Done

- [X] `cargo fmt --check` + `cargo clippy -- -D warnings` 通过
- [X] 所有纯函数有独立单元测试（不依赖 DB）：比较五态、**ComparisonPolicy 两场景（DirectBaseline 缺失 → NoData；HistoricalAverage 样本不足 → InsufficientSamples，且参数同时满足两种判断时不依赖判断顺序）**、**方向阈值与显示舍入独立（+5.4% 判 Up 且显示 +5%；恰 5% 判 Stable）**、**摘要 5%/10% 精确分档（边界：恰 5% → Flat、恰 10% → UpSlight）**、**`rounded_percent_delta`（正负数四舍五入、`i64` 边界、5% 阈值附近、`i32` 溢出钳制）**、零基线、惯性全零、**午休候选 {12,13,14} 写死**、均线有效点 < 3 等边界（slot 排名/固定测试属于 desktop 组装层，见阶段三 DoD）
- [X] `cargo test -p wuji-core` 通过（含 bindings drift 测试）
- [X] TypeScript 双副本逐字节一致

---

## 阶段二：Storage Reader（`crates/wuji-storage`）

SQL 投影层。新增 7 个统计投影原语，**实现于 `ReaderSnapshot`（读事务快照包装）上**：快照保证一次命令内全部子查询同连接同读事务（阶段三 3.1 契约）；投影原语本身以 `&self` 执行于快照事务内，不得在快照外自行开连接。

```rust
// reader.rs：ReaderSnapshot 持有独占借用的读事务（快照存活期间 Reader 本体不可再借用）
pub struct ReaderSnapshot<'a> {
    tx: rusqlite::Transaction<'a>,   // 独占借用 Reader.conn
    meta: &'a SchemaMeta,            // 共享借用 reporting_time_zone_id
}
```

### 2.1 同时刻截断辅助

```rust
fn same_moment_cutoff_utc_ms(tz: &Tz, date: &LocalDate, now_utc_ms: i64, today: &LocalDate)
  -> Result<i64>
```

- 今日：返回 now_utc_ms
- 历史日：取 now 的本地墙钟时间（**保留完整秒与毫秒，不得在换算前截断到分钟**——UI 只在展示时格式化为 HH:MM；同一墙钟时刻的比较必须精确到当前秒/毫秒），转换到目标本地日（DST 感知）：
  1. **正常唯一时间**：直接转换；
  2. **不存在时间**（spring-forward 缺口，如 `02:30` 不存在）：移动到缺口后的**第一个合法时刻**（不得钳制到日末）；
  3. **重复时间**（fall-back，如 `01:30` 对应两个 UTC 时刻）：优先选择与 now 的 UTC offset 相同的实例；无法匹配则取较早实例；
  4. 最终结果**限制在该本地日 UTC 范围** `[local_day_range_utc_ms]` 内。
- 沿用 `timeutil.rs` 保守原则：本地午夜被跳过等无法换算的极端日显式报错，不静默改算。

### 2.2 行类型（`wuji-storage` 导出）

> 实现纠错（与阶段一修 `derive_inertia` 条文同类）：合同原稿写"reader.rs 内私有"，但 1.2 已将组装层定在 desktop（跨 crate），私有类型无法被消费——故为 `pub` 并经 lib.rs 再导出；仍在 storage 层，不进 wuji-core、不参与 specta。

```rust
pub struct DayMetric { local_date: String, active_duration_ms: i64, work_block_count: i64, has_data: bool }
pub struct DayAtCutoff { local_date: String, active_duration_ms: i64, work_block_count: i64 }
pub struct AppTotalRow { app_id: i64, display_name: String, total_active_ms: i64 }
pub struct AppDayRow { local_date: String, app_id: i64, display_name: String, active_ms: i64 }
```

### 2.3 七个投影原语（`ReaderSnapshot` 方法，均为 `&self`，执行于快照事务内）

Query/组装层将 `DayMetric` 等行类型映射为 `wuji-core` 的纯函数输入（如 `DailyMetricSample`，阶段一 1.2）后再调用纯函数。

**`stats_daily_rows(&self, start, end) -> Vec<DayMetric>`**（映射为 `DailyMetricSample` 供 `compute_moving_avg7`）

- 先生成 `[start, end]` 的**完整本地日期序列**（日期骨架），再 `SELECT local_date, active_duration_ms, work_block_count FROM daily_work_metrics WHERE local_date BETWEEN ?1 AND ?2` 映射进去；
- SQL 无行的日期生成零值 `DayMetric { has_data: false }`——趋势数组长度**恒等** 7/14/30，不依赖数据库是否存在该日行；
- 供趋势、周度、摘要窗口、月度、里程碑复用。

**`recent_recorded_dates(&self, before: &LocalDate, limit: usize) -> Vec<LocalDate>`**

- `SELECT local_date FROM daily_work_metrics WHERE local_date < ?1 ORDER BY local_date DESC LIMIT ?2` 后反转为升序；
- "从昨日向前寻找最近 limit 个 `hasData=true` 的历史日"——**不是固定 lookback**：最近两周只记录 4 天就返回 4 个（调用方据此判定 `sampleDays < 3`），不擅自补 7。

**`stats_cutoff_series(&self, tz, today, now_utc_ms, dates: &[LocalDate]) -> Vec<DayAtCutoff>`**

- 输入为调用方确定的日期集合（今日、昨日、近 7 个有效日、上周同期），**不再接受 `lookback_days` 自然日窗口**；
- 以 `same_moment_cutoff_utc_ms` 计算每个日期的截止点后，用**一条 VALUES CTE 区间相交查询**完成全部日期的截断聚合（约 ≤ 10 个日期 × 3 参数，远低于 SQLite 绑定上限）。**必须 LEFT JOIN**：cutoff 日期没有任何相交 segment（昨日同期无活动、历史日截至当前时刻活跃为 0、今日刚开始）时仍返回该行并 COALESCE 为 0——零活动是有效基线，不能当成"无基线"丢弃：

```sql
WITH cuts(local_date, day_start, cutoff) AS (VALUES (?,?,?), (?,?,?), ...)
SELECT c.local_date,
       COALESCE(SUM(CASE WHEN s.activity_state = 'active'
                     THEN MIN(s.end_at_utc_ms, c.cutoff) - MAX(s.start_at_utc_ms, c.day_start)
                     ELSE 0 END), 0) AS active_duration_ms
FROM cuts c
LEFT JOIN activity_segments s
  ON s.end_at_utc_ms > c.day_start AND s.start_at_utc_ms < c.cutoff
  AND s.end_at_utc_ms > s.start_at_utc_ms
GROUP BY c.local_date
ORDER BY c.local_date
```

- Rust 侧按输入 `dates` 补齐并保持确定性顺序（LEFT JOIN 已保证每个输入日期出现，仍显式核对一遍）；
- **不逐日发 SQL**：5 秒轮询不得产生昨日 1 次 + 近 7 有效日 ≥ 7 次 + 上周若干次 + 今日 1 次的 N+1。"无 N+1"不要求本方法只执行一条 SQL——活动时长一条批量、工作块另一条批量，但不得逐日期查询；
- 今日的 `workBlockCount` 从同一快照的 `work_blocks` 批量计数（与 `recompute_dates` 完全一致的口径：块与 `[day_start, cutoff]` 相交且 `day_active > 0` 才计 1，**含当前未闭合块**）——复用现有工作块投影的权威查询，不在统计主页重新定义；
- 供 status、摘要窗口复用；**weekProgress 只对"今日"与"上周同周序日"使用本方法**（其余周序日取完整日值，见阶段三 3.1 stats_weekly 公式）。

**`stats_app_totals(&self, start, end) -> Vec<AppTotalRow>`**
`SELECT u.app_id, a.display_name, SUM(u.active_duration_ms) FROM daily_app_usage u JOIN app_identities a ON a.app_id = u.app_id WHERE u.local_date BETWEEN ?1 AND ?2 GROUP BY u.app_id ORDER BY SUM(u.active_duration_ms) DESC, u.app_id ASC`。

- 应用身份解析**复用现有 `AppDto` 身份路径**（与 `reader.today()` 相同的 `JOIN app_identities` + `AppDto` 组装，可提取共享 helper），不得另建身份规则导致与 Today/Apps 页展示不一致。

**`stats_app_rows(&self, start, end) -> Vec<AppDayRow>`**
逐日逐应用行，供 7/14 天日桶和 30 天周桶聚合；身份解析同上。

**`stats_hourly_profile(&self, start, end) -> ([i64; 24], u32)`**

- **有效日来源是 `daily_work_metrics`**（阶段零 P0-4）：

```sql
SELECT local_date FROM daily_work_metrics
WHERE local_date >= ?1 AND local_date <= ?2   -- 有效日集合
```

```sql
SELECT local_date, local_hour, SUM(active_duration_ms)
FROM hourly_app_usage
WHERE local_date >= ?1 AND local_date <= ?2
GROUP BY local_date, local_hour               -- 小时总量
```

- Rust 侧：为每个有效日建立 24 个零值 → 覆盖已有小时数据 → 对所有有效日**统一**以 `effectiveDays` 求平均（设计 §4.4 统一分母规则）；
- 不得用 `COUNT(DISTINCT local_date) FROM hourly_app_usage` 当 `effectiveDays`——某日有工作统计投影但小时表因零活动/无 segment 行而无行时，该日必须计入分母（否则均值被错误放大）。

**`stats_recorded_dates(&self) -> Vec<String>`**
`SELECT DISTINCT local_date FROM daily_work_metrics ORDER BY local_date`（里程碑连续天数输入）。

### 阶段二 Definition of Done（2026-08-03 已通过实施评审收口）

- [X] 每个原语有确定性测试（使用 `bootstrap_with_timezone` + segment + recompute 种子数据）：
  - `same_moment_cutoff_utc_ms` **三类 DST 测试**：普通日期、spring-forward 不存在时间（取缺口后第一个合法时刻）、fall-back 重复时间（优先同 offset 实例，否则较早实例——含 Paris 1910 LMT 对 2026 回拨日 02:xx 歧义窗口的"无法匹配取较早"用例，先断言 `from_local_datetime` 为 Ambiguous 防假通过）+ 结果钳制在当日 UTC 范围内；**墙钟秒/毫秒精度保留**；今日分支校验 now 落在目标本地日范围内（不一致显式报错）；
  - `stats_daily_rows` 日期骨架：缺行日期 `has_data=false`，长度恒等 7/14/30；范围护栏 366 天；
  - `recent_recorded_dates` 前向寻日：近两周只记录 4 天 → 返回 4 个而非补足 7；
  - `stats_cutoff_series` 单批次：**LEFT JOIN 下零活动日期返回 0 而非缺失**（含"昨日有有效日期但同期无活动"、"今日刚开始"）；结果按输入 dates 补齐且顺序确定；**重复日期按首次出现去重查询、按原始输入映射（不翻倍不归零）**；今日 workBlockCount 含未闭合块、按 recompute 口径；**工作块内 Segment 限定与 `[day_start, cutoff)` 相交**（跨午夜块前一日 Segment、cutoff 后 Segment 不得产生负贡献抵消——P1 回归）；
  - `stats_hourly_profile` 有效日来源：仅有 daily_work_metrics 行而无 hourly_app_usage 行的日期计入分母；每日 24 小时补齐 0；
  - **快照契约**：全部子查询在同一读事务内执行（编译期由 `ReaderSnapshot` 独占借用 `Reader.conn` 保证）；**WAL 写并发下同一快照内跨 writer 提交仍读一致视图**（运行时验证）；
  - （slot 排名 tie-break 由 Reader `stats_app_totals` 的 `ORDER BY SUM DESC, app_id ASC` 排序承载（阶段二 2.3 合同），组装层继承顺序并截断——见阶段三 stats_assembly 测试；ISO 周聚合属组装层）
- [X] `cargo test -p wuji-storage` 通过（49 项：timeutil 11 + stats_reader 14〔含阶段三 cutoff 计数器回归 1〕+ 既有 24；另有 `compile_fail` doctest 1）

> 实施纠错记录：工作块计数 SQL（`stats_cutoff_series` 与 `recompute_dates.block_stmt` 两处）
> 原未限定 Segment 与 `[day_start, cutoff)` 相交，跨午夜块/历史 cutoff 后 Segment 会产生
> 负贡献抵消有效活动导致漏计——已统一加相交条件并补三类回归测试（跨午夜、cutoff 后、
> recompute 日投影），与 `work_active` 查询口径一致。

---

## 阶段三：QueryService + 命令注册

### 3.1 QueryService（`query.rs`）——命令级快照

**一次命令 = 一个 Reader + 一个 `ReaderSnapshot` 读事务快照**（阶段零 Q-1），不再"每个方法打开 Reader"。快照结构必须**可编译**（阶段零 v3，沿用仓库 `StorageTransaction` 的既有模式）：

```rust
// reader.rs
impl Reader {
    pub fn with_snapshot<T>(
        &mut self,
        f: impl FnOnce(&ReaderSnapshot<'_>) -> Result<T>,
    ) -> Result<T> {
        let tx = self.conn.unchecked_transaction()?;   // 需要 &mut Connection
        let snapshot = ReaderSnapshot { tx, meta: &self.meta };
        let result = f(&snapshot);
        if result.is_ok() {
            snapshot.tx.commit()?;                     // Err 时 Transaction Drop 自动 ROLLBACK
        }
        result
    }
}
```

- **子查询必须通过快照执行**：全部统计投影原语实现在 `ReaderSnapshot` 上（阶段二 2.3），以 `&self` 执行于同一读事务；类型层面杜绝"事务开着却绕过事务直查 `Reader.conn`"的路径；
- 不采用"事务对象 + 回调 `&Reader`"写法：`unchecked_transaction` 的 `&mut Connection` 与回调内的 `&Reader`（含同一 `Connection`）存在借用冲突，无法通过编译（阶段零 v3 弃用项）。

```rust
struct StatsQueryContext<'a> {
    snapshot: &'a ReaderSnapshot<'a>,
    now_utc_ms: i64,
    local_date: LocalDate,
    reporting_tz: Tz,
}
```

- `stats_home` / `stats_status` 各自：打开一个 Reader → 校验时区 → 计算 `now_utc_ms`/`local_date` → `with_snapshot` 内构建 `StatsQueryContext` → 完成全部子查询。同一命令的所有区块来自**同一数据库视图**（状态卡不会比今日趋势柱多几分钟、周进度与周柱总量同一投影时点）。

**统一 daily 读取超集（保证摘要窗口与 `days` 无关）**：`stats_home` 用一次 `stats_daily_rows` 读取 `[today - read_days + 1, today]`，其中 `read_days = max(days + 6, 15)`（含今日的自然日数）：

- `days=7` → 15 天 `[today-14, today]`：均线 lookback（可见历史日往前 6 天）与摘要窗口 `[today-14, today-1]` 全部落在超集内；`days=14` → 20 天；`days=30` → 36 天；
- 趋势取超集尾部 `days` 个可见点（均线用超集内 lookback，不再自行读取）；
- **摘要固定使用 `[today-14, today-8]`（前 7 日窗口）与 `[today-7, today-1]`（近 7 日窗口），均不含今日**；`build_summary` 从超集派生，不额外查询，**7/14/30 切换下摘要不变**。

新增方法（`ctx: &StatsQueryContext`，全部在快照内执行）：

| 方法                             | 返回                                                     | 说明                                                                                                                          |
| -------------------------------- | -------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------- |
| `stats_live_status_dto(ctx)`   | `LiveStatusDto`                                        | 批次 cutoff（今日/昨日/近 7 有效日）+ 比较方向判定；**不含摘要**（阶段零 P0-1）                                         |
| `build_summary(ctx)`           | `SummaryDto`                                           | 从统一 daily 超集取`[today-14, today-1]` 两个 7 日窗口 + 惯性结果派生，**不额外查询**；仅 home 使用，与 `days` 无关 |
| `stats_trend(ctx, days)`       | `Vec<TrendPointDto>`                                   | 从统一超集取尾部`days` 个点 + 均线（超集内 lookback），裁剪返回 `days`                                                    |
| `stats_weekly(ctx)`            | `(Vec<WeeklyPointDto>, WeekProgressDto)`               | 12 ISO 周 + 本周进度                                                                                                          |
| `stats_composition(ctx, days)` | `(Vec<CompositionBucketDto>, Vec<AppPaletteEntryDto>)` | 槽位分配 + 桶聚合（日/周）；日桶按完整日期骨架，`hasData` 如实表达                                                          |
| `stats_hourly_profile(ctx)`    | `(Vec<HourlyPointDto>, InertiaDto)`                    | 惯性窗口`[today-14, today-1]` + 统一分母 + 惯性派生                                                                         |
| `stats_monthly(ctx)`           | `(Vec<MonthlyPointDto>, MilestoneDto)`                 | 最近 6 个日历月 + 里程碑                                                                                                      |

**`stats_weekly` 周进度公式（本周截至当前 / 上周同期）**：只有"今日"与"上周同周序日"使用 cutoff（`stats_cutoff_series`），其余更早周序日取**完整 `daily_work_metrics` 值**，不得把周一/周二也截断到当前时刻。例（今日 = 本周三 15:20）：

```text
本周截至当前 = 本周一完整值 + 本周二完整值 + 本周三截至 15:20
上周同期     = 上周一完整值 + 上周二完整值 + 上周三截至 15:20
```

**desktop 组装层（`stats_assembly.rs`）**：`allocate_slots`（AppTotalRow → palette：按 Reader 已排序输入分配 0..top_n 槽位、**tie-break 由 Reader 排序承载、组装层继承**、slot < 3 构造保证）与 `bucketize_composition`（AppDayRow → 日/周桶：完整日期骨架、ISO 周聚合、hasData）在此实现并单独测试（归属见阶段一 1.2；等值总量 tie-break 输入顺序继承有专门用例）。

**顶层组装：**

```rust
pub fn stats_home(&self, days: u32) -> Result<StatsHomeDto, SafeError> {
    // 打开一个 Reader → with_snapshot 内调用全部方法，单次 now_utc_ms 保证时间基准一致
    // 且同一读快照保证数据库视图一致
    // status = StatusDto { 实时五字段（来自 stats_live_status_dto(ctx)）, summary: build_summary(ctx) }
    // hasAnyData = (milestone.totalRecordedDays > 0)
}

pub fn stats_status(&self) -> Result<StatsStatusDto, SafeError> {
    // 打开一个 Reader → with_snapshot 内：liveStatus + weekProgress + todayTrendPoint
    // 只走 cutoff 批次与本周/上周同期查询，不触发趋势/惯性/月度/里程碑
}
```

> 实施纠错记录（实现收口）：
>
> 1. **今日趋势柱在 Rust 侧以 live cutoff 覆盖**（`stats_home` 内对 trend 的 isToday 点写
>    live 截止活跃/块数）——阶段五 F-4 前端渲染选择器合并仍保留（轮询后由 `live.todayTrendPoint`
>    覆盖），Rust 侧覆盖只保证首次渲染即与状态卡同口径（消除投影时点差），两处取值一致；
> 2. **命令级单批次 cutoff**（P1 收口）：`stats_home`/`stats_status` 各自在命令开始时
>    `build_cutoff_plan` 统一收集今日/昨日/近 7 有效日/上周同周序日，**只调用一次
>    `stats_cutoff_series`** 建索引，live、周进度、月度、今日趋势点共用——5 秒轮询不再产生
>    4 次 cutoff（8 条 SQL）；`ReaderSnapshot` 内置快照级计数钩子
>    （`stats_cutoff_series_calls`）；观测接口在全部子查询完成后，将最终计数与同一快照的
>    DTO 原子返回，不使用跨命令共享状态；e2e 断言每个命令恰 1 次，并以 0→1→2 单测
>    证明第二次调用可被观测——单批次有运行时回归门禁；
> 3. **`stats_status` 的 `todayTrendPoint` 需 1 次 7 日轻量 daily 读**（今日 hasData 与
>    MA sampleDays 从同一 `[today-6, today]` 骨架取得，cutoff 值来自命令级批次）——仍不
>    触发趋势/惯性/月度/里程碑，属轻量范围；
> 4. **固定时钟注入**（P1 收口）：新增公开 `stats_home_at`/`stats_status_at(now)`（生产包装
>    `stats_home`/`stats_status` 走真实 now），阶段三测试以固定 UTC 时刻确定性播种，精确断言
>    无容差、无跨午夜错位。

### 3.2 命令（`commands.rs` + `lib.rs`）

```rust
#[tauri::command]
fn stats_get_home(services: State<'_, AppServices>, days: Option<i32>)
  -> Result<StatsHomeDto, SafeError>   // 与既有命令一致：必须返回 Result，错误路径走 SafeError 合同

#[tauri::command]
fn stats_get_status(services: State<'_, AppServices>)
  -> Result<StatsStatusDto, SafeError>
```

`invoke_handler` 增加两个命令（15 → 17）。

### 阶段三 Definition of Done

- [X] `stats_home` / `stats_status` 端到端集成测试（种子 DB → 全 DTO 断言）：五态、零基线、均线 lookback、惯性统一分母（含"有工作统计投影但小时表无行"的日期计入分母）、weekProgress 常返、月度当前月口径、hasAnyData 边界、**stats_status 响应含 localDate/reportingTimeZoneId 且不含 summary**、**今日 workBlockCount 与 recompute 口径一致**、**cutoff 批次查询（`ReaderSnapshot` 快照级计数钩子在全部子查询后与同快照 DTO 原子返回；e2e 断言一次命令恰 1 次 `stats_cutoff_series`，0→1→2 单测证明第二次调用可观测；LEFT JOIN 下零活动日返回 0 基线）**、**摘要与 days 切换无关（7/14/30 下 SummaryDto 一致，窗口恒为 [today-14, today-1]，统一超集 read_days = max(days+6, 15) 覆盖）**、**周进度公式（仅今日与上周同周序日 cutoff，其余周序日完整值）**、**stats_assembly：slot 排名 tie-break、slot < 3 保证、ISO 周聚合、日桶完整骨架与 hasData**、**编译性验证：with_snapshot 内 Reader 本体不可再借用（快照存活期外任何 `&reader` 调用在编译期报错）**
- [X] `cargo test --workspace`（含 host_integration）通过（411 项，0 失败）
- [X] `cargo clippy --workspace --all-targets -- -D warnings` 通过

---

## 阶段四：前端静态布局

**先不接真实命令**——用 TypeScript fixture 完成全部视觉和可访问性，确保在任何数据变化前页面结构就是正确的。

### 4.1 基础设施

- `bridge/client.ts`：`statsGetHome` + `statsGetStatus` 签名（暂不调用）
- `App.tsx`：`/ → StatsPage`，`/today → TodayPage`
- `AppLayout.tsx`：导航新增"主页"为第一项
- `tokens.css`：`--chart-app-1/2/3`、`--chart-other`、`--chart-in-progress`、`--chart-no-data`、`--chart-ref-line`、`--chart-ma-line`（三主题块）
- `StatsPage.css`：页面布局、双列、进行中斜纹（SVG pattern）、虚框参考线、底部轻量区、里程碑条

### 4.2 纯工具模块（`statsModel.ts`）

```ts
// 文案映射（纯函数，不依赖 React）
mapDirectionDisplay(comparison: SameTimeComparisonDto, currentActiveMs: Int64String)
  → { text, showArrow }
// 五态：up/down → "▲▼ +X%"；stable → "基本持平"；
// upFromZero → "新增 N 分钟"，N = formatDeltaMs(currentActiveMs)（基线为 0，当前值即新增量，
//   故采用方案 B：传入当前值，不改 DTO；设计禁止伪造百分比）；
// unavailable → 按 unavailableReason 显示"不显示"或"历史样本不足"
mapSummaryText(summary) → string        // "最近7日日均活跃略有上升；通常主要活跃在上午"
mapPeriodText(period) → string          // morning→"上午"
mapReliabilityText(reliability) → string
slotToToken(slot) → string              // 0→var(--chart-app-1)；非法槽位→var(--chart-other)
isHomeEmpty(stats) → boolean            // !hasAnyData
coverageLabel(trend, days) → string     // "记录覆盖：近14天 12/14天"
formatDeltaMs(ms) → string              // "XhYm"
```

### 4.3 Fixture 数据

一份覆盖以下边界的 `StatsHomeDto` fixture JSON + 一份 `StatsStatusDto` fixture（含 `localDate`/`reportingTimeZoneId`/`liveStatus`，供阶段五轮询与跨日测试）：

- 正常日（hasData=true，有均线）、今日进行中（均线 null）、缺数据日（hasData=false）
- 五态：up（deltaPercent=8）、down（-12）、stable（3% 基本持平）、upFromZero（新增 12 分钟）、unavailable（insufficientSamples）
- 惯性：正常（11/14 effectiveDays）含午休低谷、reliability null（2 天）、全零曲线
- 构成：日桶含 isCurrent、hasData=true/false 桶、周桶含 isCurrent 不完整周
- 月度：isCurrentMonth 当前月、recordedDays=0 月
- hasAnyData=false 空状态

### 4.4 图表组件（纯 CSS/SVG）

| 组件                 | 数据源                                        | 关键视觉                                                                      |
| -------------------- | --------------------------------------------- | ----------------------------------------------------------------------------- |
| `StatusCard`       | `LiveStatusDto` + `home.status.summary`   | 五态文案、摘要双窗口句式、"日均"措辞                                          |
| `TrendChart`       | `Vec<TrendPointDto>` + `days`             | 均线直接渲染 DTO 值（null→断开）；今日柱斜纹；缺数据斜纹+"当日无记录数据"    |
| `WeeklyChart`      | `Vec<WeeklyPointDto>` + `WeekProgressDto` | 当前周进行中；虚框`completedRecordedDays=0` 时隐藏                          |
| `WeekProgressCard` | `WeekProgressDto`                           | lastWeekSame unavailable 时隐藏比较行                                         |
| `InertiaCurve`     | `Vec<HourlyPointDto>` + `InertiaDto`      | SVG 面积图；柱高前端归一化（max→100%）；reliability null→提示；全零→无标注 |
| `AppComposition`   | `Vec<CompositionBucketDto>` + palette       | `isCurrent` → 弱化；槽位→CSS 令牌；日桶横条/周桶纵柱                      |
| `Milestones`       | `MilestoneDto` + `Vec<MonthlyPointDto>`   | firstRecordedMonth null→不显示"自X月起"；当前月进行中；6 根柱固定            |

每个图表组件 props 接口接受其所需的 DTO 子集，不经由 StatsPage 直传顶层 DTO（便于 fixture 测试）。

每个图表 `aria-label` 携带数值；键盘 focus 可达；forced-colors 下使用系统色保留可区分性。

### 阶段四 Definition of Done

- [ ] `pnpm typecheck` + `pnpm lint`（max-warnings 0）通过
- [ ] 所有图表组件 + statsModel 有 fixture 驱动的 Vitest：五态文案（含 upFromZero "新增 N 分钟"）、均线 null 断开、虚框显隐、isCurrent 弱化 class、firstRecordedMonth null 隐藏、hasAnyData=false 整页空状态
- [ ] `pnpm test` 通过
- [ ] 手工：浅色/深色/forced-colors 下 fixture 数据图表可读、键盘导航可达

---

## 阶段五：前端接入

在静态布局验证通过后接真实命令和刷新策略。

### 5.1 双命令刷新

```ts
type ReadyStatsModel = {
  phase: 'ready';
  home: StatsHomeDto;
  live: {
    status: LiveStatusDto;        // 轮询载荷；不含摘要
    weekProgress: WeekProgressDto;
    todayTrendPoint: TrendPointDto;
  };
  days: number;
  homeGeneration: number;
  refreshState: 'idle' | 'refreshing' | 'error';
  refreshError?: SafeError;       // 已有数据时刷新失败的轻量提示，不进入整页 error
};

type StatsModel =
  | { phase: 'loading' }
  | ReadyStatsModel
  | { phase: 'error'; error: SafeError; days: number };  // 仅首次加载失败
```

- **首次 home 成功即进入 ready**（阶段零 F-1）：`live.status` = home.status 的实时部分、`live.weekProgress` = home.weekProgress、`live.todayTrendPoint` = home.trend 中 `isToday` 的点；**不需要再等一次 `stats_get_status`**；
- `useEffect` 首次进入 + `home.localDate` 变化 + days 切换 → `statsGetHome(days)`；
- `usePolling(refreshStatus, 5000, visible)` → `statsGetStatus()`，成功只替换 `live`（阶段零 F-2）；轮询回调拿到 `StatsStatusDto` 响应后先做跨日检查，再拆分应用：
- **渲染选择器合并（不改写 home.trend/home.weekly）**：

```ts
const visibleTrend = home.trend.map(p => (p.isToday ? live.todayTrendPoint : p));
// 当前周柱：live.weekProgress.currentActiveMs 覆盖当前周柱实际高度（实心+今日弱化部分随之更新）
// 状态卡：实时数字与比较用 live.status，摘要用 home.status.summary（最近一次 home 值）
```

- **双通道 generation 防串**（阶段零 F-3/P0-3）：

```ts
const homeGenerationRef = useRef(0);     // home：generation + requestedDays + requestedLocalDate 三键判定
const statusGenerationRef = useRef(0);   // status：statusGeneration 判定
```

- 普通 5 秒轮询**不得**递增 home 通道、不得废弃正在执行的主页查询（用户切 30 天时慢查询不会被轮询顶掉）；
- **跨日显式双失效**：每次状态轮询完成后 `if (resp.localDate !== home.localDate)`（`resp` 为 `StatsStatusDto` 响应，其 `localDate`/`reportingTimeZoneId` 来自报告时区；页面重新聚焦时做同检查）→ `homeGenerationRef++` + `statusGenerationRef++`，并触发 `stats_get_home(days)`——页面保持可见跨过午夜也能自动换日（阶段零 P0-2）；
- **范围切换失败**（阶段零 F-3）：保留旧 home/live 图、`days` 恢复为已生效范围、`refreshState='error'` + 非阻塞提示，**不进入整页 error**；首次加载失败才进 `phase: 'error'`。

### 5.2 StatsPage 容器

```tsx
// hasAnyData=false → 整页空状态
// 否则渲染所有 5 个区块
// stats_get_home 查询期间保留旧 home 数据不闪空（refreshState='refreshing'）
// stats_get_status 失败不影响已渲染的 home 数据
```

### 阶段五 Definition of Done

- [ ] Vitest：首次进入走 home 且直接 ready（不再等待 status）；轮询只走 status 且只替换 live；**status.localDate ≠ home.localDate 自动触发 home 重查（可见跨午夜场景）**；切换范围走 home；**双通道防串（慢 home 不被普通轮询废弃；跨日显式双失效）**；**live.todayTrendPoint 覆盖今日柱、live.weekProgress 覆盖当前周柱**；status 失败保留旧 home；**切换范围失败保留旧图 + refreshState=error + 恢复范围**；upFromZero 用 currentActiveMs 生成"新增 N 分钟"
- [ ] `pnpm typecheck` + `pnpm lint` + `pnpm test` + `pnpm build` 通过

---

## 阶段六：文档 + 全量门禁

### 6.1 文档

| 文件                       | 变更                                                                                                                                                                                                                               |
| -------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `10-统计主页设计定稿.md` | 合同修订同步（实现纠错，阶段零清单）：`StatsStatusDto` 拆 `LiveStatusDto` 并携带 `localDate`/`reportingTimeZoneId`、`CompositionBucketDto.hasData`、`StatusDto` 仅存于 home——修订 §5.3/§5.4 对应段落并追加修订说明 |
| `09-...实施基线.md`      | §8.3 命令表新增`stats_get_home` + `stats_get_status`（17）；§10.6 新增统计主页合同（含修订后 DTO）                                                                                                                           |
| `migration-status.md`    | 新页面行、新查询/命令行、17 命令、更新测试数、合同修订说明                                                                                                                                                                         |
| `CLAUDE.md`              | 命令 15→17；页面增统计主页                                                                                                                                                                                                        |

### 6.2 全量门禁

bindings 门禁**运行两次**（阶段零 P1-5）：先生成，再在无环境变量下验证生成后确实无 drift。

```powershell
cargo fmt --all -- --check
cargo check --workspace --all-targets
cargo clippy --workspace --all-targets -- -D warnings
cargo test --workspace
WUJI_UPDATE_BINDINGS=1 cargo test -p wuji-core bindings   # 生成
cargo test -p wuji-core bindings                          # 无环境变量：验证无 drift
git diff --check
pnpm typecheck && pnpm lint && pnpm test && pnpm build
.\rebuild-package.ps1
```

### 阶段六 Definition of Done

- [ ] 全部门禁通过
- [ ] dev 包烟测通过（含真实断言：capture_state=stopped 且 Observation=0）

---

## 阶段七：真实数据验收

在真实运行环境上验证——fixture 和自动测试只能验证逻辑正确性，阈值体感和边界数据的视觉合理性必须用真实数据判断。

### 7.1 观察清单

| 维度          | 观察点                          | 判断标准                                                                                             |
| ------------- | ------------------------------- | ---------------------------------------------------------------------------------------------------- |
| 均线          | 7 日均线在柱状图上的平滑效果    | 日间噪声被合理平滑，折线不因 1-2 天异常剧烈跳动；有效点 < 3 的断线位置自然                           |
| 惯性          | 24 小时曲线形状与标注           | 开工/高峰/收工标注与人的主观感知一致；午休低谷确实出现在有午休习惯的数据上，没有午休的数据不强行标注 |
| 当前周参考值  | 虚框高度                        | 在已完成 1-6 天时虚框高度合理（不被某一天极端值带偏）；completedRecordedDays=0 时不显示              |
| ±5% 噪声遮蔽 | 昨日/上周同期比较               | 1-2 分钟的随机波动不触发 ▲▼；真实趋势变化（如多工作了 30 分钟）正常显示                            |
| 零基线        | 首次使用某时间段                | "较昨日同期新增 N 分钟"而非伪造的 +∞%                                                               |
| 轮询一致性    | 5s 轮询下状态卡/今日柱/当前周柱 | 三者活跃时长同步增长，不出现状态卡已更新而柱仍停留首次值                                             |
| 跨日          | 页面保持可见跨午夜              | 自动换日重查，趋势/惯性/月度全部切到新日期                                                           |
| 空状态        | 全新安装                        | `hasAnyData=false` → 整页引导文案                                                                 |

### 7.2 手工矩阵

- 浅色 / 深色 / forced-colors 下全部图表可读
- 960×640 / 1280×800 / 100% / 150% / 200% DPI
- 键盘导航可达全部交互元素

### 阶段七 Definition of Done

- [ ] 观察清单全部通过
- [ ] 如有阈值/标注视觉不合理，记录为已知限制或回设计文档调整参数（不再扩展 v0.1 产品范围）

---

## 依赖关系

```
阶段零（合同收口）──► 阶段一（数据合同）──► 阶段二（Reader）──► 阶段三（QueryService + 命令）
         │                     │                                       │
         │                     └──────► 阶段四（前端静态）──────────────┤
         │                                        │                      │
         └────────────────────────────────────────┴──────────► 阶段五（前端接入）
                                                                       │
                                                                阶段六（文档 + 门禁）
                                                                阶段七（真实数据验收）
```

阶段零已随本版文档落实并冻结。阶段四可随阶段一开始（fixture 形状与 DTO 对齐即可，不依赖 Rust 端完成）。阶段五必须在阶段三和四都完成后开始。

## 关键风险

| 风险                                       | 缓解                                                                                                    |
| ------------------------------------------ | ------------------------------------------------------------------------------------------------------- |
| 同时刻截断 DST 正确性                      | 明确算法（唯一/缺口后首个合法时刻/重复取同 offset 否则较早/钳制当日范围）+ 三类 DST Reader 测试         |
| 比较五态 + 零基线分支多                    | `ComparisonPolicy` 决定不可用归因（不依赖判断顺序）+ `rounded_percent_delta`（i128 + 钳制）独立测试 |
| 双命令刷新状态不一致 / 竞态                | home/status 双通道 generation；`live` 覆盖状态卡/今日柱/当前周柱；status.localDate 跨日自动重查       |
| 轻量轮询实际不轻量 / N+1                   | `LiveStatusDto` 拆分（轮询不含摘要）；`recent_recorded_dates` + VALUES CTE 单批次 cutoff            |
| 摘要窗口随`days` 切换而变 / 7 天数据不足 | `stats_home` 统一读取 `max(days+6, 15)` 天超集；摘要恒用 `[today-14, today-1]`，与 `days` 无关  |
| cutoff 零活动日被当成"无基线"              | VALUES CTE 使用 LEFT JOIN + COALESCE，按输入 dates 补齐并保持顺序                                       |
| ±5% 阈值受显示舍入污染                    | 方向/摘要档位用精确比例（i128 交叉相乘），`deltaPercent` 舍入只用于显示                               |
| 多方法组装跨投影时点                       | 命令级单 Reader +`with_snapshot` 读事务快照                                                           |
| 惯性`effectiveDays` 口径                 | 有效日来自`daily_work_metrics`，24 小时补零统一分母；阶段二 DoD 覆盖两表不等价日期                    |
| 今日 workBlockCount 口径                   | 复用`recompute_dates` 工作块计数语义，含未闭合块；与今日页仅存在投影时点差                            |
| 均线 lookback 多读 6 天                    | `stats_home` 统一读取 `max(days+6, 15)` 天超集，`stats_trend` 从超集裁剪，对调用方透明            |
| 惯性午休低谷在无午休数据上误标             | 候选小时写死 {12,13,14}、严格低于 11 点与 15 点均值的局部低谷判定 + 真实数据验收兜底                    |
| 4 类 SVG 图表 + forced-colors              | 复用热力图的系统色回退模式；fixture 阶段即验证                                                          |
