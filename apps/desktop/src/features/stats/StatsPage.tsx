import { useCallback, useState } from 'react';
import type { HeatmapDto } from '../../types/wuji-core';
import { coverageLabel, isHomeEmpty, trendSummaryLabel, weeklySummaryLabel } from './statsModel';
import { useStatsHome } from './useStatsHome';
import { bridgeClient, toSafeError, type SafeError } from '../../bridge/client';
import { useDocumentVisible, usePolling } from '../../lib/polling';
import { StatusCard } from './components/StatusCard';
import { TrendChart } from './components/TrendChart';
import { WeeklyChart, WeekProgressCard } from './components/WeeklyChart';
import { InertiaCurve } from './components/InertiaCurve';
import { AppComposition } from './components/AppComposition';
import { Milestones } from './components/Milestones';
import { MiniHeatmap } from './components/MiniHeatmap';
import './StatsPage.css';

/** 缩小版热力图刷新间隔（低频，与热力图页"当前周约 15 秒"同档；31 天聚合成本中等）。 */
const HEATMAP_POLL_MS = 60_000;

/**
 * 统计主页（10 设计 §5、11 实施方案阶段五）：双命令刷新容器。
 * - 首次 home 成功即 ready；5s 轮询只替换 live（状态卡/今日柱/当前周柱）；
 * - 渲染选择器合并（阶段零 F-4）：live.todayTrendPoint 覆盖今日柱、
 *   live.weekProgress.currentActiveMs 覆盖当前周柱，不改写 home.trend/home.weekly；
 * - 区块即 ①主卡（今日状态 | 本周进度）②近 N 天活跃趋势 ③近 12 周活跃总量
 *   ④双列独立卡（工作惯性 | 应用构成）⑤长期记录。
 */
