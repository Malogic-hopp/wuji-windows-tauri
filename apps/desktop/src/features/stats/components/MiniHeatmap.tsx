import type { HeatmapDto } from '../../../types/wuji-core';
import {
  buildGrid,
  currentLocalHour,
  heatmapIntensityLabels,
  normalizeIntensityLevel,
} from '../../heatmap/heatmapModel';

/**
 * 主页缩小版热力图（产品扩展，2026-08-04）：24 小时 × N 天网格缩略图。
 * - 格子高 6px，总高 ≈ 24 行 + 图例，约 170px（"高度不要太高"）；
 * - 复用 activity 域纯函数 buildGrid/强度归一化与全局 heatmap-level--N 颜色；
 * - 缩略图语义：不带键盘格子导航/逐格 title，区块级 aria 概括；
 * - 今天列格子高亮（描边），图例"少→多"与热力图页一致。
 */
export function MiniHeatmap({ heatmap }: { heatmap: HeatmapDto }) {
  const grid = buildGrid(heatmap);
  const { dates, today, rows } = grid;
  const currentHour = currentLocalHour(heatmap.reportingTimeZoneId);
  return (
    <figure className="mini-heatmap" aria-label={`近 ${String(heatmap.days)} 天活跃热力图（缩小版）`}>
      <div
        className="mini-heatmap__grid"
        style={{ '--mini-dates': dates.length } as React.CSSProperties}
      >
        {rows.map((row) =>
          row.cells.map((cell) => (
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
              aria-hidden="true"
            />
          )),
        )}
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
