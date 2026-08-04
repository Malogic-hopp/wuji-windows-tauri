import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { InertiaCurve } from './InertiaCurve';
import { i64, inertiaAllZeroFixture, inertiaUnreliableFixture } from '../statsFixture';
import type { HourlyPointDto } from '../../../types/wuji-core';

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

describe('InertiaCurve 工作惯性', () => {
  it('正常曲线：峰值/开工/收工/午休低谷标注 + 有效天数 + 可靠性', () => {
    const { container } = render(
      <InertiaCurve
        points={hours(normalActives)}
        inertia={{
          startHour: 9,
          peakHour: 10,
          endHour: 19,
          lunchLowestHour: 12,
          effectiveDays: 11,
          totalDays: 14,
          reliability: 'normal',
        }}
      />,
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

  it('reliability null（有效日 < 3）→ 不画曲线，提示不足', () => {
    const { container } = render(
      <InertiaCurve points={hours(normalActives)} inertia={inertiaUnreliableFixture()} />,
    );
    expect(screen.getByText('有效记录日不足，无法显示工作惯性')).toBeInTheDocument();
    expect(container.querySelector('.chart__inertia-svg')).toBeNull();
  });

  it('全零曲线 → 不标注（不得伪造开工/收工），但有效天数仍显示', () => {
    const { container } = render(
      <InertiaCurve points={hours(Array.from({ length: 24 }, () => 0))} inertia={inertiaAllZeroFixture()} />,
    );
    expect(screen.queryByText(/开工约/)).not.toBeInTheDocument();
    expect(screen.queryByText(/高峰/)).not.toBeInTheDocument();
    expect(container.querySelector('.chart__inertia-svg')).not.toBeNull();
    expect(screen.getByText('有效样本日 11/14（3 天未纳入）')).toBeInTheDocument();
    // 小时刻度
    expect(container.querySelector('.chart__inertia-ticks')).not.toBeNull();
  });
});
