import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { TrendChart } from './TrendChart';
import { i64 } from '../statsFixture';
import type { TrendPointDto } from '../../../types/wuji-core';

function point(
  date: string,
  active: string,
  ma: string | null,
  hasData = true,
  isToday = false,
): TrendPointDto {
  return {
    localDate: date,
    activeDurationMs: i64(active),
    workBlockCount: i64('8'),
    hasData,
    isToday,
    movingAvg7ActiveMs: ma == null ? null : i64(ma),
    movingAvg7SampleDays: ma == null ? 0 : 7,
  };
}

describe('TrendChart 活跃趋势', () => {
  it('今日柱进行中、缺数据日斜纹 + 中性文案、均线 null 断开', () => {
    const points = [
      point('2026-07-15', '9000000', '8000000'),
      point('2026-07-16', '10000000', '9000000'), // 与上一点连续 → 第一段
      point('2026-07-17', '8000000', null, false), // 缺数据 → 均线断开
      point('2026-07-18', '12000000', null, true, true), // 今日进行中
    ];
    const { container } = render(<TrendChart points={points} days={4} cutoffLocalTime="15:20" />);
    // 今日柱进行中 class
    expect(container.querySelector('.trend-bar--today')).not.toBeNull();
    // 缺数据柱 + 中性文案（斜纹占位）
    expect(container.querySelector('.trend-bar--nodata')).not.toBeNull();
    expect(screen.getByTitle('当日无记录数据')).toBeInTheDocument();
    // 今日柱"截至 HH:MM"标签（§9 P0-4）
    expect(screen.getByTitle('今日进行中（截至 15:20）')).toBeInTheDocument();
    // 键盘可达：柱可 focus（tabIndex + aria-label）
    const todayBar = container.querySelector('.trend-bar--today');
    expect(todayBar?.getAttribute('tabindex')).toBe('0');
    // 均线 null 断开：连续两段（07-15/16）一段 + 断开 → polyline 数量 1
    const polylines = container.querySelectorAll('.chart__ma-line');
    expect(polylines.length).toBe(1);
    // 图例
    expect(screen.getByText('今日进行中')).toBeInTheDocument();
    expect(screen.getByText('当日无记录数据')).toBeInTheDocument();
    expect(screen.getByText('7 日均线')).toBeInTheDocument();
    // 时间刻度：每根柱一个 span（与柱槽同布局居中对齐），今天恒显示
    const ticks = container.querySelector('.trend-ticks');
    expect(ticks).not.toBeNull();
    expect(ticks?.getAttribute('aria-hidden')).toBe('true');
    const tickSpans = Array.from(ticks?.querySelectorAll('span') ?? []);
    const tickTexts = tickSpans.map((s) => s.textContent);
    expect(tickTexts).toEqual(['07-15', '07-16', '07-17', '今天']);
    // 今天标签带强调 class；末位（今天）不参与降密度隐藏
    expect(tickSpans[3]?.className).toContain('trend-tick--today');
    expect(tickSpans[3]?.className).not.toContain('trend-tick--sparse');
    // 中间点（index 2 = 07-17）保留；非首/中/今的 index 1（07-16）参与窄屏降密度
    expect(tickSpans[2]?.className).not.toContain('trend-tick--sparse');
    expect(tickSpans[1]?.className).toContain('trend-tick--sparse');
  });

  it('柱体 inline 高度 = 值/max（P1-01：不会退化成 2px）', () => {
    const points = [
      point('2026-07-16', '10000000', '9000000'),
      point('2026-07-17', '5000000', '8000000'),
    ];
    const { container } = render(<TrendChart points={points} days={2} cutoffLocalTime="15:20" />);
    const bars = Array.from(container.querySelectorAll('.trend-bar'));
    // 内层柱体（非 nodata）直接携带百分比高度
    expect((bars[0] as HTMLElement).style.height).toBe('100%');
    expect((bars[1] as HTMLElement).style.height).toBe('50%');
  });

  it('纵轴纳入均线最大值（P2-01）：均线高于全部柱时折线不越界', () => {
    // 柱 10M/12M，均线 20M/18M → max=20M，y ∈ [0,100]。
    const points = [
      point('2026-07-16', '10000000', '20000000'),
      point('2026-07-17', '12000000', '18000000'),
    ];
    const { container } = render(<TrendChart points={points} days={2} cutoffLocalTime="15:20" />);
    const polyline = container.querySelector('.chart__ma-line');
    expect(polyline).not.toBeNull();
    const pts = (polyline as SVGElement).getAttribute('points') ?? '';
    for (const pair of pts.split(' ')) {
      const y = Number(pair.split(',')[1]);
      expect(y >= 0 && y <= 100).toBe(true);
    }
  });

  it('均线全部 null → 无折线；aria-label 携带数值', () => {
    const points = [point('2026-07-17', '8000000', null), point('2026-07-18', '0', null, true, true)];
    const { container } = render(<TrendChart points={points} days={2} cutoffLocalTime="15:20" />);
    expect(container.querySelectorAll('.chart__ma-line').length).toBe(0);
    expect(screen.getByLabelText('2026-07-17 活跃 2h13m')).toBeInTheDocument();
  });
});
