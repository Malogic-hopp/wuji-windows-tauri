import { QueryClient } from '@tanstack/react-query';
import { describe, expect, it } from 'vitest';
import {
  activityOverviewQueryKey,
  refreshQueriesAfterBridgeReady,
} from './queryInvalidation';

describe('refreshQueriesAfterBridgeReady', () => {
  it('Bridge ready 后自动使 Dashboard Overview 缓存失效', async () => {
    const queryClient = new QueryClient();
    queryClient.setQueryData(activityOverviewQueryKey, { stale: 'old bridge generation' });

    await refreshQueriesAfterBridgeReady(queryClient);

    expect(queryClient.getQueryState(activityOverviewQueryKey)?.isInvalidated).toBe(true);
  });
});
