import type { HeatmapCellDto, HeatmapDto, Int64String } from '../../types/wuji-core';
import { formatDuration } from '../../lib/format';

export const heatmapHourCount = 24;

/** reporting 时区下的当前小时（0-23）；hour12=false 的 '24' 归一到 0。
 *  供热力图页与主页缩小版共用（今天列当前小时描边）。 */
export function currentLocalHour(timeZoneId: string): number {
  const hour = new Intl.DateTimeFormat('en-US', {
    timeZone: timeZoneId,
    hour: '2-digit',
    hour12: false,
  })
    .formatToParts(new Date())
    .find((part) => part.type === 'hour')?.value;
  return Number(hour ?? '0') % 24;
}

/** 五级强度展示文案（图例与读屏共用）。等级由后端归一化下发，前端不重推。 */
export const heatmapIntensityLabels = ['无', '低', '中', '高', '极高'] as const;

/** 一行 = 一个小时，cells 按日期升序排列。 */
export interface HeatmapGridRow {
  readonly hour: number;
  readonly cells: ReadonlyArray<HeatmapCellDto>;
}

/** buildGrid 返回的完整网格。dates 升序、rows 从上到下 0→23 时。 */
export interface HeatmapGridData {
  readonly dates: ReadonlyArray<string>;
  readonly today: string;
  readonly rangeEndLocalDate: string;
  readonly rows: ReadonlyArray<HeatmapGridRow>;
}

export interface HeatmapFocusPosition {
  readonly hourIndex: number;
  readonly dateIndex: number;
}

export type HeatmapFocusKey = 'ArrowUp' | 'ArrowDown' | 'ArrowLeft' | 'ArrowRight' | 'Home' | 'End';

export function isHeatmapEmpty(heatmap: HeatmapDto): boolean {
  return heatmap.cells.every(
    (cell) =>
      cell.activeDurationMs === '0' &&
      cell.idleDurationMs === '0' &&
      cell.unknownDurationMs === '0',
  );
}

function cellAt(
  cells: ReadonlyArray<HeatmapCellDto>,
  date: string,
  hour: number,
): HeatmapCellDto | undefined {
  return cells.find((cell) => cell.localDate === date && cell.localHour === hour);
}

/**
 * 由 today 与 days 生成完整连续日期轴（升序，today 在最右一列）。
 * 稀疏 cells 不得影响列布局：无记录的日期仍保留整列。
 * Date.UTC 按 24 小时步进归一化，跨月/跨年由历法自动进位。
 */
export function buildDateAxis(today: string, days: number): string[] {
  const count = Math.max(1, Math.trunc(days));
  const end = Date.UTC(
    Number(today.slice(0, 4)),
    Number(today.slice(5, 7)) - 1,
    Number(today.slice(8, 10)),
  );
  const dayMs = 86_400_000;
  return Array.from({ length: count }, (_, index) =>
    new Date(end - (count - 1 - index) * dayMs).toISOString().slice(0, 10),
  );
}

/** 转置：24 行（小时 0→23 从上到下）、完整日期轴从左到右，缺格补零。 */
export function buildGrid(heatmap: HeatmapDto): HeatmapGridData {
  const dates = buildDateAxis(heatmap.rangeEndLocalDate, heatmap.days);
  const rows = Array.from({ length: heatmapHourCount }, (_, hour) => ({
    hour,
    cells: dates.map((date) => cellAt(heatmap.cells, date, hour) ?? createZeroCell(date, hour)),
  }));
  return {
    dates,
    today: heatmap.today,
    rangeEndLocalDate: heatmap.rangeEndLocalDate,
    rows,
  };
}

/** 后端强度等级防御性收敛到 0-4，避免异常值击穿样式与文案下标。 */
export function normalizeIntensityLevel(level: number): number {
  return clamp(Math.trunc(level), 0, heatmapIntensityLabels.length - 1);
}

/** 默认聚焦格：今天的日期列 + 当前小时行。没有今天列时退到最后一列。 */
export function getDefaultFocusPosition(
  grid: HeatmapGridData,
  currentHour: number,
): HeatmapFocusPosition {
  const todayIndex = grid.dates.indexOf(grid.today);
  const dateCount = Math.max(1, grid.dates.length);
  const dateIndex = todayIndex < 0 ? dateCount - 1 : todayIndex;
  return {
    hourIndex: clamp(Math.trunc(currentHour), 0, heatmapHourCount - 1),
    dateIndex: clamp(dateIndex, 0, dateCount - 1),
  };
}

