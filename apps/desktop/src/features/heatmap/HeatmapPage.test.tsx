import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes, useSearchParams } from 'react-router-dom';
import { vi } from 'vitest';
import HeatmapPage from './HeatmapPage';
import type { HeatmapDto, Int64String } from '../../types/wuji-core';

/** Int64String 夹具断言（R07 品牌类型）。 */
const i64 = (text: string): Int64String => text as Int64String;

const invoke = vi.fn<(command: string, args?: unknown) => Promise<unknown>>();
vi.mock('@tauri-apps/api/core', () => ({
  invoke: (command: string, args?: unknown): Promise<unknown> => invoke(command, args),
}));

function heatmapFixture(
  cells: HeatmapDto['cells'],
  rangeEndLocalDate = '2026-07-19',
  today = '2026-07-19',
): HeatmapDto {
  return {
    today,
    rangeEndLocalDate,
    reportingTimeZoneId: 'Asia/Shanghai',
    days: 7,
    cells,
  };
}

const BUSY_CELL = {
  localDate: '2026-07-19',
  localHour: 9,
  activeDurationMs: i64('3600000'),
  idleDurationMs: i64('0'),
  unknownDurationMs: i64('0'),
  intensityLevel: 4,
};

function mockHeatmapRoute(cells: HeatmapDto['cells'], rangeEndLocalDate?: string) {
  invoke.mockImplementation((command: string) => {
    if (command === 'activity_get_heatmap') {
      return Promise.resolve(heatmapFixture(cells, rangeEndLocalDate));
    }
    return Promise.reject(new Error(`unexpected command: ${command}`));
  });
}

/** useNavigate/useSearchParams 需要 Router 上下文。 */
function renderPage(initialEntry = '/') {
  return render(
    <MemoryRouter initialEntries={[initialEntry]}>
      <HeatmapPage />
    </MemoryRouter>,
  );
}

/** 跳转断言探针：展示 /timeline 的 search 参数。 */
function TimelineProbe() {
  const [params] = useSearchParams();
  return (
    <div data-testid="timeline-probe">
      {`date=${params.get('date') ?? ''} hour=${params.get('hour') ?? ''}`}
    </div>
  );
}

function renderWithTimelineRoute() {
  return render(
    <MemoryRouter initialEntries={['/heatmap']}>
      <Routes>
        <Route path="/heatmap" element={<HeatmapPage />} />
        <Route path="/timeline" element={<TimelineProbe />} />
      </Routes>
    </MemoryRouter>,
  );
}

