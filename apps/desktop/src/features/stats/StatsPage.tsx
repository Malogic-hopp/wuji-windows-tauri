import { useState } from 'react';
import type { StatsHomeDto } from '../../types/wuji-core';
import { coverageLabel, isHomeEmpty, trendSummaryLabel, weeklySummaryLabel } from './statsModel';
import { statsHomeFixture, statsHomeWeekFixture } from './statsFixture';
import { StatusCard } from './components/StatusCard';
import { TrendChart } from './components/TrendChart';
import { WeeklyChart, WeekProgressCard } from './components/WeeklyChart';
import { InertiaCurve } from './components/InertiaCurve';
import { AppComposition } from './components/AppComposition';
import { Milestones } from './components/Milestones';
import './StatsPage.css';

/**
 * 统计主页（10 设计 §5、11 实施方案阶段四；阶段四外部评审布局定稿）。
 * 静态布局阶段以 fixture 渲染（stats 默认 statsHomeFixture），阶段五接入真实命令
 * 与双命令轮询模型；区块即 ①主卡（今日状态 | 本周进度）②近 N 天活跃趋势
 * ③近 12 周活跃总量 ④双列独立卡（工作惯性 | 应用构成）⑤长期记录。
 */
export default function StatsPage({ stats = statsHomeFixture }: { stats?: StatsHomeDto }) {
  // 选择范围固定为 14 天默认（与后端返回点数解耦）；覆盖胶囊单独说明记录日。
  const [activeDays, setActiveDays] = useState<7 | 14 | 30>(14);
  if (isHomeEmpty(stats)) {
    return (
      <div className="page">
        <h1 className="page__title">活动概览</h1>
        <div className="state-block">
          <div className="state-block__title">
            还没有记录数据，启动吾迹并保持打开即可开始
          </div>
        </div>
      </div>
    );
  }
  // 切换器静态阶段行为（阶段五接入 stats_get_home 重查）：
  // 7 天 = 主 fixture 尾部 7 点；14 天 = 主 fixture 原样；
  // 30 天 = 整体换用 statsHomeWeekFixture.trend（30 点趋势）。
  // TODO(阶段五)：接入 stats_get_home 后删除本分支——静态阶段允许 fixture 混用，
  // 接真实命令后 30 天不得再回退演示数据（review 代码层 2）。
  const trendPoints =
    activeDays === 30
      ? statsHomeWeekFixture.trend
      : activeDays === 7
        ? stats.trend.slice(-7)
        : stats.trend;
  // 方案 A（review-2）：切换器只控制活跃趋势；应用构成固定为近 14 天日桶，
  // 避免"局部位置、跨区块生效"的误读（30 天构成周桶留待构成卡内部自己的范围控制）。
  const compBuckets = stats.composition;
  return (
    <div className="page stats-page">
      <div className="stats-page__head">
        <h1 className="page__title">活动概览</h1>
        <span className="stats-page__coverage">{coverageLabel(trendPoints, activeDays)}</span>
      </div>
      {/* ① 主卡：今日状态（左）| 本周进度（右，分隔线）。 */}
      <div className="card card--split">
        <div className="card--split__main">
          <StatusCard live={stats.status} summary={stats.status.summary} />
        </div>
        <div className="card--split__side">
          <WeekProgressCard weekProgress={stats.weekProgress} />
        </div>
      </div>
      {/* ② 趋势：开放区块（无卡片），切换器在标题区。 */}
      <section className="stats-block">
        <div className="stats-block__head">
          <h2 className="card__title">
            近 {String(activeDays)} 天活跃趋势
            {trendSummaryLabel(trendPoints) !== '' && (
              <span className="chart-summary">{trendSummaryLabel(trendPoints)}</span>
            )}
          </h2>
          <div className="stats-block__switcher">
            <div className="segmented" role="group" aria-label="趋势范围">
              {([7, 14, 30] as const).map((d) => (
                <button
                  key={d}
                  type="button"
                  className="segmented__item"
                  aria-pressed={d === activeDays}
                  onClick={() => { setActiveDays(d); }}
                >
                  {d} 天
                </button>
              ))}
            </div>
          </div>
        </div>
        <TrendChart
          points={trendPoints}
          days={activeDays}
          cutoffLocalTime={stats.status.cutoffLocalTime}
        />
      </section>
      {/* ③ 近 12 周活跃总量：开放区块，全宽。 */}
      <section className="stats-block">
        <h2 className="card__title">
          近 12 周活跃总量
          {weeklySummaryLabel(stats.weekly) !== '' && (
            <span className="chart-summary">{weeklySummaryLabel(stats.weekly)}</span>
          )}
        </h2>
        <WeeklyChart points={stats.weekly} weekProgress={stats.weekProgress} />
      </section>
      {/* ④ 模式 = 双列独立卡。 */}
      <section className="stats-block stats-block--split">
        <div className="card">
          <h2 className="card__title">工作惯性（近 14 天）</h2>
          <div className="card__sub">按有效样本日平均的 24 小时活跃分布</div>
          <InertiaCurve points={stats.hourlyProfile} inertia={stats.inertia} />
        </div>
        <div className="card">
          <h2 className="card__title">近 14 天应用构成</h2>
          <AppComposition
            buckets={compBuckets}
            palette={stats.palette}
            cutoffLocalTime={stats.status.cutoffLocalTime}
          />
        </div>
      </section>
      {/* ⑤ 长期记录（底部轻量区，现状保留）。 */}
      <Milestones milestone={stats.milestone} monthly={stats.monthly} />
    </div>
  );
}
