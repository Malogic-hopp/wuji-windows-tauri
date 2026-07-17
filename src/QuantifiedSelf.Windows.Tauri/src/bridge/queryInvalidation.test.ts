import { QueryClient } from '@tanstack/react-query';
import { describe, expect, it } from 'vitest';
import {
  activityOverviewQueryKey,
  refreshQueriesAfterBridgeReady,
  refreshQueriesAfterSettingsSaved,
  settingsQueryKey,
} from './queryInvalidation';

describe('refreshQueriesAfterBridgeReady', () => {
  it('Bridge ready 后自动使 Dashboard 与 Settings 缓存失效', async () => {
    const queryClient = new QueryClient();
    queryClient.setQueryData(activityOverviewQueryKey, { stale: 'old bridge generation' });
    queryClient.setQueryData(settingsQueryKey, { stale: 'old bridge generation' });

    await refreshQueriesAfterBridgeReady(queryClient);

    expect(queryClient.getQueryState(activityOverviewQueryKey)?.isInvalidated).toBe(true);
    expect(queryClient.getQueryState(settingsQueryKey)?.isInvalidated).toBe(true);
  });
});

describe('refreshQueriesAfterSettingsSaved', () => {
  it('保存设置后失效 Settings、Agent 状态与 Dashboard 查询', async () => {
    const queryClient = new QueryClient();
    queryClient.setQueryData(settingsQueryKey, { stale: 'settings' });
    queryClient.setQueryData(['agent', 'status'], { stale: 'agent' });
    queryClient.setQueryData(activityOverviewQueryKey, { stale: 'dashboard' });

    await refreshQueriesAfterSettingsSaved(queryClient);

    expect(queryClient.getQueryState(settingsQueryKey)?.isInvalidated).toBe(true);
    expect(queryClient.getQueryState(['agent', 'status'])?.isInvalidated).toBe(true);
    expect(queryClient.getQueryState(activityOverviewQueryKey)?.isInvalidated).toBe(true);
  });
});
