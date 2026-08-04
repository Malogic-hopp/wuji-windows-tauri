/**
 * 统计主页纯工具（11 实施方案阶段四 4.2）：文案映射与展示计算。
 * 全部为纯函数（不依赖 React）；Rust 已算口径，这里只做格式化与映射，
 * 不做时长聚合、不做均线/惯性等重算。
 */
import type {
  Int64String,
  SameTimeComparisonDto,
  SummaryDto,
  TrendPointDto,
  WeeklyPointDto,
} from '../../types/wuji-core';

export interface DirectionDisplay {
  readonly text: string;
  readonly showArrow: boolean;
}

/**
 * 比较方向五态文案（10 §4.1）：
 * - up/down → "▲ +8%" / "▼ -12%"（deltaPercent 为 Rust 舍入后的显示值）；
 * - stable → "基本持平"；
 * - upFromZero → "新增 N 分钟"，N = 当前值（基线为 0，当前值即新增量，设计禁止伪造百分比）；
 * - unavailable + insufficientSamples → "历史样本不足"；unavailable + noData → 不显示。
 */
export function mapDirectionDisplay(
  comparison: SameTimeComparisonDto,
  currentActiveMs: Int64String,
): DirectionDisplay {
  switch (comparison.direction) {
    case 'up':
      return { text: `▲ +${String(comparison.deltaPercent ?? 0)}%`, showArrow: true };
    case 'down':
      return { text: `▼ ${String(comparison.deltaPercent ?? 0)}%`, showArrow: true };
    case 'stable':
      return { text: '基本持平', showArrow: false };
    case 'upFromZero':
      // 紧凑格式与主数字一致（review-2 主卡）："新增 16h12m"（设计保留"新增"措辞，
      // 不伪造百分比；时长用 formatDeltaMs 紧凑样式而非长格式）。
      return { text: `新增 ${formatDeltaMs(currentActiveMs)}`, showArrow: false };
    case 'unavailable':
      return comparison.unavailableReason === 'insufficientSamples'
        ? { text: '历史样本不足', showArrow: false }
        : { text: '', showArrow: false };
  }
}

/** 摘要方向 → 中性措辞（10 §5.3：不生成波动描述）。 */
function summaryDirectionText(direction: SummaryDto['direction']): string {
  switch (direction) {
    case 'up':
      return '上升';
    case 'upSlight':
      return '略有上升';
    case 'flat':
      return '基本持平';
    case 'downSlight':
      return '略有下降';
    case 'down':
      return '下降';
    default:
      return '';
  }
}

/**
 * 摘要双窗口句式（10 §5.3）："最近 7 日日均活跃略有上升；通常主要活跃在上午"。
 * 方向与时段来自不同窗口，用分号连接、不得暗示同一窗口；任一部分缺失时只输出存在部分。
 */
export function mapSummaryText(summary: SummaryDto): string {
  const clauses: string[] = [];
  if (summary.direction !== null) {
    clauses.push(`最近 7 日日均活跃${summaryDirectionText(summary.direction)}`);
  }
  if (summary.primaryPeriod !== null) {
    clauses.push(`通常主要活跃在${mapPeriodText(summary.primaryPeriod)}`);
  }
  return clauses.join('；');
}

export function mapPeriodText(period: SummaryDto['primaryPeriod']): string {
  switch (period) {
    case 'morning':
      return '上午';
    case 'afternoon':
      return '下午';
    case 'evening':
      return '晚上';
    case 'night':
      return '夜间';
    default:
      return '';
  }
}

export function mapReliabilityText(
  reliability: 'preliminary' | 'normal' | null,
): string {
  // 设计只定义"初步模式"（3-6 天）；normal（≥7 天）不标注。
  switch (reliability) {
    case 'preliminary':
      return '初步模式';
    case 'normal':
    default:
      return '';
  }
}

/** 槽位 → CSS 令牌（10 §4.4）；非法槽位回退到"其他"（纵深防御）。 */
export function slotToToken(slot: number): string {
  switch (slot) {
    case 0:
      return 'var(--chart-app-1)';
    case 1:
      return 'var(--chart-app-2)';
    case 2:
      return 'var(--chart-app-3)';
    default:
      return 'var(--chart-other)';
  }
}

