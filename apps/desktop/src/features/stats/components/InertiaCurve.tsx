import type { HourlyPointDto, InertiaDto } from '../../../types/wuji-core';
import { formatDeltaMs, mapReliabilityText } from '../statsModel';

const HOUR_TICKS = [0, 6, 12, 18, 24];

/**
 * ④ 工作惯性（10 §4.4）：24 小时均值曲线（SVG 面积）+ 标注。
 * - reliability null（有效日 < 3）→ 不画曲线，提示"有效记录日不足"；
 * - 全零曲线（峰值缺失）→ 画空曲线但不标注（不得伪造开工/收工）；
 * - 标注含有效天数与缺失日期数；6/12/18/24 小时刻度；每柱 aria 携带数值。
 */
export function InertiaCurve({
  points,
  inertia,
}: {
  points: readonly HourlyPointDto[];
  inertia: InertiaDto;
}) {
  const max = points.reduce((m, p) => Math.max(m, Number(p.avgActiveMs)), 0);
  const allZero = max <= 0;
  const reliabilityText = mapReliabilityText(inertia.reliability);
  const missingDays = inertia.totalDays - inertia.effectiveDays;
  return (
    <figure className="chart chart--inertia" aria-label="工作惯性（近 14 天 24 小时曲线）">
      {inertia.reliability === null ? (
        <div className="state-block__title state-block__title--small">
          有效记录日不足，无法显示工作惯性
        </div>
      ) : (
        <>
          <div className="chart__inertia-wrap">
            <svg
              className="chart__inertia-svg"
              viewBox="0 0 240 100"
              preserveAspectRatio="none"
              role="img"
              tabIndex={0}
              aria-label={`24 小时活跃均值曲线（最大值 ${String(max)}ms）`}
            >
              {points.map((point, index) => {
                const height =
                  max > 0 ? Math.round((Number(point.avgActiveMs) / max) * 100) : 0;
                return (
                  <rect
                    key={point.localHour}
                    x={index * 10}
                    y={100 - height}
                    width={9}
                    height={height}
                    className="inertia-bar"
                  >
                    <title>{`${String(point.localHour)} 点均值 ${formatDeltaMs(point.avgActiveMs)}`}</title>
                  </rect>
                );
              })}
            </svg>
            <div className="chart__inertia-ticks" aria-hidden="true">
              {HOUR_TICKS.map((hour) => (
                <span key={hour} style={{ left: `${String((hour / 24) * 100)}%` }}>
                  {hour}
                </span>
              ))}
            </div>
          </div>
          {/* 底部信息条：紧凑 dim 小字，不用图例色块（开工/高峰等是文本信息，非系列图例）。 */}
          <figcaption className="inertia-info">
            {!allZero && inertia.startHour != null && (
              <span>开工约 {String(inertia.startHour)}:00</span>
            )}
            {!allZero && inertia.peakHour != null && (
              <span>
                高峰 {String(inertia.peakHour)}–{String(inertia.peakHour + 1)} 点
              </span>
            )}
            {!allZero && inertia.endHour != null && (
              <span>收工约 {String(inertia.endHour)}:00</span>
            )}
            {!allZero && inertia.lunchLowestHour != null && (
              <span>午休低谷 {String(inertia.lunchLowestHour)} 点</span>
            )}
            <span>
              有效样本日 {String(inertia.effectiveDays)}/{String(inertia.totalDays)}
              {missingDays > 0 ? `（${String(missingDays)} 天未纳入）` : ''}
            </span>
            {reliabilityText !== '' && <span>{reliabilityText}</span>}
          </figcaption>
        </>
      )}
    </figure>
  );
}