describe('Heatmap 页面', () => {
  beforeEach(() => {
    invoke.mockReset();
    mockHeatmapRoute([BUSY_CELL]);
  });

  it('渲染 7×24 固定网格：无数据日期列保留，图例/今天列/强度格可读', async () => {
    renderPage();
    await waitFor(() => {
      expect(screen.getByRole('grid', { name: '最近 7 天每小时活跃热力图' })).toBeInTheDocument();
    });
    expect(invoke).toHaveBeenCalledWith('activity_get_heatmap', {
      days: null,
      weekOffset: null,
    });
    // 只有今天一个格子有数据，日期轴仍固定 7 列 × 24 行 = 168 格。
    expect(screen.getAllByRole('gridcell')).toHaveLength(168);
    // 轴首列 2026-07-13（周一），末列为今天。
    expect(screen.getByText('周一')).toBeInTheDocument();
    expect(screen.getByText('今天')).toBeInTheDocument();
    // 图例五级可被辅助技术感知。
    expect(screen.getByRole('img', { name: '极高' })).toBeInTheDocument();
    // 忙碌格：强度 4 样式 + aria-label。
    const busy = screen.getByRole('gridcell', {
      name: '7月19日 9时，活跃 1 小时，活跃程度 极高',
    });
    expect(busy).toHaveClass('heatmap-level--4');
    // roving tabindex：恰好一个格子在 Tab 序中。
    const inTabOrder = screen
      .getAllByRole('gridcell')
      .filter((cell) => cell.getAttribute('tabindex') === '0');
    expect(inTabOrder).toHaveLength(1);
  });

  it('空数据时显示 Empty 四态', async () => {
    mockHeatmapRoute([]);
    renderPage();
    await waitFor(() => {
      expect(screen.getByText('这周还没有活跃记录')).toBeInTheDocument();
    });
  });

  it('每 15 秒轮询重取，失败保留已展示数据', async () => {
    vi.useFakeTimers();
    try {
      let fail = false;
      invoke.mockImplementation((command: string) => {
        if (command === 'activity_get_heatmap') {
          return fail
            ? Promise.reject(new Error('boom'))
            : Promise.resolve(heatmapFixture([BUSY_CELL]));
        }
        return Promise.reject(new Error(`unexpected command: ${command}`));
      });
      renderPage();
      await act(async () => {
        await vi.advanceTimersByTimeAsync(0);
      });
      expect(screen.getByRole('grid')).toBeInTheDocument();
      const calls = () =>
        invoke.mock.calls.filter(([command]) => command === 'activity_get_heatmap').length;

      const before = calls();
      await act(async () => {
        await vi.advanceTimersByTimeAsync(15_000);
      });
      expect(calls()).toBe(before + 1);

      fail = true;
      await act(async () => {
        await vi.advanceTimersByTimeAsync(15_000);
      });
      expect(calls()).toBe(before + 2);
      // 轮询失败：旧数据保留，不进入错误四态。
      expect(screen.getByRole('grid')).toBeInTheDocument();
      expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    } finally {
      vi.useRealTimers();
    }
  });

  it('方向键移动焦点格子（roving tabindex）', async () => {
    renderPage();
    await waitFor(() => {
      expect(screen.getByRole('grid')).toBeInTheDocument();
    });
    const initialFocusables = screen
      .getAllByRole('gridcell')
      .filter((cell) => cell.getAttribute('tabindex') === '0');
    expect(initialFocusables).toHaveLength(1);
    const initial = initialFocusables[0];
    const initialHour = Number(initial.getAttribute('data-hour'));

    fireEvent.keyDown(initial, { key: 'ArrowUp' });
    await waitFor(() => {
      const focusables = screen
        .getAllByRole('gridcell')
        .filter((cell) => cell.getAttribute('tabindex') === '0');
      expect(focusables).toHaveLength(1);
      // 边缘收敛：上移一行，除非已在第 0 行。
      expect(Number(focusables[0]?.getAttribute('data-hour'))).toBe(Math.max(0, initialHour - 1));
    });
  });

  it('点击格子跳转时间线对应日期小时', async () => {
    renderWithTimelineRoute();
    const busy = await screen.findByRole('gridcell', {
      name: '7月19日 9时，活跃 1 小时，活跃程度 极高',
    });
    busy.click();
    expect(await screen.findByTestId('timeline-probe')).toHaveTextContent(
      'date=2026-07-19 hour=9',
    );
  });

  it('Enter/Space 在焦点格子上打开时间线（UI-005）', async () => {
    for (const key of ['Enter', ' ']) {
      const view = renderWithTimelineRoute();
      await waitFor(() => {
        expect(screen.getByRole('grid')).toBeInTheDocument();
      });
      const focusables = screen
        .getAllByRole('gridcell')
        .filter((cell) => cell.getAttribute('tabindex') === '0');
      expect(focusables).toHaveLength(1);
      const hour = focusables[0].getAttribute('data-hour') ?? '';

      fireEvent.keyDown(focusables[0], { key });
      expect(await screen.findByTestId('timeline-probe')).toHaveTextContent(
        `date=2026-07-19 hour=${hour}`,
      );
      view.unmount();
    }
  });

  describe('周导航', () => {
    it('本周视图：显示本周标签，下一周按钮禁用，无回到本周按钮', async () => {
      renderPage();
      await waitFor(() => {
        expect(screen.getByText(/本周/)).toBeInTheDocument();
      });
      expect(screen.getByRole('button', { name: '上一周' })).toBeEnabled();
      expect(screen.getByRole('button', { name: '下一周' })).toBeDisabled();
      expect(screen.queryByRole('button', { name: '回到本周' })).not.toBeInTheDocument();
    });

    it('上周视图（?week=-1）：无本周标签，下一周可用，回到本周出现', async () => {
      mockHeatmapRoute([{ ...BUSY_CELL, localDate: '2026-07-12' }], '2026-07-12');
      renderPage('/?week=-1');
      await waitFor(() => {
        expect(screen.getByText(/2026-07-06/)).toBeInTheDocument();
      });
      // 日期范围文案不含"· 本周"（与按钮"回到本周"区分）。
      expect(screen.queryByText('· 本周')).not.toBeInTheDocument();
      expect(screen.getByRole('button', { name: '上一周' })).toBeEnabled();
      expect(screen.getByRole('button', { name: '下一周' })).toBeEnabled();
      expect(screen.getByRole('button', { name: '回到本周' })).toBeInTheDocument();
      expect(screen.queryByText('今天')).not.toBeInTheDocument();
    });

    it('点击上一周将 weekOffset 写入 URL', async () => {
      mockHeatmapRoute([], '2026-07-12');
      renderPage();
      await waitFor(() => {
        expect(screen.getByRole('button', { name: '上一周' })).toBeEnabled();
      });
      fireEvent.click(screen.getByRole('button', { name: '上一周' }));
      // URL 应包含 ?week=-1
      await waitFor(() => {
        expect(invoke).toHaveBeenCalledWith('activity_get_heatmap', {
          days: null,
          weekOffset: -1,
        });
      });
    });

    it('回到本周清空 URL 参数', async () => {
      renderPage('/?week=-2');
      await waitFor(() => {
        expect(screen.getByRole('button', { name: '回到本周' })).toBeInTheDocument();
      });
      fireEvent.click(screen.getByRole('button', { name: '回到本周' }));
      await waitFor(() => {
        expect(invoke).toHaveBeenCalledWith('activity_get_heatmap', {
          days: null,
          weekOffset: null,
        });
      });
    });

    it('历史周不启动轮询定时器', async () => {
      vi.useFakeTimers();
      try {
        renderPage('/?week=-1');
        await act(async () => {
          await vi.advanceTimersByTimeAsync(0);
        });
        const initialCalls = invoke.mock.calls.filter(
          ([command]) => command === 'activity_get_heatmap',
        ).length;
        // 推进 15 秒后不应有新调用。
        await act(async () => {
          await vi.advanceTimersByTimeAsync(15_000);
        });
        const laterCalls = invoke.mock.calls.filter(
          ([command]) => command === 'activity_get_heatmap',
        ).length;
        expect(laterCalls).toBe(initialCalls);
      } finally {
        vi.useRealTimers();
      }
    });

    it('历史周不显示「今天」列头与当前时刻标记', async () => {
      mockHeatmapRoute([{ ...BUSY_CELL, localDate: '2026-07-12' }], '2026-07-12');
      renderPage('/?week=-1');
      await waitFor(() => {
        expect(screen.getByRole('grid')).toBeInTheDocument();
      });
      // 锚点 2026-07-12 是周日：列头显示星期，不得伪装成「今天」。
      expect(screen.queryByText('今天')).not.toBeInTheDocument();
      expect(screen.getByText('周日')).toBeInTheDocument();
      expect(document.querySelector('[aria-current="date"]')).toBeNull();
      expect(document.querySelector('.heatmap-grid__cell--now')).toBeNull();
    });

    it('?week=1（未来周）规范化回本周，不得发起未来查询', async () => {
      renderPage('/?week=1');
      await waitFor(() => {
        expect(screen.getByText(/本周/)).toBeInTheDocument();
      });
      expect(invoke).toHaveBeenCalledWith('activity_get_heatmap', {
        days: null,
        weekOffset: null,
      });
      expect(invoke).not.toHaveBeenCalledWith('activity_get_heatmap', {
        days: null,
        weekOffset: 1,
      });
      expect(screen.getByRole('button', { name: '下一周' })).toBeDisabled();
      expect(screen.queryByRole('button', { name: '回到本周' })).not.toBeInTheDocument();
    });

    it('三位历史周参数可连续翻页：-99 点击上一周发起 -100', async () => {
      renderPage('/?week=-99');
      await waitFor(() => {
        expect(screen.getByRole('button', { name: '上一周' })).toBeEnabled();
      });
      fireEvent.click(screen.getByRole('button', { name: '上一周' }));
      await waitFor(() => {
        expect(invoke).toHaveBeenCalledWith('activity_get_heatmap', {
          days: null,
          weekOffset: -100,
        });
      });
    });

    it('-520 是历史下界：上一周禁用，下一周仍可用', async () => {
      renderPage('/?week=-520');
      await waitFor(() => {
        expect(screen.getByRole('button', { name: '上一周' })).toBeDisabled();
      });
      expect(screen.getByRole('button', { name: '下一周' })).toBeEnabled();
      expect(invoke).toHaveBeenCalledWith('activity_get_heatmap', {
        days: null,
        weekOffset: -520,
      });
    });

    it('?week=-521 越界时规范化回本周，不得发起越界查询', async () => {
      renderPage('/?week=-521');
      await waitFor(() => {
        expect(screen.getByText(/本周/)).toBeInTheDocument();
      });
      expect(invoke).toHaveBeenCalledWith('activity_get_heatmap', {
        days: null,
        weekOffset: null,
      });
      expect(invoke).not.toHaveBeenCalledWith('activity_get_heatmap', {
        days: null,
        weekOffset: -521,
      });
    });

    it('轮询 pending 时切换周：新周照常加载，迟到的旧响应不得覆盖新视图', async () => {
      vi.useFakeTimers();
      try {
        let calls = 0;
        let resolveStale: ((dto: HeatmapDto) => void) | null = null;
        invoke.mockImplementation((command: string) => {
          if (command === 'activity_get_heatmap') {
            calls += 1;
            if (calls === 1) {
              return Promise.resolve(heatmapFixture([BUSY_CELL]));
            }
            if (calls === 2) {
              // 本周轮询挂起（模拟慢请求）。
              return new Promise<HeatmapDto>((resolve) => {
                resolveStale = resolve;
              });
            }
            // 上周请求。
            return Promise.resolve(
              heatmapFixture([{ ...BUSY_CELL, localDate: '2026-07-12' }], '2026-07-12'),
            );
          }
          return Promise.reject(new Error(`unexpected command: ${command}`));
        });
        renderPage();
        await act(async () => {
          await vi.advanceTimersByTimeAsync(0);
        });
        expect(screen.getByRole('grid')).toBeInTheDocument();
        await act(async () => {
          await vi.advanceTimersByTimeAsync(15_000);
        });
        expect(calls).toBe(2);

        // 轮询 pending 中切到上周：新请求必须发出，不得被防重入跳过。
        fireEvent.click(screen.getByRole('button', { name: '上一周' }));
        await act(async () => {
          await vi.advanceTimersByTimeAsync(0);
        });
        expect(calls).toBe(3);
        expect(invoke).toHaveBeenLastCalledWith('activity_get_heatmap', {
          days: null,
          weekOffset: -1,
        });
        expect(screen.getByText(/2026-07-06/)).toBeInTheDocument();

        // 迟到的本周轮询响应：一律丢弃，不得覆盖上周视图。
        await act(async () => {
          resolveStale?.(heatmapFixture([BUSY_CELL]));
          await Promise.resolve();
        });
        expect(screen.getByText(/2026-07-06/)).toBeInTheDocument();
        expect(screen.queryByText(/2026-07-13/)).not.toBeInTheDocument();
      } finally {
        vi.useRealTimers();
      }
    });

    it('A→B→A 时旧 A 的 finally 不得清除新 A 的防重入身份', async () => {
      vi.useFakeTimers();
      try {
        let calls = 0;
        let resolveOldA: ((dto: HeatmapDto) => void) | null = null;
        let resolveNewA: ((dto: HeatmapDto) => void) | null = null;
        invoke.mockImplementation((command: string) => {
          if (command !== 'activity_get_heatmap') {
            return Promise.reject(new Error(`unexpected command: ${command}`));
          }
          calls += 1;
          if (calls === 1) return Promise.resolve(heatmapFixture([BUSY_CELL]));
          if (calls === 2) {
            return new Promise<HeatmapDto>((resolve) => {
              resolveOldA = resolve;
            });
          }
          if (calls === 3) {
            return Promise.resolve(
              heatmapFixture([{ ...BUSY_CELL, localDate: '2026-07-12' }], '2026-07-12'),
            );
          }
          if (calls === 4) {
            return new Promise<HeatmapDto>((resolve) => {
              resolveNewA = resolve;
            });
          }
          return Promise.reject(new Error(`unexpected heatmap call ${String(calls)}`));
        });

        renderPage();
        await act(async () => {
          await vi.advanceTimersByTimeAsync(0);
          await vi.advanceTimersByTimeAsync(15_000);
        });
        expect(calls).toBe(2);

        fireEvent.click(screen.getByRole('button', { name: '上一周' }));
        await act(async () => {
          await vi.advanceTimersByTimeAsync(0);
        });
        expect(calls).toBe(3);
        expect(screen.getByText(/2026-07-06/)).toBeInTheDocument();

        fireEvent.click(screen.getByRole('button', { name: '回到本周' }));
        await act(async () => {
          await vi.advanceTimersByTimeAsync(0);
        });
        expect(calls).toBe(4);

        await act(async () => {
          resolveOldA?.(heatmapFixture([{ ...BUSY_CELL, localHour: 8 }]));
          await Promise.resolve();
          await vi.advanceTimersByTimeAsync(15_000);
        });
        expect(calls).toBe(4);

        await act(async () => {
          resolveNewA?.(heatmapFixture([{ ...BUSY_CELL, localHour: 10 }]));
          await Promise.resolve();
        });
        expect(
          screen.getByRole('gridcell', {
            name: '7月19日 10时，活跃 1 小时，活跃程度 极高',
          }),
        ).toBeInTheDocument();
      } finally {
        vi.useRealTimers();
      }
    });

    it('切换周查询失败：进入错误四态，不得保留旧周内容', async () => {
      let calls = 0;
      invoke.mockImplementation((command: string) => {
        if (command === 'activity_get_heatmap') {
          calls += 1;
          if (calls === 1) {
            return Promise.resolve(heatmapFixture([BUSY_CELL]));
          }
          return Promise.reject(new Error('boom'));
        }
        return Promise.reject(new Error(`unexpected command: ${command}`));
      });
      renderPage();
      await waitFor(() => {
        expect(screen.getByRole('grid')).toBeInTheDocument();
      });

      fireEvent.click(screen.getByRole('button', { name: '上一周' }));
      await waitFor(() => {
        expect(screen.getByRole('alert')).toBeInTheDocument();
      });
      // 旧周网格必须卸载，不得出现在新周标题下。
      expect(screen.queryByRole('grid')).not.toBeInTheDocument();
      expect(screen.getByRole('button', { name: '重试' })).toBeInTheDocument();
    });
  });
});
