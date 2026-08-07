import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { InertiaCurve } from './InertiaCurve';
import { i64, inertiaAllZeroFixture, inertiaUnreliableFixture, workPaceFixture } from '../statsFixture';
import type { HourlyPointDto, InertiaDto } from '../../../types/wuji-core';

const hours = (actives: number[]): HourlyPointDto[] =>
  Array.from({ length: 24 }, (_, hour) => ({
    localHour: hour,
    avgActiveMs: i64(String(actives[hour] ?? 0)),
  }));

const normalActives = Array.from({ length: 24 }, () => 0);
normalActives[9] = 3300000;
normalActives[10] = 2800000;
normalActives[12] = 300000;
normalActives[15] = 1500000;

const normalInertia: InertiaDto = {
  startHour: 9,
  peakHour: 10,
  endHour: 19,
  lunchLowestHour: 12,
  effectiveDays: 11,
  totalDays: 14,
  reliability: 'normal',
};

describe('InertiaCurve 工作惯性', () => {
  it('正常曲线：峰值/开工/收工/午休低谷标注 + 有效天数 + 可靠性', () => {
    const { container } = render(
      <InertiaCurve points={hours(normalActives)} inertia={normalInertia} workPace={workPaceFixture()} />,
    );
    expect(screen.getByText('开工约 9:00')).toBeInTheDocument();
    expect(screen.getByText('高峰 10–11 点')).toBeInTheDocument();
    expect(screen.getByText('收工约 19:00')).toBeInTheDocument();
    expect(screen.getByText('午休低谷 12 点')).toBeInTheDocument();
    expect(screen.getByText('有效样本日 11/14（3 天未纳入）')).toBeInTheDocument();
    // 设计只定义"初步模式"（3-6 天）；normal（≥7 天）不标注
    expect(screen.queryByText('正常模式')).not.toBeInTheDocument();
    // 底部为紧凑信息条：不再使用图例色块（legend-chip）
    expect(container.querySelector('.inertia-info')).not.toBeNull();
    expect(container.querySelector('.chart--inertia .legend-chip')).toBeNull();
  });

  it('工作节奏：占比条（未工作段着色）+ 常见工作时段 + 上午利用率', () => {
    const { container } = render(
      <InertiaCurve points={hours(normalActives)} inertia={normalInertia} workPace={workPaceFixture()} />,
    );
    // 无浅黄色在工位叠加（用户明确不要）
    expect(container.querySelectorAll('.inertia-cover-band').length).toBe(0);
    // 占比条：工作 35%（日均 8h24m）· 未工作 65%；未工作段由 bar 背景（浅色）填充
    expect(screen.getByText(/工作 35%（日均 8h24m）· 未工作 65%/)).toBeInTheDocument();
    const bar = container.querySelector('.inertia-pace__bar');
    expect(bar).not.toBeNull();
    expect(container.querySelector('.inertia-pace__work')).not.toBeNull();
    // 常见工作时段 + 上午利用率
    expect(
      screen.getByText('常见工作时段 16:18–23:02 · 上午(8-12点)有工作 0/11 天'),
    ).toBeInTheDocument();
  });

  it('reliability null（有效日 < 3）→ 不画曲线，提示不足', () => {
    const { container } = render(
      <InertiaCurve points={hours(normalActives)} inertia={inertiaUnreliableFixture()} workPace={workPaceFixture()} />,
    );
    expect(screen.getByText('有效记录日不足，无法显示工作惯性')).toBeInTheDocument();
    expect(container.querySelector('.chart__inertia-svg')).toBeNull();
  });

  it('全零曲线 → 不标注（不得伪造开工/收工），但有效天数仍显示', () => {
    const { container } = render(
      <InertiaCurve points={hours(Array.from({ length: 24 }, () => 0))} inertia={inertiaAllZeroFixture()} workPace={workPaceFixture()} />,
    );
    expect(screen.queryByText(/开工约/)).not.toBeInTheDocument();
    expect(screen.queryByText(/高峰/)).not.toBeInTheDocument();
    expect(container.querySelector('.chart__inertia-svg')).not.toBeNull();
    expect(screen.getByText('有效样本日 11/14（3 天未纳入）')).toBeInTheDocument();
    // 小时刻度
    expect(container.querySelector('.chart__inertia-ticks')).not.toBeNull();
  });

  it('小时刻度对齐柱体中心（0 不在容器左缘、23 对应最后一根柱）', () => {
    const { container } = render(<InertiaCurve points={hours(normalActives)} inertia={inertiaAllZeroFixture()} workPace={workPaceFixture()} />);
    const ticks = Array.from(container.querySelectorAll('.chart__inertia-ticks span'));
    const lefts = ticks.map((s) => Number((s as HTMLElement).style.left.replace('%', '')));
    // 0 点刻度在首柱中心（约 2.08%），不在 0%；23 点对齐末柱中心（约 97.9%），不在 100%
    expect(lefts[0]).toBeGreaterThan(1.5);
    expect(lefts[0]).toBeLessThan(3);
    expect(lefts[1]).toBeGreaterThan(26);
    expect(lefts[1]).toBeLessThan(28); // 6 点刻度 = 6.5/24 ≈ 27.1%
    expect(ticks[4]?.textContent).toBe('23');
    expect(lefts[4]).toBeGreaterThan(96);
    expect(lefts[4]).toBeLessThan(99); // 23 点 = 23.5/24 ≈ 97.9%
  });
});
