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

function heatmapFixture(cells: HeatmapDto['cells']): HeatmapDto {
  return { today: '2026-07-19', reportingTimeZoneId: 'Asia/Shanghai', days: 7, cells };
}

const BUSY_CELL = {
  localDate: '2026-07-19',
  localHour: 9,
  activeDurationMs: i64('3600000'),
  idleDurationMs: i64('0'),
  unknownDurationMs: i64('0'),
  intensityLevel: 4,
};

function mockHeatmapRoute(cells: HeatmapDto['cells']) {
  invoke.mockImplementation((command: string) => {
    if (command === 'activity_get_heatmap') {
      return Promise.resolve(heatmapFixture(cells));
    }
    return Promise.reject(new Error(`unexpected command: ${command}`));
  });
}

/** useNavigate/useSearchParams 需要 Router 上下文。 */
function renderPage() {
  return render(
    <MemoryRouter>
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
    expect(invoke).toHaveBeenCalledWith('activity_get_heatmap', { days: null });
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
      expect(screen.getByText('最近 7 天还没有活跃记录')).toBeInTheDocument();
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
});
