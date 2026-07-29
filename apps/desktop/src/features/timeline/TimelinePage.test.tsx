import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { vi } from 'vitest';
import TimelinePage from './TimelinePage';
import type { Int64String, TimelineItem, TimelinePageDto } from '../../types/wuji-core';

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
  return { localDate: TODAY_LOCAL_DATE, reportingTimeZoneId: TZ, items, nextCursor };
}

/** 单个已关闭 Segment 夹具；startAtUtcMs 决定升序位置。 */
function segmentFixture(id: string, displayName: string, startAtUtcMs: string): TimelineItem {
  const start = BigInt(startAtUtcMs);
  const end = start + 3_600_000n;
  return {
    kind: 'segment',
    segmentId: i64(id),
    app: { appId: i64(id), displayName },
    activityState: 'active',
    startAtUtcMs: i64(startAtUtcMs),
    endAtUtcMs: i64(end.toString()),
    durationMs: i64('3600000'),
    status: 'closed',
  };
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

  it('以 DB reporting 时区的日期查询时间线，最新条目在顶部，默认折叠切换间隔', async () => {
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
    // R08：查询必须使用 activity_get_today 的 localDate（DB reporting 时区），
    // 不得使用浏览器本地日期；单页拉取上限 500。
    expect(invoke).toHaveBeenCalledWith('activity_get_timeline', {
      localDate: TODAY_LOCAL_DATE,
      cursor: null,
      limit: 500,
    });
    // 后端升序返回，页面倒序展示：最新的暂停 gap 在顶，最旧的 Code 在底。
    const rows = screen.getAllByRole('listitem');
    expect(rows[0]).toHaveTextContent('已暂停');
    expect(rows[1]).toHaveTextContent('Edge');
    expect(rows[2]).toHaveTextContent('Code');
    expect(screen.getByText('活跃')).toBeInTheDocument();
    expect(screen.getByText('空闲')).toBeInTheDocument();
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

  it('当天条目超过单页上限时循环取完所有页', async () => {
    mockRoutes([
      pageFixture([segmentFixture('1', 'Code', '1784300000000')], 'cursor-1'),
      pageFixture([segmentFixture('2', 'Edge', '1784303603000')], null),
    ]);
    render(<TimelinePage />);
    await waitFor(() => {
      expect(screen.getByText('Edge')).toBeInTheDocument();
    });
    expect(screen.getByText('Code')).toBeInTheDocument();
    expect(invoke).toHaveBeenLastCalledWith('activity_get_timeline', {
      localDate: TODAY_LOCAL_DATE,
      cursor: 'cursor-1',
      limit: 500,
    });
  });

  it('无条目时显示 Empty 四态', async () => {
    mockRoutes([pageFixture([], null)]);
    render(<TimelinePage />);
    await waitFor(() => {
      expect(screen.getByText('今天还没有时间线记录')).toBeInTheDocument();
    });
  });

  it('每 5 秒自动整体重取；轮询失败保留已展示数据', async () => {
    vi.useFakeTimers();
    try {
      let failTimeline = false;
      invoke.mockImplementation((command: string) => {
        if (command === 'activity_get_today') {
          return Promise.resolve({ localDate: TODAY_LOCAL_DATE });
        }
        if (command === 'activity_get_timeline') {
          return failTimeline
            ? Promise.reject(new Error('boom'))
            : Promise.resolve(pageFixture([segmentFixture('1', 'Code', '1784300000000')], null));
        }
        return Promise.reject(new Error(`unexpected command: ${command}`));
      });
      render(<TimelinePage />);
      await act(async () => {
        await vi.advanceTimersByTimeAsync(0);
      });
      expect(screen.getByText('Code')).toBeInTheDocument();
      const timelineCalls = () =>
        invoke.mock.calls.filter(([command]) => command === 'activity_get_timeline').length;

      const before = timelineCalls();
      await act(async () => {
        await vi.advanceTimersByTimeAsync(5000);
      });
      expect(timelineCalls()).toBe(before + 1);

      failTimeline = true;
      await act(async () => {
        await vi.advanceTimersByTimeAsync(5000);
      });
      expect(timelineCalls()).toBe(before + 2);
      // 轮询失败：旧数据保留，不进入错误四态。
      expect(screen.getByText('Code')).toBeInTheDocument();
      expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    } finally {
      vi.useRealTimers();
    }
  });

  it('「跳到底部」始终可用；向下滚动后出现「回到顶部」', async () => {
    mockRoutes([pageFixture([segmentFixture('1', 'Code', '1784300000000')], null)]);
    render(
      <div className="app-main">
        <TimelinePage />
      </div>,
    );
    await waitFor(() => {
      expect(screen.getByText('Code')).toBeInTheDocument();
    });
    const container = document.querySelector('.app-main') as HTMLElement;
    const scrollTo = vi.fn();
    container.scrollTo = scrollTo;

    // 初始在顶部：只有「跳到底部」。
    expect(screen.queryByRole('button', { name: '回到顶部' })).not.toBeInTheDocument();
    screen.getByRole('button', { name: '跳到底部' }).click();
    expect(scrollTo).toHaveBeenCalledWith({ top: container.scrollHeight, behavior: 'smooth' });

    // 向下滚动后出现「回到顶部」，点击回滚到顶部。
    Object.defineProperty(container, 'scrollTop', { value: 500, configurable: true });
    fireEvent.scroll(container);
    const toTop = await screen.findByRole('button', { name: '回到顶部' });
    toTop.click();
    expect(scrollTo).toHaveBeenLastCalledWith({ top: 0, behavior: 'smooth' });
  });
});
