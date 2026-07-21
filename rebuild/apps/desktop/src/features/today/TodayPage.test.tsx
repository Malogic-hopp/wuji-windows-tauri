import { render, screen, waitFor } from '@testing-library/react';
import { vi } from 'vitest';
import TodayPage from './TodayPage';
import type { TodayDto } from '../../types/wuji-core';

const invoke = vi.fn<(command: string, args?: unknown) => Promise<unknown>>();
vi.mock('@tauri-apps/api/core', () => ({
  invoke: (command: string, args?: unknown): Promise<unknown> => invoke(command, args),
}));

function todayFixture(overrides: Partial<TodayDto> = {}): TodayDto {
  return {
    localDate: '2026-07-19',
    reportingTimeZoneId: 'Asia/Shanghai',
    activeDurationMs: '3723000',
    currentApp: { appId: '1', displayName: 'Code' },
    lastApp: { appId: '2', displayName: 'Edge' },
    longestWorkBlockActiveMs: '1800000',
    workBlockCount: '2',
    rawAppSwitchCount: '5',
    topApps: [
      { app: { appId: '1', displayName: 'Code' }, activeDurationMs: '3000000' },
      { app: { appId: '2', displayName: 'Edge' }, activeDurationMs: '723000' },
    ],
    quality: { isComplete: true, gapCount: '0', droppedCount: '0' },
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
        quality: { isComplete: false, gapCount: '2', droppedCount: '1' },
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
        activeDurationMs: '0',
        workBlockCount: '0',
        rawAppSwitchCount: '0',
        longestWorkBlockActiveMs: '0',
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
