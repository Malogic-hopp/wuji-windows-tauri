/**
 * 统计主页 fixture（11 实施方案阶段四 4.3）：静态布局阶段不接真实命令，
 * 用 TypeScript fixture 驱动全部视觉与可访问性；阶段五再接入真实数据。
 * 边界覆盖：五态文案、均线 null 断开、缺数据日、进行中（今日/当前周/当前桶/当前月）、
 * 惯性（正常含午休低谷 / reliability null / 全零）、firstRecordedMonth null、hasAnyData=false。
 */
import { shiftLocalDate } from '../../lib/format';
import type {
  CompositionBucketDto,
  HourlyPointDto,
  InertiaDto,
  Int64String,
  LiveStatusDto,
  MilestoneDto,
  MonthlyPointDto,
  SameTimeComparisonDto,
  StatsHomeDto,
  StatsStatusDto,
  StatusDto,
  SummaryDto,
  TrendPointDto,
  WeeklyPointDto,
  WeekProgressDto,
  WorkPaceDto,
} from '../../types/wuji-core';

/** Int64String 品牌（R07：只接受字符串）。 */
export const i64 = (text: string): Int64String => text as Int64String;

const app = (appId: number, name: string) => ({ appId: i64(String(appId)), displayName: name });

const summary: SummaryDto = {
  direction: 'upSlight',
  primaryPeriod: 'morning',
};

/** 14 天趋势（07-05..07-18）：07-11 无数据（hasData=false）、07-13 有记录但活跃 0、今日进行中。 */
const trend: TrendPointDto[] = [
  { localDate: '2026-07-05', activeDurationMs: i64('12600000'), workBlockCount: i64('7'), hasData: true, isToday: false, movingAvg7ActiveMs: null, movingAvg7SampleDays: 0 },
  { localDate: '2026-07-06', activeDurationMs: i64('11800000'), workBlockCount: i64('6'), hasData: true, isToday: false, movingAvg7ActiveMs: i64('13100000'), movingAvg7SampleDays: 7 },
  { localDate: '2026-07-07', activeDurationMs: i64('13500000'), workBlockCount: i64('8'), hasData: true, isToday: false, movingAvg7ActiveMs: i64('12900000'), movingAvg7SampleDays: 7 },
  { localDate: '2026-07-08', activeDurationMs: i64('9000000'), workBlockCount: i64('5'), hasData: true, isToday: false, movingAvg7ActiveMs: i64('13200000'), movingAvg7SampleDays: 6 },
  { localDate: '2026-07-09', activeDurationMs: i64('14400000'), workBlockCount: i64('9'), hasData: true, isToday: false, movingAvg7ActiveMs: i64('13800000'), movingAvg7SampleDays: 7 },
  { localDate: '2026-07-10', activeDurationMs: i64('15200000'), workBlockCount: i64('10'), hasData: true, isToday: false, movingAvg7ActiveMs: i64('13600000'), movingAvg7SampleDays: 7 },
  { localDate: '2026-07-11', activeDurationMs: i64('0'), workBlockCount: i64('0'), hasData: false, isToday: false, movingAvg7ActiveMs: null, movingAvg7SampleDays: 5 },
  { localDate: '2026-07-12', activeDurationMs: i64('10800000'), workBlockCount: i64('6'), hasData: true, isToday: false, movingAvg7ActiveMs: i64('13000000'), movingAvg7SampleDays: 6 },
  { localDate: '2026-07-13', activeDurationMs: i64('0'), workBlockCount: i64('0'), hasData: true, isToday: false, movingAvg7ActiveMs: i64('12600000'), movingAvg7SampleDays: 6 },
  { localDate: '2026-07-14', activeDurationMs: i64('16200000'), workBlockCount: i64('9'), hasData: true, isToday: false, movingAvg7ActiveMs: i64('13300000'), movingAvg7SampleDays: 7 },
  { localDate: '2026-07-15', activeDurationMs: i64('17600000'), workBlockCount: i64('11'), hasData: true, isToday: false, movingAvg7ActiveMs: i64('14000000'), movingAvg7SampleDays: 7 },
  { localDate: '2026-07-16', activeDurationMs: i64('15500000'), workBlockCount: i64('8'), hasData: true, isToday: false, movingAvg7ActiveMs: i64('13800000'), movingAvg7SampleDays: 7 },
  { localDate: '2026-07-17', activeDurationMs: i64('13800000'), workBlockCount: i64('7'), hasData: true, isToday: false, movingAvg7ActiveMs: i64('14200000'), movingAvg7SampleDays: 7 },
  { localDate: '2026-07-18', activeDurationMs: i64('13320000'), workBlockCount: i64('8'), hasData: true, isToday: true, movingAvg7ActiveMs: null, movingAvg7SampleDays: 6 },
];

