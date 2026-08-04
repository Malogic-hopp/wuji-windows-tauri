import { describe, expect, it } from 'vitest';
import {
  statsHomeFixture,
  statsHomeWeekFixture,
} from './statsFixture';

const i64num = (text: string): number => Number(text);

/** 守卫式取非空（避免禁用的 `!` 断言）。 */
function requireDefined<T>(value: T | undefined, what: string): T {
  expect(value).toBeDefined();
  if (value == null) {
    throw new Error(`fixture 缺少 ${what}`);
  }
  return value;
}

describe('统计主页 fixture 合同自洽性（P2-02）', () => {
  it('当前周守恒：已完成(日均×天数) + 今日 = 周总量 = weekProgress', () => {
    const current = requireDefined(
      statsHomeFixture.weekly.find((w) => w.isCurrentWeek),
      '当前周',
    );
    const completedMs =
      i64num(current.currentWeekDailyAvgMs ?? '0') * current.completedRecordedDays;
    const todayMs = i64num(statsHomeFixture.status.todayActiveMs);
    const totalMs = i64num(current.activeDurationMs);
    expect(completedMs + todayMs).toBe(totalMs);
    expect(i64num(statsHomeFixture.weekProgress.currentActiveMs)).toBe(totalMs);
  });

  it('当前月 recordedDays 按合同排除今日，不超过当月自然日上限', () => {
    const currentMonth = requireDefined(
      statsHomeFixture.monthly.find((m) => m.isCurrentMonth),
      '当前月',
    );
    expect(currentMonth.month).toBe('2026-07');
    // 报告日 2026-07-18 → 7 月 1..17 最多 17 个完整记录日（不含今日）。
    expect(currentMonth.recordedDays).toBeLessThanOrEqual(17);
    expect(currentMonth.avgActiveMsPerRecordedDay).not.toBeNull();
  });

  it('里程碑累计天数不超过首次记录月到报告日的自然日', () => {
    const total = i64num(statsHomeFixture.milestone.totalRecordedDays);
    const first = statsHomeFixture.milestone.firstRecordedMonth;
    expect(first).toBe('2026-03');
    // 2026-03-01 → 2026-07-17 自然日 = 139；fixture 自洽值 138。
    expect(total).toBeLessThanOrEqual(139);
    expect(total).toBe(138);
    expect(i64num(statsHomeFixture.milestone.longestConsecutiveDays)).toBeLessThanOrEqual(total);
  });

  it('今日趋势点与状态卡今日活跃同源', () => {
    const today = requireDefined(
      statsHomeFixture.trend.find((t) => t.isToday),
      '今日趋势点',
    );
    expect(i64num(today.activeDurationMs)).toBe(i64num(statsHomeFixture.status.todayActiveMs));
  });

  it('30 天视图 fixture：30 个合法日期、isToday 在报告日', () => {
    const trend = statsHomeWeekFixture.trend;
    expect(trend.length).toBe(30);
    for (const point of trend) {
      expect(/^\d{4}-\d{2}-\d{2}$/.test(point.localDate)).toBe(true);
    }
    const today = trend.filter((t) => t.isToday);
    expect(today.length).toBe(1);
    const todayPoint = requireDefined(today[0], '30 天视图今日点');
    expect(todayPoint.localDate).toBe('2026-07-18');
  });
});
