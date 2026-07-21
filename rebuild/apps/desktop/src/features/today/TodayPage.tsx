import { useCallback, useState } from 'react';
import type { TodayDto } from '../../types/wuji-core';
import { bridgeClient, toSafeError, type SafeError } from '../../bridge/client';
import { PageStateView, type PagePhase } from '../../components/PageState';
import { formatDuration } from '../../lib/format';
import { useDocumentVisible, usePolling } from '../../lib/polling';

type TodayModel =
  | { phase: 'loading' }
  | { phase: 'ready'; today: TodayDto }
  | { phase: 'error'; error: SafeError };

/** 今日（09 §10.1）。 */
export default function TodayPage() {
  const [model, setModel] = useState<TodayModel>({ phase: 'loading' });
  const visible = useDocumentVisible();

  const refresh = useCallback(async () => {
    try {
      const today = await bridgeClient.activityGetToday();
      setModel({ phase: 'ready', today });
    } catch (cause) {
      setModel({ phase: 'error', error: toSafeError(cause) });
    }
  }, []);

  usePolling(refresh, 5000, visible);

  const phase: PagePhase =
    model.phase === 'loading'
      ? { kind: 'loading' }
      : model.phase === 'error'
        ? { kind: 'error', error: model.error, onRetry: () => void refresh() }
        : isEmpty(model.today)
          ? { kind: 'empty', title: '今天还没有记录', hint: '开始记录后，这里会显示今日的工作概览。' }
          : { kind: 'ready' };

  return (
    <div className="page">
      <h1 className="page__title">今日</h1>
      <PageStateView phase={phase}>
        {model.phase === 'ready' && <TodayView today={model.today} />}
      </PageStateView>
    </div>
  );
}

function isEmpty(today: TodayDto): boolean {
  return (
    today.activeDurationMs === '0' &&
    today.workBlockCount === '0' &&
    today.topApps.length === 0 &&
    today.quality.gapCount === '0'
  );
}

function TodayView({ today }: { today: TodayDto }) {
  return (
    <>
      {!today.quality.isComplete && (
        <div className="notice notice--warn" role="note">
          今日数据不完整：{today.quality.gapCount} 个数据缺口、
          {today.quality.droppedCount} 次丢弃。相关时段未计入。
        </div>
      )}
      <div className="card">
        <div className="metric-grid">
          <Metric label="活跃时长" value={formatDuration(today.activeDurationMs)} />
          <Metric label="工作块" value={today.workBlockCount} />
          <Metric
            label="最长工作块"
            value={formatDuration(today.longestWorkBlockActiveMs)}
          />
          <Metric label="应用切换" value={today.rawAppSwitchCount} />
        </div>
      </div>
      <div className="card">
        <h2 className="card__title">当前 / 最近应用</h2>
        <div className="metric-grid">
          <Metric
            label="当前应用"
            value={today.currentApp?.displayName ?? '—'}
          />
          <Metric label="最近应用" value={today.lastApp?.displayName ?? '—'} />
        </div>
      </div>
      <div className="card">
        <h2 className="card__title">Top 应用</h2>
        {today.topApps.length === 0 ? (
          <div className="text-dim">暂无应用使用记录</div>
        ) : (
          <ul className="list">
            {today.topApps.map((entry) => (
              <li key={entry.app.appId} className="list__row">
                <div className="list__main">
                  <span className="list__title">{entry.app.displayName}</span>
                </div>
                <span className="mono">{formatDuration(entry.activeDurationMs)}</span>
              </li>
            ))}
          </ul>
        )}
      </div>
    </>
  );
}

function Metric({ label, value }: { label: string; value: string }) {
  return (
    <div className="metric">
      <span className="metric__label">{label}</span>
      <span className="metric__value">{value}</span>
    </div>
  );
}
