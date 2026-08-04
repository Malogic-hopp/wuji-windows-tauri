import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { AppComposition } from './AppComposition';
import { i64 } from '../statsFixture';
import type { AppPaletteEntryDto, CompositionBucketDto } from '../../../types/wuji-core';

const palette: AppPaletteEntryDto[] = [
  { app: { appId: i64('1'), displayName: 'VS Code' }, slot: 0 },
  { app: { appId: i64('2'), displayName: '浏览器' }, slot: 1 },
  { app: { appId: i64('3'), displayName: '终端' }, slot: 2 },
];

const dayBucket = (date: string, isCurrent: boolean, hasData: boolean): CompositionBucketDto => ({
  startDate: date,
  endDate: date,
  bucketKind: 'day',
  isCurrent,
  hasData,
  apps: [
    { app: { appId: i64('1'), displayName: 'VS Code' }, activeDurationMs: i64('8400000') },
    { app: { appId: i64('2'), displayName: '浏览器' }, activeDurationMs: i64('3000000') },
  ],
  othersActiveMs: i64('1200000'),
});

describe('AppComposition 应用构成', () => {
  it('日桶横向堆叠：槽位色段 + 其他 + isCurrent 弱化只作用于堆叠条', () => {
    const { container } = render(
      <AppComposition
        buckets={[dayBucket('2026-07-17', false, true), dayBucket('2026-07-18', true, true)]}
        palette={palette}
      />,
    );
    expect(container.querySelectorAll('.comp-seg').length).toBeGreaterThan(0);
    // isCurrent 桶弱化（data-current 属性驱动 CSS opacity，只作用于 stack）
    expect(container.querySelector('.comp-row[data-current]')).not.toBeNull();
    expect(container.querySelector('.comp-row[data-current] .comp-row__stack')).not.toBeNull();
    // 图例含 palette 应用与"其他"
    expect(screen.getByText('VS Code')).toBeInTheDocument();
    expect(screen.getByText('其他')).toBeInTheDocument();
    // 段 aria-label 携带应用与时长
    expect(screen.getAllByLabelText('VS Code 2h20m').length).toBeGreaterThan(0);
    // 日期标签旁的当日总时长（apps + others = 12600000ms → 3h30m），两行均有
    expect(screen.getAllByText('3h30m').length).toBe(2);
  });

  it('缺数据桶：可见斜纹占位 + 桶级 aria/焦点（P1-02）', () => {
    const noDataBucket: CompositionBucketDto = {
      ...dayBucket('2026-07-11', false, false),
      apps: [],
      othersActiveMs: i64('0'),
    };
    const currentBucket: CompositionBucketDto = dayBucket('2026-07-18', true, true);
    const { container } = render(
      <AppComposition
        buckets={[noDataBucket, currentBucket]}
        palette={palette}
        cutoffLocalTime="15:20"
      />,
    );
    // 缺数据占位斜纹段可见
    expect(container.querySelector('.comp-seg--nodata')).not.toBeNull();
    // 桶级 aria 携带进行中/无数据语义，且可聚焦
    const buckets = Array.from(container.querySelectorAll('.comp-row'));
    expect(buckets.length).toBe(2);
    const noDataEl = buckets[0] as HTMLElement;
    expect(noDataEl.getAttribute('tabindex')).toBe('0');
    expect(noDataEl.getAttribute('aria-label')).toContain('当日无记录数据');
    const currentEl = buckets[1] as HTMLElement;
    expect(currentEl.getAttribute('aria-label')).toContain('进行中（截至 15:20）');
    // 悬停 title 也带截至时刻
    expect(currentEl.getAttribute('title')).toBe('进行中（截至 15:20）');
    // 缺数据日总时长显示 "—"；当前行显示 3h30m（review-2：强化两种状态区别）
    const totals = Array.from(container.querySelectorAll('.comp-row__total'));
    expect(totals.length).toBe(2);
    expect(totals[0]?.textContent).toBe('—');
    expect(totals[1]?.textContent).toBe('3h30m');
  });

  it('周桶纵向堆叠；空桶显示空状态提示', () => {
    const weekBucket: CompositionBucketDto = {
      startDate: '2026-07-13',
      endDate: '2026-07-18',
      bucketKind: 'week',
      isCurrent: true,
      hasData: true,
      apps: [{ app: { appId: i64('1'), displayName: 'VS Code' }, activeDurationMs: i64('16000000') }],
      othersActiveMs: i64('600000'),
    };
    const { container } = render(<AppComposition buckets={[weekBucket]} palette={palette} />);
    expect(container.querySelector('.comp-col__stack')).not.toBeNull();
    expect(container.querySelector('.comp-row')).toBeNull();
    // 周桶标签只显示日期范围，不加总时长
    expect(screen.getByText('07-13–07-18')).toBeInTheDocument();
    expect(container.querySelector('.comp-row__total')).toBeNull();
    // 空桶
    const empty = render(<AppComposition buckets={[]} palette={[]} />);
    expect(empty.getByText('暂无应用构成数据')).toBeInTheDocument();
  });
});
