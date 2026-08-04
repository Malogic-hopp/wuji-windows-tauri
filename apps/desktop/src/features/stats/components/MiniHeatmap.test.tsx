import { render } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { MiniHeatmap } from './MiniHeatmap';
import type { HeatmapCellDto, HeatmapDto } from '../../../types/wuji-core';
import { i64 } from '../statsFixture';

function cell(
  localDate: string,
  localHour: number,
  level: number,
): HeatmapCellDto {
  return {
    localDate,
    localHour,
    activeDurationMs: i64('0'),
    idleDurationMs: i64('0'),
    unknownDurationMs: i64('0'),
    intensityLevel: level,
  };
}

/** 7 天迷你热力图：仅今日（最后一列）有数据，10 点 level 4、其余 level 0。
 *  其余日期由 buildGrid 自动补零值（createZeroCell）。 */
const heatmapFixture: HeatmapDto = {
  today: '2026-07-18',
  rangeEndLocalDate: '2026-07-18',
  reportingTimeZoneId: 'Asia/Shanghai',
  days: 7,
  cells: Array.from({ length: 24 }, (_, h) =>
    cell('2026-07-18', h, h === 10 ? 4 : 0),
  )
    .map((c, h) => ({ ...c, activeDurationMs: h === 10 ? i64('13320000') : i64('0') })),
};

describe('MiniHeatmap 主页缩小版热力图', () => {
  it('渲染 24 行 × N 天格子，强度 class 与今天列当前小时描边', () => {
    const { container } = render(<MiniHeatmap heatmap={heatmapFixture} />);
    const cells = container.querySelectorAll('.mini-heatmap__cell');
    // 7 天 × 24 小时 = 168 格
    expect(cells.length).toBe(168);
    // 区块级 aria 概括范围
    expect(container.querySelector('figure')?.getAttribute('aria-label')).toContain(
      '近 7 天活跃热力图',
    );
    // level 4 格子（今天 10 点）与 level 0 格子并存
    expect(container.querySelector('.heatmap-level--4')).not.toBeNull();
    expect(container.querySelector('.heatmap-level--0')).not.toBeNull();
    // 今天列当前小时描边（仅 1 格，不是整列）
    expect(container.querySelectorAll('.mini-heatmap__cell--today').length).toBe(1);
    // 格子 tooltip：10 时格含活跃时长与强度，其余零值格为无记录（getCellLabel 复用）
    expect(container.querySelector('[title*="10时"]')).not.toBeNull();
    expect(container.querySelector('[title*="活跃 3 小时 42 分钟"]')).not.toBeNull();
    expect(container.querySelectorAll('[title*="无记录"]').length).toBeGreaterThan(0);
    // 图例少→多
    expect(container.querySelector('.mini-heatmap__legend')).not.toBeNull();
  });

  it('时段标签与分界线（与原热力图一致）：3/9/15/21 点时段名、5/11/17 点行下分界线', () => {
    const { container } = render(<MiniHeatmap heatmap={heatmapFixture} />);
    const lines = container.querySelectorAll('.mini-heatmap__line');
    expect(lines.length).toBe(24);
    // 时段标签：凌晨（3）、上午（9）、下午（15）、晚上（21）各一次
    const timeLabels = Array.from(container.querySelectorAll('.mini-heatmap__time')).map(
      (s) => s.textContent,
    );
    expect(timeLabels[3]).toBe('凌晨');
    expect(timeLabels[9]).toBe('上午');
    expect(timeLabels[15]).toBe('下午');
    expect(timeLabels[21]).toBe('晚上');
    // 分界线：hour 5/11/17 三行下方
    expect(container.querySelectorAll('.mini-heatmap__line--divider').length).toBe(3);
  });
});