/** 方向键移动（边缘收敛、不环绕）。↑↓ 改小时，←→ 改日期，Home/End 跳到行首/行尾。 */
export function moveFocus(
  position: HeatmapFocusPosition,
  key: HeatmapFocusKey,
  hourCount: number,
  dateCount: number,
): HeatmapFocusPosition {
  switch (key) {
    case 'ArrowUp':
      return clampFocusPosition(
        { hourIndex: position.hourIndex - 1, dateIndex: position.dateIndex },
        hourCount,
        dateCount,
      );
    case 'ArrowDown':
      return clampFocusPosition(
        { hourIndex: position.hourIndex + 1, dateIndex: position.dateIndex },
        hourCount,
        dateCount,
      );
    case 'ArrowLeft':
      return clampFocusPosition(
        { hourIndex: position.hourIndex, dateIndex: position.dateIndex - 1 },
        hourCount,
        dateCount,
      );
    case 'ArrowRight':
      return clampFocusPosition(
        { hourIndex: position.hourIndex, dateIndex: position.dateIndex + 1 },
        hourCount,
        dateCount,
      );
    case 'Home':
      return clampFocusPosition({ hourIndex: position.hourIndex, dateIndex: 0 }, hourCount, dateCount);
    case 'End':
      return clampFocusPosition(
        { hourIndex: position.hourIndex, dateIndex: dateCount - 1 },
        hourCount,
        dateCount,
      );
  }
}

export function clampFocusPosition(
  position: HeatmapFocusPosition,
  hourCount: number,
  dateCount: number,
): HeatmapFocusPosition {
  return {
    hourIndex: clamp(position.hourIndex, 0, Math.max(0, hourCount - 1)),
    dateIndex: clamp(position.dateIndex, 0, Math.max(0, dateCount - 1)),
  };
}

/**
 * 格子标签（tooltip 与 aria-label 共用）。
 * 只格式化已下发字段：时长文本由 formatDuration 换算，强度等级不重推。
 */
export function getCellLabel(cell: HeatmapCellDto): string {
  const prefix = `${formatMonthDay(cell.localDate)} ${String(cell.localHour)}时`;
  if (
    cell.activeDurationMs === '0' &&
    cell.idleDurationMs === '0' &&
    cell.unknownDurationMs === '0'
  ) {
    return `${prefix}，无记录`;
  }
  const intensity = heatmapIntensityLabels[normalizeIntensityLevel(cell.intensityLevel)];
  return `${prefix}，活跃 ${formatDuration(cell.activeDurationMs)}，活跃程度 ${intensity}`;
}

/** YYYY-MM-DD（reporting 时区本地日期）→ M月D日。 */
export function formatMonthDay(date: string): string {
  return `${String(Number(date.slice(5, 7)))}月${String(Number(date.slice(8, 10)))}日`;
}

/** 列头短日期 M/D。 */
export function formatShortDate(date: string): string {
  return `${String(Number(date.slice(5, 7)))}/${String(Number(date.slice(8, 10)))}`;
}

/** YYYY-MM-DD → 周一/周二…；Date.UTC 解析避免浏览器本地时区干扰。 */
export function formatWeekday(date: string): string {
  const day = new Date(
    Date.UTC(Number(date.slice(0, 4)), Number(date.slice(5, 7)) - 1, Number(date.slice(8, 10))),
  );
  return new Intl.DateTimeFormat('zh-CN', { weekday: 'short', timeZone: 'UTC' }).format(day);
}

const timeOfDayLabels: Record<number, string> = {
  3: '凌晨',
  9: '上午',
  15: '下午',
  21: '晚上',
};

/** 时段标签只在每段中间显示：3时→凌晨，9时→上午，15时→下午，21时→晚上。 */
export function getTimeOfDayLabel(hour: number): string {
  return timeOfDayLabels[clamp(Math.trunc(hour), 0, 23)] ?? '';
}

/** 该小时是时段最后一行（5/11/17），下方应画分隔线。 */
export function isHourPeriodEnd(hour: number): boolean {
  return hour === 5 || hour === 11 || hour === 17;
}

function createZeroCell(localDate: string, localHour: number): HeatmapCellDto {
  return {
    localDate,
    localHour,
    activeDurationMs: '0' as Int64String,
    idleDurationMs: '0' as Int64String,
    unknownDurationMs: '0' as Int64String,
    intensityLevel: 0,
  };
}

function clamp(value: number, min: number, max: number): number {
  return Math.min(max, Math.max(min, value));
}
