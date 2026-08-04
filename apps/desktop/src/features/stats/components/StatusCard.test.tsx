import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { StatusCard } from './StatusCard';
import { comparisonFixture, i64, liveStatusFixture } from '../statsFixture';
import type { LiveStatusDto } from '../../../types/wuji-core';

function live(overrides: Partial<LiveStatusDto> = {}): LiveStatusDto {
  return { ...liveStatusFixture, ...overrides };
}

describe('StatusCard 状态摘要主卡', () => {
  it('信息层级：今日活跃标签 + 大号紧凑数字 + 工作块 + 截至时刻 + 摘要双窗口句式', () => {
    render(
      <StatusCard
        live={live()}
        summary={{ direction: 'upSlight', primaryPeriod: 'morning' }}
      />,
    );
    expect(screen.getByText('今日活跃')).toBeInTheDocument();
    // 大号核心数字用紧凑格式；完整语义保留在 aria-label
    expect(screen.getByText('3h42m')).toBeInTheDocument();
    expect(screen.getByLabelText('今日截至 15:20 活跃 3 小时 42 分钟')).toBeInTheDocument();
    expect(screen.getByText(/8 个工作块/)).toBeInTheDocument();
    expect(screen.getByText(/截至 15:20/)).toBeInTheDocument();
    expect(screen.getByText('最近 7 日日均活跃略有上升；通常主要活跃在上午')).toBeInTheDocument();
  });

  it('昨日/近 7 日比较：▲ +8% 与 ▼ -12%（基于样本数）', () => {
    render(
      <StatusCard live={live()} summary={{ direction: null, primaryPeriod: null }} />,
    );
    expect(screen.getByText(/较昨日同时刻/)).toBeInTheDocument();
    expect(screen.getByText('▲ +8%')).toBeInTheDocument();
    expect(screen.getByText('▼ -12%')).toBeInTheDocument();
    expect(screen.getByText(/基于 7 个有效日/)).toBeInTheDocument();
  });

  it('昨日 noData → 隐藏比较行；近 7 日样本不足 → 历史样本不足', () => {
    render(
      <StatusCard
        live={live({
          yesterdaySame: comparisonFixture({
            direction: 'unavailable',
            deltaPercent: null,
            activeDurationMs: null,
            unavailableReason: 'noData',
          }),
          last7AvgSame: comparisonFixture({
            direction: 'unavailable',
            deltaPercent: null,
            activeDurationMs: null,
            sampleDays: 2,
            unavailableReason: 'insufficientSamples',
          }),
        })}
        summary={{ direction: null, primaryPeriod: null }}
      />,
    );
    expect(screen.queryByText(/较昨日同时刻/)).not.toBeInTheDocument();
    expect(screen.getByText('历史样本不足')).toBeInTheDocument();
  });

  it('upFromZero：新增 N 分钟（当前值即新增量）', () => {
    render(
      <StatusCard
        live={live({
          todayActiveMs: i64('720000'),
          yesterdaySame: comparisonFixture({
            direction: 'upFromZero',
            deltaPercent: null,
            activeDurationMs: i64('0'),
          }),
        })}
        summary={{ direction: null, primaryPeriod: null }}
      />,
    );
    expect(screen.getByText('新增 12m')).toBeInTheDocument();
  });
});
