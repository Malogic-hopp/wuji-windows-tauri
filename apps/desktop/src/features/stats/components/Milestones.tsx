import type { MilestoneDto, MonthlyPointDto } from '../../../types/wuji-core';
import { formatDeltaMs } from '../statsModel';

/** "2026-03" → "2026 年 3 月"（去前导零）。 */
function monthLabel(month: string): string {
  const [year, m] = month.split('-');
  return `${year} 年 ${String(Number(m))} 月`;
}

/**
 * ⑤ 长期追踪（10 §4.5）：里程碑条 + 近 6 月月度（每有效日均值）。
 * - firstRecordedMonth null → 不显示"自 X 月起"；
 * - 当前月进行中样式；recordedDays=0 月（无有效记录）→ 无均值标注。
 */
export function Milestones({
  milestone,
  monthly,
}: {
  milestone: MilestoneDto;
  monthly: readonly MonthlyPointDto[];
}) {
  const max = monthly.reduce(
    (m, p) => Math.max(m, Number(p.avgActiveMsPerRecordedDay ?? 0)),
    0,
  );
  return (
    <section className="milestones" aria-label="长期追踪">
      <h2 className="card__title">长期记录</h2>
      <div className="milestones__line">
        累计记录 {milestone.totalRecordedDays} 天
        {milestone.firstRecordedMonth !== null && (
          <>
            {' '}· 始于 {monthLabel(milestone.firstRecordedMonth)}
          </>
        )}
        {' '}· 最长连续记录 {milestone.longestConsecutiveDays} 天
      </div>
      {/* 指标说明在图表上方：用户先知道柱高口径（每有效日均值），再看图（review P2）。 */}
      <div className="milestones__hint">近 6 个月日均活跃（按有效记录日）</div>
      <div className="chart__body chart__body--monthly">
        {monthly.map((point) => {
          const value = point.avgActiveMsPerRecordedDay;
          const height =
            max > 0 && value != null
              ? Math.round((Number(value) / max) * 100)
              : 0;
          return (
            <div key={point.month} className="chart__bar-slot">
              <div
                className={
                  point.isCurrentMonth
                    ? 'month-bar month-bar--current'
                    : value == null
                      ? 'month-bar month-bar--empty'
                      : 'month-bar'
                }
                style={{ height: `${String(height)}%` }}
                role="img"
                tabIndex={0}
                aria-label={`${monthLabel(point.month)} 每有效日均值 ${
                  value != null ? formatDeltaMs(value) : '无有效记录日'
                }${point.isCurrentMonth ? '，进行中' : ''}`}
                title={
                  value != null
                    ? `${formatDeltaMs(value)} / ${String(point.recordedDays)} 个有效日${
                        point.isCurrentMonth ? '（进行中）' : ''
                      }`
                    : '无有效记录日'
                }
              />
            </div>
          );
        })}
      </div>
      <div className="month-ticks" aria-hidden="true">
        {monthly.map((point) => (
          <span key={point.month}>{Number(point.month.slice(5))}月</span>
        ))}
      </div>
      <div className="milestones__legend">
        <span className="legend-chip legend-chip--today">当前月进行中</span>
        {monthly.some((point) => point.avgActiveMsPerRecordedDay == null) && (
          <span className="legend-chip legend-chip--nodata">无有效记录</span>
        )}
      </div>
    </section>
  );
}
