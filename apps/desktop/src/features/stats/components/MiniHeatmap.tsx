import type { HeatmapDto } from '../../../types/wuji-core';
import {
  buildGrid,
  currentLocalHour,
  getTimeOfDayLabel,
  heatmapIntensityLabels,
  isHourPeriodEnd,
  normalizeIntensityLevel,
} from '../../heatmap/heatmapModel';

/**
 * 主页缩小版热力图（产品扩展，2026-08-04）：24 小时 × N 天缩略网格。
 * - 行结构与原热力图一致：左侧时段标签（3/9/15/21 点显示凌晨/上午/下午/晚上）、
 *   5/11/17 点行下画每 6 小时一段的分隔线；
 * - 列宽 1fr 均分区块宽度（横向填满），行高由格子 10px 决定；
 * - 复用 activity 域纯函数（buildGrid/强度归一化/时段标签）与全局 heatmap-level--N 颜色；
 * - 今天列当前小时描边；缩略图语义：不带键盘格子导航/逐格 title。
 */
export function MiniHeatmap({ heatmap }: { heatmap: HeatmapDto }) {
  const grid = buildGrid(heatmap);
  const { dates, today, rows } = grid;
  const currentHour = currentLocalHour(heatmap.reportingTimeZoneId);
  return (
    <figure
      className="mini-heatmap"
      aria-label={`近 ${String(heatmap.days)} 天活跃热力图（缩小版）`}
    >
      <div className="mini-heatmap__rows" aria-hidden="true">
        {rows.map((row) => (
          <div
            key={row.hour}
            className={
              isHourPeriodEnd(row.hour)
                ? 'mini-heatmap__line mini-heatmap__line--divider'
                : 'mini-heatmap__line'
            }
          >
            <span className="mini-heatmap__time">{getTimeOfDayLabel(row.hour)}</span>
            <div
              className="mini-heatmap__cells"
              style={{ '--mini-dates': dates.length } as React.CSSProperties}
            >
              {row.cells.map((cell) => (
                <span
                  key={`${cell.localDate}-${String(cell.localHour)}`}
                  className={[
                    'mini-heatmap__cell',
                    `heatmap-level--${String(normalizeIntensityLevel(cell.intensityLevel))}`,
                    // 仅今天列当前小时描边（用户反馈：整列描边过重）。
                    cell.localDate === today && cell.localHour === currentHour
                      ? 'mini-heatmap__cell--today'
                      : '',
                  ]
                    .filter(Boolean)
                    .join(' ')}
                />
              ))}
            </div>
          </div>
        ))}
      </div>
      <figcaption className="mini-heatmap__legend" aria-hidden="true">
        <span className="text-dim">少</span>
        {heatmapIntensityLabels.map((text, level) => (
          <span
            key={text}
            className={`heatmap-legend__swatch heatmap-level--${String(level)}`}
          />
        ))}
        <span className="text-dim">多</span>
      </figcaption>
    </figure>
  );
}
