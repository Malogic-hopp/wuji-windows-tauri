import type {
  HourlyPointDto,
  InertiaDto,
  WorkPaceDto,
} from '../../../types/wuji-core';
import { formatDeltaMs, formatMinutesDuration, formatMinutesOfDay, mapReliabilityText } from '../statsModel';

const HOUR_TICKS = [0, 6, 12, 18, 23];

/**
 * ④ 工作惯性（10 §4.4）：24 小时均值曲线（SVG 面积）+ 标注。
 * v0.2 候选融合（工作节奏）：底部工作/未工作占比条（未工作段着色）+ 常见工作
 * 时段（中位开工→中位收工）+ 上午利用率（8-12 点有工作的天数）。
 * - 常见开工/收工只统计当天窗口内真实覆盖段（熬夜尾巴不参与开工；收工截到 24:00）；
 * - 不伪造"休息时段块"（用户作息碎片化时任何 Night/Midday 聚合都是伪结构）；
 * - reliability null（有效日 < 3）→ 不画曲线，提示"有效记录日不足"；
 * - 全零曲线（峰值缺失）→ 画空曲线但不标注（不得伪造开工/收工）。
 */
export function InertiaCurve({
  points,
  inertia,
  workPace,
}: {
  points: readonly HourlyPointDto[];
  inertia: InertiaDto;
  workPace: WorkPaceDto;
}) {
  const max = points.reduce((m, p) => Math.max(m, Number(p.avgActiveMs)), 0);
  const allZero = max <= 0;
  const reliabilityText = mapReliabilityText(inertia.reliability);
  const missingDays = inertia.totalDays - inertia.effectiveDays;
  const paceUsable = workPace.reliability !== null && workPace.effectiveDays > 0;
  const workWindow =
    paceUsable && workPace.commonStartMinutes != null && workPace.commonEndMinutes != null
      ? `${formatMinutesOfDay(workPace.commonStartMinutes)}–${formatMinutesOfDay(
          workPace.commonEndMinutes,
        )}`
      : '';
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
                <span
                  key={hour}
                  style={{
                    // 刻度对齐柱体中心：柱 H 代表 [H, H+1) 小时，中心 = H+0.5。
                    left: `${String(((hour + 0.5) / 24) * 100)}%`,
                  }}
                >
                  {hour}
                </span>
              ))}
            </div>
          </div>
          {paceUsable && (
            <div className="inertia-pace">
              <div
                className="inertia-pace__bar"
                role="img"
                aria-label={`工作占 ${String(workPace.workRatioPercent)}%，未工作占 ${String(100 - workPace.workRatioPercent)}%`}
              >
                <span
                  className="inertia-pace__work"
                  style={{ width: `${String(workPace.workRatioPercent)}%` }}
                />
              </div>
              <div className="inertia-pace__text">
                工作 {String(workPace.workRatioPercent)}%（日均{' '}
                {formatMinutesDuration(
                  Math.round((24 * 60 * workPace.workRatioPercent) / 100),
                )}
                ）· 未工作 {String(100 - workPace.workRatioPercent)}%
              </div>
              {workWindow !== '' && (
                <div className="inertia-pace__rest">
                  常见工作时段 {workWindow} · 上午(8-12点)有工作{' '}
                  {String(workPace.morningWorkDays)}/{String(workPace.effectiveDays)} 天
                </div>
              )}
            </div>
          )}
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
