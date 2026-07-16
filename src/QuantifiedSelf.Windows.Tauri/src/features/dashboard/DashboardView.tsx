import {
  AppWindowIcon,
  ArrowClockwiseIcon,
  ChartBarIcon,
  ClockCountdownIcon,
  InfoIcon,
  WarningCircleIcon,
} from '@phosphor-icons/react';
import type { ActivityOverviewResult } from '../../bridge/contracts';
import {
  formatCount,
  formatDuration,
  formatLastUpdated,
  formatSessionRange,
} from './dashboardFormatting';
import type { DashboardViewState } from './dashboardModel';

interface DashboardViewProps {
  readonly state: DashboardViewState;
  readonly refreshing: boolean;
  readonly onRefresh: () => void;
  readonly locale?: string;
}

export function DashboardView({ state, refreshing, onRefresh, locale }: DashboardViewProps) {
  const updatedAt = state.kind === 'ready' || state.kind === 'empty'
    ? state.updatedAt
    : 0;

  return (
    <div className="page dashboard-page page-enter">
      <header className="page-header dashboard-header">
        <div>
          <p className="eyebrow">今日概览</p>
          <h1 tabIndex={-1}>把时间，看清楚一点</h1>
          <p>数据来自本机隔离的 DEV 通道，只呈现经过 Bridge 筛选的活动摘要。</p>
        </div>
        {(state.kind === 'ready' || state.kind === 'empty') && (
          <div className="dashboard-refresh">
            <span>最后更新 {formatLastUpdated(updatedAt, locale)}</span>
            <button
              className="button button--secondary"
              type="button"
              disabled={refreshing}
              onClick={onRefresh}
            >
              <ArrowClockwiseIcon className={refreshing ? 'spin' : undefined} size={17} aria-hidden="true" />
              {refreshing ? '正在刷新' : '刷新'}
            </button>
          </div>
        )}
      </header>

      {state.kind === 'loading' && <DashboardLoading />}
      {state.kind === 'empty' && <DashboardEmpty onRefresh={onRefresh} refreshing={refreshing} />}
      {state.kind === 'error' && (
        <DashboardError message={state.message} onRetry={onRefresh} refreshing={refreshing} />
      )}
      {state.kind === 'ready' && (
        <DashboardReady overview={state.overview} locale={locale} />
      )}

      <p className="sr-only" role="status" aria-live="polite" aria-atomic="true">
        {refreshing
          ? '正在刷新今日概览。'
          : updatedAt > 0
            ? `今日概览已更新，最后更新时间 ${formatLastUpdated(updatedAt, locale)}。`
            : ''}
      </p>
    </div>
  );
}

function DashboardLoading() {
  return (
    <section className="dashboard-state" role="status" aria-live="polite" aria-busy="true">
      <ClockCountdownIcon className="spin" size={28} aria-hidden="true" />
      <div>
        <h2>正在读取今日活动</h2>
        <p>正在通过本地 Bridge 汇总摘要、常用应用和最近会话。</p>
      </div>
      <div className="dashboard-skeleton" aria-hidden="true">
        <span /><span /><span /><span />
      </div>
    </section>
  );
}

function DashboardEmpty({ onRefresh, refreshing }: { readonly onRefresh: () => void; readonly refreshing: boolean }) {
  return (
    <section className="dashboard-state dashboard-state--centered" aria-labelledby="dashboard-empty-title">
      <InfoIcon size={30} aria-hidden="true" />
      <div>
        <h2 id="dashboard-empty-title">今天还没有活动记录</h2>
        <p>启动 Agent 并使用一段时间后，这里会显示今日摘要、Top Apps 和最近会话。</p>
      </div>
      <button className="button button--secondary" type="button" onClick={onRefresh} disabled={refreshing}>
        <ArrowClockwiseIcon size={17} aria-hidden="true" />
        再检查一次
      </button>
    </section>
  );
}

function DashboardError({
  message,
  onRetry,
  refreshing,
}: {
  readonly message: string;
  readonly onRetry: () => void;
  readonly refreshing: boolean;
}) {
  return (
    <section className="dashboard-state dashboard-state--error" role="alert" aria-labelledby="dashboard-error-title">
      <WarningCircleIcon size={30} aria-hidden="true" />
      <div>
        <h2 id="dashboard-error-title">暂时无法读取今日概览</h2>
        <p>{message}</p>
        <p className="dashboard-state__note">Agent 会保持原状态运行；重试只会重新读取活动摘要。</p>
      </div>
      <button className="button button--secondary" type="button" onClick={onRetry} disabled={refreshing}>
        <ArrowClockwiseIcon className={refreshing ? 'spin' : undefined} size={17} aria-hidden="true" />
        {refreshing ? '正在重试' : '重试'}
      </button>
    </section>
  );
}

