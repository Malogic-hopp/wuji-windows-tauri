import { act, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import StatsPage from './StatsPage';
import { bridgeClient } from '../../bridge/client';
import { i64, statsHomeEmptyFixture, statsHomeFixture, statsStatusFixture } from './statsFixture';
import type { HeatmapDto, StatsHomeDto, StatsStatusDto } from '../../types/wuji-core';

/** 阶段五：mock 桥接层，仅暴露统计两条命令；toSafeError 结构兼容 SafeError。 */
vi.mock('../../bridge/client', () => ({
  bridgeClient: {
    statsGetHome: vi.fn(),
    statsGetStatus: vi.fn(),
    activityGetHeatmap: vi.fn(),
  },
  toSafeError: (cause: unknown) => ({
    code: 'TEST_ERROR',
    message: cause instanceof Error ? cause.message : '未知错误',
  }),
}));

/** 推进 fake timers 并 flush 微任务（usePolling 的 immediate/interval + invoke promise）；
 *  多轮循环覆盖：home resolve → 渲染 → 轮询 immediate → status resolve 的链式微任务。 */
async function settle(): Promise<void> {
  for (let i = 0; i < 4; i += 1) {
    await act(async () => {
      await vi.advanceTimersByTimeAsync(0);
    });
  }
}

async function tick(ms: number): Promise<void> {
  await act(async () => {
    await vi.advanceTimersByTimeAsync(ms);
  });
}

describe('StatsPage 统计主页（阶段五双命令刷新）', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.mocked(bridgeClient.statsGetHome).mockResolvedValue(statsHomeFixture);
    vi.mocked(bridgeClient.statsGetStatus).mockResolvedValue(statsStatusFixture);
    vi.mocked(bridgeClient.activityGetHeatmap).mockResolvedValue(miniHeatmapFixture);
  });

  afterEach(() => {
    vi.clearAllMocks();
    vi.useRealTimers();
  });

  it('首次进入走 home 且直接 ready（不等 status；轮询随后立即跑一次 status）', async () => {
    render(<StatsPage />);
    expect(vi.mocked(bridgeClient.statsGetHome)).toHaveBeenCalledTimes(1);
    expect(vi.mocked(bridgeClient.statsGetHome)).toHaveBeenLastCalledWith(14);
    await settle();
    // home 返回即渲染全部区块，无需等 status
    expect(screen.getByText(/近 14 天活跃趋势/)).toBeInTheDocument();
    expect(screen.getByText('日均 3h30m · 最高 4h53m')).toBeInTheDocument();
    expect(screen.getByText(/本周累计/)).toBeInTheDocument();
    expect(screen.getByText(/累计记录 138 天/)).toBeInTheDocument();
    // ready 后轮询启用：immediate 立即调一次 status
    expect(vi.mocked(bridgeClient.statsGetStatus)).toHaveBeenCalled();
    await settle();
  });

  it('轮询只走 status 且只替换 live（home 不被重复调用；状态卡实时更新）', async () => {
    render(<StatsPage />);
    await settle();
    const homeCalls = vi.mocked(bridgeClient.statsGetHome).mock.calls.length;
    // live 更新：今日活跃 13,320,000 → 15,000,000（4h10m）
    const updated: StatsStatusDto = {
      ...statsStatusFixture,
      liveStatus: { ...statsStatusFixture.liveStatus, todayActiveMs: i64('15000000') },
    };
    vi.mocked(bridgeClient.statsGetStatus).mockResolvedValue(updated);
    await tick(5000);
    expect(vi.mocked(bridgeClient.statsGetHome)).toHaveBeenCalledTimes(homeCalls);
    expect(screen.getByText('4h10m')).toBeInTheDocument();
  });

  it('跨日：status.localDate ≠ home.localDate 自动触发 home 重查', async () => {
    render(<StatsPage />);
    await settle();
    const nextDay: StatsStatusDto = { ...statsStatusFixture, localDate: '2026-07-19' };
    vi.mocked(bridgeClient.statsGetStatus).mockResolvedValue(nextDay);
    await tick(5000);
    expect(vi.mocked(bridgeClient.statsGetHome)).toHaveBeenCalledTimes(2);
    await settle();
  });

  it('切换范围走 home（statsGetHome 携带新 days；标题联动）', async () => {
    render(<StatsPage />);
    await settle();
    fireEvent.click(screen.getByText('7 天'));
    expect(vi.mocked(bridgeClient.statsGetHome)).toHaveBeenLastCalledWith(7);
    await settle();
    expect(screen.getByText(/近 7 天活跃趋势/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: '7 天' }).getAttribute('aria-pressed')).toBe('true');
  });

  it('双通道防串：慢 home 在途时普通轮询只改 live，不废弃主页查询', async () => {
    render(<StatsPage />);
    await settle();
    let resolve7: ((home: StatsHomeDto) => void) | null = null;
    vi.mocked(bridgeClient.statsGetHome).mockImplementationOnce(
      () => new Promise<StatsHomeDto>((resolve) => { resolve7 = resolve; }),
    );
    fireEvent.click(screen.getByText('7 天'));
    // home 查询在途（pending）时推进轮询：status 照常、home 不被废弃
    await tick(5000);
    expect(vi.mocked(bridgeClient.statsGetStatus)).toHaveBeenCalled();
    // 慢 home 最终返回 → 应用（未被轮询顶掉）
    await act(async () => {
      resolve7?.({ ...statsHomeFixture, trend: statsHomeFixture.trend.slice(-7) });
      await Promise.resolve();
    });
    expect(screen.getByText(/近 7 天活跃趋势/)).toBeInTheDocument();
  });

  it('live.todayTrendPoint 覆盖今日柱（渲染选择器 F-4）', async () => {
    render(<StatsPage />);
    await settle();
    const updated: StatsStatusDto = {
      ...statsStatusFixture,
      todayTrendPoint: {
        ...statsStatusFixture.todayTrendPoint,
        activeDurationMs: i64('16000000'),
      },
    };
    vi.mocked(bridgeClient.statsGetStatus).mockResolvedValue(updated);
    await tick(5000);
    // 今日柱 aria 携带覆盖后的紧凑时长（16000000ms = 4h27m）
    expect(screen.getByLabelText(/2026-07-18 活跃 4h27m/)).toBeInTheDocument();
  });

  it('live.weekProgress 覆盖当前周柱（总量柱高随轮询更新）', async () => {
    const { container } = render(<StatsPage />);
    await settle();
    // 初始：current 58.32M，max = 72M（fixture 最高周；参考值 63M 不影响）→ 81%
    const before = container.querySelector('.week-bar--current') as HTMLElement;
    expect(before.style.height).toBe('81%');
    const updated: StatsStatusDto = {
      ...statsStatusFixture,
      weekProgress: { ...statsStatusFixture.weekProgress, currentActiveMs: i64('72000000') },
    };
    vi.mocked(bridgeClient.statsGetStatus).mockResolvedValue(updated);
    await tick(5000);
    // live 覆盖后 current = 72M == max → 100%
    const after = container.querySelector('.week-bar--current') as HTMLElement;
    expect(after.style.height).toBe('100%');
  });

  it('status 失败保留旧 home（页面不闪、home 不重查）', async () => {
    render(<StatsPage />);
    await settle();
    vi.mocked(bridgeClient.statsGetStatus).mockRejectedValue(new Error('轮询失败'));
    await tick(5000);
    expect(screen.getByText(/近 14 天活跃趋势/)).toBeInTheDocument();
    expect(vi.mocked(bridgeClient.statsGetHome)).toHaveBeenCalledTimes(1);
    await settle();
  });

  it('切换范围失败：保留旧图 + 非阻塞提示 + 范围恢复', async () => {
    render(<StatsPage />);
    await settle();
    vi.mocked(bridgeClient.statsGetHome).mockRejectedValueOnce(new Error('查询失败'));
    fireEvent.click(screen.getByText('30 天'));
    await settle();
    expect(screen.getByText(/刷新失败：查询失败/)).toBeInTheDocument();
    expect(screen.getByText(/近 14 天活跃趋势/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: '30 天' }).getAttribute('aria-pressed')).toBe('false');
    expect(screen.getByRole('button', { name: '14 天' }).getAttribute('aria-pressed')).toBe('true');
    await settle();
  });

  it('hasAnyData=false → 整页空状态，不渲染区块', async () => {
    vi.mocked(bridgeClient.statsGetHome).mockResolvedValue(statsHomeEmptyFixture);
    render(<StatsPage />);
    await settle();
    expect(screen.getByText('还没有记录数据，启动吾迹并保持打开即可开始')).toBeInTheDocument();
    expect(screen.queryByText(/近 14 天活跃趋势/)).not.toBeInTheDocument();
    await settle();
  });

  it('首次加载失败 → 整页 error + 重试成功', async () => {
    vi.mocked(bridgeClient.statsGetHome).mockRejectedValueOnce(new Error('首次失败'));
    render(<StatsPage />);
    await settle();
    expect(screen.getByText('首次失败')).toBeInTheDocument();
    expect(screen.getByText('重试')).toBeInTheDocument();
    vi.mocked(bridgeClient.statsGetHome).mockResolvedValue(statsHomeFixture);
    fireEvent.click(screen.getByText('重试'));
    await settle();
    expect(screen.getByText(/近 14 天活跃趋势/)).toBeInTheDocument();
  });

  it('P1-1 跨日慢 home 在途：后续轮询不重复取消重启（防饥饿）', async () => {
    render(<StatsPage />);
    await settle(); // home ready + 首次 status（07-18，应用 live）
    let resolveCrossDay: ((home: StatsHomeDto) => void) | null = null;
    vi.mocked(bridgeClient.statsGetHome).mockImplementationOnce(
      () => new Promise<StatsHomeDto>((resolve) => { resolveCrossDay = resolve; }),
    );
    const nextDay: StatsStatusDto = { ...statsStatusFixture, localDate: '2026-07-19' };
    vi.mocked(bridgeClient.statsGetStatus).mockResolvedValue(nextDay);
    await tick(5000); // 轮询 B 返回 07-19 → 跨日触发 → 慢 home 查询在途
    expect(vi.mocked(bridgeClient.statsGetHome)).toHaveBeenCalledTimes(2);
    // 慢 home 在途时继续推进两个轮询周期：不再重复取消重启（调用次数保持 2）
    await tick(10000);
    expect(vi.mocked(bridgeClient.statsGetHome)).toHaveBeenCalledTimes(2);
    // 跨日 home 最终落地 → 应用新日期
    await act(async () => {
      resolveCrossDay?.({ ...statsHomeFixture, localDate: '2026-07-19' });
      await Promise.resolve();
    });
    await tick(5000); // 同日轮询恢复 live 应用
    expect(vi.mocked(bridgeClient.statsGetHome)).toHaveBeenCalledTimes(2);
  });

  it('P1-2 跨日重查失败：不泄漏 suppress，用户下次范围切换正常工作', async () => {
    render(<StatsPage />);
    await settle();
    const nextDay: StatsStatusDto = { ...statsStatusFixture, localDate: '2026-07-19' };
    vi.mocked(bridgeClient.statsGetStatus).mockResolvedValue(nextDay);
    vi.mocked(bridgeClient.statsGetHome).mockRejectedValueOnce(new Error('跨日查询失败'));
    await tick(5000); // 轮询 B 跨日 → 重查失败（days 未变）
    expect(screen.getByText(/刷新失败：跨日查询失败/)).toBeInTheDocument();
    // 用户切 7 天：不被残留的 suppress 吞掉
    fireEvent.click(screen.getByText('7 天'));
    expect(vi.mocked(bridgeClient.statsGetHome)).toHaveBeenLastCalledWith(7);
    await settle();
    expect(screen.getByText(/近 7 天活跃趋势/)).toBeInTheDocument();
    expect(screen.queryByText(/刷新失败：跨日查询失败/)).not.toBeInTheDocument();
  });

  it('页面重新聚焦：触发 home 重查（刷新构成/月度快照，设计 10 §5.4）', async () => {
    render(<StatsPage />);
    await settle();
    const callsBefore = vi.mocked(bridgeClient.statsGetHome).mock.calls.length;
    // 模拟页面隐藏 → 恢复可见（visibilitychange）
    Object.defineProperty(document, 'hidden', { configurable: true, value: true });
    document.dispatchEvent(new Event('visibilitychange'));
    await settle();
    Object.defineProperty(document, 'hidden', { configurable: true, value: false });
    document.dispatchEvent(new Event('visibilitychange'));
    await settle();
    expect(vi.mocked(bridgeClient.statsGetHome).mock.calls.length).toBe(callsBefore + 1);
  });

  it('跨日显式双失效：更早发出的 status 旧响应（旧 gen）被丢弃，不覆盖 live', async () => {
    render(<StatsPage />);
    // 首次轮询 A（gen=5）慢：pending，稍后返回旧日（07-18）live
    let resolveA: ((status: StatsStatusDto) => void) | null = null;
    vi.mocked(bridgeClient.statsGetStatus).mockImplementationOnce(
      () => new Promise<StatsStatusDto>((resolve) => { resolveA = resolve; }),
    );
    await settle(); // home ready；immediate 轮询 A 发出（pending）
    const nextDay: StatsStatusDto = { ...statsStatusFixture, localDate: '2026-07-19' };
    vi.mocked(bridgeClient.statsGetStatus).mockResolvedValue(nextDay);
    await tick(5000); // 轮询 B（gen=5）返回 07-19 → 跨日 → statusGeneration 失效（5→6）
    // A 迟到返回旧日：应被丢弃（状态卡仍显示 home 派生的 3h42m，而非 1m）
    await act(async () => {
      resolveA?.({
        ...statsStatusFixture,
        liveStatus: { ...statsStatusFixture.liveStatus, todayActiveMs: i64('1000') },
      });
      await Promise.resolve();
    });
    expect(screen.queryByText('1m')).not.toBeInTheDocument();
    // 状态卡仍为 home 派生的 3h42m（未被旧日 live 覆盖）
    expect(screen.getByLabelText('今日截至 15:20 活跃 3 小时 42 分钟')).toBeInTheDocument();
  });
});

