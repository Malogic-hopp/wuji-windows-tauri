import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
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
/** 2026-07-18T00:00:00Z（毫秒）= 上海 2026-07-18 08:00。 */
const T0 = 1_784_332_800_000;

function pageFixture(items: TimelinePageDto['items'], nextCursor: string | null): TimelinePageDto {
  return { localDate: TODAY_LOCAL_DATE, reportingTimeZoneId: TZ, items, nextCursor };
}

/** 单个已关闭 Segment 夹具（固定 1 小时）；startAtUtcMs 决定升序位置。 */
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

/** 自定义起止的 Segment 夹具（跨午夜/短时段用）。 */
function rangedSegment(id: string, displayName: string, start: string, end: string): TimelineItem {
  return {
    kind: 'segment',
    segmentId: i64(id),
    app: { appId: i64(id), displayName },
    activityState: 'active',
    startAtUtcMs: i64(start),
    endAtUtcMs: i64(end),
    durationMs: i64((BigInt(end) - BigInt(start)).toString()),
    status: 'closed',
  };
}

function transitionGapFixture(id: string, startAtUtcMs: string, endAtUtcMs: string): TimelineItem {
  return {
    kind: 'gap',
    gapId: i64(id),
    gapKind: 'sampling_transition',
    startAtUtcMs: i64(startAtUtcMs),
    endAtUtcMs: i64(endAtUtcMs),
    status: 'closed',
    eventCount: 1,
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

/** useSearchParams 需要 Router 上下文；initialEntry 可带 ?date=/?hour=。 */
function renderPage(initialEntry = '/') {
  return render(
    <MemoryRouter initialEntries={[initialEntry]}>
      <TimelinePage />
    </MemoryRouter>,
  );
}

const timelineCalls = () =>
  invoke.mock.calls.filter(([command]) => command === 'activity_get_timeline').length;

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
    renderPage();
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
        [transitionGapFixture('10', '1784303600000', '1784303603000')],
        null,
      ),
    ]);
    renderPage();
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
    renderPage();
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
    renderPage();
    await waitFor(() => {
      expect(screen.getByText('这一天还没有时间线记录')).toBeInTheDocument();
    });
  });

  it('空数据日期仍显示日期导航，可继续翻页', async () => {
    mockRoutes([pageFixture([], null)]);
    renderPage('/?date=2026-07-17');
    await waitFor(() => {
      expect(screen.getByText('这一天还没有时间线记录')).toBeInTheDocument();
    });
    // 空态在四态内，日期导航在四态外：空数据日期仍可前后翻页。
    expect(screen.getByText('2026-07-17')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: '前一天' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: '回到今天' })).toBeInTheDocument();

    screen.getByRole('button', { name: '后一天' }).click();
    await waitFor(() => {
      expect(invoke).toHaveBeenLastCalledWith('activity_get_timeline', {
        localDate: '2026-07-18',
        cursor: null,
        limit: 500,
      });
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
      renderPage();
      await act(async () => {
        await vi.advanceTimersByTimeAsync(0);
      });
      expect(screen.getByText('Code')).toBeInTheDocument();

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
      // 同视图轮询失败：旧数据保留，不进入错误四态。
      expect(screen.getByText('Code')).toBeInTheDocument();
      expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    } finally {
      vi.useRealTimers();
    }
  });

  it('同目标轮询未完成时跳过本轮；完成后正常继续', async () => {
    vi.useFakeTimers();
    try {
      let calls = 0;
      let resolveFirst: ((dto: TimelinePageDto) => void) | null = null;
      invoke.mockImplementation((command: string) => {
        if (command === 'activity_get_today') {
          return Promise.resolve({ localDate: TODAY_LOCAL_DATE });
        }
        if (command === 'activity_get_timeline') {
          calls += 1;
          if (calls === 1) {
            // 首轮挂起，模拟超过轮询间隔的慢请求。
            return new Promise<TimelinePageDto>((resolve) => {
              resolveFirst = resolve;
            });
          }
          return Promise.resolve(
            pageFixture([segmentFixture('1', 'Code', '1784300000000')], null),
          );
        }
        return Promise.reject(new Error(`unexpected command: ${command}`));
      });
      renderPage();
      await act(async () => {
        await vi.advanceTimersByTimeAsync(0);
      });
      expect(calls).toBe(1);

      // 首轮仍 pending：5 秒 tick 到达时必须跳过，不得并发。
      await act(async () => {
        await vi.advanceTimersByTimeAsync(5000);
      });
      expect(calls).toBe(1);

      // 首轮完成后，下一轮 tick 正常执行。
      await act(async () => {
        resolveFirst?.(pageFixture([segmentFixture('1', 'Code', '1784300000000')], null));
        await Promise.resolve();
      });
      expect(screen.getByText('Code')).toBeInTheDocument();
      await act(async () => {
        await vi.advanceTimersByTimeAsync(5000);
      });
      expect(calls).toBe(2);
    } finally {
      vi.useRealTimers();
    }
  });

  it('轮询 pending 时切换日期：新日期照常加载，迟到的旧响应不得覆盖新视图', async () => {
    vi.useFakeTimers();
    try {
      let calls = 0;
      let resolveStalePoll: ((dto: TimelinePageDto) => void) | null = null;
      invoke.mockImplementation((command: string) => {
        if (command === 'activity_get_today') {
          return Promise.resolve({ localDate: TODAY_LOCAL_DATE });
        }
        if (command === 'activity_get_timeline') {
          calls += 1;
          if (calls === 1) {
            // 首轮今天立即返回。
            return Promise.resolve(
              pageFixture([segmentFixture('1', 'Code', '1784300000000')], null),
            );
          }
          if (calls === 2) {
            // 第一轮轮询挂起（模拟慢请求）。
            return new Promise<TimelinePageDto>((resolve) => {
              resolveStalePoll = resolve;
            });
          }
          // 切换后的历史日期请求。
          return Promise.resolve(
            pageFixture([segmentFixture('2', 'Edge', '1784303603000')], null),
          );
        }
        return Promise.reject(new Error(`unexpected command: ${command}`));
      });
      renderPage();
      await act(async () => {
        await vi.advanceTimersByTimeAsync(0);
      });
      expect(screen.getByText('Code')).toBeInTheDocument();

      // 触发一次今天轮询并让它 pending。
      await act(async () => {
        await vi.advanceTimersByTimeAsync(5000);
      });
      expect(calls).toBe(2);

      // 轮询 pending 中切到历史日期：新请求必须发出，不得被防重入跳过。
      // fireEvent 包裹 act：点击的更新先落完（effect 重新调度），再推进计时器。
      fireEvent.click(screen.getByRole('button', { name: '前一天' }));
      await act(async () => {
        await vi.advanceTimersByTimeAsync(0);
      });
      expect(calls).toBe(3);
      expect(invoke).toHaveBeenLastCalledWith('activity_get_timeline', {
        localDate: '2026-07-17',
        cursor: null,
        limit: 500,
      });
      expect(screen.getByText('Edge')).toBeInTheDocument();

      // 迟到的旧日期轮询响应：一律丢弃，不得覆盖新视图。
      await act(async () => {
        resolveStalePoll?.(pageFixture([segmentFixture('1', 'Code', '1784300000000')], null));
        await Promise.resolve();
      });
      expect(screen.getByText('Edge')).toBeInTheDocument();
      expect(screen.queryByText('Code')).not.toBeInTheDocument();
      expect(screen.getByText('2026-07-17')).toBeInTheDocument();
    } finally {
      vi.useRealTimers();
    }
  });

  it('A→B→A 交错：旧 A 完成不得清除新 A 的同目标防重入登记', async () => {
    vi.useFakeTimers();
    try {
      let calls = 0;
      let resolveOldToday: ((dto: TimelinePageDto) => void) | null = null;
      let resolveNewToday: ((dto: TimelinePageDto) => void) | null = null;
      invoke.mockImplementation((command: string) => {
        if (command === 'activity_get_today') {
          return Promise.resolve({ localDate: TODAY_LOCAL_DATE });
        }
        if (command === 'activity_get_timeline') {
          calls += 1;
          if (calls === 1) {
            return Promise.resolve(
              pageFixture([segmentFixture('1', 'Code', '1784300000000')], null),
            );
          }
          if (calls === 2) {
            // A1：今天轮询挂起。
            return new Promise<TimelinePageDto>((resolve) => {
              resolveOldToday = resolve;
            });
          }
          if (calls === 3) {
            // B：历史日期立即完成，以便切回今天。
            return Promise.resolve(
              pageFixture([segmentFixture('2', 'Edge', '1784303603000')], null),
            );
          }
          if (calls === 4) {
            // A2：切回今天后的新请求保持 pending。
            return new Promise<TimelinePageDto>((resolve) => {
              resolveNewToday = resolve;
            });
          }
          return Promise.reject(new Error(`unexpected timeline call ${String(calls)}`));
        }
        return Promise.reject(new Error(`unexpected command: ${command}`));
      });

      renderPage();
      await act(async () => {
        await vi.advanceTimersByTimeAsync(0);
      });
      expect(screen.getByText('Code')).toBeInTheDocument();

      // A1 pending。
      await act(async () => {
        await vi.advanceTimersByTimeAsync(5000);
      });
      expect(calls).toBe(2);

      // A→B。
      fireEvent.click(screen.getByRole('button', { name: '前一天' }));
      await act(async () => {
        await vi.advanceTimersByTimeAsync(0);
      });
      expect(calls).toBe(3);
      expect(screen.getByText('Edge')).toBeInTheDocument();

      // B→A，A2 pending。
      fireEvent.click(screen.getByRole('button', { name: '回到今天' }));
      await act(async () => {
        await vi.advanceTimersByTimeAsync(0);
      });
      expect(calls).toBe(4);

      // 旧 A1 此时完成：不得清除 A2 的 { target, generation } 登记。
      await act(async () => {
        resolveOldToday?.(
          pageFixture([segmentFixture('3', 'Old Today', '1784307200000')], null),
        );
        await Promise.resolve();
      });
      await act(async () => {
        await vi.advanceTimersByTimeAsync(5000);
      });
      expect(calls).toBe(4);

      // 收束 A2，避免测试结束时遗留 pending Promise。
      await act(async () => {
        resolveNewToday?.(
          pageFixture([segmentFixture('4', 'New Today', '1784310800000')], null),
        );
        await Promise.resolve();
      });
      expect(screen.getByText('New Today')).toBeInTheDocument();
      expect(screen.queryByText('Old Today')).not.toBeInTheDocument();
    } finally {
      vi.useRealTimers();
    }
  });

  it('切换日期后查询失败：进入错误四态，不得保留旧日期内容', async () => {
    let calls = 0;
    invoke.mockImplementation((command: string) => {
      if (command === 'activity_get_today') {
        return Promise.resolve({ localDate: TODAY_LOCAL_DATE });
      }
      if (command === 'activity_get_timeline') {
        calls += 1;
        if (calls === 1) {
          return Promise.resolve(
            pageFixture([segmentFixture('1', 'Code', '1784300000000')], null),
          );
        }
        return Promise.reject(new Error('boom'));
      }
      return Promise.reject(new Error(`unexpected command: ${command}`));
    });
    renderPage();
    await waitFor(() => {
      expect(screen.getByText('Code')).toBeInTheDocument();
    });

    screen.getByRole('button', { name: '前一天' }).click();
    await waitFor(() => {
      expect(screen.getByRole('alert')).toBeInTheDocument();
    });
    // 旧日期内容必须卸载，不得出现在新日期标题下。
    expect(screen.queryByText('Code')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: '重试' })).toBeInTheDocument();
  });

  it('显式 ?date=今天：初始只加载一次，之后按今天视图正常轮询', async () => {
    vi.useFakeTimers();
    try {
      mockRoutes([pageFixture([segmentFixture('1', 'Code', '1784300000000')], null)]);
      renderPage(`/?date=${TODAY_LOCAL_DATE}`);
      await act(async () => {
        await vi.advanceTimersByTimeAsync(0);
      });
      expect(screen.getByText('Code')).toBeInTheDocument();
      // todayDate 到达后 isToday 翻转，不得触发第二次立即加载。
      expect(timelineCalls()).toBe(1);

      await act(async () => {
        await vi.advanceTimersByTimeAsync(5000);
      });
      expect(timelineCalls()).toBe(2);
    } finally {
      vi.useRealTimers();
    }
  });

  it('达到取页上限仍有后续时明确标记截断，不伪装成完整结果', async () => {
    let calls = 0;
    invoke.mockImplementation((command: string) => {
      if (command === 'activity_get_today') {
        return Promise.resolve({ localDate: TODAY_LOCAL_DATE });
      }
      if (command === 'activity_get_timeline') {
        calls += 1;
        // 每页都给出前进的游标，永远不到最后一页。
        return Promise.resolve(
          pageFixture(
            [segmentFixture(String(calls), `App${String(calls)}`, '1784300000000')],
            `cursor-${String(calls)}`,
          ),
        );
      }
      return Promise.reject(new Error(`unexpected command: ${command}`));
    });
    renderPage();
    await waitFor(() => {
      expect(screen.getByText('当天记录条数过多，仅显示部分记录。')).toBeInTheDocument();
    });
    expect(calls).toBe(20);
  });

  it('游标不前进时停止取页并按截断处理', async () => {
    let calls = 0;
    invoke.mockImplementation((command: string) => {
      if (command === 'activity_get_today') {
        return Promise.resolve({ localDate: TODAY_LOCAL_DATE });
      }
      if (command === 'activity_get_timeline') {
        calls += 1;
        // 始终返回同一游标：第二页即检测到停滞。
        return Promise.resolve(
          pageFixture(
            [segmentFixture(String(calls), `App${String(calls)}`, '1784300000000')],
            'cursor-stuck',
          ),
        );
      }
      return Promise.reject(new Error(`unexpected command: ${command}`));
    });
    renderPage();
    await waitFor(() => {
      expect(screen.getByText('当天记录条数过多，仅显示部分记录。')).toBeInTheDocument();
    });
    expect(calls).toBe(2);
  });

  it('「跳到底部」始终可用；向下滚动后出现「回到顶部」', async () => {
    mockRoutes([pageFixture([segmentFixture('1', 'Code', '1784300000000')], null)]);
    render(
      <MemoryRouter>
        <div className="app-main">
          <TimelinePage />
        </div>
      </MemoryRouter>,
    );
    await waitFor(() => {
      expect(screen.getByText('Code')).toBeInTheDocument();
    });
    const container = document.querySelector('.app-main') as HTMLElement;
    const scrollTo = vi.fn();
    container.scrollTo = scrollTo;

    // 列表末尾预留了悬浮按钮的占位空间，避免遮挡。
    expect(document.querySelector('.scroll-actions-spacer')).toBeInTheDocument();

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

  it('?date= 查看历史日期：按参数查询且静态视图不轮询', async () => {
    vi.useFakeTimers();
    try {
      mockRoutes([pageFixture([segmentFixture('1', 'Code', '1784300000000')], null)]);
      renderPage('/?date=2026-07-17');
      await act(async () => {
        await vi.advanceTimersByTimeAsync(0);
      });
      expect(screen.getByText('Code')).toBeInTheDocument();
      expect(invoke).toHaveBeenLastCalledWith('activity_get_timeline', {
        localDate: '2026-07-17',
        cursor: null,
        limit: 500,
      });
      const before = timelineCalls();
      await act(async () => {
        await vi.advanceTimersByTimeAsync(15_000);
      });
      // 历史日期是静态视图：不周期轮询。
      expect(timelineCalls()).toBe(before);
    } finally {
      vi.useRealTimers();
    }
  });

  it('日期导航：前一天/后一天/回到今天', async () => {
    mockRoutes([pageFixture([segmentFixture('1', 'Code', '1784300000000')], null)]);
    renderPage();
    await waitFor(() => {
      expect(screen.getByText('Code')).toBeInTheDocument();
    });
    // 今天视图：显示「· 今天」，后一天禁用，无「回到今天」。
    expect(screen.getByText('2026-07-18 · 今天')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: '后一天' })).toBeDisabled();
    expect(screen.queryByRole('button', { name: '回到今天' })).not.toBeInTheDocument();

    screen.getByRole('button', { name: '前一天' }).click();
    await waitFor(() => {
      expect(invoke).toHaveBeenLastCalledWith('activity_get_timeline', {
        localDate: '2026-07-17',
        cursor: null,
        limit: 500,
      });
    });
    // 历史视图：「回到今天」出现，后一天可用。
    expect(screen.getByRole('button', { name: '回到今天' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: '后一天' })).not.toBeDisabled();

    screen.getByRole('button', { name: '回到今天' }).click();
    await waitFor(() => {
      expect(invoke).toHaveBeenLastCalledWith('activity_get_timeline', {
        localDate: TODAY_LOCAL_DATE,
        cursor: null,
        limit: 500,
      });
    });
  });

  it('?hour= 定位覆盖该小时的可见条目：隐藏 transition 不成为目标', async () => {
    const scrollIntoView = vi.fn();
    Element.prototype.scrollIntoView = scrollIntoView;
    // Code 覆盖 9 时，9:30 的切换间隔同小时但默认隐藏，Edge 覆盖 10-11 时。
    mockRoutes([
      pageFixture(
        [
          rangedSegment('1', 'Code', String(T0 + 3_600_000), String(T0 + 5_400_000)),
          transitionGapFixture('10', String(T0 + 5_400_000), String(T0 + 5_460_000)),
          rangedSegment('2', 'Edge', String(T0 + 7_200_000), String(T0 + 10_800_000)),
        ],
        null,
      ),
    ]);
    renderPage('/?hour=9');
    await waitFor(() => {
      expect(screen.getByText('Code')).toBeInTheDocument();
    });
    const codeRow = screen.getByText('Code').closest('li');
    expect(codeRow).toHaveClass('list__row--hour-target');
    // 隐藏的 transition 不得携带高亮（页面上根本没有它的行）。
    expect(document.querySelectorAll('.list__row--hour-target')).toHaveLength(1);
    expect(screen.getByText('已定位到 9 时')).toBeInTheDocument();
    expect(scrollIntoView).toHaveBeenCalled();
  });

  it('显示 transition 后仍由活动条目承担小时定位', async () => {
    mockRoutes([
      pageFixture(
        [
          rangedSegment('1', 'Code', String(T0 + 3_600_000), String(T0 + 5_400_000)),
          transitionGapFixture('10', String(T0 + 5_400_000), String(T0 + 5_460_000)),
        ],
        null,
      ),
    ]);
    renderPage('/?hour=9');
    await waitFor(() => {
      expect(screen.getByText('Code')).toBeInTheDocument();
    });

    fireEvent.click(screen.getByRole('checkbox'));
    expect(screen.getByText('— 切换间隔 —')).toBeInTheDocument();
    expect(screen.getByText('Code').closest('li')).toHaveClass('list__row--hour-target');
    expect(document.querySelectorAll('.list__row--hour-target')).toHaveLength(1);
    expect(screen.getByText('已定位到 9 时')).toBeInTheDocument();
  });

  it('前一天跨入当天的条目：当天视图覆盖 0 时，不覆盖 23 时', async () => {
    // 2026-07-17 23:30 → 2026-07-18 00:15（上海；UTC 分别为 07-17 15:30 / 16:15）。
    const cross = rangedSegment(
      '1',
      'Code',
      String(T0 - 30_600_000),
      String(T0 - 27_900_000),
    );
    mockRoutes([pageFixture([cross], null)]);

    const first = renderPage('/?hour=0');
    await waitFor(() => {
      expect(screen.getByText('Code')).toBeInTheDocument();
    });
    expect(screen.getByText('Code').closest('li')).toHaveClass('list__row--hour-target');
    expect(screen.getByText('已定位到 0 时')).toBeInTheDocument();
    first.unmount();

    renderPage('/?hour=23');
    await waitFor(() => {
      expect(screen.getByText('Code')).toBeInTheDocument();
    });
    expect(screen.getByText('Code').closest('li')).not.toHaveClass('list__row--hour-target');
    expect(screen.queryByText(/已定位到/)).not.toBeInTheDocument();
  });

  it('当天跨入后一天的条目：当天视图覆盖 23 时，不覆盖 0 时', async () => {
    // 2026-07-18 23:30 → 2026-07-19 00:15（上海）。
    const cross = rangedSegment(
      '1',
      'Code',
      String(T0 + 55_800_000),
      String(T0 + 58_500_000),
    );
    mockRoutes([pageFixture([cross], null)]);

    const first = renderPage('/?hour=23');
    await waitFor(() => {
      expect(screen.getByText('Code')).toBeInTheDocument();
    });
    expect(screen.getByText('Code').closest('li')).toHaveClass('list__row--hour-target');
    expect(screen.getByText('已定位到 23 时')).toBeInTheDocument();
    first.unmount();

    renderPage('/?hour=0');
    await waitFor(() => {
      expect(screen.getByText('Code')).toBeInTheDocument();
    });
    expect(screen.getByText('Code').closest('li')).not.toHaveClass('list__row--hour-target');
    expect(screen.queryByText(/已定位到/)).not.toBeInTheDocument();
  });

  it('?hour= 无可见命中时不显示已定位提示', async () => {
    mockRoutes([
      pageFixture(
        [rangedSegment('1', 'Code', String(T0 + 3_600_000), String(T0 + 5_400_000))],
        null,
      ),
    ]);
    renderPage('/?hour=15');
    await waitFor(() => {
      expect(screen.getByText('Code')).toBeInTheDocument();
    });
    expect(screen.getByText('Code').closest('li')).not.toHaveClass('list__row--hour-target');
    expect(screen.queryByText(/已定位到/)).not.toBeInTheDocument();
  });
});