const weekly: WeeklyPointDto[] = [
  { weekStartDate: '2026-04-27', activeDurationMs: i64('61000000'), isCurrentWeek: false, completedRecordedDays: 0, currentWeekDailyAvgMs: null },
  { weekStartDate: '2026-05-04', activeDurationMs: i64('58000000'), isCurrentWeek: false, completedRecordedDays: 0, currentWeekDailyAvgMs: null },
  { weekStartDate: '2026-05-11', activeDurationMs: i64('64000000'), isCurrentWeek: false, completedRecordedDays: 0, currentWeekDailyAvgMs: null },
  { weekStartDate: '2026-05-18', activeDurationMs: i64('59000000'), isCurrentWeek: false, completedRecordedDays: 0, currentWeekDailyAvgMs: null },
  { weekStartDate: '2026-05-25', activeDurationMs: i64('66000000'), isCurrentWeek: false, completedRecordedDays: 0, currentWeekDailyAvgMs: null },
  { weekStartDate: '2026-06-01', activeDurationMs: i64('63000000'), isCurrentWeek: false, completedRecordedDays: 0, currentWeekDailyAvgMs: null },
  { weekStartDate: '2026-06-08', activeDurationMs: i64('69000000'), isCurrentWeek: false, completedRecordedDays: 0, currentWeekDailyAvgMs: null },
  { weekStartDate: '2026-06-15', activeDurationMs: i64('72000000'), isCurrentWeek: false, completedRecordedDays: 0, currentWeekDailyAvgMs: null },
  { weekStartDate: '2026-06-22', activeDurationMs: i64('70000000'), isCurrentWeek: false, completedRecordedDays: 0, currentWeekDailyAvgMs: null },
  { weekStartDate: '2026-06-29', activeDurationMs: i64('65000000'), isCurrentWeek: false, completedRecordedDays: 0, currentWeekDailyAvgMs: null },
  { weekStartDate: '2026-07-06', activeDurationMs: i64('68000000'), isCurrentWeek: false, completedRecordedDays: 0, currentWeekDailyAvgMs: null },
  { weekStartDate: '2026-07-13', activeDurationMs: i64('58320000'), isCurrentWeek: true, completedRecordedDays: 5, currentWeekDailyAvgMs: i64('9000000') },
];

const weekProgress: WeekProgressDto = {
  // 与当前周柱守恒：已完成 45,000,000 + 今日 13,320,000 = 58,320,000。
  currentActiveMs: i64('58320000'),
  lastWeekSame: {
    activeDurationMs: i64('0'),
    deltaPercent: null,
    direction: 'upFromZero',
    sampleDays: 1,
    unavailableReason: null,
  },
  recordedDays: 6,
  cutoffLocalTime: '15:20',
};

const palette = [
  { app: app(1, 'VS Code'), slot: 0 },
  { app: app(2, '浏览器'), slot: 1 },
  { app: app(3, '终端'), slot: 2 },
];

function dayBucket(date: string, isCurrent: boolean, hasData: boolean, apps: Array<[number, string, number]>, others: number): CompositionBucketDto {
  return {
    startDate: date,
    endDate: date,
    bucketKind: 'day',
    isCurrent,
    hasData,
    apps: apps.map(([id, name, ms]) => ({ app: app(id, name), activeDurationMs: i64(String(ms)) })),
    othersActiveMs: i64(String(others)),
  };
}