function DashboardReady({ overview, locale }: { readonly overview: ActivityOverviewResult; readonly locale?: string }) {
  const { summary, topApps, recentSessions } = overview;

  return (
    <div className="dashboard-content">
      <section className="dashboard-metrics" aria-label="今日核心摘要">
        <article className="metric-card metric-card--primary">
          <ClockCountdownIcon size={22} aria-hidden="true" />
          <div>
            <h2>今日有效使用时长</h2>
            <strong>{formatDuration(summary.activeDurationSeconds, locale)}</strong>
            <p>由 Application 层按活动状态汇总</p>
          </div>
        </article>
        <article className="metric-card">
          <ChartBarIcon size={22} aria-hidden="true" />
          <div className="metric-card__body">
            <h2>今日采样 / 会话摘要</h2>
            <dl className="summary-list">
              <div><dt>采样覆盖</dt><dd>{formatDuration(summary.totalDurationSeconds, locale)}</dd></div>
              <div><dt>活动会话</dt><dd>{formatCount(summary.sessionCount, locale)} 个</dd></div>
              <div><dt>空闲时间</dt><dd>{formatDuration(summary.idleDurationSeconds, locale)}</dd></div>
              <div><dt>未分类</dt><dd>{formatDuration(summary.unknownDurationSeconds, locale)}</dd></div>
            </dl>
          </div>
        </article>
      </section>

      <div className="dashboard-modules">
        <section className="dashboard-module" aria-labelledby="top-apps-title">
          <div className="dashboard-module__heading">
            <div className="dashboard-module__icon"><AppWindowIcon size={20} aria-hidden="true" /></div>
            <div><h2 id="top-apps-title">Top Apps</h2><p>按现有应用层排序呈现</p></div>
          </div>
          {topApps.length > 0 ? (
            <ol className="activity-list" aria-label="今日使用最多的应用">
              {topApps.map((app, index) => (
                <li key={`${app.displayName}-${String(index)}`}>
                  <span className="activity-list__rank" aria-hidden="true">{index + 1}</span>
                  <div className="activity-list__main" aria-hidden="true">
                    <strong>{app.displayName}</strong>
                    <span>{formatCount(app.sessionCount, locale)} 个会话</span>
                  </div>
                  <div className="activity-list__value" aria-hidden="true">
                    <strong>{formatDuration(app.activeDurationSeconds, locale)}</strong>
                    <span>有效使用</span>
                  </div>
                  <span className="sr-only">
                    第 {formatCount(index + 1, locale)} 名，{app.displayName}，有效使用
                    {formatDuration(app.activeDurationSeconds, locale)}，
                    {formatCount(app.sessionCount, locale)} 个会话。
                  </span>
                </li>
              ))}
            </ol>
          ) : <ModuleEmpty text="今天还没有可展示的应用汇总。" />}
        </section>

        <section className="dashboard-module" aria-labelledby="recent-sessions-title">
          <div className="dashboard-module__heading">
            <div className="dashboard-module__icon"><ClockCountdownIcon size={20} aria-hidden="true" /></div>
            <div><h2 id="recent-sessions-title">最近活动会话</h2><p>只显示安全的应用名和时间摘要</p></div>
          </div>
          {recentSessions.length > 0 ? (
            <ul className="activity-list activity-list--sessions" aria-label="最近活动会话">
              {recentSessions.map((session, index) => (
                <li key={`${session.displayName}-${session.startedAtUtc}-${String(index)}`}>
                  <div className="activity-list__main">
                    <strong>{session.displayName}</strong>
                    <span>{formatSessionRange(session.startedAtUtc, session.endedAtUtc, locale)}</span>
                  </div>
                  <div className="activity-list__value">
                    <strong>{formatDuration(session.activeDurationSeconds, locale)}</strong>
                    <span>有效使用</span>
                  </div>
                </li>
              ))}
            </ul>
          ) : <ModuleEmpty text="今天还没有最近会话。" />}
        </section>
      </div>
    </div>
  );
}

function ModuleEmpty({ text }: { readonly text: string }) {
  return <p className="module-empty">{text}</p>;
}
