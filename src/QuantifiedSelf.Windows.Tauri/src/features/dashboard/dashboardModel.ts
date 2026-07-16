import type { ActivityOverviewResult } from '../../bridge/contracts';

export const overviewVisibleRefreshInterval = 15_000;
export const overviewHiddenRefreshInterval = 60_000;

export type DashboardViewState =
  | { readonly kind: 'loading' }
  | { readonly kind: 'empty'; readonly updatedAt: number }
  | { readonly kind: 'ready'; readonly overview: ActivityOverviewResult; readonly updatedAt: number }
  | { readonly kind: 'error'; readonly message: string };

export function getOverviewRefreshInterval(visible: boolean) {
  return visible ? overviewVisibleRefreshInterval : overviewHiddenRefreshInterval;
}

export function isOverviewEmpty(overview: ActivityOverviewResult) {
  const { summary } = overview;
  return summary.totalDurationSeconds <= 0
    && summary.activeDurationSeconds <= 0
    && summary.idleDurationSeconds <= 0
    && summary.unknownDurationSeconds <= 0
    && summary.sessionCount <= 0
    && overview.topApps.length === 0
    && overview.recentSessions.length === 0;
}
