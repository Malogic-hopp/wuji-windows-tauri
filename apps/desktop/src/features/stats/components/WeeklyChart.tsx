import type { Int64String, WeeklyPointDto, WeekProgressDto } from '../../../types/wuji-core';
import { formatDeltaMs, mapDirectionDisplay } from '../statsModel';

function maxOf(points: readonly WeeklyPointDto[]): number {
  return points.reduce((max, p) => Math.max(max, Number(p.activeDurationMs)), 0);
}

/**
 * ③ 近 12 周活跃柱状 + 本周进度（10 §4.3）：
 * - 当前周 = 实心（已完成完整日）+ 弱化（今日截至当前时刻）两段式（§9 P0-5）；
 * - 虚框参考线 = 当前周日均 × 7（completedRecordedDays=0 时不显示，改提示
 *   "本周进行中，暂无稳定参考"）；上周柱顶标注参照锚点；
 * - 柱可键盘 focus，aria-label 携带数值。
 */
export function WeeklyChart({
  points,
  weekProgress,
}: {
  points: readonly WeeklyPointDto[];
  weekProgress: WeekProgressDto;
}) {
  const current = points.find((p) => p.isCurrentWeek);
  const totalMs = current != null ? Number(current.activeDurationMs) : 0;
  const completedMs =
    current != null && current.completedRecordedDays > 0 && current.currentWeekDailyAvgMs != null
      ? Number(current.currentWeekDailyAvgMs) * current.completedRecordedDays
      : 0;
  // 今日弱化部分 = 总量 - 已完成（completedRecordedDays=0 时整柱即今日部分）。
  const todayMs = Math.max(0, totalMs - completedMs);
  // 纵轴同时纳入参考值（P2-01）：参考值高于历史最大周时按真实相对高度表达，不截断。
  const refValue =
    current != null && current.completedRecordedDays > 0 && current.currentWeekDailyAvgMs != null
      ? Number(current.currentWeekDailyAvgMs) * 7
      : 0;
  const max = Math.max(maxOf(points), refValue);
  const refHeight =
    current?.currentWeekDailyAvgMs != null && current.completedRecordedDays > 0
      ? Math.min(100, Math.round((refValue / max) * 100))
      : null;
  return (
    <figure className="chart" aria-label="近 12 周活跃总量">
      <div className="chart__body chart__body--weekly">
        {refHeight != null && (
          <div
            className="chart__ref"
            style={{ bottom: `${String(refHeight)}%` }}
            title={`按已完成记录日的日均值推算：约 ${formatDeltaMs(String(refValue) as Int64String)}`}
          >
            <span className="chart__ref-label">本周日均推算</span>
          </div>
        )}
        {points.map((point, index) => {
          const height = max > 0 ? Math.round((Number(point.activeDurationMs) / max) * 100) : 0;
          const isPrev = index === points.length - 2;
          if (point.isCurrentWeek) {
            // 两段百分比按当前周总量归一（P1-1 双缩放修复）；总量柱高 = 总量/max 在柱体 inline。
            const totalPct = max > 0 ? Math.round((totalMs / max) * 100) : 0;
            const completedPct = totalMs > 0 ? Math.round((completedMs / totalMs) * 100) : 0;
            const todayPct = totalMs > 0 ? Math.round((todayMs / totalMs) * 100) : 0;
            const aria = `${point.weekStartDate} 周活跃 ${formatDeltaMs(point.activeDurationMs)}，进行中（截至 ${weekProgress.cutoffLocalTime}）`;
            const title = `进行中（截至 ${weekProgress.cutoffLocalTime}）：已完成 ${formatDeltaMs(String(completedMs) as Int64String)} + 今日 ${formatDeltaMs(String(todayMs) as Int64String)}`;
            return (
              <div key={point.weekStartDate} className="chart__bar-slot">
                <div
                  className="week-bar week-bar--current"
                  style={{ height: `${String(totalPct)}%` }}
                  tabIndex={0}
                  role="img"
                  aria-label={aria}
                  title={title}
                >
                  {todayMs > 0 && (
                    <div className="week-bar__today" style={{ height: `${String(todayPct)}%` }} />
                  )}
                  {completedMs > 0 && (
                    <div
                      className="week-bar__completed"
                      style={{ height: `${String(completedPct)}%` }}
                    />
                  )}
                </div>
              </div>
            );
          }
          return (
            <div key={point.weekStartDate} className="chart__bar-slot">
              <div
                className={isPrev ? 'week-bar week-bar--prev' : 'week-bar'}
                style={{ height: `${String(height)}%` }}
                tabIndex={0}
                role="img"
                aria-label={`${point.weekStartDate} 周活跃 ${formatDeltaMs(point.activeDurationMs)}`}
              >
                {isPrev && <span className="week-bar__anchor">上周</span>}
              </div>
            </div>
          );
        })}
      </div>
      {/* 月份边界刻度：首柱与跨月柱标注"N月"；与柱槽同一 flex 布局保证对齐（纯展示）。 */}
      <div className="week-ticks" aria-hidden="true">
        {points.map((point, index) => {
          const month = Number(point.weekStartDate.slice(5, 7));
          const prevMonth =
            index > 0 ? Number(points[index - 1]?.weekStartDate.slice(5, 7)) : null;
          const show = index === 0 || month !== prevMonth;
          return <span key={point.weekStartDate}>{show ? `${String(month)}月` : ''}</span>;
        })}
      </div>
      <figcaption className="chart__legend">
        <span className="legend-chip legend-chip--today">当前周进行中</span>
        {refHeight != null && <span className="legend-chip legend-chip--ref">本周日均推算</span>}
      </figcaption>
      {current != null && current.completedRecordedDays === 0 && (
        <div className="chart__note">本周进行中，暂无稳定参考</div>
      )}
    </figure>
  );
}

/**
 * 本周进度小卡（10 §4.3）：本周截至今日总量 + 上周同期比较；上周无数据时隐藏比较行。
 */
export function WeekProgressCard({ weekProgress }: { weekProgress: WeekProgressDto }) {
  const lastWeek = mapDirectionDisplay(weekProgress.lastWeekSame, weekProgress.currentActiveMs);
  return (
    <section className="week-progress" aria-label="本周进度">
      <div className="week-progress__head">本周累计</div>
      {/* 主数字 20-22px（小于今日 30px），与说明文字层级分开（review-2 主卡）。 */}
      <div className="week-progress__main">{formatDeltaMs(weekProgress.currentActiveMs)}</div>
      {lastWeek.text !== '' && (
        <div className="week-progress__cmp">
          较上周同期 <span className="stats-status__dir">{lastWeek.text}</span>
        </div>
      )}
      <div className="week-progress__days">记录 {String(weekProgress.recordedDays)} 天</div>
    </section>
  );
}
