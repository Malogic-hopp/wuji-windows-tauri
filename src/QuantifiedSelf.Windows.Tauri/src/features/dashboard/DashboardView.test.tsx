import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import type { ActivityOverviewResult } from '../../bridge/contracts';
import { DashboardView } from './DashboardView';

const overview: ActivityOverviewResult = {
  summary: {
    dateUtc: '2026-07-16',
    totalDurationSeconds: 7_200,
    activeDurationSeconds: 5_400,
    idleDurationSeconds: 1_200,
    unknownDurationSeconds: 600,
    sessionCount: 4,
  },
  topApps: [{
    displayName: 'Visual Studio Code',
    totalDurationSeconds: 3_600,
    activeDurationSeconds: 3_000,
    idleDurationSeconds: 600,
    unknownDurationSeconds: 0,
    sessionCount: 2,
    lastUsedAtUtc: '2026-07-16T08:00:00Z',
  }],
  recentSessions: [{
    displayName: 'Visual Studio Code',
    startedAtUtc: '2026-07-16T07:00:00Z',
    endedAtUtc: '2026-07-16T08:00:00Z',
    totalDurationSeconds: 3_600,
    activeDurationSeconds: 3_000,
    idleDurationSeconds: 600,
    unknownDurationSeconds: 0,
  }],
};

describe('DashboardView', () => {
  it('Loading 使用可读的忙碌状态', () => {
    render(<DashboardView state={{ kind: 'loading' }} refreshing onRefresh={vi.fn()} locale="zh-CN" />);

    const heading = screen.getByRole('heading', { name: '正在读取今日活动' });
    expect(heading.closest('section')).toHaveAttribute('role', 'status');
    expect(heading.closest('section')).toHaveAttribute('aria-busy', 'true');
  });

  it('Empty 说明无数据并提供原生键盘按钮', () => {
    const refresh = vi.fn();
    render(
      <DashboardView
        state={{ kind: 'empty', updatedAt: Date.now() }}
        refreshing={false}
        onRefresh={refresh}
        locale="zh-CN"
      />,
    );

    expect(screen.getByRole('heading', { name: '今天还没有活动记录' })).toBeInTheDocument();
    const button = screen.getByRole('button', { name: '再检查一次' });
    button.focus();
    expect(button).toHaveFocus();
    fireEvent.click(button);
    expect(refresh).toHaveBeenCalledOnce();
  });

  it('Ready 呈现摘要、Top Apps、最近会话和手动刷新', () => {
    const refresh = vi.fn();
    render(
      <DashboardView
        state={{ kind: 'ready', overview, updatedAt: Date.now() }}
        refreshing={false}
        onRefresh={refresh}
        locale="zh-CN"
      />,
    );

    expect(screen.getByRole('heading', { name: '今日有效使用时长' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: '今日采样 / 会话摘要' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Top Apps' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: '最近活动会话' })).toBeInTheDocument();
    expect(screen.getAllByText('Visual Studio Code').length).toBeGreaterThan(0);
    fireEvent.click(screen.getByRole('button', { name: '刷新' }));
    expect(refresh).toHaveBeenCalledOnce();
  });

  it('Error 使用 alert、显示安全消息并支持重试', () => {
    const retry = vi.fn();
    render(
      <DashboardView
        state={{ kind: 'error', message: '无法连接本地服务，请稍后重试。' }}
        refreshing={false}
        onRefresh={retry}
        locale="zh-CN"
      />,
    );

    expect(screen.getByRole('alert')).toHaveTextContent('无法连接本地服务');
    expect(screen.getByRole('alert')).toHaveTextContent('Agent 会保持原状态运行');
    fireEvent.click(screen.getByRole('button', { name: '重试' }));
    expect(retry).toHaveBeenCalledOnce();
  });
});
