import { useCallback, useEffect, useMemo, useRef, useState, type CSSProperties, type KeyboardEvent } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';
import type { HeatmapDto } from '../../types/wuji-core';
import { bridgeClient, toSafeError, type SafeError } from '../../bridge/client';
import { PageStateView, type PagePhase } from '../../components/PageState';
import { shiftLocalDate } from '../../lib/format';
import { useDocumentVisible } from '../../lib/polling';
import {
  buildGrid,
  clampFocusPosition,
  currentLocalHour,
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
  | {
      phase: 'ready';
      /** 本数据所属的 weekOffset：迟到响应与失败保留都以此为准，不得串周。 */
      weekOffset: number;
      heatmap: HeatmapDto;
    }
  | { phase: 'error'; error: SafeError };

interface InFlightRequest {
  target: number;
  generation: number;
}

/** 热力图是慢变数据：15 秒轮询足够，页面隐藏时自动暂停。 */
const REFRESH_INTERVAL_MS = 15_000;
const MIN_WEEK_OFFSET = -520;
const MAX_WEEK_OFFSET = 0;


/** 解析 ?week= 查询参数：只允许本周至前 520 周，非法值退到 0。 */
function parseWeekParam(raw: string | null): number {
  if (raw === null || !/^-?\d{1,3}$/.test(raw)) return 0;
  const n = Number(raw);
  return Number.isInteger(n) && n >= MIN_WEEK_OFFSET && n <= MAX_WEEK_OFFSET ? n : 0;
}

function isSameInFlightRequest(
  request: InFlightRequest | null,
  target: number,
  generation: number,
): boolean {
  return request?.target === target && request.generation === generation;
}

/** 热力图：最近 7 天 × 24 小时活跃分布（只读 hourly 投影，强度由后端归一化）。
 *  支持 ?week=N 按周翻页：0 为本周，-1 为上周，以此类推；不得翻入未来周。 */
export default function HeatmapPage() {
  const [model, setModel] = useState<HeatmapModel>({ phase: 'loading' });
  /** 请求代际：每次发起 +1，迟到响应（代际落后）一律丢弃，不得覆盖新视图。 */
  const generationRef = useRef(0);
  /** target 做同周防重入；generation 防止 A→B→A 时旧 A 清除新 A。 */
  const inFlightRef = useRef<InFlightRequest | null>(null);
  const visible = useDocumentVisible();
  const [searchParams, setSearchParams] = useSearchParams();
  const rawWeekParam = searchParams.get('week');
  const weekOffset = parseWeekParam(rawWeekParam);

  const refresh = useCallback(async () => {
    const target = weekOffset;
    if (inFlightRef.current?.target === target) return;
    const generation = ++generationRef.current;
    inFlightRef.current = { target, generation };
    try {
      const heatmap = await bridgeClient.activityGetHeatmap(undefined, weekOffset);
      if (generation !== generationRef.current) return; // 迟到响应丢弃
      setModel({ phase: 'ready', weekOffset, heatmap });
    } catch (cause) {
      if (generation !== generationRef.current) return;
      const error = toSafeError(cause);
      // 只有同周轮询失败才保留旧数据；切周失败进入错误四态，不得新周标题配旧数据。
      setModel((current) =>
        current.phase === 'ready' && current.weekOffset === weekOffset
          ? current
          : { phase: 'error', error },
      );
    } finally {
      if (isSameInFlightRequest(inFlightRef.current, target, generation)) {
        inFlightRef.current = null;
      }
    }
  }, [weekOffset]);

  // 非法、未来或非规范参数统一回到本周；不得把 URL 参数变成绕过按钮的未来查询入口。
  useEffect(() => {
    const canonical = weekOffset === 0 ? null : String(weekOffset);
    if (rawWeekParam === canonical) return;
    setSearchParams(canonical === null ? {} : { week: canonical }, { replace: true });
  }, [rawWeekParam, setSearchParams, weekOffset]);

  // 加载入口：挂载、切换周（refresh 随 weekOffset 变更）、页面重新可见时各加载一次。
  // setTimeout 延迟首轮：effect 体内不得同步触发 setState。
  useEffect(() => {
    if (!visible) return;
    const immediate = setTimeout(() => {
      void refresh();
    }, 0);
    return () => {
      clearTimeout(immediate);
    };
  }, [visible, refresh]);

  // 轮询入口：仅本周可见视图挂 15 秒 interval；历史周是静态视图。
  const savedRefresh = useRef(refresh);
  useEffect(() => {
    savedRefresh.current = refresh;
  }, [refresh]);
  useEffect(() => {
    if (!visible || weekOffset !== 0) return;
    const timer = setInterval(() => {
      void savedRefresh.current();
    }, REFRESH_INTERVAL_MS);
    return () => {
      clearInterval(timer);
    };
  }, [visible, weekOffset]);

  const phase: PagePhase =
    model.phase === 'loading'
      ? { kind: 'loading' }
      : model.phase === 'error'
        ? { kind: 'error', error: model.error, onRetry: () => void refresh() }
        : isHeatmapEmpty(model.heatmap)
          ? { kind: 'empty', title: '这周还没有活跃记录', hint: '开始记录后，这里会按小时显示这周的活跃热力分布。' }
          : { kind: 'ready' };

  const handleWeekChange = useCallback(
    (delta: number) => {
      const next = weekOffset + delta;
      if (next < MIN_WEEK_OFFSET || next > MAX_WEEK_OFFSET) return;
      setSearchParams(next === 0 ? {} : { week: String(next) });
    },
    [weekOffset, setSearchParams],
  );

  const goToCurrentWeek = useCallback(() => {
    setSearchParams({});
  }, [setSearchParams]);

  /** 周范围文案（如 "2026-07-25 – 2026-07-31"）。 */
  const weekRangeLabel = useMemo(() => {
    if (model.phase !== 'ready') return null;
    const { rangeEndLocalDate, days } = model.heatmap;
    const start = shiftLocalDate(rangeEndLocalDate, -(days - 1));
    return `${start} – ${rangeEndLocalDate}`;
  }, [model]);

  const isCurrentWeek = weekOffset === 0;

  return (
    <div className="page">
      <h1 className="page__title">热力图</h1>
      {/* 周导航在四态之外：空数据周同样要能继续翻页。 */}
      {model.phase === 'ready' && (
        <div className="date-nav">
          <button
            className="button"
            type="button"
            disabled={weekOffset <= MIN_WEEK_OFFSET}
            onClick={() => {
              handleWeekChange(-1);
            }}
          >
            上一周
          </button>
          <span className="text-dim">
            {weekRangeLabel}
            {isCurrentWeek ? ' · 本周' : ''}
          </span>
          <button
            className="button"
            type="button"
            disabled={weekOffset >= 0}
            onClick={() => {
              handleWeekChange(1);
            }}
          >
            下一周
          </button>
          {!isCurrentWeek && (
            <button
              className="button"
              type="button"
              onClick={goToCurrentWeek}
            >
              回到本周
            </button>
          )}
        </div>
      )}
      <PageStateView phase={phase}>
        {model.phase === 'ready' && (
          <HeatmapGrid heatmap={model.heatmap} />
        )}
      </PageStateView>
    </div>
  );
}

function HeatmapGrid({ heatmap }: { heatmap: HeatmapDto }) {
  const grid = useMemo(() => buildGrid(heatmap), [heatmap]);
  const { dates, today, rows } = grid;
  const dateCount = dates.length;
  const currentHour = currentLocalHour(heatmap.reportingTimeZoneId);
  const navigate = useNavigate();
  const gridRef = useRef<HTMLDivElement>(null);
  const [focusPosition, setFocusPosition] = useState<HeatmapFocusPosition>(() =>
    getDefaultFocusPosition(grid, currentHour),
  );
  const activeFocus = clampFocusPosition(focusPosition, heatmapHourCount, dateCount);

  // today 是 DB reporting 时区的真实今天；历史范围不含它时自然不显示今天/现在标记。
  const markedToday = dates.includes(today) ? today : null;

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
                className={
                  date === markedToday ? 'heatmap-col heatmap-col--today' : 'heatmap-col'
                }
              >
                <span>{date === markedToday ? '今天' : formatWeekday(date)}</span>
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
                  const isNow =
                    markedToday !== null &&
                    dates[dateIndex] === markedToday &&
                    cell.localHour === currentHour;
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