/** 整页空状态（10 §5.3 hasAnyData = false → 首次使用引导）。 */
export function isHomeEmpty(stats: { hasAnyData: boolean }): boolean {
  return !stats.hasAnyData;
}

/** "近 14 天记录 12 天" 轻量胶囊（趋势内 hasData=true 的点数）。 */
export function coverageLabel(trend: readonly TrendPointDto[], days: number): string {
  const recorded = trend.filter((point) => point.hasData).length;
  return `近 ${String(days)} 天记录 ${String(recorded)} 天`;
}

/** 趋势数值锚点：记录日（hasData=true）的平均与最大（纯展示汇总，不做口径重算）。 */
export function trendSummary(
  points: readonly TrendPointDto[],
): { avgMs: Int64String | null; maxMs: Int64String | null } {
  const recorded = points.filter((point) => point.hasData);
  if (recorded.length === 0) {
    return { avgMs: null, maxMs: null };
  }
  let sum = 0n;
  let max = 0n;
  for (const point of recorded) {
    const ms = BigInt(point.activeDurationMs);
    sum += ms;
    if (ms > max) max = ms;
  }
  return {
    avgMs: (sum / BigInt(recorded.length)).toString() as Int64String,
    maxMs: max.toString() as Int64String,
  };
}

/** 周趋势锚点：非进行中周的周均（进行中周不完整，不参与均值）。 */
export function weeklySummary(points: readonly WeeklyPointDto[]): Int64String | null {
  const complete = points.filter((point) => !point.isCurrentWeek);
  if (complete.length === 0) return null;
  let sum = 0n;
  for (const point of complete) sum += BigInt(point.activeDurationMs);
  return (sum / BigInt(complete.length)).toString() as Int64String;
}

/** 趋势标题右侧锚点文案："日均 3h30m · 最高 4h53m"；无记录日返回空串。 */
export function trendSummaryLabel(points: readonly TrendPointDto[]): string {
  const { avgMs, maxMs } = trendSummary(points);
  if (avgMs == null || maxMs == null) return '';
  return `日均 ${formatDeltaMs(avgMs)} · 最高 ${formatDeltaMs(maxMs)}`;
}

/** 周趋势标题右侧锚点文案："周均 18h3m"；无完整周返回空串。 */
export function weeklySummaryLabel(points: readonly WeeklyPointDto[]): string {
  const avg = weeklySummary(points);
  return avg == null ? '' : `周均 ${formatDeltaMs(avg)}`;
}

/** ISO 周序号（ISO 8601）：以该日期所在周的周四是归属年判据（12 月末可能属次年 W1）；
 *  归属年的 1 月 4 日所在周的周一为 W1 起点。 */
export function isoWeekOf(dateStr: string): number {
  const [year, month, day] = dateStr.split('-').map(Number);
  const date = Date.UTC(year, month - 1, day);
  const dow = (utc: number): number => new Date(utc).getUTCDay() || 7; // Mon=1..Sun=7
  const thursday = date + (4 - dow(date)) * 86_400_000;
  const thuYear = new Date(thursday).getUTCFullYear();
  const jan4 = Date.UTC(thuYear, 0, 4);
  const week1Start = jan4 - (dow(jan4) - 1) * 86_400_000;
  const days = Math.round((date - week1Start) / 86_400_000);
  return Math.floor(days / 7) + 1;
}

/** 紧凑时长 "3h42m" / "12m"（趋势柱 aria、紧凑标注；毫秒输入）。 */
export function formatDeltaMs(msText: Int64String): string {
  if (!/^-?\d+$/.test(msText)) return '—';
  const ms = BigInt(msText);
  if (ms < 0n) return '—';
  const minutes = (ms + 30_000n) / 60_000n;
  if (minutes < 60n) return `${minutes.toString()}m`;
  const hours = minutes / 60n;
  const rest = minutes % 60n;
  return rest === 0n ? `${hours.toString()}h` : `${hours.toString()}h${rest.toString()}m`;
}
