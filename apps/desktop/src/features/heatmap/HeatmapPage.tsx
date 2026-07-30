import { useCallback, useMemo, useRef, useState, type CSSProperties, type KeyboardEvent } from 'react';
import { useNavigate } from 'react-router-dom';
import type { HeatmapDto } from '../../types/wuji-core';
import { bridgeClient, toSafeError, type SafeError } from '../../bridge/client';
import { PageStateView, type PagePhase } from '../../components/PageState';
import { useDocumentVisible, usePolling } from '../../lib/polling';
import {
  buildGrid,
  clampFocusPosition,
  formatShortDate,
  formatWeekday,
  getCellLabel,
  getDefaultFocusPosition,
  getTimeOfDayLabel,
  heatmapHourCount,
  heatmapIntensityLabels,
  isHeatmapEmpty,
  isHourPeriodEnd,
  moveFocus,
  normalizeIntensityLevel,
  type HeatmapFocusKey,
  type HeatmapFocusPosition,
} from './heatmapModel';

type HeatmapModel =
  | { phase: 'loading' }
  | { phase: 'ready'; heatmap: HeatmapDto }
  | { phase: 'error'; error: SafeError };

/** 热力图是慢变数据：15 秒轮询足够，页面隐藏时 usePolling 自动暂停。 */
const REFRESH_INTERVAL_MS = 15_000;

/** reporting 时区下的当前小时（0-23）；hour12=false 的 '24' 归一到 0。 */
function currentLocalHour(timeZoneId: string): number {
  const hour = new Intl.DateTimeFormat('en-US', {
    timeZone: timeZoneId,
    hour: '2-digit',
    hour12: false,
  })
    .formatToParts(new Date())
    .find((part) => part.type === 'hour')?.value;
  return Number(hour ?? '0') % 24;
}

/** 热力图：最近 7 天 × 24 小时活跃分布（只读 hourly 投影，强度由后端归一化）。 */
export default function HeatmapPage() {
  const [model, setModel] = useState<HeatmapModel>({ phase: 'loading' });
  /** 轮询防重入：上一轮未结束跳过本轮（与 TimelinePage 同一约定）。 */
  const inFlightRef = useRef(false);
  const visible = useDocumentVisible();

  const refresh = useCallback(async () => {
    if (inFlightRef.current) return;
    inFlightRef.current = true;
    try {
      setModel({ phase: 'ready', heatmap: await bridgeClient.activityGetHeatmap() });
    } catch (cause) {
      // 后台轮询失败保留已展示数据，下一轮自动重试；仅首次加载失败进入错误四态。
      setModel((current) =>
        current.phase === 'ready' ? current : { phase: 'error', error: toSafeError(cause) },
      );
    } finally {
      inFlightRef.current = false;
    }
  }, []);

  usePolling(refresh, REFRESH_INTERVAL_MS, visible);

  const phase: PagePhase =
    model.phase === 'loading'
      ? { kind: 'loading' }
      : model.phase === 'error'
        ? { kind: 'error', error: model.error, onRetry: () => void refresh() }
        : isHeatmapEmpty(model.heatmap)
          ? { kind: 'empty', title: '最近 7 天还没有活跃记录', hint: '开始记录后，这里会按小时显示最近 7 天的活跃热力分布。' }
          : { kind: 'ready' };

  return (
    <div className="page">
      <h1 className="page__title">热力图</h1>
      <PageStateView phase={phase}>
        {model.phase === 'ready' && <HeatmapGrid heatmap={model.heatmap} />}
      </PageStateView>
    </div>
  );
}

