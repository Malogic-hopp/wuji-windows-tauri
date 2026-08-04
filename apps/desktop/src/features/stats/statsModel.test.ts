import { describe, expect, it } from 'vitest';
import {
  coverageLabel,
  formatDeltaMs,
  isHomeEmpty,
  mapDirectionDisplay,
  mapPeriodText,
  mapReliabilityText,
  mapSummaryText,
  slotToToken,
} from './statsModel';
import { comparisonFixture, i64 } from './statsFixture';
import { trendSummary, trendSummaryLabel, weeklySummary, weeklySummaryLabel } from './statsModel';

describe('statsModel 文案映射', () => {
  it('五态：up/down 显示 ▲▼ +X%，stable 基本持平（不含箭头）', () => {
    expect(mapDirectionDisplay(comparisonFixture({ direction: 'up', deltaPercent: 8 }), i64('13320000')))
      .toEqual({ text: '▲ +8%', showArrow: true });
    expect(mapDirectionDisplay(comparisonFixture({ direction: 'down', deltaPercent: -12 }), i64('13320000')))
      .toEqual({ text: '▼ -12%', showArrow: true });
    const stable = mapDirectionDisplay(
      comparisonFixture({ direction: 'stable', deltaPercent: 3, activeDurationMs: i64('12300000') }),
      i64('13320000'),
    );
    expect(stable.text).toBe('基本持平');
    expect(stable.showArrow).toBe(false);
  });

  it('upFromZero：新增 N 分钟（当前值即新增量，禁止伪造百分比）', () => {
    const display = mapDirectionDisplay(
      comparisonFixture({ direction: 'upFromZero', deltaPercent: null, activeDurationMs: i64('0') }),
      i64('720000'),
    );
    expect(display.text).toBe('新增 12m');
    expect(display.showArrow).toBe(false);
  });

  it('unavailable：insufficientSamples → 历史样本不足；noData → 不显示', () => {
    expect(
      mapDirectionDisplay(
        comparisonFixture({ direction: 'unavailable', deltaPercent: null, unavailableReason: 'insufficientSamples' }),
        i64('13320000'),
      ).text,
    ).toBe('历史样本不足');
    expect(
      mapDirectionDisplay(
        comparisonFixture({ direction: 'unavailable', deltaPercent: null, unavailableReason: 'noData' }),
        i64('13320000'),
      ).text,
    ).toBe('');
  });

  it('摘要双窗口句式：方向与时段分号连接，任一部分缺失只输出存在部分', () => {
    expect(mapSummaryText({ direction: 'upSlight', primaryPeriod: 'morning' }))
      .toBe('最近 7 日日均活跃略有上升；通常主要活跃在上午');
    expect(mapSummaryText({ direction: 'down', primaryPeriod: 'night' }))
      .toBe('最近 7 日日均活跃下降；通常主要活跃在夜间');
    expect(mapSummaryText({ direction: null, primaryPeriod: 'afternoon' }))
      .toBe('通常主要活跃在下午');
    expect(mapSummaryText({ direction: 'flat', primaryPeriod: null }))
      .toBe('最近 7 日日均活跃基本持平');
    expect(mapSummaryText({ direction: null, primaryPeriod: null })).toBe('');
  });

  it('时段/可靠性文案', () => {
    expect(mapPeriodText('morning')).toBe('上午');
    expect(mapPeriodText('afternoon')).toBe('下午');
    expect(mapPeriodText('evening')).toBe('晚上');
    expect(mapPeriodText('night')).toBe('夜间');
    expect(mapPeriodText(null)).toBe('');
    expect(mapReliabilityText('preliminary')).toBe('初步模式');
    expect(mapReliabilityText('normal')).toBe(''); // 设计只标注 preliminary
    expect(mapReliabilityText(null)).toBe('');
  });

  it('槽位 → 令牌：0/1/2 映射三个槽位，非法回退 other', () => {
    expect(slotToToken(0)).toBe('var(--chart-app-1)');
    expect(slotToToken(1)).toBe('var(--chart-app-2)');
    expect(slotToToken(2)).toBe('var(--chart-app-3)');
    expect(slotToToken(3)).toBe('var(--chart-other)');
    expect(slotToToken(-1)).toBe('var(--chart-other)');
  });

  it('趋势/周趋势数值锚点（阶段四 review P1-1）', () => {
    // hasData 13 天：总和 163,720,000 → 日均 12,593,846ms（3h30m）；最高 17,600,000ms（4h53m）。
    const summary = trendSummary([
      { localDate: '2026-07-05', activeDurationMs: i64('12600000'), workBlockCount: i64('7'), hasData: true, isToday: false, movingAvg7ActiveMs: null, movingAvg7SampleDays: 0 },
      { localDate: '2026-07-11', activeDurationMs: i64('0'), workBlockCount: i64('0'), hasData: false, isToday: false, movingAvg7ActiveMs: null, movingAvg7SampleDays: 5 },
      { localDate: '2026-07-18', activeDurationMs: i64('17600000'), workBlockCount: i64('8'), hasData: true, isToday: true, movingAvg7ActiveMs: null, movingAvg7SampleDays: 6 },
    ]);
    // (12,600,000 + 17,600,000) / 2 个记录日
    expect(summary.avgMs).toBe('15100000');
    expect(summary.maxMs).toBe('17600000');
    expect(trendSummaryLabel([
      { localDate: '2026-07-05', activeDurationMs: i64('12600000'), workBlockCount: i64('7'), hasData: true, isToday: false, movingAvg7ActiveMs: null, movingAvg7SampleDays: 0 },
      { localDate: '2026-07-11', activeDurationMs: i64('0'), workBlockCount: i64('0'), hasData: false, isToday: false, movingAvg7ActiveMs: null, movingAvg7SampleDays: 5 },
    ])).toBe('日均 3h30m · 最高 3h30m');
    // 全部无记录日 → 空锚点
    expect(trendSummaryLabel([
      { localDate: '2026-07-11', activeDurationMs: i64('0'), workBlockCount: i64('0'), hasData: false, isToday: false, movingAvg7ActiveMs: null, movingAvg7SampleDays: 0 },
    ])).toBe('');
    // 周均：非进行中 2 周 (60M+40M)/2 = 50M → 13h53m；全进行中 → null
    const weeks = [
      { weekStartDate: '2026-06-29', activeDurationMs: i64('60000000'), isCurrentWeek: false, completedRecordedDays: 0, currentWeekDailyAvgMs: null },
      { weekStartDate: '2026-07-06', activeDurationMs: i64('40000000'), isCurrentWeek: false, completedRecordedDays: 0, currentWeekDailyAvgMs: null },
      { weekStartDate: '2026-07-13', activeDurationMs: i64('58000000'), isCurrentWeek: true, completedRecordedDays: 5, currentWeekDailyAvgMs: i64('9000000') },
    ];
    expect(weeklySummary(weeks)).toBe('50000000');
    expect(weeklySummaryLabel(weeks)).toBe('周均 13h53m');
    expect(weeklySummary([weeks[2]])).toBeNull();
    expect(weeklySummaryLabel([weeks[2]])).toBe('');
  });

  it('空状态 / 覆盖标签 / 紧凑时长', () => {
    expect(isHomeEmpty({ hasAnyData: false })).toBe(true);
    expect(isHomeEmpty({ hasAnyData: true })).toBe(false);
    const trend = [
      { localDate: '2026-07-17', activeDurationMs: i64('13800000'), workBlockCount: i64('7'), hasData: true, isToday: false, movingAvg7ActiveMs: i64('14200000'), movingAvg7SampleDays: 7 },
      { localDate: '2026-07-18', activeDurationMs: i64('13320000'), workBlockCount: i64('8'), hasData: true, isToday: true, movingAvg7ActiveMs: null, movingAvg7SampleDays: 6 },
    ];
    expect(coverageLabel(trend, 14)).toBe('近 14 天记录 2 天');
    expect(formatDeltaMs(i64('720000'))).toBe('12m');
    expect(formatDeltaMs(i64('13320000'))).toBe('3h42m');
    expect(formatDeltaMs(i64('3600000'))).toBe('1h');
    expect(formatDeltaMs(i64('-1'))).toBe('—');
  });
});
