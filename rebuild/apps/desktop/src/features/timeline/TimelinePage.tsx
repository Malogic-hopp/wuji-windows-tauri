import { useCallback, useEffect, useState } from 'react';
import type { GapKind, TimelineItem, TimelinePageDto } from '../../types/wuji-core';
import { bridgeClient, toSafeError, type SafeError } from '../../bridge/client';
import { PageStateView, type PagePhase } from '../../components/PageState';
import { formatClock, formatDuration } from '../../lib/format';

type TimelineModel =
  | { phase: 'loading' }
  | { phase: 'ready'; page: TimelinePageDto; items: TimelineItem[] }
  | { phase: 'error'; error: SafeError };

const PAGE_SIZE = 50;

/** 时间线（09 §10.2）：Segment/Gap 按时间展示，不显示标题/Context/Focus。 */
export default function TimelinePage() {
  const [model, setModel] = useState<TimelineModel>({ phase: 'loading' });
  const [showTransitions, setShowTransitions] = useState(false);
  const [loadingMore, setLoadingMore] = useState(false);

  const loadFirst = useCallback(async () => {
    setLoadingMore(true);
    try {
      const page = await bridgeClient.activityGetTimeline(todayText(), undefined, PAGE_SIZE);
      setModel({ phase: 'ready', page, items: page.items });
    } catch (cause) {
      setModel({ phase: 'error', error: toSafeError(cause) });
    } finally {
      setLoadingMore(false);
    }
  }, []);

  const loadMore = useCallback(async () => {
    if (model.phase !== 'ready' || model.page.nextCursor == null) return;
    setLoadingMore(true);
    try {
      const page = await bridgeClient.activityGetTimeline(
        model.page.localDate,
        model.page.nextCursor,
        PAGE_SIZE,
      );
      setModel({
        phase: 'ready',
        page,
        items: [...model.items, ...page.items],
      });
    } catch (cause) {
      setModel({ phase: 'error', error: toSafeError(cause) });
    } finally {
      setLoadingMore(false);
    }
  }, [model]);

  // 首次加载。
  useEffect(() => {
    const timer = setTimeout(() => {
      void loadFirst();
    }, 0);
    return () => {
      clearTimeout(timer);
    };
  }, [loadFirst]);

  const phase: PagePhase =
    model.phase === 'loading'
      ? { kind: 'loading' }
      : model.phase === 'error'
        ? { kind: 'error', error: model.error, onRetry: () => void loadFirst() }
        : model.items.length === 0
          ? { kind: 'empty', title: '今天还没有时间线记录', hint: '开始记录后，应用使用片段会按时间显示在这里。' }
          : { kind: 'ready' };

  return (
    <div className="page">
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
                  timeZoneId={model.page.reportingTimeZoneId}
                  hideTransition={!showTransitions}
                />
              ))}
            </ul>
            {model.page.nextCursor != null ? (
              <button
                className="button"
                type="button"
                disabled={loadingMore}
                onClick={() => void loadMore()}
              >
                {loadingMore ? '正在加载…' : '加载更多'}
              </button>
            ) : (
              <div className="text-dim">已显示全部</div>
            )}
          </>
        )}
      </PageStateView>
    </div>
  );
}

function todayText(): string {
  const now = new Date();
  const year = String(now.getFullYear());
  const month = String(now.getMonth() + 1).padStart(2, '0');
  const day = String(now.getDate()).padStart(2, '0');
  return `${year}-${month}-${day}`;
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
    return (
      <li className="list__row--transition" aria-hidden="true">
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
