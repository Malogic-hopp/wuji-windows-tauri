import type { TrendPointDto } from '../../../types/wuji-core';
import { formatDeltaMs } from '../statsModel';

interface Segment {
  readonly points: ReadonlyArray<{ x: number; y: number }>;
}

/** 均线折线：null（断开）处切段；viewBox 0..100 横向映射到柱槽。 */
function buildMaSegments(points: readonly TrendPointDto[], maxMs: number): Segment[] {
  const segments: Segment[] = [];
  let current: Array<{ x: number; y: number }> = [];
  points.forEach((point, index) => {
    const x = (index + 0.5) * (100 / points.length);
    if (point.movingAvg7ActiveMs == null) {
      if (current.length >= 2) segments.push({ points: current });
      current = [];
      return;
    }
    const ms = Number(point.movingAvg7ActiveMs);
    const y = maxMs > 0 ? 100 - (ms / maxMs) * 100 : 100;
    current.push({ x, y });
  });
  if (current.length >= 2) segments.push({ points: current });
  return segments;
}

function maxOf(points: readonly TrendPointDto[]): number {
  // 纵轴同时纳入柱与有效均线值（P2-01）：均线高于全部柱时不得算出负 y。
  return points.reduce(
    (max, p) =>
      Math.max(
        max,
        Number(p.activeDurationMs),
        p.movingAvg7ActiveMs != null ? Number(p.movingAvg7ActiveMs) : 0,
      ),
    0,
  );
}

/**
 * ② 活跃趋势（10 §4.2）：柱状 + 7 日均线 overlay。
 * - 今日柱进行中斜纹 + "截至 HH:MM"标签（设计 §4.2/§9 P0-4）；
 * - hasData=false 槽位始终显示最小可见斜纹占位（与"有记录但活跃 0"区分）；
 * - 均线直接渲染 DTO 值（null 断开）；柱高前端归一化（渲染职责）；
 * - 柱/条可键盘 focus（tabIndex + :focus-visible），aria-label 携带数值。
 */
export function TrendChart({
  points,
  days,
  cutoffLocalTime,
}: {
  points: readonly TrendPointDto[];
  days: number;
  cutoffLocalTime: string;
}) {
  const max = maxOf(points);
  const segments = buildMaSegments(points, max);
  const hasMa = segments.length > 0;
  // 时间锚点：首点 / 中间点日期（MM-DD）+ 右端"今天"（纯展示，aria-hidden）。
  const firstPoint = points.length > 0 ? points[0] : undefined;
  const midPoint = points.length > 0 ? points[Math.floor(points.length / 2)] : undefined;
  return (
    <figure className="chart" aria-label={`近 ${String(days)} 天活跃时长趋势`}>
      <div className="chart__body chart__body--trend">
        {points.map((point) => {
          const height = max > 0 ? Math.round((Number(point.activeDurationMs) / max) * 100) : 0;
          if (!point.hasData) {
            return (
              <div key={point.localDate} className="trend-bar__slot trend-bar__slot--nodata">
                <div
                  className="trend-bar trend-bar--nodata"
                  tabIndex={0}
                  role="img"
                  aria-label={`${point.localDate} 当日无记录数据`}
                  title="当日无记录数据"
                />
              </div>
            );
          }
          const cls = point.isToday ? 'trend-bar trend-bar--today' : 'trend-bar';
          const todaySuffix =
            point.isToday && cutoffLocalTime !== '' ? `，进行中（截至 ${cutoffLocalTime}）` : '';
          return (
            <div key={point.localDate} className="trend-bar__slot">
              {/* 百分比高度在柱体 inline（槽位恒 100%），几何可测且不会退化成 2px（P1-01） */}
              <div
                className={cls}
                style={{ height: `${String(height)}%` }}
                tabIndex={0}
                role="img"
                aria-label={`${point.localDate} 活跃 ${formatDeltaMs(point.activeDurationMs)}${todaySuffix}`}
                title={point.isToday ? `今日进行中（截至 ${cutoffLocalTime}）` : undefined}
              />
            </div>
          );
        })}
        {hasMa && (
          <svg
            className="chart__ma"
            viewBox="0 0 100 100"
            preserveAspectRatio="none"
            aria-hidden="true"
          >
            {segments.map((segment, index) => (
              <polyline
                key={index}
                points={segment.points.map((p) => `${String(p.x)},${String(p.y)}`).join(' ')}
                className="chart__ma-line"
              />
            ))}
          </svg>
        )}
      </div>
      {firstPoint != null && midPoint != null && (
        <div className="trend-ticks" aria-hidden="true">
          <span>{firstPoint.localDate.slice(5)}</span>
          <span>{midPoint.localDate.slice(5)}</span>
          <span>今天</span>
        </div>
      )}
      <figcaption className="chart__legend">
        <span className="legend-chip legend-chip--today">今日进行中</span>
        <span className="legend-chip legend-chip--nodata">当日无记录数据</span>
        {hasMa && <span className="legend-chip legend-chip--ma">7 日均线</span>}
      </figcaption>
    </figure>
  );
}
