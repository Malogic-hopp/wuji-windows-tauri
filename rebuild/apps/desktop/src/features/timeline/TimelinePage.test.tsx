import { render, screen, waitFor } from '@testing-library/react';
import { vi } from 'vitest';
import TimelinePage from './TimelinePage';
import type { TimelinePageDto } from '../../types/wuji-core';

const invoke = vi.fn<(command: string, args?: unknown) => Promise<unknown>>();
vi.mock('@tauri-apps/api/core', () => ({
  invoke: (command: string, args?: unknown): Promise<unknown> => invoke(command, args),
}));

const TZ = 'Asia/Shanghai';

function pageFixture(items: TimelinePageDto['items'], nextCursor: string | null): TimelinePageDto {
  return { localDate: '2026-07-19', reportingTimeZoneId: TZ, items, nextCursor };
}

describe('Timeline 页面', () => {
  beforeEach(() => {
    invoke.mockReset();
  });

  it('展示 Segment 状态徽章与时长，默认折叠切换间隔', async () => {
    invoke.mockResolvedValue(
      pageFixture(
        [
          {
            kind: 'segment',
            segmentId: '1',
            app: { appId: '1', displayName: 'Code' },
            activityState: 'active',
            startAtUtcMs: '1784300000000',
            endAtUtcMs: '1784303600000',
            durationMs: '3600000',
            status: 'closed',
          },
          {
            kind: 'gap',
            gapId: '10',
            gapKind: 'sampling_transition',
            startAtUtcMs: '1784303600000',
            endAtUtcMs: '1784303603000',
            status: 'closed',
            eventCount: 1,
          },
          {
            kind: 'segment',
            segmentId: '2',
            app: { appId: '2', displayName: 'Edge' },
            activityState: 'idle',
            startAtUtcMs: '1784303603000',
            endAtUtcMs: '1784303903000',
            durationMs: '300000',
            status: 'closed',
          },
          {
            kind: 'gap',
            gapId: '11',
            gapKind: 'capture_paused',
            startAtUtcMs: '1784303903000',
            endAtUtcMs: '1784311103000',
            status: 'closed',
            eventCount: 1,
          },
        ],
        null,
      ),
    );
    render(<TimelinePage />);
    await waitFor(() => {
      expect(screen.getByText('Code')).toBeInTheDocument();
    });
    expect(screen.getByText('活跃')).toBeInTheDocument();
    expect(screen.getByText('空闲')).toBeInTheDocument();
    expect(screen.getByText('已暂停')).toBeInTheDocument();
    expect(screen.queryByText('— 切换间隔 —')).not.toBeInTheDocument();
  });

  it('勾选后显示切换间隔', async () => {
    invoke.mockResolvedValue(
      pageFixture(
        [
          {
            kind: 'gap',
            gapId: '10',
            gapKind: 'sampling_transition',
            startAtUtcMs: '1784303600000',
            endAtUtcMs: '1784303603000',
            status: 'closed',
            eventCount: 1,
          },
        ],
        null,
      ),
    );
    render(<TimelinePage />);
    await waitFor(() => {
      expect(screen.getByRole('checkbox')).toBeInTheDocument();
    });
    screen.getByRole('checkbox').click();
    await waitFor(() => {
      expect(screen.getByText('— 切换间隔 —')).toBeInTheDocument();
    });
  });

  it('nextCursor 存在时加载更多并追加条目', async () => {
    invoke
      .mockResolvedValueOnce(
        pageFixture(
          [
            {
              kind: 'segment',
              segmentId: '1',
              app: { appId: '1', displayName: 'Code' },
              activityState: 'active',
              startAtUtcMs: '1784300000000',
              endAtUtcMs: '1784303600000',
              durationMs: '3600000',
              status: 'closed',
            },
          ],
          'cursor-1',
        ),
      )
      .mockResolvedValueOnce(
        pageFixture(
          [
            {
              kind: 'segment',
              segmentId: '2',
              app: { appId: '2', displayName: 'Edge' },
              activityState: 'active',
              startAtUtcMs: '1784303603000',
              endAtUtcMs: '1784307203000',
              durationMs: '3600000',
              status: 'closed',
            },
          ],
          null,
        ),
      );
    render(<TimelinePage />);
    await waitFor(() => {
      expect(screen.getByText('Code')).toBeInTheDocument();
    });
    screen.getByRole('button', { name: '加载更多' }).click();
    await waitFor(() => {
      expect(screen.getByText('Edge')).toBeInTheDocument();
    });
    expect(screen.getByText('已显示全部')).toBeInTheDocument();
    expect(invoke).toHaveBeenLastCalledWith('activity_get_timeline', {
      localDate: '2026-07-19',
      cursor: 'cursor-1',
      limit: 50,
    });
  });

  it('无条目时显示 Empty 四态', async () => {
    invoke.mockResolvedValue(pageFixture([], null));
    render(<TimelinePage />);
    await waitFor(() => {
      expect(screen.getByText('今天还没有时间线记录')).toBeInTheDocument();
    });
  });
});