/** 缩小版热力图区块数据（今日列 level 4，其余零值；31 天窗口）。 */
const miniHeatmapFixture: HeatmapDto = {
  today: '2026-07-18',
  rangeEndLocalDate: '2026-07-18',
  reportingTimeZoneId: 'Asia/Shanghai',
  days: 31,
  cells: Array.from({ length: 24 }, (_, h) => ({
    localDate: '2026-07-18',
    localHour: h,
    activeDurationMs: i64('0'),
    idleDurationMs: i64('0'),
    unknownDurationMs: i64('0'),
    intensityLevel: h === 10 ? 4 : 0,
  })),
};

describe('StatsPage 缩小版热力图区块', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.mocked(bridgeClient.statsGetHome).mockResolvedValue(statsHomeFixture);
    vi.mocked(bridgeClient.statsGetStatus).mockResolvedValue(statsStatusFixture);
    vi.mocked(bridgeClient.activityGetHeatmap).mockResolvedValue(miniHeatmapFixture);
  });

  afterEach(() => {
    vi.clearAllMocks();
    vi.useRealTimers();
  });

  it('主页渲染缩小版热力图区块（activity 域独立拉取，31 天窗口）', async () => {
    render(<StatsPage />);
    await settle();
    expect(vi.mocked(bridgeClient.activityGetHeatmap)).toHaveBeenCalledWith(31);
    expect(screen.getByText('近 31 天活跃热力图')).toBeInTheDocument();
    expect(screen.getByLabelText(/近 31 天活跃热力图/)).toBeInTheDocument();
  });

  it('60 秒低频轮询刷新颜色深浅（时间块数据随当前小时活跃更新）', async () => {
    const { container } = render(<StatsPage />);
    await settle();
    const callsAfterMount = vi.mocked(bridgeClient.activityGetHeatmap).mock.calls.length;
    const level4Before = container.querySelectorAll('.heatmap-level--4').length;
    // 当前小时活跃增长：今天 10 点起全部 level 4（数据刷新后颜色深浅应变化）
    const updated: HeatmapDto = {
      ...miniHeatmapFixture,
      cells: Array.from({ length: 24 }, (_, h) => ({
        localDate: '2026-07-18',
        localHour: h,
        activeDurationMs: i64('0'),
        idleDurationMs: i64('0'),
        unknownDurationMs: i64('0'),
        intensityLevel: h >= 10 ? 4 : 0,
      })),
    };
    vi.mocked(bridgeClient.activityGetHeatmap).mockResolvedValue(updated);
    await tick(60_000);
    await settle();
    expect(vi.mocked(bridgeClient.activityGetHeatmap).mock.calls.length).toBe(callsAfterMount + 1);
    // 颜色深浅已刷新：level 4 格子随数据增加（原只有今天 10 点 1 格）
    expect(container.querySelectorAll('.heatmap-level--4').length).toBeGreaterThan(level4Before);
  });

  it('热力图失败只提示本区块，不阻塞主页', async () => {
    vi.mocked(bridgeClient.activityGetHeatmap).mockRejectedValue(new Error('热力图不可用'));
    render(<StatsPage />);
    await settle();
    expect(screen.getByText(/活跃热力图加载失败（热力图不可用）/)).toBeInTheDocument();
    expect(screen.getByText(/近 14 天活跃趋势/)).toBeInTheDocument();
  });
});
