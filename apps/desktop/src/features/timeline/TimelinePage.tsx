import { useCallback, useEffect, useRef, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import type { GapKind, TimelineItem } from '../../types/wuji-core';
import { bridgeClient, toSafeError, type SafeError } from '../../bridge/client';
import { PageStateView, type PagePhase } from '../../components/PageState';
import { formatClock, formatDuration, localDateAndHour, shiftLocalDate } from '../../lib/format';
import { useDocumentVisible } from '../../lib/polling';

type TimelineModel =
  | { phase: 'loading' }
  | {
      phase: 'ready';
      /** 本数据所属的 localDate：迟到响应与失败保留都以此为准，不得串日。 */
      localDate: string;
      items: TimelineItem[];
      timeZoneId: string;
      truncated: boolean;
    }
  | { phase: 'error'; error: SafeError };

interface InFlightRequest {
  target: string;
  generation: number;
}

/** host 单页上限 500（query.rs limit 校验）；UI 不分页，超长一天在此循环取完。 */
const PAGE_LIMIT = 500;
/** 循环取页的安全上限（500 × 20 = 一万条），防异常数据导致死循环。 */
const MAX_PAGES = 20;
const REFRESH_INTERVAL_MS = 5000;
/** 向下滚动超过该距离后出现「回到顶部」。 */
const SHOW_TOP_BUTTON_OFFSET_PX = 300;
/** ?date= 只接受严格 YYYY-MM-DD，其他值视为未指定（今天）。 */
const DATE_PARAM_RE = /^\d{4}-\d{2}-\d{2}$/;

interface FullDayTimeline {
  items: TimelineItem[];
  timeZoneId: string;
  /** true = 达到取页上限或游标停滞，结果不完整（不得伪装成完整当天）。 */
  truncated: boolean;
}

/** 顺序取回当天全部条目；后端游标分页仅在此内部使用，页面本身不分页。 */
async function fetchFullDayTimeline(localDate: string): Promise<FullDayTimeline> {
  const items: TimelineItem[] = [];
  let timeZoneId = '';
  let cursor: string | undefined;
  for (let page = 0; page < MAX_PAGES; page += 1) {
    const dto = await bridgeClient.activityGetTimeline(localDate, cursor, PAGE_LIMIT);
    timeZoneId = dto.reportingTimeZoneId;
    items.push(...dto.items);
    if (dto.nextCursor == null) {
      return { items, timeZoneId, truncated: false };
    }
    // 游标不前进（空页或与请求相同的游标）：防重复条目/死循环，按截断处理。
    if (dto.items.length === 0 || dto.nextCursor === cursor) {
      return { items, timeZoneId, truncated: true };
    }
    cursor = dto.nextCursor;
  }
  // 达到取页上限仍有后续：明确截断，不返回"伪完整"结果。
  return { items, timeZoneId, truncated: true };
}

/** ?hour= 解析：0-23 整数，其他值视为未指定。 */
function parseHourParam(raw: string | null): number | null {
  if (raw === null || !/^\d{1,2}$/.test(raw)) return null;
  const hour = Number(raw);
  return hour >= 0 && hour <= 23 ? hour : null;
}

/**
 * 定位覆盖目标小时的条目（items 为倒序），返回其下标；未命中返回 -1。
 * sampling_transition 是切换标记，无论是否显示都不得成为小时目标；
 * 跨午夜条目按当前查看日期裁剪（前一天 23:30 → 当天 00:15 在当天视图只覆盖 0 时）。
 */
function findHourTargetIndex(
  items: TimelineItem[],
  hour: number,
  timeZoneId: string,
  viewDate: string,
): number {
  for (let index = 0; index < items.length; index += 1) {
    const item = items[index];
    // sampling_transition 是零语义时长的切换标记，即使用户选择显示，也不得
    // 抢占按小时定位的活动/缺口目标。
    if (item.kind === 'gap' && item.gapKind === 'sampling_transition') {
      continue;
    }
    const start = localDateAndHour(item.startAtUtcMs, timeZoneId);
    if (start === null) continue;
    // 半开区间 [start, end)：end 减 1ms 取小时；进行中的 gap 覆盖到当天末尾。
    let end = { date: viewDate, hour: 23 };
    const endText = item.endAtUtcMs;
    if (endText !== null) {
      const endMs =
        BigInt(endText) > BigInt(item.startAtUtcMs)
          ? (BigInt(endText) - 1n).toString()
          : item.startAtUtcMs;
      const parsed = localDateAndHour(endMs, timeZoneId);
      if (parsed !== null) end = parsed;
    }
    if (start.date > viewDate || end.date < viewDate) continue;
    const startHour = start.date < viewDate ? 0 : start.hour;
    const endHour = end.date > viewDate ? 23 : end.hour;
    if (hour >= startHour && hour <= endHour) return index;
  }
  return -1;
}

function isSameInFlightRequest(
  request: InFlightRequest | null,
  target: string,
  generation: number,
): boolean {
  return request?.target === target && request.generation === generation;
}

/** 时间线（09 §10.2）：Segment/Gap 按时间展示，最新在顶部，不显示标题/Context/Focus。
 *  ?date=YYYY-MM-DD 查看历史日期（静态，不轮询）；?hour=H 定位对应小时的条目。 */
export default function TimelinePage() {
  const [model, setModel] = useState<TimelineModel>({ phase: 'loading' });
  const [showTransitions, setShowTransitions] = useState(false);
  const [showTopButton, setShowTopButton] = useState(false);
  const [todayDate, setTodayDate] = useState<string | null>(null);
  const pageRef = useRef<HTMLDivElement>(null);
  const listRef = useRef<HTMLUListElement>(null);
  /** 请求代际：每次发起 +1，迟到响应（代际落后）一律丢弃，不得覆盖新视图。 */
  const generationRef = useRef(0);
  /**
   * 最新进行中的请求身份（'' = 今天视图）。target 负责同目标防重入，
   * generation 保证 A→B→A 时旧 A 的 finally 不会清除新 A 的登记。
   */
  const inFlightRef = useRef<InFlightRequest | null>(null);
  /** 最近一次成功的 DB reporting 今天（失败分支判断"同视图"用，避免 state 依赖）。 */
  const todayDateRef = useRef<string | null>(null);
  const visible = useDocumentVisible();
  const [searchParams, setSearchParams] = useSearchParams();

  const rawDate = searchParams.get('date');
  const dateParam = rawDate !== null && DATE_PARAM_RE.test(rawDate) ? rawDate : null;
  const hourParam = parseHourParam(searchParams.get('hour'));

  const refresh = useCallback(async () => {
    const target = dateParam ?? '';
    if (inFlightRef.current?.target === target) return;
    const generation = ++generationRef.current;
    inFlightRef.current = { target, generation };
    try {
      // 日期以数据库 reporting 时区为准（审核 R08），不用浏览器本地日期；
      // 每轮刷新重取日期，跨午夜后自动切到新的一天。
      const today = await bridgeClient.activityGetToday();
      const { items, timeZoneId, truncated } = await fetchFullDayTimeline(
        dateParam ?? today.localDate,
      );
      if (generation !== generationRef.current) return; // 迟到响应丢弃
      todayDateRef.current = today.localDate;
      setTodayDate(today.localDate);
      // 后端按时间升序返回；页面倒序展示，最新条目在顶部。
      setModel({
        phase: 'ready',
        localDate: dateParam ?? today.localDate,
        items: items.slice().reverse(),
        timeZoneId,
        truncated,
      });
    } catch (cause) {
      if (generation !== generationRef.current) return;
      const error = toSafeError(cause);
      // 只有同视图（同 localDate）轮询失败才保留旧数据；
      // 日期变化后的失败进入错误四态，不得新日期标题配旧数据。
      const requested = dateParam ?? todayDateRef.current;
      setModel((current) =>
        current.phase === 'ready' && requested !== null && current.localDate === requested
          ? current
          : { phase: 'error', error },
      );
    } finally {
      if (isSameInFlightRequest(inFlightRef.current, target, generation)) {
        inFlightRef.current = null;
      }
    }
  }, [dateParam]);

  const isToday = dateParam === null || dateParam === todayDate;

  // 加载入口：挂载、切换日期（refresh 随 dateParam 变更）、页面重新可见时各加载一次。
  // isToday 不属本 effect 的依赖：?date=今天 在 todayDate 到达后的翻转不得触发二次加载。
  // setTimeout 延迟首轮：effect 体内不得同步触发 setState（与 usePolling 同一模式）。
  useEffect(() => {
    if (!visible) return;
    const immediate = setTimeout(() => {
      void refresh();
    }, 0);
    return () => {
      clearTimeout(immediate);
    };
  }, [visible, refresh]);

  // 轮询入口：仅「今天」可见视图挂 5 秒 interval；历史日期是静态视图。
  const savedRefresh = useRef(refresh);
  useEffect(() => {
    savedRefresh.current = refresh;
  }, [refresh]);
  useEffect(() => {
    if (!visible || !isToday) return;
    const timer = setInterval(() => {
      void savedRefresh.current();
    }, REFRESH_INTERVAL_MS);
    return () => {
      clearInterval(timer);
    };
  }, [visible, isToday]);

  // 滚动容器是 AppLayout 的 .app-main；页面独立渲染（如测试）时不存在，滚动按钮静默失效。
  const scrollContainer = useCallback(
    (): Element | null => pageRef.current?.closest('.app-main') ?? null,
    [],
  );

  // 「回到顶部」仅在向下滚动后出现。
  useEffect(() => {
    const container = scrollContainer();
    if (container == null) return;
    const onScroll = () => {
      setShowTopButton(container.scrollTop > SHOW_TOP_BUTTON_OFFSET_PX);
    };
    onScroll();
    container.addEventListener('scroll', onScroll, { passive: true });
    return () => {
      container.removeEventListener('scroll', onScroll);
    };
  }, [scrollContainer]);

  const scrollToTop = useCallback(() => {
    scrollContainer()?.scrollTo({ top: 0, behavior: 'smooth' });
  }, [scrollContainer]);

  const scrollToBottom = useCallback(() => {
    const container = scrollContainer();
    container?.scrollTo({ top: container.scrollHeight, behavior: 'smooth' });
  }, [scrollContainer]);

  const selectDate = useCallback(
    (next: string | null) => {
      // 手动切换日期时丢弃小时定位。
      setSearchParams(next === null ? {} : { date: next });
    },
    [setSearchParams],
  );

  // ?hour= 定位：按当前日期与可见过滤命中条目，加高亮类并滚动到可视区域中部。
  const hourTargetIndex =
    model.phase === 'ready' && hourParam !== null
      ? findHourTargetIndex(
          model.items,
          hourParam,
          model.timeZoneId,
          model.localDate,
        )
      : -1;

  useEffect(() => {
    if (hourTargetIndex < 0) return;
    listRef.current
      ?.querySelector('.list__row--hour-target')
      ?.scrollIntoView({ block: 'center' });
  }, [hourTargetIndex]);

  const phase: PagePhase =
    model.phase === 'loading'
      ? { kind: 'loading' }
      : model.phase === 'error'
        ? { kind: 'error', error: model.error, onRetry: () => void refresh() }
        : model.items.length === 0
          ? { kind: 'empty', title: '这一天还没有时间线记录', hint: '开始记录后，应用使用片段会按时间显示在这里。' }
          : { kind: 'ready' };

  return (
    <div className="page" ref={pageRef}>
      <h1 className="page__title">时间线</h1>
      {/* 日期导航在四态之外：空数据日期同样要能继续翻页。 */}
      {model.phase === 'ready' && (
        <div className="date-nav">
          <button
            className="button"
            type="button"
            onClick={() => { selectDate(shiftLocalDate(model.localDate, -1)); }}
          >
            前一天
          </button>
          <span className="text-dim">
            {model.localDate}
            {isToday ? ' · 今天' : ''}
          </span>
          <button
            className="button"
            type="button"
            disabled={isToday}
            onClick={() => { selectDate(shiftLocalDate(model.localDate, 1)); }}
          >
            后一天
          </button>
          {!isToday && (
            <button
              className="button"
              type="button"
              onClick={() => { selectDate(null); }}
            >
              回到今天
            </button>
          )}
        </div>
      )}
      <PageStateView phase={phase}>
        {model.phase === 'ready' && (
          <>
            {hourParam !== null && hourTargetIndex >= 0 && (
              <div className="text-dim">已定位到 {hourParam} 时</div>
            )}
            <label className="form__checkbox-row text-dim">
              <input
                type="checkbox"
                checked={showTransitions}
                onChange={(event) => { setShowTransitions(event.target.checked); }}
              />
              显示切换间隔（采样间隙，不计入时长）
            </label>
            {model.truncated && (
              <div className="notice notice--warn" role="status">
                当天记录条数过多，仅显示部分记录。
              </div>
            )}
            <ul className="list" aria-label="时间线条目" ref={listRef}>
              {model.items.map((item, index) => (
                <TimelineRow
                  key={item.kind === 'segment' ? `s-${item.segmentId}` : `g-${item.gapId}`}
                  item={item}
                  timeZoneId={model.timeZoneId}
                  hideTransition={!showTransitions}
                  highlighted={index === hourTargetIndex}
                />
              ))}
            </ul>
            {/* 为右下角悬浮按钮预留空间，避免遮挡列表末尾条目。 */}
            <div className="scroll-actions-spacer" aria-hidden="true" />
            <div className="scroll-actions">
              {showTopButton && (
                <button
                  className="button"
                  type="button"
                  aria-label="回到顶部"
                  onClick={scrollToTop}
                >
                  ↑ 顶部
                </button>
              )}
              <button
                className="button"
                type="button"
                aria-label="跳到底部"
                onClick={scrollToBottom}
              >
                ↓ 底部
              </button>
            </div>
          </>
        )}
      </PageStateView>
    </div>
  );
}

function TimelineRow({
  item,
  timeZoneId,
  hideTransition,
  highlighted,
}: {
  item: TimelineItem;
  timeZoneId: string;
  hideTransition: boolean;
  highlighted: boolean;
}) {
  if (item.kind === 'gap' && item.gapKind === 'sampling_transition') {
    if (hideTransition) return null;
    // 用户勾选显示时必须可被屏幕阅读器感知（审核 R10），不得 aria-hidden。
    return (
      <li className="list__row--transition" aria-label="切换间隔（采样间隙，不计入时长）">
        — 切换间隔 —
      </li>
    );
  }
  if (item.kind === 'segment') {
    return (
      <li className={highlighted ? 'list__row list__row--hour-target' : 'list__row'}>
        <span className={`badge badge--${item.activityState}`}>
          {stateLabel(item.activityState)}
        </span>
        <div className="list__main">
          <div className="list__title">{item.app.displayName}</div>
          <div className="list__sub mono">
            {formatClock(item.startAtUtcMs, timeZoneId)} –{' '}
            {formatClock(item.endAtUtcMs, timeZoneId)}
            {item.status === 'open' ? '（进行中）' : ''}
          </div>
        </div>
        <span className="mono">{formatDuration(item.durationMs)}</span>
      </li>
    );
  }
  return (
    <li className={highlighted ? 'list__row list__row--gap list__row--hour-target' : 'list__row list__row--gap'}>
      <span className="badge badge--dim">缺口</span>
      <div className="list__main">
        <div className="list__title">{gapLabel(item.gapKind)}</div>
        <div className="list__sub mono">
          {formatClock(item.startAtUtcMs, timeZoneId)} –{' '}
          {item.endAtUtcMs != null ? formatClock(item.endAtUtcMs, timeZoneId) : '进行中'}
          {item.eventCount > 1 ? `（${String(item.eventCount)} 次）` : ''}
        </div>
      </div>
    </li>
  );
}

function stateLabel(state: 'active' | 'idle' | 'unknown'): string {
  switch (state) {
    case 'active':
      return '活跃';
    case 'idle':
      return '空闲';
    default:
      return '未知';
  }
}

function gapLabel(kind: GapKind): string {
  switch (kind) {
    case 'privacy_excluded':
      return '隐私排除';
    case 'capture_paused':
      return '已暂停';
    case 'capture_stopped':
      return '已停止';
    case 'system_sleep':
      return '系统休眠';
    case 'session_locked':
      return '锁屏';
    case 'agent_restart':
      return 'Agent 重启';
    case 'clock_changed':
      return '时钟调整';
    case 'capture_delayed':
      return '采集延迟';
    case 'capture_queue_drop':
    case 'writer_queue_drop':
      return '队列丢弃';
    case 'capture_error':
      return '采集错误';
    default:
      return '数据缺口';
  }
}