export default function StatsPage() {
  const { model, days, switchDays, retry } = useStatsHome();
  // 缩小版热力图（activity 域）：60 秒低频轮询（与热力图页"当前周约 15 秒"同档低频），
  // 跟随页面可见性（隐藏停止、聚焦立即刷新）；失败保留旧图不阻塞主页。
  // 说明：今天列当前小时描边是本地计算（随渲染更新），颜色深浅必须靠重拉数据刷新。
  const visible = useDocumentVisible();
  const [heatmapModel, setHeatmapModel] = useState<
    | { phase: 'loading' }
    | { phase: 'ready'; heatmap: HeatmapDto }
    | { phase: 'error'; error: SafeError }
  >({ phase: 'loading' });
  const refreshHeatmap = useCallback(async () => {
    try {
      const heatmap = await bridgeClient.activityGetHeatmap(31);
      setHeatmapModel({ phase: 'ready', heatmap });
    } catch (cause) {
      setHeatmapModel((current) =>
        current.phase === 'ready' ? current : { phase: 'error', error: toSafeError(cause) },
      );
    }
  }, []);
  usePolling(refreshHeatmap, HEATMAP_POLL_MS, visible);

  if (model.phase === 'loading') {
    return (
      <div className="page stats-page">
        <h1 className="page__title">活动概览</h1>
        <div className="state-block">
          <div className="state-block__title">正在加载…</div>
        </div>
      </div>
    );
  }
  if (model.phase === 'error') {
    return (
      <div className="page stats-page">
        <h1 className="page__title">活动概览</h1>
        <div className="state-block">
          <div className="state-block__title">{model.error.message}</div>
          <button className="button" type="button" onClick={retry}>
            重试
          </button>
        </div>
      </div>
    );
  }

  const { home, live, refreshState, refreshError } = model;
  if (isHomeEmpty(home)) {
    return (
      <div className="page stats-page">
        <h1 className="page__title">活动概览</h1>
        <div className="state-block">
          <div className="state-block__title">
            还没有记录数据，启动吾迹并保持打开即可开始
          </div>
        </div>
      </div>
    );
  }

  // 渲染选择器合并（不改写 home 数据）：
  // - 今日柱用 live.todayTrendPoint（轮询后与状态卡同口径）；
  // - 当前周柱总量用 live.weekProgress.currentActiveMs（实心+今日弱化随之更新）。
  const visibleTrend = home.trend.map((p) => (p.isToday ? live.todayTrendPoint : p));
  const weeklyPoints = home.weekly.map((w) =>
    w.isCurrentWeek ? { ...w, activeDurationMs: live.weekProgress.currentActiveMs } : w,
  );
  const cutoffLocalTime = live.status.cutoffLocalTime;

  return (
    <div className="page stats-page">
      <div className="stats-page__head">
        <h1 className="page__title">活动概览</h1>
        <span className="stats-page__coverage">{coverageLabel(home.trend, days)}</span>
      </div>
      {refreshState === 'error' && refreshError != null && (
        <div className="stats-page__refresh-error" role="status">
          刷新失败：{refreshError.message}（保留上次数据）
        </div>
      )}
      {/* ① 主卡：今日状态（左）| 本周进度（右，分隔线）。 */}
      <div className="card card--split">
        <div className="card--split__main">
          <StatusCard live={live.status} summary={home.status.summary} />
        </div>
        <div className="card--split__side">
          <WeekProgressCard weekProgress={live.weekProgress} />
        </div>
      </div>
      {/* ② 趋势：开放区块（无卡片），切换器在标题区。 */}
      <section className="stats-block">
        <div className="stats-block__head">
          <h2 className="card__title">
            近 {String(days)} 天活跃趋势
            {trendSummaryLabel(visibleTrend) !== '' && (
              <span className="chart-summary">{trendSummaryLabel(visibleTrend)}</span>
            )}
          </h2>
          <div className="stats-block__switcher">
            <div className="segmented" role="group" aria-label="趋势范围">
              {([7, 14, 30] as const).map((d) => (
                <button
                  key={d}
                  type="button"
                  className="segmented__item"
                  aria-pressed={d === days}
                  onClick={() => { switchDays(d); }}
                >
                  {d} 天
                </button>
              ))}
            </div>
          </div>
        </div>
        <TrendChart
          points={visibleTrend}
          days={days}
          cutoffLocalTime={cutoffLocalTime}
        />
      </section>
      {/* ③ 近 12 周活跃总量：开放区块，全宽。 */}
      <section className="stats-block">
        <h2 className="card__title">
          近 12 周活跃总量
          {weeklySummaryLabel(home.weekly) !== '' && (
            <span className="chart-summary">{weeklySummaryLabel(home.weekly)}</span>
          )}
        </h2>
        <WeeklyChart points={weeklyPoints} weekProgress={live.weekProgress} />
      </section>
      {/* ④ 模式 = 双列独立卡。 */}
      <section className="stats-block stats-block--split">
        <div className="card">
          <h2 className="card__title">工作惯性（近 14 天）</h2>
          <div className="card__sub">按有效样本日平均的 24 小时活跃分布</div>
          <InertiaCurve points={home.hourlyProfile} inertia={home.inertia} workPace={home.workPace} />
        </div>
        <div className="card">
          <h2 className="card__title">近 14 天应用构成</h2>
          <AppComposition
            buckets={home.composition}
            palette={home.palette}
            cutoffLocalTime={cutoffLocalTime}
          />
        </div>
      </section>
      {/* ⑤ 长期记录（底部轻量区，现状保留）。 */}
      <Milestones milestone={home.milestone} monthly={home.monthly} />
      {/* ⑥ 缩小版热力图（产品扩展）：activity 域低频快照，仅本区块失败提示。 */}
      <section className="stats-block">
        {heatmapModel.phase === 'ready' && (
          <>
            <h2 className="card__title">近 {String(heatmapModel.heatmap.days)} 天活跃热力图</h2>
            <MiniHeatmap heatmap={heatmapModel.heatmap} />
          </>
        )}
        {heatmapModel.phase === 'loading' && (
          <h2 className="card__title">活跃热力图（加载中…）</h2>
        )}
        {heatmapModel.phase === 'error' && (
          <h2 className="card__title">活跃热力图加载失败（{heatmapModel.error.message}）</h2>
        )}
      </section>
    </div>
  );
}
