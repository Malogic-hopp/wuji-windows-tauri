import { render, screen, waitFor } from '@testing-library/react';
import { vi } from 'vitest';
import TodayPage from './TodayPage';
import type { TodayDto } from '../../types/wuji-core';
import type { Int64String } from '../../types/wuji-core';

/** Int64String 夹具断言（R07 品牌类型）。 */
const i64 = (text: string): Int64String => text as Int64String;

const invoke = vi.fn<(command: string, args?: unknown) => Promise<unknown>>();
vi.mock('@tauri-apps/api/core', () => ({
  invoke: (command: string, args?: unknown): Promise<unknown> => invoke(command, args),
}));

function todayFixture(overrides: Partial<TodayDto> = {}): TodayDto {
  return {
    localDate: '2026-07-19',
    reportingTimeZoneId: 'Asia/Shanghai',
    activeDurationMs: i64('3723000'),
    currentApp: { appId: i64('1'), displayName: 'Code' },
    lastApp: { appId: i64('2'), displayName: 'Edge' },
    longestWorkBlockActiveMs: i64('1800000'),
    workBlockCount: i64('2'),
    rawAppSwitchCount: i64('5'),
    topApps: [
      { app: { appId: i64('1'), displayName: 'Code' }, activeDurationMs: i64('3000000') },
      { app: { appId: i64('2'), displayName: 'Edge' }, activeDurationMs: i64('723000') },
    ],
    quality: { isComplete: true, gapCount: i64('0'), droppedCount: i64('0') },
    ...overrides,
  };
}

describe('Today 页面', () => {
  beforeEach(() => {
    invoke.mockReset();
  });

  it('Ready 状态展示指标、Top Apps 与当前/最近应用', async () => {
    invoke.mockResolvedValue(todayFixture());
    render(<TodayPage />);
    await waitFor(() => {
      expect(screen.getByText('1 小时 2 分钟')).toBeInTheDocument();
    });
    expect(screen.getAllByText('Code')).toHaveLength(2);
    expect(screen.getAllByText('Edge')).toHaveLength(2);
    expect(screen.getByText('30 分钟')).toBeInTheDocument();
    expect(screen.queryByRole('note')).not.toBeInTheDocument();
  });

  it('数据不完整时显示 gap/drop 提示', async () => {
    invoke.mockResolvedValue(
      todayFixture({
        quality: { isComplete: false, gapCount: i64('2'), droppedCount: i64('1') },
      }),
    );
    render(<TodayPage />);
    await waitFor(() => {
      expect(screen.getByRole('note')).toHaveTextContent('今日数据不完整');
    });
  });

  it('无记录时显示 Empty 四态', async () => {
    invoke.mockResolvedValue(
      todayFixture({
        activeDurationMs: i64('0'),
        workBlockCount: i64('0'),
        rawAppSwitchCount: i64('0'),
        longestWorkBlockActiveMs: i64('0'),
        currentApp: null,
        lastApp: null,
        topApps: [],
      }),
    );
    render(<TodayPage />);
    await waitFor(() => {
      expect(screen.getByText('今天还没有记录')).toBeInTheDocument();
    });
  });

  it('命令失败显示 Error 四态并可重试', async () => {
    invoke.mockRejectedValue({ code: 'DB_UNAVAILABLE', message: '数据库不可用' });
    render(<TodayPage />);
    await waitFor(() => {
      expect(screen.getByRole('alert')).toHaveTextContent('数据库不可用');
    });
    invoke.mockResolvedValue(todayFixture());
    const retry = screen.getByRole('button', { name: '重试' });
    retry.click();
    await waitFor(() => {
      expect(screen.getByText('1 小时 2 分钟')).toBeInTheDocument();
    });
  });
});
