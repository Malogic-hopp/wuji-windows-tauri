import type { QueryClient } from '@tanstack/react-query';

export const initializeQueryKey = ['app', 'initialize'] as const;
export const agentStatusQueryKey = ['agent', 'status'] as const;
export const activityOverviewQueryKey = ['activity', 'overview'] as const;
export const settingsQueryKey = ['settings', 'current'] as const;

export async function refreshQueriesAfterBridgeReady(queryClient: QueryClient) {
  await Promise.all([
    queryClient.resetQueries({ queryKey: initializeQueryKey }),
    queryClient.resetQueries({ queryKey: agentStatusQueryKey }),
    queryClient.invalidateQueries({ queryKey: activityOverviewQueryKey }),
    queryClient.invalidateQueries({ queryKey: settingsQueryKey }),
  ]);
}

export async function refreshQueriesAfterSettingsSaved(queryClient: QueryClient) {
  await Promise.all([
    queryClient.invalidateQueries({ queryKey: settingsQueryKey }),
    queryClient.invalidateQueries({ queryKey: agentStatusQueryKey }),
    queryClient.invalidateQueries({ queryKey: activityOverviewQueryKey }),
  ]);
}
