import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { WeeklyChart, WeekProgressCard } from './WeeklyChart';
import { i64 } from '../statsFixture';
import type { WeekProgressDto, WeeklyPointDto } from '../../../types/wuji-core';

const week = (date: string, ms: string, isCurrent = false, completed = 0, avg: string | null = null): WeeklyPointDto => ({
  weekStartDate: date,
  activeDurationMs: i64(ms),
  isCurrentWeek: isCurrent,
  completedRecordedDays: completed,
  currentWeekDailyAvgMs: avg == null ? null : i64(avg),
});

const progress: WeekProgressDto = {
  currentActiveMs: i64('59000000'),
  lastWeekSame: {
    activeDurationMs: i64('50000000'),
    deltaPercent: 7,
    direction: 'up',
    sampleDays: 1,
    unavailableReason: null,
  },
  recordedDays: 6,
  cutoffLocalTime: '15:20',
};

describe('WeeklyChart 近 12 周', () => {
  it('当前周进行中；completedRecordedDays>0 时显示虚框参考线', () => {
    const points = [
      week('2026-07-06', '68000000'),
      week('2026-07-13', '59000000', true, 5, '9000000'),
    ];
    const { container } = render(<WeeklyChart points={points} weekProgress={progress} />);
    expect(container.querySelector('.week-bar--current')).not.toBeNull();
    expect(container.querySelector('.chart__ref')).not.toBeNull();
    expect(container.querySelector('.chart__ref-label')).not.toBeNull();
    expect(screen.getByTitle('按已完成记录日的日均值推算：约 17h30m')).toBeInTheDocument();
    expect(screen.getAllByText('本周日均推算').length).toBeGreaterThanOrEqual(2);
  });

  it('柱下刻度：每根柱 W 序号，跨月柱叠加月份（aria-hidden）', () => {
    const points = [
      week('2026-06-22', '70000000'),
      week('2026-06-29', '65000000'),
      week('2026-07-06', '68000000'),
      week('2026-07-13', '59000000', true, 5, '9000000'),
    ];
    const { container } = render(<WeeklyChart points={points} weekProgress={progress} />);
    const ticks = container.querySelector('.week-ticks');
    expect(ticks).not.toBeNull();
    expect(ticks?.getAttribute('aria-hidden')).toBe('true');
    const ticksEl = Array.from(ticks?.querySelectorAll('.week-tick') ?? []);
    expect(ticksEl.length).toBe(4);
    // 每柱 W 序号；月份只在跨月柱（07-06）显示，首柱不再恒显示
    const monthTexts = ticksEl.map((s) => s.querySelector('.week-tick__month')?.textContent);
    expect(monthTexts).toEqual(['', '', '7月', '']);
    const weekTexts = ticksEl.map((s) => s.querySelector('.week-tick__week')?.textContent);
    expect(weekTexts).toEqual(['W26', 'W27', 'W28', 'W29']);
    // 降密度 class：跨月柱（07-06）为锚点恒保留；非跨月且隔一个的柱（06-29）参与中屏隐藏；
    // 首柱（无月份，W 序号锚点）与末柱不参与窄屏降密度
    expect(ticksEl[0]?.className).not.toContain('week-tick--month');
    expect(ticksEl[2]?.className).toContain('week-tick--month');
    expect(ticksEl[1]?.className).toContain('week-tick--dense');
    expect(ticksEl[1]?.className).toContain('week-tick--sparse');
    expect(ticksEl[3]?.className).not.toContain('week-tick--sparse');
  });

  it('completedRecordedDays=0 时隐藏虚框参考线，提示"暂无稳定参考"（§9 P0-5）', () => {
    const points = [
      week('2026-07-06', '68000000'),
      week('2026-07-13', '59000000', true, 0, null),
    ];
    const { container } = render(<WeeklyChart points={points} weekProgress={progress} />);
    expect(container.querySelector('.chart__ref')).toBeNull();
    expect(screen.getByText('本周进行中，暂无稳定参考')).toBeInTheDocument();
  });

  it('当前周两段式：实心已完成 + 今日弱化（§9 P0-5）', () => {
    const points = [
      week('2026-07-06', '68000000'),
      week('2026-07-13', '59000000', true, 5, '9000000'),
    ];
    const { container } = render(<WeeklyChart points={points} weekProgress={progress} />);
    expect(container.querySelector('.week-bar--current')).not.toBeNull();
    expect(container.querySelector('.week-bar__completed')).not.toBeNull();
    expect(container.querySelector('.week-bar__today')).not.toBeNull();
  });

  it('两段式高度按当前周总量归一（P1-1 双缩放回归）：槽高=总量/max，段内按总量分摊', () => {
    // max=6800s；当前周总量 5900s → 槽高 87%；已完成 4500s → 76%，今日 1400s → 24%。
    const points = [
      week('2026-07-06', '68000000'),
      week('2026-07-13', '59000000', true, 5, '9000000'),
    ];
    const { container } = render(<WeeklyChart points={points} weekProgress={progress} />);
    const currentBar = container.querySelector('.week-bar--current') as HTMLElement;
    expect(currentBar.style.height).toBe('87%');
    const completed = container.querySelector('.week-bar__completed') as HTMLElement;
    const today = container.querySelector('.week-bar__today') as HTMLElement;
    expect(completed.style.height).toBe('76%');
    expect(today.style.height).toBe('24%');
    // 两段合计 ≈ 100%（无柱顶空档）
    expect(completed.style.height === '76%' && today.style.height === '24%').toBe(true);
  });

  it('纵轴纳入参考值（P2-01）：参考值高于历史最大周时柱按参考值缩放', () => {
    // 上周 40M、当前周 30M、参考值 = 9M×7 = 63M → max=63M：
    // 上周柱 40/63 = 63%、参考线底部 100%（真实相对高度，不截断）。
    const points = [
      week('2026-07-06', '40000000'),
      week('2026-07-13', '30000000', true, 5, '9000000'),
    ];
    const { container } = render(<WeeklyChart points={points} weekProgress={progress} />);
    const prev = Array.from(container.querySelectorAll('.week-bar'))[0] as HTMLElement;
    expect(prev.style.height).toBe('63%');
    const ref = container.querySelector('.chart__ref') as HTMLElement;
    expect(ref.style.bottom).toBe('100%');
    // 参考线接近顶部时标签移到线下（避免与标题重叠，图表区外溢）：
    // 100% 场景应带 --below 修饰。
    const label = container.querySelector('.chart__ref-label') as HTMLElement;
    expect(label.className).toContain('chart__ref-label--below');
  });

  it('参考线较低时标签保持线上方（默认定位，不与柱顶/标题冲突）', () => {
    // 上周 100M 高于参考值 63M → max=100M，refHeight = 63% < 80 → 线上方默认。
    const points = [
      week('2026-07-06', '100000000'),
      week('2026-07-13', '30000000', true, 5, '9000000'),
    ];
    const { container } = render(<WeeklyChart points={points} weekProgress={progress} />);
    const label = container.querySelector('.chart__ref-label') as HTMLElement;
    expect(label.className).not.toContain('chart__ref-label--below');
  });

  it('历史周柱体 inline 高度 = 值/max（P1-01：不会退化成 2px）', () => {
    const points = [
      week('2026-07-06', '68000000'),
      week('2026-07-13', '59000000', true, 5, '9000000'),
    ];
    const { container } = render(<WeeklyChart points={points} weekProgress={progress} />);
    const prev = container.querySelector('.week-bar:not(.week-bar--current)') as HTMLElement;
    expect(prev.style.height).toBe('100%'); // 68M 是最大（含参考值 63M 后仍最大）
  });

  it('completedRecordedDays=0 时整柱为今日弱化（P1-2 退化回归）', () => {
    const points = [
      week('2026-07-06', '68000000'),
      week('2026-07-13', '59000000', true, 0, null),
    ];
    const { container } = render(<WeeklyChart points={points} weekProgress={progress} />);
    const today = container.querySelector('.week-bar__today') as HTMLElement;
    expect(today.style.height).toBe('100%');
    expect(container.querySelector('.week-bar__completed')).toBeNull();
  });
});

describe('WeekProgressCard 本周进度', () => {
  it('本周总量 + 上周同期比较 + 记录天数', () => {
    render(<WeekProgressCard weekProgress={progress} />);
    expect(screen.getByText('本周累计')).toBeInTheDocument();
    expect(screen.getByText('16h23m')).toBeInTheDocument();
    expect(screen.getByText('较上周同期')).toBeInTheDocument();
    expect(screen.getByText('▲ +7%')).toBeInTheDocument();
    expect(screen.getByText('记录 6 天')).toBeInTheDocument();
  });

  it('上周无数据（noData）→ 隐藏比较行', () => {
    render(
      <WeekProgressCard
        weekProgress={{
          ...progress,
          lastWeekSame: {
            activeDurationMs: null,
            deltaPercent: null,
            direction: 'unavailable',
            sampleDays: 1,
            unavailableReason: 'noData',
          },
        }}
      />,
    );
    expect(screen.queryByText(/较上周同期/)).not.toBeInTheDocument();
  });
});