const composition: CompositionBucketDto[] = [
  dayBucket('2026-07-05', false, true, [[1, 'VS Code', 8400000], [2, '浏览器', 3000000]], 1200000),
  dayBucket('2026-07-06', false, true, [[1, 'VS Code', 7000000], [2, '浏览器', 3600000]], 1200000),
  dayBucket('2026-07-07', false, true, [[1, 'VS Code', 9000000], [2, '浏览器', 2400000], [3, '终端', 1500000]], 600000),
  dayBucket('2026-07-08', false, true, [[1, 'VS Code', 5000000], [2, '浏览器', 3000000]], 1000000),
  dayBucket('2026-07-09', false, true, [[1, 'VS Code', 9600000], [3, '终端', 3000000]], 1800000),
  dayBucket('2026-07-10', false, true, [[1, 'VS Code', 10000000], [2, '浏览器', 3600000]], 1600000),
  dayBucket('2026-07-11', false, false, [], 0),
  dayBucket('2026-07-12', false, true, [[2, '浏览器', 6600000], [1, 'VS Code', 3600000]], 600000),
  dayBucket('2026-07-13', false, true, [], 0),
  dayBucket('2026-07-14', false, true, [[1, 'VS Code', 10800000], [3, '终端', 4200000]], 1200000),
  dayBucket('2026-07-15', false, true, [[1, 'VS Code', 11600000], [2, '浏览器', 4200000]], 1800000),
  dayBucket('2026-07-16', false, true, [[1, 'VS Code', 9800000], [2, '浏览器', 3000000]], 2700000),
  dayBucket('2026-07-17', false, true, [[1, 'VS Code', 9000000], [2, '浏览器', 3000000]], 1800000),
  dayBucket('2026-07-18', true, true, [[1, 'VS Code', 8400000], [2, '浏览器', 3600000]], 1320000),
];

const hourlyProfile: HourlyPointDto[] = Array.from({ length: 24 }, (_, hour) => ({
  localHour: hour,
  avgActiveMs: i64(String(
    hour === 9 ? 3300000
      : hour === 10 ? 2800000
        : hour === 11 ? 1800000
          : hour === 14 ? 600000
            : hour === 15 ? 1500000
              : hour === 16 ? 2400000
                : hour === 17 ? 1200000
                  : 0,
  )),
}));

const inertia: InertiaDto = {
  startHour: 9,
  peakHour: 10,
  endHour: 19,
  lunchLowestHour: 13,
  effectiveDays: 11,
  totalDays: 14,
  reliability: 'normal',
};

// v0.2 候选：工作节奏（占比 + 常见工作时段 + 上午利用率），与惯性同窗口/同可靠性门禁。
export const workPaceFixture = (): WorkPaceDto => ({
  hourlyCoverageMs: Array.from({ length: 24 }, (_, hour) => ({
    localHour: hour,
    avgCoverageMs: i64(String(
      hour >= 9 && hour < 12 ? 3600000
        : hour >= 13 && hour < 18 ? 3600000
          : hour === 8 ? 1800000
            : hour === 12 || hour === 18 ? 1800000
              : 0,
    )),
  })),
  // 覆盖 9-12 + 13-18（8h）＋半小时边界 ≈ 8.5h / 24 = 35%。
  workRatioPercent: 35,
  commonStartMinutes: 978, // 16:18
  commonEndMinutes: 1382, // 23:02
  morningWorkDays: 0,
  effectiveDays: 11,
  totalDays: 14,
  reliability: 'normal',
});

const workPace = workPaceFixture();

const milestone: MilestoneDto = {
  // 2026-03-01 至 2026-07-17 自然日 139；138 = 几乎每天有记录（口径自洽）。
  totalRecordedDays: i64('138'),
  longestConsecutiveDays: i64('67'),
  firstRecordedMonth: '2026-03',
};

const monthly: MonthlyPointDto[] = [
  { month: '2026-02', activeDurationMs: i64('0'), recordedDays: 0, isCurrentMonth: false, avgActiveMsPerRecordedDay: null },
  { month: '2026-03', activeDurationMs: i64('82000000'), recordedDays: 19, isCurrentMonth: false, avgActiveMsPerRecordedDay: i64('4316000') },
  { month: '2026-04', activeDurationMs: i64('96000000'), recordedDays: 22, isCurrentMonth: false, avgActiveMsPerRecordedDay: i64('4364000') },
  { month: '2026-05', activeDurationMs: i64('104000000'), recordedDays: 23, isCurrentMonth: false, avgActiveMsPerRecordedDay: i64('4522000') },
  { month: '2026-06', activeDurationMs: i64('99000000'), recordedDays: 21, isCurrentMonth: false, avgActiveMsPerRecordedDay: i64('4714000') },
  // 当前月 07-18：recordedDays 只计完整日（不含今日）→ 最大 17；总量含今日截止。
  { month: '2026-07', activeDurationMs: i64('92659000'), recordedDays: 17, isCurrentMonth: true, avgActiveMsPerRecordedDay: i64('4667000') },
];

