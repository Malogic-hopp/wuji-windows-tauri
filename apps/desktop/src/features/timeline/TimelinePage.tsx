import { useCallback, useEffect, useRef, useState } from 'react';
import type { GapKind, TimelineItem } from '../../types/wuji-core';
import { bridgeClient, toSafeError, type SafeError } from '../../bridge/client';
import { PageStateView, type PagePhase } from '../../components/PageState';
import { formatClock, formatDuration } from '../../lib/format';
import { useDocumentVisible, usePolling } from '../../lib/polling';

type TimelineModel =
  | { phase: 'loading' }
  | { phase: 'ready'; items: TimelineItem[]; timeZoneId: string }
  | { phase: 'error'; error: SafeError };

/** host 单页上限 500（query.rs limit 校验）；UI 不分页，超长一天在此循环取完。 */
const PAGE_LIMIT = 500;
/** 循环取页的安全上限（500 × 20 = 一万条），防异常数据导致死循环。 */
const MAX_PAGES = 20;
const REFRESH_INTERVAL_MS = 5000;
/** 向下滚动超过该距离后出现「回到顶部」。 */
const SHOW_TOP_BUTTON_OFFSET_PX = 300;

/** 顺序取回当天全部条目；后端游标分页仅在此内部使用，页面本身不分页。 */
async function fetchFullDayTimeline(
  localDate: string,
): Promise<{ items: TimelineItem[]; timeZoneId: string }> {
  const items: TimelineItem[] = [];
  let timeZoneId = '';
  let cursor: string | undefined;
  for (let page = 0; page < MAX_PAGES; page += 1) {
    const dto = await bridgeClient.activityGetTimeline(localDate, cursor, PAGE_LIMIT);
    timeZoneId = dto.reportingTimeZoneId;
    items.push(...dto.items);
    if (dto.nextCursor == null) {
      break;
    }
    cursor = dto.nextCursor;
  }
  return { items, timeZoneId };
}

/** 时间线（09 §10.2）：Segment/Gap 按时间展示，最新在顶部，不显示标题/Context/Focus。 */
export default function TimelinePage() {
  const [model, setModel] = useState<TimelineModel>({ phase: 'loading' });
  const [showTransitions, setShowTransitions] = useState(false);
  const [showTopButton, setShowTopButton] = useState(false);
  const pageRef = useRef<HTMLDivElement>(null);
  const visible = useDocumentVisible();

  const refresh = useCallback(async () => {
    try {
      // 日期以数据库 reporting 时区为准（审核 R08），不用浏览器本地日期；
      // 每轮刷新重取日期，跨午夜后自动切到新的一天。
      const today = await bridgeClient.activityGetToday();
      const { items, timeZoneId } = await fetchFullDayTimeline(today.localDate);
      // 后端按时间升序返回；页面倒序展示，最新条目在顶部。
      setModel({ phase: 'ready', items: items.slice().reverse(), timeZoneId });
    } catch (cause) {
      // 后台轮询失败保留已展示数据，下一轮自动重试；仅首次加载失败进入错误四态。
      setModel((current) =>
        current.phase === 'ready' ? current : { phase: 'error', error: toSafeError(cause) },
      );
    }
  }, []);

  usePolling(refresh, REFRESH_INTERVAL_MS, visible);

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

  const phase: PagePhase =
    model.phase === 'loading'
      ? { kind: 'loading' }
      : model.phase === 'error'
        ? { kind: 'error', error: model.error, onRetry: () => void refresh() }
        : model.items.length === 0
          ? { kind: 'empty', title: '今天还没有时间线记录', hint: '开始记录后，应用使用片段会按时间显示在这里。' }
          : { kind: 'ready' };

  return (
    <div className="page" ref={pageRef}>
      <h1 className="page__title">时间线</h1>
      <PageStateView phase={phase}>
        {model.phase === 'ready' && (
          <>
            <label className="form__checkbox-row text-dim">
              <input
                type="checkbox"
                checked={showTransitions}
                onChange={(event) => { setShowTransitions(event.target.checked); }}
              />
              显示切换间隔（采样间隙，不计入时长）
            </label>
            <ul className="list" aria-label="时间线条目">
              {model.items.map((item) => (
                <TimelineRow
                  key={item.kind === 'segment' ? `s-${item.segmentId}` : `g-${item.gapId}`}
                  item={item}
                  timeZoneId={model.timeZoneId}
                  hideTransition={!showTransitions}
                />
              ))}
            </ul>
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
}: {
  item: TimelineItem;
  timeZoneId: string;
  hideTransition: boolean;
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
      <li className="list__row">
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
    <li className="list__row list__row--gap">
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
