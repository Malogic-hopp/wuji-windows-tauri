import { render, screen, waitFor } from '@testing-library/react';
import { vi } from 'vitest';
import TimelinePage from './TimelinePage';
import type { TimelinePageDto } from '../../types/wuji-core';
import type { Int64String } from '../../types/wuji-core';

/** Int64String 夹具断言（R07 品牌类型）。 */
const i64 = (text: string): Int64String => text as Int64String;

const invoke = vi.fn<(command: string, args?: unknown) => Promise<unknown>>();
vi.mock('@tauri-apps/api/core', () => ({
  invoke: (command: string, args?: unknown): Promise<unknown> => invoke(command, args),
}));

const TZ = 'Asia/Shanghai';
/** 数据库 reporting 时区下的"今天"（与浏览器本地日期无关，R08）。 */
const TODAY_LOCAL_DATE = '2026-07-18';

function pageFixture(items: TimelinePageDto['items'], nextCursor: string | null): TimelinePageDto {
  return { localDate: '2026-07-19', reportingTimeZoneId: TZ, items, nextCursor };
}

/** 按命令路由：activity_get_today 提供 DB reporting 日期，activity_get_timeline 依序返回页。 */
function mockRoutes(pages: TimelinePageDto[]) {
  let index = 0;
  invoke.mockImplementation((command: string) => {
    if (command === 'activity_get_today') {
      return Promise.resolve({ localDate: TODAY_LOCAL_DATE });
    }
    if (command === 'activity_get_timeline') {
      return Promise.resolve(pages[Math.min(index++, pages.length - 1)]);
    }
    return Promise.reject(new Error(`unexpected command: ${command}`));
  });
}

describe('Timeline 页面', () => {
  beforeEach(() => {
    invoke.mockReset();
  });

  it('以 DB reporting 时区的日期查询时间线，展示徽章与时长，默认折叠切换间隔', async () => {
    mockRoutes([
      pageFixture(
        [
          {
            kind: 'segment',
            segmentId: i64('1'),
            app: { appId: i64('1'), displayName: 'Code' },
            activityState: 'active',
            startAtUtcMs: i64('1784300000000'),
            endAtUtcMs: i64('1784303600000'),
            durationMs: i64('3600000'),
            status: 'closed',
          },
          {
            kind: 'gap',
            gapId: i64('10'),
            gapKind: 'sampling_transition',
            startAtUtcMs: i64('1784303600000'),
            endAtUtcMs: i64('1784303603000'),
            status: 'closed',
            eventCount: 1,
          },
          {
            kind: 'segment',
            segmentId: i64('2'),
            app: { appId: i64('2'), displayName: 'Edge' },
            activityState: 'idle',
            startAtUtcMs: i64('1784303603000'),
            endAtUtcMs: i64('1784303903000'),
            durationMs: i64('300000'),
            status: 'closed',
          },
          {
            kind: 'gap',
            gapId: i64('11'),
            gapKind: 'capture_paused',
            startAtUtcMs: i64('1784303903000'),
            endAtUtcMs: i64('1784311103000'),
            status: 'closed',
            eventCount: 1,
          },
        ],
        null,
      ),
    ]);
    render(<TimelinePage />);
    await waitFor(() => {
      expect(screen.getByText('Code')).toBeInTheDocument();
    });
    // R08：首次查询必须使用 activity_get_today 的 localDate（DB reporting 时区），
    // 不得使用浏览器本地日期。
    expect(invoke).toHaveBeenCalledWith('activity_get_timeline', {
      localDate: TODAY_LOCAL_DATE,
      cursor: null,
      limit: 50,
    });
    expect(screen.getByText('活跃')).toBeInTheDocument();
    expect(screen.getByText('空闲')).toBeInTheDocument();
    expect(screen.getByText('已暂停')).toBeInTheDocument();
    expect(screen.queryByText('— 切换间隔 —')).not.toBeInTheDocument();
  });

  it('勾选后显示切换间隔，且切换间隔可被辅助技术感知', async () => {
    mockRoutes([
      pageFixture(
        [
          {
            kind: 'gap',
            gapId: i64('10'),
            gapKind: 'sampling_transition',
            startAtUtcMs: i64('1784303600000'),
            endAtUtcMs: i64('1784303603000'),
            status: 'closed',
            eventCount: 1,
          },
        ],
        null,
      ),
    ]);
    render(<TimelinePage />);
    await waitFor(() => {
      expect(screen.getByRole('checkbox')).toBeInTheDocument();
    });
    screen.getByRole('checkbox').click();
    await waitFor(() => {
      expect(screen.getByText('— 切换间隔 —')).toBeInTheDocument();
    });
    // R10：显示时不得 aria-hidden，必须有可访问名。
    const row = screen.getByText('— 切换间隔 —');
    expect(row).not.toHaveAttribute('aria-hidden');
    expect(row).toHaveAttribute('aria-label', '切换间隔（采样间隙，不计入时长）');
  });

  it('nextCursor 存在时加载更多并追加条目', async () => {
    mockRoutes([
      pageFixture(
        [
          {
            kind: 'segment',
            segmentId: i64('1'),
            app: { appId: i64('1'), displayName: 'Code' },
            activityState: 'active',
            startAtUtcMs: i64('1784300000000'),
            endAtUtcMs: i64('1784303600000'),
            durationMs: i64('3600000'),
            status: 'closed',
          },
        ],
        'cursor-1',
      ),
      pageFixture(
        [
          {
            kind: 'segment',
            segmentId: i64('2'),
            app: { appId: i64('2'), displayName: 'Edge' },
            activityState: 'active',
            startAtUtcMs: i64('1784303603000'),
            endAtUtcMs: i64('1784307203000'),
            durationMs: i64('3600000'),
            status: 'closed',
          },
        ],
        null,
      ),
    ]);
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
    mockRoutes([pageFixture([], null)]);
    render(<TimelinePage />);
    await waitFor(() => {
      expect(screen.getByText('今天还没有时间线记录')).toBeInTheDocument();
    });
  });
});