/** 实时状态（轻量轮询载荷；阶段零 P0-1 拆分后不含摘要）。 */
export const liveStatusFixture: LiveStatusDto = {
  todayActiveMs: i64('13320000'),
  workBlockCount: i64('8'),
  cutoffLocalTime: '15:20',
  yesterdaySame: {
    activeDurationMs: i64('12300000'),
    deltaPercent: 8,
    direction: 'up',
    sampleDays: 1,
    unavailableReason: null,
  },
  last7AvgSame: {
    activeDurationMs: i64('15100000'),
    deltaPercent: -12,
    direction: 'down',
    sampleDays: 7,
    unavailableReason: null,
  },
};

const status: StatusDto = {
  ...liveStatusFixture,
  summary,
};

/** 统计主页全量 fixture（正常数据，覆盖主要边界）。 */
export const statsHomeFixture: StatsHomeDto = {
  hasAnyData: true,
  localDate: '2026-07-18',
  reportingTimeZoneId: 'Asia/Shanghai',
  status,
  trend,
  weekly,
  weekProgress,
  composition,
  palette,
  hourlyProfile,
  inertia,
  workPace,
  milestone,
  monthly,
};

/** 轻量轮询 fixture（阶段五轮询/跨日测试用）。 */
export const statsStatusFixture: StatsStatusDto = {
  localDate: '2026-07-18',
  reportingTimeZoneId: 'Asia/Shanghai',
  liveStatus: liveStatusFixture,
  weekProgress,
  todayTrendPoint: {
    localDate: '2026-07-18',
    activeDurationMs: i64('13320000'),
    workBlockCount: i64('8'),
    hasData: true,
    isToday: true,
    movingAvg7ActiveMs: null,
    movingAvg7SampleDays: 6,
  },
};

/** 空状态（hasAnyData = false → 整页引导）。 */
export const statsHomeEmptyFixture: StatsHomeDto = {
  hasAnyData: false,
  localDate: '2026-07-18',
  reportingTimeZoneId: 'Asia/Shanghai',
  status: {
    todayActiveMs: i64('0'),
    workBlockCount: i64('0'),
    cutoffLocalTime: '15:20',
    yesterdaySame: {
      activeDurationMs: null,
      deltaPercent: null,
      direction: 'unavailable',
      sampleDays: 0,
      unavailableReason: 'noData',
    },
    last7AvgSame: {
      activeDurationMs: null,
      deltaPercent: null,
      direction: 'unavailable',
      sampleDays: 0,
      unavailableReason: 'insufficientSamples',
    },
    summary: { direction: null, primaryPeriod: null },
  },
  trend: Array.from({ length: 14 }, (_, i) => ({
    localDate: `2026-07-${String(5 + i).padStart(2, '0')}`,
    activeDurationMs: i64('0'),
    workBlockCount: i64('0'),
    hasData: false,
    isToday: i === 13,
    movingAvg7ActiveMs: null,
    movingAvg7SampleDays: 0,
  })),
  weekly: [
    '2026-04-27', '2026-05-04', '2026-05-11', '2026-05-18', '2026-05-25',
    '2026-06-01', '2026-06-08', '2026-06-15', '2026-06-22', '2026-06-29',
    '2026-07-06', '2026-07-13',
  ].map((weekStartDate, i) => ({
    weekStartDate,
    activeDurationMs: i64('0'),
    isCurrentWeek: i === 11,
    completedRecordedDays: 0,
    currentWeekDailyAvgMs: null,
  })),
  weekProgress: {
    currentActiveMs: i64('0'),
    lastWeekSame: {
      activeDurationMs: null,
      deltaPercent: null,
      direction: 'unavailable',
      sampleDays: 1,
      unavailableReason: 'noData',
    },
    recordedDays: 0,
    cutoffLocalTime: '15:20',
  },
  composition: [],
  palette: [],
  hourlyProfile: Array.from({ length: 24 }, (_, hour) => ({ localHour: hour, avgActiveMs: i64('0') })),
  inertia: {
    startHour: null,
    peakHour: null,
    endHour: null,
    lunchLowestHour: null,
    effectiveDays: 0,
    totalDays: 14,
    reliability: null,
  },
  workPace: {
    hourlyCoverageMs: Array.from({ length: 24 }, (_, hour) => ({
      localHour: hour,
      avgCoverageMs: i64('0'),
    })),
    workRatioPercent: 0,
    commonStartMinutes: null,
    commonEndMinutes: null,
    morningWorkDays: 0,
    effectiveDays: 0,
    totalDays: 14,
    reliability: null,
  },
  milestone: {
    totalRecordedDays: i64('0'),
    longestConsecutiveDays: i64('0'),
    firstRecordedMonth: null,
  },
  monthly: ['2026-02', '2026-03', '2026-04', '2026-05', '2026-06', '2026-07'].map((month, i) => ({
    month,
    activeDurationMs: i64('0'),
    recordedDays: 0,
    isCurrentMonth: i === 5,
    avgActiveMsPerRecordedDay: null,
  })),
};