function HeatmapGrid({ heatmap }: { heatmap: HeatmapDto }) {
  const grid = useMemo(() => buildGrid(heatmap), [heatmap]);
  const { dates, today, rows } = grid;
  const dateCount = dates.length;
  const currentHour = useMemo(
    () => currentLocalHour(heatmap.reportingTimeZoneId),
    [heatmap.reportingTimeZoneId],
  );
  const navigate = useNavigate();
  const gridRef = useRef<HTMLDivElement>(null);
  const [focusPosition, setFocusPosition] = useState<HeatmapFocusPosition>(() =>
    getDefaultFocusPosition(grid, currentHour),
  );
  const activeFocus = clampFocusPosition(focusPosition, heatmapHourCount, dateCount);

  const focusCell = (next: HeatmapFocusPosition) => {
    setFocusPosition(next);
    gridRef.current
      ?.querySelector<HTMLElement>(
        `[data-hour="${String(next.hourIndex)}"][data-date="${String(next.dateIndex)}"]`,
      )
      ?.focus();
  };

  /** 打开对应日期小时的时间线（UI-005：Enter/Space 与点击一致）。 */
  const openCell = (date: string, hour: number) => {
    void navigate(`/timeline?date=${date}&hour=${String(hour)}`);
  };

  const onCellKeyDown = (
    event: KeyboardEvent<HTMLDivElement>,
    hourIndex: number,
    dateIndex: number,
  ) => {
    if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault();
      openCell(dates[dateIndex], rows[hourIndex].hour);
      return;
    }
    const key = event.key as HeatmapFocusKey;
    switch (key) {
      case 'ArrowUp':
      case 'ArrowDown':
      case 'ArrowLeft':
      case 'ArrowRight':
      case 'Home':
      case 'End':
        event.preventDefault();
        focusCell(moveFocus({ hourIndex, dateIndex }, key, heatmapHourCount, dateCount));
        break;
      default:
        break;
    }
  };

  return (
    <>
      <div className="heatmap-legend">
        <span className="text-dim">活跃程度</span>
        <span className="text-dim">少</span>
        {heatmapIntensityLabels.map((text, level) => (
          <span
            key={text}
            className={`heatmap-legend__swatch heatmap-level--${String(level)}`}
            role="img"
            aria-label={text}
          />
        ))}
        <span className="text-dim">多</span>
      </div>
      <div
        className="heatmap-body"
        style={{ '--heatmap-dates': dateCount } as CSSProperties}
      >
        <div className="heatmap-line">
          <span className="heatmap-time" aria-hidden="true" />
          <div className="heatmap-cols" aria-hidden="true">
            {dates.map((date) => (
              <span
                key={date}
                className={date === today ? 'heatmap-col heatmap-col--today' : 'heatmap-col'}
              >
                <span>{date === today ? '今天' : formatWeekday(date)}</span>
                <span className="heatmap-col__date">{formatShortDate(date)}</span>
              </span>
            ))}
          </div>
        </div>
        <div
          className="heatmap-grid"
          role="grid"
          aria-label="最近 7 天每小时活跃热力图"
          ref={gridRef}
        >
          {rows.map((row, hourIndex) => (
            <div
              key={row.hour}
              className={
                isHourPeriodEnd(row.hour) ? 'heatmap-line heatmap-line--divider' : 'heatmap-line'
              }
            >
              <span className="heatmap-time" aria-hidden="true">
                {getTimeOfDayLabel(row.hour)}
              </span>
              <div className="heatmap-grid__row" role="row">
                {row.cells.map((cell, dateIndex) => {
                  const label = getCellLabel(cell);
                  const isFocus =
                    hourIndex === activeFocus.hourIndex && dateIndex === activeFocus.dateIndex;
                  const isNow = dates[dateIndex] === today && cell.localHour === currentHour;
                  const className = isNow
                    ? `heatmap-grid__cell heatmap-level--${String(normalizeIntensityLevel(cell.intensityLevel))} heatmap-grid__cell--now`
                    : `heatmap-grid__cell heatmap-level--${String(normalizeIntensityLevel(cell.intensityLevel))}`;
                  return (
                    <div
                      key={cell.localDate}
                      className={className}
                      role="gridcell"
                      tabIndex={isFocus ? 0 : -1}
                      data-hour={hourIndex}
                      data-date={dateIndex}
                      aria-label={label}
                      aria-current={isNow ? 'date' : undefined}
                      title={label}
                      onClick={() => {
                        openCell(dates[dateIndex], row.hour);
                      }}
                      onKeyDown={(event) => {
                        onCellKeyDown(event, hourIndex, dateIndex);
                      }}
                    />
                  );
                })}
              </div>
            </div>
          ))}
        </div>
      </div>
    </>
  );
}