/** 惯性可靠性 null（有效日 < 3）变体：派生字段全 null。 */
export const inertiaUnreliableFixture = (
  overrides: Partial<InertiaDto> = {},
): InertiaDto => ({
  startHour: null,
  peakHour: null,
  endHour: null,
  lunchLowestHour: null,
  effectiveDays: 2,
  totalDays: 14,
  reliability: null,
  ...overrides,
});

/** 惯性全零曲线变体（有效日充足但 24 小时均值全 0）。 */
export const inertiaAllZeroFixture = (
  overrides: Partial<InertiaDto> = {},
): InertiaDto => ({
  startHour: null,
  peakHour: null,
  endHour: null,
  lunchLowestHour: null,
  effectiveDays: 11,
  totalDays: 14,
  reliability: 'normal',
  ...overrides,
});

/** firstRecordedMonth null（无任何有效记录）里程碑变体。 */
export const milestoneNoFirstMonthFixture = (
  overrides: Partial<MilestoneDto> = {},
): MilestoneDto => ({
  totalRecordedDays: i64('0'),
  longestConsecutiveDays: i64('0'),
  firstRecordedMonth: null,
  ...overrides,
});

/** 五态比较对象变体（组件测试直接传入）。 */
export const comparisonFixture = (
  overrides: Partial<SameTimeComparisonDto>,
): SameTimeComparisonDto => ({
  activeDurationMs: i64('12300000'),
  deltaPercent: 8,
  direction: 'up',
  sampleDays: 1,
  unavailableReason: null,
  ...overrides,
});

/** 30 天构成（ISO 周桶）：当前周不完整（仅部分天有记录），一周 hasData=false。 */
export const weekCompositionFixture: CompositionBucketDto[] = [
  {
    startDate: '2026-06-19',
    endDate: '2026-06-21',
    bucketKind: 'week',
    isCurrent: false,
    hasData: true,
    apps: [{ app: app(1, 'VS Code'), activeDurationMs: i64('12000000') }],
    othersActiveMs: i64('2000000'),
  },
  {
    startDate: '2026-06-22',
    endDate: '2026-06-28',
    bucketKind: 'week',
    isCurrent: false,
    hasData: true,
    apps: [{ app: app(1, 'VS Code'), activeDurationMs: i64('34000000') }, { app: app(2, '浏览器'), activeDurationMs: i64('9000000') }],
    othersActiveMs: i64('4000000'),
  },
  {
    startDate: '2026-06-29',
    endDate: '2026-07-05',
    bucketKind: 'week',
    isCurrent: false,
    hasData: false,
    apps: [],
    othersActiveMs: i64('0'),
  },
  {
    startDate: '2026-07-06',
    endDate: '2026-07-12',
    bucketKind: 'week',
    isCurrent: false,
    hasData: true,
    apps: [{ app: app(1, 'VS Code'), activeDurationMs: i64('30000000') }],
    othersActiveMs: i64('5000000'),
  },
  {
    startDate: '2026-07-13',
    endDate: '2026-07-18',
    bucketKind: 'week',
    isCurrent: true,
    hasData: true,
    apps: [{ app: app(1, 'VS Code'), activeDurationMs: i64('24000000') }],
    othersActiveMs: i64('3000000'),
  },
];

/** 30 天视图 fixture（阶段四 4.3："周桶 isCurrent 不完整周" + "stable 3%" 边界入 fixture 本体）。 */
export const statsHomeWeekFixture: StatsHomeDto = {
  ...statsHomeFixture,
  trend: Array.from({ length: 30 }, (_, i) => ({
    localDate: shiftLocalDate('2026-07-18', -(29 - i)),
    activeDurationMs: i64(String(10_000_000 + i * 100_000)),
    workBlockCount: i64('6'),
    hasData: i !== 10,
    isToday: i === 29,
    movingAvg7ActiveMs: i >= 6 && i !== 10 ? i64('12000000') : null,
    movingAvg7SampleDays: i >= 6 ? 7 : i,
  })),
  composition: weekCompositionFixture,
  status: {
    ...statsHomeFixture.status,
    last7AvgSame: comparisonFixture({
      direction: 'stable',
      deltaPercent: 3,
      activeDurationMs: i64('12800000'),
      sampleDays: 7,
    }),
  },
};
